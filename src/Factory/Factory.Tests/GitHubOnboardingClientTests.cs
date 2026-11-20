using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Factory.Plant.Service;
using Xunit;

namespace Factory.Tests;

public sealed class GitHubOnboardingClientTests
{
    [Fact]
    public async Task ServerDiscoversOwnerInstallationAndUsesReadOnlyTokenToEnumerateSelectedRepositories()
    {
        using var key = RSA.Create(2048);
        var handler = new GitHubFixture(key);
        using var client = new GitHubOnboardingClient(new TestClock(), handler);
        var registration = await client.ConvertAsync("fixture-code", CancellationToken.None);
        Assert.Equal(42, registration.AppId);
        Assert.DoesNotContain(key.ExportRSAPrivateKeyPem(), registration.ToString());
        var installation = await client.VerifyAsync(registration, true, CancellationToken.None);
        Assert.Equal(84, installation.InstallationId);
        Assert.Equal(["product/source", "product/tasks"], installation.Repositories);
        Assert.Equal(["/app-manifests/fixture-code/conversions", "/app", "/app/installations",
            "/app/installations/84/access_tokens", "/installation/repositories"], handler.Paths);
    }

    [Theory]
    [InlineData("all", false, "product", true, "github_installation_invalid")]
    [InlineData("selected", true, "product", true, "github_installation_invalid")]
    [InlineData("selected", false, "other-owner", true, "github_installation_missing")]
    [InlineData("selected", false, "product", false, "github_permissions_missing")]
    public async Task InvalidInstallationsCannotReachRepositoryTokenIssuance(string selection, bool suspended,
        string owner, bool writes, string code)
    {
        using var key = RSA.Create(2048);
        var handler = new GitHubFixture(key) { Selection = selection, Suspended = suspended, Owner = owner, Writes = writes };
        using var client = new GitHubOnboardingClient(new TestClock(), handler);
        var registration = new GitHubAppRegistration(42, "fixture-app", "product", key.ExportRSAPrivateKeyPem());
        var error = await Assert.ThrowsAsync<ApiException>(() => client.VerifyAsync(registration, true, CancellationToken.None));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("/app/installations/84/access_tokens", handler.Paths);
    }

    [Fact]
    public async Task ReadOnlyConnectionDoesNotRequireOrRequestWritePermission()
    {
        using var key = RSA.Create(2048);
        var handler = new GitHubFixture(key) { Writes = false };
        using var client = new GitHubOnboardingClient(new TestClock(), handler);
        var result = await client.VerifyAsync(new(42, "fixture-app", "product", key.ExportRSAPrivateKeyPem()), false, CancellationToken.None);
        Assert.Equal(84, result.InstallationId);
    }

    private sealed class GitHubFixture(RSA key) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public string Selection { get; init; } = "selected";
        public string Owner { get; init; } = "product";
        public bool Suspended { get; init; }
        public bool Writes { get; init; } = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            Assert.Equal("https", request.RequestUri.Scheme);
            Assert.Equal("api.github.com", request.RequestUri.Host);
            object body;
            switch (path)
            {
                case "/app-manifests/fixture-code/conversions":
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Null(request.Headers.Authorization);
                    body = new
                    {
                        id = 42,
                        slug = "fixture-app",
                        owner = new { login = "product" },
                        pem = key.ExportRSAPrivateKeyPem(),
                        client_secret = "unused-client-secret",
                        webhook_secret = "unused-webhook-secret"
                    };
                    break;
                case "/app":
                    ValidateJwt(request);
                    body = new { id = 42, slug = "fixture-app", owner = new { login = "product" } };
                    break;
                case "/app/installations":
                    ValidateJwt(request);
                    body = new[] { new { id = 84, account = new { login = Owner }, repository_selection = Selection,
                        suspended_at = Suspended ? (DateTimeOffset?)DateTimeOffset.UtcNow : null,
                        permissions = new { contents = Writes ? "write" : "read", issues = "read",
                            pull_requests = Writes ? "write" : "read" } } };
                    break;
                case "/app/installations/84/access_tokens":
                    ValidateJwt(request);
                    using (var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
                    {
                        var permissions = document.RootElement.GetProperty("permissions");
                        Assert.All(permissions.EnumerateObject(), permission => Assert.Equal("read", permission.Value.GetString()));
                    }
                    body = new { token = "fixture-installation-token" };
                    break;
                case "/installation/repositories":
                    Assert.Equal("fixture-installation-token", request.Headers.Authorization!.Parameter);
                    body = new { repositories = new[] { new { full_name = "product/source" }, new { full_name = "product/tasks" } } };
                    break;
                default:
                    throw new InvalidOperationException("Unexpected fixture request.");
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }

        private void ValidateJwt(HttpRequestMessage request)
        {
            var jwt = request.Headers.Authorization!.Parameter!.Split('.');
            static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') +
                new string('=', (4 - value.Length % 4) % 4));
            Assert.True(key.VerifyData(System.Text.Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]),
                Decode(jwt[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
    }
}
