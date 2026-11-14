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
    public new HttpClient Human(string subject = OwnerId, bool creator = true) => Client(Token(subject,
        creator ? ["Factory.User", "Factory.PlantCreator", ApiCaller.EnrollmentAdministratorRole] : ["Factory.User"]));
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
        await scope.ServiceProvider.GetRequiredService<EnrollmentProcessor>().ProcessAsync(operation, CancellationToken.None);
    }
}

public sealed class TestDirectory : IDirectoryProvisioner
{
    private DirectoryApplication? application;
    private DirectoryPrincipal? principal;
    public int Creates { get; private set; }
    public bool LoseCreateResponse { get; set; }
    public bool HideApplication { get; set; }
    public int PrincipalCreates { get; private set; }
    public int PrincipalForbiddenFailures { get; set; }
    public bool RejectAfterPrincipalCreated { get; set; }
    public bool RejectApplicationReconciliation { get; set; }
    public int DeleteFailures { get; set; }
    public int ApplicationsDeleted { get; private set; }
    public int PrincipalsDeleted { get; private set; }
    public Func<Task>? AfterApplicationCreated { get; set; }
    public Task<DirectoryApplication?> FindApplicationAsync(string marker, string publicCertificate, CancellationToken cancellationToken)
    {
        if (RejectApplicationReconciliation)
            throw new DirectoryProvisioningException("The application does not belong to this enrollment.");
        return Task.FromResult(HideApplication ? null : application);
    }
    public Task<DirectoryPrincipal?> FindPrincipalAsync(string clientId, CancellationToken cancellationToken) => Task.FromResult(principal);
    public async Task<DirectoryApplication> CreateApplicationAsync(string marker, string publicCertificate, CancellationToken cancellationToken)
    {
        Creates++;
        application = new(Guid.NewGuid().ToString("D"), Creates == 1 ? ApiTestApplication.DaemonClientId : Guid.NewGuid().ToString("D"));
        if (AfterApplicationCreated is not null)
            await AfterApplicationCreated();
        if (LoseCreateResponse)
        {
            LoseCreateResponse = false;
            throw new HttpRequestException("Simulated lost create response.");
        }
        return application;
    }
    public Task<DirectoryPrincipal> CreatePrincipalAsync(string clientId, CancellationToken cancellationToken)
    {
        PrincipalCreates++;
        if (PrincipalForbiddenFailures-- > 0)
            throw new HttpRequestException("Simulated Graph permission rejection.", null, HttpStatusCode.Forbidden);
        principal = new(Creates == 1 ? ApiTestApplication.DaemonId : Guid.NewGuid().ToString("D"));
        if (RejectAfterPrincipalCreated)
            throw new HttpRequestException("Simulated rejected post-create ownership check.", null, HttpStatusCode.Forbidden);
        return Task.FromResult(principal);
    }
    public Task EnsureConnectRoleAsync(string principalId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task DeletePrincipalAsync(string principalId, string clientId, CancellationToken cancellationToken)
    {
        if (DeleteFailures-- > 0)
            throw new HttpRequestException("Temporary directory failure.", null, HttpStatusCode.ServiceUnavailable);
        if (principal is not null)
        {
            Assert.Equal(principal.ObjectId, principalId);
            Assert.Equal(application!.ClientId, clientId);
            PrincipalsDeleted++;
            principal = null;
        }
        return Task.CompletedTask;
    }
    public Task DeleteApplicationAsync(string applicationId, string clientId, CancellationToken cancellationToken)
    {
        if (application is not null)
        {
            Assert.Equal(application.ObjectId, applicationId);
            Assert.Equal(application.ClientId, clientId);
            ApplicationsDeleted++;
            application = null;
        }
        return Task.CompletedTask;
    }
}

public sealed class EnrollmentTests
{
    [Fact]
    public async Task SignedRetryAdoptsReconciledPrincipalWithoutRepeatingItsCreation()
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        var plant = await Plant(human);
        using var key = RSA.Create(2048);
        var draft = Draft(key);
        using var submitted = await Submit(human, plant, draft, key);
        await app.AdvanceAsync(plant, draft.OperationId);
        app.Directory.RejectAfterPrincipalCreated = true;
        await app.AdvanceAsync(plant, draft.OperationId);
        Assert.Equal("needsAttention", Assert.Single(app.Store.All<EnrollmentRecord>()).State);
        using var retry = await Submit(human, plant, draft, key);
        retry.EnsureSuccessStatusCode();
        for (var i = 0; i < 2; i++)
            await app.AdvanceAsync(plant, draft.OperationId);
        var recovered = Assert.Single(app.Store.All<EnrollmentRecord>());
        Assert.Equal("ready", recovered.State);
        Assert.Equal(ApiTestApplication.DaemonId, recovered.ServicePrincipalId);
        Assert.Equal(1, app.Directory.Creates);
        Assert.Equal(1, app.Directory.PrincipalCreates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignedRetryReconcilesRejectedPrincipalCreationWithoutReplacingApplication(bool rejectReconciliation)
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        var plant = await Plant(human);
        using var key = RSA.Create(2048);
        var draft = Draft(key);
        using var submitted = await Submit(human, plant, draft, key);
        await app.AdvanceAsync(plant, draft.OperationId);
        app.Directory.PrincipalForbiddenFailures = 1;
        await app.AdvanceAsync(plant, draft.OperationId);
        var failed = Assert.Single(app.Store.All<EnrollmentRecord>());
        Assert.Equal("needsAttention", failed.State);
        var application = failed.ApplicationObjectId;
        var client = failed.ClientId;
        app.Clock.Now += TimeSpan.FromMinutes(3);
        app.Directory.RejectApplicationReconciliation = rejectReconciliation;
        using var retry = await Submit(human, plant, draft, key);
        Assert.Equal("provisioning", (await retry.Content.ReadFromJsonAsync<EnrollmentView>())!.State);
        for (var i = 0; i < 4; i++)
            await app.AdvanceAsync(plant, draft.OperationId);
        var recovered = Assert.Single(app.Store.All<EnrollmentRecord>());
        Assert.Equal(rejectReconciliation ? "needsAttention" : "ready", recovered.State);
        Assert.Equal(application, recovered.ApplicationObjectId);
        Assert.Equal(client, recovered.ClientId);
        Assert.Equal(draft.OperationId, recovered.Id);
        Assert.Equal(draft.PublicCertificate, recovered.Draft.PublicCertificate);
        Assert.Equal(1, app.Directory.Creates);
        Assert.Equal(rejectReconciliation ? 1 : 2, app.Directory.PrincipalCreates);
    }

