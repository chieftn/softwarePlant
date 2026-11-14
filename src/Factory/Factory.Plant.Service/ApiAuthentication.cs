using System.Security.Claims;
using Factory.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Factory.Plant.Service;

public sealed record ApiCaller(EntraPrincipal Principal, string ClientId, bool EnrollmentAdministrator = false)
{
    public string Key => $"{Principal.TenantId}:{Principal.ObjectId}";

    public static ApiCaller Read(ClaimsPrincipal user) =>
        TryRead(user, out var caller) ? caller!
            : throw new InvalidOperationException("The authenticated caller has no validated identity.");

    public static bool TryRead(ClaimsPrincipal user, out ApiCaller? caller)
    {
        caller = null;
        if (!TryReadPrincipal(user, out var principal) || !SingleGuid(user, "azp", out var client))
            return false;
        caller = new(principal!, client, user.IsInRole(EnrollmentAdministratorRole));
        return true;
    }

    internal static bool TryReadPrincipal(ClaimsPrincipal user, out EntraPrincipal? principal)
    {
        principal = null;
        if (!SingleGuid(user, "tid", out var tenant) || !SingleGuid(user, "oid", out var subject))
            return false;
        principal = new(tenant, subject);
        return true;
    }

    private static bool SingleGuid(ClaimsPrincipal user, string type, out string value)
    {
        value = "";
        var claims = user.FindAll(type).ToArray();
        if (claims.Length != 1 || !Guid.TryParseExact(claims[0].Value, "D", out var id) || id == Guid.Empty)
            return false;
        value = id.ToString("D");
        return true;
    }

    public const string EnrollmentAdministratorRole = "Factory.EnrollmentAdministrator";

    public void RequireEnrollmentAdministrator()
    {
        if (!EnrollmentAdministrator)
            throw ApiProblems.Forbidden();
    }
}

public static class ApiAuthentication
{
    public static void AddFactoryAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<ApiSettings>((options, settings) =>
        {
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = false;
            options.RequireHttpsMetadata = true;
            if (Guid.TryParseExact(settings.TenantId, "D", out _) && Guid.TryParseExact(settings.ClientId, "D", out _))
                options.Authority = settings.Issuer;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = settings.Issuer,
                ValidateAudience = true,
                ValidAudience = settings.ClientId,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ClockSkew = TimeSpan.FromMinutes(1),
                NameClaimType = "oid",
                RoleClaimType = "roles"
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    if (context.Principal is not { } user ||
                        user.FindAll("ver").Select(claim => claim.Value).ToArray() is not ["2.0"] ||
                        !ApiCaller.TryRead(user, out var caller) ||
                        caller!.Principal.TenantId != settings.TenantId)
                        context.Fail("A version-2 token with the configured tenant and complete identity is required.");
                    return Task.CompletedTask;
                },
                OnChallenge = context =>
                {
                    context.HandleResponse();
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    return ApiProblems.WriteAsync(context.HttpContext, 401, "unauthenticated", "A valid API bearer token is required.");
                },
                OnForbidden = context => ApiProblems.WriteAsync(context.HttpContext, 403, "forbidden",
                    "The token does not grant the required service-entry permission.")
            };
        });
        services.AddAuthorizationBuilder()
            .AddPolicy("enrollment-admin", policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().RequireAssertion(context =>
                    Human(context.User) && context.User.IsInRole(ApiCaller.EnrollmentAdministratorRole)))
            .AddPolicy("human", policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().RequireAssertion(context => Human(context.User)))
            .AddPolicy("creator", policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().RequireAssertion(context =>
                Human(context.User) && context.User.IsInRole("Factory.PlantCreator")))
            .AddPolicy("workbench", policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().RequireAssertion(context =>
                !context.User.HasClaim(claim => claim.Type == "scp") &&
                !context.User.HasClaim("idtyp", "user") && context.User.IsInRole("Workbench.Connect")));
    }

    private static bool Human(ClaimsPrincipal user)
    {
        var scopes = user.FindAll("scp").ToArray();
        return scopes.Length == 1 && scopes[0].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains("Plant.Access", StringComparer.Ordinal) &&
            !user.HasClaim("idtyp", "app") && user.IsInRole("Factory.User");
    }
}
