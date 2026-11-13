using System.Threading.RateLimiting;
using Azure.Core;
using Azure.Identity;
using Factory.Plant.Service.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace Factory.Plant.Service;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddOptions<ApiSettings>().BindConfiguration("Factory").PostConfigure(settings =>
        {
            settings.TenantId = settings.TenantId.ToLowerInvariant();
            settings.ClientId = settings.ClientId.ToLowerInvariant();
        });
        builder.Services.AddSingleton(services => services.GetRequiredService<IOptions<ApiSettings>>().Value);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Extensions.Remove("traceId");
            context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
        });
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddFactoryAuthentication();
        var protection = builder.Services.AddDataProtection().SetApplicationName("Factory.Plant.Service");
        if (builder.Configuration["Factory:PaginationKeyRingPath"] is { Length: > 0 } keyPath)
            protection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        builder.Services.AddSingleton<Pagination>();
        builder.Services.AddScoped<PlantOperations>();
        builder.Services.AddSingleton(serviceProvider =>
        {
            var settings = serviceProvider.GetRequiredService<ApiSettings>();
            TokenCredential credential = settings.DataCredential switch
            {
                "ManagedIdentity" => new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.DataIdentityClientId)),
                "AzureCli" when builder.Environment.IsDevelopment() => new AzureCliCredential(new AzureCliCredentialOptions { TenantId = settings.TenantId }),
                _ => throw new InvalidOperationException("An explicit supported data credential is required.")
            };
            return new CosmosClient(settings.CosmosEndpoint, credential, new CosmosClientOptions
            {
                Serializer = new RecordSerializer(),
                ConnectionMode = ConnectionMode.Gateway,
                ApplicationName = "Factory.Plant.Service",
                MaxRetryAttemptsOnRateLimitedRequests = 2,
                MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(5),
                RequestTimeout = TimeSpan.FromSeconds(10)
            });
        });
        builder.Services.AddSingleton<IRecordStore>(services =>
        {
            var settings = services.GetRequiredService<ApiSettings>();
            return new CosmosRecordStore(services.GetRequiredService<CosmosClient>()
                .GetContainer(settings.CosmosDatabase, settings.CosmosContainer));
        });
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = PlantEndpoints.MaximumBodySize);
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy("api", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.Identity?.IsAuthenticated == true
                    ? ApiCaller.Read(context.User).Key : context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                }));
            options.OnRejected = (context, _) => new ValueTask(ApiProblems.WriteAsync(context.HttpContext,
                429, "rate_limited", "The request rate limit was exceeded.",
                context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay)
                    ? Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)) : 60));
        });

        var app = builder.Build();
        var configurationErrors = app.Services.GetRequiredService<ApiSettings>().Validate(app.Environment);
        app.UseExceptionHandler();
        app.UseStatusCodePages(context => ApiProblems.WriteAsync(context.HttpContext, context.HttpContext.Response.StatusCode,
            "not_found", "No response is available for this request."));
        if (configurationErrors.Count != 0)
            app.Logger.LogWarning("Plant API disabled until configuration is supplied or corrected: {Fields}",
                string.Join(", ", configurationErrors));
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/v1"))
            {
                context.Response.Headers.CacheControl = "no-store";
                if (configurationErrors.Count != 0)
                {
                    await ApiProblems.WriteAsync(context, 503, "service_unavailable", "Plant API configuration is incomplete.", 5);
                    return;
                }
            }
            await next(context);
        });
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();

        app.MapGet("/", () => Results.Content(
            """
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Factory</title>
            </head>
            <body>
              <main>
                <h1>Factory</h1>
                <p>The plant service foundation is running.</p>
                <p>Web sign-in and plant management pages are not available yet.</p>
              </main>
            </body>
            </html>
            """,
            "text/html; charset=utf-8"));

        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
        app.MapPlantEndpoints();

        app.Run();
    }
}
