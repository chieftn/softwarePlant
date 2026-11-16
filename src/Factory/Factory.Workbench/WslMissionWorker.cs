using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Factory.Contracts;

namespace Factory.Workbench;

public sealed record WslWorkerJournal(string MissionId, string MissionDigest, string InstanceId, string Nonce,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] OwnedLinuxWorker? Process,
    string Distribution, string LinuxUser, string WorkspaceRoot, string Executable);

public sealed class WslMissionWorker : IDisposable
{
    private readonly PlantHttp plant;
    private readonly WorkbenchBinding binding;
    private readonly WorkbenchConfiguration configuration;
    private readonly string root;
    private readonly Process process;
    private readonly CancellationTokenSource requests = new();
    private readonly SemaphoreSlim termination = new(1, 1);
    private readonly TaskCompletionSource<OwnedLinuxWorker> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WslWorkerJournal journal;
    public DevelopmentMission Mission { get; }
    public Task Completion { get; private set; } = Task.CompletedTask;
    public bool TerminationConfirmed { get; private set; }

    private WslMissionWorker(PlantHttp plant, WorkbenchBinding binding, WorkbenchConfiguration configuration,
        string root, DevelopmentMission mission, WslWorkerJournal journal, Process process)
    {
        this.plant = plant;
        this.binding = binding;
        this.configuration = configuration;
        this.root = root;
        Mission = mission;
        this.journal = journal;
        this.process = process;
    }

