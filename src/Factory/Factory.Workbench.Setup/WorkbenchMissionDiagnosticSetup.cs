namespace Factory.Workbench.Setup;

public sealed class WorkbenchMissionDiagnosticSetup(string root, IWorkbenchService service)
{
    public async Task<MissionDiagnosticStatus?> CollectAsync(CancellationToken cancellationToken)
    {
        using var maintenance = LocalState.Lock(root, "maintenance.lock");
        if (!File.Exists(WorkbenchMissionDiagnostics.SnapshotPath(root)))
            return null;
        var installation = LocalState.ReadInstallation(root);
        if (installation.State != "installed")
            throw new InvalidDataException("Recover the installed runtime before collecting mission diagnostics.");
        var snapshot = WorkbenchMissionDiagnostics.ReadSnapshot(root);
        if (File.Exists(Path.Combine(LocalState.RuntimeDirectory(root), "active-development-worker.json")))
            throw new IOException("An owned worker is active or unresolved. Diagnostics will not interrupt or stop it.");
        var wasRunning = service.IsRunning(installation);
        var request = new MissionDiagnosticRequest(Guid.NewGuid().ToString("D"), installation.InstallationId,
            installation.ActiveRelease, snapshot.Mission.Id, snapshot.Mission.Digest, !wasRunning);
        LocalState.Write(WorkbenchMissionDiagnostics.RequestPath(root), request);
        try
        {
            if (!wasRunning)
                await service.StartAsync(installation, cancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(75));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (File.Exists(WorkbenchMissionDiagnostics.StatusPath(root)))
                {
                    var status = LocalState.Read<MissionDiagnosticStatus>(WorkbenchMissionDiagnostics.StatusPath(root));
                    if (status.RequestId == request.Id && status.InstallationId == installation.InstallationId &&
                        status.ReleaseId == installation.ActiveRelease && status.State is "collected" or "failed")
                    {
                        if (status.State == "collected" && (status.Mission is null ||
                            status.Mission.MissionId != request.MissionId || status.Mission.MissionDigest != request.MissionDigest))
                            throw new InvalidDataException("The diagnostic result does not match the requested retained mission.");
                        return status;
                    }
                }
                if (!service.IsRunning(installation))
                    throw new IOException("The service stopped before returning retained-mission diagnostics.");
                await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
            }
        }
        finally
        {
            try
            {
                if (!wasRunning)
                    await service.StopAsync(installation, CancellationToken.None);
            }
            finally
            {
                var path = WorkbenchMissionDiagnostics.RequestPath(root);
                if (File.Exists(path) && LocalState.Read<MissionDiagnosticRequest>(path).Id == request.Id)
                    File.Delete(path);
            }
        }
    }
}
