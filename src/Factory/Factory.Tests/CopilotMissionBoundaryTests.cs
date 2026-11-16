using Factory.Contracts;
using Factory.Workbench;
using Factory.Workbench.Worker;
using GitHub.Copilot;
using System.Text.Json;
using Xunit;

namespace Factory.Tests;

public sealed class CopilotMissionBoundaryTests
{
#pragma warning disable GHCP001
    [Fact]
    public async Task SdkTokenCallbackObtainsFreshMissionCredentialsWithoutAmbientFallback()
    {
        var clock = new TestClock();
        var calls = 0;
        Task<ModelAccessCredential> Acquire(CancellationToken cancellationToken) => Task.FromResult(
            new ModelAccessCredential("github-copilot", "test-model", "ghu_fixture-" + ++calls, clock.Now.AddHours(8)));
        var first = await CopilotModelAuthentication.TokenAsync("github.com", "test-model", Acquire, clock, CancellationToken.None);
        var refreshed = await CopilotModelAuthentication.TokenAsync("github.com", "test-model", Acquire, clock, CancellationToken.None);
        Assert.NotEqual(first.Token!.AccessToken, refreshed.Token!.AccessToken);
        Assert.Equal(28800, refreshed.Token.ExpiresIn);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CopilotModelAuthentication.TokenAsync("other.example", "test-model", Acquire, clock, CancellationToken.None));
        Assert.Equal(2, calls);
        await Assert.ThrowsAsync<ContractValidationException>(() =>
            CopilotModelAuthentication.TokenAsync("github.com", "other-model", Acquire, clock, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => CopilotModelAuthentication.TokenAsync("github.com", "test-model",
            _ => throw new IOException("Fixture credentials unavailable."), clock, CancellationToken.None));
    }
#pragma warning restore GHCP001

