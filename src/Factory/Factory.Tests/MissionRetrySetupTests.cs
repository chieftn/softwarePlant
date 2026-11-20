using Factory.Contracts;
using Factory.Workbench;
using Factory.Workbench.Setup;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Factory.Tests;

public sealed class MissionRetrySetupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"factory-retry-request-{Guid.NewGuid():N}");
    private readonly string installationId = PlantApiTests.Key();
    private readonly DevelopmentMission mission = DevelopmentMissionTests.Mission();
    private const string Release = "fixture-telemetry-001";

    [Theory]
    [InlineData("started")]
    [InlineData("failed")]
    public async Task AcknowledgementLeavesRequestCleanupToTheHost(string state)
    {
        Prepare();
        var service = new FakeService(() =>
        {
            if (File.Exists(MissionRetry.RequestPath(root)))
            {
                var request = LocalState.Read<MissionRetryRequest>(MissionRetry.RequestPath(root));
                LocalState.Write(MissionRetry.StatusPath(root),
                    new MissionRetryStatus(request.Id, installationId, Release, state, "Fixture acknowledgement."));
            }
        });
        var setup = new WorkbenchMissionRetrySetup(root, service);
        if (state == "started")
            Assert.Equal(state, (await setup.RequestAsync(CancellationToken.None)).State);
        else
            await Assert.ThrowsAsync<IOException>(() => setup.RequestAsync(CancellationToken.None));

        Assert.True(File.Exists(MissionRetry.RequestPath(root)));
        var retained = LocalState.Read<MissionRetryRequest>(MissionRetry.RequestPath(root));
        Assert.Equal(retained.Id, LocalState.Read<MissionRetryStatus>(MissionRetry.StatusPath(root)).RequestId);
        File.Delete(MissionRetry.RequestPath(root));
    }

    [Fact]
    public async Task ServiceCanConsumeAcknowledgedRequestWithoutWriteAccessToInstallationState()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Prepare();
        var legacyPath = Path.Combine(root, "mission-retry-request.json");
        if (MissionRetry.RequestPath(root) != legacyPath)
            File.WriteAllText(legacyPath, "old request retained as evidence");
        var service = new FakeService(() =>
        {
            if (File.Exists(MissionRetry.RequestPath(root)))
            {
                var request = LocalState.Read<MissionRetryRequest>(MissionRetry.RequestPath(root));
                LocalState.Write(MissionRetry.StatusPath(root),
                    new MissionRetryStatus(request.Id, installationId, Release, "started", "Fixture acknowledgement."));
            }
        });
        var accepted = await new WorkbenchMissionRetrySetup(root, service).RequestAsync(CancellationToken.None);
        VerifyRestrictedCleanup(accepted.RequestId, legacyPath);
    }

    [SupportedOSPlatform("windows")]
    private void VerifyRestrictedCleanup(string requestId, string legacyPath)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("The test requires a Windows identity.");
        var restricted = new SecurityIdentifier("S-1-5-21-101-202-303-404");
        var sid = new byte[restricted.BinaryLength];
        restricted.GetBinaryForm(sid, 0);
        var pin = GCHandle.Alloc(sid, GCHandleType.Pinned);
        try
        {
            DirectorySecurity Access(FileSystemRights rights)
            {
                var acl = new DirectorySecurity();
                acl.SetAccessRuleProtection(true, false);
                acl.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                acl.AddAccessRule(new FileSystemAccessRule(restricted, rights,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                return acl;
            }
            new DirectoryInfo(root).SetAccessControl(Access(FileSystemRights.ReadAndExecute));
            new DirectoryInfo(LocalState.RuntimeDirectory(root)).SetAccessControl(Access(FileSystemRights.Modify));
            var restriction = new SidAndAttributes { Sid = pin.AddrOfPinnedObject() };
            if (!CreateRestrictedToken(identity.AccessToken, 1, 0, IntPtr.Zero, 0, IntPtr.Zero,
                1, [restriction], out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
                WindowsIdentity.RunImpersonated(token, () =>
                {
                    Assert.Equal(requestId, LocalState.Read<MissionRetryRequest>(MissionRetry.RequestPath(root)).Id);
                    File.Delete(MissionRetry.RequestPath(root));
                    Assert.False(File.Exists(MissionRetry.RequestPath(root)));
                    Assert.Throws<UnauthorizedAccessException>(() => File.Delete(Path.Combine(root, "installation.json")));
                });
            Assert.Equal("old request retained as evidence", File.ReadAllText(legacyPath));
        }
        finally
        {
            pin.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existingToken, uint flags,
        uint disableSidCount, IntPtr sidsToDisable, uint deletePrivilegeCount, IntPtr privilegesToDelete,
        uint restrictSidCount, [In] SidAndAttributes[] sidsToRestrict, out SafeAccessTokenHandle newToken);

    [Fact]
    public async Task CancellationBeforeSubmissionDoesNotQueueAnOperation()
    {
        Prepare();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WorkbenchMissionRetrySetup(root, new FakeService(() => { })).RequestAsync(cancellation.Token));
        Assert.False(File.Exists(MissionRetry.RequestPath(root)));
    }

    [Theory]
    [InlineData("processing")]
    [InlineData("started")]
    [InlineData("failed")]
    public async Task APreviouslyHandledRequestCannotRestartAfterHostRecovery(string state)
    {
        Prepare(Environment.ProcessId);
        var observed = mission.Workload.Configuration;
        var definition = observed.ProductionLine!.Definition;
        observed = observed with { ProductionLine = observed.ProductionLine with { Definition = definition with { Execution = null } } };
        var configuration = ConfigurationStore.Read(root) with
        {
            AllowedRepositories = [definition.Source.Repository, definition.Tasks.Repository],
            Development = new() { Enabled = true, WorkerExecutable = WslWorkerEnvironment.WorkerExecutable(Release) }
        };
        var binding = new WorkbenchBinding("1", installationId, null, observed.WorkbenchId,
            ApiTestApplication.Tenant, ApiTestApplication.DaemonClientId, ApiTestApplication.DaemonId, new('A', 40), observed.EnrollmentId);
        var request = new MissionRetryRequest(PlantApiTests.Key(), installationId, Release, mission.Id, mission.Digest);
        LocalState.Write(MissionRetry.RequestPath(root), request);
        LocalState.Write(MissionRetry.StatusPath(root), new MissionRetryStatus(request.Id, installationId, Release, state, "Previously handled."));
        using var http = new HttpClient();
        var plant = new PlantHttp(http, _ => throw new InvalidOperationException("A handled retry must not contact Factory or start a worker."));
        var execution = new WorkbenchDevelopmentExecution(plant, binding, configuration, root);
        await execution.ObserveAsync(observed, CancellationToken.None);
        await execution.TickAsync(CancellationToken.None);

        Assert.Contains("no automatic replay", execution.Reason);
        Assert.Equal("failed", LocalState.Read<MissionRetryStatus>(MissionRetry.StatusPath(root)).State);
        Assert.False(File.Exists(MissionRetry.RequestPath(root)));
        Assert.False(File.Exists(Path.Combine(LocalState.RuntimeDirectory(root), "active-development-worker.json")));
    }

    [Fact]
    public async Task CancellingAcknowledgementDoesNotDeleteOrResubmitTheQueuedRequest()
    {
        Prepare();
        using var cancellation = new CancellationTokenSource();
        var service = new FakeService(() =>
        {
            if (File.Exists(MissionRetry.RequestPath(root)))
                cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WorkbenchMissionRetrySetup(root, service).RequestAsync(cancellation.Token));

        var request = LocalState.Read<MissionRetryRequest>(MissionRetry.RequestPath(root));
        var error = await Assert.ThrowsAsync<IOException>(() =>
            new WorkbenchMissionRetrySetup(root, new FakeService(() => { })).RequestAsync(CancellationToken.None));
        Assert.Contains("already queued", error.Message);
        Assert.Equal(request, LocalState.Read<MissionRetryRequest>(MissionRetry.RequestPath(root)));
    }

    private void Prepare(int? processId = null)
    {
        LocalState.SaveInstallation(root, new("1", installationId, Path.Combine(root, "binaries"), "installed",
            Release, null, null, [Release], true));
        var serviceProcess = processId ?? Environment.ProcessId + 1000;
        ConfigurationStore.Save(root, new()
        {
            SchemaVersion = "1",
            InstallationId = installationId,
            PlantServiceUrl = "https://fixture.invalid",
            TenantId = PlantApiTests.Key(),
            ApiClientId = PlantApiTests.Key(),
            InstallerClientId = PlantApiTests.Key(),
            AllowedRepositories = [],
            AllowShellExecution = true,
            AllowPublication = true,
            Distribution = WslWorkerEnvironment.DistributionName(installationId),
            LinuxUser = WslWorkerEnvironment.LinuxUser,
            LinuxWorkspaceRoot = WslWorkerEnvironment.Workspace,
            Development = new() { WorkerExecutable = WslWorkerEnvironment.WorkerExecutable(Release) }
        });
        LocalState.Write(LocalState.RuntimeStatusPath(root),
            new WorkbenchRuntimeStatus("1", installationId, Release, serviceProcess, DateTimeOffset.UtcNow, "notReady", "Fixture."));
        LocalState.Write(WslWorkerEnvironment.StatusPath(root), new WorkerSetupStatus(PlantApiTests.Key(),
            installationId, Release, serviceProcess, DateTimeOffset.UtcNow, "ready", "worker-check", "Fixture ready."));
        LocalState.Write(WorkbenchMissionDiagnostics.SnapshotPath(root), new DevelopmentMissionDelivery(mission, "needsAttention", ["reviewer"]));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class FakeService(Action check) : IWorkbenchService
    {
        public bool IsRunning(WorkbenchInstallation installation) { check(); return true; }
        public Task StartAsync(WorkbenchInstallation installation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(WorkbenchInstallation installation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ConfigureAsync(string stateDirectory, WorkbenchInstallation installation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAsync(WorkbenchInstallation installation, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
