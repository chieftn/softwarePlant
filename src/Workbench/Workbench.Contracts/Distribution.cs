namespace Workbench.Contracts;

public sealed record DispatchRequest
{
    public required string WorkbenchId { get; init; }
    public required WorkerRequest Request { get; init; }

    public void Validate()
    {
        ValidateWorkbenchId(WorkbenchId);
        Assignment.Require(Request is not null, "A worker request is required.");
        Request!.Validate();
    }

    public static void ValidateWorkbenchId(string id) =>
        Assignment.Require(id is not null &&
            System.Text.RegularExpressions.Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9-]{0,62}\z"),
            "Workbench IDs use lowercase letters, digits and hyphens, up to 63 characters.");
}

public sealed record WorkClaim(string ClaimId, DispatchRequest Dispatch);
public sealed record ClaimRequest(string ClaimId);

public sealed record AssignmentState(
    string AssignmentId, string WorkbenchId, string Status,
    DateTimeOffset SubmittedAt, DateTimeOffset? LastHeartbeat, WorkerResult? Result);

public sealed record WorkbenchRegistration(string MachineName, string[] Capabilities)
{
    public void Validate()
    {
        Assignment.Require(!string.IsNullOrWhiteSpace(MachineName) && MachineName.Length <= 128,
            "A machine name of up to 128 characters is required.");
        Assignment.Require(Capabilities is { Length: > 0 and <= 32 } &&
            Capabilities.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 128) &&
            Capabilities.Distinct(StringComparer.Ordinal).Count() == Capabilities.Length,
            "Supply up to 32 distinct, nonempty capabilities of up to 128 characters.");
    }
}

public sealed record WorkbenchState(string WorkbenchId, string PrincipalId,
    WorkbenchRegistration Registration, DateTimeOffset RegisteredAt, DateTimeOffset LastSeen,
    string Presence, string? ActiveAssignmentId);

public sealed record CoordinatorInvocation
{
    public string SchemaVersion { get; init; } = "0.1";
    public required string Operation { get; init; }
    public string? WorkbenchId { get; init; }
    public string? AssignmentId { get; init; }
    public string? ClaimId { get; init; }
    public DispatchRequest? Dispatch { get; init; }
    public WorkerResult? Result { get; init; }
    public WorkbenchRegistration? Registration { get; init; }
}

public sealed record CoordinatorReply
{
    public string? PrincipalId { get; init; }
    public AssignmentState? Assignment { get; init; }
    public WorkClaim? Claim { get; init; }
    public WorkbenchState? Workbench { get; init; }
    public WorkbenchState[]? Workbenches { get; init; }
}