    [Fact]
    public async Task SignedRetryDoesNotRepeatUncertainApplicationCreationOrAcceptChangedDraft()
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        var plant = await Plant(human);
        using var key = RSA.Create(2048);
        var draft = Draft(key);
        app.Directory.LoseCreateResponse = true;
        app.Directory.HideApplication = true;
        using var submitted = await Submit(human, plant, draft, key);
        await app.AdvanceAsync(plant, draft.OperationId);
        app.Clock.Now += TimeSpan.FromMinutes(3);
        await app.AdvanceAsync(plant, draft.OperationId);
        Assert.Equal("needsAttention", Assert.Single(app.Store.All<EnrollmentRecord>()).State);
        using var changed = await Submit(human, plant, draft with { MachineName = "different" }, key);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var retry = await Submit(human, plant, draft, key);
        Assert.Equal("provisioning", (await retry.Content.ReadFromJsonAsync<EnrollmentView>())!.State);
        await app.AdvanceAsync(plant, draft.OperationId);
        Assert.Equal("needsAttention", Assert.Single(app.Store.All<EnrollmentRecord>()).State);
        Assert.Equal(1, app.Directory.Creates);
        Assert.Equal(0, app.Directory.PrincipalCreates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeviceRetiresOnlyItsOwnIdentityAndServerFinishesWithoutDevice(bool activate)
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        var plant = await Plant(human);
        using var key = RSA.Create(2048);
        var draft = Draft(key);
        using var submitted = await Submit(human, plant, draft, key);
        for (var i = 0; i < 3; i++)
            await app.AdvanceAsync(plant, draft.OperationId);
        using var machine = app.Daemon();
        if (activate)
        {
            using var activated = await Activate(machine, plant, draft.OperationId);
            activated.EnsureSuccessStatusCode();
            await Assign(human, draft.OperationId, plant, 0);
        }
        var path = $"/v1/enrollments/{draft.OperationId}/retire-self";
        async Task<HttpResponseMessage> Retire(HttpClient client, string? operation = null, object? body = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Add("Idempotency-Key", operation ?? draft.OperationId);
            if (body is not null)
                request.Content = JsonContent.Create(body);
            return await client.SendAsync(request);
        }
        using var wrongClient = app.Daemon(clientId: Guid.NewGuid().ToString("D"));
        using var wrongPrincipal = app.Daemon(subject: Guid.NewGuid().ToString("D"));
        using var forbiddenClient = await Retire(wrongClient);
        using var forbiddenPrincipal = await Retire(wrongPrincipal);
        using var forbiddenHuman = await Retire(human);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenClient.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenPrincipal.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenHuman.StatusCode);
        using var badKey = await Retire(machine, Guid.NewGuid().ToString("D"));
        using var badBody = await Retire(machine, body: new { arbitrary = "target" });
        Assert.Equal(HttpStatusCode.BadRequest, badKey.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badBody.StatusCode);
        app.Services.GetRequiredService<EnrollmentSettings>().Enabled = false;
        using var retired = await Retire(machine);
        using var replay = await Retire(machine);
        Assert.Equal(HttpStatusCode.Accepted, retired.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        using var blocked = await machine.GetAsync($"/v1/enrollments/{draft.OperationId}/connection");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        await app.AdvanceAsync(plant, draft.OperationId);
        Assert.Equal("retired", Assert.Single(app.Store.All<EnrollmentRecord>()).State);
        Assert.Equal(1, app.Directory.ApplicationsDeleted);
        using var completedReplay = await Retire(machine);
        Assert.Equal(HttpStatusCode.OK, completedReplay.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisablingNewEnrollmentDoesNotDisableExistingConnectionsOrRetirement(bool removeProvisioner)
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        var plant = await Plant(human);
        using var key = RSA.Create(2048);
        var draft = Draft(key);
        using var submitted = await Submit(human, plant, draft, key);
        for (var i = 0; i < 3; i++)
            await app.AdvanceAsync(plant, draft.OperationId);
        var settings = app.Services.GetRequiredService<EnrollmentSettings>();
        settings.Enabled = false;
        using var machine = app.Daemon();
        using var activated = await Activate(machine, plant, draft.OperationId);
        Assert.Equal(HttpStatusCode.NoContent, activated.StatusCode);
        using var status = await human.GetAsync($"/v1/enrollments/{draft.OperationId}");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using var presence = await Report(machine, $"/v1/enrollments/{draft.OperationId}/presence",
            new WorkbenchPresence { SchemaVersion = "1", Status = "notReady" });
        Assert.Equal(HttpStatusCode.NoContent, presence.StatusCode);
        using var denied = await human.PostAsJsonAsync("/v1/enrollments/challenge", new EnrollmentChallengeRequest(draft));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        if (removeProvisioner)
            settings.ProvisioningClientId = "";
        using var revoked = await Revoke(human, plant, draft.OperationId);
        Assert.Equal(HttpStatusCode.Accepted, revoked.StatusCode);
        var operation = Assert.Single(app.Store.All<EnrollmentRecord>());
        if (removeProvisioner)
        {
            Assert.Equal("retirementNeedsAttention", operation.State);
            Assert.Contains("configuration", operation.Error);
            settings.ProvisioningClientId = ApiTestApplication.Audience;
            using var retried = await Revoke(human, plant, draft.OperationId);
            Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
        }
        await app.AdvanceAsync(plant, draft.OperationId);
        Assert.Equal("retired", Assert.Single(app.Store.All<EnrollmentRecord>()).State);
        using var blocked = await machine.GetAsync($"/v1/enrollments/{draft.OperationId}/connection");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public async Task AssignmentRejectsVersionsThatCannotBeAdvanced(long version)
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        using var response = await human.PutAsJsonAsync($"/v1/enrollments/{Guid.NewGuid():D}/assignment",
            new EnrollmentAssignment("1", version));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationBetweenAProvisioningCheckpointAndItsReadCannotResumeProvisioning(bool afterCreateCheckpoint)
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        var plant = await Plant(human);
        using var key = RSA.Create(2048);
        var draft = Draft(key);
        using var submitted = await Submit(human, plant, draft, key);
        app.Store.AfterCommit = async writes =>
        {
            if (!writes.Any(write => write.Record is EnrollmentRecord
                { State: "provisioning" } operation && (operation.ApplicationCreateStartedAt is not null) == afterCreateCheckpoint))
                return;
            app.Store.AfterCommit = null;
            using var revoked = await Revoke(human, plant, draft.OperationId);
            Assert.Equal(HttpStatusCode.Accepted, revoked.StatusCode);
        };
        await app.AdvanceAsync(plant, draft.OperationId);
        Assert.Equal("revoking", Assert.Single(app.Store.All<EnrollmentRecord>()).State);
        Assert.Equal(0, app.Directory.Creates);
    }

    [Fact]
    public async Task AssignMoveAndUnassignPreserveTheDeviceIdentityAndFenceOldPlantReports()
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        var firstPlant = await Plant(human);
        var secondPlant = await Plant(human);
        using var key = RSA.Create(2048);
        var draft = Draft(key);
        using var submitted = await Submit(human, firstPlant, draft, key);
        for (var i = 0; i < 3; i++)
            await app.AdvanceAsync(firstPlant, draft.OperationId);
        using var machine = app.Daemon();
        using var activated = await Activate(machine, firstPlant, draft.OperationId);
        var original = Assert.Single(app.Store.All<EnrollmentRecord>());
        var assigned = await Assign(human, draft.OperationId, firstPlant, 0);
        var path = $"/v1/plants/{firstPlant}/workbenches/{assigned.WorkbenchId}";
        using var registration = await Report(machine, path + "/registration", WorkbenchApiTests.Registration());
        Assert.Equal(HttpStatusCode.NoContent, registration.StatusCode);

        var moved = await Assign(human, draft.OperationId, secondPlant, 1);
        Assert.Equal(2, moved.AssignmentVersion);
        using var oldPresence = await Report(machine, path + "/presence",
            new WorkbenchPresence { SchemaVersion = "1", Status = "notReady", AssignmentVersion = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, oldPresence.StatusCode);
        using var staleAssignment = await human.PutAsJsonAsync($"/v1/enrollments/{draft.OperationId}/assignment",
            new EnrollmentAssignment("1", 0, firstPlant));
        Assert.Equal(HttpStatusCode.Conflict, staleAssignment.StatusCode);
        var unassigned = await Assign(human, draft.OperationId, null, 2);
        Assert.Null(unassigned.PlantId);
        using var factoryPresence = await Report(machine, $"/v1/enrollments/{draft.OperationId}/presence",
            new WorkbenchPresence { SchemaVersion = "1", Status = "notReady" });
        Assert.Equal(HttpStatusCode.NoContent, factoryPresence.StatusCode);
        await Assign(human, draft.OperationId, firstPlant, 3);
        using var staleGeneration = await Report(machine, path + "/registration",
            WorkbenchApiTests.Registration() with { AssignmentVersion = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, staleGeneration.StatusCode);
        using var currentGeneration = await Report(machine, path + "/registration",
            WorkbenchApiTests.Registration() with { AssignmentVersion = 4 });
        Assert.Equal(HttpStatusCode.NoContent, currentGeneration.StatusCode);
        var current = Assert.Single(app.Store.All<EnrollmentRecord>());
        Assert.Equal(original.ApplicationObjectId, current.ApplicationObjectId);
        Assert.Equal(original.ClientId, current.ClientId);
        Assert.Equal(original.ServicePrincipalId, current.ServicePrincipalId);
        Assert.Equal(1, app.Directory.Creates);
        Assert.Equal(0, app.Directory.ApplicationsDeleted);
    }

    [Fact]
    public async Task AssignmentRequiresAdministrationAndAuthorityOverBothPlants()
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        using var other = app.Human(ApiTestApplication.ReaderId);
        var ownPlant = await Plant(human);
        var otherPlant = await Plant(other);
        using var key = RSA.Create(2048);
        var draft = Draft(key);
        using var submitted = await Submit(human, ownPlant, draft, key);
        for (var i = 0; i < 3; i++)
            await app.AdvanceAsync(ownPlant, draft.OperationId);
        using var unauthorizedTarget = await human.PutAsJsonAsync($"/v1/enrollments/{draft.OperationId}/assignment",
            new EnrollmentAssignment("1", 0, otherPlant));
        Assert.Equal(HttpStatusCode.Forbidden, unauthorizedTarget.StatusCode);
        await Assign(human, draft.OperationId, ownPlant, 0);
        using var unauthorizedSource = await other.PutAsJsonAsync($"/v1/enrollments/{draft.OperationId}/assignment",
            new EnrollmentAssignment("1", 1, otherPlant));
        Assert.Equal(HttpStatusCode.Forbidden, unauthorizedSource.StatusCode);
        using var noAdmin = app.Client(app.Token(ApiTestApplication.OwnerId, ["Factory.User", "Factory.PlantCreator"]));
        using var denied = await noAdmin.PutAsJsonAsync($"/v1/enrollments/{draft.OperationId}/assignment",
            new { schemaVersion = "1", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(ownPlant, Assert.Single(app.Store.All<EnrollmentRecord>()).AssignedPlantId);
    }

    [Fact]
    public async Task OfflineRevocationRejectsExistingTokensAndReenrollmentReplacesOnlyTheRetiredGeneration()
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        var plant = await Plant(human);
        using var key = RSA.Create(2048);
        var draft = Draft(key);
        using var submitted = await Submit(human, plant, draft, key);
        for (var i = 0; i < 3; i++)
            await app.AdvanceAsync(plant, draft.OperationId);
        await Assign(human, draft.OperationId, plant, 0);
        using var machine = app.Daemon();
        using var activated = await Activate(machine, plant, draft.OperationId);
        var workbenchId = $"wb-{Guid.Parse(draft.InstallationId):N}";
        var path = $"/v1/plants/{plant}/workbenches/{workbenchId}";
        using var registered = await Report(machine, path + "/registration", WorkbenchApiTests.Registration());
        using var presence = await Report(machine, path + "/presence", new WorkbenchPresence
            { SchemaVersion = "1", Status = "notReady" });
        Assert.Equal(HttpStatusCode.NoContent, presence.StatusCode);

        using var revoked = await Revoke(human, plant, draft.OperationId);
        Assert.Equal(HttpStatusCode.Accepted, revoked.StatusCode);
        using var deniedActivation = await Activate(machine, plant, draft.OperationId);
        using var deniedRegistration = await Report(machine, path + "/registration", WorkbenchApiTests.Registration());
        using var deniedPresence = await Report(machine, path + "/presence",
            new WorkbenchPresence { SchemaVersion = "1", Status = "notReady" });
        Assert.Equal(HttpStatusCode.Forbidden, deniedActivation.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deniedRegistration.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deniedPresence.StatusCode);
        app.Directory.DeleteFailures = 1;
        await app.AdvanceAsync(plant, draft.OperationId);
        Assert.Equal("revoking", Assert.Single(app.Store.All<EnrollmentRecord>()).State);
        var offline = await human.GetFromJsonAsync<WorkbenchView>(path);
        Assert.Equal("revoked", offline!.AdmissionState);
        Assert.Equal("unknown", offline.ContactState);
        app.Clock.Now += TimeSpan.FromSeconds(11);
        await app.AdvanceAsync(plant, draft.OperationId);
        Assert.Equal("retired", Assert.Single(app.Store.All<EnrollmentRecord>()).State);
        Assert.Equal(1, app.Directory.ApplicationsDeleted);
        Assert.Equal(1, app.Directory.PrincipalsDeleted);

        using var nextKey = RSA.Create(2048);
        var next = Draft(nextKey) with { InstallationId = draft.InstallationId };
        using var resubmitted = await Submit(human, plant, next, nextKey);
        for (var i = 0; i < 3; i++)
            await app.AdvanceAsync(plant, next.OperationId);
        await Assign(human, next.OperationId, plant, 0);
        var reenrolled = await human.GetFromJsonAsync<WorkbenchView>(path);
        Assert.Equal(next.OperationId, reenrolled!.EnrollmentId);
        Assert.Null(reenrolled.Presence);
        Assert.Null(reenrolled.Registration);
        using var newMachine = app.Daemon(reenrolled.Identity.ObjectId, reenrolled.Identity.ClientId);
        using var newActivation = await Activate(newMachine, plant, next.OperationId);
        Assert.Equal(HttpStatusCode.NoContent, newActivation.StatusCode);
        using var oldToken = await Report(machine, path + "/registration", WorkbenchApiTests.Registration());
        Assert.Equal(HttpStatusCode.Forbidden, oldToken.StatusCode);
        using var oldRemovalReplay = await Revoke(human, plant, draft.OperationId);
        Assert.Equal(HttpStatusCode.OK, oldRemovalReplay.StatusCode);
        Assert.Equal("active", Assert.Single(app.Store.All<WorkbenchRecord>()).AdmissionState);
        Assert.Equal(2, app.Directory.Creates);
        Assert.Equal(2, app.Store.All<EnrollmentRecord>().Length);
    }

    [Fact]
    public async Task RemovalDuringAnInflightCreateWaitsForItsLeaseAndReconcilesTheUncheckpointedIdentity()
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        var plant = await Plant(human);
        using var key = RSA.Create(2048);
        var draft = Draft(key);
        using var submitted = await Submit(human, plant, draft, key);
        app.Directory.AfterApplicationCreated = async () =>
        {
            using var revoked = await Revoke(human, plant, draft.OperationId);
            Assert.Equal(HttpStatusCode.Accepted, revoked.StatusCode);
        };
        await app.AdvanceAsync(plant, draft.OperationId);
        Assert.Null(Assert.Single(app.Store.All<EnrollmentRecord>()).ApplicationObjectId);
        await app.AdvanceAsync(plant, draft.OperationId);
        Assert.Equal(0, app.Directory.ApplicationsDeleted);
        app.Clock.Now += TimeSpan.FromMinutes(3);
        await app.AdvanceAsync(plant, draft.OperationId);
        await app.AdvanceAsync(plant, draft.OperationId);
        Assert.Equal("retired", Assert.Single(app.Store.All<EnrollmentRecord>()).State);
        Assert.Equal(1, app.Directory.Creates);
        Assert.Equal(1, app.Directory.ApplicationsDeleted);
        Assert.Empty(app.Store.All<WorkbenchRecord>());
    }

    [Fact]
    public async Task CancellingAnUnsubmittedJournalPreventsALateSubmissionFromCreatingAnIdentity()
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        var plant = await Plant(human);
        using var key = RSA.Create(2048);
        var draft = Draft(key);
        using var revoked = await Revoke(human, plant, draft.OperationId, draft);
        Assert.Equal(HttpStatusCode.Accepted, revoked.StatusCode);
        await app.AdvanceAsync(plant, draft.OperationId);
        using var late = await Submit(human, plant, draft, key);
        Assert.Equal("retired", (await late.Content.ReadFromJsonAsync<EnrollmentView>())!.State);
        await app.AdvanceAsync(plant, draft.OperationId);
        Assert.Equal(0, app.Directory.Creates);
    }

