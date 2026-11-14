using System.Security.Claims;
using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Factory.Plant.Service.Web;

public static class WebsiteAuthentication
{
    public const string CookieScheme = "Factory.Web.Cookie";
    public const string OidcScheme = "Factory.Web.Oidc";
    public const string UserPolicy = "web-user";
    public const string CreatorPolicy = "web-creator";
    public const string EnrollmentAdministratorPolicy = "web-enrollment-admin";

    public static bool AddFactoryWebsite(this IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment, IDataProtectionBuilder protection)
    {
        var enabled = configuration.GetValue<bool>("Factory:Web:Enabled");
        services.AddOptions<WebsiteSettings>().BindConfiguration("Factory:Web");
        services.AddSingleton(services => services.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebsiteSettings>>().Value);
        services.AddControllersWithViews(options => options.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute()));
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "__Host-Factory.Antiforgery";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });

        var authentication = services.AddAuthentication()
            .AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = "__Host-Factory.Session";
                options.Cookie.Path = "/";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.LoginPath = "/account/signin";
                options.AccessDeniedPath = "/account/access-denied";
                options.ExpireTimeSpan = TimeSpan.FromHours(1);
                options.SlidingExpiration = false;
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.Redirect(QueryHelpers.AddQueryString("/account/signin", "returnUrl",
                        context.Request.PathBase + context.Request.Path + context.Request.QueryString));
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.Redirect("/account/access-denied");
                    return Task.CompletedTask;
                };
                options.Events.OnValidatePrincipal = context =>
                {
                    if (context.Principal is not { } user ||
                        !ValidUser(user, context.HttpContext.RequestServices.GetRequiredService<ApiSettings>()))
                        context.RejectPrincipal();
                    return Task.CompletedTask;
                };
            })
            .AddPolicyScheme("Factory.Request", null, options =>
                options.ForwardDefaultSelector = context => context.Request.Path.StartsWithSegments("/v1")
                    ? JwtBearerDefaults.AuthenticationScheme : CookieScheme);

        services.AddAuthorizationBuilder()
            .AddPolicy(EnrollmentAdministratorPolicy, policy => policy.AddAuthenticationSchemes(CookieScheme)
                .RequireAuthenticatedUser().RequireRole("Factory.User").RequireRole(ApiCaller.EnrollmentAdministratorRole))
            .AddPolicy(UserPolicy, policy => policy.AddAuthenticationSchemes(CookieScheme)
                .RequireAuthenticatedUser().RequireRole("Factory.User"))
            .AddPolicy(CreatorPolicy, policy => policy.AddAuthenticationSchemes(CookieScheme)
                .RequireAuthenticatedUser().RequireRole("Factory.User").RequireRole("Factory.PlantCreator"));

        if (enabled)
        {
            authentication.AddMicrosoftIdentityWebApp(configuration.GetSection("Factory:Web"),
                openIdConnectScheme: OidcScheme, cookieScheme: null);
            services.AddOptions<MicrosoftIdentityOptions>(OidcScheme).Configure<ApiSettings>((options, api) =>
            {
                options.Instance = "https://login.microsoftonline.com/";
                options.TenantId = api.TenantId;
                options.ClientId = api.ClientId;
                options.ClientSecret = api.ClientSecret;
            });
            services.AddOptions<OpenIdConnectOptions>(OidcScheme).PostConfigure<ApiSettings, WebsiteSettings>((options, api, web) =>
            {
                options.SignInScheme = CookieScheme;
                options.MapInboundClaims = false;
                // Identity.Web supplies a handler separate from the OIDC options' default handler.
                options.TokenHandler = new JsonWebTokenHandler { MapInboundClaims = false };
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.SaveTokens = false;
                options.UseTokenLifetime = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.CallbackPath = "/signin-oidc";
                options.SignedOutCallbackPath = "/signout-callback-oidc";
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = api.Issuer,
                    IssuerValidator = (issuer, _, _) => issuer == api.Issuer
                        ? issuer : throw new SecurityTokenInvalidIssuerException("The browser token issuer is not accepted."),
                    ValidateAudience = true,
                    ValidAudience = api.ClientId,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromMinutes(1),
                    NameClaimType = "name",
                    RoleClaimType = "roles"
                };
                options.Events.OnRedirectToIdentityProvider = context =>
                {
                    context.ProtocolMessage.RedirectUri = web.PublicOrigin.TrimEnd('/') + options.CallbackPath;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToIdentityProviderForSignOut = context =>
                {
                    context.ProtocolMessage.PostLogoutRedirectUri = web.PublicOrigin.TrimEnd('/') + options.SignedOutCallbackPath;
                    return Task.CompletedTask;
                };
                options.Events.OnTokenValidated = context =>
                {
                    if (context.Principal is not { } user || !ValidUser(user, api) ||
                        user.FindAll("ver").Select(claim => claim.Value).ToArray() is not ["2.0"])
                        context.Fail("A tenant user with the Factory.User role and a complete Entra identity is required.");
                    return Task.CompletedTask;
                };
                options.Events.OnRemoteFailure = context =>
                {
                    context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Factory.Web.Authentication")
                        .LogWarning("Browser sign-in failed; correlation {CorrelationId}", context.HttpContext.TraceIdentifier);
                    context.Response.Redirect("/account/signin-error");
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
            });
            if (!environment.IsDevelopment())
            {
                protection.PersistKeysToAzureBlobStorage(services => new BlobClient(
                    new Uri(services.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebsiteSettings>>().Value.DataProtectionBlobUri),
                    services.GetRequiredService<TokenCredential>()))
                .ProtectKeysWithAzureKeyVault(
                    services => new Uri(services.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebsiteSettings>>().Value.DataProtectionKeyId),
                    services => services.GetRequiredService<TokenCredential>());
            }
        }

        services.Configure<AuthenticationOptions>(options =>
        {
            options.DefaultAuthenticateScheme = "Factory.Request";
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        });
        return enabled;
    }

    public static ApiCaller Caller(ClaimsPrincipal user, ApiSettings settings) =>
        ValidUser(user, settings) && ApiCaller.TryReadPrincipal(user, out var principal)
            ? new(principal!, settings.ClientId, user.IsInRole(ApiCaller.EnrollmentAdministratorRole))
            : throw new InvalidOperationException("The browser session has no validated Entra user.");

    private static bool ValidUser(ClaimsPrincipal user, ApiSettings settings) =>
        ApiCaller.TryReadPrincipal(user, out var principal) && principal!.TenantId == settings.TenantId &&
        !user.HasClaim("idtyp", "app") && user.IsInRole("Factory.User");
}
