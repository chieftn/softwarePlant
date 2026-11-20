using Factory.Contracts;

namespace Factory.Workbench;

public sealed record MissionRetryRequest(string Id, string InstallationId, string ReleaseId, string MissionId, string MissionDigest);
public sealed record MissionRetryStatus(string RequestId, string InstallationId, string ReleaseId, string State, string Detail);
public sealed record MissionAttempt(string InstanceId);

public static class MissionRetry
{
    public static string RequestPath(string root) => Path.Combine(LocalState.RuntimeDirectory(root), "mission-retry-request.json");
    public static string StatusPath(string root) => Path.Combine(LocalState.RuntimeDirectory(root), "mission-retry-status.json");

    public static DevelopmentMissionState Prepare(DevelopmentMissionState state, DevelopmentMission expected)
    {
        state.Mission.Validate();
        if (state.Mission.Id != expected.Id || state.Mission.Digest != expected.Digest ||
            state.State != "needsAttention" || state.CandidateCommit is not null || state.PullRequest is not null ||
            state.PublicationPending || state.Alignment is not null || state.Verification.Length != 0 ||
            state.Iterations < 0 || state.ActiveMilliseconds < 0)
            throw new InvalidDataException("Retry requires the same frozen failed mission with no recorded candidate or publication. Reconcile effects instead.");
        var workload = expected.Workload.Validate();
        var policy = expected.Workload.Configuration.ProductionLine!.Definition.Policy;
        if (state.Iterations >= Math.Min(workload.Workflow.MaximumIterations, policy.MaximumIterations) ||
            state.ActiveMilliseconds >= (long)Math.Min(workload.Workflow.TimeoutMinutes, policy.TimeoutMinutes) * 60 * 1000 ||
            expected.ApprovalDeadline <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("The frozen mission budget or deadline is exhausted. Retry does not reset its limits.");
        return state with { State = "claimed", Reason = "Explicit operator retry; prior attempt retained and original budget preserved." };
    }

    public static string AttemptDirectory(string run)
    {
        var path = Path.Combine(run, "attempt.json");
        if (!File.Exists(path))
            return run;
        var attempt = LocalState.Read<MissionAttempt>(path);
        if (!Guid.TryParseExact(attempt.InstanceId, "D", out _))
            throw new InvalidDataException("The retained attempt directory must identify an owned launch.");
        return Path.Combine(run, "attempts", attempt.InstanceId);
    }

    public static string RetainedAttemptDirectory(string run, DevelopmentMissionState state)
    {
        var directory = AttemptDirectory(run);
        if (directory == run || Directory.Exists(directory))
            return directory;
        var instance = Path.GetFileName(directory);
        var beforePath = Path.Combine(run, $"mission-state-before-{instance}.json");
        var telemetryPath = Path.Combine(run, "mission-telemetry.jsonl");
        var attempts = Path.Combine(run, "attempts");
        MissionTelemetry.RequireRegularPath(beforePath);
        MissionTelemetry.RequireRegularPath(telemetryPath);
        MissionTelemetry.RequireRegularPath(Path.Combine(attempts, "validation"));
        var before = LocalState.Read<DevelopmentMissionState>(beforePath, 512 * 1024);
        _ = Prepare(before, state.Mission);
        using var file = new FileStream(telemetryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MissionTelemetry.MaximumBytes)
            throw new InvalidDataException("Interrupted retry telemetry exceeds its bounded evidence.");
        using var reader = new StreamReader(file);
        var entries = MissionTelemetry.Parse(reader.ReadToEnd(), state.Mission.Id, state.Mission.Digest)
            .Where(entry => entry.Source == "worker" && entry.InstanceId == instance).ToArray();
        if (before.Iterations != state.Iterations || before.ActiveMilliseconds != state.ActiveMilliseconds ||
            !entries.Any(entry => entry.Stage == "worker" && entry.Outcome == "started") ||
            !entries.Any(entry => entry.Stage == "retry-integrity" && entry.Outcome == "completed") ||
            entries.Any(entry => entry.Stage is not ("worker" or "retry-integrity")) ||
            Directory.Exists(attempts) && Directory.EnumerateFileSystemEntries(attempts).Any() ||
            !Directory.Exists(Path.Combine(run, "repository")))
            throw new InvalidDataException("The missing retry checkout has ambiguous execution evidence. Reconcile; do not replay.");
        return run;
    }
}
