using System.ComponentModel;

namespace Factory.Workbench.Setup;

public interface IWorkbenchService
{
    Task ConfigureAsync(string stateDirectory, WorkbenchInstallation installation, CancellationToken cancellationToken);
    Task StartAsync(WorkbenchInstallation installation, CancellationToken cancellationToken);
    Task StopAsync(WorkbenchInstallation installation, CancellationToken cancellationToken);
    Task RemoveAsync(WorkbenchInstallation installation, CancellationToken cancellationToken);
    bool IsRunning(WorkbenchInstallation installation);
}

public sealed partial class WorkbenchLifecycle(string root, IWorkbenchService service)
{
    public async Task InstallAsync(string package, string installDirectory, bool allowUnsignedPackage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var maintenance = LocalState.Lock(root, "maintenance.lock");
        using var runtime = LocalState.LockRuntime(root);
        BindingStore.RequireEnrollmentAllowed(root);
        var configuration = ConfigurationStore.Read(root);
        if (File.Exists(Path.Combine(root, "installation.json")) &&
            LocalState.ReadInstallation(root).State != "uninstalled")
            throw new InvalidDataException("This workbench is already installed. Use update or recover.");
        var directory = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var stateDirectory = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (directory.Equals(stateDirectory, StringComparison.OrdinalIgnoreCase) ||
            directory.StartsWith(stateDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            stateDirectory.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Binary and state directories must be separate, non-nested locations.");
        var manifest = PackageInstaller.ReadManifest(package);
        var installation = new WorkbenchInstallation("1", configuration.InstallationId, directory, "installing",
            manifest.ReleaseId, null, null, [manifest.ReleaseId], false);
        installation.Validate();
        if (!allowUnsignedPackage)
            throw new InvalidDataException("Unsigned development installation requires --allow-unsigned-package.");
        ClaimDirectory(installation);
        LocalState.SaveInstallation(root, installation);
        PackageInstaller.Install(package, directory, allowUnsignedPackage);
        await service.ConfigureAsync(root, installation, cancellationToken);
        LocalState.SaveInstallation(root, installation with { State = "installed" });
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var maintenance = LocalState.Lock(root, "maintenance.lock");
        BindingStore.RequireEnrollmentAllowed(root);
        var installation = Installed();
        VerifyConfiguration(installation);
        VerifyRelease(installation, installation.ActiveRelease);
        if (installation.RunRequested && service.IsRunning(installation))
            return;
        using (LocalState.LockRuntime(root))
        {
            installation = installation with { RunRequested = true };
            LocalState.SaveInstallation(root, installation);
        }
        await service.StartAsync(installation, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var maintenance = LocalState.Lock(root, "maintenance.lock");
        var installation = Owned() with { RunRequested = false };
        LocalState.SaveInstallation(root, installation);
        await service.StopAsync(installation, cancellationToken);
        using var runtime = LocalState.LockRuntime(root);
    }

    public async Task UpdateAsync(string package, bool allowUnsignedPackage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var maintenance = LocalState.Lock(root, "maintenance.lock");
        BindingStore.RequireEnrollmentAllowed(root);
        var installation = Installed();
        VerifyConfiguration(installation);
        var manifest = PackageInstaller.ReadManifest(package);
        if (installation.OwnedReleases.Contains(manifest.ReleaseId, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Release already belongs to this installation. Use rollback to select a previous release.");
        if (installation.OwnedReleases.Length >= 256)
            throw new InvalidDataException("Release inventory is full; explicit release retention management is required.");
        PackageInstaller.Install(package, installation.InstallDirectory, allowUnsignedPackage);
        installation = installation with { OwnedReleases = [.. installation.OwnedReleases, manifest.ReleaseId] };
        LocalState.SaveInstallation(root, installation);
        await SwitchAsync(installation, manifest.ReleaseId, cancellationToken);
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var maintenance = LocalState.Lock(root, "maintenance.lock");
        BindingStore.RequireEnrollmentAllowed(root);
        var installation = Installed();
        VerifyConfiguration(installation);
        if (installation.PreviousRelease is not { } previous)
            throw new InvalidDataException("There is no previous release to roll back to.");
        VerifyRelease(installation, previous);
        await SwitchAsync(installation, previous, cancellationToken);
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var maintenance = LocalState.Lock(root, "maintenance.lock");
        var installation = Owned();
        var installer = File.Exists(InstallerJournal)
            ? LocalState.Read<InstallerMaintenance>(InstallerJournal) : null;
        if (installer is not null)
        {
            ReadInstaller(installer.TransactionId);
            if (installer.Original.InstallationId != installation.InstallationId ||
                installer.Original.InstallDirectory != installation.InstallDirectory)
                throw new InvalidDataException("Installer recovery belongs to another installation.");
        }
        if (installation.State is "uninstalling" or "uninstalled")
        {
            await RemoveAsync(installation, cancellationToken);
            if (installer is not null)
                File.Delete(InstallerJournal);
            return;
        }
        BindingStore.RequireEnrollmentAllowed(root);
        if (installer is not null)
        {
            await RollbackInstallerCoreAsync(installer.TransactionId, cancellationToken);
            return;
        }
        if (installation.State == "installing" &&
            !Directory.Exists(Path.GetDirectoryName(installation.Executable())))
        {
            await RemoveAsync(installation, cancellationToken);
            return;
        }
        VerifyConfiguration(installation);
        await service.StopAsync(installation, cancellationToken);
        using (LocalState.LockRuntime(root))
        {
            VerifyRelease(installation, installation.ActiveRelease);
            installation = installation with { State = "installed", PendingRelease = null };
            await service.ConfigureAsync(root, installation, cancellationToken);
            LocalState.SaveInstallation(root, installation);
        }
        if (installation.RunRequested)
            await service.StartAsync(installation, cancellationToken);
    }

    public async Task UninstallAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var maintenance = LocalState.Lock(root, "maintenance.lock");
        await RemoveAsync(Owned(), cancellationToken);
    }

    private async Task SwitchAsync(WorkbenchInstallation original, string target, CancellationToken cancellationToken)
    {
        VerifyRelease(original, target);
        await service.StopAsync(original, cancellationToken);
        var runtime = LocalState.LockRuntime(root);
        try
        {
            WorkbenchInstallation activated;
            using (runtime)
            {
                var pending = original with { State = "updating", PendingRelease = target };
                LocalState.SaveInstallation(root, pending);
                await service.ConfigureAsync(root, pending, cancellationToken);
                activated = pending with
                {
                    State = "installed",
                    ActiveRelease = target,
                    PreviousRelease = original.ActiveRelease,
                    PendingRelease = null
                };
                LocalState.SaveInstallation(root, activated);
            }
            if (activated.RunRequested)
                await service.StartAsync(activated, cancellationToken);
        }
        catch (Exception error) when (MaintenanceFailure(error))
        {
            try
            {
                LocalState.SaveInstallation(root, original with { State = "updating", PendingRelease = target });
                await service.StopAsync(original, CancellationToken.None);
                using (LocalState.LockRuntime(root))
                {
                    VerifyRelease(original, original.ActiveRelease);
                    await service.ConfigureAsync(root, original, CancellationToken.None);
                    LocalState.SaveInstallation(root, original);
                }
                if (original.RunRequested)
                    await service.StartAsync(original, CancellationToken.None);
            }
            catch (Exception recoveryError) when (MaintenanceFailure(recoveryError))
            {
                throw new AggregateException("Release activation and restoration failed. Run recover; do not delete state.",
                    error, recoveryError);
            }
            throw new IOException("Release activation failed; the previous selection was restored.", error);
        }
    }

    private async Task RemoveAsync(WorkbenchInstallation installation, CancellationToken cancellationToken)
    {
        if (installation.State == "uninstalled")
        {
            await service.RemoveAsync(installation, cancellationToken);
            return;
        }
        installation = installation with { State = "uninstalling", PendingRelease = null, RunRequested = false };
        LocalState.SaveInstallation(root, installation);
        await service.StopAsync(installation, cancellationToken);
        using var runtime = LocalState.LockRuntime(root);
        await service.RemoveAsync(installation, cancellationToken);
        foreach (var release in installation.OwnedReleases)
        {
            var directory = Path.Combine(installation.InstallDirectory, "releases", release);
            if (Directory.Exists(directory))
            {
                RejectLinks(directory);
                Directory.Delete(directory, recursive: true);
            }
        }
        LocalState.SaveInstallation(root, installation with
        {
            State = "uninstalled", OwnedReleases = [], PreviousRelease = null
        });
    }

    private WorkbenchInstallation Installed()
    {
        var installation = Owned();
        if (installation.State != "installed")
            throw new InvalidDataException("Installation is not active. Complete pending maintenance with recover.");
        return installation;
    }

    private WorkbenchInstallation Owned()
    {
        var installation = LocalState.ReadInstallation(root);
        var marker = Path.Combine(installation.InstallDirectory, "installation.id");
        if (!File.Exists(marker) || File.ReadAllText(marker) != installation.InstallationId)
            throw new InvalidDataException("Binary-directory ownership is missing or does not match this installation.");
        return installation;
    }

    private static void ClaimDirectory(WorkbenchInstallation installation)
    {
        var directory = installation.InstallDirectory;
        var marker = Path.Combine(directory, "installation.id");
        if (File.Exists(marker))
        {
            if (File.ReadAllText(marker) != installation.InstallationId)
                throw new InvalidDataException("Binary directory belongs to another installation.");
            return;
        }
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new InvalidDataException("Use an empty binary directory; setup will not adopt existing unrelated files.");
        Directory.CreateDirectory(directory);
        using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(installation.InstallationId);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private void VerifyConfiguration(WorkbenchInstallation installation)
    {
        if (ConfigurationStore.Read(root).InstallationId != installation.InstallationId)
            throw new InvalidDataException("Configuration and installed machine identities do not match.");
    }

    private static void VerifyRelease(WorkbenchInstallation installation, string release) =>
        PackageInstaller.VerifyInstalled(Path.Combine(installation.InstallDirectory, "releases", release), release);

    private static void RejectLinks(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Refusing to remove a linked release directory.");
        foreach (var child in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Refusing to remove a release containing links.");
            if (Directory.Exists(child))
                RejectLinks(child);
        }
    }

    private static bool MaintenanceFailure(Exception error) => error is IOException or InvalidDataException or
        UnauthorizedAccessException or Win32Exception or InvalidOperationException or OperationCanceledException;
}
