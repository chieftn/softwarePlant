using System.Diagnostics;
using Factory.Contracts;

namespace Factory.Workbench;

public interface IDevelopmentMissionRepository
{
    Task PrepareAsync(DevelopmentMission mission, CancellationToken cancellationToken);
    Task<string> CommitAsync(DevelopmentMission mission, string? expectedParent, CancellationToken cancellationToken);
    Task<CandidateVerification[]> VerifyAsync(DevelopmentMission mission, string candidate, CancellationToken cancellationToken);
    Task AssertCandidateAsync(string candidate, CancellationToken cancellationToken);
    Task<PublishedDevelopmentPullRequest> PublishAsync(DevelopmentMission mission, string candidate,
        PublishedDevelopmentPullRequest? previous, CancellationToken cancellationToken);
    Task<PublishedDevelopmentPullRequest?> ReconcilePublicationAsync(DevelopmentMission mission, string candidate,
        CancellationToken cancellationToken);
    Task<DevelopmentReview> ReviewAsync(PublishedDevelopmentPullRequest pullRequest, CancellationToken cancellationToken);
}

public interface IDevelopmentMissionAgent
{
    Task ImplementAsync(DevelopmentMission mission, CandidateAlignment? previousAlignment,
        DevelopmentReviewFeedback[] feedback, CancellationToken cancellationToken);
    Task<CandidateAlignment> AlignAsync(DevelopmentMission mission, string candidate,
        CandidateVerification[] verification, CancellationToken cancellationToken);
}

