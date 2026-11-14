using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace Factory.Plant.Service;

public sealed record DirectoryApplication(string ObjectId, string ClientId);
public sealed record DirectoryPrincipal(string ObjectId);
public sealed class DirectoryProvisioningException(string message) : Exception(message);

public interface IDirectoryProvisioner
{
    Task<DirectoryApplication?> FindApplicationAsync(string marker, string publicCertificate, CancellationToken cancellationToken);
    Task<DirectoryApplication> CreateApplicationAsync(string marker, string publicCertificate, CancellationToken cancellationToken);
    Task<DirectoryPrincipal?> FindPrincipalAsync(string clientId, CancellationToken cancellationToken);
    Task<DirectoryPrincipal> CreatePrincipalAsync(string clientId, CancellationToken cancellationToken);
    Task EnsureConnectRoleAsync(string principalId, CancellationToken cancellationToken);
    Task DeletePrincipalAsync(string principalId, string clientId, CancellationToken cancellationToken);
    Task DeleteApplicationAsync(string applicationId, string clientId, CancellationToken cancellationToken);
}

public sealed class GraphDirectoryProvisioner : IDirectoryProvisioner, IDisposable
{
    private readonly HttpClient http;
    private readonly TokenCredential credential;
    private readonly EnrollmentSettings settings;
    private readonly ApiSettings api;

    public GraphDirectoryProvisioner(EnrollmentSettings settings, ApiSettings api) : this(settings, api,
        CreateCredential(settings, api), new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

    internal GraphDirectoryProvisioner(EnrollmentSettings settings, ApiSettings api, TokenCredential credential, HttpMessageHandler handler)
    {
        this.settings = settings;
        this.api = api;
        this.credential = credential;
        http = new(handler)
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"), Timeout = TimeSpan.FromSeconds(20),
            MaxResponseContentBufferSize = 128 * 1024
        };
    }

