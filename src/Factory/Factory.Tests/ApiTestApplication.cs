using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Factory.Contracts;
using Factory.Plant.Service;
using Factory.Plant.Service.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Factory.Tests;

public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class ApiTestApplication : WebApplicationFactory<Program>
{
    public const string Tenant = "10000000-0000-0000-0000-000000000001";
    public const string Audience = "10000000-0000-0000-0000-000000000002";
    public const string OwnerId = "20000000-0000-0000-0000-000000000001";
    public const string ReaderId = "20000000-0000-0000-0000-000000000002";
    public const string DaemonId = "30000000-0000-0000-0000-000000000001";
    public const string DaemonClientId = "30000000-0000-0000-0000-000000000002";
    public const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";
    private readonly RSA signingKey = RSA.Create(2048);
    public TestRecordStore Store { get; } = new();
    public TestClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Factory:TenantId"] = Tenant,
            ["Factory:ClientId"] = Audience,
            ["Factory:CosmosEndpoint"] = "https://cosmos.example.invalid",
            ["Factory:DataCredential"] = "ManagedIdentity",
            ["Factory:DataIdentityClientId"] = Audience,
            ["Factory:SupportedProtocolVersions:0"] = "1",
            ["Factory:SupportedAssignmentSchemaVersions:0"] = "1"
        }));
        builder.ConfigureTestServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.RemoveAll<IRecordStore>();
            services.AddSingleton<IRecordStore>(Store);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                configuration.SigningKeys.Add(new RsaSecurityKey(signingKey.ExportParameters(false)) { KeyId = "test-key" });
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            });
        });
    }

    public HttpClient Human(string subject = OwnerId, bool creator = true) => Client(Token(subject,
        creator ? ["Factory.User", "Factory.PlantCreator"] : ["Factory.User"]));

    public HttpClient Daemon(string subject = DaemonId, string clientId = DaemonClientId) =>
        Client(Token(subject, ["Workbench.Connect"], delegated: false, clientId: clientId));

    public HttpClient Client(string? token = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public string Token(string subject, string[] roles, bool delegated = true, string clientId = Audience,
        string tenant = Tenant, string issuer = Issuer, string audience = Audience, bool expired = false,
        string version = "2.0", string scope = "Plant.Access", RSA? key = null)
    {
        var claims = new List<Claim>
        {
            new("tid", tenant), new("oid", subject), new("azp", clientId), new("ver", version),
            new("idtyp", delegated ? "user" : "app")
        };
        if (delegated)
            claims.Add(new("scp", scope));
        claims.AddRange(roles.Select(role => new Claim("roles", role)));
        var token = new JwtSecurityToken(issuer, audience, claims, DateTime.UtcNow.AddMinutes(-30),
            DateTime.UtcNow.AddMinutes(expired ? -5 : 10),
            new SigningCredentials(new RsaSecurityKey(key ?? signingKey) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            signingKey.Dispose();
    }
}

public sealed class UnconfiguredApplication : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Factory:TenantId"] = "", ["Factory:ClientId"] = "" }));
        builder.ConfigureTestServices(services => services.AddDataProtection().UseEphemeralDataProtectionProvider());
    }
}

public sealed class TestRecordStore : IRecordStore
{
    private readonly object gate = new();
    private readonly Dictionary<(string Scope, string Id), StoredRecord> records = [];
    public int ConflictsRemaining { get; set; }
    public bool FailWrites { get; set; }
    public int CommitAttempts { get; private set; }

    public void Seed(StoreRecord record)
    {
        lock (gate)
            records[(record.ScopeId, record.Id)] = new(Clone(record), $"\"{Guid.NewGuid():N}\"");
    }

    public T[] All<T>() where T : StoreRecord
    {
        lock (gate)
            return records.Values.Select(value => Clone(value.Record)).OfType<T>().ToArray();
    }

    public Task<StoredRecord?> ReadAsync(string scopeId, string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var value = records.GetValueOrDefault((scopeId, id));
            return Task.FromResult(value is null ? null : value with { Record = Clone(value.Record) });
        }
    }

    public Task<IReadOnlyList<StoreRecord>> QueryAsync(RecordQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            IReadOnlyList<StoreRecord> result = records.Values.Select(value => value.Record)
                .Where(record => Type(record) == query.Type && (query.PlantId is null || record.PlantId == query.PlantId) &&
                    (query.ScopeId is null || record.ScopeId == query.ScopeId) &&
                    (query.Principal is null || record is AccessRecord access && access.Principal == query.Principal) &&
                    (query.AfterId is null || StringComparer.Ordinal.Compare(record.Id, query.AfterId) > 0))
                .OrderBy(record => record.Id, StringComparer.Ordinal).Take(query.Limit).Select(Clone).ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<bool> CommitAsync(string scopeId, IReadOnlyList<RecordWrite> writes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            CommitAttempts++;
            if (FailWrites)
                throw new CosmosException("Test persistence outage.", HttpStatusCode.ServiceUnavailable, 0, "test", 0);
            if (ConflictsRemaining > 0)
            {
                ConflictsRemaining--;
                return Task.FromResult(false);
            }
            if (writes.Any(write => write.Record.ScopeId != scopeId))
                throw new InvalidOperationException("The test transaction crossed partitions.");
            foreach (var write in writes)
            {
                var previous = records.GetValueOrDefault((scopeId, write.Record.Id));
                if (write.ExpectedETag is null ? previous is not null : previous?.ETag != write.ExpectedETag)
                    return Task.FromResult(false);
            }
            foreach (var write in writes)
                records[(scopeId, write.Record.Id)] = new(Clone(write.Record), $"\"{Guid.NewGuid():N}\"");
            return Task.FromResult(true);
        }
    }

    private static StoreRecord Clone(StoreRecord record) =>
        JsonSerializer.Deserialize<StoreRecord>(JsonSerializer.SerializeToUtf8Bytes(record, Records.Json), Records.Json)!;
    private static string Type(StoreRecord record) =>
        JsonSerializer.SerializeToElement(record, Records.Json).GetProperty("type").GetString()!;
}
