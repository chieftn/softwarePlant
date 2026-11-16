using System.Net;
using System.Net.Http.Json;
using Factory.Contracts;
using Factory.Plant.Service;
using Factory.Workbench;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using static Factory.Tests.PlantApiTests;
using static Factory.Tests.ProductionLineTests;

namespace Factory.Tests;

public sealed class EnrollmentWorkloadTests
{
    [Fact]
    public async Task WorkloadEndpointUsesExactMachineAndRejectsLateRevokedResolution()
    {
        using var app = new WorkloadApplication();
        using var owner = app.Human();
        var (plant, _) = await CreateAsync(owner, "Plant");
        var enrollment = SeedMember(app, plant.Id);
        var line = await CreateLineAsync(owner);
        using var assignment = await owner.PutAsJsonAsync($"/v1/plants/{plant.Id}/production-line",
            new ProductionLineAssignment("1", 0, line.Id));
        assignment.EnsureSuccessStatusCode();
        using var machine = app.Daemon();
        var config = (await machine.GetFromJsonAsync<WorkbenchLineConfiguration>($"/v1/enrollments/{enrollment.Id}/configuration"))!;
        var path = $"/v1/enrollments/{enrollment.Id}/workload";
        using var wrong = app.Daemon(Key(), Key());
        using var denied = await wrong.PostAsJsonAsync(path, WorkloadRequest.From(config));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0, app.Reader.Calls);
        using var result = await machine.PostAsJsonAsync(path, WorkloadRequest.From(config));
        result.EnsureSuccessStatusCode();
        var workload = (await result.Content.ReadFromJsonAsync<ResolvedRepositoryWorkload>())!;
        workload.Validate();
        Assert.Equal(line.Revision, workload.Configuration.ProductionLine!.Revision);
        app.Reader.AfterRead = async () =>
        {
            using var revoke = await owner.PutAsJsonAsync($"/v1/plants/{plant.Id}/production-line",
                new ProductionLineAssignment("1", 1));
            revoke.EnsureSuccessStatusCode();
        };
        using var late = await machine.PostAsJsonAsync(path, WorkloadRequest.From(config));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("configuration_changed", (await late.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("code").GetString());
    }

    [Fact]
    public async Task MissingAppConfigurationIsAnExplicitBlockerNotInteractiveAuthentication()
    {
        using var app = new ApiTestApplication();
        using var owner = app.Human();
        var (plant, _) = await CreateAsync(owner, "Plant");
        var enrollment = SeedMember(app, plant.Id);
        var line = await CreateLineAsync(owner);
        using var assignment = await owner.PutAsJsonAsync($"/v1/plants/{plant.Id}/production-line",
            new ProductionLineAssignment("1", 0, line.Id));
        assignment.EnsureSuccessStatusCode();
        using var machine = app.Daemon();
        var config = (await machine.GetFromJsonAsync<WorkbenchLineConfiguration>($"/v1/enrollments/{enrollment.Id}/configuration"))!;
        using var response = await machine.PostAsJsonAsync($"/v1/enrollments/{enrollment.Id}/workload", WorkloadRequest.From(config));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("repository_connection_unconfigured", (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("code").GetString());
    }

    [Fact]
    public void LocalCacheCannotReactivateRemovedOrReplacedConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), $"factory-workload-{Guid.NewGuid():N}");
        try
        {
            var configuration = RepositoryWorkloadTests.Configuration();
            var binding = new WorkbenchBinding("1", Key(), null, configuration.WorkbenchId, ApiTestApplication.Tenant,
                ApiTestApplication.DaemonClientId, ApiTestApplication.DaemonId, new('A', 40), configuration.EnrollmentId);
            WorkbenchLineConfigurationStore.Apply(root, binding, configuration);
            var workload = ResolvedRepositoryWorkload.Create(configuration, new('a', 40), RepositoryWorkloadTests.Files());
            WorkbenchWorkloadStore.Save(root, binding, workload);
            Assert.NotNull(WorkbenchWorkloadStore.Read(root, binding, configuration));
            WorkbenchLineConfigurationStore.Apply(root, binding, configuration with { BindingVersion = 2, ProductionLine = null });
            Assert.False(File.Exists(WorkbenchWorkloadStore.PathFor(root)));
            Assert.Throws<InvalidDataException>(() => WorkbenchWorkloadStore.Save(root, binding, workload));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private sealed class WorkloadApplication : ApiTestApplication
    {
        public FixtureReader Reader { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGitHubRepositoryReader>();
                services.AddSingleton<IGitHubRepositoryReader>(Reader);
            });
        }
    }

    private sealed class FixtureReader : IGitHubRepositoryReader
    {
        public int Calls { get; private set; }
        public Func<Task>? AfterRead { get; set; }
        public async Task<ResolvedRepositoryWorkload> ResolveAsync(WorkbenchLineConfiguration configuration, CancellationToken cancellationToken)
        {
            Calls++;
            if (AfterRead is not null)
                await AfterRead();
            return ResolvedRepositoryWorkload.Create(configuration, new('a', 40), RepositoryWorkloadTests.Files());
        }
    }
}
