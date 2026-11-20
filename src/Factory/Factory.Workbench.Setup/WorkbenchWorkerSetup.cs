using Factory.Contracts;

namespace Factory.Workbench.Setup;

public sealed class WorkbenchWorkerSetup(string root, IWorkbenchService service)
{
    public async Task PrepareAsync(Action<WorkerSetupStatus> progress, CancellationToken cancellationToken) =>
        await ExecuteAsync("prepare", progress, cancellationToken);

    public async Task RemoveAsync(CancellationToken cancellationToken) =>
        await ExecuteAsync("remove", _ => { }, cancellationToken);

    private async Task ExecuteAsync(string operation, Action<WorkerSetupStatus> progress, CancellationToken cancellationToken)
    {
        WorkbenchInstallation installation;
        WorkerSetupRequest request;
        using (LocalState.Lock(root, "maintenance.lock"))
        {
            installation = LocalState.ReadInstallation(root);
            if (installation.State != "installed")
                throw new InvalidDataException("Recover the installed runtime before preparing its Linux environment.");
            var configuration = ConfigurationStore.Read(root);
            if (operation != "remove" && (configuration.Development?.Enabled == true || configuration.ExecutionConsent is not null ||
                File.Exists(Path.Combine(LocalState.RuntimeDirectory(root), "active-development-worker.json"))))
                throw new InvalidOperationException("Stop and reconcile mission execution before changing the Linux environment.");
            await service.StopAsync(installation, cancellationToken);
            if (File.Exists(Path.Combine(LocalState.RuntimeDirectory(root), "active-development-worker.json")))
                throw new IOException("Prior mission shutdown is unresolved. Preserve its journal before changing the Linux environment.");
            if (operation == "remove")
                ConfigurationStore.Save(root, configuration with
                {
                    ExecutionConsent = null,
                    Development = configuration.Development is { } development ? development with { Enabled = false } : null
                });
            if (operation == "prepare")
                ConfigurationStore.Save(root, configuration with
                {
                    Distribution = WslWorkerEnvironment.DistributionName(installation.InstallationId),
                    LinuxUser = WslWorkerEnvironment.LinuxUser,
                    LinuxWorkspaceRoot = WslWorkerEnvironment.Workspace,
                    Development = (configuration.Development ?? new WorkbenchDevelopmentConfiguration()) with
                    {
                        Enabled = false,
                        WorkerExecutable = WslWorkerEnvironment.WorkerExecutable(installation.ActiveRelease)
                    }
                });
            request = new(Guid.NewGuid().ToString("D"), installation.InstallationId, installation.ActiveRelease, operation);
            LocalState.Write(WslWorkerEnvironment.RequestPath(root), request);
            if (OperatingSystem.IsWindows())
            {
                MachineCertificates.GrantServiceStateAccess(root, installation);
                MachineCertificates.GrantServiceLinuxAccess(root, installation);
            }
            await service.StartAsync(installation, cancellationToken);
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(25));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (File.Exists(WslWorkerEnvironment.StatusPath(root)))
                {
                    var status = LocalState.Read<WorkerSetupStatus>(WslWorkerEnvironment.StatusPath(root));
                    if (status.RequestId == request.Id && status.InstallationId == installation.InstallationId &&
                        status.ReleaseId == installation.ActiveRelease)
                    {
                        progress(status);
                        if (status.State == "failed")
                            throw new IOException($"Worker setup failed at {status.Stage}: {status.Detail}");
                        if (status.State == (operation == "remove" ? "removed" : "ready"))
                            return;
                    }
                }
                if (!service.IsRunning(installation))
                    throw new IOException("The service stopped before reporting worker readiness. See runtime-failure.log in setup diagnostics.");
                await Task.Delay(TimeSpan.FromMilliseconds(500), deadline.Token);
            }
        }
        finally
        {
            if (!installation.RunRequested)
                await service.StopAsync(installation, CancellationToken.None);
        }
    }
}