    [Fact]
    public async Task UncertainCreatesRequireReconciliationAndExplicitRetryBeforeLocalRetirementCanComplete()
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        var plant = await Plant(human);
        using var key = RSA.Create(2048);
        var draft = Draft(key);
        using var submitted = await Submit(human, plant, draft, key);
        app.Directory.LoseCreateResponse = true;
        await app.AdvanceAsync(plant, draft.OperationId);
        app.Directory.HideApplication = true;
        using var revoked = await Revoke(human, plant, draft.OperationId);
        await app.AdvanceAsync(plant, draft.OperationId);
        Assert.Equal("retirementNeedsAttention", Assert.Single(app.Store.All<EnrollmentRecord>()).State);
        Assert.Equal(0, app.Directory.ApplicationsDeleted);
        app.Directory.HideApplication = false;
        await app.AdvanceAsync(plant, draft.OperationId);
        Assert.Equal(0, app.Directory.ApplicationsDeleted);
        using var retry = await Revoke(human, plant, draft.OperationId);
        await app.AdvanceAsync(plant, draft.OperationId);
        await app.AdvanceAsync(plant, draft.OperationId);
        Assert.Equal("retired", Assert.Single(app.Store.All<EnrollmentRecord>()).State);
        Assert.Equal(1, app.Directory.ApplicationsDeleted);
    }

