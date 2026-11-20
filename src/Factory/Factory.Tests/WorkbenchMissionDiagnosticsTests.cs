using System.IO.Compression;
using System.Text.Json;
using Factory.Contracts;
using Factory.Workbench;
using Factory.Workbench.Host;
using Factory.Workbench.Setup;
using Xunit;

namespace Factory.Tests;

public sealed class WorkbenchMissionDiagnosticsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"factory-mission-diagnostics-{Guid.NewGuid():N}");
    private readonly string installationId = PlantApiTests.Key();
    private readonly DevelopmentMission mission = DevelopmentMissionTests.Mission();
    private WorkbenchInstallation Installation => new("1", installationId, Path.Combine(root, "binaries"), "installed",
        "diagnostic-test-001", null, null, ["diagnostic-test-001"], true);

    [Fact]
    public async Task CollectionReadsOnlyOwnedRetainedStateAndExportsAllowlistedRedactedFields()
    {
        Prepare();
        var state = DevelopmentMissionState.Claimed(mission) with
        {
            State = "needsAttention",
            Iterations = 1,
            ActiveMilliseconds = 42000,
            Reason = "Mission boundary failed. Bearer fixture-secret client_secret=\"fixture-client-secret\" ghp_fixturesecret",
            ProcessedFeedback = [new string('x', 20000)],
            Verification = [new(new('b', 40), mission.Workload.Validate().Verification.Commands[0], 1, new('c', 64), 12)]
        };
        var commands = new FakeWsl(JsonSerializer.Serialize(state, ConfigurationStore.Json));
        var request = Request();
        var before = File.ReadAllText(WorkbenchMissionDiagnostics.SnapshotPath(root));

        await new WorkbenchMissionDiagnostics(root, Installation, commands).ProcessAsync(request, CancellationToken.None);

        Assert.Equal(2, commands.Calls.Count);
        var call = commands.Calls[0];
        Assert.Equal(WslWorkerEnvironment.DistributionName(installationId), call[1]);
        Assert.Equal("worker", call[3]);
        Assert.Equal($"{WslWorkerEnvironment.Workspace}/{mission.Id}/mission-state.json", call[^1]);
        Assert.Contains("readlink -f", call[8]);
        Assert.Contains("head -c 524288", call[8]);
        Assert.DoesNotContain('\r', call[8]);
        Assert.Null(commands.Input);
        Assert.DoesNotContain(call, argument => argument is "--terminate" or "--unregister" or "prepare" or "run" or "--resume");
        Assert.Equal(before, File.ReadAllText(WorkbenchMissionDiagnostics.SnapshotPath(root)));
        var status = LocalState.Read<MissionDiagnosticStatus>(WorkbenchMissionDiagnostics.StatusPath(root));
        Assert.Equal("collected", status.State);
        Assert.Equal(1, status.Mission!.Iterations);
        Assert.Equal(42000, status.Mission.ActiveMilliseconds);
        Assert.Equal(1, Assert.Single(status.Mission.Verification).ExitCode);

        var zip = Path.Combine(Path.GetDirectoryName(root)!, $"{Path.GetFileName(root)}.zip");
        try
        {
            new SetupDiagnostics(root).Export(zip, root, mission: status);
            using var archive = ZipFile.OpenRead(zip);
            using var reader = new StreamReader(archive.GetEntry("mission-diagnostics.json")!.Open());
            var exported = reader.ReadToEnd();
            Assert.Contains("needsAttention", exported);
            Assert.DoesNotContain("fixture-secret", exported);
            Assert.DoesNotContain("fixture-client-secret", exported);
            Assert.DoesNotContain("ghp_fixturesecret", exported);
            Assert.DoesNotContain("fixture-development", exported);
            Assert.DoesNotContain(new string('x', 100), exported);
            Assert.DoesNotContain("\"arguments\"", exported);
            Assert.DoesNotContain(archive.Entries, entry => entry.Name == "config.json");
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public async Task RetainedWorkerTelemetryIsCollectedAndExportedWithoutRawCredentials()
    {
        Prepare();
        var state = DevelopmentMissionState.Claimed(mission) with { State = "needsAttention", Iterations = 1 };
        var entry = new MissionTelemetryEntry("1", mission.Id, mission.Digest, PlantApiTests.Key(), "worker", 1,
            DateTimeOffset.UtcNow, "sdk-session", "failed", 1, 100,
            [new("System.IO.IOException", "Fixture failure ghu_fixturesecret", 1, Code: "fixture_rejected", RpcCode: -32001)]);
        var commands = new FakeWsl(JsonSerializer.Serialize(state, ConfigurationStore.Json))
        {
            TelemetryOutput = JsonSerializer.Serialize(entry, new JsonSerializerOptions(ConfigurationStore.Json) { WriteIndented = false }) + "\n"
        };
        await new WorkbenchMissionDiagnostics(root, Installation, commands).ProcessAsync(Request(), CancellationToken.None);
        var zip = root + ".zip";
        try
        {
            var status = LocalState.Read<MissionDiagnosticStatus>(WorkbenchMissionDiagnostics.StatusPath(root));
            new SetupDiagnostics(root).Export(zip, root, mission: status);
            using var archive = ZipFile.OpenRead(zip);
            using var reader = new StreamReader(archive.GetEntry("mission-telemetry.jsonl")!.Open());
            var exported = reader.ReadToEnd();
            Assert.Contains("sdk-session", exported);
            Assert.Contains("fixture_rejected", exported);
            Assert.Contains("-32001", exported);
            Assert.DoesNotContain("ghu_fixturesecret", exported);
            Assert.DoesNotContain('\r', commands.Calls[1][8]);
        }
        finally { File.Delete(zip); }
    }

    [Theory]
    [InlineData("installation")]
    [InlineData("release")]
    [InlineData("mission")]
    [InlineData("digest")]
    [InlineData("active")]
    [InlineData("ownership")]
    public async Task WrongRequestsAndUnresolvedWorkersNeverInvokeWsl(string failure)
    {
        Prepare();
        var request = Request();
        var commands = new FakeWsl("{}") { Owned = failure != "ownership" };
        request = failure switch
        {
            "installation" => request with { InstallationId = PlantApiTests.Key() },
            "release" => request with { ReleaseId = "different-release" },
            "mission" => request with { MissionId = PlantApiTests.Key() },
            "digest" => request with { MissionDigest = new('f', 64) },
            _ => request
        };
        if (failure == "active")
            File.WriteAllText(Path.Combine(LocalState.RuntimeDirectory(root), "active-development-worker.json"), "retain");

        var error = await Record.ExceptionAsync(() =>
            new WorkbenchMissionDiagnostics(root, Installation, commands).ProcessAsync(request, CancellationToken.None));

        Assert.True(error is InvalidDataException or IOException, error?.ToString());
        Assert.Empty(commands.Calls);
        Assert.Equal("failed", LocalState.Read<MissionDiagnosticStatus>(WorkbenchMissionDiagnostics.StatusPath(root)).State);
        if (failure == "active")
            Assert.Equal("retain", File.ReadAllText(Path.Combine(LocalState.RuntimeDirectory(root), "active-development-worker.json")));
    }

    [Theory]
    [InlineData("unreadable")]
    [InlineData("identity")]
    [InlineData("oversized")]
    public async Task UnreadableOrMismatchedStateProducesExplicitFailureWithoutReplay(string failure)
    {
        Prepare();
        var state = DevelopmentMissionState.Claimed(failure == "identity" ? DevelopmentMissionTests.Mission() : mission);
        var output = failure == "oversized" ? new string('x', WorkbenchMissionDiagnostics.MaximumStateBytes + 1)
            : JsonSerializer.Serialize(state, ConfigurationStore.Json);
        var commands = new FakeWsl(output) { ExitCode = failure == "unreadable" ? 66 : 0 };

        var error = await Record.ExceptionAsync(() =>
            new WorkbenchMissionDiagnostics(root, Installation, commands).ProcessAsync(Request(), CancellationToken.None));

        Assert.True(error is InvalidDataException or IOException, error?.ToString());
        Assert.Single(commands.Calls);
        var status = LocalState.Read<MissionDiagnosticStatus>(WorkbenchMissionDiagnostics.StatusPath(root));
        Assert.Equal("failed", status.State);
        Assert.Null(status.Mission);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetupPreservesServiceIntentAndOnlyStartsDiagnosticModeWhenServiceWasStopped(bool running)
    {
        Prepare();
        var before = File.ReadAllText(Path.Combine(root, "config.json"));
        var installationBefore = File.ReadAllText(Path.Combine(root, "installation.json"));
        var service = new FakeService(running, () =>
        {
            var request = LocalState.Read<MissionDiagnosticRequest>(WorkbenchMissionDiagnostics.RequestPath(root));
            Assert.Equal(!running, request.DiagnosticOnly);
            LocalState.Write(WorkbenchMissionDiagnostics.StatusPath(root),
                new MissionDiagnosticStatus(request.Id, installationId, Installation.ActiveRelease, 123,
                    DateTimeOffset.UtcNow, "collected", "read-only fixture",
                    WorkbenchMissionDiagnostics.Project(DevelopmentMissionState.Claimed(mission), mission)));
        });
        if (running)
        {
            service.OnCheck = () =>
            {
                if (File.Exists(WorkbenchMissionDiagnostics.RequestPath(root)))
                    service.Publish();
            };
        }

        var status = await new WorkbenchMissionDiagnosticSetup(root, service).CollectAsync(CancellationToken.None);

        Assert.Equal("collected", status!.State);
        Assert.Equal(before, File.ReadAllText(Path.Combine(root, "config.json")));
        Assert.Equal(installationBefore, File.ReadAllText(Path.Combine(root, "installation.json")));
        Assert.Equal(running, service.Running);
        Assert.Equal(running ? 0 : 1, service.Starts);
        Assert.Equal(running ? 0 : 1, service.Stops);
        Assert.False(File.Exists(WorkbenchMissionDiagnostics.RequestPath(root)));
    }

    [Fact]
    public async Task EmptyCollectedResultCannotBeReportedAsSuccessfulAndDoesNotChangeServiceIntent()
    {
        Prepare();
        var service = new FakeService(false, () =>
        {
            var request = LocalState.Read<MissionDiagnosticRequest>(WorkbenchMissionDiagnostics.RequestPath(root));
            LocalState.Write(WorkbenchMissionDiagnostics.StatusPath(root), new MissionDiagnosticStatus(request.Id,
                installationId, Installation.ActiveRelease, 123, DateTimeOffset.UtcNow, "collected", "incomplete fixture", null));
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new WorkbenchMissionDiagnosticSetup(root, service).CollectAsync(CancellationToken.None));

        Assert.False(service.Running);
        Assert.False(File.Exists(WorkbenchMissionDiagnostics.RequestPath(root)));
        Assert.True(LocalState.ReadInstallation(root).RunRequested);
    }

    [Fact]
    public async Task DiagnosticHostCannotBypassTheServiceIdentityOrBeginNormalExecution()
    {
        Prepare();
        LocalState.Write(WorkbenchMissionDiagnostics.RequestPath(root), Request() with { DiagnosticOnly = true });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkbenchRuntime.RunAsync(root, Installation.ActiveRelease, true, CancellationToken.None));

        Assert.Contains("dedicated Windows service", error.Message);
        Assert.False(File.Exists(WorkbenchMissionDiagnostics.StatusPath(root)));
        Assert.False(File.Exists(Path.Combine(root, "worker-setup-request.json")));
    }

    private void Prepare()
    {
        LocalState.SaveInstallation(root, Installation);
        ConfigurationStore.Save(root, new()
        {
            SchemaVersion = "1",
            InstallationId = installationId,
            PlantServiceUrl = "https://factory.example",
            TenantId = PlantApiTests.Key(),
            ApiClientId = PlantApiTests.Key(),
            InstallerClientId = PlantApiTests.Key(),
            Distribution = WslWorkerEnvironment.DistributionName(installationId),
            LinuxUser = WslWorkerEnvironment.LinuxUser,
            LinuxWorkspaceRoot = WslWorkerEnvironment.Workspace,
            AllowedRepositories = [],
            AllowShellExecution = false,
            AllowPublication = false
        });
        LocalState.Write(WorkbenchMissionDiagnostics.SnapshotPath(root), new DevelopmentMissionDelivery(mission, "needsAttention", ["reviewer"]));
        var directory = Path.Combine(WslWorkerEnvironment.EnvironmentDirectory(root), "distribution");
        Directory.CreateDirectory(directory);
        LocalState.Write(Path.Combine(WslWorkerEnvironment.EnvironmentDirectory(root), "environment.json"),
            new WorkerDistribution(installationId, WslWorkerEnvironment.DistributionName(installationId), directory));
    }

    private MissionDiagnosticRequest Request() => new(PlantApiTests.Key(), installationId, Installation.ActiveRelease, mission.Id, mission.Digest, false);

    private sealed class FakeWsl(string output) : IWslCommands
    {
        public string TelemetryOutput { get; init; } = "";
        public bool Owned { get; init; } = true;
        public int ExitCode { get; init; }
        public List<string[]> Calls { get; } = [];
        public Stream? Input { get; private set; }
        public bool OwnsDistribution(string name, string directory) => Owned;
        public Task<WslCommandResult> RunAsync(string[] arguments, Stream? input, CancellationToken cancellationToken)
        {
            Calls.Add(arguments);
            Input = input;
            return Task.FromResult(new WslCommandResult(ExitCode,
                arguments[^1].EndsWith("mission-telemetry.jsonl", StringComparison.Ordinal) ? TelemetryOutput : output,
                "Retained state unavailable."));
        }
    }

    private sealed class FakeService(bool running, Action publish) : IWorkbenchService
    {
        public bool Running { get; private set; } = running;
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public Action? OnCheck { get; set; }
        public void Publish() => publish();
        public bool IsRunning(WorkbenchInstallation installation) { OnCheck?.Invoke(); return Running; }
        public Task StartAsync(WorkbenchInstallation installation, CancellationToken cancellationToken)
        {
            Running = true;
            Starts++;
            Publish();
            return Task.CompletedTask;
        }
        public Task StopAsync(WorkbenchInstallation installation, CancellationToken cancellationToken) { Running = false; Stops++; return Task.CompletedTask; }
        public Task ConfigureAsync(string stateDirectory, WorkbenchInstallation installation, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAsync(WorkbenchInstallation installation, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
