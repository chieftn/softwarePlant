using Factory.Contracts;

namespace Factory.Plant.Service;

public static partial class PlantEndpoints
{
    private static void MapEnrollmentEndpoints(WebApplication app)
    {
        var inventory = app.MapGroup("/v1/enrollments").RequireRateLimiting("api");
        inventory.MapGet("", async (HttpContext context, EnrollmentOperations operations, CancellationToken cancellationToken) =>
        {
            var (size, token) = PageQuery(context);
            return Results.Json(await operations.ListAsync(ApiCaller.Read(context.User), size, token, cancellationToken), Json);
        }).RequireAuthorization("enrollment-admin");
        inventory.MapPost("/challenge", async (HttpContext context, EnrollmentOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            return Results.Json(await operations.ChallengeAsync(ApiCaller.Read(context.User),
                (await BodyAsync<EnrollmentChallengeRequest>(context, cancellationToken)).Draft, cancellationToken), Json);
        }).RequireAuthorization("enrollment-admin");
        inventory.MapPost("", async (HttpContext context, EnrollmentOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            var request = await BodyAsync<EnrollmentSubmission>(context, cancellationToken);
            if (request.Draft.OperationId != OperationKey(context))
                throw ApiProblems.Invalid("Idempotency-Key must equal draft.operationId.");
            var view = await operations.SubmitAsync(ApiCaller.Read(context.User), request, cancellationToken);
            context.Response.Headers.Location = $"/v1/enrollments/{view.Id}";
            return Results.Json(view, Json, statusCode: 202);
        }).RequireAuthorization("enrollment-admin");
        inventory.MapGet("/{operationId}", async (string operationId, HttpContext context,
            EnrollmentOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            return Results.Json(await operations.GetAsync(PlantId(operationId), ApiCaller.Read(context.User), cancellationToken), Json);
        }).RequireAuthorization("enrollment-admin");
        inventory.MapPost("/{operationId}/revoke", async (string operationId, HttpContext context,
            EnrollmentOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            var id = PlantId(operationId);
            if (OperationKey(context) != id)
                throw ApiProblems.Invalid("Revocation requires its enrollment Idempotency-Key.");
            var draft = context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")
                ? await BodyAsync<EnrollmentDraft>(context, cancellationToken) : null;
            var view = await operations.RevokeAsync(id, ApiCaller.Read(context.User), cancellationToken, draft);
            return Results.Json(view, Json, statusCode: view.State == "retired" ? 200 : 202);
        }).RequireAuthorization("enrollment-admin");
        inventory.MapPut("/{operationId}/assignment", async (string operationId, HttpContext context,
            EnrollmentOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            return Results.Json(await operations.AssignAsync(PlantId(operationId), ApiCaller.Read(context.User),
                await BodyAsync<EnrollmentAssignment>(context, cancellationToken), cancellationToken), Json);
        }).RequireAuthorization("enrollment-admin");
        inventory.MapPost("/{operationId}/activate", async (string operationId, HttpContext context,
            EnrollmentOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            var id = PlantId(operationId);
            if (OperationKey(context) != id || context.Request.ContentLength is > 0 ||
                context.Request.Headers.ContainsKey("Transfer-Encoding"))
                throw ApiProblems.Invalid("Activation requires an empty body and its enrollment Idempotency-Key.");
            await operations.ActivateAsync(id, ApiCaller.Read(context.User), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("workbench");
        inventory.MapGet("/{operationId}/connection", async (string operationId, HttpContext context,
            EnrollmentOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            return Results.Json(await operations.ConnectionAsync(PlantId(operationId), ApiCaller.Read(context.User), cancellationToken), Json);
        }).RequireAuthorization("workbench");
        inventory.MapPut("/{operationId}/presence", async (string operationId, HttpContext context,
            EnrollmentOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            await operations.PresenceAsync(PlantId(operationId), ApiCaller.Read(context.User), OperationKey(context),
                await BodyAsync<WorkbenchPresence>(context, cancellationToken), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("workbench");
    }
}
