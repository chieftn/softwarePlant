using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Factory.Contracts;
using Factory.Workbench;
using Factory.Workbench.Host;
using Factory.Workbench.Setup;
using Xunit;

namespace Factory.Tests;

public sealed class WorkbenchLifecycleTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"factory-lifecycle-tests-{Guid.NewGuid():N}");
    private readonly TestService service = new();
    private string Root => Path.Combine(directory, "state");
    private string Binaries => Path.Combine(directory, "binaries");
    private WorkbenchLifecycle Lifecycle => new(Root, service);

    [Theory]
    [InlineData("revoking", "pending")]
    [InlineData("retirementNeedsAttention", "attention")]
    [InlineData("retired", "confirmed")]
    [InlineData("offline", "unconfirmed")]
    public async Task FullUninstallRemovesOwnedStateWithoutWaitingForDirectoryRetirement(string remote, string expected)
    {
        await Install();
        await Lifecycle.StartAsync(CancellationToken.None);
        var journal = Enrollment();
        var diagnostics = new SetupDiagnostics(Root);
        diagnostics.Record("test", "started");
        var certificateRemoved = false;
        var calls = 0;
        var removal = new WorkbenchFullRemoval(Root, service, _ => certificateRemoved = true, diagnostics);
        var transaction = Guid.NewGuid().ToString("D");
        var result = await removal.PrepareAsync(transaction, _ =>
        {
            calls++;
            if (remote == "offline")
                throw new HttpRequestException("Synthetic outage");
            return Task.FromResult(new EnrollmentView("1", journal.OperationId, null,
                $"wb-{Guid.Parse(journal.InstallationId):N}", remote, DateTimeOffset.UtcNow));
        }, CancellationToken.None);
        Assert.Equal(expected, result.RemoteOutcome);
        Assert.Equal(1, calls);
        Assert.False(certificateRemoved);
        Assert.False(service.Running);
        await Assert.ThrowsAsync<InvalidDataException>(() => Lifecycle.StartAsync(CancellationToken.None));
        await Lifecycle.CommitInstallerAsync(transaction, CancellationToken.None);
        removal.Purge();
        Assert.True(certificateRemoved);
        Assert.False(Directory.Exists(Root));
        Assert.False(Directory.Exists(Binaries));
        Configure();
        Assert.NotEqual(journal.InstallationId, ConfigurationStore.Read(Root).InstallationId);
    }

    [Fact]
    public async Task FailedCertificateCleanupRetainsRecoveryStateAndCanBeRetried()
    {
        await Install();
        Enrollment();
        var diagnostics = new SetupDiagnostics(Root);
        var fails = true;
        var removal = new WorkbenchFullRemoval(Root, service, _ =>
        {
            if (fails)
                throw new IOException("Synthetic credential cleanup failure.");
        }, diagnostics);
        var transaction = Guid.NewGuid().ToString("D");
        await removal.PrepareAsync(transaction, null, CancellationToken.None);
        await Lifecycle.CommitInstallerAsync(transaction, CancellationToken.None);
        Assert.Throws<IOException>(removal.Purge);
        Assert.True(File.Exists(Path.Combine(Root, "config.json")));
        Assert.True(File.Exists(Path.Combine(Root, "removal-intent.json")));
        fails = false;
        removal.Purge();
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public async Task FullRemovalRefusesUnownedStateAndDoesNotReactivateAfterRollback()
    {
        await Install();
        await Lifecycle.StartAsync(CancellationToken.None);
        Enrollment();
        File.WriteAllText(Path.Combine(Root, "operator-owned.txt"), "preserve");
        var certificatesRemoved = false;
        var removal = new WorkbenchFullRemoval(Root, service, _ => certificatesRemoved = true, new SetupDiagnostics(Root));
        var transaction = Guid.NewGuid().ToString("D");
        await Assert.ThrowsAsync<InvalidDataException>(() => removal.PrepareAsync(transaction, null, CancellationToken.None));
        await Lifecycle.RollbackInstallerAsync(transaction, CancellationToken.None);
        Assert.False(service.Running);
        Assert.False(LocalState.ReadInstallation(Root).RunRequested);
        Assert.False(certificatesRemoved);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(Root, "operator-owned.txt")));
        Assert.True(File.Exists(Path.Combine(Root, "binding.json")));
    }

    [Fact]
    public async Task FullUninstallCanFinishAfterAnInterruptedMsiUpdate()
    {
        await Install();
        await Lifecycle.ApplyInstallerUpdateAsync(Guid.NewGuid().ToString("D"), Package("second"), CancellationToken.None);
        var removal = new WorkbenchFullRemoval(Root, service, _ => { }, new SetupDiagnostics(Root));
        var transaction = Guid.NewGuid().ToString("D");
        await removal.PrepareAsync(transaction, null, CancellationToken.None);
        await Lifecycle.CommitInstallerAsync(transaction, CancellationToken.None);
        removal.Purge();
        Assert.False(Directory.Exists(Binaries));
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public async Task MsiStagesServiceRemovalBeforeItsBoundedRetirementRequest()
    {
        await Install();
        await Lifecycle.StartAsync(CancellationToken.None);
        var journal = Enrollment();
        var removal = new WorkbenchFullRemoval(Root, service, _ => { }, new SetupDiagnostics(Root));
        var transaction = Guid.NewGuid().ToString("D");
        var calls = 0;
        Task<EnrollmentView> Retire(CancellationToken _)
        {
            calls++;
            Assert.False(service.Running);
            return Task.FromResult(new EnrollmentView("1", journal.OperationId, null,
                $"wb-{Guid.Parse(journal.InstallationId):N}", "revoking", DateTimeOffset.UtcNow));
        }
        await removal.PrepareAsync(transaction, Retire, CancellationToken.None, requestRetirement: false);
        Assert.Equal(0, calls);
        var accepted = await removal.RequestRetirementAsync(Retire, CancellationToken.None);
        await removal.RequestRetirementAsync(Retire, CancellationToken.None);
        Assert.Equal("pending", accepted.RemoteOutcome);
        Assert.Equal(1, calls);
        await Lifecycle.CommitInstallerAsync(transaction, CancellationToken.None);
        removal.Purge();
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public async Task RetirementFailureStopsTheServiceButPreservesCredentialsAndBlocksRestart()
    {
        await Install();
        await Lifecycle.StartAsync(CancellationToken.None);
        var journal = Enrollment();
        var removed = false;
        using var http = new HttpClient(new RetirementResponse(journal, "retirementNeedsAttention")) { BaseAddress = new("https://plant.example") };
        var client = new EnrollmentClient(new PlantHttp(http, _ => Task.FromResult("fixture-token")));
        var removal = new WorkbenchUnenrollment(Root, service, (_, _) => removed = true);
        await Assert.ThrowsAsync<InvalidDataException>(() => removal.RunAsync(client, _ => { }, CancellationToken.None));
        Assert.False(removed);
        Assert.False(service.Running);
        Assert.True(File.Exists(Path.Combine(Root, "binding.json")));
        Assert.Equal(journal, BindingStore.ReadJournal(Root));
        Assert.Equal("requested", BindingStore.Removal(Root)!.State);
        await Assert.ThrowsAsync<InvalidDataException>(() => Lifecycle.StartAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkbenchRuntime.RunAsync(Root, "first", true, CancellationToken.None));
    }

    [Fact]
    public async Task VerifiedRetirementResumesFailedLocalCleanupOfflineThenAllowsFreshEnrollment()
    {
        await Install();
        var journal = Enrollment();
        var config = File.ReadAllText(Path.Combine(Root, "config.json"));
        File.WriteAllText(Path.Combine(Root, "journal.json"), "retain audit evidence");
        using var http = new HttpClient(new RetirementResponse(journal, "retired")) { BaseAddress = new("https://plant.example") };
        var client = new EnrollmentClient(new PlantHttp(http, _ => Task.FromResult("fixture-token")));
        var failures = 1;
        var removed = false;
        var removal = new WorkbenchUnenrollment(Root, service, (root, thumbprint) =>
        {
            Assert.Equal(Root, root);
            Assert.Equal(journal.CertificateThumbprint, thumbprint);
            if (failures-- > 0)
                throw new IOException("Synthetic certificate cleanup interruption.");
            removed = true;
        });
        await Assert.ThrowsAsync<IOException>(() => removal.RunAsync(client, _ => { }, CancellationToken.None));
        Assert.Equal("retired", BindingStore.Removal(Root)!.State);
        Assert.True(File.Exists(Path.Combine(Root, "binding.json")));
        await removal.RunAsync(null, _ => { }, CancellationToken.None);
        Assert.True(removed);
        Assert.True(service.Removed);
        Assert.Equal("uninstalled", LocalState.ReadInstallation(Root).State);
        Assert.False(File.Exists(Path.Combine(Root, "binding.json")));
        Assert.False(File.Exists(Path.Combine(Root, "enrollment.json")));
        Assert.Equal("completed", BindingStore.Removal(Root)!.State);
        Assert.Equal(config, File.ReadAllText(Path.Combine(Root, "config.json")));
        Assert.Equal("retain audit evidence", File.ReadAllText(Path.Combine(Root, "journal.json")));
        using var key = RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=Fresh enrollment fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var next = BindingStore.Journal(Root, certificate);
        Assert.NotEqual(journal.OperationId, next.OperationId);
        Assert.Equal(journal.InstallationId, next.InstallationId);
        await Lifecycle.InstallAsync(Path.Combine(directory, "package-first"), Binaries, true, CancellationToken.None);
    }

    private EnrollmentJournal Enrollment()
    {
        var configuration = ConfigurationStore.Read(Root);
        var journal = new EnrollmentJournal("1", configuration.InstallationId, null, Guid.NewGuid().ToString("D"),
            new string('A', 40), "fixture-device", "AQID");
        LocalState.Write(Path.Combine(Root, "enrollment.json"), journal);
        LocalState.Write(Path.Combine(Root, "binding.json"), new WorkbenchBinding("1", configuration.InstallationId, null,
            $"wb-{Guid.Parse(configuration.InstallationId):N}", configuration.TenantId,
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), journal.CertificateThumbprint, journal.OperationId));
        return journal;
    }

    private sealed class RetirementResponse(EnrollmentJournal journal, string state) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new EnrollmentView("1", journal.OperationId, null,
                    $"wb-{Guid.Parse(journal.InstallationId):N}", state, DateTimeOffset.UtcNow))
            });
    }

    [Fact]
    public async Task RecoveryFinishesInstallerRemovalWithoutLeavingAStaleCommitThatCanRemoveAReinstall()
    {
        await Install();
        var transaction = Guid.NewGuid().ToString();
        await Lifecycle.PrepareInstallerUninstallAsync(transaction, CancellationToken.None);
        await Lifecycle.RecoverAsync(CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(Root, "installer-transaction.json")));
        await Lifecycle.InstallAsync(Path.Combine(directory, "package-first"), Binaries, true, CancellationToken.None);
        await Lifecycle.CommitInstallerAsync(transaction, CancellationToken.None);
        Assert.Equal("installed", LocalState.ReadInstallation(Root).State);
        Assert.True(File.Exists(LocalState.ReadInstallation(Root).Executable()));
    }

    [Fact]
    public async Task RecoveryRollsBackAnUncommittedMsiUpdateInsteadOfKeepingTheNewRuntime()
    {
        await Install();
        var transaction = Guid.NewGuid().ToString();
        await Lifecycle.ApplyInstallerUpdateAsync(transaction, Package("second"), CancellationToken.None);
        await Lifecycle.RecoverAsync(CancellationToken.None);
        Assert.Equal("first", LocalState.ReadInstallation(Root).ActiveRelease);
        Assert.False(File.Exists(Path.Combine(Root, "installer-transaction.json")));
    }

    [Fact]
    public async Task InstallerRejectsDifferentBuildMetadataUnderAnExistingReleaseId()
    {
        await Install();
        var package = Path.Combine(directory, "package-first");
        var manifest = PackageInstaller.ReadManifest(package) with { SourceRevision = "different-source" };
        LocalState.Write(Path.Combine(package, "workbench-package.json"), manifest);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Lifecycle.ApplyInstallerUpdateAsync(Guid.NewGuid().ToString(), package, CancellationToken.None));
        Assert.Equal("first", LocalState.ReadInstallation(Root).ActiveRelease);
    }

    [Fact]
    public async Task InstallerUpdateRollbackAndRetryPreserveIdentityAndPriorRunIntent()
    {
        await Install();
        await Lifecycle.StartAsync(CancellationToken.None);
        var transaction = Guid.NewGuid().ToString();
        var package = Package("second");
        await Lifecycle.ApplyInstallerUpdateAsync(transaction, package, CancellationToken.None);
        Assert.Equal("second", LocalState.ReadInstallation(Root).ActiveRelease);
        await Lifecycle.RollbackInstallerAsync(transaction, CancellationToken.None);
        Assert.Equal("first", LocalState.ReadInstallation(Root).ActiveRelease);
        Assert.True(service.Running);
        await Lifecycle.ApplyInstallerUpdateAsync(transaction, package, CancellationToken.None);
        await Lifecycle.CommitInstallerAsync(transaction, CancellationToken.None);
        Assert.Equal("second", LocalState.ReadInstallation(Root).ActiveRelease);
        Assert.True(service.Running);
        Assert.False(File.Exists(Path.Combine(Root, "installer-transaction.json")));
    }

    [Fact]
    public async Task InstallerUninstallKeepsRuntimeUntilCommitAndCanRestoreServiceOnRollback()
    {
        await Install();
        await Lifecycle.StartAsync(CancellationToken.None);
        var transaction = Guid.NewGuid().ToString();
        var identity = ConfigurationStore.Read(Root).InstallationId;
        await Lifecycle.PrepareInstallerUninstallAsync(transaction, CancellationToken.None);
        Assert.False(service.Running);
        Assert.True(service.Removed);
        Assert.True(File.Exists(Path.Combine(Binaries, "releases", "first", "Factory.Workbench.Host.exe")));
        await Lifecycle.RollbackInstallerAsync(transaction, CancellationToken.None);
        Assert.True(service.Running);
        Assert.Equal("installed", LocalState.ReadInstallation(Root).State);
        await Lifecycle.PrepareInstallerUninstallAsync(transaction, CancellationToken.None);
        await Lifecycle.CommitInstallerAsync(transaction, CancellationToken.None);
        Assert.Equal("uninstalled", LocalState.ReadInstallation(Root).State);
        Assert.Equal(identity, ConfigurationStore.Read(Root).InstallationId);
        Assert.False(File.Exists(Path.Combine(Binaries, "releases", "first", "Factory.Workbench.Host.exe")));
    }

    [Fact]
    public async Task InstallerUpdateHonorsAnIntentionalStopAndFirstInstallDoesNotInventConfiguration()
    {
        await Lifecycle.ApplyInstallerUpdateAsync(Guid.NewGuid().ToString(), Package("bits-only"), CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(Root, "config.json")));
        await Install();
        var transaction = Guid.NewGuid().ToString();
        await Lifecycle.ApplyInstallerUpdateAsync(transaction, Package("second"), CancellationToken.None);
        await Lifecycle.CommitInstallerAsync(transaction, CancellationToken.None);
        Assert.False(service.Running);
        Assert.False(LocalState.ReadInstallation(Root).RunRequested);
    }

    [Fact]
    public async Task InstallRunAndStopPreserveMachineIdentityAndIntent()
    {
        await Install();
        var installed = LocalState.ReadInstallation(Root);
        Assert.Equal("installed", installed.State);
        Assert.False(installed.RunRequested);
        Assert.False(service.Running);
        Assert.Equal(installed.ActiveRelease, service.SelectedRelease);
        await Lifecycle.StartAsync(CancellationToken.None);
        await Lifecycle.StartAsync(CancellationToken.None);
        Assert.True(service.Running);
        Assert.True(LocalState.ReadInstallation(Root).RunRequested);
        await Lifecycle.StopAsync(CancellationToken.None);
        Assert.False(service.Running);
        Assert.False(LocalState.ReadInstallation(Root).RunRequested);
        Assert.Equal(installed.InstallationId, ConfigurationStore.Read(Root).InstallationId);
    }

    [Fact]
    public async Task UpdateAndRollbackSwitchReleasesWithoutChangingConfigurationOrJournals()
    {
        await Install();
        await Lifecycle.StartAsync(CancellationToken.None);
        var config = File.ReadAllText(Path.Combine(Root, "config.json"));
        File.WriteAllText(Path.Combine(Root, "journal.json"), "durable execution evidence");
        await Lifecycle.UpdateAsync(Package("second"), true, CancellationToken.None);
        var updated = LocalState.ReadInstallation(Root);
        Assert.Equal("second", updated.ActiveRelease);
        Assert.Equal("first", updated.PreviousRelease);
        Assert.True(service.Running);
        await Lifecycle.RollbackAsync(CancellationToken.None);
        Assert.Equal("first", LocalState.ReadInstallation(Root).ActiveRelease);
        Assert.Equal("first", service.SelectedRelease);
        Assert.Equal(config, File.ReadAllText(Path.Combine(Root, "config.json")));
        Assert.Equal("durable execution evidence", File.ReadAllText(Path.Combine(Root, "journal.json")));
        Assert.True(File.Exists(Path.Combine(Binaries, "releases", "second", "Factory.Workbench.Host.exe")));
    }

    [Fact]
    public async Task FailedActivationRestoresPreviousSelectionAndRunIntent()
    {
        await Install();
        await Lifecycle.StartAsync(CancellationToken.None);
        service.StartFailures = 1;
        var error = await Assert.ThrowsAsync<IOException>(() =>
            Lifecycle.UpdateAsync(Package("second"), true, CancellationToken.None));
        Assert.Contains("previous selection was restored", error.Message);
        var state = LocalState.ReadInstallation(Root);
        Assert.Equal("installed", state.State);
        Assert.Equal("first", state.ActiveRelease);
        Assert.Null(state.PendingRelease);
        Assert.Equal("first", service.SelectedRelease);
        Assert.True(service.Running);
    }

    [Fact]
    public async Task RecoverRollsBackInterruptedSelectionWithoutExecutingWork()
    {
        await Install();
        await Lifecycle.UpdateAsync(Package("second"), true, CancellationToken.None);
        var state = LocalState.ReadInstallation(Root) with
        {
            State = "updating", ActiveRelease = "first", PreviousRelease = null,
            PendingRelease = "second", RunRequested = true
        };
        LocalState.SaveInstallation(Root, state);
        await Lifecycle.RecoverAsync(CancellationToken.None);
        var recovered = LocalState.ReadInstallation(Root);
        Assert.Equal("installed", recovered.State);
        Assert.Equal("first", recovered.ActiveRelease);
        Assert.Null(recovered.PendingRelease);
        Assert.Equal("first", service.SelectedRelease);
        Assert.True(service.Running);
        Assert.False(File.Exists(Path.Combine(Root, "assignment.json")));
    }

    [Fact]
    public async Task RecoverFinishesInterruptedServiceRegistration()
    {
        Configure();
        service.ConfigureFailures = 1;
        await Assert.ThrowsAsync<IOException>(() => Lifecycle.InstallAsync(Package("first"), Binaries, true, CancellationToken.None));
        Assert.Equal("installing", LocalState.ReadInstallation(Root).State);
        await Lifecycle.RecoverAsync(CancellationToken.None);
        Assert.Equal("installed", LocalState.ReadInstallation(Root).State);
        Assert.False(service.Running);
    }

    [Fact]
    public async Task RecoverBacksOutAnInstallWhosePayloadNeverCompleted()
    {
        Configure();
        var package = Package("first");
        File.WriteAllText(Path.Combine(package, "host", "Factory.Workbench.Host.exe"), "corrupted");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Lifecycle.InstallAsync(package, Binaries, true, CancellationToken.None));
        Assert.Equal("installing", LocalState.ReadInstallation(Root).State);
        await Lifecycle.RecoverAsync(CancellationToken.None);
        Assert.Equal("uninstalled", LocalState.ReadInstallation(Root).State);
        await Lifecycle.InstallAsync(Package("repaired"), Binaries, true, CancellationToken.None);
        Assert.Equal("installed", LocalState.ReadInstallation(Root).State);
    }

    [Fact]
    public async Task FailedActivationAndRestorationRemainExplicitlyRecoverable()
    {
        await Install();
        service.ConfigureFailures = 2;
        await Assert.ThrowsAsync<AggregateException>(() =>
            Lifecycle.UpdateAsync(Package("second"), true, CancellationToken.None));
        Assert.Equal("updating", LocalState.ReadInstallation(Root).State);
        Assert.Equal("first", LocalState.ReadInstallation(Root).ActiveRelease);
        await Lifecycle.RecoverAsync(CancellationToken.None);
        Assert.Equal("installed", LocalState.ReadInstallation(Root).State);
        Assert.Equal("first", service.SelectedRelease);
    }

    [Fact]
    public async Task AnIntentionalStopIsHonoredOnBoot()
    {
        await Install();
        await WorkbenchRuntime.RunAsync(Root, "first", false, CancellationToken.None);
        Assert.False(File.Exists(LocalState.RuntimeStatusPath(Root)));
    }

    [Fact]
    public async Task RecoverFinishesUninstallAndReinstallKeepsIdentityAndEvidence()
    {
        await Install();
        var identity = ConfigurationStore.Read(Root).InstallationId;
        var config = File.ReadAllText(Path.Combine(Root, "config.json"));
        File.WriteAllText(Path.Combine(Root, "journal.json"), "retain this");
        File.WriteAllText(Path.Combine(Binaries, "operator.txt"), "not package-owned");
        service.RemoveFailures = 1;
        await Assert.ThrowsAsync<IOException>(() => Lifecycle.UninstallAsync(CancellationToken.None));
        Assert.Equal("uninstalling", LocalState.ReadInstallation(Root).State);
        await Lifecycle.RecoverAsync(CancellationToken.None);
        await Lifecycle.UninstallAsync(CancellationToken.None);
        Assert.Equal("uninstalled", LocalState.ReadInstallation(Root).State);
        Assert.False(Directory.Exists(Path.Combine(Binaries, "releases", "first")));
        Assert.Equal(config, File.ReadAllText(Path.Combine(Root, "config.json")));
        Assert.Equal("retain this", File.ReadAllText(Path.Combine(Root, "journal.json")));
        Assert.Equal("not package-owned", File.ReadAllText(Path.Combine(Binaries, "operator.txt")));
        await Lifecycle.InstallAsync(Path.Combine(directory, "package-first"), Binaries, true, CancellationToken.None);
        Assert.Equal(identity, LocalState.ReadInstallation(Root).InstallationId);
    }

    [Fact]
    public async Task LiveRuntimeBlocksMaintenanceAndReportsNotReadyWithoutWsl()
    {
        await Install();
        using var cancellation = new CancellationTokenSource();
        var runtime = WorkbenchRuntime.RunAsync(Root, "first", true, cancellation.Token);
        try
        {
            var status = LocalState.Read<WorkbenchRuntimeStatus>(LocalState.RuntimeStatusPath(Root));
            Assert.Equal("notReady", status.State);
            Assert.Contains("WSL is not started", status.Reason);
            await Assert.ThrowsAsync<IOException>(() => WorkbenchRuntime.RunAsync(Root, "first", true, cancellation.Token));
            await Assert.ThrowsAsync<IOException>(() => Lifecycle.UpdateAsync(Package("second"), true, CancellationToken.None));
            Assert.Equal("first", LocalState.ReadInstallation(Root).ActiveRelease);
            Assert.Throws<IOException>(() => WorkbenchConfigurator.Configure(Root, new StringReader(""), new StringWriter()));
        }
        finally
        {
            cancellation.Cancel();
            await runtime;
        }
        Assert.Equal("stopped", LocalState.Read<WorkbenchRuntimeStatus>(LocalState.RuntimeStatusPath(Root)).State);
    }

    [Fact]
    public async Task StartupRejectsPendingMaintenanceAndUnselectedReleases()
    {
        await Install();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            WorkbenchRuntime.RunAsync(Root, "other-release", true, CancellationToken.None));
        var state = LocalState.ReadInstallation(Root) with { State = "updating", PendingRelease = "second" };
        LocalState.SaveInstallation(Root, state);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            WorkbenchRuntime.RunAsync(Root, "first", true, CancellationToken.None));
    }

    [Fact]
    public async Task OwnershipMismatchPreventsRemoval()
    {
        await Install();
        File.WriteAllText(Path.Combine(Binaries, "installation.id"), Guid.NewGuid().ToString("D"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Lifecycle.UninstallAsync(CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(Binaries, "releases", "first", "Factory.Workbench.Host.exe")));
        Assert.False(service.Removed);
    }

    private async Task Install()
    {
        Configure();
        await Lifecycle.InstallAsync(Package("first"), Binaries, true, CancellationToken.None);
    }

    private void Configure() => ConfigurationStore.Save(Root, new WorkbenchConfiguration
    {
        SchemaVersion = "1", InstallationId = Guid.NewGuid().ToString("D"),
        PlantServiceUrl = "https://plant.example",
        TenantId = "00000000-0000-0000-0000-000000000001",
        ApiClientId = "00000000-0000-0000-0000-000000000002",
        InstallerClientId = "00000000-0000-0000-0000-000000000003",
        Distribution = "Ubuntu", LinuxUser = "worker", LinuxWorkspaceRoot = "/home/worker/work",
        AllowedRepositories = [], AllowShellExecution = false, AllowPublication = false
    });

    private string Package(string release)
    {
        var package = Path.Combine(directory, $"package-{release}");
        Directory.CreateDirectory(Path.Combine(package, "host"));
        var content = Encoding.UTF8.GetBytes($"host fixture {release}");
        File.WriteAllBytes(Path.Combine(package, "host", "Factory.Workbench.Host.exe"), content);
        var manifest = new WorkbenchPackage("1", release, "win-x64", "local-test",
            new() { ["Factory.Workbench.Host.exe"] = Convert.ToHexString(SHA256.HashData(content)) });
        File.WriteAllText(Path.Combine(package, "workbench-package.json"),
            JsonSerializer.Serialize(manifest, ConfigurationStore.Json));
        return package;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class TestService : IWorkbenchService
    {
        public bool Running { get; private set; }
        public bool Removed { get; private set; }
        public string? SelectedRelease { get; private set; }
        public int ConfigureFailures { get; set; }
        public int StartFailures { get; set; }
        public int RemoveFailures { get; set; }

        public Task ConfigureAsync(string stateDirectory, WorkbenchInstallation installation, CancellationToken cancellationToken)
        {
            if (ConfigureFailures-- > 0)
                throw new IOException("Synthetic service configuration failure.");
            SelectedRelease = installation.PendingRelease ?? installation.ActiveRelease;
            Removed = false;
            return Task.CompletedTask;
        }

        public Task StartAsync(WorkbenchInstallation installation, CancellationToken cancellationToken)
        {
            if (StartFailures-- > 0)
                throw new IOException("Synthetic start failure.");
            Running = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(WorkbenchInstallation installation, CancellationToken cancellationToken)
        {
            Running = false;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(WorkbenchInstallation installation, CancellationToken cancellationToken)
        {
            if (RemoveFailures-- > 0)
                throw new IOException("Synthetic removal failure.");
            Removed = true;
            return Task.CompletedTask;
        }

        public bool IsRunning(WorkbenchInstallation installation) => Running;
    }
}