    public static ProcessStartInfo Invocation(WorkbenchConfiguration configuration, string instance, string nonce, bool resume)
    {
        var start = new ProcessStartInfo("wsl.exe")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "--distribution", configuration.Distribution, "--user", configuration.LinuxUser,
            "--exec", "/usr/bin/setsid", "--wait", "--", configuration.Development!.WorkerExecutable,
            "run", "--workspace-root", configuration.LinuxWorkspaceRoot, "--instance", instance, "--nonce", nonce })
            start.ArgumentList.Add(argument);
        if (resume)
            start.ArgumentList.Add("--resume");
        start.Environment.Remove("FACTORY_WORKBENCH_COPILOT_TOKEN");
        return start;
    }

    public static async Task<WslMissionWorker> StartAsync(PlantHttp plant, WorkbenchBinding binding,
        WorkbenchConfiguration configuration, string root, DevelopmentMissionDelivery delivery,
        bool resume, CancellationToken cancellationToken)
    {
        configuration.Validate();
        if (configuration.Development?.Enabled != true)
            throw new InvalidDataException("Local development execution is disabled.");
        var request = new WorkerMissionRequest(delivery.Mission, true, delivery.Mission.Model, "github-copilot",
            configuration.AllowShellExecution, configuration.AllowPublication, configuration.AllowedRepositories,
            configuration.Development.GitAuthorName, configuration.Development.GitAuthorEmail, delivery.Reviewers);
        request.Validate();
        var journal = new WslWorkerJournal(delivery.Mission.Id, delivery.Mission.Digest, Guid.NewGuid().ToString("D"),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), null, configuration.Distribution,
            configuration.LinuxUser, configuration.LinuxWorkspaceRoot, configuration.Development.WorkerExecutable);
        var process = new Process { StartInfo = Invocation(configuration, journal.InstanceId, journal.Nonce, resume) };
        var worker = new WslMissionWorker(plant, binding, configuration, root, delivery.Mission, journal, process);
        LocalState.Write(worker.JournalPath, journal);
        if (!process.Start())
            throw new IOException("Service-owned WSL worker did not start.");
        worker.Completion = worker.ReadAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, RepositoryWorkload.Json).AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            await worker.started.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return worker;
        }
        catch
        {
            try { await worker.StopAsync(CancellationToken.None); }
            finally { worker.Dispose(); }
            throw;
        }
    }

    public bool Matches(WorkbenchLineConfiguration current)
    {
        var original = Mission.Workload.Configuration;
        var definition = current.ProductionLine?.Definition;
        return current.PlantId == original.PlantId && current.AssignmentVersion == original.AssignmentVersion &&
            current.BindingVersion == original.BindingVersion && current.ProductionLine?.Id == original.ProductionLine!.Id &&
            definition is not null && definition.Policy.AllowShellExecution && definition.Policy.AllowPublication &&
            definition.Source.Repository == original.ProductionLine.Definition.Source.Repository &&
            definition.Source.ConnectionReference == original.ProductionLine.Definition.Source.ConnectionReference &&
            definition.Tasks.Repository == original.ProductionLine.Definition.Tasks.Repository &&
            definition.Tasks.ConnectionReference == original.ProductionLine.Definition.Tasks.ConnectionReference;
    }

    private string JournalPath => Path.Combine(LocalState.RuntimeDirectory(root), "active-development-worker.json");
    private string Endpoint => $"v1/enrollments/{binding.EnrollmentId}/mission";

    private async Task ReadAsync()
    {
        var errors = DrainErrorsAsync();
        try
        {
            while (await process.StandardOutput.ReadLineAsync(requests.Token) is { } line)
            {
                if (Encoding.UTF8.GetByteCount(line) > 128 * 1024)
                    throw new InvalidDataException("Worker pipe output exceeded its bound.");
                var message = JsonSerializer.Deserialize<WorkerPipeMessage>(line, RepositoryWorkload.Json)
                    ?? throw new InvalidDataException("Worker pipe message was empty.");
                if (message.Type == "started")
                {
                    var owned = message.Payload.Deserialize<OwnedLinuxWorker>(RepositoryWorkload.Json)
                        ?? throw new InvalidDataException("Worker ownership handshake was empty.");
                    if (owned.InstanceId != journal.InstanceId || owned.Nonce != journal.Nonce ||
                        owned.MissionId != Mission.Id || owned.ProcessId <= 1 || owned.ProcessGroupId != owned.ProcessId ||
                        started.Task.IsCompleted)
                        throw new InvalidDataException("Worker ownership handshake did not match this launch.");
                    journal = journal with { Process = owned };
                    LocalState.Write(JournalPath, journal);
                    started.TrySetResult(owned);
                }
                else if (message.Type == "request")
                {
                    if (journal.Process is null || !Guid.TryParseExact(message.Id, "D", out _))
                        throw new InvalidDataException("Unowned worker cannot request plant operations.");
                    await RespondAsync(message);
                }
                else if (message.Type != "result" || message.Id != Mission.Id)
                    throw new InvalidDataException("Unrecognized worker pipe message.");
            }
            await process.WaitForExitAsync(CancellationToken.None);
            if (!started.Task.IsCompleted)
                started.TrySetException(new IOException("Worker exited before proving process ownership."));
            if (process.ExitCode != 0)
                throw new IOException("Owned worker exited without approved completion; reconcile retained state.");
        }
        catch
        {
            try
            {
                await ConfirmTerminationAsync();
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                started.TrySetException(new IOException("Worker protocol failed before a usable ownership handshake."));
            }
            throw;
        }
        finally
        {
            await errors;
        }
    }

    private async Task DrainErrorsAsync()
    {
        while (await process.StandardError.ReadLineAsync() is not null)
            Console.Error.WriteLine("Owned Linux worker reported a diagnostic; inspect redacted mission state.");
    }

    private async Task RespondAsync(WorkerPipeMessage message)
    {
        JsonElement payload;
        try
        {
            switch (message.Operation)
            {
                case "authorize":
                    await plant.WriteAsync(HttpMethod.Post, Endpoint + "/authorize", null, message.Id, requests.Token);
                    payload = JsonSerializer.SerializeToElement(new { authorized = true });
                    break;
                case "credential":
                    payload = await plant.SendAsync<JsonElement>(HttpMethod.Post, Endpoint + "/credential",
                        message.Payload.Deserialize<MissionCredentialRequest>(RepositoryWorkload.Json), null, requests.Token);
                    break;
                case "model-credential":
                    var modelRequest = message.Payload.Deserialize<MissionModelCredentialRequest>(RepositoryWorkload.Json);
                    if (modelRequest?.MissionId != Mission.Id || modelRequest.MissionDigest != Mission.Digest)
                        throw new InvalidDataException("Worker requested model credentials outside its owned mission.");
                    payload = await plant.SendAsync<JsonElement>(HttpMethod.Post, Endpoint + "/model-credential",
                        modelRequest, null, requests.Token);
                    break;
                case "publish":
                case "published":
                    payload = await plant.SendAsync<JsonElement>(HttpMethod.Post, Endpoint + "/" + message.Operation,
                        message.Payload.Deserialize<MissionCandidateRequest>(RepositoryWorkload.Json), null, requests.Token);
                    break;
                case "review":
                    payload = await plant.ReadAsync<JsonElement>(Endpoint + "/review", requests.Token);
                    break;
                case "progress":
                    var progress = message.Payload.Deserialize<DevelopmentMissionProgress>(RepositoryWorkload.Json)
                        ?? throw new InvalidDataException("Worker progress was empty.");
                    if (progress.MissionId != Mission.Id || progress.MissionDigest != Mission.Digest)
                        throw new InvalidDataException("Worker progress changed frozen mission identity.");
                    var pendingPath = Path.Combine(LocalState.RuntimeDirectory(root), "pending-development-progress.json");
                    if (File.Exists(pendingPath))
                    {
                        var previous = LocalState.Read<PendingWorkbenchReport<DevelopmentMissionProgress>>(pendingPath);
                        if (previous.EnrollmentId != binding.EnrollmentId || previous.Body.MissionId != Mission.Id)
                            throw new InvalidDataException("Pending mission report belongs to another ownership scope.");
                        await plant.WriteAsync(HttpMethod.Put, Endpoint + "/progress", previous.Body, previous.OperationId, requests.Token);
                    }
                    LocalState.Write(pendingPath, new PendingWorkbenchReport<DevelopmentMissionProgress>("1", binding.InstallationId,
                        Mission.Workload.Configuration.PlantId, binding.WorkbenchId, message.Id, progress, binding.EnrollmentId,
                        Mission.Workload.Configuration.AssignmentVersion));
                    await plant.WriteAsync(HttpMethod.Put, Endpoint + "/progress", progress, message.Id, requests.Token);
                    File.Delete(pendingPath);
                    payload = JsonSerializer.SerializeToElement(new { recorded = true });
                    break;
                default:
                    throw new InvalidDataException("Unsupported worker operation cannot widen plant authority.");
            }
            await Reply(new("response", message.Id, message.Operation, payload));
        }
        catch (Exception error) when (error is IOException or HttpRequestException or JsonException or ContractValidationException)
        {
            await Reply(new("error", message.Id, message.Operation, JsonSerializer.SerializeToElement(new
            {
                code = error is PlantConnectionException rejection ? rejection.Code ?? "plant_rejected" : "worker_boundary_failed"
            })));
        }
    }

    private async Task Reply(WorkerPipeMessage response)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(response, RepositoryWorkload.Json).AsMemory(), requests.Token);
        await process.StandardInput.FlushAsync(requests.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await requests.CancelAsync();
        process.StandardInput.Close();
        try
        {
            if (journal.Process is not null || journal.Distribution == WslWorkerEnvironment.DistributionName(configuration.InstallationId))
            {
                await ConfirmTerminationAsync();
                if (TerminationConfirmed)
                    await plant.WriteAsync(HttpMethod.Post, Endpoint + "/terminated", new MissionTerminationRequest(Mission.Id, true),
                        Guid.NewGuid().ToString("D"), cancellationToken);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        if (!TerminationConfirmed)
            throw new IOException("Linux owned-group termination is unconfirmed. Keep the mission journal and claim for reconciliation.");
        File.Delete(JournalPath);
    }

    private async Task ConfirmTerminationAsync()
    {
        await termination.WaitAsync();
        try
        {
            if (!TerminationConfirmed)
                TerminationConfirmed = await StopRecordedAsync(configuration, journal, CancellationToken.None, root);
        }
        finally { termination.Release(); }
    }

    public static async Task<bool> StopRecordedAsync(WorkbenchConfiguration configuration, WslWorkerJournal journal,
        CancellationToken cancellationToken, string? root = null)
    {
        if (root is not null && journal.Distribution == WslWorkerEnvironment.DistributionName(configuration.InstallationId))
        {
            var installation = LocalState.ReadInstallation(root);
            if (installation.InstallationId != configuration.InstallationId ||
                journal.LinuxUser != WslWorkerEnvironment.LinuxUser || journal.WorkspaceRoot != WslWorkerEnvironment.Workspace ||
                !journal.Executable.StartsWith("/opt/factory/releases/", StringComparison.Ordinal))
                throw new InvalidDataException("Recorded worker environment does not match the owned installation.");
            await new WslWorkerEnvironment(root, installation, new WslCommands()).TerminateAsync(cancellationToken);
            return true;
        }
        if (journal.Process is null)
            return false;
        var start = new ProcessStartInfo("wsl.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "--distribution", journal.Distribution, "--user", journal.LinuxUser,
            "--exec", journal.Executable, "stop", "--workspace-root", journal.WorkspaceRoot,
            "--mission", journal.MissionId, "--instance", journal.InstanceId, "--nonce", journal.Nonce })
            start.ArgumentList.Add(argument);
        using var stopping = Process.Start(start) ?? throw new IOException("Owned Linux stop helper did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var output = stopping.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = stopping.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await stopping.WaitForExitAsync(timeout.Token);
            await error;
            var text = await output;
            if (text.Length > 4096 || stopping.ExitCode != 0)
                return false;
            using var result = JsonDocument.Parse(text);
            return result.RootElement.GetProperty("confirmed").GetBoolean();
        }
        finally
        {
            if (!stopping.HasExited)
            {
                stopping.Kill(entireProcessTree: true);
                await stopping.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    public void Dispose()
    {
        requests.Dispose();
        termination.Dispose();
        process.Dispose();
    }
}
