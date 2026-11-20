using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Factory.Contracts;
using Factory.Plant.Service.Storage;
using Factory.Plant.Service.Web;
using Microsoft.AspNetCore.DataProtection;

namespace Factory.Plant.Service;

public sealed record GitHubManifestPage(string Action, string Manifest, string Reference);

public sealed class ManagedGitHubConnections(IRecordStore store, IGitHubConnectionSecrets secrets,
    IGitHubOnboardingClient github, IDataProtectionProvider protection, WebsiteSettings website, TimeProvider clock)
{
    private readonly IDataProtector stateProtection = protection.CreateProtector("Factory.GitHub.Manifest.v1");
    private sealed record CallbackState(string Reference, EntraPrincipal Principal, DateTimeOffset ExpiresAt);
    public static bool IsReference(string reference) => Regex.IsMatch(reference, "^github-[a-f0-9]{32}$");
    private static string Scope(string reference) => $"github-connection:{reference}";

    public async Task<GitHubConnectionRecord[]> ListAsync(ApiCaller caller, CancellationToken cancellationToken)
    {
        var records = await store.QueryAsync(new("githubConnection", 101, Principal: caller.Principal), cancellationToken);
        if (records.Count > 100)
            throw new ApiException(409, "github_connection_limit", "This owner has more than 100 connections; connection pagination is required.");
        var connections = records.Select(Records.Require<GitHubConnectionRecord>).ToArray();
        if (connections.Any(connection => connection.Principal != caller.Principal))
            throw new InvalidOperationException("The stored connection ownership is inconsistent.");
        return connections;
    }

    public async Task<GitHubConnectionRecord> GetAsync(string reference, ApiCaller caller, CancellationToken cancellationToken)
    {
        var stored = await ReadAsync(reference, cancellationToken);
        var record = Records.Require<GitHubConnectionRecord>(stored.Record);
        if (record.Principal != caller.Principal)
            throw ApiProblems.Forbidden();
        return record;
    }

    public async Task<GitHubManifestPage> BeginAsync(ApiCaller caller, string operation, string name, string? organization,
        bool publication, CancellationToken cancellationToken)
    {
        secrets.RequireConfigured();
        var origin = PublicOrigin();
        if (!Guid.TryParseExact(operation, "D", out var id) || id == Guid.Empty ||
            string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Any(char.IsControl))
            throw ApiProblems.Invalid("Supply a connection name (at most 64 characters) and reload before submitting.");
        organization = organization?.Trim() ?? "";
        if (organization.Length != 0 && !Regex.IsMatch(organization, "^[a-zA-Z0-9][a-zA-Z0-9-]{0,38}$"))
            throw ApiProblems.Invalid("Organization must be a GitHub organization login, or blank for your personal account.");
        var reference = "github-" + id.ToString("N");
        var record = new GitHubConnectionRecord
        {
            Id = reference,
            ScopeId = Scope(reference),
            Principal = caller.Principal,
            Name = name.Trim(),
            Organization = organization,
            State = "pending",
            AllowPublication = publication,
            CreatedAt = clock.GetUtcNow()
        };
        if (!await store.CommitAsync(record.ScopeId, [new(record)], cancellationToken))
        {
            record = await GetAsync(reference, caller, cancellationToken);
            if (record.Name != name.Trim() || record.Organization != organization || record.AllowPublication != publication ||
                record.State != "pending")
                throw ApiProblems.Conflict();
        }
        if (clock.GetUtcNow() > record.CreatedAt.AddHours(1))
            throw ApiProblems.Invalid("This setup expired. Start a new connection.");
        await secrets.CheckAccessAsync(reference, cancellationToken);
        var state = stateProtection.Protect(JsonSerializer.Serialize(new CallbackState(reference, caller.Principal, record.CreatedAt.AddHours(1))));
        var manifest = JsonSerializer.Serialize(new
        {
            name = "Factory-" + id.ToString("N")[..26],
            description = "Factory repository connection: " + record.Name,
            url = origin,
            @public = false,
            redirect_url = origin + "/repository-connections/manifest",
            setup_url = origin + "/repository-connections/" + reference,
            hook_attributes = new { url = origin + "/repository-connections/events", active = false },
            request_oauth_on_install = false,
            setup_on_update = true,
            default_permissions = new Dictionary<string, string>
            {
                ["contents"] = publication ? "write" : "read",
                ["issues"] = "read",
                ["pull_requests"] = publication ? "write" : "read"
            },
            default_events = Array.Empty<string>()
        });
        var target = organization.Length == 0 ? "https://github.com/settings/apps/new"
            : $"https://github.com/organizations/{organization}/settings/apps/new";
        return new(target + "?state=" + Uri.EscapeDataString(state), manifest, reference);
    }

    public async Task<string> ManifestAsync(ApiCaller caller, string state, string code, CancellationToken cancellationToken)
    {
        CallbackState callback;
        try { callback = JsonSerializer.Deserialize<CallbackState>(stateProtection.Unprotect(state)) ?? throw new JsonException(); }
        catch (Exception error) when (error is CryptographicException or JsonException or ArgumentException)
        {
            throw ApiProblems.Invalid("GitHub setup state is invalid. Return to repository connections.");
        }
        if (callback.Principal != caller.Principal || callback.ExpiresAt <= clock.GetUtcNow())
            throw ApiProblems.Forbidden();
        await ExchangeAsync(callback.Reference, caller, code, cancellationToken);
        return callback.Reference;
    }

    private async Task ExchangeAsync(string reference, ApiCaller caller, string code, CancellationToken cancellationToken)
    {
        var record = await GetAsync(reference, caller, cancellationToken);
        if (record.State is "installing" or "connected")
            return;
        if (record.State != "pending")
            throw new ApiException(409, "github_setup_incomplete", "App creation was already attempted. Use Recover saved registration; do not exchange the code again.");
        var stored = await ReadAsync(reference, cancellationToken);
        record = Records.Require<GitHubConnectionRecord>(stored.Record);
        if (record.State != "pending" || !await store.CommitAsync(record.ScopeId,
            [new(record with { State = "exchanging" }, stored.ETag)], cancellationToken))
            throw ApiProblems.Conflict();
        // Once the one-use exchange starts, preserve its key even if the browser disconnects.
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var registration = await github.ConvertAsync(code, completion.Token);
        if (record.Organization.Length != 0 && !string.Equals(record.Organization, registration.Account, StringComparison.OrdinalIgnoreCase))
            throw new ApiException(409, "github_owner_changed", "GitHub app ownership does not match the selected organization. Setup was not enabled.");
        await secrets.SaveAsync(reference, registration, completion.Token);
        await RecoverAsync(reference, caller, completion.Token);
    }

    public async Task RecoverAsync(string reference, ApiCaller caller, CancellationToken cancellationToken)
    {
        var record = await GetAsync(reference, caller, cancellationToken);
        if (record.State != "exchanging")
            throw ApiProblems.Invalid("Only an interrupted registration exchange can be recovered.");
        var registration = await secrets.ReadAsync(reference, cancellationToken);
        if (record.Organization.Length != 0 && !string.Equals(record.Organization, registration.Account, StringComparison.OrdinalIgnoreCase))
            throw ApiProblems.Forbidden();
        var stored = await ReadAsync(reference, cancellationToken);
        record = Records.Require<GitHubConnectionRecord>(stored.Record);
        if (record.State != "exchanging" || !await store.CommitAsync(record.ScopeId, [new(record with
        {
            State = "installing", AppId = registration.AppId, Slug = registration.Slug, Account = registration.Account
        }, stored.ETag)], cancellationToken))
            throw ApiProblems.Conflict();
    }

    public async Task VerifyAsync(string reference, ApiCaller caller, CancellationToken cancellationToken)
    {
        var record = await GetAsync(reference, caller, cancellationToken);
        if (record.State is not ("installing" or "connected"))
            throw ApiProblems.Invalid("Complete GitHub App registration before verifying repository access.");
        var stored = await ReadAsync(reference, cancellationToken);
        record = Records.Require<GitHubConnectionRecord>(stored.Record);
        if (record.State is not ("installing" or "connected"))
            throw ApiProblems.Conflict();
        var registration = await secrets.ReadAsync(reference, cancellationToken);
        if (registration.AppId != record.AppId || registration.Account != record.Account || registration.Slug != record.Slug)
            throw new InvalidOperationException("The saved registration does not match the connection.");
        // Discover the installation from app authority; never trust a callback's installation_id.
        var installation = await github.VerifyAsync(registration, record.AllowPublication, cancellationToken);
        if (!await store.CommitAsync(record.ScopeId, [new(record with
        {
            State = "connected", InstallationId = installation.InstallationId, Repositories = installation.Repositories
        }, stored.ETag),
            new(new AuditRecord { Id = Guid.NewGuid().ToString("D"), ScopeId = record.ScopeId, Principal = caller.Principal,
                Action = "githubConnection.verified", RecordedAt = clock.GetUtcNow() })], cancellationToken))
            throw ApiProblems.Conflict();
    }

    public async Task DisableAsync(string reference, ApiCaller caller, CancellationToken cancellationToken)
    {
        await GetAsync(reference, caller, cancellationToken);
        var stored = await ReadAsync(reference, cancellationToken);
        var record = Records.Require<GitHubConnectionRecord>(stored.Record);
        if (!await store.CommitAsync(record.ScopeId, [new(record with { State = "disabled" }, stored.ETag),
            new(new AuditRecord { Id = Guid.NewGuid().ToString("D"), ScopeId = record.ScopeId, Principal = caller.Principal,
                Action = "githubConnection.disabled", RecordedAt = clock.GetUtcNow() })], cancellationToken))
            throw ApiProblems.Conflict();
    }

    public async Task RequireReferenceAsync(string reference, string repository, ApiCaller caller, CancellationToken cancellationToken,
        bool publication = false)
    {
        if (!IsReference(reference))
            return;
        var record = await GetAsync(reference, caller, cancellationToken);
        RequireConnected(record, repository);
        if (publication && !record.AllowPublication)
            throw new ApiException(403, "repository_write_disabled", "Publication is disabled for this managed connection.");
    }

    public async Task<GitHubAppConnection> ResolveAsync(string reference, string repository, CancellationToken cancellationToken)
    {
        var record = Records.Require<GitHubConnectionRecord>((await ReadAsync(reference, cancellationToken)).Record);
        RequireConnected(record, repository);
        var registration = await secrets.ReadAsync(reference, cancellationToken);
        if (registration.AppId != record.AppId || registration.Account != record.Account || registration.Slug != record.Slug)
            throw new InvalidOperationException("The saved registration does not match the connection.");
        return new()
        {
            AppId = record.AppId,
            InstallationId = record.InstallationId,
            Repositories = record.Repositories,
            PrivateKeyPem = registration.PrivateKeyPem,
            AllowPublication = record.AllowPublication
        };
    }

    private static void RequireConnected(GitHubConnectionRecord record, string repository)
    {
        if (record.State != "connected" || !record.Repositories.Contains(repository, StringComparer.OrdinalIgnoreCase))
            throw new ApiException(403, "repository_not_permitted", "The managed GitHub connection is disabled, unverified or does not include this repository.");
    }

    private async Task<StoredRecord> ReadAsync(string reference, CancellationToken cancellationToken)
    {
        if (!IsReference(reference))
            throw ApiProblems.Invalid("Invalid managed GitHub connection reference.");
        return await store.ReadAsync(Scope(reference), reference, cancellationToken)
            ?? throw new ApiException(404, "github_connection_missing", "The repository connection does not exist.");
    }

    private string PublicOrigin() => Uri.TryCreate(website.PublicOrigin, UriKind.Absolute, out var origin) &&
        origin.Scheme == "https" && origin.AbsolutePath == "/" && origin.UserInfo.Length == 0 &&
        origin.Query.Length == 0 && origin.Fragment.Length == 0 ? website.PublicOrigin.TrimEnd('/')
            : throw new ApiException(503, "github_origin_unconfigured", "Configure the website public HTTPS origin before GitHub setup.");
}
