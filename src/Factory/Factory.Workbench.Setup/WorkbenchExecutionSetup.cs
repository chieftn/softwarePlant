using Factory.Contracts;

namespace Factory.Workbench.Setup;

public sealed class WorkbenchExecutionSetup(string root, IWorkbenchService service)
{
    public async Task StartAsync(PlantHttp plant, WorkbenchBinding binding, WorkbenchLineConfiguration approved,
        CancellationToken cancellationToken)
    {
        using var maintenance = LocalState.Lock(root, "maintenance.lock");
        BindingStore.RequireEnrollmentAllowed(root);
        var installation = LocalState.ReadInstallation(root);
        if (installation.State != "installed" || binding.InstallationId != installation.InstallationId)
            throw new InvalidDataException("Recover the enrolled installation before starting a line.");
        var current = await plant.ReadAsync<WorkbenchLineConfiguration>(
            $"v1/enrollments/{binding.EnrollmentId}/configuration", cancellationToken);
        if (current.EnrollmentId != binding.EnrollmentId || current.WorkbenchId != binding.WorkbenchId ||
            current.PlantId != approved.PlantId || !WorkloadRequest.From(approved).Matches(current))
            throw new InvalidDataException("The assignment changed. Review the current line before granting local permission.");
        var line = current.ProductionLine!;
        if (line.Definition.Execution is not { Enabled: true } ||
            !line.Definition.Policy.AllowShellExecution || !line.Definition.Policy.AllowPublication)
            throw new InvalidDataException("Configure and enable execution in the assigned production line first.");
        line.Definition.Validate();
        var workload = await plant.SendAsync<ResolvedRepositoryWorkload>(HttpMethod.Post,
            $"v1/enrollments/{binding.EnrollmentId}/workload", WorkloadRequest.From(current), null, cancellationToken);
        var definition = workload.Validate();
        if (workload.Configuration.EnrollmentId != binding.EnrollmentId ||
            workload.Configuration.WorkbenchId != binding.WorkbenchId ||
            workload.Configuration.PlantId != current.PlantId ||
            !WorkloadRequest.From(current).Matches(workload.Configuration))
            throw new InvalidDataException("The resolved workload does not match the approved line.");
        if (definition.Verification.Commands.Length == 0 || definition.Workflow.CompletionCriterion != "pull_request_merged")
            throw new InvalidDataException("The repository must configure real project verification and pull_request_merged completion before starting.");
        var configuration = ConfigurationStore.Read(root);
        var prepared = File.Exists(WslWorkerEnvironment.StatusPath(root))
            ? LocalState.Read<WorkerSetupStatus>(WslWorkerEnvironment.StatusPath(root)) : null;
        if (configuration.Development?.WorkerExecutable != WslWorkerEnvironment.WorkerExecutable(installation.ActiveRelease) ||
            configuration.Distribution != WslWorkerEnvironment.DistributionName(installation.InstallationId) ||
            configuration.LinuxUser != WslWorkerEnvironment.LinuxUser ||
            configuration.LinuxWorkspaceRoot != WslWorkerEnvironment.Workspace ||
            prepared is not { State: "ready" } || prepared.InstallationId != installation.InstallationId ||
            prepared.ReleaseId != installation.ActiveRelease)
            throw new InvalidDataException("Prepare the SDK worker for the installed runtime before starting the line.");
        if (File.Exists(Path.Combine(LocalState.RuntimeDirectory(root), "active-development-worker.json")))
            throw new InvalidOperationException("An owned mission is still active or unresolved. Stop and reconcile it before starting a line.");
        await service.StopAsync(installation, cancellationToken);
        using (LocalState.LockRuntime(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(LocalState.RuntimeDirectory(root), "active-development-worker.json")))
                throw new IOException("Worker shutdown is unresolved. Preserve its mission journal before starting a line.");
            configuration = ConfigurationStore.Read(root);
            ConfigurationStore.Save(root, configuration with
            {
                AllowedRepositories = new[] { line.Definition.Source.Repository, line.Definition.Tasks.Repository }
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                AllowShellExecution = true,
                AllowPublication = true,
                ExecutionConsent = WorkbenchExecutionConsent.From(current),
                Development = configuration.Development! with { Enabled = false }
            });
            installation = installation with { RunRequested = true };
            LocalState.SaveInstallation(root, installation);
        }
        await service.StartAsync(installation, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        using var maintenance = LocalState.Lock(root, "maintenance.lock");
        var installation = LocalState.ReadInstallation(root);
        var configuration = ConfigurationStore.Read(root);
        ConfigurationStore.Save(root, configuration with
        {
            ExecutionConsent = null,
            Development = configuration.Development is { } development ? development with { Enabled = false } : null
        });
        await service.StopAsync(installation, cancellationToken);
        using (LocalState.LockRuntime(root))
        {
            if (File.Exists(Path.Combine(LocalState.RuntimeDirectory(root), "active-development-worker.json")))
                throw new IOException("Worker termination is unresolved. Consent is revoked; preserve the mission journal for reconciliation.");
        }
        if (installation.RunRequested)
            await service.StartAsync(installation, cancellationToken);
    }
}
