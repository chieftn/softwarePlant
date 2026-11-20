using System.Net;
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

public sealed class GitHubConnectionOnboardingTests
{
    private static readonly ApiCaller Owner = new(new(ApiTestApplication.Tenant, ApiTestApplication.OwnerId), ApiTestApplication.Audience);
    private static readonly ApiCaller Other = new(new(ApiTestApplication.Tenant, ApiTestApplication.ReaderId), ApiTestApplication.Audience);

    [Fact]
    public async Task ManifestStoresNoCredentialsAndCallbackCreatesOneReusableVerifiedConnection()
    {
        var fixture = new Fixture();
        var page = await fixture.Begin();
        using var manifest = JsonDocument.Parse(page.Manifest);
        Assert.False(manifest.RootElement.GetProperty("public").GetBoolean());
        Assert.InRange(manifest.RootElement.GetProperty("name").GetString()!.Length, 1, 34);
        Assert.False(manifest.RootElement.GetProperty("hook_attributes").GetProperty("active").GetBoolean());
        Assert.Equal("read", manifest.RootElement.GetProperty("default_permissions").GetProperty("contents").GetString());
        Assert.Contains("/organizations/product/settings/apps/new?", page.Action);
        Assert.DoesNotContain("private", page.Manifest, StringComparison.OrdinalIgnoreCase);
        var reference = await fixture.Connections.ManifestAsync(Owner, State(page), "fixture-code", CancellationToken.None);
        await fixture.Connections.ManifestAsync(Owner, State(page), "fixture-code", CancellationToken.None);
        Assert.Equal(1, fixture.GitHub.Exchanges);
        Assert.Equal(1, fixture.Secrets.Writes);
        Assert.Equal("installing", (await fixture.Connections.GetAsync(reference, Owner, CancellationToken.None)).State);
        await fixture.Connections.VerifyAsync(reference, Owner, CancellationToken.None);
        var resolved = await fixture.Connections.ResolveAsync(reference, "product/source", CancellationToken.None);
        Assert.Equal(42, resolved.AppId);
        Assert.Equal(84, resolved.InstallationId);
        Assert.False(resolved.AllowPublication);
        Assert.Equal("fixture-private-key", resolved.PrivateKeyPem);
        var writes = await Assert.ThrowsAsync<ApiException>(() => fixture.Connections.RequireReferenceAsync(
            reference, "product/source", Owner, CancellationToken.None, publication: true));
        Assert.Equal("repository_write_disabled", writes.Code);
        Assert.DoesNotContain("fixture-private-key", JsonSerializer.Serialize(fixture.Store.All<GitHubConnectionRecord>(), Records.Json));
        Assert.Single(await fixture.Connections.ListAsync(Owner, CancellationToken.None));
        Assert.Empty(await fixture.Connections.ListAsync(Other, CancellationToken.None));
    }

    [Fact]
    public async Task TamperedExpiredAndCrossUserCallbacksNeverExchangeCredentials()
    {
        var fixture = new Fixture();
        var page = await fixture.Begin();
        await Assert.ThrowsAsync<ApiException>(() => fixture.Connections.ManifestAsync(Other, State(page), "code", CancellationToken.None));
        await Assert.ThrowsAsync<ApiException>(() => fixture.Connections.ManifestAsync(Owner, "tampered", "code", CancellationToken.None));
        fixture.Clock.Now = fixture.Clock.Now.AddHours(2);
        await Assert.ThrowsAsync<ApiException>(() => fixture.Connections.ManifestAsync(Owner, State(page), "code", CancellationToken.None));
        Assert.Equal(0, fixture.GitHub.Exchanges);
        Assert.Equal(0, fixture.Secrets.Writes);
    }

    [Fact]
    public async Task VaultPermissionFailureStopsBeforeGitHubApprovalAndCannotEnableAConnection()
    {
        var fixture = new Fixture();
        fixture.Secrets.FailCheck = true;
        await Assert.ThrowsAsync<ApiException>(() => fixture.Begin());
        Assert.Equal(0, fixture.GitHub.Exchanges);
        Assert.DoesNotContain(fixture.Store.All<GitHubConnectionRecord>(), item => item.State == "connected");
    }

