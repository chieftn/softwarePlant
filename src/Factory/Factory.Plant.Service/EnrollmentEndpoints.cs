using Factory.Contracts;

namespace Factory.Plant.Service;

public static partial class PlantEndpoints
{
    private static void MapEnrollmentEndpoints(RouteGroupBuilder plants)
    {
        plants.MapPost("/{plantId}/enrollments/challenge", async (string plantId, HttpContext context,
            EnrollmentOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            var request = await BodyAsync<EnrollmentChallengeRequest>(context, cancellationToken);
            return Results.Json(await operations.ChallengeAsync(PlantId(plantId), ApiCaller.Read(context.User),
                request.Draft, cancellationToken), Json);
        }).RequireAuthorization("human");

        plants.MapPost("/{plantId}/enrollments", async (string plantId, HttpContext context,
            EnrollmentOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            var key = OperationKey(context);
            var request = await BodyAsync<EnrollmentSubmission>(context, cancellationToken);
            if (request.Draft.OperationId != key)
                throw ApiProblems.Invalid("Idempotency-Key must equal draft.operationId.");
            var view = await operations.SubmitAsync(PlantId(plantId), ApiCaller.Read(context.User), request, cancellationToken);
            context.Response.Headers.Location = $"/v1/plants/{view.PlantId}/enrollments/{view.Id}";
            return Results.Json(view, Json, statusCode: 202);
        }).RequireAuthorization("human");

        plants.MapGet("/{plantId}/enrollments/{operationId}", async (string plantId, string operationId, HttpContext context,
            EnrollmentOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            return Results.Json(await operations.GetAsync(PlantId(plantId), PlantId(operationId),
                ApiCaller.Read(context.User), cancellationToken), Json);
        }).RequireAuthorization("human");

        plants.MapPost("/{plantId}/enrollments/{operationId}/activate", async (string plantId, string operationId, HttpContext context,
            EnrollmentOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            var id = PlantId(operationId);
            if (OperationKey(context) != id || context.Request.ContentLength is > 0 ||
                context.Request.Headers.ContainsKey("Transfer-Encoding"))
                throw ApiProblems.Invalid("Activation requires an empty body and its enrollment Idempotency-Key.");
            await operations.ActivateAsync(PlantId(plantId), id, ApiCaller.Read(context.User), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("workbench");
    }
}
