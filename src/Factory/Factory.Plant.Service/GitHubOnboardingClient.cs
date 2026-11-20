using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Factory.Plant.Service;

public sealed record GitHubInstallation(long InstallationId, string[] Repositories);

public interface IGitHubOnboardingClient
{
    Task<GitHubAppRegistration> ConvertAsync(string code, CancellationToken cancellationToken);
    Task<GitHubInstallation> VerifyAsync(GitHubAppRegistration registration, bool publication, CancellationToken cancellationToken);
}

public sealed class GitHubOnboardingClient : IGitHubOnboardingClient, IDisposable
{
    private readonly HttpClient http;
    private readonly TimeProvider clock;
    public GitHubOnboardingClient(TimeProvider clock) : this(clock, new HttpClientHandler { AllowAutoRedirect = false }) { }
    internal GitHubOnboardingClient(TimeProvider clock, HttpMessageHandler handler)
    {
        this.clock = clock;
        http = new(handler)
        {
            BaseAddress = new("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 1024 * 1024
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Factory.Plant.Service/1");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<GitHubAppRegistration> ConvertAsync(string code, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(code, "^[a-zA-Z0-9_-]{1,256}$"))
            throw ApiProblems.Invalid("GitHub supplied an invalid manifest code.");
        var result = await SendAsync(HttpMethod.Post, $"app-manifests/{code}/conversions", null, null, cancellationToken);
        var registration = new GitHubAppRegistration(result.GetProperty("id").GetInt64(),
            result.GetProperty("slug").GetString()!, result.GetProperty("owner").GetProperty("login").GetString()!,
            result.GetProperty("pem").GetString()!);
        if (registration.AppId <= 0 || !Regex.IsMatch(registration.Slug, "^[a-zA-Z0-9-]{1,100}$") ||
            !Regex.IsMatch(registration.Account, "^[a-zA-Z0-9-]{1,39}$"))
            throw new ApiException(502, "github_response_invalid", "GitHub returned an invalid app identity.");
        return registration;
    }

    public async Task<GitHubInstallation> VerifyAsync(GitHubAppRegistration registration, bool publication, CancellationToken cancellationToken)
    {
        var jwt = GitHubRepositoryReader.Jwt(new() { AppId = registration.AppId, PrivateKeyPem = registration.PrivateKeyPem }, clock.GetUtcNow());
        var app = await SendAsync(HttpMethod.Get, "app", jwt, null, cancellationToken);
        if (app.GetProperty("id").GetInt64() != registration.AppId || app.GetProperty("slug").GetString() != registration.Slug ||
            !string.Equals(app.GetProperty("owner").GetProperty("login").GetString(), registration.Account, StringComparison.OrdinalIgnoreCase))
            throw new ApiException(409, "github_app_changed", "Expected the GitHub App created by this setup.");
        var installs = await SendAsync(HttpMethod.Get, "app/installations?per_page=100", jwt, null, cancellationToken);
        var matches = installs.EnumerateArray().Where(item => string.Equals(
            item.GetProperty("account").GetProperty("login").GetString(), registration.Account, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || installs.GetArrayLength() >= 100)
            throw new ApiException(409, "github_installation_missing", "Install this private app on its owning account, then verify again.");
        var installation = matches[0];
        if (installation.GetProperty("repository_selection").GetString() != "selected" ||
            installation.GetProperty("suspended_at").ValueKind != JsonValueKind.Null)
            throw new ApiException(409, "github_installation_invalid", "Choose Only select repositories and ensure the installation is not suspended.");
        var permissions = installation.GetProperty("permissions");
        bool Allows(string permission, string needed) => permissions.TryGetProperty(permission, out var value) &&
            (value.GetString() == needed || needed == "read" && value.GetString() == "write");
        if (!Allows("contents", publication ? "write" : "read") || !Allows("issues", "read") ||
            !Allows("pull_requests", publication ? "write" : "read"))
            throw new ApiException(409, "github_permissions_missing", "The installation does not have the requested repository permissions.");
        var id = installation.GetProperty("id").GetInt64();
        var token = await SendAsync(HttpMethod.Post, $"app/installations/{id}/access_tokens", jwt, new
        {
            permissions = new Dictionary<string, string> { ["contents"] = "read", ["issues"] = "read", ["pull_requests"] = "read" }
        }, cancellationToken);
        var repositories = new List<string>();
        for (var page = 1; page <= 10; page++)
        {
            var response = await SendAsync(HttpMethod.Get, $"installation/repositories?per_page=100&page={page}",
                token.GetProperty("token").GetString(), null, cancellationToken);
            var items = response.GetProperty("repositories").EnumerateArray().ToArray();
            repositories.AddRange(items.Select(item => item.GetProperty("full_name").GetString()!));
            if (items.Length == 100)
                continue;
            var connection = new GitHubAppConnection
            {
                AppId = registration.AppId,
                InstallationId = id,
                PrivateKeyPem = registration.PrivateKeyPem,
                Repositories = repositories.ToArray(),
                AllowPublication = publication
            };
            connection.Validate();
            if (repositories.Any(repository => !string.Equals(repository.Split('/')[0], registration.Account, StringComparison.OrdinalIgnoreCase)))
                throw new ApiException(409, "github_installation_invalid", "Repository ownership does not match the app owner.");
            return new(id, repositories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
        throw new ApiException(409, "github_repository_limit", "This installation exceeds the bounded repository listing. Select fewer repositories.");
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, string? token, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, cancellationToken); }
        catch (HttpRequestException)
        {
            throw new ApiException(502, "github_onboarding_failed", "GitHub setup could not be contacted. Reconcile any interrupted registration before retrying.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ApiException(504, "github_onboarding_timeout", "GitHub setup exceeded its request deadline. Reconcile any interrupted registration before retrying.");
        }
        using var completedResponse = response;
        if (!response.IsSuccessStatusCode)
            throw new ApiException(502, "github_onboarding_failed",
                $"GitHub setup returned HTTP {(int)response.StatusCode}. Check the app installation; no connection was enabled.");
        try { return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken); }
        catch (JsonException)
        {
            throw new ApiException(502, "github_response_invalid", "GitHub returned invalid setup JSON.");
        }
    }

    public void Dispose() => http.Dispose();
}
