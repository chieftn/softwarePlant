using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Factory.Contracts;
using Factory.Plant.Service;
using Factory.Plant.Service.Storage;
using Factory.Plant.Service.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Factory.Tests;

public sealed class ManagedCopilotAccessTests
{
    private static readonly ApiCaller Owner = new(new(ApiTestApplication.Tenant, ApiTestApplication.OwnerId), ApiTestApplication.Audience);
    private static readonly ApiCaller Other = new(new(ApiTestApplication.Tenant, ApiTestApplication.ReaderId), ApiTestApplication.Audience);

    [Fact]
    public async Task SubscriptionAuthorizationIsSeparateFromRepositoriesAndUsesPkceOfflineUserConsent()
    {
        var fixture = new Fixture();
        var manifest = await fixture.Setup();
        using var document = JsonDocument.Parse(manifest.Manifest);
        Assert.Empty(document.RootElement.GetProperty("default_permissions").EnumerateObject());
        Assert.Contains("https://factory.example/copilot-access/callback",
            document.RootElement.GetProperty("callback_urls").EnumerateArray().Select(value => value.GetString()));
        var uri = await fixture.Access.BeginAuthorizationAsync(Owner, CancellationToken.None);
        var query = QueryHelpers.ParseQuery(new Uri(uri).Query);
        Assert.Equal("offline_access", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        using var disconnect = new CancellationTokenSource();
        fixture.OAuth.AfterExchange = disconnect.Cancel;
        await fixture.Access.CompleteAuthorizationAsync(Owner, query["state"].ToString(), "fixture-user-code", disconnect.Token);
        Assert.True(disconnect.IsCancellationRequested);
        Assert.Equal(query["code_challenge"], WebEncoders.Base64UrlEncode(SHA256.HashData(
            Encoding.ASCII.GetBytes(fixture.OAuth.Verifier!))));
        var credential = await fixture.Access.AcquireAsync(Owner.Principal, "test-model", CancellationToken.None);
        Assert.Equal("ghu_fixture-user", credential.AccessToken);
        Assert.Equal("test-model", credential.Model);
        var record = Assert.IsType<CopilotAccessRecord>(await fixture.Access.GetAsync(Owner.Principal, CancellationToken.None));
        Assert.Equal("connected", record.State);
        Assert.Null(await fixture.Access.GetAsync(Other.Principal, CancellationToken.None));
        var serialized = JsonSerializer.Serialize(fixture.Store.All<CopilotAccessRecord>(), Records.Json);
        Assert.DoesNotContain("ghu_", serialized);
        Assert.DoesNotContain("ghr_", serialized);
        Assert.DoesNotContain("fixture-client-secret", serialized);
        Assert.Empty(fixture.Store.All<GitHubConnectionRecord>());
    }

    [Fact]
    public async Task ForgedExpiredCrossOwnerAndReplayedCallbacksNeverExchangeUserTokens()
    {
        var fixture = new Fixture();
        await fixture.Setup();
        var uri = await fixture.Access.BeginAuthorizationAsync(Owner, CancellationToken.None);
        var state = State(uri);
        await Assert.ThrowsAsync<ApiException>(() => fixture.Access.CompleteAuthorizationAsync(Other, state, "code", CancellationToken.None));
        await Assert.ThrowsAsync<ApiException>(() => fixture.Access.CompleteAuthorizationAsync(Owner, "tampered", "code", CancellationToken.None));
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(11);
        await Assert.ThrowsAsync<ApiException>(() => fixture.Access.CompleteAuthorizationAsync(Owner, state, "code", CancellationToken.None));
        Assert.Equal(0, fixture.OAuth.Exchanges);
        uri = await fixture.Access.BeginAuthorizationAsync(Owner, CancellationToken.None);
        await fixture.Access.CompleteAuthorizationAsync(Owner, State(uri), "code", CancellationToken.None);
        await Assert.ThrowsAsync<ApiException>(() => fixture.Access.CompleteAuthorizationAsync(Owner, State(uri), "code", CancellationToken.None));
        Assert.Equal(1, fixture.OAuth.Exchanges);
    }

    [Fact]
    public async Task InterruptedRefreshRecoversSavedRotatedTokensWithoutReusingRefreshToken()
    {
        var fixture = new Fixture();
        await fixture.Connect();
        fixture.Clock.Now = fixture.Clock.Now.AddHours(7);
        fixture.Secrets.FailAfterAuthorizationSave = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Access.AcquireAsync(Owner.Principal, "test-model", CancellationToken.None));
        Assert.Equal("refreshing", (await fixture.Access.GetAsync(Owner.Principal, CancellationToken.None))!.State);
        await fixture.Access.RecoverAsync(Owner, CancellationToken.None);
        var credential = await fixture.Access.AcquireAsync(Owner.Principal, "test-model", CancellationToken.None);
        Assert.Equal("ghu_fixture-refreshed", credential.AccessToken);
        Assert.Equal(1, fixture.OAuth.Refreshes);
        Assert.Equal("ghr_fixture-first", fixture.OAuth.PreviousRefreshToken);
    }

