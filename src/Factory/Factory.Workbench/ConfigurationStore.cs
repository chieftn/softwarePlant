using System.Text.Json;
using System.Text.Json.Serialization;
using Factory.Contracts;

namespace Factory.Workbench;

public static class ConfigurationStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = 16
    };

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Factory", "Workbench");

    public static WorkbenchConfiguration Read(string root)
    {
        var configuration = LocalState.Read<WorkbenchConfiguration>(Path.Combine(root, "config.json"));
        configuration.Validate();
        return configuration;
    }

    public static void Save(string root, WorkbenchConfiguration configuration)
    {
        configuration.Validate();
        var path = Path.Combine(root, "config.json");
        if (File.Exists(path) && Read(root).InstallationId != configuration.InstallationId)
            throw new InvalidDataException("Configuration cannot replace this installation's identity.");
        if (File.Exists(path) && (File.Exists(Path.Combine(root, "binding.json")) || File.Exists(Path.Combine(root, "enrollment.json")) ||
            BindingStore.Removal(root) is { State: not "completed" }))
        {
            var previous = Read(root);
            if (previous.PlantServiceUrl != configuration.PlantServiceUrl || previous.TenantId != configuration.TenantId ||
                previous.ApiClientId != configuration.ApiClientId || previous.InstallerClientId != configuration.InstallerClientId)
                throw new InvalidDataException("Plant origin and authentication profile cannot change after enrollment begins.");
        }
        LocalState.Write(path, configuration);
    }
}
