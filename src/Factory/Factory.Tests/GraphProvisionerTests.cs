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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrincipalCreationUsesMinimalRequestAndVerifiesOwnershipBeforeReturning(bool wrongOwner)
    {
        var handler = new GraphHandler("AQID") { RejectOwnerBinding = true, WrongOwner = wrongOwner };
        using var directory = Create(handler);
        if (wrongOwner)
            await Assert.ThrowsAsync<DirectoryProvisioningException>(() =>
                directory.CreatePrincipalAsync(ApiTestApplication.DaemonClientId, CancellationToken.None));
        else
            Assert.Equal(GraphHandler.Principal,
                (await directory.CreatePrincipalAsync(ApiTestApplication.DaemonClientId, CancellationToken.None)).ObjectId);
        var body = Assert.Single(handler.Bodies).Body;
        Assert.Equal(ApiTestApplication.DaemonClientId, body.GetProperty("appId").GetString());
        Assert.False(body.TryGetProperty("owners@odata.bind", out _));
        Assert.Empty(handler.Deleted);
    }

    [Fact]
    public async Task GraphFailurePreservesStatusCodeAndRequestIdWithoutLoggingResponseSecrets()
    {
        var handler = new GraphHandler("AQID") { GraphFailure = true };
        using var directory = Create(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            directory.CreatePrincipalAsync(ApiTestApplication.DaemonClientId, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Contains("Authorization_RequestDenied", error.Message);
        Assert.Contains(GraphHandler.RequestId, error.Message);
        Assert.DoesNotContain("fixture-response-secret", error.ToString());
        Assert.DoesNotContain("fixture-directory-token", error.ToString());
    }

    [Fact]
    public async Task MalformedGraphErrorDoesNotReplaceForbiddenStatusOrExposeItsBody()
    {
        var handler = new GraphHandler("AQID") { GraphFailure = true, MalformedError = true };
        using var directory = Create(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            directory.CreatePrincipalAsync(ApiTestApplication.DaemonClientId, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Contains("invalid-error-json", error.Message);
        Assert.Contains(GraphHandler.RequestId, error.Message);
        Assert.DoesNotContain("fixture-response-secret", error.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetirementChecksOwnershipAndApplicationIdentityBeforeDeleting(bool principal)
    {
        var handler = new GraphHandler("AQID");
        using var directory = Create(handler);
        if (principal)
            await directory.DeletePrincipalAsync(GraphHandler.Principal, ApiTestApplication.DaemonClientId, CancellationToken.None);
        else
            await directory.DeleteApplicationAsync(GraphHandler.Application, ApiTestApplication.DaemonClientId, CancellationToken.None);
        Assert.Single(handler.Deleted);

        var foreign = new GraphHandler("AQID") { WrongOwner = true };
        using var foreignDirectory = Create(foreign);
        await Assert.ThrowsAsync<DirectoryProvisioningException>(() =>
            foreignDirectory.DeleteApplicationAsync(GraphHandler.Application, ApiTestApplication.DaemonClientId, CancellationToken.None));
        Assert.Empty(foreign.Deleted);
        await Assert.ThrowsAsync<DirectoryProvisioningException>(() =>
            directory.DeleteApplicationAsync(GraphHandler.Application, Guid.NewGuid().ToString("D"), CancellationToken.None));
        Assert.Single(handler.Deleted);
    }

    [Fact]
    public async Task RetirementIsIdempotentForMissingObjectsAndRefusesTheSharedApiIdentity()
    {
        var handler = new GraphHandler("AQID") { Missing = true };
        using var directory = Create(handler);
        await directory.DeletePrincipalAsync(GraphHandler.Principal, ApiTestApplication.DaemonClientId, CancellationToken.None);
        await directory.DeleteApplicationAsync(GraphHandler.Application, ApiTestApplication.DaemonClientId, CancellationToken.None);
        Assert.Empty(handler.Deleted);
        await Assert.ThrowsAsync<DirectoryProvisioningException>(() =>
            directory.DeleteApplicationAsync(GraphHandler.Application, ApiTestApplication.Audience, CancellationToken.None));
    }

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
        public const string RequestId = "60000000-0000-0000-0000-000000000006";
        public string PublicCertificate { get; } = certificate;
        public bool WrongOwner { get; init; }
        public bool WrongKey { get; init; }
        public bool Missing { get; init; }
        public bool RejectOwnerBinding { get; init; }
        public bool GraphFailure { get; init; }
        public bool MalformedError { get; init; }
        public List<string> Deleted { get; } = [];
        public List<(string Path, JsonElement Body)> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("graph.microsoft.com", request.RequestUri.Host);
            Assert.Equal("fixture-directory-token", request.Headers.Authorization?.Parameter);
            var path = request.RequestUri.AbsolutePath;
            if (Missing)
                return new(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Delete)
            {
                Deleted.Add(path);
                return new(HttpStatusCode.NoContent);
            }
            object body;
            if (request.Method == HttpMethod.Post)
            {
                var posted = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                Bodies.Add((path, posted));
                if (GraphFailure || RejectOwnerBinding && posted.TryGetProperty("owners@odata.bind", out _))
                {
                    var failure = new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = MalformedError ? new StringContent("fixture-response-secret") : JsonContent.Create(new { error = new
                        {
                            code = "Authorization_RequestDenied", message = "fixture-response-secret"
                        } })
                    };
                    failure.Headers.Add("request-id", RequestId);
                    return failure;
                }
                body = new { id = path == "/v1.0/servicePrincipals" ? Principal : Application,
                    appId = ApiTestApplication.DaemonClientId };
            }
            else if (path.EndsWith("/owners", StringComparison.Ordinal))
                body = new { value = new[] { new { id = WrongOwner ? Guid.NewGuid().ToString() : Owner } } };
            else if (path == $"/v1.0/applications/{Application}")
                body = new { id = Application, appId = ApiTestApplication.DaemonClientId,
                    keyCredentials = new[] { new { key = WrongKey ? "BAUG" : PublicCertificate } } };
            else if (path == $"/v1.0/servicePrincipals/{Principal}")
                body = new { id = Principal, appId = ApiTestApplication.DaemonClientId };
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
