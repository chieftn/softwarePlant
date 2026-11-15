using System.Net;
using System.Net.Http.Json;
using Factory.Contracts;
using Factory.Plant.Service;
using Factory.Plant.Service.Storage;
using Factory.Workbench;
using Xunit;
using static Factory.Tests.PlantApiTests;
using static Factory.Tests.ProductionLineTests;

namespace Factory.Tests;

public sealed class ProductionLineSynchronizationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"factory-line-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task RealConnectionPersistsConfigurationAndReplacesObsoletePendingAcknowledgments()
    {
        using var app = new ApiTestApplication();
        using var owner = app.Human();
        var (plant, _) = await CreateAsync(owner, "Plant");
        var enrollment = SeedMember(app, plant.Id);
        var binding = Configure(enrollment);
        var line = await CreateLineAsync(owner);
        var assignmentPath = $"/v1/plants/{plant.Id}/production-line";
        using var assigned = await owner.PutAsJsonAsync(assignmentPath, new ProductionLineAssignment("1", 0, line.Id));
        assigned.EnsureSuccessStatusCode();
        using var machine = new HttpClient(new LostObservationResponse(app.Server.CreateHandler())) { BaseAddress = new Uri("http://localhost") };
        var connection = new WorkbenchPlantConnection(Transport(app, machine), binding, "test-release", root);
        await Assert.ThrowsAsync<HttpRequestException>(() => connection.PublishAsync(CancellationToken.None));
        Assert.Equal("blocked", WorkbenchLineConfigurationStore.Read(root, binding)!.Observation.State);
        Assert.True(File.Exists(Path.Combine(LocalState.RuntimeDirectory(root), "pending-configuration-observation.json")));
        using var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/v1/production-lines/{line.Id}")
        { Content = JsonContent.Create(Definition() with { Name = "New workload configuration" }) };
        updateRequest.Headers.Add("Idempotency-Key", Key());
        updateRequest.Headers.Add("If-Match", line.Revision);
        using var update = await owner.SendAsync(updateRequest);
        update.EnsureSuccessStatusCode();
        var revised = (await update.Content.ReadFromJsonAsync<ProductionLineView>())!;
        connection = new WorkbenchPlantConnection(Transport(app, machine), binding, "test-release", root);
        await connection.PublishAsync(CancellationToken.None);
        Assert.Equal(revised.Revision, WorkbenchLineConfigurationStore.Read(root, binding)!.Configuration.ProductionLine!.Revision);
        var observation = Assert.Single(app.Store.All<ConfigurationObservationRecord>()).Observation;
        Assert.Equal(revised.Revision, observation.ProductionLineRevision);
        Assert.Equal("blocked", observation.State);
        Assert.False(File.Exists(Path.Combine(LocalState.RuntimeDirectory(root), "pending-configuration-observation.json")));
        using var revoked = await owner.PutAsJsonAsync(assignmentPath, new ProductionLineAssignment("1", 1));
        revoked.EnsureSuccessStatusCode();
        await connection.PublishAsync(CancellationToken.None);
        Assert.Null(WorkbenchLineConfigurationStore.Read(root, binding)!.Configuration.ProductionLine);
        Assert.Equal("unconfigured", Assert.Single(app.Store.All<ConfigurationObservationRecord>()).Observation.State);
    }

    [Fact]
    public async Task PlantUnassignmentClearsLocalMemoEvenWhenSubsequentConfigurationFetchFails()
    {
        using var app = new ApiTestApplication();
        using var owner = app.Human();
        using var admin = app.Client(app.Token(ApiTestApplication.OwnerId, ["Factory.User", ApiCaller.EnrollmentAdministratorRole]));
        var (plant, _) = await CreateAsync(owner, "Plant");
        var enrollment = SeedMember(app, plant.Id);
        var binding = Configure(enrollment);
        var line = await CreateLineAsync(owner);
        using var assigned = await owner.PutAsJsonAsync($"/v1/plants/{plant.Id}/production-line",
            new ProductionLineAssignment("1", 0, line.Id));
        assigned.EnsureSuccessStatusCode();
        var handler = new ConfigurationOutage(app.Server.CreateHandler());
        using var machine = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var connection = new WorkbenchPlantConnection(Transport(app, machine), binding, "test-release", root);
        await connection.PublishAsync(CancellationToken.None);
        using var unassigned = await admin.PutAsJsonAsync($"/v1/enrollments/{enrollment.Id}/assignment",
            new { schemaVersion = "1", expectedVersion = 1 });
        unassigned.EnsureSuccessStatusCode();
        handler.Fail = true;
        await Assert.ThrowsAsync<PlantConnectionException>(() => connection.PublishAsync(CancellationToken.None));
        var state = WorkbenchLineConfigurationStore.Read(root, binding)!;
        Assert.Null(state.Configuration.ProductionLine);
        Assert.Null(state.Configuration.PlantId);
        Assert.Equal("unconfigured", state.Observation.State);
        Assert.Equal("unconfigured", connection.ConfigurationObservation!.State);
        handler.Fail = false;
        await connection.PublishAsync(CancellationToken.None);
        Assert.Equal(2, Assert.Single(app.Store.All<ConfigurationObservationRecord>()).Observation.AssignmentVersion);
    }

    [Fact]
    public async Task MovingPlantsInheritsDestinationConfigurationWithoutReusingOldMembership()
    {
        using var app = new ApiTestApplication();
        using var owner = app.Human();
        using var admin = app.Client(app.Token(ApiTestApplication.OwnerId, ["Factory.User", ApiCaller.EnrollmentAdministratorRole]));
        var (firstPlant, _) = await CreateAsync(owner, "First plant");
        var (secondPlant, _) = await CreateAsync(owner, "Second plant");
        var enrollment = SeedMember(app, firstPlant.Id);
        var binding = Configure(enrollment);
        var firstLine = await CreateLineAsync(owner);
        var secondLine = await CreateLineAsync(owner, Definition() with { Name = "Destination line" });
        var firstPath = $"/v1/plants/{firstPlant.Id}/production-line";
        using var assigned = await owner.PutAsJsonAsync(firstPath, new ProductionLineAssignment("1", 0, firstLine.Id));
        assigned.EnsureSuccessStatusCode();
        using var revoked = await owner.PutAsJsonAsync(firstPath, new ProductionLineAssignment("1", 1));
        revoked.EnsureSuccessStatusCode();
        using var reassigned = await owner.PutAsJsonAsync(firstPath, new ProductionLineAssignment("1", 2, firstLine.Id));
        reassigned.EnsureSuccessStatusCode();
        using var destination = await owner.PutAsJsonAsync($"/v1/plants/{secondPlant.Id}/production-line",
            new ProductionLineAssignment("1", 0, secondLine.Id));
        destination.EnsureSuccessStatusCode();
        using var machine = app.Client();
        var connection = new WorkbenchPlantConnection(Transport(app, machine), binding, "test-release", root);
        await connection.PublishAsync(CancellationToken.None);
        var oldConfiguration = WorkbenchLineConfigurationStore.Read(root, binding)!.Configuration;
        Assert.Equal(3, oldConfiguration.BindingVersion);
        using var moved = await admin.PutAsJsonAsync($"/v1/enrollments/{enrollment.Id}/assignment",
            new EnrollmentAssignment("1", 1, secondPlant.Id));
        moved.EnsureSuccessStatusCode();
        await connection.PublishAsync(CancellationToken.None);
        var current = WorkbenchLineConfigurationStore.Read(root, binding)!.Configuration;
        Assert.Equal(secondPlant.Id, current.PlantId);
        Assert.Equal(secondLine.Id, current.ProductionLine!.Id);
        Assert.Equal(2, current.AssignmentVersion);
        Assert.Equal(1, current.BindingVersion);
        Assert.Throws<InvalidDataException>(() => WorkbenchLineConfigurationStore.Apply(root, binding, oldConfiguration));
        Assert.Equal(secondLine.Id, WorkbenchLineConfigurationStore.Read(root, binding)!.Configuration.ProductionLine!.Id);
        using var returned = await admin.PutAsJsonAsync($"/v1/enrollments/{enrollment.Id}/assignment",
            new EnrollmentAssignment("1", 2, firstPlant.Id));
        returned.EnsureSuccessStatusCode();
        await connection.PublishAsync(CancellationToken.None);
        current = WorkbenchLineConfigurationStore.Read(root, binding)!.Configuration;
        Assert.Equal(firstLine.Id, current.ProductionLine!.Id);
        Assert.Equal(3, current.AssignmentVersion);
        Assert.Equal(3, current.BindingVersion);
        Assert.Equal(3, Assert.Single(app.Store.All<ConfigurationObservationRecord>()).Observation.AssignmentVersion);
    }

    [Fact]
    public void ObsoleteMembershipCannotReactivatePersistedConfiguration()
    {
        var enrollment = new EnrollmentRecord
        {
            Id = Key(),
            ScopeId = "fixture",
            WorkbenchId = "fixture",
            State = "active",
            Draft = new() { SchemaVersion = "1", OperationId = Key(), InstallationId = Key(), MachineName = "Fixture", PublicCertificate = "AQID" },
            Requester = new(ApiTestApplication.Tenant, ApiTestApplication.OwnerId),
            RequesterClientId = ApiTestApplication.Audience,
            Digest = "fixture",
            UpdatedAt = DateTimeOffset.UtcNow,
            AuthorizedAt = DateTimeOffset.UtcNow
        };
        enrollment = enrollment with { WorkbenchId = $"wb-{Guid.Parse(enrollment.Draft.InstallationId):N}" };
        var binding = Configure(enrollment);
        WorkbenchLineConfigurationStore.Apply(root, binding, new("1", enrollment.Id, enrollment.WorkbenchId, null, 4, 0, null));
        Assert.Throws<InvalidDataException>(() => WorkbenchLineConfigurationStore.ObserveMembership(root, binding,
            new("1", enrollment.Id, enrollment.WorkbenchId, Key(), 3)));
        Assert.Null(WorkbenchLineConfigurationStore.Read(root, binding)!.Configuration.ProductionLine);
    }

    private WorkbenchBinding Configure(EnrollmentRecord enrollment)
    {
        ConfigurationStore.Save(root, new()
        {
            SchemaVersion = "1",
            InstallationId = enrollment.Draft.InstallationId,
            PlantServiceUrl = "https://plant.example",
            TenantId = ApiTestApplication.Tenant,
            ApiClientId = ApiTestApplication.Audience,
            InstallerClientId = ApiTestApplication.Audience,
            Distribution = "Ubuntu",
            LinuxUser = "worker",
            LinuxWorkspaceRoot = "/home/worker/work",
            AllowedRepositories = [],
            AllowShellExecution = false,
            AllowPublication = false
        });
        return new("1", enrollment.Draft.InstallationId, null, enrollment.WorkbenchId, ApiTestApplication.Tenant,
            ApiTestApplication.DaemonClientId, ApiTestApplication.DaemonId, new string('A', 40), enrollment.Id);
    }

    private static PlantHttp Transport(ApiTestApplication app, HttpClient client) => new(client, _ => Task.FromResult(
        app.Token(ApiTestApplication.DaemonId, ["Workbench.Connect"], false, ApiTestApplication.DaemonClientId)));

    private sealed class LostObservationResponse(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private bool lost;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (!lost && response.IsSuccessStatusCode && request.RequestUri!.AbsolutePath.EndsWith("/configuration-observation", StringComparison.Ordinal))
            {
                lost = true;
                response.Dispose();
                throw new HttpRequestException("Synthetic lost acknowledgment after commit.");
            }
            return response;
        }
    }

    private sealed class ConfigurationOutage(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool Fail { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Fail && request.RequestUri!.AbsolutePath.EndsWith("/configuration", StringComparison.Ordinal)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
                : base.SendAsync(request, cancellationToken);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
