using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Workbench.Contracts;
using Workbench.Distributor;
using Workbench.Host;
using Xunit;

namespace Workbench.Tests;

public sealed class DistributionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "distribution-tests-" + Guid.NewGuid().ToString("N"));
    private const string AdminToken = "operator-test-token-at-least-32-characters";
    private const string FirstToken = "first-workbench-test-token-at-least-32";
    private const string SecondToken = "second-workbench-test-token-at-least-32";

    public DistributionTests() => Directory.CreateDirectory(root);

    [Fact]
    public void RestartPreservesClaimsAndStaleWorkIsNeverReassigned()
    {
        var clock = new TestClock();
        var store = Store(clock);
        store.Submit(Dispatch());
        var claimId = Guid.NewGuid().ToString("N");
        var claim = Assert.IsType<WorkClaim>(store.Claim("box-one", claimId));
        clock.Now += TimeSpan.FromMinutes(2);
        store = Store(clock);
        Assert.Equal("attention", store.Get("test-task")!.Status);
        Assert.Throws<DistributionConflictException>(() => store.Claim("box-one", Guid.NewGuid().ToString("N")));
        Assert.Equal(Wire.Write(claim), Wire.Write(store.Claim("box-one", claimId)));
        store.Heartbeat("test-task", "box-one", claimId);
        Assert.Equal("running", store.Get("test-task")!.Status);
        store.Complete("test-task", "box-one", claimId, Success());
        store.Complete("test-task", "box-one", claimId, Success());
        Assert.Equal("succeeded", Store(clock).Get("test-task")!.Status);
        Assert.Null(store.Claim("box-one", Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task ConcurrentClaimsReserveOnlyOneAssignmentPerWorkbench()
    {
        var store = Store();
        store.Submit(Dispatch());
        store.Submit(Dispatch("second-task"));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try
            {
                return store.Claim("box-one", Guid.NewGuid().ToString("N"));
            }
            catch (DistributionConflictException)
            {
                return null;
            }
        })));
        Assert.Single(attempts, claim => claim is not null);
        Assert.Equal("queued", store.Get("second-task")!.Status);
    }

    [Fact]
    public void ClaimsAndTerminalResultsCannotBeOverwritten()
    {
        var store = Store();
        store.Submit(Dispatch());
        Assert.Throws<DistributionConflictException>(() => store.Submit(Dispatch()));
        var claim = store.Claim("box-one", Guid.NewGuid().ToString("N"))!;
        Assert.Throws<DistributionConflictException>(() => store.Heartbeat("test-task", "box-two", claim.ClaimId));
        Assert.Throws<DistributionConflictException>(() => store.Complete("test-task", "box-one", "wrong", Success()));
        Assert.Throws<InvalidDataException>(() => store.Complete("test-task", "box-one", claim.ClaimId,
            Success() with { AssignmentId = "other" }));
        store.Complete("test-task", "box-one", null, Success());
        store.Submit(Dispatch("second-task"));
        Assert.Throws<DistributionConflictException>(() => store.Claim("box-one", claim.ClaimId));
        Assert.Throws<DistributionConflictException>(() => store.Complete("test-task", "box-one", claim.ClaimId,
            Success() with { Status = "failed", Error = "Conflicting outcome" }));
    }

    [Fact]
    public void RemoteAssignmentsCannotExpandLocalExecutionPermissions()
    {
        var policy = new DaemonPolicy(["chieftn/productionOutput1"], false, false);
        policy.Validate(Dispatch().Request);
        Assert.Throws<InvalidDataException>(() => policy.Validate(Dispatch().Request with { AllowShellExecution = true }));
        Assert.Throws<InvalidDataException>(() => policy.Validate(Dispatch().Request with { PublishPullRequest = true }));
        Assert.Throws<InvalidDataException>(() => policy.Validate(Dispatch().Request with
        {
            Assignment = Dispatch().Request.Assignment with { Repository = "other/repository" }
        }));
        Assert.Throws<ArgumentException>(() => DistributorClient.Create("http://example.com", FirstToken));
    }

    [Fact]
    public async Task HttpServiceAuthenticatesAndSeparatesWorkbenches()
    {
        await using var app = Application();
        await app.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("assignments/test-task")).StatusCode);
        using var admin = Http(app.Urls.Single(), AdminToken);
        using var first = Http(app.Urls.Single(), FirstToken);
        using var second = Http(app.Urls.Single(), SecondToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await first.PostAsJsonAsync("assignments", Dispatch(), Wire.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("assignments",
            Dispatch() with { WorkbenchId = "unknown" }, Wire.Options)).StatusCode);
        using var submitter = new DistributorClient(Http(app.Urls.Single(), AdminToken));
        Assert.Equal("queued", (await submitter.SubmitAsync(Dispatch(), CancellationToken.None)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("assignments", Dispatch(), Wire.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await second.GetAsync("assignments/test-task")).StatusCode);
        var intent = new ClaimRequest(Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await second.PostAsJsonAsync("workbenches/box-one/claim", intent, Wire.Options)).StatusCode);
        using var client = new DistributorClient(first);
        var claim = Assert.IsType<WorkClaim>(await client.ClaimAsync("box-one", intent.ClaimId, CancellationToken.None));
        var repeated = await client.ClaimAsync("box-one", intent.ClaimId, CancellationToken.None);
        Assert.Equal(Wire.Write(claim), Wire.Write(repeated));
        await client.HeartbeatAsync(claim, CancellationToken.None);
        await client.CompleteAsync(claim, Success(), CancellationToken.None);
        await client.CompleteAsync(claim, Success(), CancellationToken.None);
        Assert.Equal("succeeded", (await client.GetAsync("test-task", CancellationToken.None)).Status);
        await app.StopAsync();
    }

    [Fact]
    public async Task DaemonExecutesAndReportsWithoutReplayingAfterLostResultAcknowledgement()
    {
        await using var app = Application();
        await app.StartAsync();
        var store = app.Services.GetService(typeof(AssignmentStore)) as AssignmentStore
            ?? throw new InvalidOperationException("Missing store.");
        store.Submit(Dispatch());
        using var lostAcknowledgement = new LoseFirstResultResponse { InnerHandler = new HttpClientHandler() };
        using var http = new HttpClient(lostAcknowledgement) { BaseAddress = new Uri(app.Urls.Single()) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FirstToken);
        using var client = new DistributorClient(http);
        var calls = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var daemon = Daemon(client, (_, _) =>
        {
            calls++;
            return Task.FromResult(Success());
        });
        var running = daemon.RunAsync(stop.Token);
        await UntilAsync(() => !File.Exists(Path.Combine(root, "daemon", "pending.json")) &&
            store.Get("test-task")?.Status == "succeeded", stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(1, calls);
        Assert.Equal(2, lostAcknowledgement.ResultCalls);
        Assert.Equal("succeeded", store.Get("test-task")!.Status);
        await app.StopAsync();
    }

    [Fact]
    public async Task DaemonReportsLocalPermissionRejectionWithoutStartingWorker()
    {
        await using var app = Application();
        await app.StartAsync();
        var store = (AssignmentStore)app.Services.GetService(typeof(AssignmentStore))!;
        store.Submit(Dispatch() with { Request = Dispatch().Request with { AllowShellExecution = true } });
        using var client = new DistributorClient(Http(app.Urls.Single(), FirstToken));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = Daemon(client, (_, _) => throw new InvalidOperationException("Must not execute")).RunAsync(stop.Token);
        await UntilAsync(() => store.Get("test-task")?.Status == "failed", stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Contains("shell", store.Get("test-task")!.Result!.Error);
        await app.StopAsync();
    }

    [Fact]
    public async Task DaemonRecoversLostClaimResponseUsingPersistedIntent()
    {
        await using var app = Application();
        await app.StartAsync();
        var store = (AssignmentStore)app.Services.GetService(typeof(AssignmentStore))!;
        store.Submit(Dispatch());
        var intent = new ClaimRequest(Guid.NewGuid().ToString("N"));
        store.Claim("box-one", intent.ClaimId);
        var state = Path.Combine(root, "daemon");
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(Path.Combine(state, "claim-intent.json"), Wire.Write(intent));
        using var client = new DistributorClient(Http(app.Urls.Single(), FirstToken));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var calls = 0;
        var running = Daemon(client, (_, _) => { calls++; return Task.FromResult(Success()); }).RunAsync(stop.Token);
        await UntilAsync(() => store.Get("test-task")?.Status == "succeeded", stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(1, calls);
        await app.StopAsync();
    }

    [Fact]
    public async Task DaemonResendsSavedResultButNeverRerunsInterruptedWork()
    {
        await using var app = Application();
        await app.StartAsync();
        var store = (AssignmentStore)app.Services.GetService(typeof(AssignmentStore))!;
        store.Submit(Dispatch());
        var claim = store.Claim("box-one", Guid.NewGuid().ToString("N"))!;
        var state = Path.Combine(root, "daemon");
        Directory.CreateDirectory(state);
        var pending = Path.Combine(state, "pending.json");
        await File.WriteAllTextAsync(pending, Wire.Write(claim));
        using var client = new DistributorClient(Http(app.Urls.Single(), FirstToken));
        var daemon = Daemon(client, (_, _) => throw new InvalidOperationException("Must not execute"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => daemon.RunAsync(CancellationToken.None));
        Assert.Equal("running", store.Get("test-task")!.Status);
        await File.WriteAllTextAsync(Path.Combine(state, $"result-{claim.ClaimId}.json"), Wire.Write(Success()));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = daemon.RunAsync(stop.Token);
        await UntilAsync(() => store.Get("test-task")?.Status == "succeeded", stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal("succeeded", store.Get("test-task")!.Status);
        await app.StopAsync();
    }

    private AssignmentStore Store(TimeProvider? clock = null) => new(Path.Combine(root, "distributor.db"), clock);
    private static WorkerResult Success() => new() { AssignmentId = "test-task", Status = "succeeded", Commit = "verified-commit" };
    private static DispatchRequest Dispatch(string id = "test-task") => new()
    {
        WorkbenchId = "box-one",
        Request = WorkbenchTests.Request() with { Assignment = WorkbenchTests.Request().Assignment with { Id = id } }
    };

    private Microsoft.AspNetCore.Builder.WebApplication Application() => DistributorApplication.Build(
        ["--urls", "http://127.0.0.1:0", "--Distributor:Database", Path.Combine(root, "distributor.db"),
            "--Distributor:AdminToken", AdminToken, "--Distributor:Workbenches:box-one", FirstToken,
            "--Distributor:Workbenches:box-two", SecondToken, "--Logging:LogLevel:Default", "Warning"]);

    private static HttpClient Http(string endpoint, string token)
    {
        var client = new HttpClient { BaseAddress = new Uri(endpoint) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private WorkbenchDaemon Daemon(DistributorClient client,
        Func<WorkerRequest, CancellationToken, Task<WorkerResult>> execute) => new(
        client, "box-one", Path.Combine(root, "daemon"),
        new(["chieftn/productionOutput1"], false, false), execute,
        id => Path.Combine(root, "output", id, "result.json"));

    private static async Task UntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
            await Task.Delay(20, cancellationToken);
        await Task.Delay(100, cancellationToken);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class LoseFirstResultResponse : DelegatingHandler
    {
        public int ResultCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri!.AbsolutePath.EndsWith("/result", StringComparison.Ordinal) && ++ResultCalls == 1)
            {
                response.Dispose();
                throw new HttpRequestException("Simulated lost result acknowledgement.");
            }
            return response;
        }
    }
}
