using System.Net;
using System.Text.Json;
using Factory.Contracts;
using Factory.Workbench;
using Factory.Workbench.Worker;
using Xunit;

namespace Factory.Tests;

public sealed class MissionTelemetryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"factory-telemetry-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(false, "implementation")]
    [InlineData(true, "commit")]
    public async Task FailedMissionPersistsStageUnderlyingErrorAndDoesNotReplay(bool commitFailure, string stage)
    {
        var mission = DevelopmentMissionTests.Mission();
        var file = Path.Combine(root, "mission-telemetry.jsonl");
        var published = new List<MissionTelemetryEntry>();
        var telemetry = new MissionTelemetry(file, mission.Id, mission.Digest, PlantApiTests.Key(), "worker", published.Add);
        var error = new MissionOperationException(commitFailure ? "git-commit" : "sdk-session",
            "Fixture failure ghu_fixturesecret", new IOException("Underlying runtime rejected fixture request."),
            code: "fixture_rejected", rpcCode: -32001, exitCode: commitFailure ? 128 : null);
        var repository = new Repository(commitFailure ? error : null);
        var agent = new Agent(commitFailure ? null : error);
        var runner = new DevelopmentMissionRunner(repository, agent, (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask, ["reviewer"], TimeProvider.System, telemetry);

        var state = await runner.AdvanceAsync(DevelopmentMissionState.Claimed(mission), CancellationToken.None);
        Assert.Equal("needsAttention", state.State);
        Assert.Contains(commitFailure ? "git-commit" : "sdk-session", state.Reason);
        var entries = MissionTelemetry.Parse(File.ReadAllText(file), mission.Id, mission.Digest);
        var failed = entries.Last(entry => entry.Stage == stage && entry.Outcome == "failed");
        Assert.Equal("fixture_rejected", failed.Errors[0].Code);
        Assert.Equal(-32001, failed.Errors[0].RpcCode);
        Assert.Contains("Underlying runtime rejected", failed.Errors[1].Message);
        Assert.DoesNotContain("ghu_fixturesecret", File.ReadAllText(file));
        Assert.Equal(entries.Length, published.Count);
        Assert.Equal(state, await runner.AdvanceAsync(state, CancellationToken.None));
        Assert.Equal(1, agent.Calls);
        Assert.Null(state.CandidateCommit);
    }

    [Fact]
    public async Task HostFailureCodeStatusCorrelationAndMessageSurviveTheWorkerPipe()
    {
        WorkerPipeMessage? emitted = null;
        var pipe = new WorkerPlantPipe(message => emitted = message);
        var task = pipe.ModelCredentialAsync(DevelopmentMissionTests.Mission(), CancellationToken.None);
        var request = Assert.IsType<WorkerPipeMessage>(emitted);
        pipe.Receive(new("error", request.Id, request.Operation, JsonSerializer.SerializeToElement(
            new WorkerOperationFailure("copilot_reauthorization_required", "Fixture expired ghu_fixturesecret",
                403, "fixture-correlation"), RepositoryWorkload.Json)));
        var error = await Assert.ThrowsAsync<MissionOperationException>(() => task);
        Assert.Equal("model-credential", error.Stage);
        Assert.Equal("copilot_reauthorization_required", error.Code);
        Assert.Equal(403, error.HttpStatus);
        Assert.Equal("fixture-correlation", error.CorrelationId);
        Assert.DoesNotContain("ghu_fixturesecret", error.Message);
    }

    [Fact]
    public async Task MalformedFailureCompletesThePendingRequestInsteadOfHanging()
    {
        WorkerPipeMessage? emitted = null;
        var pipe = new WorkerPlantPipe(message => emitted = message);
        var task = pipe.ModelCredentialAsync(DevelopmentMissionTests.Mission(), CancellationToken.None);
        var request = Assert.IsType<WorkerPipeMessage>(emitted);
        pipe.Receive(new("error", request.Id, request.Operation, JsonSerializer.SerializeToElement(new { code = "fixture" })));
        await Assert.ThrowsAsync<InvalidDataException>(() => task);
    }

    [Fact]
    public void ProjectionRedactsCredentialsAndPreservesRequestMetadata()
    {
        var error = new PlantConnectionException(HttpStatusCode.Forbidden,
            "Authorization: Basic Zml4dHVyZQ== https://fixture:secret@example.invalid/ Bearer fixture-token client_secret=\"fixture-secret\"",
            "fixture-correlation", "fixture_forbidden");
        var projected = Assert.Single(MissionTelemetry.Project(error));
        Assert.Equal(403, projected.HttpStatus);
        Assert.Equal("fixture_forbidden", projected.Code);
        Assert.Equal("fixture-correlation", projected.CorrelationId);
        Assert.DoesNotContain("Zml4dHVyZQ", projected.Message);
        Assert.DoesNotContain("fixture:secret", projected.Message);
        Assert.DoesNotContain("fixture-token", projected.Message);
        Assert.DoesNotContain("fixture-secret", projected.Message);
    }

    [Fact]
    public void WrongMissionAndOversizedTelemetryCannotBeCollected()
    {
        var mission = DevelopmentMissionTests.Mission();
        var telemetry = new MissionTelemetry(Path.Combine(root, "mission.jsonl"), mission.Id, mission.Digest, PlantApiTests.Key(), "worker");
        telemetry.Record("implementation", "started", 1);
        var text = File.ReadAllText(telemetry.FilePath);
        Assert.Throws<InvalidDataException>(() => MissionTelemetry.Parse(text, PlantApiTests.Key(), mission.Digest));
        Assert.Throws<InvalidDataException>(() => MissionTelemetry.Parse(new string('x', MissionTelemetry.MaximumBytes + 1), mission.Id, mission.Digest));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class Agent(Exception? error) : IDevelopmentMissionAgent
    {
        public int Calls { get; private set; }
        public Task ImplementAsync(DevelopmentMission mission, CandidateAlignment? alignment,
            DevelopmentReviewFeedback[] feedback, CancellationToken cancellationToken)
        {
            Calls++;
            return error is null ? Task.CompletedTask : Task.FromException(error);
        }
        public Task<CandidateAlignment> AlignAsync(DevelopmentMission mission, string candidate,
            CandidateVerification[] verification, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected alignment.");
    }

    private sealed class Repository(Exception? error) : IDevelopmentMissionRepository
    {
        public Task PrepareAsync(DevelopmentMission mission, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> CommitAsync(DevelopmentMission mission, string? expectedParent, CancellationToken cancellationToken) =>
            Task.FromException<string>(error ?? new InvalidOperationException("Unexpected commit."));
        public Task<CandidateVerification[]> VerifyAsync(DevelopmentMission mission, string candidate, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected verification.");
        public Task AssertCandidateAsync(string candidate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<PublishedDevelopmentPullRequest> PublishAsync(DevelopmentMission mission, string candidate,
            PublishedDevelopmentPullRequest? previous, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected publication.");
        public Task<PublishedDevelopmentPullRequest?> ReconcilePublicationAsync(DevelopmentMission mission, string candidate,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected reconciliation.");
        public Task<DevelopmentReview> ReviewAsync(PublishedDevelopmentPullRequest pullRequest, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected review.");
    }
}