    [Fact]
    public async Task ConcurrentAcquisitionsShareOneDurablyOwnedRefresh()
    {
        var fixture = new Fixture();
        await fixture.Connect();
        fixture.Clock.Now = fixture.Clock.Now.AddHours(7);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.OAuth.RefreshGate = release.Task;
        var first = fixture.Access.AcquireAsync(Owner.Principal, "test-model", CancellationToken.None);
        Assert.Equal(1, fixture.OAuth.Refreshes);
        var second = fixture.Access.AcquireAsync(Owner.Principal, "test-model", CancellationToken.None);
        release.SetResult();
        var tokens = await Task.WhenAll(first, second);
        Assert.All(tokens, token => Assert.Equal("ghu_fixture-refreshed", token.AccessToken));
        Assert.Equal(1, fixture.OAuth.Refreshes);
    }

    [Fact]
    public async Task DisableDuringRefreshCannotBeUndoneBySavingItsResult()
    {
        var fixture = new Fixture();
        await fixture.Connect();
        fixture.Clock.Now = fixture.Clock.Now.AddHours(7);
        fixture.OAuth.AfterRefresh = () => fixture.Access.DisableAsync(Owner, CancellationToken.None);
        await Assert.ThrowsAsync<ApiException>(() => fixture.Access.AcquireAsync(Owner.Principal, "test-model", CancellationToken.None));
        Assert.Equal("disabled", (await fixture.Access.GetAsync(Owner.Principal, CancellationToken.None))!.State);
        await Assert.ThrowsAsync<ApiException>(() => fixture.Access.RequireAsync(Owner.Principal, CancellationToken.None));
        await Assert.ThrowsAsync<ApiException>(() => fixture.Access.AcquireAsync(Other.Principal, "test-model", CancellationToken.None));
    }

