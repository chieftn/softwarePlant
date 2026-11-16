using System.Net;
using System.Net.Http.Json;
using Factory.Contracts;
using Factory.Plant.Service;
using Factory.Plant.Service.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using static Factory.Tests.PlantApiTests;
using static Factory.Tests.ProductionLineTests;

namespace Factory.Tests;

public sealed class EnrollmentMissionTests
{
    [Fact]
    public async Task ClaimFreezesIssueAndSourceAndIsUniqueAcrossWorkbenches()
    {
        using var app = new MissionApplication();
        var (enrollment, configuration) = await Assigned(app);
        using var machine = app.Daemon();
        var request = Request(configuration);
        var path = $"/v1/enrollments/{enrollment.Id}/mission";
        var first = await Claim(machine, path, request);
        var retry = await Claim(machine, path, request);
        Assert.Equal(first.Mission.Digest, retry.Mission.Digest);
        Assert.Equal("claimed", first.State);
        Assert.Single(app.Store.All<DevelopmentIssueClaimRecord>());
        var otherIdentity = new EntraApplicationPrincipal(ApiTestApplication.Tenant, Key(), Key());
        var other = SeedMember(app, configuration.PlantId!, otherIdentity);
        using var otherMachine = app.Daemon(otherIdentity.ObjectId, otherIdentity.ClientId);
        var otherConfiguration = (await otherMachine.GetFromJsonAsync<WorkbenchLineConfiguration>(
            $"/v1/enrollments/{other.Id}/configuration"))!;
        using var duplicate = await SendAsync(otherMachine, HttpMethod.Post, $"/v1/enrollments/{other.Id}/mission",
            Request(otherConfiguration), Key());
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        var otherRequest = Request(otherConfiguration);
        using var owned = await SendAsync(otherMachine, HttpMethod.Post, $"/v1/enrollments/{other.Id}/mission",
            otherRequest, otherRequest.OperationId);
        Assert.Equal(HttpStatusCode.Conflict, owned.StatusCode);
        Assert.Single(app.Store.All<DevelopmentIssueClaimRecord>());
    }

    [Fact]
    public async Task SourceWriteNeedsPublicationIntentAndRevocationFencesAllNewGrants()
    {
        using var app = new MissionApplication();
        var (enrollment, configuration) = await Assigned(app);
        using var machine = app.Daemon();
        using var owner = app.Human();
        var path = $"/v1/enrollments/{enrollment.Id}/mission";
        var delivery = await Claim(machine, path, Request(configuration));
        using var premature = await machine.PostAsJsonAsync(path + "/credential", new MissionCredentialRequest("publish"));
        Assert.Equal(HttpStatusCode.Forbidden, premature.StatusCode);
        Assert.Equal(0, app.Repository.WriteGrants);
        var progress = new DevelopmentMissionProgress(delivery.Mission.Id, delivery.Mission.Digest, "publishing", 1, 10,
            new('b', 40), null, true, "Recorded exact verified candidate publication intent.");
        using var report = await SendAsync(machine, HttpMethod.Put, path + "/progress", progress, Key());
        report.EnsureSuccessStatusCode();
        Assert.True((await machine.GetFromJsonAsync<DevelopmentMissionDelivery>(path))!.PublicationPending);
        using var granted = await machine.PostAsJsonAsync(path + "/credential", new MissionCredentialRequest("publish"));
        granted.EnsureSuccessStatusCode();
        Assert.Equal(1, app.Repository.WriteGrants);
        using var revoked = await owner.PutAsJsonAsync($"/v1/plants/{configuration.PlantId}/production-line",
            new ProductionLineAssignment("1", 1));
        revoked.EnsureSuccessStatusCode();
        using var fenced = await machine.PostAsJsonAsync(path + "/credential", new MissionCredentialRequest("publish"));
        Assert.Equal(HttpStatusCode.Conflict, fenced.StatusCode);
        Assert.Equal(1, app.Repository.WriteGrants);
        using var fault = await SendAsync(machine, HttpMethod.Put, path + "/progress", progress with
        { State = "needsAttention", Reason = "Revoked; physical termination/publication requires reconciliation." }, Key());
        fault.EnsureSuccessStatusCode();
        Assert.Single(app.Store.All<DevelopmentIssueClaimRecord>());
    }

