using System.Threading.RateLimiting;
using Azure.Core;
using Azure.Identity;
using Factory.Plant.Service.Storage;
using Factory.Plant.Service.Web;
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
        var websiteEnabled = builder.Services.AddFactoryWebsite(builder.Configuration, builder.Environment, protection);
        builder.Services.AddSingleton<Pagination>();
        builder.Services.AddScoped<PlantOperations>();
        builder.Services.AddOptions<EnrollmentSettings>().BindConfiguration("Factory:Enrollment");
        builder.Services.AddSingleton(services => services.GetRequiredService<IOptions<EnrollmentSettings>>().Value);
        builder.Services.AddScoped<EnrollmentOperations>();
        builder.Services.AddScoped<EnrollmentProcessor>();
        builder.Services.AddSingleton<IDirectoryProvisioner, GraphDirectoryProvisioner>();
        builder.Services.AddHostedService<EnrollmentWorker>();
        builder.Services.AddSingleton<TokenCredential>(serviceProvider =>
        {
            var settings = serviceProvider.GetRequiredService<ApiSettings>();
            return settings.DataCredential switch
            {
                "ManagedIdentity" => new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.DataIdentityClientId)),
                "AzureCli" when builder.Environment.IsDevelopment() => new AzureCliCredential(new AzureCliCredentialOptions { TenantId = settings.TenantId }),
                "ClientSecret" when builder.Environment.IsDevelopment() => new ClientSecretCredential(
                    settings.TenantId, settings.ClientId, settings.ClientSecret),
                _ => throw new InvalidOperationException("An explicit supported data credential is required.")
            };
        });
        builder.Services.AddSingleton(serviceProvider =>
        {
            var settings = serviceProvider.GetRequiredService<ApiSettings>();
            return new CosmosClient(settings.CosmosEndpoint, serviceProvider.GetRequiredService<TokenCredential>(), new CosmosClientOptions
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
                    PermitLimit = 120,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
            options.OnRejected = (context, _) => new ValueTask(ApiProblems.WriteAsync(context.HttpContext,
                429, "rate_limited", "The request rate limit was exceeded.",
                context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay)
                    ? Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)) : 60));
        });

        var app = builder.Build();
        var apiSettings = app.Services.GetRequiredService<ApiSettings>();
        app.Services.GetRequiredService<EnrollmentSettings>().Validate(app.Environment);
        var configurationErrors = apiSettings.Validate(app.Environment);
        if (app.Services.GetRequiredService<EnrollmentSettings>().Enabled && configurationErrors.Count != 0)
            throw new InvalidOperationException("Enrollment requires complete plant API configuration: " + string.Join(", ", configurationErrors));
        var websiteSettings = app.Services.GetRequiredService<IOptions<WebsiteSettings>>().Value;
        if (websiteEnabled)
        {
            var websiteErrors = configurationErrors.Concat(websiteSettings.Validate(app.Environment)).ToList();
            if (!apiSettings.HasClientSecret)
                websiteErrors.Add("Factory:ClientSecret (resolved application credential)");
            if (websiteErrors.Count != 0)
                throw new InvalidOperationException("Factory website configuration is incomplete: " + string.Join(", ", websiteErrors));
            if (!app.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Authentication.AuthenticationOptions>>()
                .Value.Schemes.Any(scheme => scheme.Name == WebsiteAuthentication.OidcScheme))
                throw new InvalidOperationException("Factory requires application-managed OpenID Connect. Disable App Service Authentication.");
        }
        app.UseExceptionHandler();
        app.UseStatusCodePages(context => WebsiteErrors.IsPage(context.HttpContext.Request.Path)
            ? WebsiteErrors.WriteAsync(context.HttpContext, context.HttpContext.Response.StatusCode,
                context.HttpContext.Response.StatusCode == 400
                    ? "The form could not be accepted. Reload the page and submit it again."
                    : "No page is available for this request, or the requested action is not permitted.")
            : ApiProblems.WriteAsync(context.HttpContext, context.HttpContext.Response.StatusCode,
                "not_found", "No response is available for this request."));
        if (configurationErrors.Count != 0)
            app.Logger.LogWarning("Plant API disabled until configuration is supplied or corrected: {Fields}",
                string.Join(", ", configurationErrors));
        if (!websiteEnabled)
            app.Logger.LogWarning("Browser sign-in disabled. Set Factory:Web:Enabled and supply its required configuration.");
        app.Use(async (context, next) =>
        {
            if (WebsiteErrors.IsPage(context.Request.Path))
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Headers["Content-Security-Policy"] =
                        "default-src 'self'; script-src 'self'; style-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self' https://login.microsoftonline.com";
                    context.Response.Headers["Referrer-Policy"] = "no-referrer";
                    context.Response.Headers.XContentTypeOptions = "nosniff";
                    return Task.CompletedTask;
                });
                if (!websiteEnabled && context.Request.Path != "/")
                {
                    await WebsiteErrors.WriteAsync(context, 503,
                        "Browser sign-in is not configured. Configure Factory:Web and the backing services before using plant pages.");
                    return;
                }
            }
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
        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();

        app.MapControllers();
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
        app.MapPlantEndpoints();

        app.Run();
    }
}