    [Fact]
    public async Task LostRefreshResponseBlocksNewMissionAuthorityUntilExplicitReauthorization()
    {
        var fixture = new Fixture();
        await fixture.Connect();
        fixture.Clock.Now = fixture.Clock.Now.AddHours(7);
        fixture.OAuth.FailRefresh = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Access.AcquireAsync(Owner.Principal, "test-model", CancellationToken.None));
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(3);
        var blocked = await Assert.ThrowsAsync<ApiException>(() => fixture.Access.RequireAsync(Owner.Principal, CancellationToken.None));
        Assert.Equal("copilot_refresh_pending", blocked.Code);
        await Assert.ThrowsAsync<ApiException>(() => fixture.Access.AcquireAsync(Owner.Principal, "test-model", CancellationToken.None));
        Assert.Equal(1, fixture.OAuth.Refreshes);
        var uri = await fixture.Access.BeginAuthorizationAsync(Owner, CancellationToken.None);
        await fixture.Access.CompleteAuthorizationAsync(Owner, State(uri), "new-code", CancellationToken.None);
        Assert.Equal("ghu_fixture-user", (await fixture.Access.AcquireAsync(Owner.Principal, "test-model", CancellationToken.None)).AccessToken);
    }

    [Fact]
    public async Task BrowserSetupAndAuthorizationRequireAntiforgeryAndNeverExposeCredentials()
    {
        using var app = new CopilotWebsite();
        using var browser = app.Browser();
        using var signIn = await app.SignInAsync(browser);
        using var index = await browser.GetAsync("/copilot-access");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        var html = await index.Content.ReadAsStringAsync();
        Assert.Contains("<h1>Model access</h1>", html);
        Assert.Contains(">Connect GitHub Copilot</button>", html);
        var form = new Dictionary<string, string> { ["operationKey"] = Hidden(html, "operationKey") };
        using var rejected = await browser.PostAsync("/copilot-access/setup", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Empty(app.Store.All<CopilotAccessRecord>());
        form["__RequestVerificationToken"] = Hidden(html, "__RequestVerificationToken");
        using var begin = await browser.PostAsync("/copilot-access/setup", new FormUrlEncodedContent(form));
        begin.EnsureSuccessStatusCode();
        Assert.Contains("https://github.com", begin.Headers.GetValues("Content-Security-Policy").Single());
        var attempt = Assert.Single(app.Store.All<CopilotAccessRecord>()).Attempt;
        using var pendingPage = await browser.GetAsync("/copilot-access");
        html = await pendingPage.Content.ReadAsStringAsync();
        Assert.Contains("Continue setup", html);
        Assert.Equal(attempt, Hidden(html, "operationKey"));
        var continueForm = Regex.Match(html, "<form action=\"/copilot-access/setup\".*?</form>", RegexOptions.Singleline).Value;
        Assert.DoesNotContain("name=\"restart\"", continueForm);
        using var resumed = await browser.PostAsync("/copilot-access/setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["operationKey"] = Hidden(continueForm, "operationKey"),
            ["__RequestVerificationToken"] = Hidden(continueForm, "__RequestVerificationToken")
        }));
        resumed.EnsureSuccessStatusCode();
        Assert.Equal(attempt, Assert.Single(app.Store.All<CopilotAccessRecord>()).Attempt);
        html = await resumed.Content.ReadAsStringAsync();
        var action = WebUtility.HtmlDecode(Regex.Match(html, "action=\"(https://github.com[^\"]+)\"").Groups[1].Value);
        using var callback = await browser.GetAsync("/copilot-access/manifest?code=fixture-code&state=" + Uri.EscapeDataString(State(action)));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        using var page = await browser.GetAsync("/copilot-access");
        html = await page.Content.ReadAsStringAsync();
        Assert.DoesNotContain("fixture-client-secret", html);
        using var denied = await browser.PostAsync("/copilot-access/authorize", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        using var authorized = await browser.PostAsync("/copilot-access/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Hidden(html, "__RequestVerificationToken")
        }));
        Assert.Equal(HttpStatusCode.Redirect, authorized.StatusCode);
        Assert.Equal("github.com", authorized.Headers.Location!.Host);
        using var connectedCallback = await browser.GetAsync("/copilot-access/callback?code=fixture-user-code&state=" +
            Uri.EscapeDataString(State(authorized.Headers.Location.OriginalString)));
        Assert.Equal(HttpStatusCode.Redirect, connectedCallback.StatusCode);
        using var connectedPage = await browser.GetAsync("/copilot-access");
        connectedPage.EnsureSuccessStatusCode();
        html = await connectedPage.Content.ReadAsStringAsync();
        Assert.Contains("Authorization: <strong>Connected</strong>", html);
        Assert.Contains(">Reconnect GitHub Copilot</button>", html);
        Assert.DoesNotContain(">Connect GitHub Copilot</button>", html);
        Assert.DoesNotContain("fixture-client-secret", html);
    }

    [Fact]
    public async Task ExpiredSetupRequiresExplicitRestartWithANewAttempt()
    {
        using var app = new CopilotWebsite();
        using var browser = app.Browser();
        using var signIn = await app.SignInAsync(browser);
        var access = app.Services.GetRequiredService<ManagedCopilotAccess>();
        await access.BeginSetupAsync(Owner, PlantApiTests.Key(), false, CancellationToken.None);
        var previous = Assert.Single(app.Store.All<CopilotAccessRecord>()).Attempt;
        app.Clock.Now = app.Clock.Now.AddHours(2);
        using var refreshedSignIn = await app.SignInAsync(browser);
        using var page = await browser.GetAsync("/copilot-access");
        page.EnsureSuccessStatusCode();
        var html = await page.Content.ReadAsStringAsync();
        Assert.DoesNotContain(">Continue setup</button>", html);
        Assert.NotEqual(previous, Hidden(html, "operationKey"));
        var form = new Dictionary<string, string>
        {
            ["operationKey"] = Hidden(html, "operationKey"),
            ["__RequestVerificationToken"] = Hidden(html, "__RequestVerificationToken")
        };
        using var notConfirmed = await browser.PostAsync("/copilot-access/setup", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Conflict, notConfirmed.StatusCode);
        form["restart"] = "true";
        using var restarted = await browser.PostAsync("/copilot-access/setup", new FormUrlEncodedContent(form));
        restarted.EnsureSuccessStatusCode();
        Assert.NotEqual(previous, Assert.Single(app.Store.All<CopilotAccessRecord>()).Attempt);
    }

    private static string Hidden(string html, string name) => WebUtility.HtmlDecode(
        Regex.Match(html, $"<input[^>]*name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);
    private static string State(string uri) => QueryHelpers.ParseQuery(new Uri(uri).Query)["state"].ToString();

    private sealed class Fixture
    {
        public TestRecordStore Store { get; } = new();
        public TestClock Clock { get; } = new();
        public FakeSecrets Secrets { get; } = new();
        public FakeOAuth OAuth { get; }
        public ManagedCopilotAccess Access { get; }
        public Fixture()
        {
            OAuth = new(Clock);
            Access = new(Store, Secrets, OAuth, new EphemeralDataProtectionProvider(),
                new WebsiteSettings { PublicOrigin = "https://factory.example" }, Clock);
        }
        public async Task<GitHubManifestPage> Setup()
        {
            var manifest = await Access.BeginSetupAsync(Owner, PlantApiTests.Key(), false, CancellationToken.None);
            await Access.CompleteSetupAsync(Owner, State(manifest.Action), "fixture-app-code", CancellationToken.None);
            return manifest;
        }
        public async Task Connect()
        {
            await Setup();
            var uri = await Access.BeginAuthorizationAsync(Owner, CancellationToken.None);
            await Access.CompleteAuthorizationAsync(Owner, State(uri), "fixture-user-code", CancellationToken.None);
        }
    }

    private sealed class CopilotWebsite : WebsiteTestApplication
    {
        private readonly FakeSecrets secrets = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICopilotSecrets>();
                services.RemoveAll<IGitHubCopilotOAuth>();
                services.AddSingleton<ICopilotSecrets>(secrets);
                services.AddSingleton<IGitHubCopilotOAuth>(new FakeOAuth(Clock));
            });
        }
    }

    private sealed class FakeSecrets : ICopilotSecrets
    {
        private readonly Dictionary<(string, string), CopilotAppRegistration> apps = [];
        private readonly Dictionary<(string, string), CopilotUserAuthorization> authorizations = [];
        public bool FailAfterAuthorizationSave { get; set; }
        public Task CheckAsync(string reference, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAppAsync(string reference, string attempt, CopilotAppRegistration registration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            apps[(reference, attempt)] = registration;
            return Task.CompletedTask;
        }
        public Task<CopilotAppRegistration> ReadAppAsync(string reference, string attempt, CancellationToken cancellationToken) =>
            Task.FromResult(apps[(reference, attempt)]);
        public Task SaveAuthorizationAsync(string reference, string attempt, CopilotUserAuthorization authorization, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            authorizations[(reference, attempt)] = authorization;
            if (FailAfterAuthorizationSave) throw new IOException("Fixture lost vault response.");
            return Task.CompletedTask;
        }
        public Task<CopilotUserAuthorization> ReadAuthorizationAsync(string reference, string attempt, CancellationToken cancellationToken) =>
            Task.FromResult(authorizations[(reference, attempt)]);
    }

    private sealed class FakeOAuth(TestClock clock) : IGitHubCopilotOAuth
    {
        public int Exchanges { get; private set; }
        public int Refreshes { get; private set; }
        public string? Verifier { get; private set; }
        public string? PreviousRefreshToken { get; private set; }
        public Action? AfterExchange { get; set; }
        public Func<Task>? AfterRefresh { get; set; }
        public Task? RefreshGate { get; set; }
        public bool FailRefresh { get; set; }
        public Task<CopilotAppRegistration> RegisterAsync(string code, CancellationToken cancellationToken) =>
            Task.FromResult(new CopilotAppRegistration(42, "fixture-copilot-app", "fixture-client", "fixture-client-secret"));
        public Task<CopilotUserAuthorization> ExchangeAsync(CopilotAppRegistration app, string code, string verifier,
            string callback, CancellationToken cancellationToken)
        {
            Exchanges++;
            Verifier = verifier;
            Assert.Equal("https", new Uri(callback).Scheme);
            AfterExchange?.Invoke();
            return Task.FromResult(new CopilotUserAuthorization("ghu_fixture-user", clock.Now.AddHours(8),
                "ghr_fixture-first", clock.Now.AddDays(180), 123, "fixture-user"));
        }
        public async Task<CopilotUserAuthorization> RefreshAsync(CopilotAppRegistration app, CopilotUserAuthorization previous,
            CancellationToken cancellationToken)
        {
            Refreshes++;
            if (FailRefresh) throw new IOException("Fixture lost refresh response.");
            PreviousRefreshToken = previous.RefreshToken;
            if (RefreshGate is not null) await RefreshGate.WaitAsync(cancellationToken);
            if (AfterRefresh is not null) await AfterRefresh();
            return new("ghu_fixture-refreshed", clock.Now.AddHours(8), "ghr_fixture-rotated", clock.Now.AddDays(180), 123, "fixture-user");
        }
    }
}
