using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace Factory.Contracts;

public sealed record DevelopmentIssue(long Number, string Repository, string Title, string Body,
    string[] AcceptanceCriteria, string[] Labels, DateTimeOffset UpdatedAt)
{
    public void Validate(ProductionLineTasks tasks)
    {
        ContractValidation.Require(Number > 0 && Repository == tasks.Repository, "Issue must belong to the assigned task repository.");
        ContractValidation.Text(Title, 256, "issue.title");
        ContractValidation.Text(Body, 16 * 1024, "issue.body");
        ContractValidation.Require(AcceptanceCriteria is { Length: > 0 and <= 64 } &&
            AcceptanceCriteria.Distinct(StringComparer.Ordinal).Count() == AcceptanceCriteria.Length,
            "Frozen issue must have distinct explicit acceptance criteria.");
        foreach (var criterion in AcceptanceCriteria!)
            ContractValidation.Text(criterion, 1024, "acceptance criterion");
        ContractValidation.Require(Labels is not null && tasks.RequiredLabels.All(label =>
            Labels.Contains(label, StringComparer.OrdinalIgnoreCase)), "Issue is missing required approval/selection labels.");
    }
}

public sealed record DevelopmentMission(string SchemaVersion, string Id, ResolvedRepositoryWorkload Workload,
    DevelopmentIssue Issue, string Model, DateTimeOffset CreatedAt, DateTimeOffset ApprovalDeadline, string Digest)
{
    public string Branch => $"factory/mission-{Id}";

    public static DevelopmentMission Create(string id, ResolvedRepositoryWorkload workload, DevelopmentIssue issue,
        string model, DateTimeOffset now, DateTimeOffset approvalDeadline) =>
        new("1", id, workload, issue, model, now, approvalDeadline, ComputeDigest(id, workload, issue, model, now, approvalDeadline));

    public void Validate()
    {
        ContractValidation.Require(SchemaVersion == "1" && Guid.TryParseExact(Id, "D", out var id) && id != Guid.Empty,
            "A development mission requires an immutable UUID.");
        var definition = Workload.Validate();
        ContractValidation.Text(Model, 128, "mission model");
        Issue.Validate(Workload.Configuration.ProductionLine!.Definition.Tasks);
        ContractValidation.Require(definition.Verification.Commands.Length > 0,
            "Project verification commands are required before claiming execution.");
        ContractValidation.Require(ApprovalDeadline > CreatedAt && ApprovalDeadline <= CreatedAt.AddDays(7),
            "Human approval wait must be bounded to at most seven days.");
        ContractValidation.Require(Digest == ComputeDigest(Id, Workload, Issue, Model, CreatedAt, ApprovalDeadline),
            "Mission digest does not match its frozen inputs.");
        ContractValidation.Require(JsonSerializer.SerializeToUtf8Bytes(this, RepositoryWorkload.Json).Length <= 96 * 1024,
            "Frozen mission snapshot exceeds its transport bound.");
    }

    private static string ComputeDigest(string id, ResolvedRepositoryWorkload workload, DevelopmentIssue issue, string model,
        DateTimeOffset createdAt, DateTimeOffset approvalDeadline) => Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new { id, workload, issue, model, createdAt, approvalDeadline }, RepositoryWorkload.Json)));
}

public sealed record CandidateVerification(string CandidateCommit, WorkloadCommand Command,
    int ExitCode, string OutputDigest, long DurationMilliseconds);
public sealed record AlignmentCoverage(string Criterion, string Evidence);
public sealed record AlignmentFinding(string Id, bool Blocking, string Location, string Reason, string RequestedCorrection);
public sealed record CandidateAlignment(string CandidateCommit, string Outcome, AlignmentCoverage[] Coverage,
    AlignmentFinding[] Findings, string[] Blockers)
{
    public void Validate(DevelopmentMission mission, string candidate)
    {
        ContractValidation.Require(CandidateCommit == candidate && Outcome is "pass" or "revise" or "blocked",
            "Alignment must assess the exact candidate with a supported outcome.");
        ContractValidation.Require(Coverage is not null && Coverage.Length == mission.Issue.AcceptanceCriteria.Length &&
            Coverage.All(item => item is not null) &&
            Coverage.Select(item => item.Criterion).Distinct(StringComparer.Ordinal).Count() == Coverage.Length &&
            Coverage.Select(item => item.Criterion).Order(StringComparer.Ordinal)
                .SequenceEqual(mission.Issue.AcceptanceCriteria.Order(StringComparer.Ordinal)),
            "Alignment coverage must include each frozen acceptance criterion exactly once.");
        foreach (var item in Coverage!)
            ContractValidation.Text(item.Evidence, 4096, "coverage evidence");
        ContractValidation.Require(Findings is { Length: <= 32 } && Blockers is { Length: <= 32 },
            "Alignment findings and blockers must be bounded arrays.");
        foreach (var finding in Findings!)
        {
            ContractValidation.Require(finding is not null, "Alignment findings cannot be null.");
            ContractValidation.Text(finding!.Id, 64, "finding id");
            ContractValidation.Text(finding.Location, 512, "finding location");
            ContractValidation.Text(finding.Reason, 1024, "finding reason");
            ContractValidation.Text(finding.RequestedCorrection, 1024, "requested correction");
        }
        ContractValidation.Require(Findings.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == Findings.Length,
            "Finding identifiers must be distinct.");
        foreach (var blocker in Blockers!)
            ContractValidation.Text(blocker, 1024, "alignment blocker");
        ContractValidation.Require(Outcome != "pass" || !Findings.Any(item => item.Blocking) && Blockers.Length == 0,
            "Alignment cannot pass with blockers or blocking findings.");
    }
}

