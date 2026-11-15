using System.Net;
using System.Net.Http.Json;
using Factory.Contracts;
using Factory.Plant.Service;
using Factory.Plant.Service.Storage;
using Xunit;
using static Factory.Tests.PlantApiTests;

namespace Factory.Tests;

public sealed class ProductionLineTests
{
    [Fact]
    public async Task DefinitionsAreOwnedConfigurableRevisionedAndRetrySafe()
    {
        using var app = new ApiTestApplication();
        using var owner = app.Human(creator: false);
        using var other = app.Human(ApiTestApplication.ReaderId, creator: false);
        var key = Key();
        var definition = Definition();
        using var create = await SendAsync(owner, HttpMethod.Post, "/v1/production-lines", definition, key);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var line = (await create.Content.ReadFromJsonAsync<ProductionLineView>())!;
        using var replay = await SendAsync(owner, HttpMethod.Post, "/v1/production-lines", definition, key);
        Assert.Equal(line.Id, (await replay.Content.ReadFromJsonAsync<ProductionLineView>())!.Id);
        Assert.Single(app.Store.All<ProductionLineRecord>());
        using var conflict = await SendAsync(owner, HttpMethod.Post, "/v1/production-lines", definition with { Name = "Changed" }, key);
        await ProblemAsync(conflict, 409, "idempotency_conflict");
        var second = await CreateLineAsync(owner, definition with
        {
            Name = "Another line",
            Source = definition.Source with { Repository = "another/source", WorkloadPath = "methods/line" }
        });
        Assert.NotEqual(line.Id, second.Id);
        Assert.Equal(2, (await owner.GetFromJsonAsync<Page<ProductionLineView>>("/v1/production-lines"))!.Items.Count);
        Assert.Empty((await other.GetFromJsonAsync<Page<ProductionLineView>>("/v1/production-lines"))!.Items);
        using var denied = await other.GetAsync($"/v1/production-lines/{line.Id}");
        await ProblemAsync(denied, 403, "forbidden");

        var changed = definition with { Name = "Revised", Tasks = definition.Tasks with { Repository = "tasks/separate" } };
        var updateKey = Key();
        using var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/v1/production-lines/{line.Id}")
        { Content = JsonContent.Create(changed) };
        updateRequest.Headers.Add("Idempotency-Key", updateKey);
        updateRequest.Headers.Add("If-Match", line.Revision);
        using var update = await owner.SendAsync(updateRequest);
        update.EnsureSuccessStatusCode();
        var revised = (await update.Content.ReadFromJsonAsync<ProductionLineView>())!;
        Assert.NotEqual(line.Revision, revised.Revision);
        using var retryRequest = new HttpRequestMessage(HttpMethod.Put, $"/v1/production-lines/{line.Id}")
        { Content = JsonContent.Create(changed) };
        retryRequest.Headers.Add("Idempotency-Key", updateKey);
        retryRequest.Headers.Add("If-Match", line.Revision);
        using var retry = await owner.SendAsync(retryRequest);
        Assert.Equal(revised.Revision, (await retry.Content.ReadFromJsonAsync<ProductionLineView>())!.Revision);
        using var staleRequest = new HttpRequestMessage(HttpMethod.Put, $"/v1/production-lines/{line.Id}")
        { Content = JsonContent.Create(definition) };
        staleRequest.Headers.Add("Idempotency-Key", Key());
        staleRequest.Headers.Add("If-Match", line.Revision);
        using var stale = await owner.SendAsync(staleRequest);
        await ProblemAsync(stale, 412, "etag_mismatch");
        Assert.Contains(app.Store.All<ProductionLineRevisionRecord>(), record =>
            record.Line.Id == line.Id && record.Line.Revision == line.Revision && record.Line.Definition.Name == "Configured line");
    }

