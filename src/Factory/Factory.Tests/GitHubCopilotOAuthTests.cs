using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Factory.Plant.Service;
using Xunit;

namespace Factory.Tests;

public sealed class GitHubCopilotOAuthTests
{
    [Fact]
    public async Task ExchangesPkceUserCodeAndRotatesRefreshTokensUsingTheSameAccount()
    {
        var clock = new TestClock();
        var handler = new OAuthHandler();
        using var client = new GitHubCopilotOAuth(clock, handler);
        var app = await client.RegisterAsync("fixture-code", CancellationToken.None);
        Assert.DoesNotContain("fixture-secret", app.ToString());
        var authorization = await client.ExchangeAsync(app, "user-code", "fixture-verifier",
            "https://factory.example/copilot-access/callback", CancellationToken.None);
        Assert.Equal("fixture-verifier", handler.LastGrant.GetProperty("code_verifier").GetString());
        Assert.Equal(123, authorization.UserId);
        Assert.Equal(clock.Now.AddHours(8), authorization.ExpiresAt);
        var refreshed = await client.RefreshAsync(app, authorization, CancellationToken.None);
        Assert.Equal("refresh_token", handler.LastGrant.GetProperty("grant_type").GetString());
        Assert.Equal(authorization.RefreshToken, handler.LastGrant.GetProperty("refresh_token").GetString());
        Assert.NotEqual(authorization.RefreshToken, refreshed.RefreshToken);
        Assert.Equal(["api.github.com/app-manifests/fixture-code/conversions",
            "github.com/login/oauth/access_token", "api.github.com/user",
            "github.com/login/oauth/access_token", "api.github.com/user"], handler.Requests);
    }

    [Theory]
    [InlineData("nonrenewable")]
    [InlineData("error")]
    [InlineData("null-token")]
    [InlineData("installation-token")]
    [InlineData("changed-account")]
    [InlineData("redirect")]
    public async Task InvalidAuthorizationNeverBecomesASdkCredential(string failure)
    {
        var handler = new OAuthHandler { Failure = failure };
        using var client = new GitHubCopilotOAuth(new TestClock(), handler);
        var app = new CopilotAppRegistration(42, "fixture-app", "fixture-client", "fixture-secret");
        var previous = new CopilotUserAuthorization("ghu_old", DateTimeOffset.UtcNow.AddMinutes(1),
            "ghr_old", DateTimeOffset.UtcNow.AddDays(1), 123, "fixture-user");
        var error = await Assert.ThrowsAsync<ApiException>(() => client.RefreshAsync(app, previous, CancellationToken.None));
        Assert.DoesNotContain("ghr_old", error.Message);
        Assert.DoesNotContain("fixture-secret", error.Message);
        if (failure == "changed-account")
            Assert.Equal("copilot_identity_changed", error.Code);
    }

    private sealed class OAuthHandler : HttpMessageHandler
    {
        public string? Failure { get; init; }
        public List<string> Requests { get; } = [];
        public JsonElement LastGrant { get; private set; }
        private int grants;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.Host + uri.AbsolutePath);
            Assert.Equal("https", uri.Scheme);
            object body;
            if (uri.Host == "api.github.com" && uri.AbsolutePath.StartsWith("/app-manifests/"))
            {
                Assert.Null(request.Headers.Authorization);
                body = new { id = 42, slug = "fixture-app", client_id = "fixture-client", client_secret = "fixture-secret" };
            }
            else if (uri.Host == "github.com" && uri.AbsolutePath == "/login/oauth/access_token")
            {
                Assert.Null(request.Headers.Authorization);
                LastGrant = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
                Assert.Equal("fixture-secret", LastGrant.GetProperty("client_secret").GetString());
                grants++;
                if (Failure == "redirect")
                    return new(HttpStatusCode.Redirect) { Headers = { Location = new("https://other.example") } };
                body = Failure switch
                {
                    "nonrenewable" => new { access_token = "ghu_nonexpiring", token_type = "bearer" },
                    "error" => new { error = "bad_refresh_token" },
                    "null-token" => new { access_token = (string?)null, refresh_token = "ghr_refresh",
                        expires_in = 28800, refresh_token_expires_in = 15897600, token_type = "bearer" },
                    _ => new { access_token = Failure == "installation-token" ? "ghs_installation" : "ghu_fixture-user",
                        refresh_token = "ghr_fixture-" + grants, expires_in = 28800, refresh_token_expires_in = 15897600, token_type = "bearer" }
                };
            }
            else if (uri.Host == "api.github.com" && uri.AbsolutePath == "/user")
            {
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                Assert.Equal("ghu_fixture-user", request.Headers.Authorization.Parameter);
                body = new { id = Failure == "changed-account" ? 999 : 123, login = "fixture-user" };
            }
            else
                throw new InvalidOperationException("Unexpected OAuth request.");
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
    }
}
