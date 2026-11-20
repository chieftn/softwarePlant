using System.Text;
using System.Text.Json;
using Factory.Contracts;

namespace Factory.Workbench;

public sealed record MissionDiagnosticRequest(string Id, string InstallationId, string ReleaseId,
    string MissionId, string MissionDigest, bool DiagnosticOnly);
public sealed record MissionVerificationDiagnostic(string Executable, int ExitCode, long DurationMilliseconds);
public sealed record RetainedMissionDiagnostic(string MissionId, string MissionDigest, string Repository, long IssueNumber, string State,
    string Reason, int Iterations, long ActiveMilliseconds, string? CandidateCommit,
    MissionVerificationDiagnostic[] Verification, string? AlignmentOutcome, bool PublicationPending);
public sealed record MissionDiagnosticStatus(string RequestId, string InstallationId, string ReleaseId,
    int ProcessId, DateTimeOffset ObservedAt, string State, string Detail, RetainedMissionDiagnostic? Mission);

public sealed class WorkbenchMissionDiagnostics(string root, WorkbenchInstallation installation, IWslCommands commands)
{
    public const int MaximumStateBytes = 512 * 1024;
    public static string RequestPath(string root) => Path.Combine(root, "mission-diagnostic-request.json");
    public static string StatusPath(string root) => Path.Combine(LocalState.RuntimeDirectory(root), "mission-diagnostics.json");
    public static string SnapshotPath(string root) => Path.Combine(LocalState.RuntimeDirectory(root), "development-mission.json");

    public static DevelopmentMissionDelivery ReadSnapshot(string root)
    {
        var path = SnapshotPath(root);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Retained mission snapshots must not be filesystem links.");
        var snapshot = LocalState.Read<DevelopmentMissionDelivery>(path);
        snapshot.Mission.Validate();
        return snapshot;
    }

    public async Task ProcessAsync(MissionDiagnosticRequest request, CancellationToken cancellationToken)
    {
        var stage = "validate-diagnostic-request";
        try
        {
            if (!Guid.TryParseExact(request.Id, "D", out var id) || id == Guid.Empty ||
                installation.State != "installed" || request.InstallationId != installation.InstallationId ||
                request.ReleaseId != installation.ActiveRelease)
                throw new InvalidDataException("Mission diagnostics must match the selected installed runtime.");
            var snapshot = ReadSnapshot(root);
            if (request.MissionId != snapshot.Mission.Id || request.MissionDigest != snapshot.Mission.Digest)
                throw new InvalidDataException("Diagnostic request does not match the retained frozen mission.");
            if (File.Exists(Path.Combine(LocalState.RuntimeDirectory(root), "active-development-worker.json")))
                throw new IOException("An owned worker is active or unresolved. Retained-mission diagnostics will not stop it.");
            var configuration = ConfigurationStore.Read(root);
            if (configuration.InstallationId != installation.InstallationId ||
                configuration.Distribution != WslWorkerEnvironment.DistributionName(installation.InstallationId) ||
                configuration.LinuxUser != WslWorkerEnvironment.LinuxUser ||
                configuration.LinuxWorkspaceRoot != WslWorkerEnvironment.Workspace)
                throw new InvalidDataException("Diagnostic access must use this installation's owned Linux environment.");
            stage = "read-retained-mission-state";
            Report("collecting", "Reading retained mission state as the service identity.", null);
            new WslWorkerEnvironment(root, installation, commands).RequireOwnedRegistration();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await commands.RunAsync(
                ["--distribution", configuration.Distribution, "--user", WslWorkerEnvironment.LinuxUser,
                    "--exec", "/bin/sh", "-eu", "-c",
                    ReadStateScript.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'), "mission-diagnostics",
                    $"{WslWorkerEnvironment.Workspace}/{request.MissionId}/mission-state.json"],
                null, deadline.Token);
            if (result.ExitCode != 0)
                throw new IOException($"Retained-state read failed (exit {result.ExitCode}): {SetupDiagnostics.Redact(result.Error)}");
            if (Encoding.UTF8.GetByteCount(result.Output) > MaximumStateBytes)
                throw new InvalidDataException("Retained mission state exceeds its diagnostic byte bound.");
            stage = "validate-retained-mission-state";
            var state = JsonSerializer.Deserialize<DevelopmentMissionState>(result.Output, ConfigurationStore.Json)
                ?? throw new InvalidDataException("Retained mission state is empty.");
            var report = Project(state, snapshot.Mission);
            stage = "read-retained-mission-telemetry";
            var telemetry = await commands.RunAsync(
                ["--distribution", configuration.Distribution, "--user", WslWorkerEnvironment.LinuxUser,
                    "--exec", "/bin/sh", "-eu", "-c",
                    ReadTelemetryScript.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'), "mission-telemetry",
                    $"{WslWorkerEnvironment.Workspace}/{request.MissionId}/mission-telemetry.jsonl"],
                null, deadline.Token);
            if (telemetry.ExitCode != 0)
                throw new IOException($"Retained telemetry read failed (exit {telemetry.ExitCode}): {SetupDiagnostics.Redact(telemetry.Error)}");
            MissionTelemetry.SaveCollected(root, request.MissionId, request.MissionDigest, telemetry.Output);
            Report("collected", "Retained state collected. No mission was resumed and no credentials or raw logs were exported.", report);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
            JsonException or ContractValidationException or ArgumentException or OperationCanceledException)
        {
            var detail = error is JsonException ? "Retained mission JSON could not be validated."
                : SetupDiagnostics.Redact(error.Message);
            Report("failed", $"{stage}: {error.GetType().Name}: {detail}", null);
            throw;
        }

        void Report(string state, string detail, RetainedMissionDiagnostic? mission) =>
            LocalState.Write(StatusPath(root), new MissionDiagnosticStatus(request.Id, installation.InstallationId,
                installation.ActiveRelease, Environment.ProcessId, DateTimeOffset.UtcNow, state,
                detail.Length <= 4096 ? detail : detail[..4096], mission));
    }

