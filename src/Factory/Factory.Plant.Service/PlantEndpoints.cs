using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Factory.Contracts;

namespace Factory.Plant.Service;

public static partial class PlantEndpoints
{
    public const long MaximumBodySize = 128 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32
    };

    public static void MapPlantEndpoints(this WebApplication app)
    {
        var plants = app.MapGroup("/v1/plants").RequireRateLimiting("api");
        MapEnrollmentEndpoints(plants);
        plants.MapGet("", async (HttpContext context, PlantOperations operations, CancellationToken cancellationToken) =>
        {
            var (size, token) = PageQuery(context);
            return Results.Json(await operations.ListPlantsAsync(ApiCaller.Read(context.User), size, token, cancellationToken), Json);
        }).RequireAuthorization("human");

        plants.MapPost("", async (HttpContext context, PlantOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            var key = OperationKey(context);
            var request = await BodyAsync<PlantWriteRequest>(context, cancellationToken);
            var result = await operations.CreatePlantAsync(ApiCaller.Read(context.User), key, request, cancellationToken);
            context.Response.Headers.ETag = result.Revision;
            context.Response.Headers.Location = $"/v1/plants/{result.Plant.Id}";
            return Results.Json(result.Plant, Json, statusCode: 201);
        }).RequireAuthorization("creator");

        plants.MapGet("/{plantId}", async (string plantId, HttpContext context, PlantOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            var result = await operations.GetPlantAsync(PlantId(plantId), ApiCaller.Read(context.User), cancellationToken);
            context.Response.Headers.ETag = result.Revision;
            return Results.Json(result.Plant, Json);
        }).RequireAuthorization("human");

        plants.MapPut("/{plantId}", async (string plantId, HttpContext context, PlantOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            var id = PlantId(plantId);
            var key = OperationKey(context);
            if (!context.Request.Headers.TryGetValue("If-Match", out var tags))
                throw new ApiException(428, "precondition_required", "An If-Match header is required.");
            if (tags.Count != 1 || !StrongTag().IsMatch(tags[0] ?? ""))
                throw ApiProblems.Invalid("Supply exactly one strong ETag; wildcard and weak tags are not accepted.");
            var result = await operations.UpdatePlantAsync(id, ApiCaller.Read(context.User), key, tags[0]!,
                await BodyAsync<PlantWriteRequest>(context, cancellationToken), cancellationToken);
            context.Response.Headers.ETag = result.Revision;
            return Results.Json(result.Plant, Json);
        }).RequireAuthorization("human");

        plants.MapGet("/{plantId}/workbenches", async (string plantId, HttpContext context, PlantOperations operations,
            CancellationToken cancellationToken) =>
        {
            var (size, token) = PageQuery(context);
            return Results.Json(await operations.ListWorkbenchesAsync(PlantId(plantId), ApiCaller.Read(context.User),
                size, token, cancellationToken), Json);
        }).RequireAuthorization("human");

        plants.MapGet("/{plantId}/workbenches/{workbenchId}", async (string plantId, string workbenchId, HttpContext context,
            PlantOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            return Results.Json(await operations.GetWorkbenchAsync(PlantId(plantId), WorkbenchId(workbenchId),
                ApiCaller.Read(context.User), cancellationToken), Json);
        }).RequireAuthorization("human");

        plants.MapPut("/{plantId}/workbenches/{workbenchId}/registration", async (string plantId, string workbenchId,
            HttpContext context, PlantOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            var id = PlantId(plantId);
            var workbench = WorkbenchId(workbenchId);
            var key = OperationKey(context);
            await operations.RegisterAsync(id, workbench, ApiCaller.Read(context.User), key,
                await BodyAsync<WorkbenchRegistration>(context, cancellationToken), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("workbench");

        plants.MapPut("/{plantId}/workbenches/{workbenchId}/presence", async (string plantId, string workbenchId,
            HttpContext context, PlantOperations operations, CancellationToken cancellationToken) =>
        {
            NoQuery(context);
            var id = PlantId(plantId);
            var workbench = WorkbenchId(workbenchId);
            var key = OperationKey(context);
            await operations.PresenceAsync(id, workbench, ApiCaller.Read(context.User), key,
                await BodyAsync<WorkbenchPresence>(context, cancellationToken), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("workbench");
    }

    private static async Task<T> BodyAsync<T>(HttpContext context, CancellationToken cancellationToken) where T : class
    {
        if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType) ||
            !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(415, "unsupported_media_type", "The request body must be application/json.");
        if (context.Request.ContentLength > MaximumBodySize)
            throw new ApiException(413, "request_too_large", "The request body exceeds the allowed size.");
        using var document = await JsonDocument.ParseAsync(context.Request.Body,
            new JsonDocumentOptions { MaxDepth = 32 }, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.EnumerateObject().Any(property => property.Value.ValueKind == JsonValueKind.Null))
            throw ApiProblems.Invalid("Supply a JSON object without null properties.");
        return document.Deserialize<T>(Json) ?? throw ApiProblems.Invalid("A JSON request body is required.");
    }

    internal static (int Size, string? Token) PageQuery(HttpContext context)
    {
        if (context.Request.Query.Keys.Any(key => key is not ("pageSize" or "continuationToken")))
            throw ApiProblems.Invalid("Unsupported query parameter.");
        var size = 50;
        if (context.Request.Query.TryGetValue("pageSize", out var values) &&
            (values.Count != 1 || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out size) ||
                size is < 1 or > 100))
            throw ApiProblems.Invalid("pageSize must be between 1 and 100.");
        string? token = null;
        if (context.Request.Query.TryGetValue("continuationToken", out var tokens))
        {
            if (tokens.Count != 1 || string.IsNullOrEmpty(tokens[0]))
                throw ApiProblems.Invalid("Supply one nonempty continuationToken.");
            token = tokens[0];
        }
        return (size, token);
    }

    private static void NoQuery(HttpContext context)
    {
        if (context.Request.Query.Count != 0)
            throw ApiProblems.Invalid("This operation does not accept query parameters.");
    }

    private static string OperationKey(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("Idempotency-Key", out var values) || values.Count != 1 ||
            !Guid.TryParseExact(values[0], "D", out var key))
            throw ApiProblems.Invalid("Supply one Idempotency-Key UUID.");
        return key.ToString("D");
    }

    internal static string PlantId(string value) => Guid.TryParseExact(value, "D", out var id)
        ? id.ToString("D") : throw ApiProblems.Invalid("plantId must be a UUID.");
    internal static string WorkbenchId(string value) => WorkbenchPattern().IsMatch(value)
        ? value : throw ApiProblems.Invalid("workbenchId must contain lowercase letters, digits and hyphens, up to 63 characters.");

    [GeneratedRegex("\\A[a-z0-9][a-z0-9-]{0,62}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex WorkbenchPattern();

    [GeneratedRegex("\\A\"[\\x21\\x23-\\x7E]+\"\\z", RegexOptions.CultureInvariant)]
    private static partial Regex StrongTag();
}
