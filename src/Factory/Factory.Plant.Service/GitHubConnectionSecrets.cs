using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using Factory.Plant.Service.Web;
using System.Text.Json;

namespace Factory.Plant.Service;

public sealed record GitHubAppRegistration(long AppId, string Slug, string Account, string PrivateKeyPem)
{
    public override string ToString() => $"GitHubAppRegistration ({AppId}; credentials redacted)";
}

public interface IGitHubConnectionSecrets
{
    void RequireConfigured();
    Task CheckAccessAsync(string reference, CancellationToken cancellationToken);
    Task SaveAsync(string reference, GitHubAppRegistration registration, CancellationToken cancellationToken);
    Task<GitHubAppRegistration> ReadAsync(string reference, CancellationToken cancellationToken);
}

public sealed class KeyVaultGitHubConnectionSecrets(WebsiteSettings website, TokenCredential credential) : IGitHubConnectionSecrets
{
    public void RequireConfigured() => _ = Vault(website);

    internal static Uri Vault(WebsiteSettings website)
    {
        if (!Uri.TryCreate(website.DataProtectionKeyId, UriKind.Absolute, out var key) ||
            key.Scheme != "https" || !key.Host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase) ||
            key.UserInfo.Length != 0 || key.Query.Length != 0 || key.Fragment.Length != 0 ||
            key.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is not ["keys", { Length: > 0 }])
            throw new ApiException(503, "github_vault_unconfigured",
                "GitHub onboarding uses the website's existing Azure Key Vault. Configure its DataProtectionKeyId through deployment.");
        return new(key.GetLeftPart(UriPartial.Authority));
    }

    private static string SecretName(string reference) =>
        ManagedGitHubConnections.IsReference(reference) ? reference
            : throw ApiProblems.Invalid("Invalid managed GitHub connection reference.");

    public async Task CheckAccessAsync(string reference, CancellationToken cancellationToken)
    {
        try
        {
            var client = new SecretClient(Vault(website), credential);
            _ = SecretName(reference);
            const string checkName = "factory-github-onboarding-access-check";
            await client.SetSecretAsync(new KeyVaultSecret(checkName, "pending-github-registration"), cancellationToken);
            var saved = await client.GetSecretAsync(checkName, cancellationToken: cancellationToken);
            if (saved.Value.Value != "pending-github-registration")
                throw new InvalidOperationException("The GitHub setup secret changed during its access check.");
        }
        catch (RequestFailedException)
        {
            throw new ApiException(503, "github_secret_storage_failed",
                "GitHub setup requires secret get/set access to the existing website vault. Apply the reviewed onboarding identity deployment before creating an app.");
        }
    }

    public async Task SaveAsync(string reference, GitHubAppRegistration registration, CancellationToken cancellationToken)
    {
        try
        {
            await new SecretClient(Vault(website), credential).SetSecretAsync(new KeyVaultSecret(SecretName(reference),
                JsonSerializer.Serialize(registration)), cancellationToken);
        }
        catch (RequestFailedException)
        {
            throw new ApiException(503, "github_secret_storage_failed",
                "The GitHub key could not be stored. Check the service identity's secret get/set grant and private vault connectivity. Do not retry app creation blindly.");
        }
    }

    public async Task<GitHubAppRegistration> ReadAsync(string reference, CancellationToken cancellationToken)
    {
        try
        {
            var secret = await new SecretClient(Vault(website), credential).GetSecretAsync(SecretName(reference),
                cancellationToken: cancellationToken);
            try
            {
                var registration = JsonSerializer.Deserialize<GitHubAppRegistration>(secret.Value.Value);
                return registration is { AppId: > 0 } && !string.IsNullOrWhiteSpace(registration.PrivateKeyPem)
                    ? registration : throw new JsonException();
            }
            catch (JsonException)
            {
                throw new ApiException(409, "github_registration_missing",
                    "No completed GitHub registration was saved. Reconcile or delete the incomplete GitHub App before starting another setup.");
            }
        }
        catch (RequestFailedException)
        {
            throw new ApiException(503, "github_secret_unavailable",
                "The GitHub registration secret is unavailable. Reconcile this setup before creating another app.");
        }
    }
}