    private static TokenCredential CreateCredential(EnrollmentSettings settings, ApiSettings api) =>
        settings.ProvisioningCredential switch
        {
            "ManagedIdentity" => new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ProvisioningClientId)),
            "ClientSecret" => new ClientSecretCredential(api.TenantId, settings.ProvisioningClientId, settings.ProvisioningClientSecret),
            _ => throw new InvalidOperationException("No explicit directory provisioning credential is configured.")
        };

    public async Task<DirectoryApplication?> FindApplicationAsync(string marker, string publicCertificate, CancellationToken cancellationToken)
    {
        var result = await SendAsync(HttpMethod.Get,
            $"applications?$filter={Uri.EscapeDataString($"displayName eq '{marker}'")}&$select=id,appId&$top=2", null, cancellationToken);
        var items = Values(result);
        if (items.Length == 0)
            return null;
        if (items.Length != 1 || result.TryGetProperty("@odata.nextLink", out _))
            throw new DirectoryProvisioningException("Directory application marker is ambiguous; administrator reconciliation is required.");
        await RequireOwnerAsync("applications", Id(items[0], "id"), cancellationToken);
        var credentials = await SendAsync(HttpMethod.Get, $"applications/{Id(items[0], "id")}?$select=keyCredentials", null, cancellationToken);
        var keys = credentials.GetProperty("keyCredentials").EnumerateArray().ToArray();
        if (keys.Length != 1 || !keys[0].TryGetProperty("key", out var encoded) || encoded.GetString() is not { } found ||
            !Convert.FromBase64String(found).AsSpan().SequenceEqual(Convert.FromBase64String(publicCertificate)))
            throw new DirectoryProvisioningException("The reconciled application's public key differs from the authorized enrollment.");
        return new(Id(items[0], "id"), Id(items[0], "appId"));
    }

    public async Task<DirectoryApplication> CreateApplicationAsync(string marker, string publicCertificate, CancellationToken cancellationToken)
    {
        using var certificate = EnrollmentOperations.Certificate(publicCertificate, DateTimeOffset.UtcNow);
        var result = await SendAsync(HttpMethod.Post, "applications", new Dictionary<string, object>
        {
            ["displayName"] = marker,
            ["signInAudience"] = "AzureADMyOrg",
            ["owners@odata.bind"] = new[] { $"https://graph.microsoft.com/v1.0/servicePrincipals/{settings.ProvisioningObjectId}" },
            ["keyCredentials"] = new[] { new
            {
                type = "AsymmetricX509Cert", usage = "Verify", key = publicCertificate,
                startDateTime = new DateTimeOffset(certificate.NotBefore.ToUniversalTime()),
                endDateTime = new DateTimeOffset(certificate.NotAfter.ToUniversalTime())
            } }
        }, cancellationToken);
        return new(Id(result, "id"), Id(result, "appId"));
    }

    public async Task<DirectoryPrincipal?> FindPrincipalAsync(string clientId, CancellationToken cancellationToken)
    {
        var result = await SendAsync(HttpMethod.Get,
            $"servicePrincipals?$filter={Uri.EscapeDataString($"appId eq '{clientId}'")}&$select=id&$top=2", null, cancellationToken);
        var items = Values(result);
        if (items.Length == 0)
            return null;
        if (items.Length != 1 || result.TryGetProperty("@odata.nextLink", out _))
            throw new DirectoryProvisioningException("Directory principal lookup is ambiguous.");
        await RequireOwnerAsync("servicePrincipals", Id(items[0], "id"), cancellationToken);
        return new(Id(items[0], "id"));
    }

    public async Task<DirectoryPrincipal> CreatePrincipalAsync(string clientId, CancellationToken cancellationToken)
    {
        var result = await SendAsync(HttpMethod.Post, "servicePrincipals", new Dictionary<string, object>
        {
            ["appId"] = clientId
        }, cancellationToken);
        var id = Id(result, "id");
        if (Id(result, "appId") != clientId)
            throw new DirectoryProvisioningException("Created principal does not match the authorized application.");
        await RequireOwnerAsync("servicePrincipals", id, cancellationToken);
        return new(id);
    }

    public async Task EnsureConnectRoleAsync(string principalId, CancellationToken cancellationToken)
    {
        var resource = await SendAsync(HttpMethod.Get,
            $"servicePrincipals?$filter={Uri.EscapeDataString($"appId eq '{api.ClientId}'")}&$select=id,appRoles&$top=2", null, cancellationToken);
        var resources = Values(resource);
        if (resources.Length != 1 || resource.TryGetProperty("@odata.nextLink", out _))
            throw new DirectoryProvisioningException("The configured plant API service principal is missing or ambiguous.");
        var roles = resources[0].GetProperty("appRoles").EnumerateArray().Where(role =>
            role.GetProperty("value").GetString() == "Workbench.Connect" && role.GetProperty("isEnabled").GetBoolean() &&
            role.GetProperty("allowedMemberTypes").EnumerateArray().Any(member => member.GetString() == "Application")).ToArray();
        if (roles.Length != 1)
            throw new DirectoryProvisioningException("The configured plant API requires one enabled application Workbench.Connect role.");
        var resourceId = Id(resources[0], "id");
        var roleId = Id(roles[0], "id");
        var assignments = await SendAsync(HttpMethod.Get,
            $"servicePrincipals/{principalId}/appRoleAssignments?$select=resourceId,appRoleId&$top=100", null, cancellationToken);
        if (assignments.TryGetProperty("@odata.nextLink", out _))
            throw new DirectoryProvisioningException("Unexpected directory permission inventory; reconcile before granting access.");
        if (Values(assignments).Any(item => Id(item, "resourceId") == resourceId && Id(item, "appRoleId") == roleId))
            return;
        await SendAsync(HttpMethod.Post, $"servicePrincipals/{principalId}/appRoleAssignments",
            new { principalId, resourceId, appRoleId = roleId }, cancellationToken);
    }

    public Task DeletePrincipalAsync(string principalId, string clientId, CancellationToken cancellationToken) =>
        DeleteOwnedAsync("servicePrincipals", principalId, clientId, cancellationToken);

    public Task DeleteApplicationAsync(string applicationId, string clientId, CancellationToken cancellationToken) =>
        DeleteOwnedAsync("applications", applicationId, clientId, cancellationToken);

    private async Task DeleteOwnedAsync(string kind, string objectId, string clientId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(objectId, "D", out var id) || id == Guid.Empty ||
            !Guid.TryParseExact(clientId, "D", out var client) || client == Guid.Empty ||
            clientId.Equals(api.ClientId, StringComparison.OrdinalIgnoreCase) ||
            clientId.Equals(settings.InstallerClientId, StringComparison.OrdinalIgnoreCase) ||
            clientId.Equals(settings.ProvisioningClientId, StringComparison.OrdinalIgnoreCase))
            throw new DirectoryProvisioningException("Refusing to retire a shared or invalid directory identity.");
        try
        {
            var existing = await SendAsync(HttpMethod.Get, $"{kind}/{objectId}?$select=id,appId", null, cancellationToken);
            if (Id(existing, "id") != objectId || Id(existing, "appId") != clientId)
                throw new DirectoryProvisioningException("Directory retirement identity does not match the recorded enrollment.");
            await RequireOwnerAsync(kind, objectId, cancellationToken);
            await SendAsync(HttpMethod.Delete, $"{kind}/{objectId}", null, cancellationToken);
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            // A lost delete response is safe to retry; a missing object is already retired.
        }
    }

    private async Task RequireOwnerAsync(string kind, string objectId, CancellationToken cancellationToken)
    {
        var owners = await SendAsync(HttpMethod.Get, $"{kind}/{objectId}/owners?$select=id&$top=100", null, cancellationToken);
        if (!Values(owners).Any(owner => Id(owner, "id") == settings.ProvisioningObjectId))
            throw new DirectoryProvisioningException("Refusing to adopt a directory object not owned by the configured provisioner.");
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://graph.microsoft.com/.default"]), cancellationToken);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var code = "not-returned";
            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String &&
                    value.GetString() is { Length: > 0 and <= 128 } candidate &&
                    candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.'))
                    code = candidate;
                else
                    code = "invalid-error-envelope";
            }
            catch (JsonException)
            {
                code = "invalid-error-json";
            }
            var requestId = response.Headers.TryGetValues("request-id", out var identifiers) &&
                Guid.TryParse(identifiers.FirstOrDefault(), out var id) ? id.ToString("D") : "not-returned";
            throw new HttpRequestException(
                $"Directory {method} {path.Split('?')[0]} returned HTTP {(int)response.StatusCode}; Graph code {code}; request-id {requestId}. Check consent and provisioner ownership.",
                null, response.StatusCode);
        }
        if (response.StatusCode == HttpStatusCode.NoContent)
            return JsonSerializer.SerializeToElement(new { });
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    private static JsonElement[] Values(JsonElement root) => root.GetProperty("value").EnumerateArray().ToArray();
    private static string Id(JsonElement element, string property)
    {
        if (!Guid.TryParseExact(element.GetProperty(property).GetString(), "D", out var id) || id == Guid.Empty)
            throw new DirectoryProvisioningException("Directory returned an invalid object identifier.");
        return id.ToString("D");
    }

    public void Dispose() => http.Dispose();
}
