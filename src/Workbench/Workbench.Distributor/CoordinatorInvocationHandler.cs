using System.Text.Json;
using Azure.AI.AgentServer.Core.Storage;
using Azure.AI.AgentServer.Invocations;
using Workbench.Contracts;

namespace Workbench.Distributor;

public sealed class CoordinatorInvocationHandler(
    CoordinatorAccess access, CoordinatorStore store, ILogger<CoordinatorInvocationHandler> logger) : InvocationHandler
{
    public override async Task HandleAsync(HttpRequest request, HttpResponse response,
        InvocationContext context, CancellationToken cancellationToken)
    {
        try
        {
            var caller = access.Authenticate(request);
            var input = await request.ReadFromJsonAsync<CoordinatorInvocation>(Wire.Options, cancellationToken)
                ?? throw new InvalidDataException("An invocation is required.");
            Assignment.Require(input.SchemaVersion == "0.1", "Unsupported invocation schemaVersion.");
            var reply = await InvokeAsync(input, caller, cancellationToken);
            await response.WriteAsJsonAsync(reply, Wire.Options, cancellationToken);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or
            DistributionConflictException or UnauthorizedAccessException or KeyNotFoundException)
        {
            response.StatusCode = error switch
            {
                UnauthorizedAccessException => StatusCodes.Status403Forbidden,
                KeyNotFoundException => StatusCodes.Status404NotFound,
                DistributionConflictException => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest
            };
            logger.LogWarning("Coordinator invocation rejected: {Message}", error.Message);
            await response.WriteAsJsonAsync(new { error = error.Message }, cancellationToken);
        }
        catch (FoundryStorageException error)
        {
            logger.LogError(error, "Coordinator persistence is unavailable.");
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await response.WriteAsJsonAsync(new { error = "Coordinator persistence unavailable; retry the same operation identity." }, cancellationToken);
        }
    }

    private async Task<CoordinatorReply> InvokeAsync(CoordinatorInvocation input, CoordinatorCaller caller,
        CancellationToken cancellationToken)
    {
        string Workbench()
        {
            var id = Required(input.WorkbenchId, "workbenchId");
            DispatchRequest.ValidateWorkbenchId(id);
            if (caller.WorkbenchId != id)
                throw new UnauthorizedAccessException("This operation requires the owning workbench identity.");
            return id;
        }
        void Operator()
        {
            if (!caller.IsOperator)
                throw new UnauthorizedAccessException("This operation requires an operator.");
        }
        async Task<AssignmentState> VisibleAssignment()
        {
            var id = Required(input.AssignmentId, "assignmentId");
            var assignment = await store.GetAsync(id, cancellationToken) ?? throw new KeyNotFoundException("Unknown assignment.");
            if (!caller.IsOperator && caller.WorkbenchId != assignment.WorkbenchId)
                throw new UnauthorizedAccessException("Assignment belongs to another workbench.");
            return assignment;
        }

        switch (input.Operation)
        {
            case "identity":
                return new() { PrincipalId = caller.PrincipalId };
            case "register":
                return new()
                {
                    Workbench = await store.RegisterAsync(Workbench(), caller.PrincipalId,
                        input.Registration ?? throw new InvalidDataException("registration is required."), cancellationToken)
                };
            case "workbenches":
                Operator();
                return new() { Workbenches = await store.WorkbenchesAsync(cancellationToken) };
            case "submit":
                Operator();
                var dispatch = input.Dispatch ?? throw new InvalidDataException("dispatch is required.");
                dispatch.Validate();
                if (!access.KnowsWorkbench(dispatch.WorkbenchId))
                    throw new InvalidDataException("Unknown target workbench.");
                await store.SubmitAsync(dispatch, cancellationToken);
                return new() { Assignment = await store.GetAsync(dispatch.Request.Assignment.Id, cancellationToken) };
            case "get":
                return new() { Assignment = await VisibleAssignment() };
            case "claim":
                return new()
                {
                    Claim = await store.ClaimAsync(Workbench(), caller.PrincipalId,
                        Required(input.ClaimId, "claimId"), cancellationToken)
                };
            case "heartbeat":
                await store.HeartbeatAsync(Required(input.AssignmentId, "assignmentId"), Workbench(), caller.PrincipalId,
                    Required(input.ClaimId, "claimId"), cancellationToken);
                return new();
            case "result":
                await store.CompleteAsync(Required(input.AssignmentId, "assignmentId"), Workbench(), caller.PrincipalId,
                    Required(input.ClaimId, "claimId"),
                    input.Result ?? throw new InvalidDataException("result is required."), cancellationToken);
                return new();
            case "resolve":
                Operator();
                var state = await VisibleAssignment();
                await store.CompleteAsync(state.AssignmentId, state.WorkbenchId, null, null,
                    input.Result ?? throw new InvalidDataException("result is required."), cancellationToken);
                logger.LogWarning("Operator {Principal} resolved assignment {Assignment}", caller.PrincipalId, state.AssignmentId);
                return new();
            default:
                throw new InvalidDataException("Unknown coordinator operation.");
        }
    }

    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException(name + " is required.");
}
