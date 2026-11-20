using System.Security.Cryptography;
using System.Text.Json;
using Factory.Contracts;
using Factory.Plant.Service.Storage;

namespace Factory.Plant.Service;

public sealed class DevelopmentMissionSettings
{
    // An explicit false is an operator shutdown override, including line-managed execution.
    public bool? Enabled { get; set; }
    public string[] Reviewers { get; set; } = [];
    public int ApprovalTimeoutHours { get; set; } = 48;

    public void RequireEnabled()
    {
        if (Enabled != true)
            throw new ApiException(503, "development_execution_disabled", "Development mission execution is disabled on the plant service.");
        if (ApprovalTimeoutHours is < 1 or > 168 || Reviewers is not { Length: > 0 and <= 32 } ||
            Reviewers.Any(actor => string.IsNullOrWhiteSpace(actor) || actor.Length > 39 ||
                !actor.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')))
            throw new ApiException(503, "development_policy_unconfigured", "Configure trusted reviewers and a bounded human review/merge deadline.");
    }

    public DevelopmentMissionSettings Resolve(ProductionLineView? line)
    {
        if (Enabled == false || line?.Definition.Execution is null)
        {
            RequireEnabled();
            return this;
        }
        var execution = line.Definition.Execution;
        execution.Validate();
        var resolved = new DevelopmentMissionSettings
        {
            Enabled = execution.Enabled,
            Reviewers = execution.Reviewers,
            ApprovalTimeoutHours = execution.ApprovalTimeoutHours
        };
        resolved.RequireEnabled();
        return resolved;
    }
}

public sealed partial class EnrollmentOperations
{
    public async Task<DevelopmentQueueSelection> ClaimNextMissionAsync(string id, ApiCaller caller,
        DevelopmentQueueRequest request, DevelopmentMissionSettings development, IGitHubRepositoryReader workloads,
        IGitHubDevelopmentRepository repositories, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(request.OperationId, "D", out var operation) || operation == Guid.Empty)
            throw ApiProblems.Invalid("An immutable queue operation UUID is required.");
        var configuration = await ConfigurationAsync(id, caller, cancellationToken);
        if (configuration.ProductionLine is null || !request.Configuration.Matches(configuration))
            throw new ApiException(409, "configuration_changed", "Queue request was superseded or revoked.");
        development = development.Resolve(configuration.ProductionLine);
        var line = configuration.ProductionLine;
        RequireConfiguredModel(line, request.Model);
        if (!line.Definition.Policy.AllowShellExecution || !line.Definition.Policy.AllowPublication)
            throw new ApiException(403, "development_not_permitted", "The assigned line does not permit verified PR execution.");
        await plants.RequireRepositoryAuthorityAsync(line, cancellationToken, publication: true);
        await models.RequireAsync(line.Owner, cancellationToken);
        var scope = Records.EnrollmentScope(id);
        var retired = await store.ReadAsync(scope, $"development-mission-{request.OperationId}", cancellationToken);
        if (retired is not null)
        {
            var old = Records.Require<DevelopmentMissionRecord>(retired.Record);
            if (request.WorkloadDigest != old.Mission.Workload.Digest || request.Model != old.Mission.Model ||
                !request.Configuration.Matches(old.Mission.Workload.Configuration))
                throw ApiProblems.Conflict();
            return new(Delivery(old, development));
        }
        var slot = await store.ReadAsync(scope, "development-mission", cancellationToken);
        var previous = slot is null ? null : Records.Require<DevelopmentMissionRecord>(slot.Record);
        if (previous is not null && (previous.Mission.Id == request.OperationId ||
            previous.Progress.State != "rejected" && !(previous.Progress.State == "completed" && previous.TerminationConfirmed)))
        {
            if (previous.Mission.Id == request.OperationId)
                return new(await ClaimMissionAsync(id, caller, new(request.OperationId, request.Configuration,
                    request.WorkloadDigest, previous.Mission.Issue.Number, request.Model),
                    development, workloads, repositories, cancellationToken));
            if (previous.Progress.State == "reserving")
                return new(await ClaimMissionAsync(id, caller, new(previous.Mission.Id,
                    WorkloadRequest.From(previous.Mission.Workload.Configuration), previous.Mission.Workload.Digest,
                    previous.Mission.Issue.Number, previous.Mission.Model), development, workloads, repositories, cancellationToken));
            return new(Delivery(await RequireMissionAsync(id, caller, development, cancellationToken), development));
        }
        var taskScope = $"development-issues:{line.Definition.Tasks.Repository.ToLowerInvariant()}";
        await foreach (var issue in repositories.EligibleIssuesAsync(line.Definition.Tasks, cancellationToken))
        {
            if (await store.ReadAsync(taskScope, $"issue-{issue.Number}", cancellationToken) is not null)
                continue;
            return new(await ClaimMissionAsync(id, caller, new(request.OperationId, request.Configuration,
                request.WorkloadDigest, issue.Number, request.Model), development, workloads, repositories, cancellationToken));
        }
        return new(null);
    }

    public async Task<DevelopmentMissionDelivery> ClaimMissionAsync(string id, ApiCaller caller, DevelopmentClaimRequest request,
        DevelopmentMissionSettings development, IGitHubRepositoryReader workloads, IGitHubDevelopmentRepository repositories,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(request.OperationId, "D", out var operation) || operation == Guid.Empty || request.IssueNumber <= 0)
            throw ApiProblems.Invalid("An immutable operation UUID and explicitly assigned issue number are required.");
        var configuration = await ConfigurationAsync(id, caller, cancellationToken);
        if (!request.Configuration.Matches(configuration))
            throw new ApiException(409, "configuration_changed", "Mission request was superseded or revoked.");
        development = development.Resolve(configuration.ProductionLine);
        RequireConfiguredModel(configuration.ProductionLine!, request.Model);
        var definition = configuration.ProductionLine!.Definition;
        if (!definition.Policy.AllowShellExecution || !definition.Policy.AllowPublication)
            throw new ApiException(403, "development_not_permitted", "The assigned line does not permit verified PR execution.");
        await plants.RequireRepositoryAuthorityAsync(configuration.ProductionLine!, cancellationToken, publication: true);
        await models.RequireAsync(configuration.ProductionLine!.Owner, cancellationToken);
        var scope = Records.EnrollmentScope(id);
        var slot = await store.ReadAsync(scope, "development-mission", cancellationToken);
        var previous = slot is null ? null : Records.Require<DevelopmentMissionRecord>(slot.Record);
        var requestDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, Records.Json)));
        var retired = await store.ReadAsync(scope, $"development-mission-{request.OperationId}", cancellationToken);
        if (retired is not null)
        {
            var archived = Records.Require<DevelopmentMissionRecord>(retired.Record);
            if (archived.RequestDigest != requestDigest)
                throw ApiProblems.Conflict();
            return Delivery(archived, development);
        }
        DevelopmentMissionRecord current;
        if (previous?.Mission.Id == request.OperationId)
        {
            if (previous.RequestDigest != requestDigest)
                throw ApiProblems.Conflict();
            current = previous;
            if (current.Progress.State != "reserving")
                return Delivery(current, development);
        }
        else
        {
            if (previous is not null && previous.Progress.State != "rejected" &&
                !(previous.Progress.State == "completed" && previous.TerminationConfirmed))
                throw new ApiException(409, "mission_owned", "This workbench already owns a mission; unresolved work cannot be reclaimed or replayed.");
            var resolved = await WorkloadAsync(id, caller, request.Configuration, workloads, cancellationToken);
            if (resolved.Digest != request.WorkloadDigest)
                throw new ApiException(409, "workload_changed", "Refresh the resolved workload before claiming its immutable source snapshot.");
            var issue = await repositories.IssueAsync(definition.Tasks, request.IssueNumber, cancellationToken);
            var mission = DevelopmentMission.Create(request.OperationId, resolved, issue, request.Model, clock.GetUtcNow(),
                clock.GetUtcNow().AddHours(development.ApprovalTimeoutHours));
            mission.Validate();
            current = new()
            {
                Id = "development-mission",
                ScopeId = scope,
                Mission = mission,
                RequestDigest = requestDigest,
                Progress = new(mission.Id, mission.Digest, "reserving", 0, 0, null, null, false,
                    "Workbench slot reserved; unique issue ownership is not yet complete.")
            };
            var enrollment = await RequireMachineAsync(id, caller, cancellationToken);
            var writes = new List<RecordWrite> { new(enrollment.Record, enrollment.ETag), new(current, slot?.ETag) };
            if (previous is not null)
                writes.Add(new(previous with { Id = $"development-mission-{previous.Mission.Id}" }));
            if (!await store.CommitAsync(scope, writes, cancellationToken))
                throw ApiProblems.Unavailable();
        }
        var taskScope = $"development-issues:{definition.Tasks.Repository.ToLowerInvariant()}";
        var taskId = $"issue-{request.IssueNumber}";
        var owned = await store.ReadAsync(taskScope, taskId, cancellationToken);
        if (owned is null)
        {
            if (!await store.CommitAsync(taskScope, [new(new DevelopmentIssueClaimRecord
            {
                Id = taskId, ScopeId = taskScope, EnrollmentId = id, MissionId = current.Mission.Id
            })], cancellationToken))
                throw ApiProblems.Unavailable();
        }
        else if (Records.Require<DevelopmentIssueClaimRecord>(owned.Record) is var claim &&
            (claim.EnrollmentId != id || claim.MissionId != current.Mission.Id))
        {
            await RejectReservation();
            throw new ApiException(409, "issue_owned", "The assigned issue is already owned by another durable mission.");
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var enrollment = await RequireMachineAsync(id, caller, cancellationToken);
            var stored = await store.ReadAsync(scope, "development-mission", cancellationToken) ?? throw ApiProblems.Unavailable();
            current = Records.Require<DevelopmentMissionRecord>(stored.Record);
            if (current.Mission.Id != request.OperationId)
                throw ApiProblems.Conflict();
            var latest = await ConfigurationAsync(id, caller, cancellationToken);
            if (!request.Configuration.Matches(latest))
                throw new ApiException(409, "configuration_changed", "Configuration changed while reserving issue ownership; reconcile the retained reservation.");
            if (current.Progress.State != "reserving")
                return Delivery(current, development);
            var accepted = current with { Progress = current.Progress with { State = "claimed", Reason = "Approved frozen issue uniquely claimed." } };
            if (await store.CommitAsync(scope, [new(enrollment.Record, enrollment.ETag), new(accepted, stored.ETag)], cancellationToken))
                return Delivery(accepted, development);
        }
        throw ApiProblems.Unavailable();

        async Task RejectReservation()
        {
            var stored = await store.ReadAsync(scope, "development-mission", cancellationToken) ?? throw ApiProblems.Unavailable();
            var reserved = Records.Require<DevelopmentMissionRecord>(stored.Record);
            if (reserved.Mission.Id != request.OperationId || reserved.Progress.State != "reserving" ||
                !await store.CommitAsync(scope, [new(reserved with { Progress = reserved.Progress with
                { State = "rejected", Reason = "Another mission owns the issue; no worker execution was authorized." } }, stored.ETag)], cancellationToken))
                throw ApiProblems.Unavailable();
        }
    }

    public async Task<DevelopmentMissionDelivery> MissionAsync(string id, ApiCaller caller, DevelopmentMissionSettings development,
        CancellationToken cancellationToken)
    {
        var mission = await RequireMissionAsync(id, caller, development, cancellationToken);
        return Delivery(mission, development);
    }

    private static DevelopmentMissionDelivery Delivery(DevelopmentMissionRecord mission, DevelopmentMissionSettings development) =>
        new(mission.Mission, mission.Progress.State, ReviewersFor(mission, development)) { PublicationPending = mission.Progress.PublicationPending };

    private static string[] ReviewersFor(DevelopmentMissionRecord mission, DevelopmentMissionSettings development) =>
        development.Resolve(mission.Mission.Workload.Configuration.ProductionLine).Reviewers;

    private static void RequireConfiguredModel(ProductionLineView line, string model)
    {
        if (line.Definition.Execution is { } execution && execution.Model != model)
            throw new ApiException(403, "development_not_permitted", "The requested model is not authorized by the assigned production line.");
    }

    private async Task<DevelopmentMissionRecord> RequireMissionAsync(string id, ApiCaller caller, DevelopmentMissionSettings development,
        CancellationToken cancellationToken)
    {
        var configuration = await ConfigurationAsync(id, caller, cancellationToken);
        development.Resolve(configuration.ProductionLine);
        var stored = await store.ReadAsync(Records.EnrollmentScope(id), "development-mission", cancellationToken)
            ?? throw new ApiException(404, "mission_not_found", "No mission is owned by this workbench.");
        var mission = Records.Require<DevelopmentMissionRecord>(stored.Record);
        var original = mission.Mission.Workload.Configuration;
        var definition = configuration.ProductionLine?.Definition;
        if (configuration.PlantId != original.PlantId || configuration.AssignmentVersion != original.AssignmentVersion ||
            configuration.BindingVersion != original.BindingVersion || configuration.ProductionLine?.Id != original.ProductionLine!.Id ||
            definition is null || !definition.Policy.AllowShellExecution || !definition.Policy.AllowPublication ||
            (original.ProductionLine.Definition.Execution is { } execution
                ? !execution.Matches(definition.Execution) : definition.Execution is not null) ||
            definition.Source.Repository != original.ProductionLine.Definition.Source.Repository ||
            definition.Source.ConnectionReference != original.ProductionLine.Definition.Source.ConnectionReference ||
            definition.Tasks.Repository != original.ProductionLine.Definition.Tasks.Repository ||
            definition.Tasks.ConnectionReference != original.ProductionLine.Definition.Tasks.ConnectionReference ||
            mission.Progress.State is "reserving" or "rejected")
            throw new ApiException(409, "mission_revoked", "Current membership, line binding or repository authority no longer permits this frozen mission.");
        await plants.RequireRepositoryAuthorityAsync(configuration.ProductionLine!, cancellationToken, publication: true);
        await models.RequireAsync(configuration.ProductionLine!.Owner, cancellationToken);
        return mission;
    }

    public async Task<ModelAccessCredential> MissionModelCredentialAsync(string id, ApiCaller caller,
        MissionModelCredentialRequest request, DevelopmentMissionSettings development,
        IGitHubDevelopmentRepository repositories, CancellationToken cancellationToken)
    {
        await AuthorizeMissionAsync(id, caller, development, repositories, cancellationToken);
        var owned = await RequireMissionAsync(id, caller, development, cancellationToken);
        RequireRequestedMission(owned, request);
        var credential = await models.AcquireAsync(owned.Mission.Workload.Configuration.ProductionLine!.Owner,
            owned.Mission.Model, cancellationToken);
        await AuthorizeMissionAsync(id, caller, development, repositories, cancellationToken);
        RequireRequestedMission(await RequireMissionAsync(id, caller, development, cancellationToken), request);
        credential.Validate(owned.Mission.Model, clock.GetUtcNow());
        return credential;
    }

    private static void RequireRequestedMission(DevelopmentMissionRecord owned, MissionModelCredentialRequest request)
    {
        if (owned.Mission.Id != request.MissionId || owned.Mission.Digest != request.MissionDigest ||
            owned.Progress.State != "executing")
            throw new ApiException(409, "model_access_not_authorized",
                "Model credentials require the exact owned mission and recorded active execution.");
    }

    public async Task AuthorizeMissionAsync(string id, ApiCaller caller, DevelopmentMissionSettings development,
        IGitHubDevelopmentRepository repositories, CancellationToken cancellationToken)
    {
        var owned = await RequireMissionAsync(id, caller, development, cancellationToken);
        // Review/merge observation is read-only and must survive GitHub auto-closing the issue.
        if (owned.Progress.State is "awaitingApproval" or "awaitingMerge" or "approved" &&
            owned.Progress.PullRequest is not null)
            return;
        var current = await repositories.IssueAsync(owned.Mission.Workload.Configuration.ProductionLine!.Definition.Tasks,
            owned.Mission.Issue.Number, cancellationToken);
        var configuration = await ConfigurationAsync(id, caller, cancellationToken);
        if (configuration.ProductionLine is null || !configuration.ProductionLine.Definition.Tasks.RequiredLabels.All(label =>
            current.Labels.Contains(label, StringComparer.OrdinalIgnoreCase)))
            throw new ApiException(409, "issue_not_eligible", "The issue no longer satisfies current task selection policy.");
        if (current.UpdatedAt != owned.Mission.Issue.UpdatedAt || current.Body != owned.Mission.Issue.Body ||
            current.Title != owned.Mission.Issue.Title)
            throw new ApiException(409, "issue_changed", "The approved frozen issue changed. Stop and reconcile; comments cannot silently redefine it.");
    }

    public async Task<RepositoryAccessCredential> MissionCredentialAsync(string id, ApiCaller caller, string purpose,
        DevelopmentMissionSettings development, IGitHubDevelopmentRepository repositories, CancellationToken cancellationToken)
    {
        if (purpose is not ("read" or "publish"))
            throw ApiProblems.Invalid("Credential purpose must be read or publish.");
        await AuthorizeMissionAsync(id, caller, development, repositories, cancellationToken);
        var owned = await RequireMissionAsync(id, caller, development, cancellationToken);
        if (purpose == "publish" && (owned.Progress.State != "publishing" || !owned.Progress.PublicationPending ||
            owned.Progress.CandidateCommit is null))
            throw new ApiException(403, "publication_not_authorized", "Source write access requires recorded candidate publication intent.");
        return await repositories.SourceCredentialAsync(owned.Mission, purpose == "publish", cancellationToken);
    }

    public async Task<PublishedDevelopmentPullRequest?> PublishMissionAsync(string id, ApiCaller caller, string candidate, bool reconcile,
        DevelopmentMissionSettings development, IGitHubDevelopmentRepository repositories, CancellationToken cancellationToken)
    {
        RepositoryWorkload.Commit(candidate);
        await AuthorizeMissionAsync(id, caller, development, repositories, cancellationToken);
        var owned = await RequireMissionAsync(id, caller, development, cancellationToken);
        if (candidate != owned.Progress.CandidateCommit || !owned.Progress.PublicationPending)
            throw new ApiException(403, "publication_not_authorized", "The exact candidate has no recorded publication intent.");
        return reconcile ? await repositories.PublishedAsync(owned.Mission, candidate, cancellationToken)
            : await repositories.PublishAsync(owned.Mission, candidate, cancellationToken);
    }

    public async Task<DevelopmentReview> ReviewMissionAsync(string id, ApiCaller caller, DevelopmentMissionSettings development,
        IGitHubDevelopmentRepository repositories, CancellationToken cancellationToken)
    {
        var owned = await RequireMissionAsync(id, caller, development, cancellationToken);
        if (owned.Progress.PullRequest is null)
            throw new ApiException(409, "publication_missing", "No verified publication receipt exists.");
        var review = await repositories.ReviewAsync(owned.Mission, owned.Progress.PullRequest, cancellationToken);
        return review with
        {
            Feedback = review.Feedback.Where(item =>
            ReviewersFor(owned, development).Contains(item.Actor, StringComparer.OrdinalIgnoreCase)).ToArray()
        };
    }

    public async Task ReportMissionAsync(string id, ApiCaller caller, string operation, DevelopmentMissionProgress progress,
        DevelopmentMissionSettings development, IGitHubDevelopmentRepository repositories, CancellationToken cancellationToken)
    {
        var scope = Records.EnrollmentScope(id);
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(progress, Records.Json)));
        var identity = Records.Require<EnrollmentRecord>((await ReadAsync(id, cancellationToken)
            ?? throw ApiProblems.Forbidden()).Record);
        if (identity.ClientId != caller.ClientId || identity.ServicePrincipalId != caller.Principal.ObjectId ||
            caller.Principal.TenantId != api.TenantId)
            throw ApiProblems.Forbidden();
        var oldReceipt = await store.ReadAsync(scope, $"mission-progress-{operation}", cancellationToken);
        if (oldReceipt is not null)
        {
            if (Records.Require<OperationRecord>(oldReceipt.Record).Digest != digest)
                throw ApiProblems.Conflict();
            return;
        }
        var fault = progress.State is "needsAttention" or "cancelled" or "blocked";
        var owned = fault ? Records.Require<DevelopmentMissionRecord>((await store.ReadAsync(scope,
            "development-mission", cancellationToken) ?? throw ApiProblems.Forbidden()).Record)
            : await RequireMissionAsync(id, caller, development, cancellationToken);
        if (progress.MissionId != owned.Mission.Id || progress.MissionDigest != owned.Mission.Digest ||
            progress.Iterations < owned.Progress.Iterations || progress.ActiveMilliseconds < owned.Progress.ActiveMilliseconds ||
            progress.Iterations > Math.Min(owned.Mission.Workload.Validate().Workflow.MaximumIterations,
                owned.Mission.Workload.Configuration.ProductionLine!.Definition.Policy.MaximumIterations) ||
            progress.State is not ("claimed" or "executing" or "publishing" or "awaitingApproval" or "revising" or
                "awaitingMerge" or "approved" or "completed" or "blocked" or "cancelled" or "needsAttention") || progress.Reason.Length is < 1 or > 256)
            throw ApiProblems.Invalid("Mission progress cannot change its frozen identity or decrease durable counters.");
        if (owned.Progress.State == "completed" && progress.State != "completed")
            throw new ApiException(409, "mission_completed", "A completed mission cannot return to execution or review.");
        if (progress.CandidateCommit is not null)
            RepositoryWorkload.Commit(progress.CandidateCommit);
        if (progress.State is "awaitingApproval" or "awaitingMerge" or "approved" or "completed")
        {
            if (progress.PullRequest is null || progress.CandidateCommit != progress.PullRequest.CandidateCommit ||
                progress.PublicationPending ||
                progress.PullRequest.Branch != owned.Mission.Branch ||
                progress.PullRequest.BaseBranch != owned.Mission.Workload.Configuration.ProductionLine!.Definition.Source.BaseBranch ||
                progress.PullRequest.Repository != owned.Mission.Workload.Configuration.ProductionLine!.Definition.Source.Repository)
                throw ApiProblems.Invalid("Approval wait requires the exact owned publication receipt.");
            if (progress.State == "completed")
            {
                if (owned.Progress.State is not ("awaitingApproval" or "awaitingMerge" or "approved" or "completed") ||
                    owned.Progress.PullRequest != progress.PullRequest || owned.Progress.CandidateCommit != progress.CandidateCommit ||
                    owned.Mission.Workload.Validate().Workflow.CompletionCriterion != "pull_request_merged")
                    throw new ApiException(409, "completion_missing", "Completion requires the recorded verified publication and declared merge criterion.");
                var review = await repositories.ReviewAsync(owned.Mission, progress.PullRequest, cancellationToken);
                if (review.State != "merged" || review.CandidateCommit != progress.CandidateCommit)
                    throw new ApiException(409, "completion_missing", "GitHub must confirm the exact owned candidate was merged; worker assertions are insufficient.");
            }
            if (progress.State is "awaitingMerge" or "approved")
            {
                var review = await repositories.ReviewAsync(owned.Mission, progress.PullRequest, cancellationToken);
                var latest = review.Feedback.Where(item => item.CandidateCommit == progress.CandidateCommit &&
                    ReviewersFor(owned, development).Contains(item.Actor, StringComparer.OrdinalIgnoreCase) &&
                    item.Kind is "approved" or "changesRequested" or "dismissed")
                    .GroupBy(item => item.Actor, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(item => item.RecordedAt).ThenByDescending(item => item.Id, StringComparer.Ordinal).First()).ToArray();
                if (review.State != "open" || review.CandidateCommit != progress.CandidateCommit ||
                    latest.Any(item => item.Kind == "changesRequested") || !latest.Any(item => item.Kind == "approved"))
                    throw new ApiException(409, "approval_missing", "A trusted current-head approval is required; worker assertions or stale reviews are insufficient.");
            }
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var receipt = await store.ReadAsync(scope, $"mission-progress-{operation}", cancellationToken);
            if (receipt is not null)
            {
                if (Records.Require<OperationRecord>(receipt.Record).Digest != digest)
                    throw ApiProblems.Conflict();
                return;
            }
            var enrollment = fault ? await ReadAsync(id, cancellationToken) ?? throw ApiProblems.Forbidden()
                : await RequireMachineAsync(id, caller, cancellationToken);
            var stored = await store.ReadAsync(scope, "development-mission", cancellationToken) ?? throw ApiProblems.Unavailable();
            var current = Records.Require<DevelopmentMissionRecord>(stored.Record);
            if (current.Mission.Id != progress.MissionId || current.Progress != owned.Progress ||
                progress.Iterations < current.Progress.Iterations ||
                progress.ActiveMilliseconds < current.Progress.ActiveMilliseconds)
                throw ApiProblems.Conflict();
            if (await store.CommitAsync(scope, [new(enrollment.Record, enrollment.ETag),
                new(current with { Progress = progress,
                    TerminationConfirmed = current.Progress.State == "completed" && current.TerminationConfirmed }, stored.ETag),
                new(new OperationRecord { Id = $"mission-progress-{operation}", ScopeId = scope, Digest = digest,
                    RecordedAt = clock.GetUtcNow() })], cancellationToken))
                return;
        }
        throw ApiProblems.Unavailable();
    }

    public async Task ConfirmMissionTerminationAsync(string id, ApiCaller caller, MissionTerminationRequest request,
        CancellationToken cancellationToken)
    {
        var enrollment = await ReadAsync(id, cancellationToken) ?? throw ApiProblems.Forbidden();
        var identity = Records.Require<EnrollmentRecord>(enrollment.Record);
        if (caller.Principal.TenantId != api.TenantId || identity.ClientId != caller.ClientId ||
            identity.ServicePrincipalId != caller.Principal.ObjectId || !request.Confirmed)
            throw ApiProblems.Forbidden();
        var scope = Records.EnrollmentScope(id);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stored = await store.ReadAsync(scope, "development-mission", cancellationToken) ?? throw ApiProblems.Forbidden();
            var mission = Records.Require<DevelopmentMissionRecord>(stored.Record);
            if (mission.Mission.Id != request.MissionId)
                throw ApiProblems.Forbidden();
            if (mission.TerminationConfirmed)
                return;
            if (await store.CommitAsync(scope, [new(mission with { TerminationConfirmed = true }, stored.ETag)], cancellationToken))
                return;
        }
        throw ApiProblems.Unavailable();
    }
}
