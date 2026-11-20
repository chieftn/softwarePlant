using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Factory.Plant.Service;

public interface IGitHubCopilotOAuth
{
    Task<CopilotAppRegistration> RegisterAsync(string code, CancellationToken cancellationToken);
    Task<CopilotUserAuthorization> ExchangeAsync(CopilotAppRegistration app, string code, string verifier,
        string callback, CancellationToken cancellationToken);
    Task<CopilotUserAuthorization> RefreshAsync(CopilotAppRegistration app, CopilotUserAuthorization previous,
        CancellationToken cancellationToken);
}

public sealed class GitHubCopilotOAuth : IGitHubCopilotOAuth, IDisposable
{
    private readonly HttpClient http;
    private readonly TimeProvider clock;
    public GitHubCopilotOAuth(TimeProvider clock) : this(clock, new HttpClientHandler { AllowAutoRedirect = false }) { }
    internal GitHubCopilotOAuth(TimeProvider clock, HttpMessageHandler handler)
    {
        this.clock = clock;
        http = new(handler) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 64 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Factory.Plant.Service/1");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<CopilotAppRegistration> RegisterAsync(string code, CancellationToken cancellationToken)
    {
        Code(code);
        var result = await SendAsync(HttpMethod.Post, $"https://api.github.com/app-manifests/{code}/conversions",
            null, null, cancellationToken);
        try
        {
            var registration = new CopilotAppRegistration(result.GetProperty("id").GetInt64(),
                result.GetProperty("slug").GetString()!, result.GetProperty("client_id").GetString()!,
                result.GetProperty("client_secret").GetString()!);
            if (registration.AppId <= 0 || string.IsNullOrWhiteSpace(registration.Slug) ||
                !Regex.IsMatch(registration.Slug, "^[a-zA-Z0-9-]{1,100}$") ||
                string.IsNullOrWhiteSpace(registration.ClientId) || string.IsNullOrWhiteSpace(registration.ClientSecret))
                throw new JsonException();
            return registration;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw InvalidResponse();
        }
    }

    public Task<CopilotUserAuthorization> ExchangeAsync(CopilotAppRegistration app, string code, string verifier,
        string callback, CancellationToken cancellationToken)
    {
        Code(code);
        return TokenAsync(new { client_id = app.ClientId, client_secret = app.ClientSecret, code,
            code_verifier = verifier, redirect_uri = callback }, cancellationToken);
    }

    public async Task<CopilotUserAuthorization> RefreshAsync(CopilotAppRegistration app, CopilotUserAuthorization previous,
        CancellationToken cancellationToken)
    {
        var refreshed = await TokenAsync(new { client_id = app.ClientId, client_secret = app.ClientSecret,
            grant_type = "refresh_token", refresh_token = previous.RefreshToken }, cancellationToken);
        if (refreshed.UserId != previous.UserId)
            throw new ApiException(403, "copilot_identity_changed", "GitHub token refresh changed the authorized account. Authorize again explicitly.");
        return refreshed;
    }

    private async Task<CopilotUserAuthorization> TokenAsync(object body, CancellationToken cancellationToken)
    {
        var issuedAt = clock.GetUtcNow();
        var result = await SendAsync(HttpMethod.Post, "https://github.com/login/oauth/access_token", null, body, cancellationToken);
        try
        {
            if (result.TryGetProperty("error", out _))
                throw new ApiException(409, "copilot_reauthorization_required",
                    "GitHub did not issue a renewable user authorization. Return to Model access and reconnect GitHub Copilot.");
            var access = result.GetProperty("access_token").GetString()!;
            var refresh = result.GetProperty("refresh_token").GetString()!;
            var lifetime = result.GetProperty("expires_in").GetInt32();
            var refreshLifetime = result.GetProperty("refresh_token_expires_in").GetInt32();
            if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh) ||
                !access.StartsWith("ghu_", StringComparison.Ordinal) || !refresh.StartsWith("ghr_", StringComparison.Ordinal) ||
                access.Length > 1024 || refresh.Length > 1024 || access.Any(char.IsWhiteSpace) || refresh.Any(char.IsWhiteSpace) ||
                lifetime is < 7200 or > 28800 || refreshLifetime <= lifetime || refreshLifetime > 366 * 24 * 60 * 60 ||
                !string.Equals(result.GetProperty("token_type").GetString(), "bearer", StringComparison.OrdinalIgnoreCase))
                throw new JsonException();
            var user = await SendAsync(HttpMethod.Get, "https://api.github.com/user", access, null, cancellationToken);
            var id = user.GetProperty("id").GetInt64();
            var login = user.GetProperty("login").GetString()!;
            if (id <= 0 || string.IsNullOrWhiteSpace(login) || !Regex.IsMatch(login, "^[a-zA-Z0-9-]{1,39}$"))
                throw new JsonException();
            return new(access, issuedAt.AddSeconds(lifetime), refresh, issuedAt.AddSeconds(refreshLifetime), id, login);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentNullException)
        {
            throw InvalidResponse();
        }
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string url, string? token, object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, cancellationToken); }
        catch (HttpRequestException)
        {
            throw new ApiException(502, "copilot_oauth_failed", "GitHub authorization could not be contacted. Reconcile any interrupted token exchange before retrying.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ApiException(504, "copilot_oauth_timeout", "GitHub authorization exceeded its deadline. Recover saved authorization or explicitly authorize again.");
        }
        using var completed = response;
        if (!response.IsSuccessStatusCode)
            throw new ApiException(502, "copilot_oauth_failed", $"GitHub authorization returned HTTP {(int)response.StatusCode}; no model access was granted.");
        try { return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken); }
        catch (JsonException) { throw InvalidResponse(); }
    }

    private static void Code(string code)
    {
        if (!Regex.IsMatch(code, "^[a-zA-Z0-9_-]{1,256}$"))
            throw ApiProblems.Invalid("GitHub supplied an invalid authorization code.");
    }
    private static ApiException InvalidResponse() => new(502, "copilot_oauth_response_invalid",
        "GitHub returned an invalid or nonrenewable authorization response. No model access was enabled.");
    public void Dispose() => http.Dispose();
}