    [Fact]
    public async Task BindingDiscoveryAndAcknowledgmentRespectMembershipRevocationAndReassignment()
    {
        using var app = new ApiTestApplication();
        using var owner = app.Human();
        using var daemon = app.Daemon();
        using var admin = app.Client(app.Token(ApiTestApplication.OwnerId, ["Factory.User", ApiCaller.EnrollmentAdministratorRole]));
        var (plant, _) = await CreateAsync(owner, "Plant");
        var enrollment = SeedMember(app, plant.Id);
        var line = await CreateLineAsync(owner);
        var path = $"/v1/plants/{plant.Id}/workbenches/{enrollment.WorkbenchId}/production-line";
        using var assigned = await owner.PutAsJsonAsync(path, new ProductionLineAssignment("1", 0, line.Id.ToUpperInvariant()));
        assigned.EnsureSuccessStatusCode();
        using var repeat = await owner.PutAsJsonAsync(path, new ProductionLineAssignment("1", 0, line.Id));
        repeat.EnsureSuccessStatusCode();
        Assert.Single(app.Store.All<ProductionLineBindingRecord>());
        var configPath = $"/v1/enrollments/{enrollment.Id}/configuration";
        var configuration = (await daemon.GetFromJsonAsync<WorkbenchLineConfiguration>(configPath))!;
        Assert.Equal(line.Revision, configuration.ProductionLine!.Revision);
        using var wrongIdentity = app.Daemon(subject: Key());
        using var denied = await wrongIdentity.GetAsync(configPath);
        await ProblemAsync(denied, 403, "forbidden");
        var observation = Observation(configuration);
        var observationKey = Key();
        using var observed = await SendAsync(daemon, HttpMethod.Put, configPath + "-observation", observation, observationKey);
        observed.EnsureSuccessStatusCode();
        using var revoked = await owner.PutAsJsonAsync(path, new ProductionLineAssignment("1", 1));
        revoked.EnsureSuccessStatusCode();
        using var stale = await SendAsync(daemon, HttpMethod.Put, configPath + "-observation", observation, Key());
        await ProblemAsync(stale, 409, "configuration_changed");
        var noLine = (await daemon.GetFromJsonAsync<WorkbenchLineConfiguration>(configPath))!;
        Assert.Null(noLine.ProductionLine);
        Assert.Equal(2, noLine.BindingVersion);
        using var reassigned = await owner.PutAsJsonAsync(path, new ProductionLineAssignment("1", 2, line.Id));
        reassigned.EnsureSuccessStatusCode();
        using var unassigned = await admin.PutAsJsonAsync($"/v1/enrollments/{enrollment.Id}/assignment",
            new { schemaVersion = "1", expectedVersion = 1 });
        unassigned.EnsureSuccessStatusCode();
        var unconfigured = (await daemon.GetFromJsonAsync<WorkbenchLineConfiguration>(configPath))!;
        Assert.Null(unconfigured.PlantId);
        Assert.Null(unconfigured.ProductionLine);
        using var acknowledged = await SendAsync(daemon, HttpMethod.Put, configPath + "-observation",
            Observation(unconfigured), Key());
        acknowledged.EnsureSuccessStatusCode();
        var acknowledgedAt = Assert.Single(app.Store.All<ConfigurationObservationRecord>()).RecordedAt;
        app.Clock.Now += TimeSpan.FromSeconds(15);
        using var oldAcknowledgment = await SendAsync(daemon, HttpMethod.Put, configPath + "-observation", observation, observationKey);
        oldAcknowledgment.EnsureSuccessStatusCode();
        var currentObservation = Assert.Single(app.Store.All<ConfigurationObservationRecord>());
        Assert.Equal("unconfigured", currentObservation.Observation.State);
        Assert.Equal(acknowledgedAt, currentObservation.RecordedAt);
        using var returned = await admin.PutAsJsonAsync($"/v1/enrollments/{enrollment.Id}/assignment",
            new EnrollmentAssignment("1", 2, plant.Id));
        returned.EnsureSuccessStatusCode();
        var afterReturn = (await daemon.GetFromJsonAsync<WorkbenchLineConfiguration>(configPath))!;
        Assert.Null(afterReturn.ProductionLine);
        Assert.Equal(3, afterReturn.AssignmentVersion);
        Assert.Equal(line.Id, Assert.Single(app.Store.All<ProductionLineBindingRecord>()).Binding.ProductionLineId);
    }

