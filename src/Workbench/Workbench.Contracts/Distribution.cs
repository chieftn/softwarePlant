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
