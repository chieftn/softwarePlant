namespace Factory.Workbench.Setup;

public sealed record InstallerMaintenance(string TransactionId, string Operation, WorkbenchInstallation Original);

public sealed partial class WorkbenchLifecycle
{
    private string InstallerJournal => Path.Combine(root, "installer-transaction.json");

    public async Task ApplyInstallerUpdateAsync(string transaction, string package, CancellationToken cancellationToken)
    {
        ValidateTransaction(transaction);
        using var maintenance = LocalState.Lock(root, "maintenance.lock");
        if (!File.Exists(Path.Combine(root, "installation.json")) || LocalState.ReadInstallation(root).State == "uninstalled")
            return;
        var pending = LocalState.ReadInstallation(root);
        if (pending.State == "updating" && File.Exists(InstallerJournal))
        {
            BindingStore.RequireEnrollmentAllowed(root);
            var interrupted = LocalState.Read<InstallerMaintenance>(InstallerJournal);
            ReadInstaller(interrupted.TransactionId);
            if (interrupted.Operation != "update" || interrupted.Original.State != "installed" ||
                pending.ActiveRelease != interrupted.Original.ActiveRelease)
                throw new InvalidDataException("Pending maintenance is not an interrupted update of the original active release. Reconcile it before installing.");
            await RollbackInstallerCoreAsync(interrupted.TransactionId, cancellationToken, preserveCurrentStop: true);
        }
        var original = Installed();
        BindingStore.RequireEnrollmentAllowed(root);
        VerifyConfiguration(original);
        var manifest = PackageInstaller.ReadManifest(package);
        if (original.ActiveRelease == manifest.ReleaseId)
        {
            PackageInstaller.VerifySameRelease(package, Path.GetDirectoryName(original.Executable())!);
            return;
        }
        BeginInstaller(transaction, "update", original);
        if (!original.OwnedReleases.Contains(manifest.ReleaseId, StringComparer.OrdinalIgnoreCase))
        {
            if (original.OwnedReleases.Length >= 256)
                throw new InvalidDataException("Release inventory is full; explicit release retention management is required.");
            original = original with { OwnedReleases = [.. original.OwnedReleases, manifest.ReleaseId] };
            LocalState.SaveInstallation(root, original);
        }
        var target = Path.GetDirectoryName(original.Executable(manifest.ReleaseId))!;
        if (!Directory.Exists(target))
            PackageInstaller.Install(package, original.InstallDirectory, allowUnsignedPackage: true);
        else
            PackageInstaller.VerifySameRelease(package, target);
        await SwitchAsync(original, manifest.ReleaseId, cancellationToken);
    }

    public async Task PrepareInstallerUninstallAsync(string transaction, CancellationToken cancellationToken)
    {
        ValidateTransaction(transaction);
        using var maintenance = LocalState.Lock(root, "maintenance.lock");
        if (!File.Exists(Path.Combine(root, "installation.json")) || LocalState.ReadInstallation(root).State == "uninstalled")
            return;
        var installation = Owned();
        BeginInstaller(transaction, "uninstall", installation);
        LocalState.SaveInstallation(root, installation with { State = "uninstalling", PendingRelease = null, RunRequested = false });
        await service.StopAsync(installation, cancellationToken);
        using var runtime = LocalState.LockRuntime(root);
        await service.RemoveAsync(installation, cancellationToken);
    }

    public async Task RollbackInstallerAsync(string transaction, CancellationToken cancellationToken)
    {
        ValidateTransaction(transaction);
        using var maintenance = LocalState.Lock(root, "maintenance.lock");
        await RollbackInstallerCoreAsync(transaction, cancellationToken);
    }

    private async Task RollbackInstallerCoreAsync(string transaction, CancellationToken cancellationToken, bool preserveCurrentStop = false)
    {
        if (!File.Exists(InstallerJournal))
            return;
        var journal = ReadInstaller(transaction);
        var current = Owned();
        if (current.InstallationId != journal.Original.InstallationId ||
            current.InstallDirectory != journal.Original.InstallDirectory)
            throw new InvalidDataException("Installer rollback cannot replace another installation.");
        await service.StopAsync(current, cancellationToken);
        var restored = journal.Original with
        {
            OwnedReleases = journal.Original.OwnedReleases.Union(current.OwnedReleases, StringComparer.OrdinalIgnoreCase).ToArray(),
            RunRequested = journal.Original.RunRequested && (!preserveCurrentStop || current.RunRequested) &&
                !File.Exists(Path.Combine(root, "removal-intent.json"))
        };
        using (LocalState.LockRuntime(root))
        {
            VerifyRelease(restored, restored.ActiveRelease);
            await service.ConfigureAsync(root, restored, cancellationToken);
            LocalState.SaveInstallation(root, restored);
        }
        if (restored.RunRequested && !File.Exists(Path.Combine(root, "removal-intent.json")))
            await service.StartAsync(restored, cancellationToken);
        File.Delete(InstallerJournal);
    }

    public async Task CommitInstallerAsync(string transaction, CancellationToken cancellationToken)
    {
        ValidateTransaction(transaction);
        using var maintenance = LocalState.Lock(root, "maintenance.lock");
        if (!File.Exists(InstallerJournal))
            return;
        var journal = ReadInstaller(transaction);
        if (journal.Operation == "uninstall")
            await RemoveAsync(Owned(), cancellationToken);
        else if (Owned().State != "installed")
            throw new InvalidDataException("Installer cannot commit an incomplete runtime update.");
        File.Delete(InstallerJournal);
    }

    private void BeginInstaller(string transaction, string operation, WorkbenchInstallation original)
    {
        if (File.Exists(InstallerJournal))
        {
            var existing = LocalState.Read<InstallerMaintenance>(InstallerJournal);
            ReadInstaller(existing.TransactionId);
            if (existing.TransactionId != transaction && operation == "uninstall" &&
                File.Exists(Path.Combine(root, "removal-intent.json")) &&
                LocalState.Read<RemovalIntent>(Path.Combine(root, "removal-intent.json")).InstallationId == original.InstallationId &&
                existing.Original.InstallationId == original.InstallationId &&
                existing.Original.InstallDirectory == original.InstallDirectory)
            {
                LocalState.Write(InstallerJournal, new InstallerMaintenance(transaction, operation, original));
                return;
            }
            ReadInstaller(transaction);
            if (existing.Operation != operation || existing.Original.InstallationId != original.InstallationId)
                throw new InvalidDataException("Another installer transaction requires recovery.");
            return;
        }
        LocalState.Write(InstallerJournal, new InstallerMaintenance(transaction, operation, original));
    }

    private InstallerMaintenance ReadInstaller(string transaction)
    {
        ValidateTransaction(transaction);
        var journal = LocalState.Read<InstallerMaintenance>(InstallerJournal);
        journal.Original.Validate();
        if (journal.TransactionId != transaction || journal.Operation is not ("update" or "uninstall"))
            throw new InvalidDataException("Another installer transaction requires recovery.");
        return journal;
    }

    private static void ValidateTransaction(string transaction)
    {
        if (!Guid.TryParse(transaction, out var id) || id == Guid.Empty)
            throw new InvalidDataException("Installer transaction must be a nonempty UUID.");
    }
}
