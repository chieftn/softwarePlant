using Factory.Contracts;
using Factory.Workbench;
using Xunit;

namespace Factory.Tests;

public sealed class DevelopmentMissionTests
{
    internal static DevelopmentMission Mission(int iterations = 3)
    {
        var configuration = RepositoryWorkloadTests.Configuration();
        configuration = configuration with
        {
            ProductionLine = configuration.ProductionLine! with
            {
                Definition = configuration.ProductionLine.Definition with { Policy = new(true, true, iterations, 30) }
            }
        };
        var workload = ResolvedRepositoryWorkload.Create(configuration, new('a', 40), RepositoryWorkloadTests.Files(true));
        var issue = new DevelopmentIssue(7, "product/tasks", "Fix approved behavior",
            "Implement the approved issue.\n\n## Acceptance criteria\n- Preserve existing ownership.",
            ["Preserve existing ownership."], ["approved"], DateTimeOffset.UtcNow);
        return DevelopmentMission.Create(PlantApiTests.Key(), workload, issue, "test-model", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(2));
    }

    [Fact]
    public async Task IssueRevisesOnePrAndApprovalWaitsForMergedCompletion()
    {
        var fixture = new Fixture();
        var state = await fixture.Runner.AdvanceAsync(DevelopmentMissionState.Claimed(Mission()), CancellationToken.None);
        Assert.Equal("awaitingApproval", state.State);
        Assert.Equal(1, fixture.Agent.Implementations);
        var firstCommit = state.CandidateCommit!;
        fixture.Repository.Feedback = [
            new("old-approval", "reviewer", new('f', 40), "approved", "", fixture.Clock.Now),
            new("untrusted", "stranger", firstCommit, "comment", "Widen scope.", fixture.Clock.Now),
            new("feedback-1", "reviewer", firstCommit, "comment", "Correct the approved ownership case.", fixture.Clock.Now)
        ];
        state = await fixture.Runner.AdvanceAsync(state, CancellationToken.None);
        Assert.Equal("awaitingApproval", state.State);
        Assert.NotEqual(firstCommit, state.CandidateCommit);
        Assert.Equal(2, fixture.Agent.Implementations);
        Assert.Equal(11, state.PullRequest!.Number);
        Assert.Equal(["feedback-1"], state.ProcessedFeedback);
        Assert.Single(fixture.Agent.LastFeedback);
        state = await fixture.Runner.AdvanceAsync(state, CancellationToken.None);
        Assert.Equal(2, fixture.Agent.Implementations);
        fixture.Repository.Feedback = [new("approval-2", "reviewer", state.CandidateCommit!, "approved", "", fixture.Clock.Now)];
        state = await fixture.Runner.AdvanceAsync(state, CancellationToken.None);
        Assert.Equal("awaitingMerge", state.State);
        state = await fixture.Runner.AdvanceAsync(state, CancellationToken.None);
        Assert.Equal("awaitingMerge", state.State);
        fixture.Repository.ReviewState = "merged";
        state = await fixture.Runner.AdvanceAsync(state, CancellationToken.None);
        Assert.Equal("completed", state.State);
        Assert.Equal(state, await fixture.Runner.AdvanceAsync(state, CancellationToken.None));
        Assert.Equal(2, fixture.Repository.Publications);
        Assert.Equal(2, fixture.Agent.Implementations);
    }

    [Theory]
    [InlineData("closed", false, "blocked")]
    [InlineData("merged", true, "needsAttention")]
    public async Task ClosedUnmergedOrChangedHeadCannotComplete(string reviewState, bool changedHead, string expected)
    {
        var fixture = new Fixture();
        var state = await fixture.Runner.AdvanceAsync(DevelopmentMissionState.Claimed(Mission()), CancellationToken.None);
        fixture.Repository.ReviewState = reviewState;
        fixture.Repository.ChangedHead = changedHead;
        state = await fixture.Runner.AdvanceAsync(state, CancellationToken.None);
        Assert.Equal(expected, state.State);
        Assert.Equal(state, await fixture.Runner.AdvanceAsync(state, CancellationToken.None));
        Assert.Equal(1, fixture.Agent.Implementations);
    }

