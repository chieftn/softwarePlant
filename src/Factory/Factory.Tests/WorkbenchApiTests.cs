using System.Net;
using System.Net.Http.Json;
using Factory.Contracts;
using Factory.Plant.Service.Storage;
using Xunit;
using static Factory.Tests.PlantApiTests;

namespace Factory.Tests;

public sealed class WorkbenchApiTests
{
    [Fact]
    public async Task RegistrationAndPresenceRequireTheExactActiveApplicationIdentity()
    {
        using var app = new ApiTestApplication();
        using var owner = app.Human();
        using var daemon = app.Daemon();
        var (plant, _) = await CreateAsync(owner, "Plant");
        var admission = Workbench(app, plant.Id, "box-one") with { AdmissionState = "ready" };
        app.Store.Seed(admission);
        var path = $"/v1/plants/{plant.Id}/workbenches/box-one";
        using (var inactive = await SendAsync(daemon, HttpMethod.Put, path + "/registration", Registration(), Key()))
            await ProblemAsync(inactive, 403, "forbidden");
        app.Store.Seed(admission with { AdmissionState = "active" });
        using (var unregistered = await SendAsync(daemon, HttpMethod.Put, path + "/presence", Presence("idle"), Key()))
            await ProblemAsync(unregistered, 409, "workbench_not_registered");
        using (var human = await SendAsync(owner, HttpMethod.Put, path + "/registration", Registration(), Key()))
            await ProblemAsync(human, 403, "forbidden");
        using (var humanWithAppRole = app.Client(app.Token(ApiTestApplication.OwnerId, ["Workbench.Connect"])))
        using (var denied = await SendAsync(humanWithAppRole, HttpMethod.Put, path + "/registration", Registration(), Key()))
            await ProblemAsync(denied, 403, "forbidden");
        using (var wrongSubject = app.Daemon(subject: Key()))
        using (var denied = await SendAsync(wrongSubject, HttpMethod.Put, path + "/registration", Registration(), Key()))
            await ProblemAsync(denied, 403, "forbidden");
        using (var wrongClient = app.Daemon(clientId: Key()))
        using (var denied = await SendAsync(wrongClient, HttpMethod.Put, path + "/registration", Registration(), Key()))
            await ProblemAsync(denied, 403, "forbidden");
        using (var inventory = await daemon.GetAsync($"/v1/plants/{plant.Id}/workbenches"))
            await ProblemAsync(inventory, 403, "forbidden");
        using (var incompatible = await SendAsync(daemon, HttpMethod.Put, path + "/registration",
            Registration() with { ProtocolVersion = "not-supported" }, Key()))
            await ProblemAsync(incompatible, 409, "incompatible_version");
        using (var duplicateCapabilities = await SendAsync(daemon, HttpMethod.Put, path + "/registration",
            Registration() with { Capabilities = ["workspace", "workspace"] }, Key()))
            await ProblemAsync(duplicateCapabilities, 400, "invalid_request");
        Assert.Empty(app.Store.All<RegistrationRecord>());
        Assert.Empty(app.Store.All<PresenceRecord>());
    }

    [Fact]
    public async Task ReportsAreIndependentAndReplayCannotRefreshContactOrOverwriteANewerReport()
    {
        using var app = new ApiTestApplication();
        using var owner = app.Human();
        using var daemon = app.Daemon();
        var (plant, _) = await CreateAsync(owner, "Plant");
        var admission = Workbench(app, plant.Id, "box-one");
        app.Store.Seed(admission);
        var path = $"/v1/plants/{plant.Id}/workbenches/box-one";
        var registrationKey = Key();
        using (var registered = await SendAsync(daemon, HttpMethod.Put, path + "/registration", Registration(), registrationKey))
            Assert.Equal(HttpStatusCode.NoContent, registered.StatusCode);
        var registeredAt = app.Clock.Now;
        var initial = await owner.GetFromJsonAsync<WorkbenchView>(path);
        Assert.Equal("Approved box-one", initial!.Name);
        Assert.Equal("observed-machine", initial.Registration!.MachineName);
        Assert.Equal("unknown", initial.ContactState);
        Assert.Null(initial.LastSeenAt);
        Assert.Equal(admission.Identity, initial.Identity);

        app.Clock.Now += TimeSpan.FromMinutes(1);
        using (var replay = await SendAsync(daemon, HttpMethod.Put, path + "/registration", Registration(), registrationKey))
            Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
        Assert.Equal(registeredAt, Assert.Single(app.Store.All<RegistrationRecord>()).RecordedAt);
        var idleKey = Key();
        using (var idle = await SendAsync(daemon, HttpMethod.Put, path + "/presence", Presence("idle"), idleKey))
            Assert.Equal(HttpStatusCode.NoContent, idle.StatusCode);
        var observedAt = app.Clock.Now;
        var online = await owner.GetFromJsonAsync<WorkbenchView>(path);
        Assert.Equal("online", online!.ContactState);
        Assert.Equal(observedAt, online.LastSeenAt);
        app.Clock.Now += TimeSpan.FromSeconds(91);
        var stale = await owner.GetFromJsonAsync<WorkbenchView>(path);
        Assert.Equal("stale", stale!.ContactState);
        Assert.Equal("active", stale.AdmissionState);
        Assert.Equal(registeredAt, stale.RegisteredAt);

        using (var busy = await SendAsync(daemon, HttpMethod.Put, path + "/presence", Presence("busy"), Key()))
            Assert.Equal(HttpStatusCode.NoContent, busy.StatusCode);
        var busyAt = app.Clock.Now;
        app.Clock.Now += TimeSpan.FromSeconds(10);
        using (var oldReplay = await SendAsync(daemon, HttpMethod.Put, path + "/presence", Presence("idle"), idleKey))
            Assert.Equal(HttpStatusCode.NoContent, oldReplay.StatusCode);
        var current = await owner.GetFromJsonAsync<WorkbenchView>(path);
        Assert.Equal("busy", current!.Presence!.Status);
        Assert.Equal(busyAt, current.LastSeenAt);
        Assert.Equal(admission, Assert.Single(app.Store.All<WorkbenchRecord>()));
        using (var changedReplay = await SendAsync(daemon, HttpMethod.Put, path + "/presence", Presence("notReady"), idleKey))
            await ProblemAsync(changedReplay, 409, "idempotency_conflict");

        app.Store.Seed(admission with { AdmissionState = "revoked" });
        using (var revoked = await SendAsync(daemon, HttpMethod.Put, path + "/presence", Presence("idle"), idleKey))
            await ProblemAsync(revoked, 403, "forbidden");
        using (var revoked = await SendAsync(daemon, HttpMethod.Put, path + "/registration", Registration(), registrationKey))
            await ProblemAsync(revoked, 403, "forbidden");
        Assert.Equal(busyAt, Assert.Single(app.Store.All<PresenceRecord>()).RecordedAt);
    }