    [Fact]
    public void ImplementationAndAlignmentReceiveDifferentContainedFilePermissions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"factory-sdk-boundary-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "repository");
        var read = new PermissionRequestRead { Path = Path.Combine(workspace, "code.cs"), Intention = "Read candidate" };
        var write = new PermissionRequestWrite
        {
            FileName = Path.Combine(workspace, "code.cs"),
            Intention = "Edit candidate",
            CanOfferSessionApproval = false,
            Diff = ""
        };
        Assert.True(MissionPermissionPolicy.Allows(read, workspace, false));
        Assert.True(MissionPermissionPolicy.Allows(write, workspace, true));
        Assert.False(MissionPermissionPolicy.Allows(write, workspace, false));
        Assert.False(MissionPermissionPolicy.Allows(new PermissionRequestRead
        {
            Path = Path.Combine(workspace, ".git", "config"),
            Intention = "Read metadata"
        }, workspace, true));
        Assert.False(MissionPermissionPolicy.Allows(new PermissionRequestRead
        {
            Path = Path.Combine(root, "outside"),
            Intention = "Read outside"
        }, workspace, true));
        Assert.False(MissionPermissionPolicy.Allows(new PermissionRequestRead
        {
            Path = "code.cs",
            Intention = "Read",
            ManagedApprovalRequired = true
        }, workspace, true));
    }

    [Fact]
    public async Task DisabledSdkAdapterCannotStartRuntimeOrCreateSessionState()
    {
        var root = Path.Combine(Path.GetTempPath(), $"factory-disabled-sdk-{Guid.NewGuid():N}");
        var agent = new CopilotMissionAgent(Path.Combine(root, "repository"), Path.Combine(root, "sessions"), "test-model",
            _ => throw new InvalidOperationException("Disabled execution must not request model credentials."), false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.ImplementAsync(DevelopmentMissionTests.Mission(), null, [], CancellationToken.None));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task DisabledMissionControllerDoesNotAcquireCredentialsOrCreateWorkerState()
    {
        var root = Path.Combine(Path.GetTempPath(), $"factory-disabled-controller-{Guid.NewGuid():N}");
        var current = RepositoryWorkloadTests.Configuration();
        var configuration = new WorkbenchConfiguration
        {
            SchemaVersion = "1",
            InstallationId = PlantApiTests.Key(),
            PlantServiceUrl = "https://plant.example",
            TenantId = ApiTestApplication.Tenant,
            ApiClientId = ApiTestApplication.Audience,
            InstallerClientId = ApiTestApplication.Audience,
            Distribution = "Ubuntu",
            LinuxUser = "worker",
            LinuxWorkspaceRoot = "/home/worker/work",
            AllowedRepositories = [],
            AllowShellExecution = false,
            AllowPublication = false
        };
        var binding = new WorkbenchBinding("1", configuration.InstallationId, null, current.WorkbenchId,
            ApiTestApplication.Tenant, ApiTestApplication.DaemonClientId, ApiTestApplication.DaemonId, new('A', 40), current.EnrollmentId);
        using var http = new HttpClient();
        var plant = new PlantHttp(http, _ => throw new InvalidOperationException("Disabled execution must not request a token."));
        var execution = new WorkbenchDevelopmentExecution(plant, binding, configuration, root);
        await execution.ObserveAsync(current, CancellationToken.None);
        await execution.TickAsync(CancellationToken.None);
        Assert.Equal("notReady", execution.State);
        Assert.False(Directory.Exists(root));
    }
    [Fact]
    public void ShellRequestsAreDeniedForBothRoles()
    {
        var shell = new PermissionRequestShell
        {
            CanOfferSessionApproval = false,
            Commands = [],
            FullCommandText = "git status",
            HasWriteFileRedirection = false,
            Intention = "Inspect code",
            PossiblePaths = [],
            PossibleUrls = []
        };
        Assert.False(MissionPermissionPolicy.Allows(shell, Path.GetTempPath(), true));
        Assert.False(MissionPermissionPolicy.Allows(shell, Path.GetTempPath(), false));
    }
    [Fact]
    public void WorkerInvocationUsesArgumentArraysAndDoesNotPlaceCredentialsOnCommandLine()
    {
        var configuration = new WorkbenchConfiguration
        {
            SchemaVersion = "1",
            InstallationId = PlantApiTests.Key(),
            PlantServiceUrl = "https://plant.example",
            TenantId = ApiTestApplication.Tenant,
            ApiClientId = ApiTestApplication.Audience,
            InstallerClientId = ApiTestApplication.Audience,
            Distribution = "Ubuntu",
            LinuxUser = "worker",
            LinuxWorkspaceRoot = "/home/worker/work",
            AllowedRepositories = ["product/source", "product/tasks"],
            AllowShellExecution = true,
            AllowPublication = true,
            Development = new()
            {
                Enabled = true,
                AssignedIssueNumber = 7,
                WorkerExecutable = "/opt/factory/worker",
                Model = "test-model",
                GitAuthorName = "Factory",
                GitAuthorEmail = "factory@example.invalid"
            }
        };
        configuration.Validate();
        var invocation = WslMissionWorker.Invocation(configuration, PlantApiTests.Key(), new('A', 64), false);
        Assert.Equal("wsl.exe", invocation.FileName);
        Assert.Contains("/usr/bin/setsid", invocation.ArgumentList);
        Assert.Contains("/opt/factory/worker", invocation.ArgumentList);
        Assert.DoesNotContain(invocation.ArgumentList, argument => argument.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.False(invocation.UseShellExecute);
    }

    [Fact]
    public void DuplicateAlignmentFieldsCannotManufacturePass()
    {
        Assert.Throws<ContractValidationException>(() => RepositoryWorkload.ParseJson<CandidateAlignment>(
            """{"candidateCommit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","outcome":"blocked","outcome":"pass","coverage":[],"findings":[],"blockers":[]}"""));
    }

    [Fact]
    public async Task PipeAcceptsMissingPublicationAndRejectsRequestsAfterDisconnect()
    {
        WorkerPipeMessage? emitted = null;
        var pipe = new WorkerPlantPipe(message => emitted = message);
        var published = pipe.PublishedAsync(new('b', 40), CancellationToken.None);
        var request = Assert.IsType<WorkerPipeMessage>(emitted);
        pipe.Receive(new("response", request.Id, request.Operation, JsonSerializer.SerializeToElement<object?>(null)));
        Assert.Null(await published);
        emitted = null;
        pipe.Disconnect();
        await Assert.ThrowsAsync<IOException>(() => pipe.CredentialAsync(false, CancellationToken.None));
        Assert.Null(emitted);
    }

    [Fact]
    public void ClaimedMissionAndUnstartedWorkerJournalRoundTripWithoutLosingNullableState()
    {
        var root = Path.Combine(Path.GetTempPath(), $"factory-mission-journal-{Guid.NewGuid():N}");
        try
        {
            var mission = DevelopmentMissionTests.Mission();
            var statePath = Path.Combine(root, "mission-state.json");
            LocalState.Write(statePath, DevelopmentMissionState.Claimed(mission), 512 * 1024);
            var restored = LocalState.Read<DevelopmentMissionState>(statePath, 512 * 1024);
            restored.Mission.Validate();
            Assert.Null(restored.CandidateCommit);
            Assert.Null(restored.Alignment);
            Assert.Null(restored.PullRequest);
            var journalPath = Path.Combine(root, "worker.json");
            LocalState.Write(journalPath, new WslWorkerJournal(mission.Id, mission.Digest, PlantApiTests.Key(),
                new('A', 64), null, "Ubuntu", "worker", "/home/worker/work", "/opt/factory/worker"));
            Assert.Null(LocalState.Read<WslWorkerJournal>(journalPath).Process);
            var progress = new DevelopmentMissionProgress(mission.Id, mission.Digest, "claimed", 0, 0,
                null, null, false, "Claimed.");
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(progress, RepositoryWorkload.Json));
            Assert.False(document.RootElement.TryGetProperty("candidateCommit", out _));
            Assert.False(document.RootElement.TryGetProperty("pullRequest", out _));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