    [Fact]
    public async Task ApprovalCanBeWithdrawnAndNewFeedbackStillRevisesWhileWaitingForMerge()
    {
        var fixture = new Fixture();
        var state = await fixture.Runner.AdvanceAsync(DevelopmentMissionState.Claimed(Mission()), CancellationToken.None);
        fixture.Repository.Feedback = [new("approved", "reviewer", state.CandidateCommit!, "approved", "", fixture.Clock.Now)];
        state = await fixture.Runner.AdvanceAsync(state, CancellationToken.None);
        Assert.Equal("awaitingMerge", state.State);
        fixture.Repository.Feedback = [];
        state = await fixture.Runner.AdvanceAsync(state, CancellationToken.None);
        Assert.Equal("awaitingApproval", state.State);
        fixture.Repository.Feedback = [new("approved", "reviewer", state.CandidateCommit!, "approved", "", fixture.Clock.Now)];
        state = await fixture.Runner.AdvanceAsync(state, CancellationToken.None);
        fixture.Repository.Feedback = [new("revision", "reviewer", state.CandidateCommit!, "changesRequested", "Correct ownership.", fixture.Clock.Now)];
        state = await fixture.Runner.AdvanceAsync(state, CancellationToken.None);
        Assert.Equal("awaitingApproval", state.State);
        Assert.Equal(2, fixture.Agent.Implementations);
        Assert.Equal(11, state.PullRequest!.Number);
    }

    [Fact]
    public async Task FailedIndependentVerificationCannotAlignOrPublish()
    {
        var fixture = new Fixture();
        fixture.Repository.FailedVerification = true;
        var state = await fixture.Runner.AdvanceAsync(DevelopmentMissionState.Claimed(Mission()), CancellationToken.None);
        Assert.Equal("blocked", state.State);
        Assert.Contains(state.Verification, evidence => evidence.ExitCode != 0);
        Assert.Equal(0, fixture.Agent.Alignments);
        Assert.Equal(0, fixture.Repository.Publications);
    }

    [Fact]
    public async Task WrongAlignmentCandidateCannotPublish()
    {
        var fixture = new Fixture();
        fixture.Agent.WrongAlignmentCommit = true;
        var state = await fixture.Runner.AdvanceAsync(DevelopmentMissionState.Claimed(Mission()), CancellationToken.None);
        Assert.Equal("needsAttention", state.State);
        Assert.Equal(0, fixture.Repository.Publications);
    }

    [Fact]
    public async Task RevocationAfterImplementationStopsBeforeCommitOrPublication()
    {
        var fixture = new Fixture();
        fixture.Agent.AfterImplement = () => fixture.Authorized = false;
        var state = await fixture.Runner.AdvanceAsync(DevelopmentMissionState.Claimed(Mission()), CancellationToken.None);
        Assert.Equal("needsAttention", state.State);
        Assert.Equal(0, fixture.Repository.Commits);
        Assert.Equal(0, fixture.Repository.Publications);
    }

    [Fact]
    public async Task InterruptedExecutionIsNotReplayed()
    {
        var fixture = new Fixture();
        var state = DevelopmentMissionState.Claimed(Mission()) with { State = "executing", Iterations = 1 };
        state = await fixture.Runner.AdvanceAsync(state, CancellationToken.None);
        Assert.Equal("needsAttention", state.State);
        Assert.Equal(0, fixture.Agent.Implementations);
        Assert.Equal(0, fixture.Repository.Preparations);
    }

    [Fact]
    public async Task LostPublicationResponseReconcilesExactPrWithoutAnotherAgentOrWrite()
    {
        var fixture = new Fixture();
        fixture.Repository.LosePublicationResponse = true;
        var state = await fixture.Runner.AdvanceAsync(DevelopmentMissionState.Claimed(Mission()), CancellationToken.None);
        Assert.Equal("needsAttention", state.State);
        Assert.Contains(fixture.Persisted, item => item.State == "publishing" && item.Alignment?.Outcome == "pass");
        state = await fixture.Runner.ReconcilePublicationAsync(state, CancellationToken.None);
        Assert.Equal("awaitingApproval", state.State);
        Assert.Equal(1, fixture.Repository.Publications);
        Assert.Equal(1, fixture.Agent.Implementations);
    }

