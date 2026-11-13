using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Contracts;
using Factory.Plant.Service;
using Factory.Plant.Service.Storage;
using Xunit;

namespace Factory.Tests;

public sealed class PlantApiTests
{
    [Fact]
    public async Task CreationAndConditionalUpdatesAreDurableIdempotentAndOwnerBound()
    {
        using var app = new ApiTestApplication();
        using var owner = app.Human();
        using var other = app.Human(ApiTestApplication.ReaderId);
        var creationKey = Key();
        app.Store.ConflictsRemaining = 1;
        var (plant, originalTag) = await CreateAsync(owner, "First plant", creationKey);
        Assert.Equal(new(ApiTestApplication.Tenant, ApiTestApplication.OwnerId), plant.Owner);
        var (replayed, replayedTag) = await CreateAsync(owner, "First plant", creationKey);
        Assert.Equal(plant, replayed);
        Assert.Equal(originalTag, replayedTag);
        Assert.Single(app.Store.All<PlantRecord>());
        Assert.Single(app.Store.All<OwnerRecord>());
        Assert.Single(app.Store.All<AuditRecord>());
        Assert.Single(app.Store.All<OperationRecord>());

        using (var conflict = await SendAsync(owner, HttpMethod.Post, "/v1/plants", Write("Different"), creationKey))
            await ProblemAsync(conflict, 409, "idempotency_conflict");
        var (otherPlant, _) = await CreateAsync(other, "Other owner's plant", creationKey);
        Assert.NotEqual(plant.Id, otherPlant.Id);
        using (var denied = await other.GetAsync($"/v1/plants/{plant.Id}"))
            await ProblemAsync(denied, 403, "forbidden");
        using (var denied = await SendAsync(other, HttpMethod.Put, $"/v1/plants/{plant.Id}", Write("Hijacked"), Key(), originalTag))
            await ProblemAsync(denied, 403, "forbidden");
        var page = await owner.GetFromJsonAsync<Page<PlantView>>("/v1/plants");
        Assert.Equal(plant.Id, Assert.Single(page!.Items).Id);

        app.Clock.Now += TimeSpan.FromMinutes(1);
        var updateKey = Key();
        using var updated = await SendAsync(owner, HttpMethod.Put, $"/v1/plants/{plant.Id}", Write("Renamed"), updateKey, originalTag);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var changed = await updated.Content.ReadFromJsonAsync<PlantView>();
        Assert.NotNull(changed);
        Assert.Equal("Renamed", changed.Name);
        Assert.Equal(plant.Owner, changed.Owner);
        Assert.Equal(plant.CreatedAt, changed.CreatedAt);
        Assert.Equal(app.Clock.Now, changed.UpdatedAt);
        Assert.NotEqual(originalTag, updated.Headers.ETag!.ToString());
        using (var retry = await SendAsync(owner, HttpMethod.Put, $"/v1/plants/{plant.Id}", Write("Renamed"), updateKey, originalTag))
        {
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.Equal(changed, await retry.Content.ReadFromJsonAsync<PlantView>());
            Assert.Equal(updated.Headers.ETag, retry.Headers.ETag);
        }
        using (var stale = await SendAsync(owner, HttpMethod.Put, $"/v1/plants/{plant.Id}", Write("Lost update"), Key(), originalTag))
            await ProblemAsync(stale, 412, "etag_mismatch");
        using (var conflict = await SendAsync(owner, HttpMethod.Put, $"/v1/plants/{plant.Id}", Write("Changed retry"), updateKey, originalTag))
            await ProblemAsync(conflict, 409, "idempotency_conflict");
        Assert.Equal(2, app.Store.All<AuditRecord>().Count(record => record.PlantId == plant.Id));

        var stored = app.Store.All<PlantRecord>().Single(record => record.PlantId == plant.Id);
        app.Store.Seed(stored with { Plant = stored.Plant with { Owner = otherPlant.Owner } });
        using var revokedReplay = await SendAsync(owner, HttpMethod.Post, "/v1/plants", Write("First plant"), creationKey);
        await ProblemAsync(revokedReplay, 403, "forbidden");
    }

