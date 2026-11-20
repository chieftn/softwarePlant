using Azure.Core;
using Factory.Plant.Service;
using Factory.Plant.Service.Web;
using Xunit;

namespace Factory.Tests;

public sealed class GitHubConnectionSecretsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("http://factory.vault.azure.net/keys/cookies")]
    [InlineData("https://factory.vault.azure.net.evil.example/keys/cookies")]
    [InlineData("https://factory.vault.azure.net/secrets/password")]
    [InlineData("https://factory.vault.azure.net/keys/cookies?redirect=elsewhere")]
    public void MissingOrInvalidExistingVaultConfigurationFailsWithoutAcquiringCredentials(string key)
    {
        var secrets = new KeyVaultGitHubConnectionSecrets(new WebsiteSettings { DataProtectionKeyId = key }, new NoCredential());
        var error = Assert.Throws<ApiException>(secrets.RequireConfigured);
        Assert.Equal("github_vault_unconfigured", error.Code);
    }

    [Fact]
    public void ExistingWebsiteKeyIdentifiesVaultWithoutAdditionalConnectionSettings()
    {
        var secrets = new KeyVaultGitHubConnectionSecrets(new WebsiteSettings
        {
            DataProtectionKeyId = "https://factory.vault.azure.net/keys/cookie-protection"
        }, new NoCredential());
        secrets.RequireConfigured();
    }

    private sealed class NoCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Configuration validation must not acquire a token.");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Configuration validation must not acquire a token.");
    }
}