    [Fact]
    public async Task ProgressRetriesCannotOverwriteNewerStateAndApprovalIsVerifiedByService()
    {
        using var app = new MissionApplication();
        var (enrollment, configuration) = await Assigned(app);
        using var machine = app.Daemon();
        var path = $"/v1/enrollments/{enrollment.Id}/mission";
        var delivery = await Claim(machine, path, Request(configuration));
        var oldKey = Key();
        var old = new DevelopmentMissionProgress(delivery.Mission.Id, delivery.Mission.Digest, "executing", 1, 1,
            new('b', 40), null, false, "Candidate in progress.");
        using var first = await SendAsync(machine, HttpMethod.Put, path + "/progress", old, oldKey);
        first.EnsureSuccessStatusCode();
        var receipt = new PublishedDevelopmentPullRequest(9, "product/source", delivery.Mission.Branch, "main",
            new('b', 40), "https://github.com/product/source/pull/9");
        var waiting = old with { State = "awaitingApproval", ActiveMilliseconds = 10, PullRequest = receipt };
        using var next = await SendAsync(machine, HttpMethod.Put, path + "/progress", waiting, Key());
        next.EnsureSuccessStatusCode();
        using var retry = await SendAsync(machine, HttpMethod.Put, path + "/progress", old, oldKey);
        retry.EnsureSuccessStatusCode();
        Assert.Equal("awaitingApproval", Assert.Single(app.Store.All<DevelopmentMissionRecord>()).Progress.State);
        using var fakeApproval = await SendAsync(machine, HttpMethod.Put, path + "/progress", waiting with { State = "approved" }, Key());
        Assert.Equal(HttpStatusCode.Conflict, fakeApproval.StatusCode);
        app.Repository.Approved = true;
        app.Development.Reviewers = ["replacement"];
        var revokedReview = (await machine.GetFromJsonAsync<DevelopmentReview>(path + "/review"))!;
        Assert.Empty(revokedReview.Feedback);
        using var revokedApproval = await SendAsync(machine, HttpMethod.Put, path + "/progress", waiting with { State = "approved" }, Key());
        Assert.Equal(HttpStatusCode.Conflict, revokedApproval.StatusCode);
        app.Development.Reviewers = ["reviewer"];
        using var approved = await SendAsync(machine, HttpMethod.Put, path + "/progress", waiting with { State = "approved" }, Key());
        approved.EnsureSuccessStatusCode();
        Assert.False(Assert.Single(app.Store.All<DevelopmentMissionRecord>()).TerminationConfirmed);
        using var terminated = await machine.PostAsJsonAsync(path + "/terminated", new MissionTerminationRequest(delivery.Mission.Id, true));
        terminated.EnsureSuccessStatusCode();
        Assert.True(Assert.Single(app.Store.All<DevelopmentMissionRecord>()).TerminationConfirmed);
    }