    [Fact]
    public async Task PublicationReconciliationRequiresSuccessfulRecordedVerification()
    {
        var fixture = new Fixture();
        fixture.Repository.LosePublicationResponse = true;
        var state = await fixture.Runner.AdvanceAsync(DevelopmentMissionState.Claimed(Mission()), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Runner.ReconcilePublicationAsync(
            state with { Verification = [] }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Runner.ReconcilePublicationAsync(
            state with { Verification = [state.Verification[0] with { ExitCode = 1 }] }, CancellationToken.None));
        Assert.Equal(1, fixture.Repository.Publications);
        Assert.Equal(1, fixture.Agent.Implementations);
    }
    [Fact]
    public async Task RevisionsConsumeOriginalBudgetRatherThanResettingIt()
    {
        var fixture = new Fixture();
        var state = await fixture.Runner.AdvanceAsync(DevelopmentMissionState.Claimed(Mission(1)), CancellationToken.None);
        fixture.Repository.Feedback = [new("feedback", "reviewer", state.CandidateCommit!, "comment", "Correct the approved case.", fixture.Clock.Now)];
        state = await fixture.Runner.AdvanceAsync(state, CancellationToken.None);
        Assert.Equal("blocked", state.State);
        Assert.Equal(1, fixture.Agent.Implementations);
        Assert.Equal(1, state.Iterations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HumanWaitDoesNotExecuteAndDeadlineIsNotResetByPolling(bool approved)
    {
        var fixture = new Fixture();
        var state = await fixture.Runner.AdvanceAsync(DevelopmentMissionState.Claimed(Mission()), CancellationToken.None);
        if (approved)
            fixture.Repository.Feedback = [new("approved", "reviewer", state.CandidateCommit!, "approved", "", fixture.Clock.Now)];
        var active = state.ActiveMilliseconds;
        state = await fixture.Runner.AdvanceAsync(state, CancellationToken.None);
        Assert.Equal(approved ? "awaitingMerge" : "awaitingApproval", state.State);
        Assert.Equal(active, state.ActiveMilliseconds);
        fixture.Clock.Now = state.Mission.ApprovalDeadline;
        state = await fixture.Runner.AdvanceAsync(state, CancellationToken.None);
        Assert.Equal("blocked", state.State);
        Assert.Equal(1, fixture.Agent.Implementations);
    }

    [Fact]
    public async Task ReviewBoundaryFailureRetainsOwnershipWithoutRestartingAgents()
    {
        var fixture = new Fixture();
        var state = await fixture.Runner.AdvanceAsync(DevelopmentMissionState.Claimed(Mission()), CancellationToken.None);
        fixture.Repository.FailReview = true;
        state = await fixture.Runner.AdvanceAsync(state, CancellationToken.None);
        Assert.Equal("needsAttention", state.State);
        Assert.NotNull(state.PullRequest);
        state = await fixture.Runner.AdvanceAsync(state, CancellationToken.None);
        Assert.Equal("needsAttention", state.State);
        Assert.Equal(1, fixture.Agent.Implementations);
    }

    [Fact]
    public async Task PersistenceMustSucceedBeforeExternalExecution()
    {
        var fixture = new Fixture();
        fixture.FailPersistence = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Runner.AdvanceAsync(DevelopmentMissionState.Claimed(Mission()), CancellationToken.None));
        Assert.Equal(0, fixture.Repository.Preparations);
        Assert.Equal(0, fixture.Agent.Implementations);
    }

    [Fact]
    public async Task LatestChangesRequestedInvalidatesEarlierApprovalBySameActor()
    {
        var fixture = new Fixture();
        var state = await fixture.Runner.AdvanceAsync(DevelopmentMissionState.Claimed(Mission()), CancellationToken.None);
        fixture.Repository.Feedback = [
            new("review-1", "reviewer", state.CandidateCommit!, "approved", "", fixture.Clock.Now.AddSeconds(-1)),
            new("review-2", "reviewer", state.CandidateCommit!, "changesRequested", "Correct the ownership case.", fixture.Clock.Now)
        ];
        state = await fixture.Runner.AdvanceAsync(state, CancellationToken.None);
        Assert.Equal("awaitingApproval", state.State);
        Assert.Equal(2, fixture.Agent.Implementations);
    }

    private sealed class Fixture
    {
        public TestClock Clock { get; } = new();
        public FakeRepository Repository { get; } = new();
        public FakeAgent Agent { get; } = new();
        public List<DevelopmentMissionState> Persisted { get; } = [];
        public bool Authorized { get; set; } = true;
        public bool FailPersistence { get; set; }
        public DevelopmentMissionRunner Runner => new(Repository, Agent, (state, _) =>
        {
            if (FailPersistence)
                throw new IOException("Synthetic persistence outage.");
            Persisted.Add(state);
            return Task.CompletedTask;
        }, (_, _) => Authorized ? Task.CompletedTask : throw new IOException("Synthetic revoked authorization."),
            ["reviewer"], Clock);
    }

    private sealed class FakeAgent : IDevelopmentMissionAgent
    {
        public int Implementations { get; private set; }
        public int Alignments { get; private set; }
        public bool WrongAlignmentCommit { get; set; }
        public Action? AfterImplement { get; set; }
        public DevelopmentReviewFeedback[] LastFeedback { get; private set; } = [];
        public Task ImplementAsync(DevelopmentMission mission, CandidateAlignment? previousAlignment,
            DevelopmentReviewFeedback[] feedback, CancellationToken cancellationToken)
        {
            Implementations++;
            LastFeedback = feedback;
            AfterImplement?.Invoke();
            return Task.CompletedTask;
        }
        public Task<CandidateAlignment> AlignAsync(DevelopmentMission mission, string candidate,
            CandidateVerification[] verification, CancellationToken cancellationToken)
        {
            Alignments++;
            return Task.FromResult(new CandidateAlignment(WrongAlignmentCommit ? new('f', 40) : candidate, "pass",
                mission.Issue.AcceptanceCriteria.Select(criterion => new AlignmentCoverage(criterion, "Independently verified candidate.")).ToArray(),
                [], []));
        }
    }

    private sealed class FakeRepository : IDevelopmentMissionRepository
    {
        public int Preparations { get; private set; }
        public int Commits { get; private set; }
        public int Publications { get; private set; }
        public bool FailedVerification { get; set; }
        public bool LosePublicationResponse { get; set; }
        public bool FailReview { get; set; }
        public string ReviewState { get; set; } = "open";
        public bool ChangedHead { get; set; }
        public DevelopmentReviewFeedback[] Feedback { get; set; } = [];
        private PublishedDevelopmentPullRequest? published;
        public Task PrepareAsync(DevelopmentMission mission, CancellationToken cancellationToken)
        {
            Preparations++;
            return Task.CompletedTask;
        }
        public Task<string> CommitAsync(DevelopmentMission mission, string? expectedParent, CancellationToken cancellationToken) =>
            Task.FromResult(new string((char)('b' + Commits++), 40));
        public Task<CandidateVerification[]> VerifyAsync(DevelopmentMission mission, string candidate, CancellationToken cancellationToken) =>
            Task.FromResult(mission.Workload.Validate().Verification.Commands.Select(command =>
                new CandidateVerification(candidate, command, FailedVerification ? 1 : 0, new('A', 64), 1)).ToArray());
        public Task AssertCandidateAsync(string candidate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<PublishedDevelopmentPullRequest> PublishAsync(DevelopmentMission mission, string candidate,
            PublishedDevelopmentPullRequest? previous, CancellationToken cancellationToken)
        {
            Publications++;
            published = new(11, "product/source", mission.Branch, "main", candidate, "https://github.com/product/source/pull/11");
            if (LosePublicationResponse)
                throw new IOException("Synthetic lost response after publication.");
            return Task.FromResult(published);
        }
        public Task<PublishedDevelopmentPullRequest?> ReconcilePublicationAsync(DevelopmentMission mission, string candidate,
            CancellationToken cancellationToken) => Task.FromResult(published);
        public Task<DevelopmentReview> ReviewAsync(PublishedDevelopmentPullRequest pullRequest, CancellationToken cancellationToken) =>
            FailReview ? Task.FromException<DevelopmentReview>(new IOException("Synthetic review transport failure."))
                : Task.FromResult(new DevelopmentReview(ChangedHead ? new('f', 40) : pullRequest.CandidateCommit, ReviewState, Feedback));
    }
}