    [Fact]
    public async Task AssignmentRequiresBothLineAndPlantOwnershipAndCannotClaimReadiness()
    {
        using var app = new ApiTestApplication();
        using var owner = app.Human();
        using var other = app.Human(ApiTestApplication.ReaderId);
        using var daemon = app.Daemon();
        var (plant, _) = await CreateAsync(owner, "Plant");
        var enrollment = SeedMember(app, plant.Id);
        var owned = await CreateLineAsync(owner);
        var foreign = await CreateLineAsync(other);
        var path = $"/v1/plants/{plant.Id}/workbenches/{enrollment.WorkbenchId}/production-line";
        using var wrongLine = await owner.PutAsJsonAsync(path, new ProductionLineAssignment("1", 0, foreign.Id));
        await ProblemAsync(wrongLine, 403, "forbidden");
        using var wrongPlant = await other.PutAsJsonAsync(path, new ProductionLineAssignment("1", 0, foreign.Id));
        await ProblemAsync(wrongPlant, 403, "forbidden");
        using var assigned = await owner.PutAsJsonAsync(path, new ProductionLineAssignment("1", 0, owned.Id));
        assigned.EnsureSuccessStatusCode();
        var configuration = (await daemon.GetFromJsonAsync<WorkbenchLineConfiguration>(
            $"/v1/enrollments/{enrollment.Id}/configuration"))!;
        using var ready = await SendAsync(daemon, HttpMethod.Put, $"/v1/enrollments/{enrollment.Id}/configuration-observation",
            Observation(configuration) with { State = "ready" }, Key());
        await ProblemAsync(ready, 400, "invalid_request");
        Assert.Empty(app.Store.All<ConfigurationObservationRecord>());
    }

    [Theory]
    [InlineData("workload", "../productionLine")]
    [InlineData("workload", "/productionLine")]
    [InlineData("source", "https://token@github.com/owner/repo")]
    [InlineData("connection", "a-secret:token")]
    [InlineData("branch", "main.lock")]
    public async Task InvalidReferencesAreRejectedBeforePersistence(string field, string value)
    {
        using var app = new ApiTestApplication();
        using var owner = app.Human();
        var definition = Definition();
        definition = definition with
        {
            Source = field switch
            {
                "workload" => definition.Source with { WorkloadPath = value },
                "source" => definition.Source with { Repository = value },
                "connection" => definition.Source with { ConnectionReference = value },
                _ => definition.Source with { BaseBranch = value }
            }
        };
        using var rejected = await SendAsync(owner, HttpMethod.Post, "/v1/production-lines", definition, Key());
        await ProblemAsync(rejected, 400, "invalid_request");
        Assert.Empty(app.Store.All<ProductionLineRecord>());
    }

    internal static ProductionLineWriteRequest Definition() => new()
    {
        SchemaVersion = "1",
        Name = "Configured line",
        Source = new("github", "product/source", "main", "productionLine", "source-app"),
        Tasks = new("github-issues", "product/tasks", "task-app", ["approved"]),
        Policy = new(false, false, 3, 30)
    };

    internal static async Task<ProductionLineView> CreateLineAsync(HttpClient owner, ProductionLineWriteRequest? definition = null)
    {
        using var created = await SendAsync(owner, HttpMethod.Post, "/v1/production-lines", definition ?? Definition(), Key());
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<ProductionLineView>())!;
    }

    internal static EnrollmentRecord SeedMember(ApiTestApplication app, string plantId)
    {
        var id = Key();
        var installation = Key();
        var enrollment = new EnrollmentRecord
        {
            Id = id,
            ScopeId = Records.EnrollmentScope(id),
            AssignedPlantId = plantId,
            AssignmentVersion = 1,
            State = "active",
            WorkbenchId = $"wb-{Guid.Parse(installation):N}",
            Draft = new()
            {
                SchemaVersion = "1",
                OperationId = id,
                InstallationId = installation,
                MachineName = "Fixture",
                PublicCertificate = "AQID"
            },
            Requester = new(ApiTestApplication.Tenant, ApiTestApplication.OwnerId),
            RequesterClientId = ApiTestApplication.Audience,
            Digest = "fixture",
            UpdatedAt = app.Clock.Now,
            AuthorizedAt = app.Clock.Now,
            ClientId = ApiTestApplication.DaemonClientId,
            ServicePrincipalId = ApiTestApplication.DaemonId
        };
        app.Store.Seed(enrollment);
        app.Store.Seed(WorkbenchApiTests.Workbench(app, plantId, enrollment.WorkbenchId) with
        {
            EnrollmentId = id,
            AssignmentVersion = 1
        });
        return enrollment;
    }

    internal static WorkbenchConfigurationObservation Observation(WorkbenchLineConfiguration configuration) => new()
    {
        SchemaVersion = "1",
        AssignmentVersion = configuration.AssignmentVersion,
        BindingVersion = configuration.BindingVersion,
        ProductionLineId = configuration.ProductionLine?.Id,
        ProductionLineRevision = configuration.ProductionLine?.Revision,
        State = configuration.ProductionLine is null ? "unconfigured" : "blocked",
        Reason = "Test observation"
    };
}
