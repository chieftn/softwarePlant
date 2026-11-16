using System.Formats.Tar;
using Factory.Contracts;
using Factory.Workbench;
using Xunit;

namespace Factory.Tests;

public sealed class WslWorkerEnvironmentTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"factory-worker-setup-{Guid.NewGuid():N}");
    private readonly string installationId = Guid.NewGuid().ToString("D");
    private WorkbenchInstallation Installation => new("1", installationId, Path.Combine(root, "binaries"), "installed",
        "worker-test-001", null, null, ["worker-test-001"], true);

    [Fact]
    public async Task PreparationCopiesExecutablePayloadChecksSdkAndStopsTheWholeOwnedDistribution()
    {
        var commands = RegisteredEnvironment();
        var payload = Path.Combine(Path.GetDirectoryName(Installation.Executable())!, "worker", "linux-x64");
        Directory.CreateDirectory(payload);
        File.WriteAllBytes(Path.Combine(payload, "Factory.Workbench.Worker"), [127, 69, 76, 70, 1, 2]);
        File.WriteAllText(Path.Combine(payload, "settings.json"), "{}");
        var request = Request("prepare");

        await Environment(commands).ProcessAsync(request, CancellationToken.None);

        var status = LocalState.Read<WorkerSetupStatus>(WslWorkerEnvironment.StatusPath(root));
        Assert.Equal("ready", status.State);
        Assert.Equal(request.Id, status.RequestId);
        Assert.True(commands.WorkerExecutableMode);
        Assert.False(commands.SettingsExecutableMode);
        Assert.True(commands.SdkChecked);
        Assert.False(commands.Running);
        Assert.Contains(commands.Calls, args => args is ["--terminate", _]);
        Assert.All(commands.Calls.Where(args => args[0] == "--distribution"),
            args => Assert.Equal(WslWorkerEnvironment.DistributionName(installationId), args[1]));
        Assert.True(WslWorkerEnvironment.IsReady(root, Configuration(), Installation.ActiveRelease));
        Assert.False(WslWorkerEnvironment.IsReady(root, Configuration(), "different-release"));
        Assert.False(File.Exists(Path.Combine(WslWorkerEnvironment.EnvironmentDirectory(root), "payload.tar")));
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\n")]
    public async Task LinuxShellReceivesPosixLinesRegardlessOfWindowsSourceLineEndings(string newline)
    {
        var commands = RegisteredEnvironment();
        var script = $"export DEBIAN_FRONTEND=noninteractive{newline}apt-get update{newline}printf '%s\\n' 'ready'{newline}";

        await Environment(commands).RunLinuxScriptAsync("root", script, CancellationToken.None);

        var invocation = Assert.Single(commands.Calls);
        Assert.Equal(new[]
        {
            "--distribution", WslWorkerEnvironment.DistributionName(installationId), "--user", "root",
            "--exec", "/bin/sh", "-eu", "-c",
            "export DEBIAN_FRONTEND=noninteractive\napt-get update\nprintf '%s\\n' 'ready'\n"
        }, invocation);
    }

    [Fact]
    public async Task SdkFailureIsReportedAndOwnedLinuxExecutionIsTerminated()
    {
        var commands = RegisteredEnvironment();
        commands.FailSdk = true;
        var request = Request("check");

        await Assert.ThrowsAsync<IOException>(() => Environment(commands).ProcessAsync(request, CancellationToken.None));

        var status = LocalState.Read<WorkerSetupStatus>(WslWorkerEnvironment.StatusPath(root));
        Assert.Equal("failed", status.State);
        Assert.Equal("worker-sdk-check", status.Stage);
        Assert.Contains("SDK probe failed", status.Detail);
        Assert.False(commands.Running);
        Assert.False(WslWorkerEnvironment.IsReady(root, Configuration(), Installation.ActiveRelease));
    }

    [Fact]
    public async Task RegistrationAtAnotherPathCannotBeUsedOrTerminated()
    {
        var commands = RegisteredEnvironment();
        commands.Owned = false;

        await Assert.ThrowsAsync<IOException>(() => Environment(commands).ProcessAsync(Request("check"), CancellationToken.None));

        Assert.False(commands.SdkChecked);
        Assert.DoesNotContain(commands.Calls, args => args[0] is "--terminate" or "--unregister");
    }

    [Fact]
    public async Task OccupiedNameWithoutOwnershipRecordCannotBeAdopted()
    {
        var commands = RegisteredEnvironment();
        File.Delete(Path.Combine(WslWorkerEnvironment.EnvironmentDirectory(root), "environment.json"));

        await Assert.ThrowsAsync<IOException>(() => Environment(commands).ProcessAsync(Request("prepare"), CancellationToken.None));

        Assert.DoesNotContain(commands.Calls, args => args[0] is "--distribution" or "--import" or "--unregister");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalUnregistersOnlyTheOwnedDistributionAndPreservesUnrelatedFiles(bool leaveEmptyDirectory)
    {
        var commands = RegisteredEnvironment();
        commands.LeaveEmptyDirectory = leaveEmptyDirectory;
        var sentinel = Path.Combine(root, "unrelated.txt");
        File.WriteAllText(sentinel, "retain");

        await Environment(commands).ProcessAsync(Request("remove"), CancellationToken.None);

        Assert.Contains(commands.Calls, args => args.SequenceEqual(new[] { "--unregister", WslWorkerEnvironment.DistributionName(installationId) }));
        Assert.Equal("removed", LocalState.Read<WorkerSetupStatus>(WslWorkerEnvironment.StatusPath(root)).State);
        Assert.Equal("retain", File.ReadAllText(sentinel));
        Assert.False(File.Exists(Path.Combine(WslWorkerEnvironment.EnvironmentDirectory(root), "environment.json")));
    }

    [Fact]
    public async Task FailedPrerequisiteIsReportedWithoutAttemptingImportOrSdk()
    {
        var commands = RegisteredEnvironment();
        commands.FailStatus = true;

        await Assert.ThrowsAsync<IOException>(() => Environment(commands).ProcessAsync(Request("prepare"), CancellationToken.None));

        var status = LocalState.Read<WorkerSetupStatus>(WslWorkerEnvironment.StatusPath(root));
        Assert.Equal("service-identity-wsl", status.Stage);
        Assert.Equal("failed", status.State);
        Assert.False(commands.SdkChecked);
        Assert.DoesNotContain(commands.Calls, args => args[0] == "--import");
    }

    [Fact]
    public async Task WrongInstallationRequestCannotRunAnyWslCommands()
    {
        var commands = RegisteredEnvironment();
        await Assert.ThrowsAsync<InvalidDataException>(() => Environment(commands).ProcessAsync(
            Request("prepare") with { InstallationId = Guid.NewGuid().ToString("D") }, CancellationToken.None));
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task ImageWithWrongDigestCannotBeImported()
    {
        var commands = RegisteredEnvironment();
        commands.Registered = false;
        Directory.Delete(Path.Combine(WslWorkerEnvironment.EnvironmentDirectory(root), "distribution"));
        var worker = Path.Combine(Path.GetDirectoryName(Installation.Executable())!, "worker");
        Directory.CreateDirectory(worker);
        File.WriteAllText(Path.Combine(worker, WslWorkerEnvironment.ImageName), "untrusted image");

        await Assert.ThrowsAsync<InvalidDataException>(() => Environment(commands).ProcessAsync(Request("prepare"), CancellationToken.None));

        Assert.DoesNotContain(commands.Calls, args => args[0] == "--import");
        Assert.Equal("failed", LocalState.Read<WorkerSetupStatus>(WslWorkerEnvironment.StatusPath(root)).State);
    }

    [Fact]
    public async Task FailedUnregisterPreservesOwnershipForRecovery()
    {
        var commands = RegisteredEnvironment();
        commands.FailUnregister = true;

        await Assert.ThrowsAsync<IOException>(() => Environment(commands).ProcessAsync(Request("remove"), CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(WslWorkerEnvironment.EnvironmentDirectory(root), "environment.json")));
        Assert.Equal("failed", LocalState.Read<WorkerSetupStatus>(WslWorkerEnvironment.StatusPath(root)).State);
    }

    private WslWorkerEnvironment Environment(FakeWsl commands) => new(root, Installation, commands);
    private WorkerSetupRequest Request(string operation) =>
        new(Guid.NewGuid().ToString("D"), installationId, Installation.ActiveRelease, operation);

    private FakeWsl RegisteredEnvironment()
    {
        var directory = Path.Combine(WslWorkerEnvironment.EnvironmentDirectory(root), "distribution");
        Directory.CreateDirectory(directory);
        LocalState.Write(Path.Combine(WslWorkerEnvironment.EnvironmentDirectory(root), "environment.json"),
            new WorkerDistribution(installationId, WslWorkerEnvironment.DistributionName(installationId), directory));
        return new(WslWorkerEnvironment.DistributionName(installationId), directory);
    }

    private WorkbenchConfiguration Configuration() => new()
    {
        SchemaVersion = "1", InstallationId = installationId, PlantServiceUrl = "https://factory.example",
        TenantId = Guid.NewGuid().ToString("D"), ApiClientId = Guid.NewGuid().ToString("D"),
        InstallerClientId = Guid.NewGuid().ToString("D"),
        Distribution = WslWorkerEnvironment.DistributionName(installationId), LinuxUser = WslWorkerEnvironment.LinuxUser,
        LinuxWorkspaceRoot = WslWorkerEnvironment.Workspace, AllowedRepositories = [], AllowShellExecution = false,
        AllowPublication = false,
        Development = new() { WorkerExecutable = WslWorkerEnvironment.WorkerExecutable(Installation.ActiveRelease) }
    };

    private sealed class FakeWsl(string name, string directory) : IWslCommands
    {
        public List<string[]> Calls { get; } = [];
        public bool Running { get; private set; }
        public bool Owned { get; set; } = true;
        public bool FailSdk { get; set; }
        public bool FailStatus { get; set; }
        public bool FailUnregister { get; set; }
        public bool LeaveEmptyDirectory { get; set; }
        public bool SdkChecked { get; private set; }
        public bool WorkerExecutableMode { get; private set; }
        public bool SettingsExecutableMode { get; private set; }
        public bool Registered { get; set; } = true;
        public bool OwnsDistribution(string selected, string path) => Registered && Owned && selected == name && path == directory;

        public async Task<WslCommandResult> RunAsync(string[] arguments, Stream? input, CancellationToken cancellationToken)
        {
            Calls.Add(arguments);
            if (arguments[0] == "--status")
                return FailStatus ? new(1, "", "WSL service identity unavailable") : new(0, "", "");
            if (arguments[0] == "--list")
                return new(0, Registered && (!arguments.Contains("--running") || Running) ? name : "", "");
            if (arguments[0] == "--terminate")
            {
                Running = false;
                return new(0, "", "");
            }
            if (arguments[0] == "--unregister")
            {
                if (FailUnregister)
                    return new(1, "", "Unregister blocked");
                Registered = Running = false;
                if (!LeaveEmptyDirectory)
                    Directory.Delete(directory);
                return new(0, "", "");
            }
            Assert.Equal("--distribution", arguments[0]);
            Running = true;
            if (input is not null)
            {
                using var reader = new TarReader(input, leaveOpen: true);
                TarEntry? entry;
                while ((entry = await reader.GetNextEntryAsync(cancellationToken: cancellationToken)) is not null)
                {
                    if (entry.Name == "Factory.Workbench.Worker")
                        WorkerExecutableMode = (entry.Mode & UnixFileMode.UserExecute) != 0;
                    if (entry.Name == "settings.json")
                        SettingsExecutableMode = (entry.Mode & UnixFileMode.UserExecute) != 0;
                }
            }
            if (arguments.Contains("check"))
            {
                SdkChecked = true;
                return FailSdk ? new(1, "", "SDK probe failed") : new(0, "{\"ready\":true,\"sdkStarted\":true}", "");
            }
            return new(0, "", "");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
