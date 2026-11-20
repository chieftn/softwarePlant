namespace Factory.Workbench.Setup;

public sealed class WorkbenchMissionRetrySetup(string root, IWorkbenchService service)
{
    public async Task<MissionRetryStatus> RequestAsync(CancellationToken cancellationToken)
    {
        using var maintenance = LocalState.Lock(root, "maintenance.lock");
        var installation = LocalState.ReadInstallation(root);
        var snapshot = WorkbenchMissionDiagnostics.ReadSnapshot(root);
        if (!service.IsRunning(installation))
            throw new IOException("Start the existing workbench service before requesting an explicit mission retry.");
        if (File.Exists(Path.Combine(LocalState.RuntimeDirectory(root), "active-development-worker.json")))
            throw new IOException("Prior worker termination is unresolved. Retry will not stop or replace it.");
        if (File.Exists(MissionRetry.RequestPath(root)))
            throw new IOException("An explicit retry is already queued. The service owns its acknowledgement and cleanup; do not replace it.");
        var runtime = LocalState.Read<WorkbenchRuntimeStatus>(LocalState.RuntimeStatusPath(root));
        if (runtime.InstallationId != installation.InstallationId || runtime.ReleaseId != installation.ActiveRelease ||
            !WslWorkerEnvironment.IsReady(root, ConfigurationStore.Read(root), installation.ActiveRelease, runtime.ProcessId))
            throw new IOException("Prepare the updated SDK worker before requesting a telemetry-enabled retry.");
        var request = new MissionRetryRequest(Guid.NewGuid().ToString("D"), installation.InstallationId,
            installation.ActiveRelease, snapshot.Mission.Id, snapshot.Mission.Digest);
        cancellationToken.ThrowIfCancellationRequested();
        LocalState.Write(MissionRetry.RequestPath(root), request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(75));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (File.Exists(MissionRetry.StatusPath(root)))
            {
                var status = LocalState.Read<MissionRetryStatus>(MissionRetry.StatusPath(root));
                if (status.RequestId == request.Id && status.InstallationId == request.InstallationId &&
                    status.ReleaseId == request.ReleaseId && status.State is "started" or "failed")
                {
                    if (status.State == "failed")
                        throw new IOException(status.Detail);
                    return status;
                }
            }
            if (!service.IsRunning(installation))
                throw new IOException("The service stopped before acknowledging the explicit retry. Its queued request is retained.");
            await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
        }
    }
}
