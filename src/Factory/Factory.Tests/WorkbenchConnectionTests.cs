using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Factory.Contracts;
using Factory.Workbench;
using Xunit;

namespace Factory.Tests;

public sealed class WorkbenchConnectionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"factory-connection-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task ActualClientResumesEnrollmentAndPublishesUsingOnlyTheBoundMachineIdentity()
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        using var create = new HttpRequestMessage(HttpMethod.Post, "/v1/plants")
            { Content = JsonContent.Create(new PlantWriteRequest { SchemaVersion = "1", Name = "Connected plant" }) };
        create.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var created = await human.SendAsync(create);
        created.EnsureSuccessStatusCode();
        var plant = (await created.Content.ReadFromJsonAsync<PlantView>())!;
        ConfigurationStore.Save(root, Configuration());
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Workbench Connection Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(180));
        var journal = BindingStore.Journal(root, plant.Id, certificate);
        var client = new EnrollmentClient(new PlantHttp(human, _ => Task.FromResult(app.Token(ApiTestApplication.OwnerId, ["Factory.User"]))));
        Assert.Contains(await client.PlantsAsync(CancellationToken.None), item => item.Id == plant.Id);
        await client.SubmitAsync(journal, certificate, CancellationToken.None);
        Assert.Equal(journal, BindingStore.Journal(root, plant.Id, certificate));
        await client.SubmitAsync(journal, certificate, CancellationToken.None);
        for (var i = 0; i < 3; i++)
            await app.AdvanceAsync(plant.Id, journal.OperationId);
        var view = await client.StatusAsync(journal, CancellationToken.None);
        var binding = EnrollmentClient.SaveBinding(root, journal, view);
        Assert.Equal(binding, BindingStore.Read(root));
        using var machine = new HttpClient(new LostPresenceResponse(app.Server.CreateHandler())) { BaseAddress = new Uri("http://localhost") };
        var machineTransport = new PlantHttp(machine, _ => Task.FromResult(
            app.Token(ApiTestApplication.DaemonId, ["Workbench.Connect"], false, ApiTestApplication.DaemonClientId)));
        var connection = new WorkbenchPlantConnection(machineTransport, binding, "test-release", root);
        await Assert.ThrowsAsync<HttpRequestException>(() => connection.PublishAsync(CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(LocalState.RuntimeDirectory(root), "pending-presence.json")));
        app.Clock.Now += TimeSpan.FromSeconds(20);
        connection = new WorkbenchPlantConnection(machineTransport, binding, "test-release", root);
        await connection.PublishAsync(CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(LocalState.RuntimeDirectory(root), "pending-presence.json")));
        var report = await human.GetFromJsonAsync<WorkbenchView>($"/v1/plants/{plant.Id}/workbenches/{binding.WorkbenchId}");
        Assert.Equal("online", report!.ContactState);
        Assert.Equal("notReady", report.Presence!.Status);
        Assert.Equal(app.Clock.Now, report.LastSeenAt);
        Assert.Equal("test-release", report.Registration!.HostVersion);
        Assert.Throws<InvalidDataException>(() => BindingStore.Journal(root, Guid.NewGuid().ToString(), certificate));
        Assert.Throws<InvalidDataException>(() => ConfigurationStore.Save(root, ConfigurationStore.Read(root) with { PlantServiceUrl = "https://different.example" }));
    }

    [Fact]
    public async Task RejectedBearerRequestsAFreshTokenForTheNextAttempt()
    {
        var refresh = false;
        using var http = new HttpClient(new RejectedHandler()) { BaseAddress = new Uri("https://plant.example") };
        var client = new PlantHttp(http, _ => Task.FromResult("test-token"), () => refresh = true);
        var failure = await Assert.ThrowsAsync<PlantConnectionException>(() =>
            client.WriteAsync(HttpMethod.Put, "v1/test", new { schemaVersion = "1" }, Guid.NewGuid().ToString(), CancellationToken.None));
        Assert.Equal(HttpStatusCode.Forbidden, failure.Status);
        Assert.True(refresh);
    }

    private static WorkbenchConfiguration Configuration() => new()
    {
        SchemaVersion = "1", InstallationId = Guid.NewGuid().ToString(), PlantServiceUrl = "https://plant.example",
        TenantId = ApiTestApplication.Tenant, ApiClientId = ApiTestApplication.Audience, InstallerClientId = ApiTestApplication.Audience,
        Distribution = "Ubuntu", LinuxUser = "worker", LinuxWorkspaceRoot = "/home/worker/work",
        AllowedRepositories = [], AllowShellExecution = false, AllowPublication = false
    };

    private sealed class RejectedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }

    private sealed class LostPresenceResponse(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private bool lost;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (!lost && response.IsSuccessStatusCode && request.RequestUri!.AbsolutePath.EndsWith("/presence", StringComparison.Ordinal))
            {
                lost = true;
                response.Dispose();
                throw new HttpRequestException("Simulated lost response after server commit.");
            }
            return response;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
