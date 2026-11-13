using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using Azure.AI.AgentServer.Core.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Workbench.Contracts;
using Workbench.Distributor;
using Workbench.Host;
using Xunit;

namespace Workbench.Tests;

[CollectionDefinition("Coordinator", DisableParallelization = true)]
public sealed class CoordinatorCollection;

[Collection("Coordinator")]
public sealed class DistributionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "coordinator-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string? previousRoot = Environment.GetEnvironmentVariable("AGENTSERVER_STATE_ROOT");
    private const string AdminToken = "operator-test-token-at-least-32-characters";
    private const string FirstToken = "first-workbench-test-token-at-least-32";
    private const string SecondToken = "second-workbench-test-token-at-least-32";

    public DistributionTests()
    {
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("AGENTSERVER_STATE_ROOT", root);
    }

    [Fact]
    public async Task RestartPreservesRegistrationAndClaimsAndNeverReassignsStaleWork()
    {
        var clock = new TestClock();
        var store = new CoordinatorStore(await BackendAsync(), clock);
        await store.RegisterAsync("box-one", "local:box-one", Registration(), CancellationToken.None);
        await store.SubmitAsync(Dispatch(), CancellationToken.None);
        var id = Guid.NewGuid().ToString("N");
        var claim = await store.ClaimAsync("box-one", "local:box-one", id, CancellationToken.None);
        clock.Now += TimeSpan.FromMinutes(2);
        store = new(await BackendAsync(), clock);
        Assert.Equal("attention", (await store.GetAsync("test-task", CancellationToken.None))!.Status);
        var workbench = Assert.Single(await store.WorkbenchesAsync(CancellationToken.None));
        Assert.Equal("offline", workbench.Presence);
        Assert.Equal("test-task", workbench.ActiveAssignmentId);
        await Assert.ThrowsAsync<DistributionConflictException>(() =>
            store.ClaimAsync("box-one", "local:box-one", Guid.NewGuid().ToString("N"), CancellationToken.None));
        Assert.Equal(Wire.Write(claim), Wire.Write(await store.ClaimAsync("box-one", "local:box-one", id, CancellationToken.None)));
        await store.HeartbeatAsync("test-task", "box-one", "local:box-one", id, CancellationToken.None);
        Assert.Equal("online", Assert.Single(await store.WorkbenchesAsync(CancellationToken.None)).Presence);
        await store.CompleteAsync("test-task", "box-one", "local:box-one", id, Success(), CancellationToken.None);
        await store.CompleteAsync("test-task", "box-one", "local:box-one", id, Success(), CancellationToken.None);
        Assert.Equal("succeeded", (await new CoordinatorStore(await BackendAsync()).GetAsync("test-task", CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task ConcurrentCoordinatorInstancesReserveOneAssignmentPerWorkbench()
    {
        var backend = await BackendAsync();
        var store = new CoordinatorStore(backend);
        await store.RegisterAsync("box-one", "local:box-one", Registration(), CancellationToken.None);
        await store.SubmitAsync(Dispatch(), CancellationToken.None);
        await store.SubmitAsync(Dispatch("second-task"), CancellationToken.None);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try
            {
                return await new CoordinatorStore(await BackendAsync())
                    .ClaimAsync("box-one", "local:box-one", Guid.NewGuid().ToString("N"), CancellationToken.None);
            }
            catch (DistributionConflictException)
            {
                return null;
            }
        }));
        Assert.Single(attempts, claim => claim is not null);
        Assert.Equal("queued", (await store.GetAsync("second-task", CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task TwoRegisteredWorkbenchesKeepIndependentOwnershipAcrossCoordinatorRestart()
    {
        var store = new CoordinatorStore(await BackendAsync());
        await store.RegisterAsync("box-one", "local:box-one", Registration(), CancellationToken.None);
        await store.RegisterAsync("box-two", "local:box-two", Registration() with { MachineName = "second-machine" }, CancellationToken.None);
        await store.SubmitAsync(Dispatch(), CancellationToken.None);
        await store.SubmitAsync(Dispatch("second-task") with { WorkbenchId = "box-two" }, CancellationToken.None);
        var firstId = Guid.NewGuid().ToString("N");
        var secondId = Guid.NewGuid().ToString("N");
        var claims = await Task.WhenAll(
            store.ClaimAsync("box-one", "local:box-one", firstId, CancellationToken.None),
            store.ClaimAsync("box-two", "local:box-two", secondId, CancellationToken.None));
        Assert.Equal("test-task", claims[0]!.Dispatch.Request.Assignment.Id);
        Assert.Equal("second-task", claims[1]!.Dispatch.Request.Assignment.Id);
        store = new(await BackendAsync());
        await store.CompleteAsync("test-task", "box-one", "local:box-one", firstId, Success(), CancellationToken.None);
        await store.CompleteAsync("second-task", "box-two", "local:box-two", secondId,
            Success() with { AssignmentId = "second-task" }, CancellationToken.None);
        Assert.All(await store.WorkbenchesAsync(CancellationToken.None), item => Assert.Null(item.ActiveAssignmentId));
        Assert.Equal("succeeded", (await store.GetAsync("second-task", CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task RegistrationAndTerminalResultsCannotBeHijackedOrOverwritten()
    {
        var store = new CoordinatorStore(await BackendAsync());
        await Assert.ThrowsAsync<DistributionConflictException>(() => store.SubmitAsync(Dispatch(), CancellationToken.None));
        await store.RegisterAsync("box-one", "local:box-one", Registration(), CancellationToken.None);
        await Assert.ThrowsAsync<DistributionConflictException>(() =>
            store.RegisterAsync("box-one", "another-principal", Registration(), CancellationToken.None));
        await store.SubmitAsync(Dispatch(), CancellationToken.None);
        await Assert.ThrowsAsync<DistributionConflictException>(() => store.SubmitAsync(Dispatch(), CancellationToken.None));
        var claim = (await store.ClaimAsync("box-one", "local:box-one", Guid.NewGuid().ToString("N"), CancellationToken.None))!;
        await Assert.ThrowsAsync<DistributionConflictException>(() =>
            store.CompleteAsync("test-task", "box-one", "local:box-one", "wrong", Success(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.CompleteAsync("test-task", "box-one", "local:box-one", claim.ClaimId,
                Success() with { AssignmentId = "other" }, CancellationToken.None));
        await store.CompleteAsync("test-task", "box-one", null, null, Success(), CancellationToken.None);
        await store.SubmitAsync(Dispatch("second-task"), CancellationToken.None);
        await Assert.ThrowsAsync<DistributionConflictException>(() =>
            store.ClaimAsync("box-one", "local:box-one", claim.ClaimId, CancellationToken.None));
        await Assert.ThrowsAsync<DistributionConflictException>(() =>
            store.CompleteAsync("test-task", "box-one", "local:box-one", claim.ClaimId,
                Success() with { Status = "failed", Error = "Conflicting outcome" }, CancellationToken.None));
    }

    [Fact]
    public async Task OversizedPayloadDoesNotCommitOrReleaseAnAssignment()
    {
        var store = new CoordinatorStore(await BackendAsync());
        await store.RegisterAsync("box-one", "local:box-one", Registration(), CancellationToken.None);
        await store.SubmitAsync(Dispatch(), CancellationToken.None);
        var claim = (await store.ClaimAsync("box-one", "local:box-one", Guid.NewGuid().ToString("N"), CancellationToken.None))!;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.CompleteAsync("test-task", "box-one", "local:box-one", claim.ClaimId,
                Success() with { AgentResponse = new string('x', 950 * 1024) }, CancellationToken.None));
        Assert.Equal("running", (await store.GetAsync("test-task", CancellationToken.None))!.Status);
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
        Assert.Throws<ArgumentException>(() => DistributorClient.Create("https://example.com", FirstToken));
    }

    [Fact]
    public void HostedAccessUsesPlatformCallerAndDoesNotAuthorizeSelfReportedWorkbenchIdentity()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Coordinator:Operators:0"] = "operator-principal",
            ["Coordinator:Workbenches:box-one"] = "machine-principal"
        }).Build();
        var access = new CoordinatorAccess(configuration, local: false);
        var context = new DefaultHttpContext();
        context.Request.Headers["x-agent-user-id"] = "machine-principal";
        context.Request.Headers["x-agent-foundry-call-id"] = "platform-call";
        context.Request.Headers["x-client-workbench"] = "arbitrary";
        var caller = access.Authenticate(context.Request);
        Assert.False(caller.IsOperator);
        Assert.Equal("box-one", caller.WorkbenchId);
        context.Request.Headers.Remove("x-agent-foundry-call-id");
        Assert.Throws<UnauthorizedAccessException>(() => access.Authenticate(context.Request));
    }

    [Fact]
    public async Task FoundryClientRefreshesCredentialsAndPreservesInvocationSession()
    {
        var handler = new CaptureInvocations();
        using var http = new HttpClient(handler);
        var endpoint = new Uri("https://example.services.ai.azure.com/api/projects/plant/agents/coordinator/endpoint/protocols/invocations?api-version=v1&agent_session_id=stable");
        var credential = new TestCredential();
        using var client = new DistributorClient(http, endpoint, credential);
        await client.InvokeAsync(new() { Operation = "identity" }, CancellationToken.None);
        await client.InvokeAsync(new() { Operation = "identity" }, CancellationToken.None);
        Assert.Equal(2, credential.Calls);
        Assert.Equal(endpoint, handler.Target);
        Assert.Equal("Bearer", handler.Authorization!.Scheme);
        Assert.Equal("https://ai.azure.com/.default", Assert.Single(credential.Scopes!));
        Assert.Throws<ArgumentException>(() => DistributorClient.CreateFoundry("https://example.com/project", Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task InvocationServiceAuthenticatesRegistersAndSeparatesWorkbenches()
    {
        await using var app = await ApplicationAsync();
        await app.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("readiness")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(anonymous, new() { Operation = "identity" })).StatusCode);
        using var admin = Http(app.Urls.Single(), AdminToken);
        using var first = Http(app.Urls.Single(), FirstToken);
        using var second = Http(app.Urls.Single(), SecondToken);
        Assert.Equal(HttpStatusCode.Conflict,
            (await PostAsync(admin, new() { Operation = "submit", Dispatch = Dispatch() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostAsync(first, new() { Operation = "register", WorkbenchId = "box-two", Registration = Registration() })).StatusCode);
        using var client = new DistributorClient(first);
        await client.RegisterAsync("box-one", Registration(), CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(first, new() { Operation = "submit", Dispatch = Dispatch() })).StatusCode);
        using var submitter = new DistributorClient(Http(app.Urls.Single(), AdminToken));
        Assert.Equal("queued", (await submitter.SubmitAsync(Dispatch(), CancellationToken.None)).Status);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostAsync(second, new() { Operation = "get", AssignmentId = "test-task" })).StatusCode);
        var claim = (await client.ClaimAsync("box-one", Guid.NewGuid().ToString("N"), CancellationToken.None))!;
        Assert.Equal(Wire.Write(claim), Wire.Write(await client.ClaimAsync("box-one", claim.ClaimId, CancellationToken.None)));
        await client.HeartbeatAsync(claim, CancellationToken.None);
        await client.CompleteAsync(claim, Success(), CancellationToken.None);
        await client.CompleteAsync(claim, Success(), CancellationToken.None);
        Assert.Equal("succeeded", (await client.GetAsync("test-task", CancellationToken.None)).Status);
        first.DefaultRequestHeaders.Add("x-agent-user-id", "operator-principal");
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(first, new() { Operation = "workbenches" })).StatusCode);
        await app.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DaemonRegistersAndExecutesOnceDespiteLostResultAcknowledgement(bool rejectShell)
    {
        await using var app = await ApplicationAsync();
        await app.StartAsync();
        var store = (CoordinatorStore)app.Services.GetService(typeof(CoordinatorStore))!;
        await store.RegisterAsync("box-one", "local:box-one", Registration(), CancellationToken.None);
        await store.SubmitAsync(Dispatch() with { Request = Dispatch().Request with { AllowShellExecution = rejectShell } }, CancellationToken.None);
        using var lostAcknowledgement = new LoseFirstResultResponse { InnerHandler = new HttpClientHandler() };
        using var http = new HttpClient(lostAcknowledgement) { BaseAddress = new Uri(app.Urls.Single()) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FirstToken);
        using var client = new DistributorClient(http);
        var calls = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var running = Daemon(client, (_, _) => { calls++; return Task.FromResult(Success()); }).RunAsync(stop.Token);
        try
        {
            await UntilAsync(async () => !File.Exists(Path.Combine(root, "daemon", "pending.json")) &&
                (await store.GetAsync("test-task", stop.Token))?.Result is not null, stop.Token);
        }
        finally
        {
            await stop.CancelAsync();
            await IgnoreCancellationAsync(running);
        }
        Assert.Equal(rejectShell ? 0 : 1, calls);
        Assert.Equal(2, lostAcknowledgement.ResultCalls);
        Assert.Equal(rejectShell ? "failed" : "succeeded", (await store.GetAsync("test-task", CancellationToken.None))!.Status);
        await app.StopAsync();
    }

    [Fact]
    public async Task DaemonRecoversLostClaimResponseUsingPersistedIntent()
    {
        await using var app = await ApplicationAsync();
        await app.StartAsync();
        var store = (CoordinatorStore)app.Services.GetService(typeof(CoordinatorStore))!;
        await store.RegisterAsync("box-one", "local:box-one", Registration(), CancellationToken.None);
        await store.SubmitAsync(Dispatch(), CancellationToken.None);
        var intent = new ClaimRequest(Guid.NewGuid().ToString("N"));
        await store.ClaimAsync("box-one", "local:box-one", intent.ClaimId, CancellationToken.None);
        var state = Path.Combine(root, "daemon");
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(Path.Combine(state, "claim-intent.json"), Wire.Write(intent));
        using var client = new DistributorClient(Http(app.Urls.Single(), FirstToken));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var calls = 0;
        var running = Daemon(client, (_, _) => { calls++; return Task.FromResult(Success()); }).RunAsync(stop.Token);
        try
        {
            await UntilAsync(async () => (await store.GetAsync("test-task", stop.Token))?.Status == "succeeded", stop.Token);
        }
        finally
        {
            await stop.CancelAsync();
            await IgnoreCancellationAsync(running);
        }
        Assert.Equal(1, calls);
        await app.StopAsync();
    }

    [Fact]
    public async Task DaemonResendsSavedResultButNeverRerunsInterruptedWork()
    {
        await using var app = await ApplicationAsync();
        await app.StartAsync();
        var store = (CoordinatorStore)app.Services.GetService(typeof(CoordinatorStore))!;
        await store.RegisterAsync("box-one", "local:box-one", Registration(), CancellationToken.None);
        await store.SubmitAsync(Dispatch(), CancellationToken.None);
        var claim = (await store.ClaimAsync("box-one", "local:box-one", Guid.NewGuid().ToString("N"), CancellationToken.None))!;
        var state = Path.Combine(root, "daemon");
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(Path.Combine(state, "pending.json"), Wire.Write(claim));
        using var client = new DistributorClient(Http(app.Urls.Single(), FirstToken));
        var daemon = Daemon(client, (_, _) => throw new InvalidOperationException("Must not execute"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => daemon.RunAsync(CancellationToken.None));
        await File.WriteAllTextAsync(Path.Combine(state, $"result-{claim.ClaimId}.json"), Wire.Write(Success()));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = daemon.RunAsync(stop.Token);
        try
        {
            await UntilAsync(async () => (await store.GetAsync("test-task", stop.Token))?.Status == "succeeded", stop.Token);
        }
        finally
        {
            await stop.CancelAsync();
            await IgnoreCancellationAsync(running);
        }
        await app.StopAsync();
    }

    private Task<FoundryStateStore> BackendAsync() => FoundryStateStore.GetOrCreateAsync("plant-test", itemTtlSeconds: -1);
    private static WorkerResult Success() => new() { AssignmentId = "test-task", Status = "succeeded", Commit = "verified-commit" };
    private static WorkbenchRegistration Registration() => new("test-machine", ["wsl", "copilot-sdk"]);
    private static DispatchRequest Dispatch(string id = "test-task") => new()
    {
        WorkbenchId = "box-one",
        Request = WorkbenchTests.Request() with { Assignment = WorkbenchTests.Request().Assignment with { Id = id } }
    };

    private async Task<Microsoft.AspNetCore.Builder.WebApplication> ApplicationAsync() =>
        await DistributorApplication.BuildAsync(
            ["--urls", "http://127.0.0.1:0", "--Coordinator:Mode", "Local", "--Coordinator:StoreName", "plant-test",
                "--Coordinator:Local:OperatorToken", AdminToken, "--Coordinator:Local:Workbenches:box-one", FirstToken,
                "--Coordinator:Local:Workbenches:box-two", SecondToken, "--Logging:LogLevel:Default", "Warning"]);

    private static HttpClient Http(string endpoint, string token)
    {
        var client = new HttpClient { BaseAddress = new Uri(endpoint) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, CoordinatorInvocation input) =>
        client.PostAsJsonAsync("invocations", input, Wire.Options);

    private WorkbenchDaemon Daemon(DistributorClient client,
        Func<WorkerRequest, CancellationToken, Task<WorkerResult>> execute) => new(
        client, "box-one", Path.Combine(root, "daemon"),
        new(["chieftn/productionOutput1"], false, false), execute,
        id => Path.Combine(root, "output", id, "result.json"));

    private static async Task UntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        while (!await condition())
            await Task.Delay(20, cancellationToken);
        await Task.Delay(100, cancellationToken);
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AGENTSERVER_STATE_ROOT", previousRoot);
        Directory.Delete(root, recursive: true);
    }

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
            var input = await request.Content!.ReadFromJsonAsync<CoordinatorInvocation>(Wire.Options, cancellationToken);
            var response = await base.SendAsync(request, cancellationToken);
            if (input?.Operation == "result" && ++ResultCalls == 1)
            {
                response.Dispose();
                throw new HttpRequestException("Simulated lost result acknowledgement.");
            }
            return response;
        }
    }

    private sealed class CaptureInvocations : HttpMessageHandler
    {
        public Uri? Target { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Target = request.RequestUri;
            Authorization = request.Headers.Authorization;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new CoordinatorReply { PrincipalId = "test" }, options: Wire.Options) });
        }
    }

    private sealed class TestCredential : TokenCredential
    {
        public int Calls { get; private set; }
        public string[]? Scopes { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            Scopes = requestContext.Scopes;
            return ValueTask.FromResult(new AccessToken("test-token", DateTimeOffset.UtcNow.AddMinutes(5)));
        }
    }
}