public sealed class DevelopmentMissionRunner(IDevelopmentMissionRepository repository, IDevelopmentMissionAgent agents,
    Func<DevelopmentMissionState, CancellationToken, Task> persist,
    Func<DevelopmentMission, CancellationToken, Task> authorize, string[] reviewers, TimeProvider clock)
{
    public async Task<DevelopmentMissionState> AdvanceAsync(DevelopmentMissionState state, CancellationToken cancellationToken)
    {
        state.Mission.Validate();
        if (state.Iterations < 0 || state.ActiveMilliseconds < 0 || state.ProcessedFeedback is null || state.PendingFeedback is null)
            throw new InvalidDataException("Mission counters and feedback journals are invalid.");
        if (reviewers.Length == 0 || reviewers.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("An explicit trusted reviewer allowlist is required.");
        if (state.State is "approved" or "blocked" or "cancelled" or "needsAttention")
            return state;
        if (state.State is "executing" or "publishing")
            return await Save(state with
            {
                State = "needsAttention",
                Reason = "Interrupted execution/publication is not automatically replayed. Reconcile owned work and external effects."
            });
        try { await authorize(state.Mission, cancellationToken); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or TimeoutException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return await Save(state with
            {
                State = "needsAttention",
                Reason = "Mission authorization unavailable; no agent started. Reconcile the retained claim before retrying."
            });
        }
        if (state.State == "awaitingApproval")
        {
            if (clock.GetUtcNow() >= state.Mission.ApprovalDeadline)
                return await Save(state with { State = "blocked", Reason = "Human review deadline exhausted; no automatic merge or new execution." });
            var pullRequest = state.PullRequest ?? throw new InvalidDataException("Approval wait has no recorded pull request.");
            DevelopmentReview review;
            try { review = await repository.ReviewAsync(pullRequest, cancellationToken); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or TimeoutException ||
                error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return await Save(state with
                {
                    State = "needsAttention",
                    Reason = "Review boundary unavailable or invalid. Preserve the PR and claim; do not restart agent work."
                });
            }
            if (review.CandidateCommit != state.CandidateCommit || review.State != "open")
                return await Save(state with
                {
                    State = "needsAttention",
                    Reason = "Published PR head or state changed outside this mission. Reconciliation is required."
                });
            var trusted = review.Feedback.Where(item => reviewers.Contains(item.Actor, StringComparer.OrdinalIgnoreCase) &&
                item.CandidateCommit == state.CandidateCommit).ToArray();
            var latestReviews = trusted.Where(item => item.Kind is "approved" or "changesRequested" or "dismissed")
                .GroupBy(item => item.Actor, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.RecordedAt).ThenByDescending(item => item.Id, StringComparer.Ordinal).First())
                .ToArray();
            var feedback = trusted.Where(item => item.Kind is "comment" or "changesRequested" &&
                !state.ProcessedFeedback.Contains(item.Id, StringComparer.Ordinal))
                .DistinctBy(item => item.Id, StringComparer.Ordinal).ToArray();
            if (feedback.Length == 0 && !latestReviews.Any(item => item.Kind == "changesRequested") &&
                latestReviews.Any(item => item.Kind == "approved"))
                return await Save(state with { State = "approved", Reason = "Trusted review approved the current candidate. No merge was performed." });
            if (feedback.Length == 0)
                return state;
            if (feedback.Length > 64 || state.ProcessedFeedback.Length + feedback.Length > 256)
                return await Save(state with { State = "blocked", Reason = "Review feedback exceeds the bounded mission intake limit." });
            state = await Save(state with
            {
                State = "revising",
                PendingFeedback = feedback,
                ProcessedFeedback = state.ProcessedFeedback.Concat(feedback.Select(item => item.Id)).Distinct(StringComparer.Ordinal).ToArray(),
                Reason = "Accepted trusted commit-specific feedback; frozen scope and permissions are unchanged."
            });
        }
        if (state.State is not ("claimed" or "revising"))
            throw new InvalidDataException("Unsupported durable mission state.");
        var workload = state.Mission.Workload.Validate();
        var policy = state.Mission.Workload.Configuration.ProductionLine!.Definition.Policy;
        var maximumIterations = Math.Min(workload.Workflow.MaximumIterations, policy.MaximumIterations);
        var maximumMilliseconds = (long)Math.Min(workload.Workflow.TimeoutMinutes, policy.TimeoutMinutes) * 60 * 1000;
        if (!policy.AllowShellExecution || !policy.AllowPublication)
            return await Save(state with { State = "blocked", Reason = "The frozen line policy does not permit verification/publication." });
        if (state.Iterations >= maximumIterations || state.ActiveMilliseconds >= maximumMilliseconds)
            return await Save(state with { State = "blocked", Reason = "Active execution/revision budget exhausted." });
        var originalState = state.State;
        state = await Save(state with { State = "executing", Reason = "Owned execution in progress; interruption requires reconciliation." });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(maximumMilliseconds - state.ActiveMilliseconds));
        var started = Stopwatch.GetTimestamp();
        var previousActiveMilliseconds = state.ActiveMilliseconds;
        var monitoring = MonitorAuthorizationAsync();
        try
        {
            if (originalState == "claimed")
                await repository.PrepareAsync(state.Mission, deadline.Token);
            while (state.Iterations < maximumIterations)
            {
                await authorize(state.Mission, deadline.Token);
                state = await Save(state with { Iterations = state.Iterations + 1 });
                await agents.ImplementAsync(state.Mission, state.Alignment, state.PendingFeedback, deadline.Token);
                await authorize(state.Mission, deadline.Token);
                var candidate = await repository.CommitAsync(state.Mission, state.CandidateCommit, deadline.Token);
                RepositoryWorkload.Commit(candidate);
                state = await Save(state with { CandidateCommit = candidate, Verification = [], Alignment = null });
                var evidence = await repository.VerifyAsync(state.Mission, candidate, deadline.Token);
                state = await Save(state with { Verification = evidence });
                if (evidence.Length != workload.Verification.Commands.Length ||
                    evidence.Where((item, index) => item.CandidateCommit != candidate || item.ExitCode != 0 ||
                        !CommandsEqual(item.Command, workload.Verification.Commands[index])).Any())
                    return await Finish("blocked", "Independent verification failed or did not cover the declared checks.");
                await repository.AssertCandidateAsync(candidate, deadline.Token);
                await authorize(state.Mission, deadline.Token);
                var alignment = await agents.AlignAsync(state.Mission, candidate, evidence, deadline.Token);
                alignment.Validate(state.Mission, candidate);
                await repository.AssertCandidateAsync(candidate, deadline.Token);
                state = await Save(state with { Verification = evidence, Alignment = alignment });
                if (alignment.Outcome == "blocked")
                    return await Finish("blocked", "Alignment reported unresolved blockers.");
                if (alignment.Outcome == "revise")
                    continue;
                await authorize(state.Mission, deadline.Token);
                state = await Save(state with
                {
                    State = "publishing",
                    PublicationPending = true,
                    ActiveMilliseconds = ElapsedActiveMilliseconds(),
                    Reason = "Publication intent recorded; uncertain effects require read-back."
                });
                var published = await repository.PublishAsync(state.Mission, candidate, state.PullRequest, deadline.Token);
                ValidatePublication(state.Mission, candidate, published, state.PullRequest);
                state = state with { PullRequest = published, PendingFeedback = [], PublicationPending = false };
                return await Finish("awaitingApproval", "Verified and aligned candidate published. Awaiting trusted human review; no SDK session is retained.");
            }
            return await Finish("blocked", "Implementation/alignment iteration budget exhausted.");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return await Finish("needsAttention", "Execution cancelled, contact lost or authorization revoked. Confirm owned-process termination and publication state before release.");
        }
        catch (Exception error) when (error is IOException or ContractValidationException or System.Text.Json.JsonException)
        {
            return await Finish("needsAttention", $"Mission boundary failed ({error.GetType().Name}). Reconcile evidence and effects; do not replay.");
        }
        finally
        {
            await deadline.CancelAsync();
            await monitoring;
        }

        long ElapsedActiveMilliseconds() => checked(previousActiveMilliseconds + (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        async Task MonitorAuthorizationAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), deadline.Token);
                    using var contact = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    contact.CancelAfter(TimeSpan.FromSeconds(10));
                    await authorize(state.Mission, contact.Token);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or
                OperationCanceledException or TimeoutException)
            {
                await deadline.CancelAsync();
            }
        }

        async Task<DevelopmentMissionState> Finish(string status, string reason)
        {
            state = state with
            {
                State = status,
                Reason = reason,
                ActiveMilliseconds = ElapsedActiveMilliseconds()
            };
            return await Save(state);
        }
        async Task<DevelopmentMissionState> Save(DevelopmentMissionState current)
        {
            await persist(current, CancellationToken.None);
            return current;
        }
    }

    public async Task<DevelopmentMissionState> ReconcilePublicationAsync(DevelopmentMissionState state, CancellationToken cancellationToken)
    {
        state.Mission.Validate();
        if (state.State is not ("needsAttention" or "publishing") || !state.PublicationPending ||
            state.CandidateCommit is null || state.Alignment?.Outcome != "pass")
            throw new InvalidDataException("Only recorded verified/aligned publication intent can be reconciled.");
        RepositoryWorkload.Commit(state.CandidateCommit);
        state.Alignment.Validate(state.Mission, state.CandidateCommit);
        var commands = state.Mission.Workload.Validate().Verification.Commands;
        if (state.Verification is null || state.Verification.Length != commands.Length ||
            state.Verification.Where((item, index) => item.CandidateCommit != state.CandidateCommit ||
                item.ExitCode != 0 || !CommandsEqual(item.Command, commands[index])).Any())
            throw new InvalidDataException("Publication reconciliation requires the recorded successful candidate checks.");
        await authorize(state.Mission, cancellationToken);
        var published = await repository.ReconcilePublicationAsync(state.Mission, state.CandidateCommit, cancellationToken);
        if (published is null)
            return state;
        ValidatePublication(state.Mission, state.CandidateCommit, published, state.PullRequest);
        var reconciled = state with
        {
            State = "awaitingApproval",
            PullRequest = published,
            PublicationPending = false,
            PendingFeedback = [],
            Reason = "Recorded publication reconciled by exact branch/commit/PR read-back; no agent work replayed."
        };
        await persist(reconciled, cancellationToken);
        return reconciled;
    }

    private static bool CommandsEqual(WorkloadCommand left, WorkloadCommand right) =>
        left.Executable == right.Executable && left.WorkingDirectory == right.WorkingDirectory &&
        left.TimeoutSeconds == right.TimeoutSeconds && left.Arguments.SequenceEqual(right.Arguments);

    private static void ValidatePublication(DevelopmentMission mission, string candidate,
        PublishedDevelopmentPullRequest published, PublishedDevelopmentPullRequest? previous)
    {
        var source = mission.Workload.Configuration.ProductionLine!.Definition.Source;
        if (published.Repository != source.Repository || published.Branch != mission.Branch ||
            published.CandidateCommit != candidate || published.BaseBranch != source.BaseBranch || published.Number <= 0 ||
            published.Url != $"https://github.com/{source.Repository}/pull/{published.Number}" ||
            previous is not null && published.Number != previous.Number)
            throw new InvalidDataException("Publication read-back does not match the one owned mission PR.");
    }
}
