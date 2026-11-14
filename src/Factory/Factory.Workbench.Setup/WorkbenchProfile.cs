using Factory.Contracts;

namespace Factory.Workbench.Setup;

public static class WorkbenchProfile
{
    public static void Configure(string root, WorkbenchInstallationProfile trusted)
    {
        trusted.Validate();
        using var maintenance = LocalState.Lock(root, "maintenance.lock");
        if (File.Exists(Path.Combine(root, "removal-intent.json")))
            throw new InvalidDataException("Full uninstall is incomplete. Resume removal before installing or enrolling.");
        if (File.Exists(Path.Combine(root, "config.json")))
        {
            var existing = ConfigurationStore.Read(root);
            if (existing.PlantServiceUrl != trusted.PlantServiceUrl || existing.TenantId != trusted.TenantId ||
                existing.ApiClientId != trusted.ApiClientId || existing.InstallerClientId != trusted.InstallerClientId)
                throw new InvalidDataException("This machine has a different installation profile. Do not overwrite or re-enroll its identity.");
            return;
        }
        ConfigurationStore.Save(root, new WorkbenchConfiguration
        {
            SchemaVersion = "1", InstallationId = Guid.NewGuid().ToString("D"),
            PlantServiceUrl = trusted.PlantServiceUrl, TenantId = trusted.TenantId,
            ApiClientId = trusted.ApiClientId, InstallerClientId = trusted.InstallerClientId,
            Distribution = "Ubuntu", LinuxUser = "worker", LinuxWorkspaceRoot = "/home/worker/work",
            AllowedRepositories = [], AllowShellExecution = false, AllowPublication = false
        });
    }
}
