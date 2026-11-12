using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Workbench.Contracts;

namespace Workbench.Distributor;

public static class DistributorApplication
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 8 * 1024 * 1024);
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.UnmappedMemberHandling = Wire.Options.UnmappedMemberHandling);
        var adminToken = builder.Configuration["Distributor:AdminToken"]
            ?? throw new InvalidOperationException("Distributor:AdminToken is required.");
        var workbenches = builder.Configuration.GetSection("Distributor:Workbenches")
            .GetChildren().ToDictionary(item => item.Key, item => item.Value
                ?? throw new InvalidOperationException("Each workbench needs a token."), StringComparer.Ordinal);
        var tokens = workbenches.Values.Append(adminToken).ToArray();
        if (workbenches.Count == 0 || tokens.Any(token => token.Length < 32) ||
            tokens.Distinct(StringComparer.Ordinal).Count() != tokens.Length)
            throw new InvalidOperationException("Configure distinct tokens of at least 32 characters for the operator and each workbench.");
        foreach (var id in workbenches.Keys)
            DispatchRequest.ValidateWorkbenchId(id);
        var store = new AssignmentStore(builder.Configuration["Distributor:Database"] ?? "data/distributor.db");
        builder.Services.AddSingleton(store);
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/healthz")
            {
                await next(context);
                return;
            }
            var authorization = context.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 401;
                return;
            }
            var token = authorization["Bearer ".Length..];
            var isAdmin = Matches(token, adminToken);
            var workbench = workbenches.FirstOrDefault(item => Matches(token, item.Value)).Key;
            if (!isAdmin && workbench is null)
            {
                context.Response.StatusCode = 401;
                return;
            }
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, isAdmin ? "operator" : workbench!),
                    new Claim(ClaimTypes.Role, isAdmin ? "operator" : "workbench")], "Bearer"));
            try
            {
                await next(context);
            }
            catch (Exception error) when (error is InvalidDataException or JsonException or DistributionConflictException)
            {
                app.Logger.LogWarning("Distribution request rejected: {Message}", error.Message);
                context.Response.StatusCode = error is DistributionConflictException ? 409 : 400;
                await context.Response.WriteAsJsonAsync(new { error = error.Message });
            }
        });

        app.MapGet("/healthz", () => Results.Ok(new { status = "ready" }));
        app.MapPost("/assignments", (DispatchRequest dispatch, HttpContext context) =>
        {
            if (!context.User.IsInRole("operator"))
                return Results.StatusCode(403);
            dispatch.Validate();
            if (!workbenches.ContainsKey(dispatch.WorkbenchId))
                throw new InvalidDataException("Unknown target workbench.");
            store.Submit(dispatch);
            return Results.Created($"/assignments/{dispatch.Request.Assignment.Id}",
                store.Get(dispatch.Request.Assignment.Id));
        });
        app.MapGet("/assignments/{id}", (string id, HttpContext context) =>
        {
            var state = store.Get(id);
            if (state is null)
                return Results.NotFound();
            if (!context.User.IsInRole("operator") && context.User.Identity?.Name != state.WorkbenchId)
                return Results.StatusCode(403);
            return Results.Ok(state);
        });
        app.MapPost("/workbenches/{workbench}/claim", (string workbench, ClaimRequest request, HttpContext context) =>
        {
            if (!IsWorkbench(context, workbench))
                return Results.StatusCode(403);
            var claim = store.Claim(workbench, request.ClaimId);
            return claim is null ? Results.NoContent() : Results.Ok(claim);
        });
        app.MapPost("/workbenches/{workbench}/assignments/{id}/{claim}/heartbeat",
            (string workbench, string id, string claim, HttpContext context) =>
            {
                if (!IsWorkbench(context, workbench))
                    return Results.StatusCode(403);
                store.Heartbeat(id, workbench, claim);
                return Results.NoContent();
            });
        app.MapPost("/workbenches/{workbench}/assignments/{id}/{claim}/result",
            (string workbench, string id, string claim, WorkerResult result, HttpContext context) =>
            {
                if (!IsWorkbench(context, workbench))
                    return Results.StatusCode(403);
                store.Complete(id, workbench, claim, result);
                return Results.NoContent();
            });
        app.MapPost("/assignments/{id}/resolve", (string id, WorkerResult result, HttpContext context) =>
        {
            if (!context.User.IsInRole("operator"))
                return Results.StatusCode(403);
            var state = store.Get(id);
            if (state is null)
                return Results.NotFound();
            store.Complete(id, state.WorkbenchId, null, result);
            app.Logger.LogWarning("Operator resolved assignment {AssignmentId} as {Status}", id, result.Status);
            return Results.NoContent();
        });
        return app;
    }

    private static bool IsWorkbench(HttpContext context, string id) =>
        context.User.IsInRole("workbench") && context.User.Identity?.Name == id;

    private static bool Matches(string token, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
