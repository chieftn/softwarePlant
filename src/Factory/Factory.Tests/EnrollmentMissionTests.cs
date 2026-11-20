using System.Net;
using System.Net.Http.Json;
using Factory.Contracts;
using Factory.Plant.Service;
using Factory.Plant.Service.Storage;
using Factory.Workbench;
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
    public async Task ExplicitFailedMissionRestartUsesExistingApiAndPreservesClaimAndBudget()
    {
        using var app = new MissionApplication();
        var (enrollment, configuration) = await Assigned(app);
        using var machine = app.Daemon();
        var path = $"/v1/enrollments/{enrollment.Id}/mission";
        var delivery = await Claim(machine, path, Request(configuration));
        var failed = new DevelopmentMissionProgress(delivery.Mission.Id, delivery.Mission.Digest,
            "needsAttention", 1, 5688, null, null, false, "Retained failure with telemetry.");
        using var fault = await SendAsync(machine, HttpMethod.Put, path + "/progress", failed, Key());
        fault.EnsureSuccessStatusCode();
        using var stopped = await machine.PostAsJsonAsync(path + "/terminated", new MissionTerminationRequest(delivery.Mission.Id, true));
        stopped.EnsureSuccessStatusCode();
        using var retry = await SendAsync(machine, HttpMethod.Put, path + "/progress",
            failed with { State = "executing", Reason = "Explicit retry; original budget preserved." }, Key());
        retry.EnsureSuccessStatusCode();
        using var credential = await machine.PostAsJsonAsync(path + "/model-credential",
            new MissionModelCredentialRequest(delivery.Mission.Id, delivery.Mission.Digest));
        credential.EnsureSuccessStatusCode();
        var stored = Assert.Single(app.Store.All<DevelopmentMissionRecord>());
        Assert.Equal(delivery.Mission.Id, stored.Mission.Id);
        Assert.Equal(delivery.Mission.Digest, stored.Mission.Digest);
        Assert.Equal(1, stored.Progress.Iterations);
        Assert.Equal(5688, stored.Progress.ActiveMilliseconds);
        Assert.False(stored.TerminationConfirmed);
        Assert.Single(app.Store.All<DevelopmentIssueClaimRecord>());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ManagedLineSuppliesExecutionPolicyWithoutManualServiceSettingsAndHonorsShutdown(bool? enabled, bool permitted)
    {
        using var app = new MissionApplication();
        app.Development.Enabled = enabled;
        app.Development.Reviewers = [];
        var execution = new ProductionLineExecution(true, "test-model", "Factory", "factory@example.invalid", ["reviewer"], 12);
        var (enrollment, configuration) = await Assigned(app, execution);
        using var machine = app.Daemon();
        var path = $"/v1/enrollments/{enrollment.Id}/mission";
        var request = Request(configuration);
        using var response = await SendAsync(machine, HttpMethod.Post, path, request, request.OperationId);
        if (!permitted)
        {
            await ProblemAsync(response, 503, "development_execution_disabled");
            Assert.Empty(app.Store.All<DevelopmentIssueClaimRecord>());
            return;
        }
        response.EnsureSuccessStatusCode();
        var delivery = (await response.Content.ReadFromJsonAsync<DevelopmentMissionDelivery>())!;
        Assert.Equal(execution.Reviewers, delivery.Reviewers);
        Assert.Equal(app.Clock.Now.AddHours(12), delivery.Mission.ApprovalDeadline);
        var receipt = new PublishedDevelopmentPullRequest(9, "product/source", delivery.Mission.Branch, "main",
            new('b', 40), "https://github.com/product/source/pull/9");
        var waiting = new DevelopmentMissionProgress(delivery.Mission.Id, delivery.Mission.Digest, "awaitingApproval", 1, 10,
            receipt.CandidateCommit, receipt, false, "Verified candidate awaiting review.");
        using var published = await SendAsync(machine, HttpMethod.Put, path + "/progress", waiting, Key());
        published.EnsureSuccessStatusCode();
        app.Repository.Approved = true;
        using var approved = await SendAsync(machine, HttpMethod.Put, path + "/progress", waiting with { State = "awaitingMerge" }, Key());
        approved.EnsureSuccessStatusCode();
        Assert.Equal("reviewer", Assert.Single((await machine.GetFromJsonAsync<DevelopmentReview>(path + "/review"))!.Feedback).Actor);
    }

    [Fact]
    public async Task ManagedLineCannotClaimAnotherModelAndPolicyChangesRevokeOwnedModelAuthority()
    {
        using var app = new MissionApplication();
        app.Development.Enabled = null;
        var execution = new ProductionLineExecution(true, "test-model", "Factory", "factory@example.invalid", ["reviewer"], 48);
        var (enrollment, configuration) = await Assigned(app, execution);
        using var machine = app.Daemon();
        var path = $"/v1/enrollments/{enrollment.Id}/mission";
        var wrongModel = Request(configuration) with { Model = "other-model" };
        using var denied = await SendAsync(machine, HttpMethod.Post, path, wrongModel, wrongModel.OperationId);
        await ProblemAsync(denied, 403, "development_not_permitted");
        Assert.Empty(app.Store.All<DevelopmentIssueClaimRecord>());
        var delivery = await Claim(machine, path, Request(configuration));
        using var owner = app.Human();
        var line = configuration.ProductionLine!;
        using var update = new HttpRequestMessage(HttpMethod.Put, $"/v1/production-lines/{line.Id}")
        { Content = JsonContent.Create(line.Definition with { Execution = execution with { Reviewers = ["replacement"] } }) };
        update.Headers.Add("Idempotency-Key", Key());
        update.Headers.Add("If-Match", line.Revision);
        using var updated = await owner.SendAsync(update);
        updated.EnsureSuccessStatusCode();
        using var revoked = await machine.PostAsync(path + "/authorize", null);
        await ProblemAsync(revoked, 409, "mission_revoked");
        Assert.Equal(delivery.Mission.Digest, Assert.Single(app.Store.All<DevelopmentMissionRecord>()).Mission.Digest);
    }

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
    public async Task QueueIsIdleWhenEmptyAndDoesNotAdvanceOnApprovalOrBeforeMergedWorkerTerminates()
    {
        using var app = new MissionApplication();
        var (enrollment, configuration) = await Assigned(app);
        using var machine = app.Daemon();
        var path = $"/v1/enrollments/{enrollment.Id}/mission";
        DevelopmentQueueRequest RequestNext() => new(Key(), WorkloadRequest.From(configuration),
            Request(configuration).WorkloadDigest, "test-model");
        app.Repository.Issues = [];
        Assert.Null((await Next(machine, path, RequestNext())).Mission);
        Assert.Empty(app.Store.All<DevelopmentIssueClaimRecord>());
        app.Repository.Issues = [7, 8];
        var request = RequestNext();
        var first = (await Next(machine, path, request)).Mission!;
        Assert.Equal(7, first.Mission.Issue.Number);
        Assert.Equal(first.Mission.Digest, (await Next(machine, path, request)).Mission!.Mission.Digest);
        var receipt = new PublishedDevelopmentPullRequest(9, "product/source", first.Mission.Branch, "main",
            new('b', 40), "https://github.com/product/source/pull/9");
        var waiting = new DevelopmentMissionProgress(first.Mission.Id, first.Mission.Digest, "awaitingApproval", 1, 10,
            receipt.CandidateCommit, receipt, false, "Verified candidate awaiting review.");
        using var published = await SendAsync(machine, HttpMethod.Put, path + "/progress", waiting, Key());
        published.EnsureSuccessStatusCode();
        app.Repository.Approved = true;
        using var approved = await SendAsync(machine, HttpMethod.Put, path + "/progress", waiting with { State = "awaitingMerge" }, Key());
        approved.EnsureSuccessStatusCode();
        using var earlyTermination = await machine.PostAsJsonAsync(path + "/terminated", new MissionTerminationRequest(first.Mission.Id, true));
        earlyTermination.EnsureSuccessStatusCode();
        var stillWaiting = (await Next(machine, path, RequestNext())).Mission!;
        Assert.Equal("awaitingMerge", stillWaiting.State);
        Assert.Equal(first.Mission.Id, stillWaiting.Mission.Id);
        var premature = Request(configuration) with { IssueNumber = 8 };
        using var explicitPrematureClaim = await SendAsync(machine, HttpMethod.Post, path, premature, premature.OperationId);
        Assert.Equal(HttpStatusCode.Conflict, explicitPrematureClaim.StatusCode);
        var completed = waiting with { State = "completed", Reason = "Observed owned PR merge." };
        foreach (var state in new[] { "open", "closed" })
        {
            app.Repository.ReviewState = state;
            using var falseCompletion = await SendAsync(machine, HttpMethod.Put, path + "/progress", completed, Key());
            Assert.Equal(HttpStatusCode.Conflict, falseCompletion.StatusCode);
        }
        app.Repository.ReviewState = "merged";
        app.Repository.ChangedHead = true;
        using var wrongHead = await SendAsync(machine, HttpMethod.Put, path + "/progress", completed, Key());
        Assert.Equal(HttpStatusCode.Conflict, wrongHead.StatusCode);
        app.Repository.ChangedHead = false;
        app.Repository.Changed = true;
        using var observeAfterIssueAutoClosure = await machine.PostAsync(path + "/authorize", null);
        observeAfterIssueAutoClosure.EnsureSuccessStatusCode();
        using var merged = await SendAsync(machine, HttpMethod.Put, path + "/progress", completed, Key());
        merged.EnsureSuccessStatusCode();
        Assert.False(Assert.Single(app.Store.All<DevelopmentMissionRecord>()).TerminationConfirmed);
        Assert.Equal(first.Mission.Id, (await Next(machine, path, RequestNext())).Mission!.Mission.Id);
        using var regress = await SendAsync(machine, HttpMethod.Put, path + "/progress", waiting, Key());
        Assert.Equal(HttpStatusCode.Conflict, regress.StatusCode);
        using var terminated = await machine.PostAsJsonAsync(path + "/terminated", new MissionTerminationRequest(first.Mission.Id, true));
        terminated.EnsureSuccessStatusCode();
        app.Repository.SourceCommit = new('c', 40);
        var staleRequest = RequestNext();
        using var stale = await SendAsync(machine, HttpMethod.Post, path + "/next", staleRequest, staleRequest.OperationId);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("workload_changed", (await stale.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("code").GetString());
        var refreshed = staleRequest with { WorkloadDigest = (await app.Repository.ResolveAsync(configuration, CancellationToken.None)).Digest };
        var next = (await Next(machine, path, refreshed)).Mission!;
        Assert.Equal(8, next.Mission.Issue.Number);
        Assert.Equal(app.Repository.SourceCommit, next.Mission.Workload.SourceCommit);
        Assert.NotEqual(first.Mission.Id, next.Mission.Id);
        Assert.Equal(2, app.Store.All<DevelopmentIssueClaimRecord>().Length);
        Assert.Equal("completed", (await Next(machine, path, request)).Mission!.State);
        Assert.Equal(2, app.Store.All<DevelopmentIssueClaimRecord>().Length);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task HostRefreshesIncompleteSpecsPollsQueueAndMigratesPendingClaimIntent(bool checksMissing, bool managed)
    {
        using var app = new MissionApplication();
        app.Development.Enabled = managed ? null : true;
        var (enrollment, current) = await Assigned(app, managed
            ? new(true, "test-model", "Factory", "factory@example.invalid", ["reviewer"], 48) : null);
        using var machine = app.Daemon();
        app.Repository.Issues = [];
        var root = Path.Combine(Path.GetTempPath(), $"factory-queue-host-{Guid.NewGuid():N}");
        try
        {
            var installationId = Key();
            const string release = "queue-test-001";
            var configuration = new WorkbenchConfiguration
            {
                SchemaVersion = "1",
                InstallationId = installationId,
                PlantServiceUrl = "https://plant.example",
                TenantId = ApiTestApplication.Tenant,
                ApiClientId = ApiTestApplication.Audience,
                InstallerClientId = ApiTestApplication.Audience,
                Distribution = WslWorkerEnvironment.DistributionName(installationId),
                LinuxUser = WslWorkerEnvironment.LinuxUser,
                LinuxWorkspaceRoot = WslWorkerEnvironment.Workspace,
                AllowedRepositories = ["product/source", "product/tasks"],
                AllowShellExecution = true,
                AllowPublication = true,
                ExecutionConsent = managed ? WorkbenchExecutionConsent.From(current) : null,
                Development = new()
                {
                    Enabled = !managed,
                    AssignedIssueNumber = 999,
                    WorkerExecutable = WslWorkerEnvironment.WorkerExecutable(release),
                    Model = managed ? "" : "test-model",
                    GitAuthorName = managed ? "" : "Factory",
                    GitAuthorEmail = managed ? "" : "factory@example.invalid"
                }
            };
            ConfigurationStore.Save(root, configuration);
            LocalState.SaveInstallation(root, new("1", installationId, Path.Combine(root, "binaries"), "installed",
                release, null, null, [release], true));
            LocalState.Write(WslWorkerEnvironment.StatusPath(root), new WorkerSetupStatus(Key(), installationId,
                release, Environment.ProcessId, DateTimeOffset.UtcNow, "ready", "complete", "Isolated readiness fixture."));
            var binding = new WorkbenchBinding("1", installationId, null, current.WorkbenchId, ApiTestApplication.Tenant,
                ApiTestApplication.DaemonClientId, ApiTestApplication.DaemonId, new('A', 40), enrollment.Id);
            WorkbenchLineConfigurationStore.Apply(root, binding, current);
            var oldFiles = RepositoryWorkloadTests.Files(!checksMissing);
            if (!checksMissing)
                oldFiles["workload.json"] = oldFiles["workload.json"].Replace(",\"completionCriterion\":\"pull_request_merged\"", "");
            WorkbenchWorkloadStore.Save(root, binding, ResolvedRepositoryWorkload.Create(current, new('a', 40), oldFiles));
            var legacyPath = Path.Combine(LocalState.RuntimeDirectory(root), "pending-development-claim.json");
            LocalState.Write(legacyPath, Request(current));
            var plant = new PlantHttp(machine, _ => Task.FromResult(
                app.Token(ApiTestApplication.DaemonId, ["Workbench.Connect"], false, ApiTestApplication.DaemonClientId)));
            await WorkbenchWorkloadStore.ObserveAsync(root, binding,
                WorkbenchLineConfigurationStore.Read(root, binding)!, plant, CancellationToken.None);
            var refreshed = WorkbenchWorkloadStore.Read(root, binding, current)!.Validate();
            Assert.NotEmpty(refreshed.Verification.Commands);
            Assert.Equal("pull_request_merged", refreshed.Workflow.CompletionCriterion);
            var host = new WorkbenchDevelopmentExecution(plant, binding, configuration, root);
            await host.ObserveAsync(current, CancellationToken.None);
            await host.TickAsync(CancellationToken.None);
            await host.TickAsync(CancellationToken.None);
            Assert.Equal("idle", host.State);
            Assert.Contains("No unclaimed eligible issues", host.Reason);
            Assert.False(File.Exists(legacyPath));
            Assert.False(File.Exists(Path.Combine(LocalState.RuntimeDirectory(root), "pending-development-queue.json")));
            Assert.False(File.Exists(Path.Combine(LocalState.RuntimeDirectory(root), "active-development-worker.json")));
            Assert.Empty(app.Store.All<DevelopmentIssueClaimRecord>());
            WorkbenchWorkloadStore.Revoke(root);
            await host.TickAsync(CancellationToken.None);
            Assert.Equal("notReady", host.State);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task QueueSkipsDurableClaimsAndRecoversFromSelectionRaceWithoutReplayingRejectedWork()
    {
        using var app = new MissionApplication();
        var (enrollment, configuration) = await Assigned(app);
        using var machine = app.Daemon();
        var path = $"/v1/enrollments/{enrollment.Id}/mission";
        DevelopmentQueueRequest RequestNext() => new(Key(), WorkloadRequest.From(configuration),
            Request(configuration).WorkloadDigest, "test-model");
        app.Store.AfterCommit = writes =>
        {
            if (writes.Any(write => write.Record is DevelopmentMissionRecord { Progress.State: "reserving", Mission.Issue.Number: 7 }))
                app.Store.Seed(new DevelopmentIssueClaimRecord
                {
                    Id = "issue-7",
                    ScopeId = "development-issues:product/tasks",
                    EnrollmentId = Key(),
                    MissionId = Key()
                });
            return Task.CompletedTask;
        };
        var raced = RequestNext();
        using var lost = await SendAsync(machine, HttpMethod.Post, path + "/next", raced, raced.OperationId);
        Assert.Equal(HttpStatusCode.Conflict, lost.StatusCode);
        Assert.Equal("rejected", Assert.Single(app.Store.All<DevelopmentMissionRecord>()).Progress.State);
        Assert.Equal("rejected", (await Next(machine, path, raced)).Mission!.State);
        var next = (await Next(machine, path, RequestNext())).Mission!;
        Assert.Equal(8, next.Mission.Issue.Number);
        Assert.Equal("claimed", next.State);
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("needsAttention")]
    [InlineData("cancelled")]
    public async Task UnresolvedQueueMissionsRetainOwnership(string state)
    {
        using var app = new MissionApplication();
        var (enrollment, configuration) = await Assigned(app);
        using var machine = app.Daemon();
        var path = $"/v1/enrollments/{enrollment.Id}/mission";
        var claim = Request(configuration);
        var first = await Claim(machine, path, claim);
        using var fault = await SendAsync(machine, HttpMethod.Put, path + "/progress",
            new DevelopmentMissionProgress(first.Mission.Id, first.Mission.Digest, state, 0, 0, null, null, false, "Requires reconciliation."), Key());
        fault.EnsureSuccessStatusCode();
        var request = new DevelopmentQueueRequest(Key(), claim.Configuration, claim.WorkloadDigest, claim.Model);
        var retained = (await Next(machine, path, request)).Mission!;
        Assert.Equal(first.Mission.Id, retained.Mission.Id);
        Assert.Equal(state, retained.State);
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
    public async Task ModelCredentialsRequireExactExecutingMissionAndRemainFencedAfterRevocation()
    {
        using var app = new MissionApplication();
        var (enrollment, configuration) = await Assigned(app);
        using var machine = app.Daemon();
        using var owner = app.Human();
        var path = $"/v1/enrollments/{enrollment.Id}/mission";
        var delivery = await Claim(machine, path, Request(configuration));
        var credentialRequest = new MissionModelCredentialRequest(delivery.Mission.Id, delivery.Mission.Digest);
        using var premature = await machine.PostAsJsonAsync(path + "/model-credential", credentialRequest);
        Assert.Equal(HttpStatusCode.Conflict, premature.StatusCode);
        Assert.Equal(0, app.Models.Grants);
        using var progress = await SendAsync(machine, HttpMethod.Put, path + "/progress",
            new DevelopmentMissionProgress(delivery.Mission.Id, delivery.Mission.Digest, "executing", 1, 1, null, null, false, "Executing."), Key());
        progress.EnsureSuccessStatusCode();
        using var wrong = await machine.PostAsJsonAsync(path + "/model-credential", credentialRequest with { MissionId = Key() });
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        using var granted = await machine.PostAsJsonAsync(path + "/model-credential", credentialRequest);
        granted.EnsureSuccessStatusCode();
        Assert.Equal(1, app.Models.Grants);
        Assert.Equal(configuration.ProductionLine!.Owner, app.Models.LastOwner);
        Assert.Equal("no-store", granted.Headers.CacheControl!.ToString());
        using var humanDenied = await owner.PostAsJsonAsync(path + "/model-credential", credentialRequest);
        Assert.Equal(HttpStatusCode.Forbidden, humanDenied.StatusCode);
        app.Models.AfterAcquire = () => app.Models.Enabled = false;
        using var disabledDuringAcquire = await machine.PostAsJsonAsync(path + "/model-credential", credentialRequest);
        Assert.Equal(HttpStatusCode.Conflict, disabledDuringAcquire.StatusCode);
        var grants = app.Models.Grants;
        using var disabled = await machine.PostAsJsonAsync(path + "/model-credential", credentialRequest);
        Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
        Assert.Equal(grants, app.Models.Grants);
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

    private static async Task<DevelopmentQueueSelection> Next(HttpClient machine, string path, DevelopmentQueueRequest request)
    {
        using var response = await SendAsync(machine, HttpMethod.Post, path + "/next", request, request.OperationId);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DevelopmentQueueSelection>())!;
    }

    private static async Task<(EnrollmentRecord, WorkbenchLineConfiguration)> Assigned(MissionApplication app,
        ProductionLineExecution? execution = null)
    {
        using var owner = app.Human();
        using var machine = app.Daemon();
        var (plant, _) = await CreateAsync(owner, "Plant");
        var enrollment = SeedMember(app, plant.Id);
        var line = await CreateLineAsync(owner, Definition() with { Policy = new(true, true, 3, 30), Execution = execution });
        using var assignment = await owner.PutAsJsonAsync($"/v1/plants/{plant.Id}/production-line",
            new ProductionLineAssignment("1", 0, line.Id));
        assignment.EnsureSuccessStatusCode();
        return (enrollment, (await machine.GetFromJsonAsync<WorkbenchLineConfiguration>($"/v1/enrollments/{enrollment.Id}/configuration"))!);
    }

    private sealed class MissionApplication : ApiTestApplication
    {
        public FakeRepository Repository { get; } = new();
        public FakeModels Models { get; } = new();
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
                services.RemoveAll<IMissionModelAccess>();
                services.AddSingleton<IMissionModelAccess>(Models);
            });
        }

    }

    private sealed class FakeModels : IMissionModelAccess
    {
        public bool Enabled { get; set; } = true;
        public int Grants { get; private set; }
        public EntraPrincipal? LastOwner { get; private set; }
        public Action? AfterAcquire { get; set; }
        public Task RequireAsync(EntraPrincipal owner, CancellationToken cancellationToken) => Enabled ? Task.CompletedTask
            : throw new ApiException(409, "copilot_reauthorization_required", "Fixture authorization disabled.");
        public Task<ModelAccessCredential> AcquireAsync(EntraPrincipal owner, string model, CancellationToken cancellationToken)
        {
            Grants++;
            LastOwner = owner;
            AfterAcquire?.Invoke();
            return Task.FromResult(new ModelAccessCredential("github-copilot", model, "ghu_fixture-model", DateTimeOffset.UtcNow.AddHours(8)));
        }
    }

    private sealed class FakeRepository : IGitHubRepositoryReader, IGitHubDevelopmentRepository
    {
        public int WriteGrants { get; private set; }
        public bool Approved { get; set; }
        public bool Changed { get; set; }
        public bool ChangedHead { get; set; }
        public string ReviewState { get; set; } = "open";
        public long[] Issues { get; set; } = [7, 8];
        public string SourceCommit { get; set; } = new('a', 40);
        private readonly DateTimeOffset updated = DateTimeOffset.UtcNow;
        public Task<ResolvedRepositoryWorkload> ResolveAsync(WorkbenchLineConfiguration configuration, CancellationToken cancellationToken) =>
            Task.FromResult(ResolvedRepositoryWorkload.Create(configuration, SourceCommit, RepositoryWorkloadTests.Files(true)));
        public Task<DevelopmentIssue> IssueAsync(ProductionLineTasks tasks, long number, CancellationToken cancellationToken) =>
            Task.FromResult(new DevelopmentIssue(number, tasks.Repository, "Approved task", "## Acceptance criteria\n- Preserve ownership.",
                ["Preserve ownership."], ["approved"], Changed ? updated.AddSeconds(1) : updated));
        public async IAsyncEnumerable<DevelopmentIssue> EligibleIssuesAsync(ProductionLineTasks tasks,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var number in Issues)
                yield return await IssueAsync(tasks, number, cancellationToken);
        }
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
            Task.FromResult(new DevelopmentReview(ChangedHead ? new('f', 40) : pullRequest.CandidateCommit, ReviewState, Approved
                ? [new("review-1", "reviewer", pullRequest.CandidateCommit, "approved", "", DateTimeOffset.UtcNow)] : []));
    }
}
