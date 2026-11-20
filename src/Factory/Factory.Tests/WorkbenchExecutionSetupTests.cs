using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Factory.Contracts;
using Factory.Workbench;
using Factory.Workbench.Setup;
using Xunit;
using static Factory.Tests.PlantApiTests;

namespace Factory.Tests;

public sealed class WorkbenchExecutionSetupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"factory-execution-setup-{Guid.NewGuid():N}");
    private const string Release = "managed-execution-test";
    private readonly TestService service = new();

    [Fact]
    public async Task StartAndStopManageLocalConsentWithoutCopyingCredentialsOrReplacingEnrollment()
    {
        var (local, binding, desired) = Prepare();
        var handler = new ConfigurationHandler(desired);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://plant.example") };
        var plant = new PlantHttp(client, _ => Task.FromResult("isolated-machine-token"));
        var setup = new WorkbenchExecutionSetup(root, service);
        await setup.StartAsync(plant, binding, desired, CancellationToken.None);
        var saved = ConfigurationStore.Read(root);
        Assert.Equal(local.InstallationId, saved.InstallationId);
        Assert.Equal(local.TenantId, saved.TenantId);
        Assert.Equal(binding, BindingStore.Read(root));
        Assert.Equal(["product/source", "product/tasks"], saved.AllowedRepositories);
        Assert.Equal("", saved.Development!.Model);
        Assert.False(saved.Development.Enabled);
        var effective = WorkbenchExecutionConfiguration.Resolve(saved, desired);
        Assert.True(effective.Development!.Enabled);
        Assert.Equal("test-model", effective.Development.Model);
        Assert.Equal("Factory", effective.Development.GitAuthorName);
        Assert.Equal("factory@example.invalid", effective.Development.GitAuthorEmail);
        Assert.True(service.Running);
        Assert.Equal(2, handler.Requests);
        Assert.DoesNotContain("token", File.ReadAllText(Path.Combine(root, "config.json")), StringComparison.OrdinalIgnoreCase);
        await setup.StopAsync(CancellationToken.None);
        saved = ConfigurationStore.Read(root);
        Assert.Null(saved.ExecutionConsent);
        Assert.False(WorkbenchExecutionConfiguration.Resolve(saved, desired).Development!.Enabled);
        Assert.True(service.Running);
        Assert.Equal(binding, BindingStore.Read(root));
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("membership")]
    [InlineData("source")]
    [InlineData("tasks")]
    [InlineData("line")]
    [InlineData("disabled")]
    [InlineData("removed")]
    public void ConsentCannotFollowAssignmentOrRepositoryChangesOrFallBackToLegacyExecution(string change)
    {
        var (local, _, desired) = Prepare();
        local = local with { ExecutionConsent = WorkbenchExecutionConsent.From(desired) };
        Assert.True(WorkbenchExecutionConfiguration.Resolve(local, desired).Development!.Enabled);
        var line = desired.ProductionLine!;
        var changed = change switch
        {
            "binding" => desired with { BindingVersion = 2 },
            "membership" => desired with { AssignmentVersion = 2 },
            "source" => desired with { ProductionLine = line with { Definition = line.Definition with { Source = line.Definition.Source with { Repository = "other/source" } } } },
            "tasks" => desired with { ProductionLine = line with { Definition = line.Definition with { Tasks = line.Definition.Tasks with { Repository = "other/tasks" } } } },
            "line" => desired with { ProductionLine = line with { Id = Key() } },
            "disabled" => desired with { ProductionLine = line with { Definition = line.Definition with { Execution = line.Definition.Execution! with { Enabled = false } } } },
            _ => desired with { ProductionLine = line with { Definition = line.Definition with { Execution = null } } }
        };
        Assert.False(WorkbenchExecutionConfiguration.Resolve(local, changed).Development!.Enabled);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("verification")]
    [InlineData("completion")]
    [InlineData("worker")]
    [InlineData("journal")]
    public async Task StartRefusesChangedAssignmentsAndMissingPrerequisitesWithoutGrantingConsent(string blocker)
    {
        var (_, binding, approved) = Prepare();
        var current = blocker == "changed" ? approved with { BindingVersion = 2 } : approved;
        var handler = new ConfigurationHandler(current) { Blocker = blocker };
        if (blocker == "worker")
            File.Delete(WslWorkerEnvironment.StatusPath(root));
        if (blocker == "journal")
            File.WriteAllText(Path.Combine(LocalState.RuntimeDirectory(root), "active-development-worker.json"), "retained fixture");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://plant.example") };
        var plant = new PlantHttp(client, _ => Task.FromResult("isolated-machine-token"));
        var error = await Record.ExceptionAsync(() =>
            new WorkbenchExecutionSetup(root, service).StartAsync(plant, binding, approved, CancellationToken.None));
        Assert.True(error is InvalidDataException or InvalidOperationException, error?.ToString());
        Assert.Null(ConfigurationStore.Read(root).ExecutionConsent);
        Assert.False(ConfigurationStore.Read(root).AllowShellExecution);
        Assert.Equal(0, service.Starts);
        Assert.Equal(0, service.Stops);
    }

    [Fact]
    public async Task StopRevokesConsentButPreservesUnresolvedMissionOwnership()
    {
        var (local, _, desired) = Prepare();
        ConfigurationStore.Save(root, local with { ExecutionConsent = WorkbenchExecutionConsent.From(desired) });
        var journal = Path.Combine(LocalState.RuntimeDirectory(root), "active-development-worker.json");
        File.WriteAllText(journal, "retained fixture");
        await Assert.ThrowsAsync<IOException>(() => new WorkbenchExecutionSetup(root, service).StopAsync(CancellationToken.None));
        Assert.Null(ConfigurationStore.Read(root).ExecutionConsent);
        Assert.Equal("retained fixture", File.ReadAllText(journal));
        Assert.False(service.Running);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("reviewers")]
    [InlineData("deadline")]
    public void InvalidManagedPolicyIsRejected(string field)
    {
        var (_, _, desired) = Prepare();
        var definition = desired.ProductionLine!.Definition;
        var execution = definition.Execution!;
        execution = field switch
        {
            "model" => execution with { Model = "" },
            "reviewers" => execution with { Reviewers = ["reviewer", "REVIEWER"] },
            _ => execution with { ApprovalTimeoutHours = 169 }
        };
        Assert.Throws<ContractValidationException>(() => (definition with { Execution = execution }).Validate());
    }

    [Fact]
    public void LegacyLineSerializationDoesNotRewriteFrozenDigests()
    {
        var legacy = ProductionLineTests.Definition();
        Assert.DoesNotContain("\"execution\"", JsonSerializer.Serialize(legacy, RepositoryWorkload.Json));
        var (local, _, desired) = Prepare();
        Assert.Same(local, WorkbenchExecutionConfiguration.Resolve(local,
            desired with { ProductionLine = desired.ProductionLine! with { Definition = legacy } }));
    }

    private (WorkbenchConfiguration, WorkbenchBinding, WorkbenchLineConfiguration) Prepare()
    {
        var installationId = Key();
        var local = new WorkbenchConfiguration
        {
            SchemaVersion = "1",
            InstallationId = installationId,
            PlantServiceUrl = "https://plant.example",
            TenantId = ApiTestApplication.Tenant,
            ApiClientId = ApiTestApplication.Audience,
            InstallerClientId = ApiTestApplication.Audience,
            Distribution = WslWorkerEnvironment.DistributionName(installationId),
            LinuxUser = WslWorkerEnvironment.LinuxUser,
            LinuxWorkspaceRoot = WslWorkerEnvironment.Workspace,
            AllowedRepositories = [],
            AllowShellExecution = false,
            AllowPublication = false,
            Development = new() { WorkerExecutable = WslWorkerEnvironment.WorkerExecutable(Release) }
        };
        ConfigurationStore.Save(root, local);
        var binding = new WorkbenchBinding("1", installationId, null, $"wb-{Guid.Parse(installationId):N}",
            ApiTestApplication.Tenant, ApiTestApplication.DaemonClientId, ApiTestApplication.DaemonId, new('A', 40), Key());
        LocalState.Write(Path.Combine(root, "binding.json"), binding);
        LocalState.SaveInstallation(root, new("1", installationId, Path.Combine(root, "binaries"), "installed",
            Release, null, null, [Release], true));
        LocalState.Write(WslWorkerEnvironment.StatusPath(root), new WorkerSetupStatus(Key(), installationId,
            Release, Environment.ProcessId, DateTimeOffset.UtcNow, "ready", "complete", "Isolated readiness fixture."));
        var definition = ProductionLineTests.Definition() with
        {
            Policy = new(true, true, 3, 30),
            Execution = new(true, "test-model", "Factory", "factory@example.invalid", ["reviewer"], 48)
        };
        var line = new ProductionLineView("1", Key(), $"\"{Guid.NewGuid():N}\"",
            new(ApiTestApplication.Tenant, ApiTestApplication.OwnerId), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, definition);
        return (local, binding, new("1", binding.EnrollmentId, binding.WorkbenchId, Key(), 1, 1, line));
    }

    private sealed class ConfigurationHandler(WorkbenchLineConfiguration desired) : HttpMessageHandler
    {
        public string? Blocker { get; init; }
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/configuration", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(desired) });
            Assert.EndsWith("/workload", request.RequestUri.AbsolutePath);
            var files = RepositoryWorkloadTests.Files(Blocker != "verification");
            if (Blocker == "completion")
                files["workload.json"] = files["workload.json"].Replace(",\"completionCriterion\":\"pull_request_merged\"", "");
            var workload = ResolvedRepositoryWorkload.Create(desired, new('a', 40), files);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(workload) });
        }
    }

    private sealed class TestService : IWorkbenchService
    {
        public bool Running { get; private set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public Task ConfigureAsync(string stateDirectory, WorkbenchInstallation installation, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAsync(WorkbenchInstallation installation, CancellationToken cancellationToken) { Running = true; Starts++; return Task.CompletedTask; }
        public Task StopAsync(WorkbenchInstallation installation, CancellationToken cancellationToken) { Running = false; Stops++; return Task.CompletedTask; }
        public Task RemoveAsync(WorkbenchInstallation installation, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsRunning(WorkbenchInstallation installation) => Running;
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
