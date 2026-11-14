using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Factory.Plant.Service.Web;
using Factory.Plant.Service;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Factory.Tests;

public sealed class WebsiteTestApplication(string environment = "Development") : ApiTestApplication
{
    public bool EnableEnrollment { get; init; }
    private readonly Dictionary<string, string> tokens = [];
    public bool WrongAudience { get; set; }
    public bool WrongTenant { get; set; }
    public bool WrongNonce { get; set; }
    public bool MissingObjectId { get; set; }
    public bool DuplicateObjectId { get; set; }
    public Exception? AuthenticationFailure { get; private set; }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Host configuration reaches the entry point before optional middleware is registered.
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Factory:Web:Enabled"] = "true" }));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Factory:Web:PublicOrigin"] = "https://localhost",
            ["Factory:ClientSecret"] = "isolated-test-credential",
            ["Factory:Web:DataProtectionBlobUri"] = "",
            ["Factory:Web:DataProtectionKeyId"] = ""
        }));
        builder.ConfigureTestServices(services =>
        {
            if (EnableEnrollment)
            {
                services.PostConfigure<EnrollmentSettings>(settings =>
                {
                    settings.Enabled = true;
                    settings.InstallerClientId = Audience;
                    settings.ProvisioningClientId = Audience;
                    settings.ProvisioningObjectId = DaemonId;
                    settings.ProvisioningCredential = "ManagedIdentity";
                });
                foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService) &&
                    item.ImplementationType == typeof(EnrollmentWorker)).ToArray())
                    services.Remove(descriptor);
                services.RemoveAll<IDirectoryProvisioner>();
                services.AddSingleton<IDirectoryProvisioner>(new TestDirectory());
            }
            services.PostConfigure<KeyManagementOptions>(options =>
            {
                options.XmlRepository = new TestKeyRepository();
                options.XmlEncryptor = null;
            });
            services.PostConfigure<CookieAuthenticationOptions>(WebsiteAuthentication.CookieScheme,
                options => options.TimeProvider = Clock);
            services.PostConfigure<OpenIdConnectOptions>(WebsiteAuthentication.OidcScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration
                {
                    Issuer = Issuer,
                    AuthorizationEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize",
                    TokenEndpoint = "https://identity.example.invalid/token",
                    EndSessionEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/logout"
                };
                configuration.SigningKeys.Add(new RsaSecurityKey(signingKey.ExportParameters(false)) { KeyId = "test-key" });
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                options.Backchannel = new HttpClient(new TokenEndpointHandler(tokens));
                var remoteFailure = options.Events.OnRemoteFailure;
                options.Events.OnRemoteFailure = context =>
                {
                    AuthenticationFailure = context.Failure;
                    return remoteFailure(context);
                };
            });
        });
    }

    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    public async Task<HttpResponseMessage> SignInAsync(HttpClient browser, string subject = OwnerId, bool creator = true,
        string[]? roles = null)
    {
        using var challenge = await browser.GetAsync("/account/signin");
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var query = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        var code = Guid.NewGuid().ToString("N");
        var claims = new List<Claim>
        {
            new("sub", $"isolated-{subject}"),
            new("tid", WrongTenant ? ReaderId : Tenant),
            new("ver", "2.0"),
            new("name", "Factory test user"),
            new("nonce", WrongNonce ? "not-the-issued-nonce" : query["nonce"].ToString()),
            new(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };
        if (!MissingObjectId)
            claims.Add(new("oid", subject));
        if (DuplicateObjectId)
            claims.Add(new("oid", ReaderId));
        roles ??= creator ? ["Factory.User", "Factory.PlantCreator"] : ["Factory.User"];
        claims.AddRange(roles.Select(role => new Claim("roles", role)));
        tokens[code] = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            Issuer, WrongAudience ? ReaderId : Audience, claims,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10),
            new SigningCredentials(new RsaSecurityKey(signingKey) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256)));
        return await browser.PostAsync("/signin-oidc", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["state"] = query["state"].ToString()
        }));
    }

    private sealed class TokenEndpointHandler(Dictionary<string, string> tokens) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://identity.example.invalid/token", request.RequestUri!.AbsoluteUri);
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("authorization_code", form["grant_type"]);
            Assert.Equal(Audience, form["client_id"]);
            Assert.Equal("isolated-test-credential", form["client_secret"]);
            Assert.Equal("https://localhost/signin-oidc", form["redirect_uri"]);
            Assert.False(string.IsNullOrEmpty(form["code_verifier"]));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    id_token = tokens[form["code"].ToString()],
                    access_token = "isolated-test-access-token",
                    token_type = "Bearer",
                    expires_in = 600
                }), Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class TestKeyRepository : IXmlRepository
    {
        private readonly List<XElement> elements = [];
        public IReadOnlyCollection<XElement> GetAllElements() => elements.Select(element => new XElement(element)).ToArray();
        public void StoreElement(XElement element, string friendlyName) => elements.Add(new XElement(element));
    }
}