    [Fact]
    public async Task InterruptedSecretWriteCanRecoverSavedRegistrationWithoutExchangingCodeAgain()
    {
        var fixture = new Fixture();
        var page = await fixture.Begin();
        fixture.Secrets.FailAfterWrite = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Connections.ManifestAsync(Owner, State(page), "code", CancellationToken.None));
        Assert.Equal("exchanging", (await fixture.Connections.GetAsync(page.Reference, Owner, CancellationToken.None)).State);
        await Assert.ThrowsAsync<ApiException>(() => fixture.Connections.ManifestAsync(Owner, State(page), "code", CancellationToken.None));
        await fixture.Connections.RecoverAsync(page.Reference, Owner, CancellationToken.None);
        Assert.Equal("installing", (await fixture.Connections.GetAsync(page.Reference, Owner, CancellationToken.None)).State);
        Assert.Equal(1, fixture.GitHub.Exchanges);
    }

    [Fact]
    public async Task BrowserDisconnectAfterOneUseExchangeDoesNotDiscardReturnedPrivateKey()
    {
        var fixture = new Fixture();
        var page = await fixture.Begin();
        using var browser = new CancellationTokenSource();
        fixture.GitHub.AfterExchange = browser.Cancel;
        await fixture.Connections.ManifestAsync(Owner, State(page), "code", browser.Token);
        Assert.True(browser.IsCancellationRequested);
        Assert.Equal(1, fixture.Secrets.Writes);
        Assert.Equal("installing", (await fixture.Connections.GetAsync(page.Reference, Owner, CancellationToken.None)).State);
    }

    [Fact]
    public async Task DisabledUnselectedAndOtherOwnersConnectionsCannotAuthorizeRepositoryAccess()
    {
        var fixture = new Fixture();
        var page = await fixture.Begin();
        await fixture.Connections.ManifestAsync(Owner, State(page), "code", CancellationToken.None);
        await Assert.ThrowsAsync<ApiException>(() => fixture.Connections.ResolveAsync(page.Reference, "product/source", CancellationToken.None));
        await fixture.Connections.VerifyAsync(page.Reference, Owner, CancellationToken.None);
        await Assert.ThrowsAsync<ApiException>(() => fixture.Connections.RequireReferenceAsync(page.Reference, "product/source", Other, CancellationToken.None));
        await Assert.ThrowsAsync<ApiException>(() => fixture.Connections.ResolveAsync(page.Reference, "product/other", CancellationToken.None));
        await fixture.Connections.DisableAsync(page.Reference, Owner, CancellationToken.None);
        var reads = fixture.Secrets.Reads;
        await Assert.ThrowsAsync<ApiException>(() => fixture.Connections.ResolveAsync(page.Reference, "product/source", CancellationToken.None));
        await Assert.ThrowsAsync<ApiException>(() => fixture.Connections.VerifyAsync(page.Reference, Owner, CancellationToken.None));
        Assert.Equal(reads, fixture.Secrets.Reads);
    }

    [Fact]
    public async Task ProductionLineApiChecksManagedConnectionOwnershipAndPreservesLegacyReferences()
    {
        using var app = new OnboardingApplication();
        var connections = app.Services.GetRequiredService<ManagedGitHubConnections>();
        var page = await connections.BeginAsync(Owner, Guid.NewGuid().ToString("D"), "Source", "product", false, CancellationToken.None);
        await connections.ManifestAsync(Owner, State(page), "code", CancellationToken.None);
        await connections.VerifyAsync(page.Reference, Owner, CancellationToken.None);
        var request = ProductionLineTests.Definition() with
        {
            Source = ProductionLineTests.Definition().Source with { ConnectionReference = page.Reference }
        };
        using var owner = app.Human();
        using var other = app.Human(ApiTestApplication.ReaderId);
        var line = await ProductionLineTests.CreateLineAsync(owner, request);
        Assert.Equal(page.Reference, line.Definition.Source.ConnectionReference);
        using var forbidden = await PlantApiTests.SendAsync(other, HttpMethod.Post, "/v1/production-lines", request, PlantApiTests.Key());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await connections.DisableAsync(page.Reference, Owner, CancellationToken.None);
        using var disabled = await PlantApiTests.SendAsync(owner, HttpMethod.Post, "/v1/production-lines", request, PlantApiTests.Key());
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        var legacy = await ProductionLineTests.CreateLineAsync(owner);
        Assert.NotEqual(page.Reference, legacy.Definition.Source.ConnectionReference);
    }

    [Fact]
    public async Task BrowserOnboardingRequiresAntiforgeryIgnoresSpoofedInstallationAndOffersManagedDropdown()
    {
        using var app = new OnboardingApplication();
        using var browser = app.Browser();
        using var signIn = await app.SignInAsync(browser);
        using var index = await browser.GetAsync("/repository-connections");
        var html = await index.Content.ReadAsStringAsync();
        var form = new Dictionary<string, string>
        {
            ["operationKey"] = Hidden(html, "operationKey"),
            ["name"] = "Selected source",
            ["organization"] = "product"
        };
        using var rejected = await browser.PostAsync("/repository-connections", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Empty(app.Store.All<GitHubConnectionRecord>());
        form["__RequestVerificationToken"] = Hidden(html, "__RequestVerificationToken");
        using var begin = await browser.PostAsync("/repository-connections", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        Assert.Contains("https://github.com", begin.Headers.GetValues("Content-Security-Policy").Single());
        html = await begin.Content.ReadAsStringAsync();
        var action = WebUtility.HtmlDecode(Regex.Match(html, "action=\"(https://github.com[^\"]+)\"").Groups[1].Value);
        var state = QueryHelpers.ParseQuery(new Uri(action).Query)["state"].ToString();
        using var callback = await browser.GetAsync("/repository-connections/manifest?code=fixture-code&state=" + Uri.EscapeDataString(state));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var detailPath = callback.Headers.Location!.OriginalString;
        using var detail = await browser.GetAsync(detailPath + "?installation_id=999999");
        Assert.Equal(0, app.GitHub.Verifications);
        html = await detail.Content.ReadAsStringAsync();
        using var noCsrf = await browser.PostAsync(detailPath + "/verify", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var verified = await browser.PostAsync(detailPath + "/verify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Hidden(html, "__RequestVerificationToken")
        }));
        Assert.Equal(HttpStatusCode.Redirect, verified.StatusCode);
        Assert.Equal(1, app.GitHub.Verifications);
        using var linePage = await browser.GetAsync("/production-lines/create");
        html = await linePage.Content.ReadAsStringAsync();
        Assert.Contains("<select", html);
        Assert.Contains("Selected source", html);
        Assert.DoesNotContain("fixture-private-key", html);
        using var outsider = app.Browser();
        using var outsiderSignIn = await app.SignInAsync(outsider, ApiTestApplication.ReaderId);
        using var denied = await outsider.GetAsync(detailPath);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("fixture-private-key", await denied.Content.ReadAsStringAsync());
    }

    private static string Hidden(string html, string name) => WebUtility.HtmlDecode(
        Regex.Match(html, $"<input[^>]*name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);
    private static string State(GitHubManifestPage page) => QueryHelpers.ParseQuery(new Uri(page.Action).Query)["state"].ToString();

    private sealed class Fixture
    {
        public TestRecordStore Store { get; } = new();
        public TestClock Clock { get; } = new();
        public FakeSecrets Secrets { get; } = new();
        public FakeGitHub GitHub { get; } = new();
        public ManagedGitHubConnections Connections { get; }
        public Fixture() => Connections = new(Store, Secrets, GitHub, new EphemeralDataProtectionProvider(),
            new WebsiteSettings { PublicOrigin = "https://factory.example" }, Clock);
        public Task<GitHubManifestPage> Begin() => Connections.BeginAsync(Owner, PlantApiTests.Key(),
            "Source", "product", false, CancellationToken.None);
    }

    private sealed class OnboardingApplication : WebsiteTestApplication
    {
        public FakeSecrets Secrets { get; } = new();
        public FakeGitHub GitHub { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGitHubConnectionSecrets>();
                services.RemoveAll<IGitHubOnboardingClient>();
                services.AddSingleton<IGitHubConnectionSecrets>(Secrets);
                services.AddSingleton<IGitHubOnboardingClient>(GitHub);
            });
        }
    }

    private sealed class FakeSecrets : IGitHubConnectionSecrets
    {
        private readonly Dictionary<string, GitHubAppRegistration> values = [];
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public bool FailCheck { get; set; }
        public bool FailAfterWrite { get; set; }
        public void RequireConfigured() { }
        public Task CheckAccessAsync(string reference, CancellationToken cancellationToken) => FailCheck
            ? throw new ApiException(503, "github_secret_storage_failed", "Fixture vault permission failure.") : Task.CompletedTask;
        public Task SaveAsync(string reference, GitHubAppRegistration registration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes++;
            values[reference] = registration;
            if (FailAfterWrite)
                throw new IOException("Fixture lost vault response.");
            return Task.CompletedTask;
        }
        public Task<GitHubAppRegistration> ReadAsync(string reference, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(values[reference]);
        }
    }

    private sealed class FakeGitHub : IGitHubOnboardingClient
    {
        public int Exchanges { get; private set; }
        public int Verifications { get; private set; }
        public Action? AfterExchange { get; set; }
        public Task<GitHubAppRegistration> ConvertAsync(string code, CancellationToken cancellationToken)
        {
            Exchanges++;
            AfterExchange?.Invoke();
            return Task.FromResult(new GitHubAppRegistration(42, "fixture-app", "product", "fixture-private-key"));
        }
        public Task<GitHubInstallation> VerifyAsync(GitHubAppRegistration registration, bool publication, CancellationToken cancellationToken)
        {
            Verifications++;
            return Task.FromResult(new GitHubInstallation(84, ["product/source", "product/tasks"]));
        }
    }
}