public sealed record PublishedDevelopmentPullRequest(long Number, string Repository, string Branch,
    string BaseBranch, string CandidateCommit, string Url);

public sealed record DevelopmentReviewFeedback(string Id, string Actor, string CandidateCommit,
    string Kind, string Body, DateTimeOffset RecordedAt);

public sealed record DevelopmentReview(string CandidateCommit, string State, DevelopmentReviewFeedback[] Feedback);

public sealed record RepositoryAccessCredential(string Repository, string Token, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"RepositoryAccessCredential ({Repository}; token redacted)";
}

public sealed record DevelopmentClaimRequest(string OperationId, WorkloadRequest Configuration,
    string WorkloadDigest, long IssueNumber, string Model);

public sealed record DevelopmentMissionState(DevelopmentMission Mission, string State, int Iterations,
    long ActiveMilliseconds, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CandidateCommit, CandidateVerification[] Verification,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] CandidateAlignment? Alignment,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PublishedDevelopmentPullRequest? PullRequest, string[] ProcessedFeedback,
    DevelopmentReviewFeedback[] PendingFeedback, string Reason)
{
    public bool PublicationPending { get; init; }
    public static DevelopmentMissionState Claimed(DevelopmentMission mission) =>
        new(mission, "claimed", 0, 0, null, [], null, null, [], [], "Approved issue claimed; execution has not started.");
}

public sealed record DevelopmentMissionProgress
{
    public required string MissionId { get; init; }
    public required string MissionDigest { get; init; }
    public required string State { get; init; }
    public required int Iterations { get; init; }
    public required long ActiveMilliseconds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CandidateCommit { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublishedDevelopmentPullRequest? PullRequest { get; init; }
    public required bool PublicationPending { get; init; }
    public required string Reason { get; init; }

    [JsonConstructor]
    public DevelopmentMissionProgress() { }

    [SetsRequiredMembers]
    public DevelopmentMissionProgress(string missionId, string missionDigest, string state, int iterations,
        long activeMilliseconds, string? candidateCommit, PublishedDevelopmentPullRequest? pullRequest,
        bool publicationPending, string reason)
    {
        MissionId = missionId;
        MissionDigest = missionDigest;
        State = state;
        Iterations = iterations;
        ActiveMilliseconds = activeMilliseconds;
        CandidateCommit = candidateCommit;
        PullRequest = pullRequest;
        PublicationPending = publicationPending;
        Reason = reason;
    }
}

public sealed record WorkerMissionRequest(DevelopmentMission Mission, bool Enabled, string Model, string ModelProvider,
    bool AllowShellExecution, bool AllowPublication, string[] AllowedRepositories, string GitAuthorName, string GitAuthorEmail,
    string[] Reviewers)
{
    public void Validate()
    {
        Mission.Validate();
        if (!Enabled || ModelProvider != "github-copilot" || !AllowShellExecution || !AllowPublication ||
            AllowedRepositories is null || !AllowedRepositories.Contains(
                Mission.Workload.Configuration.ProductionLine!.Definition.Source.Repository, StringComparer.OrdinalIgnoreCase) ||
            !AllowedRepositories.Contains(Mission.Workload.Configuration.ProductionLine.Definition.Tasks.Repository, StringComparer.OrdinalIgnoreCase) ||
            Model != Mission.Model)
            throw new ContractValidationException("Worker execution/model/source/publication require explicit local permission and credentials.");
        ContractValidation.Text(Model, 128, "model");
        ContractValidation.Text(GitAuthorName, 128, "Git author name");
        ContractValidation.Text(GitAuthorEmail, 254, "Git author email");
        if (Reviewers is not { Length: > 0 and <= 32 } || Reviewers.Any(string.IsNullOrWhiteSpace))
            throw new ContractValidationException("Trusted reviewer identities must be configured.");
    }

    public override string ToString() => $"WorkerMissionRequest ({Mission.Id}; credentials redacted)";
}

public sealed record DevelopmentMissionDelivery(DevelopmentMission Mission, string State, string[] Reviewers)
{
    public bool PublicationPending { get; init; }
}
public sealed record MissionCandidateRequest(string CandidateCommit);
public sealed record MissionCredentialRequest(string Purpose);
public sealed record MissionTerminationRequest(string MissionId, bool Confirmed);
public sealed record WorkerPipeMessage(string Type, string Id, string Operation, JsonElement Payload);
public sealed record OwnedLinuxWorker(int ProcessId, int ProcessGroupId, string InstanceId, string Nonce, string MissionId);
