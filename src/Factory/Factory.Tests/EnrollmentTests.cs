using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Factory.Contracts;
using Factory.Plant.Service;
using Factory.Plant.Service.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Factory.Tests;

public sealed class EnrollmentTestApplication : ApiTestApplication
{
    public TestDirectory Directory { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<EnrollmentSettings>(settings =>
            {
                settings.Enabled = true;
                settings.InstallerClientId = Audience;
                settings.ProvisioningClientId = Audience;
                settings.ProvisioningObjectId = DaemonId;
                settings.ProvisioningCredential = "ManagedIdentity";
            });
            services.RemoveAll<IDirectoryProvisioner>();
            services.AddSingleton<IDirectoryProvisioner>(Directory);
            foreach (var descriptor in services.Where(item =>
                item.ServiceType == typeof(IHostedService) && item.ImplementationType == typeof(EnrollmentWorker)).ToArray())
                services.Remove(descriptor);
        });
    }

    public async Task AdvanceAsync(string plant, string operation)
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EnrollmentProcessor>().ProcessAsync(plant, operation, CancellationToken.None);
    }
}

public sealed class TestDirectory : IDirectoryProvisioner
{
    private DirectoryApplication? application;
    private DirectoryPrincipal? principal;
    public int Creates { get; private set; }
    public bool LoseCreateResponse { get; set; }
    public Task<DirectoryApplication?> FindApplicationAsync(string marker, string publicCertificate, CancellationToken cancellationToken) => Task.FromResult(application);
    public Task<DirectoryPrincipal?> FindPrincipalAsync(string clientId, CancellationToken cancellationToken) => Task.FromResult(principal);
    public Task<DirectoryApplication> CreateApplicationAsync(string marker, string publicCertificate, CancellationToken cancellationToken)
    {
        Creates++;
        application = new(Guid.NewGuid().ToString("D"), ApiTestApplication.DaemonClientId);
        if (LoseCreateResponse)
        {
            LoseCreateResponse = false;
            throw new HttpRequestException("Simulated lost create response.");
        }
        return Task.FromResult(application);
    }
    public Task<DirectoryPrincipal> CreatePrincipalAsync(string clientId, CancellationToken cancellationToken) =>
        Task.FromResult(principal = new(ApiTestApplication.DaemonId));
    public Task EnsureConnectRoleAsync(string principalId, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class EnrollmentTests
{
    [Fact]
    public async Task HumanEnrollmentProvisionsOneIdentityThenMachineActivatesRegistersAndAppearsInInventory()
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        var plant = await Plant(human);
        using var key = RSA.Create(2048);
        var draft = Draft(key);
        using var submitted = await Submit(human, plant, draft, key);
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        Assert.Empty(app.Store.All<WorkbenchRecord>());
        for (var i = 0; i < 3; i++)
            await app.AdvanceAsync(plant, draft.OperationId);
        var ready = await human.GetFromJsonAsync<EnrollmentView>($"/v1/plants/{plant}/enrollments/{draft.OperationId}");
        Assert.Equal("ready", ready!.State);
        Assert.Equal(1, app.Directory.Creates);
        using var wrong = app.Daemon(clientId: Guid.NewGuid().ToString());
        using var denied = await Activate(wrong, plant, draft.OperationId);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var machine = app.Daemon();
        using var activated = await Activate(machine, plant, draft.OperationId);
        Assert.Equal(HttpStatusCode.NoContent, activated.StatusCode);
        using var replayed = await Activate(machine, plant, draft.OperationId);
        Assert.Equal(HttpStatusCode.NoContent, replayed.StatusCode);
        machine.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        var basePath = $"/v1/plants/{plant}/workbenches/{ready.WorkbenchId}";
        using var registered = await machine.PutAsJsonAsync(basePath + "/registration", new WorkbenchRegistration
        {
            SchemaVersion = "1", MachineName = "new-workbench", HostVersion = "test", WorkerVersion = "unavailable",
            ProtocolVersion = "1", AssignmentSchemaVersions = ["1"], Capabilities = ["plant-connection"]
        });
        Assert.Equal(HttpStatusCode.NoContent, registered.StatusCode);
        machine.DefaultRequestHeaders.Remove("Idempotency-Key");
        machine.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var presence = await machine.PutAsJsonAsync(basePath + "/presence",
            new WorkbenchPresence { SchemaVersion = "1", Status = "notReady", Reason = "Execution is not installed." });
        Assert.Equal(HttpStatusCode.NoContent, presence.StatusCode);
        var inventory = await human.GetFromJsonAsync<Page<WorkbenchView>>($"/v1/plants/{plant}/workbenches");
        var item = Assert.Single(inventory!.Items);
        Assert.Equal("active", item.AdmissionState);
        Assert.Equal("online", item.ContactState);
        Assert.Equal("notReady", item.Presence!.Status);
        Assert.NotNull(item.Registration);
    }

    [Fact]
    public async Task LostDirectoryCreateResponseReconcilesWithoutCreatingASecondApplication()
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        var plant = await Plant(human);
        using var key = RSA.Create(2048);
        var draft = Draft(key);
        using var submitted = await Submit(human, plant, draft, key);
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        app.Directory.LoseCreateResponse = true;
        await app.AdvanceAsync(plant, draft.OperationId);
        Assert.NotNull(Assert.Single(app.Store.All<EnrollmentRecord>()).ApplicationCreateStartedAt);
        app.Clock.Now += TimeSpan.FromSeconds(11);
        for (var i = 0; i < 3; i++)
            await app.AdvanceAsync(plant, draft.OperationId);
        Assert.Equal("ready", Assert.Single(app.Store.All<EnrollmentRecord>()).State);
        Assert.Equal(1, app.Directory.Creates);
        using var retry = await Submit(human, plant, draft, key);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        using var changed = await Submit(human, plant, draft with { MachineName = "changed" }, key);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
    }

