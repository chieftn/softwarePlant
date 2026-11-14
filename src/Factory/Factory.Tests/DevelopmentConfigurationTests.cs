using System.Net;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Factory.Tests;

public sealed class DevelopmentConfigurationTests
{
    [Fact]
    public void DevelopmentUsesTheApplicationCredentialWithoutAnAzureCliLogin()
    {
        using var source = new ApiTestApplication();
        using var app = source.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Factory:DataCredential"] = "ClientSecret",
                ["Factory:ClientSecret"] = "isolated-test-credential"
            })));

        Assert.IsType<ClientSecretCredential>(app.Services.GetRequiredService<TokenCredential>());
    }

    [Theory]
    [InlineData("Development", "")]
    [InlineData("Development", "@Microsoft.KeyVault(SecretUri=https://vault.invalid/secrets/client)")]
    [InlineData("Production", "isolated-test-credential")]
    [InlineData("Staging", "isolated-test-credential")]
    public async Task MissingCredentialsOrNonDevelopmentClientSecretsDisableTheApi(string environment, string secret)
    {
        using var source = new ApiTestApplication();
        using var app = source.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Factory:DataCredential"] = "ClientSecret",
                    ["Factory:ClientSecret"] = secret
                }));
        });
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/v1/plants");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void NonDevelopmentBrowserSignInStillRequiresAzureProtectedKeys(string environment)
    {
        using var app = new WebsiteTestApplication(environment);
        var failure = Assert.Throws<InvalidOperationException>(() => app.CreateClient());

        Assert.Contains("Factory:Web:DataProtectionBlobUri", failure.Message);
        Assert.Contains("Factory:Web:DataProtectionKeyId", failure.Message);
    }
}