    [Fact]
    public async Task RemovalRequiresEnrollmentAdministrationNotPlantOwnershipOrDaemonAccess()
    {
        using var app = new EnrollmentTestApplication();
        using var human = app.Human();
        var plant = await Plant(human);
        using var key = RSA.Create(2048);
        var draft = Draft(key);
        using var submitted = await Submit(human, plant, draft, key);
        using var reader = app.Human(ApiTestApplication.ReaderId, false);
        using var daemon = app.Daemon();
        using var deniedReader = await Revoke(reader, plant, draft.OperationId);
        using var deniedDaemon = await Revoke(daemon, plant, draft.OperationId);
        Assert.Equal(HttpStatusCode.Forbidden, deniedReader.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deniedDaemon.StatusCode);
        using var otherPlantOwner = app.Client(app.Token(ApiTestApplication.ReaderId, ["Factory.User", "Factory.PlantCreator"]));
        var otherPlant = await Plant(otherPlantOwner);
        using var crossPlant = await Revoke(otherPlantOwner, otherPlant, draft.OperationId);
        Assert.Equal(HttpStatusCode.Forbidden, crossPlant.StatusCode);
        app.Store.Seed(new AccessRecord
        {
            Id = "scoped-registrar", PlantId = plant, ScopeId = Records.PlantScope(plant),
            Principal = new(ApiTestApplication.Tenant, ApiTestApplication.ReaderId),
            Role = "Workbench.Registrar", WorkbenchId = $"wb-{Guid.Parse(draft.InstallationId):N}"
        });
        using var deniedScoped = await Revoke(reader, plant, draft.OperationId);
        Assert.Equal(HttpStatusCode.Forbidden, deniedScoped.StatusCode);
        app.Store.Seed(app.Store.All<AccessRecord>().Single() with { WorkbenchId = null });
        using var stillDenied = await Revoke(reader, plant, draft.OperationId);
        Assert.Equal(HttpStatusCode.Forbidden, stillDenied.StatusCode);
        using var allowed = await Revoke(human, plant, draft.OperationId);
        Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
        app.Store.Seed(app.Store.All<AccessRecord>().Single() with { Role = "Plant.Reader" });
        await app.AdvanceAsync(plant, draft.OperationId);
        Assert.Equal("retired", Assert.Single(app.Store.All<EnrollmentRecord>()).State);
    }