    [Fact]
    public async Task ChangedIssueStopsAuthorizationWithoutChangingFrozenInputs()
    {
        using var app = new MissionApplication();
        var (enrollment, configuration) = await Assigned(app);
        using var machine = app.Daemon();
        var path = $"/v1/enrollments/{enrollment.Id}/mission";
        var delivery = await Claim(machine, path, Request(configuration));
        app.Repository.Changed = true;
        using var response = await machine.PostAsync(path + "/authorize", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(delivery.Mission.Digest, Assert.Single(app.Store.All<DevelopmentMissionRecord>()).Mission.Digest);
    }

    [Fact]
    public async Task DisablingManagedSourceConnectionFencesAnActiveMissionEvenWithSeparateTaskConnection()
    {
        using var app = new MissionApplication();
        using var owner = app.Human();
        using var machine = app.Daemon();
        var (plant, _) = await CreateAsync(owner, "Managed plant");
        var enrollment = SeedMember(app, plant.Id);
        var reference = "github-" + Guid.NewGuid().ToString("N");
        app.Store.Seed(new GitHubConnectionRecord
        {
            Id = reference,
            ScopeId = $"github-connection:{reference}",
            Principal = new(ApiTestApplication.Tenant, ApiTestApplication.OwnerId),
            Name = "Managed source",
            Organization = "product",
            State = "connected",
            AllowPublication = true,
            CreatedAt = app.Clock.Now,
            AppId = 42,
            Slug = "fixture-app",
            Account = "product",
            InstallationId = 84,
            Repositories = ["product/source"]
        });
        var definition = Definition() with { Policy = new(true, true, 3, 30) };
        var line = await CreateLineAsync(owner, definition with { Source = definition.Source with { ConnectionReference = reference } });
        using var assignment = await owner.PutAsJsonAsync($"/v1/plants/{plant.Id}/production-line", new ProductionLineAssignment("1", 0, line.Id));
        assignment.EnsureSuccessStatusCode();
        var configuration = (await machine.GetFromJsonAsync<WorkbenchLineConfiguration>($"/v1/enrollments/{enrollment.Id}/configuration"))!;
        var path = $"/v1/enrollments/{enrollment.Id}/mission";
        await Claim(machine, path, Request(configuration));
        await app.Services.GetRequiredService<ManagedGitHubConnections>().DisableAsync(reference,
            new(new(ApiTestApplication.Tenant, ApiTestApplication.OwnerId), ApiTestApplication.Audience), CancellationToken.None);
        using var denied = await machine.PostAsync(path + "/authorize", null);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Single(app.Store.All<DevelopmentIssueClaimRecord>());
        Assert.Equal(0, app.Repository.WriteGrants);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangingTaskConnectionOrSelectionRevokesFrozenMissionAuthority(bool connection)
    {
        using var app = new MissionApplication();
        var (enrollment, configuration) = await Assigned(app);
        using var machine = app.Daemon();
        using var owner = app.Human();
        var path = $"/v1/enrollments/{enrollment.Id}/mission";
        var delivery = await Claim(machine, path, Request(configuration));
        var line = configuration.ProductionLine!;
        using var update = new HttpRequestMessage(HttpMethod.Put, $"/v1/production-lines/{line.Id}")
        {
            Content = JsonContent.Create(line.Definition with
            {
                Tasks = connection ? line.Definition.Tasks with { ConnectionReference = "github-next" }
                    : line.Definition.Tasks with { RequiredLabels = ["approved", "new-approval"] }
            })
        };
        update.Headers.Add("Idempotency-Key", Key());
        update.Headers.Add("If-Match", line.Revision);
        using var updated = await owner.SendAsync(update);
        updated.EnsureSuccessStatusCode();
        using var response = await machine.PostAsync(path + "/authorize", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(delivery.Mission.Digest, Assert.Single(app.Store.All<DevelopmentMissionRecord>()).Mission.Digest);
    }
    private static DevelopmentClaimRequest Request(WorkbenchLineConfiguration configuration) => new(Key(),
        WorkloadRequest.From(configuration),
        ResolvedRepositoryWorkload.Create(configuration, new('a', 40), RepositoryWorkloadTests.Files(true)).Digest, 7, "test-model");

    private static async Task<DevelopmentMissionDelivery> Claim(HttpClient machine, string path, DevelopmentClaimRequest request)
    {
        using var response = await SendAsync(machine, HttpMethod.Post, path, request, request.OperationId);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DevelopmentMissionDelivery>())!;
    }

    private static async Task<(EnrollmentRecord, WorkbenchLineConfiguration)> Assigned(MissionApplication app)
    {
        using var owner = app.Human();
        using var machine = app.Daemon();
        var (plant, _) = await CreateAsync(owner, "Plant");
        var enrollment = SeedMember(app, plant.Id);
        var line = await CreateLineAsync(owner, Definition() with { Policy = new(true, true, 3, 30) });
        using var assignment = await owner.PutAsJsonAsync($"/v1/plants/{plant.Id}/production-line",
            new ProductionLineAssignment("1", 0, line.Id));
        assignment.EnsureSuccessStatusCode();
        return (enrollment, (await machine.GetFromJsonAsync<WorkbenchLineConfiguration>($"/v1/enrollments/{enrollment.Id}/configuration"))!);
    }

    private sealed class MissionApplication : ApiTestApplication
    {
        public FakeRepository Repository { get; } = new();
        public DevelopmentMissionSettings Development { get; } = new() { Enabled = true, Reviewers = ["reviewer"] };
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DevelopmentMissionSettings>();
                services.AddSingleton(Development);
                services.RemoveAll<IGitHubRepositoryReader>();
                services.RemoveAll<IGitHubDevelopmentRepository>();
                services.AddSingleton<IGitHubRepositoryReader>(Repository);
                services.AddSingleton<IGitHubDevelopmentRepository>(Repository);
            });
        }
    }

    private sealed class FakeRepository : IGitHubRepositoryReader, IGitHubDevelopmentRepository
    {
        public int WriteGrants { get; private set; }
        public bool Approved { get; set; }
        public bool Changed { get; set; }
        private readonly DateTimeOffset updated = DateTimeOffset.UtcNow;
        public Task<ResolvedRepositoryWorkload> ResolveAsync(WorkbenchLineConfiguration configuration, CancellationToken cancellationToken) =>
            Task.FromResult(ResolvedRepositoryWorkload.Create(configuration, new('a', 40), RepositoryWorkloadTests.Files(true)));
        public Task<DevelopmentIssue> IssueAsync(ProductionLineTasks tasks, long number, CancellationToken cancellationToken) =>
            Task.FromResult(new DevelopmentIssue(number, tasks.Repository, "Approved task", "## Acceptance criteria\n- Preserve ownership.",
                ["Preserve ownership."], ["approved"], Changed ? updated.AddSeconds(1) : updated));
        public Task<RepositoryAccessCredential> SourceCredentialAsync(DevelopmentMission mission, bool write, CancellationToken cancellationToken)
        {
            if (write)
                WriteGrants++;
            return Task.FromResult(new RepositoryAccessCredential("product/source", "fixture-only-token", DateTimeOffset.UtcNow.AddMinutes(30)));
        }
        public Task<PublishedDevelopmentPullRequest> PublishAsync(DevelopmentMission mission, string candidate, CancellationToken cancellationToken) =>
            Task.FromResult(new PublishedDevelopmentPullRequest(9, "product/source", mission.Branch, "main", candidate, "https://github.com/product/source/pull/9"));
        public async Task<PublishedDevelopmentPullRequest?> PublishedAsync(DevelopmentMission mission, string candidate, CancellationToken cancellationToken) =>
            await PublishAsync(mission, candidate, cancellationToken);
        public Task<DevelopmentReview> ReviewAsync(DevelopmentMission mission, PublishedDevelopmentPullRequest pullRequest, CancellationToken cancellationToken) =>
            Task.FromResult(new DevelopmentReview(pullRequest.CandidateCommit, "open", Approved
                ? [new("review-1", "reviewer", pullRequest.CandidateCommit, "approved", "", DateTimeOffset.UtcNow)] : []));
    }
}
