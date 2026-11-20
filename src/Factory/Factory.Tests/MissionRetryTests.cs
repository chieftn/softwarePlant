using Factory.Contracts;
using Factory.Workbench;
using Factory.Workbench.Worker;
using Xunit;

namespace Factory.Tests;

public sealed class MissionRetryTests
{
    [Fact]
    public void ExplicitRetryPreservesTheFrozenMissionAndConsumesTheOriginalRemainingBudget()
    {
        var mission = DevelopmentMissionTests.Mission();
        var failed = DevelopmentMissionState.Claimed(mission) with
        { State = "needsAttention", Iterations = 1, ActiveMilliseconds = 5688 };
        var retry = MissionRetry.Prepare(failed, mission);
        Assert.Equal("claimed", retry.State);
        Assert.Same(mission, retry.Mission);
        Assert.Equal(1, retry.Iterations);
        Assert.Equal(5688, retry.ActiveMilliseconds);
        Assert.Equal("needsAttention", failed.State);
        Assert.Contains("original budget preserved", retry.Reason);
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("publication")]
    [InlineData("alignment")]
    [InlineData("state")]
    [InlineData("iterations")]
    [InlineData("time")]
    [InlineData("identity")]
    public void RecordedEffectsOrExhaustedAuthorityCannotBeReplayed(string failure)
    {
        var mission = DevelopmentMissionTests.Mission();
        var state = DevelopmentMissionState.Claimed(mission) with { State = "needsAttention", Iterations = 1 };
        state = failure switch
        {
            "candidate" => state with { CandidateCommit = new('b', 40) },
            "publication" => state with { PublicationPending = true },
            "alignment" => state with { Alignment = new(new('b', 40), "pass", [], [], []) },
            "state" => state with { State = "executing" },
            "iterations" => state with { Iterations = 3 },
            "time" => state with { ActiveMilliseconds = 30 * 60 * 1000 },
            _ => state
        };
        Assert.Throws<InvalidDataException>(() => MissionRetry.Prepare(state,
            failure == "identity" ? DevelopmentMissionTests.Mission() : mission));
    }

    [Fact]
    public void RetryInvocationIsExplicitAndDoesNotUseAutomaticResume()
    {
        var configuration = new WorkbenchConfiguration
        {
            SchemaVersion = "1",
            InstallationId = PlantApiTests.Key(),
            PlantServiceUrl = "https://fixture.invalid",
            TenantId = PlantApiTests.Key(),
            ApiClientId = PlantApiTests.Key(),
            InstallerClientId = PlantApiTests.Key(),
            AllowedRepositories = [],
            AllowShellExecution = true,
            AllowPublication = true,
            Distribution = "fixture",
            LinuxUser = "worker",
            LinuxWorkspaceRoot = "/fixture",
            Development = new() { WorkerExecutable = "/fixture/worker" }
        };
        var invocation = WslMissionWorker.Invocation(configuration, PlantApiTests.Key(), new('A', 64), true, true);
        Assert.Contains("--retry", invocation.ArgumentList);
        Assert.DoesNotContain("--resume", invocation.ArgumentList);
    }