    private static async Task<HttpResponseMessage> Revoke(HttpClient client, string plant, string operation, EnrollmentDraft? draft = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/enrollments/{operation}/revoke");
        request.Headers.Add("Idempotency-Key", operation);
        if (draft is not null)
            request.Content = JsonContent.Create(draft);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Report<T>(HttpClient client, string path, T body)
    {
        object report = body switch
        {
            WorkbenchRegistration registration => registration with { AssignmentVersion = registration.AssignmentVersion ?? 1 },
            WorkbenchPresence presence => presence with { AssignmentVersion = presence.AssignmentVersion ?? 1 },
            _ => body!
        };
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(report) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return await client.SendAsync(request);
    }

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
        var ready = await human.GetFromJsonAsync<EnrollmentView>($"/v1/enrollments/{draft.OperationId}");
        Assert.Equal("ready", ready!.State);
        Assert.Null(ready.PlantId);
        Assert.Empty(app.Store.All<WorkbenchRecord>());
        Assert.Equal(1, app.Directory.Creates);
        using var wrong = app.Daemon(clientId: Guid.NewGuid().ToString());
        using var denied = await Activate(wrong, plant, draft.OperationId);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var machine = app.Daemon();
        using var activated = await Activate(machine, plant, draft.OperationId);
        Assert.Equal(HttpStatusCode.NoContent, activated.StatusCode);
        using var replayed = await Activate(machine, plant, draft.OperationId);
        Assert.Equal(HttpStatusCode.NoContent, replayed.StatusCode);
        using var factoryPresence = await Report(machine, $"/v1/enrollments/{draft.OperationId}/presence",
            new WorkbenchPresence { SchemaVersion = "1", Status = "notReady" });
        Assert.Equal(HttpStatusCode.NoContent, factoryPresence.StatusCode);
        await Assign(human, draft.OperationId, plant, 0);
        machine.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        var basePath = $"/v1/plants/{plant}/workbenches/{ready.WorkbenchId}";
        using var registered = await machine.PutAsJsonAsync(basePath + "/registration", new WorkbenchRegistration
        {
            SchemaVersion = "1", MachineName = "new-workbench", HostVersion = "test", WorkerVersion = "unavailable",
            ProtocolVersion = "1", AssignmentSchemaVersions = ["1"], Capabilities = ["plant-connection"], AssignmentVersion = 1
        });
        Assert.Equal(HttpStatusCode.NoContent, registered.StatusCode);
        machine.DefaultRequestHeaders.Remove("Idempotency-Key");
        machine.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var presence = await machine.PutAsJsonAsync(basePath + "/presence",
            new WorkbenchPresence { SchemaVersion = "1", Status = "notReady", Reason = "Execution is not installed.", AssignmentVersion = 1 });
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
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/enrollments")
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
        using var forbidden = await reader.PostAsJsonAsync("/v1/enrollments/challenge",
            new EnrollmentChallengeRequest(draft));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        app.Store.Seed(app.Store.All<AccessRecord>().Single() with { WorkbenchId = null });
        using var allowed = await reader.PostAsJsonAsync("/v1/enrollments/challenge",
            new EnrollmentChallengeRequest(draft));
        Assert.Equal(HttpStatusCode.Forbidden, allowed.StatusCode);
    }

