using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Contracts;
using Factory.Workbench;
using Factory.Workbench.Setup;
using Xunit;

namespace Factory.Tests;

public sealed class WorkbenchSetupTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"factory-setup-tests-{Guid.NewGuid():N}");
    private string Root => Path.Combine(directory, "installation");
    private string Package => Path.Combine(directory, "package");

    [Theory]
    [InlineData("http://plant.example")]
    [InlineData("https://user:password@plant.example")]
    [InlineData("https://plant.example/path")]
    [InlineData("https://plant.example?token=value")]
    [InlineData("https://plant.example#fragment")]
    public void ConfigurationRejectsUntrustedEndpointShapes(string endpoint)
    {
        Assert.Throws<ContractValidationException>(() => (Configuration() with { PlantServiceUrl = endpoint }).Validate());
    }

    [Fact]
    public void ConfigurationRejectsUnsafeWorkerSelectionsAndPolicy()
    {
        var valid = Configuration();
        foreach (var invalid in new[]
        {
            valid with { Distribution = "--exec sh" },
            valid with { Distribution = "--exec" },
            valid with { LinuxUser = "root;echo" },
            valid with { LinuxWorkspaceRoot = "/" },
            valid with { LinuxWorkspaceRoot = "/home/worker/../other" },
            valid with { AllowedRepositories = ["https://github.com/owner/repository"] },
            valid with { AllowedRepositories = ["owner/*"] },
            valid with { AllowedRepositories = ["owner/repository", "OWNER/Repository"] },
            valid with { TenantId = Guid.Empty.ToString("D") }
        })
            Assert.Throws<ContractValidationException>(invalid.Validate);
    }

    [Fact]
    public void ConfigurationPersistsAndPreservesInstallationIdentityAndJournals()
    {
        var configuration = Configuration();
        ConfigurationStore.Save(Root, configuration);
        var journal = Path.Combine(Root, "journal.json");
        File.WriteAllText(journal, "existing state");
        ConfigurationStore.Save(Root, configuration with { Distribution = "Ubuntu-24.04" });
        var restored = ConfigurationStore.Read(Root);
        Assert.Equal(configuration.InstallationId, restored.InstallationId);
        Assert.Equal("Ubuntu-24.04", restored.Distribution);
        Assert.Equal("existing state", File.ReadAllText(journal));
        Assert.Throws<InvalidDataException>(() => ConfigurationStore.Save(Root, Configuration()));
        Assert.Empty(Directory.GetFiles(Root, "*.tmp"));
    }

    [Theory]
    [InlineData("\"unexpectedSecret\":\"do-not-accept\",")]
    [InlineData("\"tenantId\":\"00000000-0000-0000-0000-000000000001\",")]
    public void ConfigurationRejectsUnknownAndDuplicateFields(string extra)
    {
        Directory.CreateDirectory(Root);
        var json = JsonSerializer.Serialize(Configuration(), ConfigurationStore.Json);
        File.WriteAllText(Path.Combine(Root, "config.json"), "{" + extra + json[1..]);
        Assert.Throws<JsonException>(() => ConfigurationStore.Read(Root));
    }

    [Fact]
    public void CancelledConfigurationDoesNotWriteAndReconfigurationCanRemovePermissions()
    {
        var configuration = Configuration() with
        {
            AllowedRepositories = ["owner/repository"],
            AllowShellExecution = true,
            AllowPublication = true
        };
        ConfigurationStore.Save(Root, configuration);
        var path = Path.Combine(Root, "config.json");
        var original = File.ReadAllText(path);
        WorkbenchConfigurator.Configure(Root, new StringReader(string.Join('\n',
            Enumerable.Repeat("", 10).Append("no"))), new StringWriter());
        Assert.Equal(original, File.ReadAllText(path));

        var values = Enumerable.Repeat("", 7).Concat(["-", "no", "no", "yes", ""]);
        WorkbenchConfigurator.Configure(Root, new StringReader(string.Join('\n', values)), new StringWriter());
        var updated = ConfigurationStore.Read(Root);
        Assert.Equal(configuration.InstallationId, updated.InstallationId);
        Assert.Empty(updated.AllowedRepositories);
        Assert.False(updated.AllowShellExecution);
        Assert.False(updated.AllowPublication);
    }

    [Fact]
    public async Task InspectionWithMissingConfigurationDoesNotCreateInstallation()
    {
        var output = new StringWriter();
        Assert.Equal(1, await WorkbenchInspection.RunAsync(Root, output, CancellationToken.None));
        Assert.Contains("Configuration missing", output.ToString());
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public void InstallationRequiresExplicitUnsignedConsentAndChecksCopiedDigests()
    {
        WritePackage();
        Assert.Throws<InvalidDataException>(() => PackageInstaller.Install(Package, Root, false));
        Assert.False(Directory.Exists(Root));
        File.WriteAllText(Path.Combine(Package, "host", "Factory.Workbench.Host.exe"), "tampered");
        var error = Assert.Throws<InvalidDataException>(() => PackageInstaller.Install(Package, Root, true));
        Assert.Contains("digest mismatch", error.Message);
        Assert.False(Directory.Exists(Path.Combine(Root, "releases", "test-release")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(Root, "releases")));
    }

    [Theory]
    [InlineData("..\\escaped.txt")]
    [InlineData("C:\\escaped.txt")]
    [InlineData("directory\\..\\escaped.txt")]
    [InlineData("Factory.Workbench.Host.exe:stream")]
    [InlineData("workbench-package.json")]
    public void InstallationRejectsPackagePathEscapes(string name)
    {
        var manifest = WritePackage();
        manifest.Files.Add(name, new string('a', 64));
        WriteManifest(manifest);
        Assert.Throws<InvalidDataException>(() => PackageInstaller.Install(Package, Root, true));
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public void InstallationStagesImmutableReleasesAndPreservesConfiguration()
    {
        var configuration = Configuration();
        ConfigurationStore.Save(Root, configuration);
        var manifest = WritePackage();
        var executable = PackageInstaller.Install(Package, Root, true);
        Assert.Equal("setup host fixture", File.ReadAllText(executable));
        Assert.Equal(configuration.InstallationId, ConfigurationStore.Read(Root).InstallationId);
        Assert.Throws<IOException>(() => PackageInstaller.Install(Package, Root, true));
        WriteManifest(manifest with { ReleaseId = "second-release" });
        var second = PackageInstaller.Install(Package, Root, true);
        Assert.NotEqual(executable, second);
        Assert.True(File.Exists(executable));
        Assert.False(File.Exists(Path.Combine(Root, "startup.json")));
    }

    private WorkbenchPackage WritePackage()
    {
        Directory.CreateDirectory(Path.Combine(Package, "host"));
        var content = Encoding.UTF8.GetBytes("setup host fixture");
        File.WriteAllBytes(Path.Combine(Package, "host", "Factory.Workbench.Host.exe"), content);
        var manifest = new WorkbenchPackage("1", "test-release", "win-x64", "local-test",
            new() { ["Factory.Workbench.Host.exe"] = Convert.ToHexString(SHA256.HashData(content)) });
        WriteManifest(manifest);
        return manifest;
    }

    private void WriteManifest(WorkbenchPackage manifest) =>
        File.WriteAllText(Path.Combine(Package, "workbench-package.json"),
            JsonSerializer.Serialize(manifest, ConfigurationStore.Json));

    private static WorkbenchConfiguration Configuration() => new()
    {
        SchemaVersion = "1",
        InstallationId = Guid.NewGuid().ToString("D"),
        PlantServiceUrl = "https://plant.example",
        TenantId = "00000000-0000-0000-0000-000000000001",
        ApiClientId = "00000000-0000-0000-0000-000000000002",
        InstallerClientId = "00000000-0000-0000-0000-000000000003",
        Distribution = "Ubuntu",
        LinuxUser = "worker",
        LinuxWorkspaceRoot = "/home/worker/.local/share/factory/workbench/assignments",
        AllowedRepositories = [],
        AllowShellExecution = false,
        AllowPublication = false
    };

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
