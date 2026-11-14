using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Azure.Core;
using Factory.Plant.Service;
using Xunit;

namespace Factory.Tests;

public sealed class GraphProvisionerTests
{
    [Fact]
    public async Task DirectoryRequestsCarryOnlyPublicCredentialAndGrantOnlyTheConfiguredConnectRole()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Graph Provisioning Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(180));
        var handler = new GraphHandler(Convert.ToBase64String(certificate.RawData));
        using var directory = Create(handler);
        await directory.CreateApplicationAsync("Factory Workbench test", handler.PublicCertificate, CancellationToken.None);
        await directory.EnsureConnectRoleAsync(GraphHandler.Principal, CancellationToken.None);
        var created = Assert.Single(handler.Bodies, item => item.Path == "/v1.0/applications").Body;
        Assert.False(created.TryGetProperty("passwordCredentials", out _));
        Assert.Equal($"https://graph.microsoft.com/v1.0/servicePrincipals/{GraphHandler.Owner}",
            created.GetProperty("owners@odata.bind")[0].GetString());
        var credential = Assert.Single(created.GetProperty("keyCredentials").EnumerateArray());
        Assert.Equal(certificate.RawData, Convert.FromBase64String(credential.GetProperty("key").GetString()!));
        Assert.Equal(new DateTimeOffset(certificate.NotAfter.ToUniversalTime()), credential.GetProperty("endDateTime").GetDateTimeOffset());
        var grant = Assert.Single(handler.Bodies, item => item.Path.EndsWith("/appRoleAssignments", StringComparison.Ordinal)).Body;
        Assert.Equal(GraphHandler.ApiPrincipal, grant.GetProperty("resourceId").GetString());
        Assert.Equal(GraphHandler.ConnectRole, grant.GetProperty("appRoleId").GetString());
        Assert.Equal(GraphHandler.Principal, grant.GetProperty("principalId").GetString());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AmbiguousCreateReconciliationRejectsDifferentOwnerOrDifferentPublicKey(bool wrongOwner, bool wrongKey)
    {
        var handler = new GraphHandler("AQID") { WrongOwner = wrongOwner, WrongKey = wrongKey };
        using var directory = Create(handler);
        await Assert.ThrowsAsync<DirectoryProvisioningException>(() =>
            directory.FindApplicationAsync("Factory Workbench test", handler.PublicCertificate, CancellationToken.None));
        Assert.Empty(handler.Bodies);
    }

    private static GraphDirectoryProvisioner Create(GraphHandler handler) => new(
        new EnrollmentSettings { ProvisioningObjectId = GraphHandler.Owner },
        new ApiSettings { ClientId = ApiTestApplication.Audience, TenantId = ApiTestApplication.Tenant },
        new GraphCredential(), handler);

    private sealed class GraphCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Assert.Equal(["https://graph.microsoft.com/.default"], requestContext.Scopes);
            return new("fixture-directory-token", DateTimeOffset.UtcNow.AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class GraphHandler(string certificate) : HttpMessageHandler
    {
        public const string Owner = "60000000-0000-0000-0000-000000000001";
        public const string Application = "60000000-0000-0000-0000-000000000002";
        public const string Principal = "60000000-0000-0000-0000-000000000003";
        public const string ApiPrincipal = "60000000-0000-0000-0000-000000000004";
        public const string ConnectRole = "60000000-0000-0000-0000-000000000005";
        public string PublicCertificate { get; } = certificate;
        public bool WrongOwner { get; init; }
        public bool WrongKey { get; init; }
        public List<(string Path, JsonElement Body)> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("graph.microsoft.com", request.RequestUri.Host);
            Assert.Equal("fixture-directory-token", request.Headers.Authorization?.Parameter);
            var path = request.RequestUri.AbsolutePath;
            object body;
            if (request.Method == HttpMethod.Post)
            {
                Bodies.Add((path, await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken)));
                body = new { id = Application, appId = ApiTestApplication.DaemonClientId };
            }
            else if (path.EndsWith("/owners", StringComparison.Ordinal))
                body = new { value = new[] { new { id = WrongOwner ? Guid.NewGuid().ToString() : Owner } } };
            else if (path == $"/v1.0/applications/{Application}")
                body = new { keyCredentials = new[] { new { key = WrongKey ? "BAUG" : PublicCertificate } } };
            else if (path == "/v1.0/applications")
                body = new { value = new[] { new { id = Application, appId = ApiTestApplication.DaemonClientId } } };
            else if (path == "/v1.0/servicePrincipals")
            {
                Assert.Contains(ApiTestApplication.Audience, Uri.UnescapeDataString(request.RequestUri.Query));
                body = new { value = new[] { new
                {
                    id = ApiPrincipal,
                    appRoles = new[] { new { id = ConnectRole, value = "Workbench.Connect", isEnabled = true, allowedMemberTypes = new[] { "Application" } } }
                } } };
            }
            else if (path.EndsWith("/appRoleAssignments", StringComparison.Ordinal))
                body = new { value = Array.Empty<object>() };
            else
                throw new InvalidOperationException("Unexpected directory request in isolated test.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
    }
}
