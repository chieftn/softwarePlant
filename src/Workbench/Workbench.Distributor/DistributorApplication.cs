using Azure.AI.AgentServer.Core;
using Azure.AI.AgentServer.Core.Storage;
using Azure.AI.AgentServer.Invocations;
using Azure.Identity;

namespace Workbench.Distributor;

public static class DistributorApplication
{
    public static async Task<WebApplication> BuildAsync(string[] args, FoundryStateStore? stateStore = null)
    {
        var host = AgentHost.CreateBuilder(args);
        var builder = host.WebApplicationBuilder;
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1024 * 1024);
        var configuration = builder.Configuration;
        var mode = configuration["Coordinator:Mode"] ?? "Foundry";
        var local = mode == "Local";
        if (mode is not ("Local" or "Foundry"))
            throw new InvalidOperationException("Coordinator:Mode must be Foundry or Local.");
        var hosted = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FOUNDRY_HOSTING_ENVIRONMENT"));
        if (local == hosted)
            throw new InvalidOperationException("Foundry mode requires the hosted environment; Local mode is forbidden inside Foundry.");
        if (local)
        {
            var urls = configuration["urls"] ?? "http://127.0.0.1:8088";
            if (urls.Split(';').Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback))
                throw new InvalidOperationException("Local development mode may listen only on loopback.");
            builder.WebHost.UseUrls(urls);
        }
        var access = new CoordinatorAccess(configuration, local);
        var name = configuration["Coordinator:StoreName"]
            ?? throw new InvalidOperationException("Coordinator:StoreName is required and must stay stable across deployments.");
        var backend = stateStore ?? await FoundryStateStore.GetOrCreateAsync(name,
            credential: local ? null : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned),
            userIsolation: false, itemTtlSeconds: -1);
        var descriptor = await backend.GetAsync();
        if (descriptor is null || descriptor.UserIsolation || descriptor.ItemTtlSeconds != -1)
            throw new InvalidOperationException("The plant store must exist, be shared across callers, and have expiration disabled.");
        builder.Services.AddSingleton(access);
        builder.Services.AddSingleton(new CoordinatorStore(backend));
        if (local)
        {
            builder.Services.AddInvocationsServer();
            builder.Services.AddScoped<InvocationHandler, CoordinatorInvocationHandler>();
            var app = builder.Build();
            app.UseAgentServerCore();
            app.MapGet("/readiness", () => Results.Ok(new { status = "ready" }));
            app.MapInvocationsServer();
            return app;
        }
        host.AddInvocations<CoordinatorInvocationHandler>();
        return host.Build().App;
    }
}
