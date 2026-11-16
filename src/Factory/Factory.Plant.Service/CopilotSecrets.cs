using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using Factory.Plant.Service.Web;

namespace Factory.Plant.Service;

public sealed record CopilotAppRegistration(long AppId, string Slug, string ClientId, string ClientSecret)
{
    public override string ToString() => $"CopilotAppRegistration ({AppId}; credentials redacted)";
}

public sealed record CopilotUserAuthorization(string AccessToken, DateTimeOffset ExpiresAt, string RefreshToken,
    DateTimeOffset RefreshExpiresAt, long UserId, string Login)
{
    public override string ToString() => $"CopilotUserAuthorization ({Login}; credentials redacted)";
}

public interface ICopilotSecrets
{
    Task CheckAsync(string reference, CancellationToken cancellationToken);
    Task SaveAppAsync(string reference, string attempt, CopilotAppRegistration registration, CancellationToken cancellationToken);
    Task<CopilotAppRegistration> ReadAppAsync(string reference, string attempt, CancellationToken cancellationToken);
    Task SaveAuthorizationAsync(string reference, string attempt, CopilotUserAuthorization authorization, CancellationToken cancellationToken);
    Task<CopilotUserAuthorization> ReadAuthorizationAsync(string reference, string attempt, CancellationToken cancellationToken);
}

public sealed class KeyVaultCopilotSecrets(WebsiteSettings website, TokenCredential credential) : ICopilotSecrets
{
    private SecretClient Client() => new(KeyVaultGitHubConnectionSecrets.Vault(website), credential);
    private static string Name(string reference, string? attempt = null)
    {
        if (!Regex.IsMatch(reference, "^copilot-[a-f0-9]{32}$") ||
            attempt is not null && !Guid.TryParseExact(attempt, "D", out _))
            throw ApiProblems.Invalid("Invalid Copilot credential reference.");
        return attempt is null ? reference : reference + "-" + Guid.Parse(attempt).ToString("N");
    }

    public async Task CheckAsync(string reference, CancellationToken cancellationToken)
    {
        _ = Name(reference);
        const string marker = "factory-copilot-onboarding-access-check";
        await WriteAsync(marker, "copilot-access-check", cancellationToken);
        if (await ReadAsync<string>(marker, cancellationToken) != "copilot-access-check")
            throw new InvalidOperationException("The Copilot vault access check changed.");
    }

    public Task SaveAppAsync(string reference, string attempt, CopilotAppRegistration registration, CancellationToken cancellationToken) =>
        WriteAsync(Name(reference, attempt) + "-app", registration, cancellationToken);
    public Task<CopilotAppRegistration> ReadAppAsync(string reference, string attempt, CancellationToken cancellationToken) =>
        ReadAsync<CopilotAppRegistration>(Name(reference, attempt) + "-app", cancellationToken);
    public Task SaveAuthorizationAsync(string reference, string attempt, CopilotUserAuthorization authorization, CancellationToken cancellationToken) =>
        WriteAsync(Name(reference, attempt) + "-user", authorization, cancellationToken);
    public Task<CopilotUserAuthorization> ReadAuthorizationAsync(string reference, string attempt, CancellationToken cancellationToken) =>
        ReadAsync<CopilotUserAuthorization>(Name(reference, attempt) + "-user", cancellationToken);

    private async Task WriteAsync<T>(string name, T value, CancellationToken cancellationToken)
    {
        try { await Client().SetSecretAsync(new KeyVaultSecret(name, JsonSerializer.Serialize(value)), cancellationToken); }
        catch (RequestFailedException)
        {
            throw new ApiException(503, "copilot_secret_storage_failed",
                "Copilot credentials could not be saved in the website vault. Check the existing secret get/set grant and private connectivity; recover saved authorization before retrying.");
        }
    }

    private async Task<T> ReadAsync<T>(string name, CancellationToken cancellationToken)
    {
        try
        {
            var secret = await Client().GetSecretAsync(name, cancellationToken: cancellationToken);
            return JsonSerializer.Deserialize<T>(secret.Value.Value)
                ?? throw new JsonException();
        }
        catch (RequestFailedException)
        {
            throw new ApiException(503, "copilot_secret_unavailable",
                "The saved Copilot credential is unavailable. Recover the interrupted setup, or explicitly authorize again if its user token was not saved.");
        }
        catch (JsonException)
        {
            throw new ApiException(503, "copilot_secret_invalid", "The saved Copilot credential is invalid; reconcile it before authorizing again.");
        }
    }
}