    [Fact]
    public async Task ResourceScopedReadersCannotSeeSiblingWorkbenchesOrOtherPlantsAndRevocationIsImmediate()
    {
        using var app = new ApiTestApplication();
        using var owner = app.Human();
        using var reader = app.Human(ApiTestApplication.ReaderId, creator: false);
        var (a, _) = await CreateAsync(owner, "A");
        var (b, _) = await CreateAsync(owner, "B");
        app.Store.Seed(Workbench(app, a.Id, "box-a"));
        app.Store.Seed(Workbench(app, a.Id, "box-b"));
        app.Store.Seed(Workbench(app, b.Id, "box-a") with
        {
            Identity = new(ApiTestApplication.Tenant, Key(), Key())
        });
        var grant = new AccessRecord
        {
            Id = "reader-grant", PlantId = a.Id, ScopeId = Records.PlantScope(a.Id),
            Principal = new(ApiTestApplication.Tenant, ApiTestApplication.ReaderId),
            Role = "Plant.Reader", WorkbenchId = "box-a"
        };
        app.Store.Seed(grant);
        var list = await reader.GetFromJsonAsync<Page<WorkbenchView>>($"/v1/plants/{a.Id}/workbenches");
        Assert.Equal("box-a", Assert.Single(list!.Items).Id);
        using (var allowed = await reader.GetAsync($"/v1/plants/{a.Id}/workbenches/box-a"))
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        foreach (var path in new[] { $"/v1/plants/{a.Id}", $"/v1/plants/{a.Id}/workbenches/box-b",
            $"/v1/plants/{b.Id}/workbenches", $"/v1/plants/{b.Id}/workbenches/box-a" })
        {
            using var denied = await reader.GetAsync(path);
            await ProblemAsync(denied, 403, "forbidden");
        }
        using (var daemon = app.Daemon())
        using (var wrongPlant = await SendAsync(daemon, HttpMethod.Put,
            $"/v1/plants/{b.Id}/workbenches/box-a/registration", Registration(), Key()))
            await ProblemAsync(wrongPlant, 403, "forbidden");

        app.Store.Seed(grant with { WorkbenchId = null });
        var inherited = await reader.GetFromJsonAsync<Page<WorkbenchView>>($"/v1/plants/{a.Id}/workbenches");
        Assert.Equal(2, inherited!.Items.Count);
        using (var plantRead = await reader.GetAsync($"/v1/plants/{a.Id}"))
            Assert.Equal(HttpStatusCode.OK, plantRead.StatusCode);
        app.Store.Seed(grant with { Role = "unrecognized-role" });
        using var revoked = await reader.GetAsync($"/v1/plants/{a.Id}/workbenches/box-a");
        await ProblemAsync(revoked, 403, "forbidden");
    }

    private static WorkbenchRecord Workbench(ApiTestApplication app, string plantId, string id) => new()
    {
        Id = id, PlantId = plantId, ScopeId = Records.WorkbenchScope(plantId, id), Name = "Approved " + id,
        Identity = new(ApiTestApplication.Tenant, ApiTestApplication.DaemonId, ApiTestApplication.DaemonClientId),
        AdmissionState = "active", CreatedAt = app.Clock.Now
    };

    private static WorkbenchRegistration Registration() => new()
    {
        SchemaVersion = "1", MachineName = "observed-machine", HostVersion = "test-host", WorkerVersion = "test-worker",
        ProtocolVersion = "1", AssignmentSchemaVersions = ["1"], Capabilities = ["workspace", "verification"]
    };

    private static WorkbenchPresence Presence(string status) => new() { SchemaVersion = "1", Status = status };
}