    [Fact]
    public async Task ConcurrentOperationsNeverDuplicateCreationOrOverwriteAStaleRevision()
    {
        using var app = new ApiTestApplication();
        using var owner = app.Human();
        var key = Key();
        var created = await Task.WhenAll(CreateAsync(owner, "One", key), CreateAsync(owner, "One", key));
        Assert.Equal(created[0], created[1]);
        Assert.Single(app.Store.All<PlantRecord>());
        Assert.Single(app.Store.All<OwnerRecord>());

        var path = $"/v1/plants/{created[0].Plant.Id}";
        var responses = await Task.WhenAll(
            SendAsync(owner, HttpMethod.Put, path, Write("A"), Key(), created[0].ETag),
            SendAsync(owner, HttpMethod.Put, path, Write("B"), Key(), created[0].ETag));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.PreconditionFailed);
            Assert.Equal(2, app.Store.All<OperationRecord>().Length);
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":\"2\",\"name\":\"Plant\"}")]
    [InlineData("{\"schemaVersion\":\"1\",\"name\":null}")]
    [InlineData("{\"schemaVersion\":\"1\",\"name\":\"   \"}")]
    [InlineData("{\"schemaVersion\":\"1\",\"Name\":\"Plant\"}")]
    [InlineData("{\"schemaVersion\":\"1\",\"name\":\"Plant\",\"owner\":{}}")]
    [InlineData("{\"schemaVersion\":\"1\",\"name\":\"A\",\"name\":\"B\"}")]
    public async Task InvalidBodiesNeverWriteState(string json)
    {
        using var app = new ApiTestApplication();
        using var owner = app.Human();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/plants")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", Key());
        using var response = await owner.SendAsync(request);
        await ProblemAsync(response, 400, "invalid_request");
        Assert.Empty(app.Store.All<PlantRecord>());
    }

    [Fact]
    public async Task HeadersMediaTypesBoundsAndMissingResourcesFailExplicitly()
    {
        using var app = new ApiTestApplication();
        using var owner = app.Human();
        using (var missingKey = await SendAsync(owner, HttpMethod.Post, "/v1/plants", Write("Missing key")))
            await ProblemAsync(missingKey, 400, "invalid_request");
        using (var overlong = await SendAsync(owner, HttpMethod.Post, "/v1/plants", Write(new string('x', 129)), Key()))
            await ProblemAsync(overlong, 400, "invalid_request");
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/v1/plants") { Content = new StringContent("plain text") })
        {
            request.Headers.Add("Idempotency-Key", Key());
            using var response = await owner.SendAsync(request);
            await ProblemAsync(response, 415, "unsupported_media_type");
        }
        var (plant, _) = await CreateAsync(owner, "Plant");
        var path = $"/v1/plants/{plant.Id}";
        using (var missingTag = await SendAsync(owner, HttpMethod.Put, path, Write("Renamed"), Key()))
            await ProblemAsync(missingTag, 428, "precondition_required");
        foreach (var tag in new[] { "*", "W/\"weak\"", "\"one\", \"two\"" })
        {
            using var invalidTag = await SendAsync(owner, HttpMethod.Put, path, Write("Renamed"), Key(), tag);
            await ProblemAsync(invalidTag, 400, "invalid_request");
        }
        using (var missing = await SendAsync(owner, HttpMethod.Put, $"/v1/plants/{Key()}", Write("Not an upsert"), Key(), "\"revision\""))
            await ProblemAsync(missing, 404, "plant_not_found");
        foreach (var route in new[] { "/v1/plants/not-a-uuid", "/v1/plants?pageSize=0", "/v1/plants?pageSize=101",
            "/v1/plants?pageSize=1&pageSize=2", "/v1/plants?owner=someone", "/v1/plants?continuationToken=" })
        {
            using var invalid = await owner.GetAsync(route);
            await ProblemAsync(invalid, 400, "invalid_request");
        }
        Assert.Single(app.Store.All<PlantRecord>());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("cookie")]
    [InlineData("forwarded-header")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("tenant")]
    [InlineData("expired")]
    [InlineData("signature")]
    [InlineData("version")]
    [InlineData("object-id")]
    public async Task AuthenticationRequiresATrustedVersionTwoApiToken(string failure)
    {
        using var app = new ApiTestApplication();
        using var wrongKey = RSA.Create(2048);
        var token = failure switch
        {
            "issuer" => app.Token(ApiTestApplication.OwnerId, ["Factory.User"], issuer: "https://issuer.example.invalid"),
            "audience" => app.Token(ApiTestApplication.OwnerId, ["Factory.User"], audience: Key()),
            "tenant" => app.Token(ApiTestApplication.OwnerId, ["Factory.User"], tenant: Key()),
            "expired" => app.Token(ApiTestApplication.OwnerId, ["Factory.User"], expired: true),
            "signature" => app.Token(ApiTestApplication.OwnerId, ["Factory.User"], key: wrongKey),
            "version" => app.Token(ApiTestApplication.OwnerId, ["Factory.User"], version: "1.0"),
            "object-id" => app.Token("not-an-object-id", ["Factory.User"]),
            _ => null
        };
        using var client = app.Client(token);
        if (failure == "cookie")
            client.DefaultRequestHeaders.Add("Cookie", "Factory.Session=not-a-bearer-token");
        if (failure == "forwarded-header")
            client.DefaultRequestHeaders.Add("x-agent-user-id", ApiTestApplication.OwnerId);
        using var response = await client.GetAsync("/v1/plants");
        await ProblemAsync(response, 401, "unauthenticated");
        Assert.Null(response.Headers.Location);
        Assert.NotEmpty(response.Headers.WwwAuthenticate);
    }

    [Fact]
    public async Task ServiceEntryRolesAndCallerKindsDoNotGrantPlantAuthority()
    {
        using var app = new ApiTestApplication();
        using var withoutRole = app.Client(app.Token(ApiTestApplication.OwnerId, []));
        using var withoutScope = app.Client(app.Token(ApiTestApplication.OwnerId, ["Factory.User"], scope: "Other.Scope"));
        using var appAsUser = app.Client(app.Token(ApiTestApplication.OwnerId, ["Factory.User"], delegated: false));
        foreach (var client in new[] { withoutRole, withoutScope, appAsUser })
        {
            using var response = await client.GetAsync("/v1/plants");
            await ProblemAsync(response, 403, "forbidden");
        }
        using var reader = app.Human(creator: false);
        using var deniedCreate = await SendAsync(reader, HttpMethod.Post, "/v1/plants", Write("No creator role"), Key());
        await ProblemAsync(deniedCreate, 403, "forbidden");
    }

    [Fact]
    public async Task PaginationIsFilteredProtectedAndBoundToTheCallerAndQuery()
    {
        using var app = new ApiTestApplication();
        using var owner = app.Human();
        using var other = app.Human(ApiTestApplication.ReaderId);
        var (a, _) = await CreateAsync(owner, "A");
        var (b, _) = await CreateAsync(owner, "B");
        await CreateAsync(other, "Not visible");
        var found = new List<string>();
        var first = await owner.GetFromJsonAsync<Page<PlantView>>("/v1/plants?pageSize=1");
        Assert.NotNull(first!.ContinuationToken);
        var page = first;
        while (true)
        {
            found.AddRange(page.Items.Select(plant => plant.Id));
            if (page.ContinuationToken is null)
                break;
            page = (await owner.GetFromJsonAsync<Page<PlantView>>(
                "/v1/plants?pageSize=1&continuationToken=" + Uri.EscapeDataString(page.ContinuationToken)))!;
        }
        Assert.Equal(new[] { a.Id, b.Id }.Order(StringComparer.Ordinal), found.Order(StringComparer.Ordinal));
        var token = Uri.EscapeDataString(first.ContinuationToken);
        using (var changedCaller = await other.GetAsync($"/v1/plants?pageSize=1&continuationToken={token}"))
            await ProblemAsync(changedCaller, 400, "invalid_request");
        using (var changedSize = await owner.GetAsync($"/v1/plants?pageSize=2&continuationToken={token}"))
            await ProblemAsync(changedSize, 400, "invalid_request");
        using (var changedRoute = await owner.GetAsync($"/v1/plants/{a.Id}/workbenches?pageSize=1&continuationToken={token}"))
            await ProblemAsync(changedRoute, 400, "invalid_request");
        using (var tampered = await owner.GetAsync($"/v1/plants?pageSize=1&continuationToken={token}x"))
            await ProblemAsync(tampered, 400, "invalid_request");
        app.Clock.Now += TimeSpan.FromHours(2);
        using var expired = await owner.GetAsync($"/v1/plants?pageSize=1&continuationToken={token}");
        await ProblemAsync(expired, 400, "invalid_request");
    }

    [Fact]
    public async Task UnavailableStorageAndContentionCannotReportSuccessOrLeavePartialCreation()
    {
        using var app = new ApiTestApplication();
        using var owner = app.Human();
        app.Store.ConflictsRemaining = 3;
        var key = Key();
        using (var exhausted = await SendAsync(owner, HttpMethod.Post, "/v1/plants", Write("Pending"), key))
        {
            await ProblemAsync(exhausted, 503, "service_unavailable");
            Assert.NotNull(exhausted.Headers.RetryAfter);
            Assert.Equal(3, app.Store.CommitAttempts);
        }
        Assert.Empty(app.Store.All<PlantRecord>());
        Assert.Empty(app.Store.All<OwnerRecord>());
        Assert.Empty(app.Store.All<AuditRecord>());
        app.Store.FailWrites = true;
        using (var failed = await SendAsync(owner, HttpMethod.Post, "/v1/plants", Write("Pending"), key))
            await ProblemAsync(failed, 503, "service_unavailable");
        app.Store.FailWrites = false;
        await CreateAsync(owner, "Pending", key);
        Assert.Single(app.Store.All<PlantRecord>());
        Assert.Single(app.Store.All<OwnerRecord>());
        Assert.Single(app.Store.All<OperationRecord>());
    }

    internal static string Key() => Guid.NewGuid().ToString("D");
    internal static PlantWriteRequest Write(string name) => new() { SchemaVersion = "1", Name = name };

    internal static async Task<(PlantView Plant, string ETag)> CreateAsync(HttpClient client, string name, string? key = null)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/v1/plants", Write(name), key ?? Key());
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var plant = await response.Content.ReadFromJsonAsync<PlantView>();
        Assert.NotNull(plant);
        Assert.Equal($"/v1/plants/{plant.Id}", response.Headers.Location!.ToString());
        return (plant, response.Headers.ETag!.ToString());
    }

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, object body,
        string? key = null, string? tag = null)
    {
        using var request = new HttpRequestMessage(method, route) { Content = JsonContent.Create(body) };
        if (key is not null)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        if (tag is not null)
            request.Headers.TryAddWithoutValidation("If-Match", tag);
        return await client.SendAsync(request);
    }

    internal static async Task ProblemAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(status, problem.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("correlationId").GetString()));
        Assert.False(problem.TryGetProperty("traceId", out _));
    }
}
