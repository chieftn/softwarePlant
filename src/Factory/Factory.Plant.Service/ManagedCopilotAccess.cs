using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Contracts;
using Factory.Plant.Service.Storage;
using Factory.Plant.Service.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace Factory.Plant.Service;

public interface IMissionModelAccess
{
    Task RequireAsync(EntraPrincipal owner, CancellationToken cancellationToken);
    Task<ModelAccessCredential> AcquireAsync(EntraPrincipal owner, string model, CancellationToken cancellationToken);
}

public sealed class ManagedCopilotAccess(IRecordStore store, ICopilotSecrets secrets, IGitHubCopilotOAuth github,
    IDataProtectionProvider protection, WebsiteSettings website, TimeProvider clock) : IMissionModelAccess
{
    private readonly IDataProtector stateProtection = protection.CreateProtector("Factory.Copilot.OAuth.v1");
    private sealed record FlowState(EntraPrincipal Principal, string Attempt, string Kind, DateTimeOffset ExpiresAt, string Verifier);
    private static string Reference(EntraPrincipal owner) => "copilot-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{owner.TenantId}:{owner.ObjectId}")))[..32].ToLowerInvariant();
    private static string Scope(EntraPrincipal owner) => "model-access:" + Reference(owner);
    private string Origin()
    {
        if (!Uri.TryCreate(website.PublicOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ApiException(503, "copilot_origin_unconfigured", "Configure the website public HTTPS origin before Copilot authorization.");
        return website.PublicOrigin.TrimEnd('/');
    }

    public async Task<CopilotAccessRecord?> GetAsync(EntraPrincipal owner, CancellationToken cancellationToken) =>
        (await ReadAsync(owner, cancellationToken))?.Record as CopilotAccessRecord;

    private async Task<StoredRecord?> ReadAsync(EntraPrincipal owner, CancellationToken cancellationToken)
    {
        var stored = await store.ReadAsync(Scope(owner), Reference(owner), cancellationToken);
        if (stored is not null && (stored.Record is not CopilotAccessRecord record || record.Principal != owner ||
            record.ScopeId != Scope(owner) || record.Id != Reference(owner) || record.SchemaVersion != "1"))
            throw new InvalidOperationException("Stored Copilot authorization ownership is inconsistent.");
        return stored;
    }

    private async Task<StoredRecord> RequiredAsync(EntraPrincipal owner, CancellationToken cancellationToken) =>
        await ReadAsync(owner, cancellationToken) ?? throw Reauthorize();

    public async Task<GitHubManifestPage> BeginSetupAsync(ApiCaller caller, string operation, bool restart,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(operation, "D", out var attempt) || attempt == Guid.Empty)
            throw ApiProblems.Invalid("Reload Model access before creating the GitHub Copilot authorization app.");
        var origin = Origin();
        var stored = await ReadAsync(caller.Principal, cancellationToken);
        var previous = stored?.Record as CopilotAccessRecord;
        if (previous is { AppId: > 0 } ||
            previous is not null && !(previous.State == "pending" && previous.Attempt == operation) && !restart)
            throw new ApiException(409, "copilot_setup_exists", "Recover the saved app first. Restart app creation only after reconciling any incomplete app on GitHub.");
        var reference = Reference(caller.Principal);
        await secrets.CheckAsync(reference, cancellationToken);
        var record = previous is { State: "pending" } && previous.Attempt == operation ? previous : new CopilotAccessRecord
        {
            Id = reference, ScopeId = Scope(caller.Principal), Principal = caller.Principal, State = "pending",
            Attempt = operation, UpdatedAt = clock.GetUtcNow()
        };
        if (clock.GetUtcNow() >= record.UpdatedAt.AddHours(1))
            throw ApiProblems.Invalid("App setup expired. Reconcile any incomplete GitHub app and explicitly restart setup.");
        if (record != previous)
            await ReplaceAsync(stored, record, cancellationToken);
        var state = Protect(record, "manifest", "", record.UpdatedAt.AddHours(1));
        var manifest = JsonSerializer.Serialize(new
        {
            name = "Factory-Copilot-" + attempt.ToString("N")[..19],
            description = "Factory Copilot subscription authorization; no repository permissions.",
            url = origin, @public = false,
            redirect_url = origin + "/copilot-access/manifest",
            callback_urls = new[] { origin + "/copilot-access/callback" },
            hook_attributes = new { url = origin + "/copilot-access/events", active = false },
            request_oauth_on_install = false,
            default_permissions = new Dictionary<string, string>(),
            default_events = Array.Empty<string>()
        });
        return new("https://github.com/settings/apps/new?state=" + Uri.EscapeDataString(state), manifest, reference);
    }

    public async Task CompleteSetupAsync(ApiCaller caller, string state, string code, CancellationToken cancellationToken)
    {
        var flow = Unprotect(caller, state, "manifest");
        var stored = await RequiredAsync(caller.Principal, cancellationToken);
        var record = Records.Require<CopilotAccessRecord>(stored.Record);
        if (record.Attempt != flow.Attempt || record.State != "pending")
            throw ApiProblems.Conflict();
        var exchanging = record with { State = "exchangingApp", UpdatedAt = clock.GetUtcNow() };
        await ReplaceAsync(stored, exchanging, cancellationToken);
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var registration = await github.RegisterAsync(code, completion.Token);
        await secrets.SaveAppAsync(record.Id, record.Attempt, registration, completion.Token);
        await RecoverAsync(caller, completion.Token);
    }

    public async Task<string> BeginAuthorizationAsync(ApiCaller caller, CancellationToken cancellationToken)
    {
        var stored = await RequiredAsync(caller.Principal, cancellationToken);
        var record = Records.Require<CopilotAccessRecord>(stored.Record);
        if (record.AppId <= 0)
            throw ApiProblems.Invalid("Finish or recover authorization-app creation first.");
        var registration = await secrets.ReadAppAsync(record.Id, record.AppRegistrationId, cancellationToken);
        MatchApp(record, registration);
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var attempt = Guid.NewGuid().ToString("D");
        var next = record with { State = "authorizing", Attempt = attempt, UpdatedAt = clock.GetUtcNow() };
        await ReplaceAsync(stored, next, cancellationToken);
        var state = Protect(next, "user", verifier, clock.GetUtcNow().AddMinutes(10));
        return QueryHelpers.AddQueryString("https://github.com/login/oauth/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = registration.ClientId, ["redirect_uri"] = Origin() + "/copilot-access/callback",
            ["state"] = state, ["scope"] = "offline_access", ["allow_signup"] = "false",
            ["prompt"] = "select_account",
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256"
        });
    }

    public async Task CompleteAuthorizationAsync(ApiCaller caller, string state, string code, CancellationToken cancellationToken)
    {
        var flow = Unprotect(caller, state, "user");
        var stored = await RequiredAsync(caller.Principal, cancellationToken);
        var record = Records.Require<CopilotAccessRecord>(stored.Record);
        if (record.Attempt != flow.Attempt || record.State != "authorizing")
            throw ApiProblems.Conflict();
        var app = await secrets.ReadAppAsync(record.Id, record.AppRegistrationId, cancellationToken);
        MatchApp(record, app);
        await ReplaceAsync(stored, record with { State = "exchangingUser", UpdatedAt = clock.GetUtcNow() }, cancellationToken);
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var authorization = await github.ExchangeAsync(app, code, flow.Verifier, Origin() + "/copilot-access/callback", completion.Token);
        await secrets.SaveAuthorizationAsync(record.Id, record.Attempt, authorization, completion.Token);
        await RecoverAsync(caller, completion.Token);
    }

    public async Task RecoverAsync(ApiCaller caller, CancellationToken cancellationToken)
    {
        var stored = await RequiredAsync(caller.Principal, cancellationToken);
        var record = Records.Require<CopilotAccessRecord>(stored.Record);
        CopilotAccessRecord recovered;
        if (record.State == "exchangingApp")
        {
            var registration = await secrets.ReadAppAsync(record.Id, record.Attempt, cancellationToken);
            recovered = record with { State = "needsAuthorization", AppId = registration.AppId, Slug = registration.Slug,
                AppRegistrationId = record.Attempt };
        }
        else if (record.State is "exchangingUser" or "refreshing")
        {
            var authorization = await secrets.ReadAuthorizationAsync(record.Id, record.Attempt, cancellationToken);
            if (record.State == "refreshing" && authorization.UserId != record.GitHubUserId)
                throw new ApiException(403, "copilot_identity_changed", "Recovered token belongs to a different GitHub account.");
            _ = Credential("recovery", authorization);
            recovered = record with
            {
                State = "connected", CredentialId = record.Attempt, GitHubUserId = authorization.UserId,
                GitHubLogin = authorization.Login, AccessExpiresAt = authorization.ExpiresAt,
                RefreshExpiresAt = authorization.RefreshExpiresAt
            };
        }
        else
            throw ApiProblems.Invalid("Only an interrupted app, user-token exchange or refresh can be recovered.");
        await ReplaceAsync(stored, recovered with { UpdatedAt = clock.GetUtcNow() }, cancellationToken);
    }

    public async Task DisableAsync(ApiCaller caller, CancellationToken cancellationToken)
    {
        var stored = await RequiredAsync(caller.Principal, cancellationToken);
        var record = Records.Require<CopilotAccessRecord>(stored.Record);
        await ReplaceAsync(stored, record with { State = "disabled", UpdatedAt = clock.GetUtcNow() }, cancellationToken);
    }

    public async Task RequireAsync(EntraPrincipal owner, CancellationToken cancellationToken)
    {
        var record = Records.Require<CopilotAccessRecord>((await RequiredAsync(owner, cancellationToken)).Record);
        if (record.State is not ("connected" or "refreshing") ||
            record.RefreshExpiresAt <= clock.GetUtcNow() || record.RefreshExpiresAt is null)
            throw Reauthorize();
        if (record.State == "refreshing" && record.UpdatedAt.AddMinutes(2) <= clock.GetUtcNow())
            throw RefreshPending();
    }

    public async Task<ModelAccessCredential> AcquireAsync(EntraPrincipal owner, string model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 128 || model.Any(char.IsControl))
            throw ApiProblems.Invalid("Specify a model name of at most 128 characters.");
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await RequireAsync(owner, cancellationToken);
            var stored = await RequiredAsync(owner, cancellationToken);
            var record = Records.Require<CopilotAccessRecord>(stored.Record);
            if (record.State == "refreshing")
            {
                if (record.UpdatedAt.AddMinutes(2) <= clock.GetUtcNow())
                    break;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }
            if (record.State != "connected")
                throw Reauthorize();
            var authorization = await secrets.ReadAuthorizationAsync(record.Id, record.CredentialId, cancellationToken);
            if (authorization.UserId != record.GitHubUserId)
                throw new ApiException(403, "copilot_identity_changed", "Saved model authorization does not match the authorized GitHub account.");
            if (authorization.ExpiresAt <= clock.GetUtcNow().AddMinutes(70))
            {
                if (authorization.RefreshExpiresAt <= clock.GetUtcNow())
                    throw Reauthorize();
                var app = await secrets.ReadAppAsync(record.Id, record.AppRegistrationId, cancellationToken);
                MatchApp(record, app);
                var refreshing = record with { State = "refreshing", Attempt = Guid.NewGuid().ToString("D"), UpdatedAt = clock.GetUtcNow() };
                if (!await TryReplaceAsync(stored, refreshing, cancellationToken))
                    continue;
                using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                authorization = await github.RefreshAsync(app, authorization, completion.Token);
                await secrets.SaveAuthorizationAsync(record.Id, refreshing.Attempt, authorization, completion.Token);
                await RecoverAsync(new(owner, ""), completion.Token);
                continue;
            }
            var latest = await RequiredAsync(owner, cancellationToken);
            if (latest.ETag != stored.ETag)
                continue;
            return Credential(model, authorization);
        }
        throw RefreshPending();
    }

    private ModelAccessCredential Credential(string model, CopilotUserAuthorization authorization)
    {
        var credential = new ModelAccessCredential("github-copilot", model, authorization.AccessToken, authorization.ExpiresAt);
        credential.Validate(model, clock.GetUtcNow());
        return credential;
    }

    private async Task ReplaceAsync(StoredRecord? previous, CopilotAccessRecord record, CancellationToken cancellationToken)
    {
        if (!await TryReplaceAsync(previous, record, cancellationToken))
            throw ApiProblems.Conflict();
    }

    private Task<bool> TryReplaceAsync(StoredRecord? previous, CopilotAccessRecord record, CancellationToken cancellationToken) =>
        store.CommitAsync(record.ScopeId,
        [
            new(record, previous?.ETag),
            new(new AuditRecord { Id = Guid.NewGuid().ToString("D"), ScopeId = record.ScopeId, Principal = record.Principal,
                Action = "copilotAccess." + record.State, RecordedAt = clock.GetUtcNow() })
        ], cancellationToken);

    private string Protect(CopilotAccessRecord record, string kind, string verifier, DateTimeOffset expiresAt) =>
        stateProtection.Protect(JsonSerializer.Serialize(new FlowState(record.Principal, record.Attempt, kind, expiresAt, verifier)));
    private FlowState Unprotect(ApiCaller caller, string state, string kind)
    {
        FlowState flow;
        try { flow = JsonSerializer.Deserialize<FlowState>(stateProtection.Unprotect(state)) ?? throw new JsonException(); }
        catch (Exception error) when (error is CryptographicException or JsonException or ArgumentException)
        {
            throw ApiProblems.Invalid("GitHub Copilot authorization state is invalid. Return to Model access.");
        }
        if (flow.Principal != caller.Principal || flow.Kind != kind || flow.ExpiresAt <= clock.GetUtcNow())
            throw ApiProblems.Forbidden();
        return flow;
    }
    private static void MatchApp(CopilotAccessRecord record, CopilotAppRegistration app)
    {
        if (record.AppId != app.AppId || record.Slug != app.Slug)
            throw new InvalidOperationException("The saved Copilot app does not match its registration.");
    }
    private static ApiException Reauthorize() => new(409, "copilot_reauthorization_required",
        "The production-line owner's GitHub Copilot authorization is missing, disabled or expired. Connect through Model access.");
    private static ApiException RefreshPending() => new(503, "copilot_refresh_pending",
        "Token refresh is still owned by another request or was interrupted. Recover saved authorization; do not replay refresh.");
}
