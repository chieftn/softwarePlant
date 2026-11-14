using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Factory.Plant.Service;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Factory.Tests;

public sealed class ServiceFoundationTests : IClassFixture<UnconfiguredApplication>
{
    private readonly HttpClient client;

    public ServiceFoundationTests(UnconfiguredApplication factory)
    {
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task LandingPageExplainsMissingBrowserConfiguration()
    {
        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Browser sign-in is not configured", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LivenessDoesNotRequireCloudConfiguration()
    {
        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("alive", body.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("GET", "/.well-known/plant-config")]
    [InlineData("GET", "/health/ready")]
    public async Task UnimplementedOperationsReturnProblemDetailsInsteadOfPlaceholderSuccess(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(404, problem.Status);
    }

    [Fact]
    public async Task MissingCloudConfigurationExplicitlyDisablesTheApiWithoutAStorageOrAuthenticationFallback()
    {
        using var response = await client.GetAsync("/v1/plants");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("service_unavailable", problem.GetProperty("code").GetString());
        Assert.True(problem.TryGetProperty("correlationId", out _));
        Assert.NotNull(response.Headers.RetryAfter);
    }

    [Theory]
    [InlineData("/plants")]
    [InlineData("/account/signin")]
    public async Task MissingBrowserConfigurationReturnsAnExplicitHtmlError(string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Browser sign-in is not configured", await response.Content.ReadAsStringAsync());
    }
}