    public static RetainedMissionDiagnostic Project(DevelopmentMissionState state, DevelopmentMission expected)
    {
        state.Mission.Validate();
        if (state.Mission.Id != expected.Id || state.Mission.Digest != expected.Digest ||
            state.Iterations < 0 || state.ActiveMilliseconds < 0 || state.Reason.Length > 4096 ||
            state.State is not ("claimed" or "executing" or "revising" or "publishing" or "awaitingApproval" or
                "awaitingMerge" or "approved" or "completed" or "blocked" or "cancelled" or "needsAttention") ||
            state.Verification.Length > 32 ||
            state.Alignment is not null && state.Alignment.Outcome is not ("pass" or "revise" or "blocked"))
            throw new InvalidDataException("Retained state does not match a valid frozen mission.");
        if (state.CandidateCommit is not null)
            RepositoryWorkload.Commit(state.CandidateCommit);
        var commands = expected.Workload.Validate().Verification.Commands;
        var checks = state.Verification.Select(check =>
        {
            if (check is null || check.Command is null || check.Command.Arguments is null ||
                check.DurationMilliseconds < 0 || !commands.Any(command =>
                    command.Executable == check.Command.Executable &&
                    command.WorkingDirectory == check.Command.WorkingDirectory &&
                    command.TimeoutSeconds == check.Command.TimeoutSeconds &&
                    command.Arguments.SequenceEqual(check.Command.Arguments, StringComparer.Ordinal)))
                throw new InvalidDataException("Retained verification does not match the frozen workload.");
            return new MissionVerificationDiagnostic(check.Command.Executable, check.ExitCode, check.DurationMilliseconds);
        }).ToArray();
        return new(expected.Id, expected.Digest, expected.Issue.Repository, expected.Issue.Number, state.State,
            SetupDiagnostics.Redact(state.Reason), state.Iterations, state.ActiveMilliseconds, state.CandidateCommit,
            checks, state.Alignment?.Outcome, state.PublicationPending);
    }

    private const string ReadStateScript = """
        if [ ! -f "$1" ] || [ -L "$1" ]; then
          echo 'Retained mission state is missing or linked.' >&2
          exit 66
        fi
        if [ "$(readlink -f -- "$1")" != "$1" ]; then
          echo 'Retained mission path traverses a filesystem link.' >&2
          exit 73
        fi
        if [ "$(wc -c < "$1")" -gt 524288 ]; then
          echo 'Retained mission state exceeds its byte bound.' >&2
          exit 65
        fi
        head -c 524288 -- "$1"
        """;

    private const string ReadTelemetryScript = """
        if [ ! -e "$1" ] && [ ! -L "$1" ]; then
          exit 0
        fi
        if [ ! -f "$1" ] || [ -L "$1" ] || [ "$(readlink -f -- "$1")" != "$1" ]; then
          echo 'Retained telemetry is not a regular owned file.' >&2
          exit 73
        fi
        if [ "$(wc -c < "$1")" -gt 2097152 ]; then
          echo 'Retained telemetry exceeds its byte bound.' >&2
          exit 65
        fi
        head -c 2097152 -- "$1"
        """;
}
