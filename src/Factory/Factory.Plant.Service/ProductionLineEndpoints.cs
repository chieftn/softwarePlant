using Factory.Contracts;

namespace Factory.Plant.Service;

public static partial class PlantEndpoints
{
    private static void MapProductionLineEndpoints(WebApplication app)
    {
        var lines = app.MapGroup("/v1/production-lines").RequireRateLimiting("api").RequireAuthorization("human");
        lines.MapGet("", async (HttpContext context, PlantOperations operations, CancellationToken cancellationToken) =>
        {
            var (size, token) = PageQuery(context);
            return Results.Json(await operations.ListProductionLinesAsync(ApiCaller.Read(context.User), size, token, cancellationToken), Json);
        });
        lines.MapPost("", async (HttpContext context, PlantOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            var line = await operations.CreateProductionLineAsync(ApiCaller.Read(context.User), OperationKey(context),
                await BodyAsync<ProductionLineWriteRequest>(context, cancellationToken), cancellationToken);
            context.Response.Headers.ETag = line.Revision;
            context.Response.Headers.Location = $"/v1/production-lines/{line.Id}";
            return Results.Json(line, Json, statusCode: 201);
        });
        lines.MapGet("/{lineId}", async (string lineId, HttpContext context, PlantOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            var line = await operations.GetProductionLineAsync(PlantId(lineId), ApiCaller.Read(context.User), cancellationToken);
            context.Response.Headers.ETag = line.Revision;
            return Results.Json(line, Json);
        });
        lines.MapPut("/{lineId}", async (string lineId, HttpContext context, PlantOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            if (!context.Request.Headers.TryGetValue("If-Match", out var tags))
                throw new ApiException(428, "precondition_required", "An If-Match header is required.");
            if (tags.Count != 1 || !StrongTag().IsMatch(tags[0] ?? ""))
                throw ApiProblems.Invalid("Supply exactly one strong ETag.");
            var line = await operations.UpdateProductionLineAsync(PlantId(lineId), ApiCaller.Read(context.User),
                OperationKey(context), tags[0]!, await BodyAsync<ProductionLineWriteRequest>(context, cancellationToken), cancellationToken);
            context.Response.Headers.ETag = line.Revision;
            return Results.Json(line, Json);
        });

        var bindings = app.MapGroup("/v1/plants/{plantId}/workbenches/{workbenchId}/production-line")
            .RequireRateLimiting("api").RequireAuthorization("human");
        bindings.MapGet("", async (string plantId, string workbenchId, HttpContext context, PlantOperations operations,
            CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            return Results.Json(await operations.GetProductionLineBindingAsync(PlantId(plantId), WorkbenchId(workbenchId),
                ApiCaller.Read(context.User), cancellationToken), Json);
        });
        bindings.MapPut("", async (string plantId, string workbenchId, HttpContext context, PlantOperations operations,
            CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            return Results.Json(await operations.AssignProductionLineAsync(PlantId(plantId), WorkbenchId(workbenchId),
                ApiCaller.Read(context.User), await BodyAsync<ProductionLineAssignment>(context, cancellationToken), cancellationToken), Json);
        });
    }
}