    [Fact]
    public async Task ProofCannotBeReusedWithAnotherDraftAndReaderOrWorkbenchScopedRegistrarCannotEnroll()
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        var plant = await Plant(human);
        using var key = RSA.Create(2048);
        var draft = Draft(key);
        var challenge = await Challenge(human, plant, draft);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/plants/{plant}/enrollments")
        {
            Content = JsonContent.Create(new EnrollmentSubmission(draft with { MachineName = "substituted" },
                challenge.Challenge, Sign(key, challenge.Challenge)))
        };
        request.Headers.Add("Idempotency-Key", draft.OperationId);
        using var invalid = await human.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var reader = app.Human(ApiTestApplication.ReaderId, false);
        app.Store.Seed(new AccessRecord
        {
            Id = "scoped-registrar", PlantId = plant, ScopeId = Records.PlantScope(plant),
            Principal = new(ApiTestApplication.Tenant, ApiTestApplication.ReaderId),
            Role = "Workbench.Registrar", WorkbenchId = "another-machine"
        });
        using var forbidden = await reader.PostAsJsonAsync($"/v1/plants/{plant}/enrollments/challenge",
            new EnrollmentChallengeRequest(draft));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        app.Store.Seed(app.Store.All<AccessRecord>().Single() with { WorkbenchId = null });
        using var allowed = await reader.PostAsJsonAsync($"/v1/plants/{plant}/enrollments/challenge",
            new EnrollmentChallengeRequest(draft));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task RevokedHumanAuthorityFailsClosedBeforeDirectoryMutation()
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        var plant = await Plant(human);
        using var key = RSA.Create(2048);
        var draft = Draft(key);
        using var submitted = await Submit(human, plant, draft, key);
        var previous = Assert.Single(app.Store.All<PlantRecord>());
        app.Store.Seed(previous with { Plant = previous.Plant with { Owner = new(ApiTestApplication.Tenant, ApiTestApplication.ReaderId) } });
        await app.AdvanceAsync(plant, draft.OperationId);
        Assert.Equal("failed", Assert.Single(app.Store.All<EnrollmentRecord>()).State);
        Assert.Equal(0, app.Directory.Creates);
    }

    [Fact]
    public async Task ASecondOperationForTheSameInstallationDoesNotCreateAnotherIdentity()
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        var plant = await Plant(human);
        using var key = RSA.Create(2048);
        var draft = Draft(key);
        using var first = await Submit(human, plant, draft, key);
        await app.AdvanceAsync(plant, draft.OperationId);
        var other = draft with { OperationId = Guid.NewGuid().ToString("D") };
        using var second = await Submit(human, plant, other, key);
        await app.AdvanceAsync(plant, other.OperationId);
        Assert.Equal("needsAttention", app.Store.All<EnrollmentRecord>().Single(item => item.Id == other.OperationId).State);
        Assert.Equal(1, app.Directory.Creates);
    }

    private static async Task<string> Plant(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/plants")
            { Content = JsonContent.Create(new PlantWriteRequest { SchemaVersion = "1", Name = "Enrollment plant" }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PlantView>())!.Id;
    }

    private static EnrollmentDraft Draft(RSA key)
    {
        var request = new CertificateRequest("CN=Factory Workbench Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(180));
        return new()
        {
            SchemaVersion = "1", OperationId = Guid.NewGuid().ToString("D"), InstallationId = Guid.NewGuid().ToString("D"),
            MachineName = "new-workbench", PublicCertificate = Convert.ToBase64String(certificate.RawData)
        };
    }
    private static async Task<EnrollmentChallenge> Challenge(HttpClient client, string plant, EnrollmentDraft draft)
    {
        using var response = await client.PostAsJsonAsync($"/v1/plants/{plant}/enrollments/challenge", new EnrollmentChallengeRequest(draft));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EnrollmentChallenge>())!;
    }
    private static async Task<HttpResponseMessage> Submit(HttpClient client, string plant, EnrollmentDraft draft, RSA key)
    {
        var challenge = await Challenge(client, plant, draft);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/plants/{plant}/enrollments")
            { Content = JsonContent.Create(new EnrollmentSubmission(draft, challenge.Challenge, Sign(key, challenge.Challenge))) };
        request.Headers.Add("Idempotency-Key", draft.OperationId);
        return await client.SendAsync(request);
    }
    private static string Sign(RSA key, string challenge) =>
        Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(challenge), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    private static Task<HttpResponseMessage> Activate(HttpClient client, string plant, string operation)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/plants/{plant}/enrollments/{operation}/activate");
        request.Headers.Add("Idempotency-Key", operation);
        return Send();
        async Task<HttpResponseMessage> Send()
        {
            using (request)
                return await client.SendAsync(request);
        }
    }
}
