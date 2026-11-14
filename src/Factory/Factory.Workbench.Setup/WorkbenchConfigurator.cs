using System.Text.Json;
using Factory.Contracts;

namespace Factory.Workbench.Setup;

public static class WorkbenchConfigurator
{
    public static void Configure(string root, TextReader input, TextWriter output)
    {
        using var maintenance = LocalState.Lock(root, "maintenance.lock");
        using var runtime = LocalState.LockRuntime(root);
        if (File.Exists(Path.Combine(root, "installation.json")) &&
            LocalState.ReadInstallation(root).State is not ("installed" or "uninstalled"))
            throw new InvalidDataException("Complete pending maintenance with recover before configuring.");
        var existing = File.Exists(Path.Combine(root, "config.json")) ? ConfigurationStore.Read(root) : null;
        output.WriteLine("Configure local settings from your trusted plant installation profile.");
        output.WriteLine("No sign-in, enrollment, WSL interaction, or worker execution is performed.");
        var service = Prompt("Plant HTTPS origin", existing?.PlantServiceUrl);
        var tenant = Prompt("Expected Entra tenant ID", existing?.TenantId);
        var api = Prompt("Expected plant API client ID", existing?.ApiClientId);
        var installer = Prompt("Installer public-client ID", existing?.InstallerClientId);
        var distribution = Prompt("Future WSL distribution (not verified)", existing?.Distribution ?? "Ubuntu");
        var linuxUser = Prompt("Future WSL user (not verified)", existing?.LinuxUser);
        var workspace = Prompt("Future Linux workspace root",
            existing?.LinuxWorkspaceRoot ?? $"/home/{linuxUser}/.local/share/factory/workbench/assignments");
        var repositories = Prompt("Allowed GitHub repositories (comma-separated; '-' denies all)",
            existing is null ? "-" : string.Join(",", existing.AllowedRepositories.DefaultIfEmpty("-")));
        var configuration = new WorkbenchConfiguration
        {
            SchemaVersion = "1",
            InstallationId = existing?.InstallationId ?? Guid.NewGuid().ToString("D"),
            PlantServiceUrl = service,
            TenantId = tenant,
            ApiClientId = api,
            InstallerClientId = installer,
            Distribution = distribution,
            LinuxUser = linuxUser,
            LinuxWorkspaceRoot = workspace,
            AllowedRepositories = repositories == "-" ? [] :
                repositories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            AllowShellExecution = Permission("Allow agent shell execution", existing?.AllowShellExecution ?? false),
            AllowPublication = Permission("Allow branch/PR publication", existing?.AllowPublication ?? false)
        };
        configuration.Validate();
        output.WriteLine(JsonSerializer.Serialize(configuration, ConfigurationStore.Json));
        output.Write("Save this local configuration? [y/N]: ");
        var answer = input.ReadLine() ?? throw new EndOfStreamException("Configuration cancelled: input closed.");
        if (!answer.Equals("y", StringComparison.OrdinalIgnoreCase) &&
            !answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine("Configuration not saved.");
            return;
        }
        ConfigurationStore.Save(root, configuration);
        output.WriteLine($"Configuration saved: {Path.Combine(root, "config.json")}");
        output.WriteLine("Settings alone do not enroll this machine. Use Factory Workbench Setup to sign in, select a plant, and enroll.");

        string Prompt(string label, string? current)
        {
            output.Write($"{label}{(current is null ? "" : $" [{current}]")}: ");
            var value = input.ReadLine() ?? throw new EndOfStreamException("Configuration cancelled: input closed.");
            value = value.Trim();
            if (value.Length == 0)
                value = current ?? "";
            if (value.Length == 0)
                throw new InvalidDataException($"{label} is required.");
            return value;
        }

        bool Permission(string label, bool current)
        {
            var value = Prompt($"{label} (yes/no)", current ? "yes" : "no");
            return value.ToLowerInvariant() switch
            {
                "yes" or "y" => true,
                "no" or "n" => false,
                _ => throw new InvalidDataException($"{label} must be yes or no.")
            };
        }
    }
}