    [Fact]
    public async Task NewAttemptIsRegisteredOnlyWhenItsCheckoutExistsAndBeforePreparationCanFail()
    {
        var root = Path.Combine(Path.GetTempPath(), $"factory-attempt-registration-{Guid.NewGuid():N}");
        var instance = PlantApiTests.Key();
        var directory = Path.Combine(root, "attempts", instance);
        var repository = new GitMissionRepository(DevelopmentMissionTests.Mission(), directory, "Factory", "factory@example.invalid",
            new UnusedGateway(), new(), preparationStarted: () =>
            {
                LocalState.Write(Path.Combine(root, "attempt.json"), new MissionAttempt(instance));
                throw new IOException("Synthetic interruption before any Git command.");
            });
        try
        {
            Assert.Equal(root, MissionRetry.AttemptDirectory(root));
            await Assert.ThrowsAsync<IOException>(() => repository.PrepareAsync(DevelopmentMissionTests.Mission(), CancellationToken.None));
            Assert.Equal(directory, MissionRetry.AttemptDirectory(root));
            Assert.True(Directory.Exists(repository.Checkout));
            Assert.False(Directory.Exists(Path.Combine(repository.Checkout, ".git")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AnAcknowledgedRetryInterruptedBeforePreparationRetainsItsOriginalCheckout()
    {
        CheckInterruptedAttempt(null);
    }

    [Theory]
    [InlineData("iteration")]
    [InlineData("time")]
    [InlineData("preparation")]
    [InlineData("other-attempt")]
    public void MissingRetryCheckoutCannotHideConsumedExecutionOrAnotherAttempt(string failure)
    {
        CheckInterruptedAttempt(failure);
    }

    private static void CheckInterruptedAttempt(string? failure)
    {
        var root = Path.Combine(Path.GetTempPath(), $"factory-interrupted-retry-{Guid.NewGuid():N}");
        var mission = DevelopmentMissionTests.Mission();
        var state = DevelopmentMissionState.Claimed(mission) with
        { State = "needsAttention", Iterations = 1, ActiveMilliseconds = 5688 };
        var instance = PlantApiTests.Key();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "repository"));
            LocalState.Write(Path.Combine(root, "attempt.json"), new MissionAttempt(instance));
            LocalState.Write(Path.Combine(root, $"mission-state-before-{instance}.json"), state, 512 * 1024);
            var telemetry = new MissionTelemetry(Path.Combine(root, "mission-telemetry.jsonl"),
                mission.Id, mission.Digest, instance, "worker");
            telemetry.Record("worker", "started");
            telemetry.Record("retry-integrity", "completed", state.Iterations);
            if (failure == "preparation")
                telemetry.Record("preparation", "started", state.Iterations);
            if (failure == "other-attempt")
                Directory.CreateDirectory(Path.Combine(root, "attempts", PlantApiTests.Key()));
            var current = failure switch
            {
                "iteration" => state with { Iterations = 2 },
                "time" => state with { ActiveMilliseconds = 6000 },
                _ => state
            };
            if (failure is null)
            {
                Assert.Equal(root, MissionRetry.RetainedAttemptDirectory(root, current));
                Assert.Equal(instance, LocalState.Read<MissionAttempt>(Path.Combine(root, "attempt.json")).InstanceId);
                Assert.True(Directory.Exists(Path.Combine(root, "repository")));
            }
            else
                Assert.Throws<InvalidDataException>(() => MissionRetry.RetainedAttemptDirectory(root, current));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SetupChecksReadinessAgainstTheServiceProcessRatherThanItsOwnProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), $"factory-retry-readiness-{Guid.NewGuid():N}");
        var installationId = PlantApiTests.Key();
        const string release = "fixture-telemetry-001";
        var configuration = new WorkbenchConfiguration
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
            Development = new() { WorkerExecutable = WslWorkerEnvironment.WorkerExecutable(release) }
        };
        try
        {
            var serviceProcess = Environment.ProcessId + 1000;
            LocalState.Write(WslWorkerEnvironment.StatusPath(root), new WorkerSetupStatus(PlantApiTests.Key(),
                installationId, release, serviceProcess, DateTimeOffset.UtcNow, "ready", "worker-check", "Fixture ready."));
            Assert.False(WslWorkerEnvironment.IsReady(root, configuration, release));
            Assert.True(WslWorkerEnvironment.IsReady(root, configuration, release, serviceProcess));
            Assert.False(WslWorkerEnvironment.IsReady(root, configuration, "another-release", serviceProcess));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class UnusedGateway : IWorkerPlantGateway
    {
        public Task AuthorizeAsync(DevelopmentMission mission, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RepositoryAccessCredential> CredentialAsync(bool write, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PublishedDevelopmentPullRequest> PublishAsync(string candidate, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PublishedDevelopmentPullRequest?> PublishedAsync(string candidate, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DevelopmentReview> ReviewAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReportAsync(DevelopmentMissionProgress progress, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