    [Fact]
    public async Task AcceptedFactoryEnrollmentIsIndependentOfLaterPlantOwnershipChanges()
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
        Assert.Equal("provisioning", Assert.Single(app.Store.All<EnrollmentRecord>()).State);
        Assert.Equal(1, app.Directory.Creates);
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

    private static async Task<EnrollmentView> Assign(HttpClient client, string operation, string? plant, long version)
    {
        using var response = await client.PutAsJsonAsync($"/v1/enrollments/{operation}/assignment",
            plant is null ? new { schemaVersion = "1", expectedVersion = version } :
                (object)new { schemaVersion = "1", expectedVersion = version, plantId = plant });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EnrollmentView>())!;
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
        using var response = await client.PostAsJsonAsync("/v1/enrollments/challenge", new EnrollmentChallengeRequest(draft));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EnrollmentChallenge>())!;
    }
    private static async Task<HttpResponseMessage> Submit(HttpClient client, string plant, EnrollmentDraft draft, RSA key)
    {
        var challenge = await Challenge(client, plant, draft);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/enrollments")
            { Content = JsonContent.Create(new EnrollmentSubmission(draft, challenge.Challenge, Sign(key, challenge.Challenge))) };
        request.Headers.Add("Idempotency-Key", draft.OperationId);
        return await client.SendAsync(request);
    }
    private static string Sign(RSA key, string challenge) =>
        Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(challenge), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    private static Task<HttpResponseMessage> Activate(HttpClient client, string plant, string operation)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/enrollments/{operation}/activate");
        request.Headers.Add("Idempotency-Key", operation);
        return Send();
        async Task<HttpResponseMessage> Send()
        {
            using (request)
                return await client.SendAsync(request);
        }
    }
}
