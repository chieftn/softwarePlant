using Factory.Contracts;

namespace Factory.Workbench.Setup;

public sealed class WorkbenchUnenrollment(string root, IWorkbenchService service, Action<string, string> removeCertificate)
{
    public async Task RunAsync(EnrollmentClient? client, Action<EnrollmentView> progress, CancellationToken cancellationToken)
    {
        UnenrollmentJournal removal;
        using (LocalState.Lock(root, "maintenance.lock"))
        {
            if (File.Exists(Path.Combine(root, "installer-transaction.json")))
                throw new InvalidDataException("Recover the interrupted MSI transaction before unenrolling.");
            removal = BindingStore.Removal(root) ?? Begin();
            if (removal.State == "completed")
            {
                if (!File.Exists(Path.Combine(root, "enrollment.json")))
                    return;
                removal = Begin();
            }
            ValidateCurrent(removal.Enrollment);
            LocalState.Write(Path.Combine(root, "unenrollment.json"), removal);
        }
        var lifecycle = new WorkbenchLifecycle(root, service);
        if (File.Exists(Path.Combine(root, "installation.json")) && LocalState.ReadInstallation(root).State != "uninstalled")
            await lifecycle.StopAsync(cancellationToken);
        if (removal.State != "retired")
        {
            if (client is null)
                throw new InvalidDataException("Sign in as a Factory enrollment administrator to retire this device.");
            var view = await client.RevokeAsync(removal.Enrollment, cancellationToken);
            while (true)
            {
                ValidateResponse(removal.Enrollment, view);
                progress(view);
                if (view.State != "revoking")
                    break;
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                view = await client.StatusAsync(removal.Enrollment, cancellationToken);
            }
            if (view.State != "retired")
                throw new InvalidDataException($"Plant access is revoked but retirement is incomplete ({view.State}): {view.Error} " +
                    "Identity and journals are retained. Reconcile the failure, then explicitly retry Unenroll.");
            removal = removal with { State = "retired" };
            using (LocalState.Lock(root, "maintenance.lock"))
            {
                ValidateCurrent(removal.Enrollment);
                LocalState.Write(Path.Combine(root, "unenrollment.json"), removal);
            }
        }
        if (File.Exists(Path.Combine(root, "installation.json")))
            await lifecycle.UninstallAsync(cancellationToken);
        using (LocalState.Lock(root, "maintenance.lock"))
        using (LocalState.LockRuntime(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCurrent(removal.Enrollment);
            var paths = new[] { Path.Combine(root, "binding.json"), Path.Combine(root, "enrollment.json"),
                LocalState.RuntimeStatusPath(root), Path.Combine(LocalState.RuntimeDirectory(root), "pending-registration.json"),
                Path.Combine(LocalState.RuntimeDirectory(root), "pending-presence.json"),
                Path.Combine(LocalState.RuntimeDirectory(root), "pending-factory-presence.json") };
            foreach (var path in paths)
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Unenrollment will not remove linked state files.");
            removeCertificate(root, removal.Enrollment.CertificateThumbprint);
            foreach (var path in paths)
                File.Delete(path);
            LocalState.Write(Path.Combine(root, "unenrollment.json"), removal with { State = "completed" });
        }

        UnenrollmentJournal Begin() => new("1", BindingStore.ReadJournal(root), "requested");
    }

    private void ValidateCurrent(EnrollmentJournal journal)
    {
        BindingStore.ValidateJournal(root, journal);
        if (File.Exists(Path.Combine(root, "enrollment.json")) && BindingStore.ReadJournal(root) != journal)
            throw new InvalidDataException("Unenrollment cannot replace a different enrollment journal.");
        if (File.Exists(Path.Combine(root, "binding.json")))
        {
            var binding = BindingStore.Read(root);
            if (binding.EnrollmentId != journal.OperationId ||
                binding.CertificateThumbprint != journal.CertificateThumbprint)
                throw new InvalidDataException("Unenrollment cannot remove another machine binding.");
        }
    }

    private static void ValidateResponse(EnrollmentJournal journal, EnrollmentView view)
    {
        if (view.SchemaVersion != "1" || view.Id != journal.OperationId ||
            view.WorkbenchId != $"wb-{Guid.Parse(journal.InstallationId):N}" ||
            view.State is not ("revoking" or "retired" or "retirementNeedsAttention"))
            throw new InvalidDataException("Plant returned an unexpected retirement result. No local identity was removed.");
    }
}
