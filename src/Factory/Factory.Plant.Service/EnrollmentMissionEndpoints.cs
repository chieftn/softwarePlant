using Factory.Contracts;

namespace Factory.Plant.Service;

public static partial class PlantEndpoints
{
    private static void MapMissionEndpoints(RouteGroupBuilder inventory)
    {
        var missions = inventory.MapGroup("/{operationId}/mission").RequireAuthorization("workbench")
            .AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                return await next(context);
            });
        missions.MapPost("", async (string operationId, HttpContext context, EnrollmentOperations operations,
            DevelopmentMissionSettings development, IGitHubRepositoryReader workloads,
            IGitHubDevelopmentRepository repositories, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            context.Response.Headers.CacheControl = "no-store";
            var request = await BodyAsync<DevelopmentClaimRequest>(context, cancellationToken);
            if (OperationKey(context) != request.OperationId)
                throw ApiProblems.Invalid("Claim Idempotency-Key must equal the immutable mission operation ID.");
            return Results.Json(await operations.ClaimMissionAsync(PlantId(operationId), ApiCaller.Read(context.User),
                request, development, workloads, repositories, cancellationToken), Json);
        });
        missions.MapGet("", async (string operationId, HttpContext context, EnrollmentOperations operations,
            DevelopmentMissionSettings development, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(await operations.MissionAsync(PlantId(operationId), ApiCaller.Read(context.User),
                development, cancellationToken), Json);
        });
        missions.MapPost("/authorize", async (string operationId, HttpContext context, EnrollmentOperations operations,
            DevelopmentMissionSettings development, IGitHubDevelopmentRepository repositories, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            await operations.AuthorizeMissionAsync(PlantId(operationId), ApiCaller.Read(context.User),
                development, repositories, cancellationToken);
            return Results.NoContent();
        });
        missions.MapPost("/credential", async (string operationId, HttpContext context, EnrollmentOperations operations,
            DevelopmentMissionSettings development, IGitHubDevelopmentRepository repositories, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(await operations.MissionCredentialAsync(PlantId(operationId), ApiCaller.Read(context.User),
                (await BodyAsync<MissionCredentialRequest>(context, cancellationToken)).Purpose,
                development, repositories, cancellationToken), Json);
        });
        missions.MapPost("/publish", async (string operationId, HttpContext context, EnrollmentOperations operations,
            DevelopmentMissionSettings development, IGitHubDevelopmentRepository repositories, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            return Results.Json(await operations.PublishMissionAsync(PlantId(operationId), ApiCaller.Read(context.User),
                (await BodyAsync<MissionCandidateRequest>(context, cancellationToken)).CandidateCommit, false,
                development, repositories, cancellationToken), Json);
        });
        missions.MapPost("/published", async (string operationId, HttpContext context, EnrollmentOperations operations,
            DevelopmentMissionSettings development, IGitHubDevelopmentRepository repositories, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            return Results.Json(await operations.PublishMissionAsync(PlantId(operationId), ApiCaller.Read(context.User),
                (await BodyAsync<MissionCandidateRequest>(context, cancellationToken)).CandidateCommit, true,
                development, repositories, cancellationToken), Json);
        });
        missions.MapGet("/review", async (string operationId, HttpContext context, EnrollmentOperations operations,
            DevelopmentMissionSettings development, IGitHubDevelopmentRepository repositories, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            return Results.Json(await operations.ReviewMissionAsync(PlantId(operationId), ApiCaller.Read(context.User),
                development, repositories, cancellationToken), Json);
        });
        missions.MapPut("/progress", async (string operationId, HttpContext context, EnrollmentOperations operations,
            DevelopmentMissionSettings development, IGitHubDevelopmentRepository repositories, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            await operations.ReportMissionAsync(PlantId(operationId), ApiCaller.Read(context.User), OperationKey(context),
                await BodyAsync<DevelopmentMissionProgress>(context, cancellationToken), development, repositories, cancellationToken);
            return Results.NoContent();
        });
        missions.MapPost("/terminated", async (string operationId, HttpContext context, EnrollmentOperations operations,
            CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            await operations.ConfirmMissionTerminationAsync(PlantId(operationId), ApiCaller.Read(context.User),
                await BodyAsync<MissionTerminationRequest>(context, cancellationToken), cancellationToken);
            return Results.NoContent();
        });
    }
}
