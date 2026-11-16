using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Contracts;

namespace Factory.Plant.Service;

public sealed class GitHubConnectionsSettings
{
    public Dictionary<string, GitHubAppConnection> Connections { get; set; } = new(StringComparer.Ordinal);
}

public sealed class GitHubAppConnection
{
    public long AppId { get; set; }
    public long InstallationId { get; set; }
    public string[] Repositories { get; set; } = [];
    public string PrivateKeyPem { get; set; } = "";
    public bool AllowPublication { get; set; }

    public void Validate()
    {
        if (AppId <= 0 || InstallationId <= 0 || Repositories.Length == 0 ||
            Repositories.Any(repository => repository.Split('/') is not [_, _]) ||
            Repositories.Select(repository => repository.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            throw new ApiException(503, "repository_connection_unconfigured",
                "Configure one GitHub App installation and explicit repository allowlist for this connection.");
        foreach (var repository in Repositories)
        {
            if (repository.Split('/').Any(part => part.Length == 0 || part is "." or ".." ||
                !part.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
                throw new ApiException(503, "repository_connection_unconfigured", "GitHub connection repository allowlist is invalid.");
        }
        if (string.IsNullOrWhiteSpace(PrivateKeyPem) || PrivateKeyPem.StartsWith("@Microsoft.KeyVault(", StringComparison.Ordinal))
            throw new ApiException(503, "repository_connection_unconfigured",
                "The GitHub App private key must be provisioned through a resolved service secret reference.");
        try
        {
            using var key = RSA.Create();
            key.ImportFromPem(PrivateKeyPem);
            if (key.KeySize < 2048 || key.ExportParameters(true).D is null)
                throw new CryptographicException();
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException)
        {
            throw new ApiException(503, "repository_connection_unconfigured", "The GitHub App private key is invalid.");
        }
    }
}

public interface IGitHubRepositoryReader
{
    Task<ResolvedRepositoryWorkload> ResolveAsync(WorkbenchLineConfiguration configuration, CancellationToken cancellationToken);
}

public sealed partial class GitHubRepositoryReader : IGitHubRepositoryReader, IGitHubDevelopmentRepository, IDisposable
{
    private readonly GitHubConnectionsSettings settings;
    private readonly TimeProvider clock;
    private readonly HttpClient http;

    public GitHubRepositoryReader(GitHubConnectionsSettings settings, TimeProvider clock)
        : this(settings, clock, new HttpClientHandler { AllowAutoRedirect = false }) { }

    internal GitHubRepositoryReader(GitHubConnectionsSettings settings, TimeProvider clock, HttpMessageHandler handler)
    {
        this.settings = settings;
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

    public async Task<ResolvedRepositoryWorkload> ResolveAsync(WorkbenchLineConfiguration configuration, CancellationToken cancellationToken)
    {
        var source = configuration.ProductionLine?.Definition.Source
            ?? throw new ApiException(409, "configuration_changed", "No production line is assigned.");
        var access = await InstallationTokenAsync(source.ConnectionReference, source.Repository,
            new() { ["contents"] = "read" }, cancellationToken);
        var token = access.Token;
        var repositoryPath = $"repos/{source.Repository}/";
        var commitResponse = await SendAsync(HttpMethod.Get,
            repositoryPath + "commits/" + Uri.EscapeDataString(source.BaseBranch), token, null, cancellationToken);
        var commit = commitResponse.GetProperty("sha").GetString()!;
        RepositoryWorkload.Commit(commit);
        var rootTree = commitResponse.GetProperty("commit").GetProperty("tree").GetProperty("sha").GetString()!;
        var trees = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var workloadTree = await WalkTree(rootTree, source.WorkloadPath);
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workload.json"] = await ReadFile("workload.json"),
            ["workload.schema.json"] = await ReadFile("workload.schema.json")
        };
        try
        {
            var workload = RepositoryWorkload.Parse(files["workload.json"]);
            files.Add(workload.Agents.Implementation.Instructions, await ReadFile(workload.Agents.Implementation.Instructions));
            files.Add(workload.Agents.Alignment.Instructions, await ReadFile(workload.Agents.Alignment.Instructions));
            var resolved = ResolvedRepositoryWorkload.Create(configuration, commit, files);
            resolved.Validate();
            return resolved;
        }
        catch (Exception error) when (error is ContractValidationException or JsonException or ArgumentException)
        {
            throw new ApiException(409, "workload_invalid", "The pinned repository workload is invalid or unsupported. Validate its v1 manifest and instruction files.");
        }

        async Task<JsonElement> Tree(string sha)
        {
            RepositoryWorkload.Commit(sha);
            if (!trees.TryGetValue(sha, out var tree))
            {
                tree = await SendAsync(HttpMethod.Get, repositoryPath + "git/trees/" + sha, token, null, cancellationToken);
                if (tree.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean())
                    throw new ApiException(409, "workload_invalid", "The GitHub tree response is truncated; workload resolution cannot guess missing files.");
                trees.Add(sha, tree);
            }
            return tree;
        }

        async Task<string> WalkTree(string sha, string path)
        {
            foreach (var component in path.Split('/'))
            {
                var entry = Entry(await Tree(sha), component);
                if (entry.GetProperty("type").GetString() != "tree" || entry.GetProperty("mode").GetString() != "040000")
                    throw new ApiException(409, "workload_invalid", "Workload folders must be Git trees, not links or submodules.");
                sha = entry.GetProperty("sha").GetString()!;
            }
            return sha;
        }

        async Task<string> ReadFile(string path)
        {
            var slash = path.LastIndexOf('/');
            var tree = slash < 0 ? workloadTree : await WalkTree(workloadTree, path[..slash]);
            var entry = Entry(await Tree(tree), path[(slash + 1)..]);
            if (entry.GetProperty("type").GetString() != "blob" ||
                entry.GetProperty("mode").GetString() is not ("100644" or "100755"))
                throw new ApiException(409, "workload_invalid", "Workload files must be regular Git blobs; links and submodules are unsupported.");
            var blob = await SendAsync(HttpMethod.Get, repositoryPath + "git/blobs/" +
                entry.GetProperty("sha").GetString(), token, null, cancellationToken);
            var size = blob.GetProperty("size").GetInt32();
            if (size is <= 0 or > 16 * 1024 || blob.GetProperty("encoding").GetString() != "base64")
                throw new ApiException(409, "workload_invalid", "Workload file is empty, too large or uses an unsupported encoding.");
            try
            {
                var bytes = Convert.FromBase64String(blob.GetProperty("content").GetString()!);
                if (bytes.Length != size)
                    throw new FormatException();
                return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            }
            catch (Exception error) when (error is FormatException or DecoderFallbackException)
            {
                throw new ApiException(409, "workload_invalid", "Workload file is not a valid bounded UTF-8 blob.");
            }
        }
    }

    private static JsonElement Entry(JsonElement tree, string name)
    {
        var matches = tree.GetProperty("tree").EnumerateArray()
            .Where(entry => entry.GetProperty("path").GetString() == name).ToArray();
        if (matches.Length != 1)
            throw new ApiException(409, "workload_missing", "The configured repository revision does not contain the complete workload path.");
        return matches[0];
    }

    private async Task<RepositoryAccessCredential> InstallationTokenAsync(string reference, string repository,
        Dictionary<string, string> permissions, CancellationToken cancellationToken)
    {
        if (!settings.Connections.TryGetValue(reference, out var connection))
            throw new ApiException(503, "repository_connection_unconfigured", "The assigned repository connection is not configured.");
        connection.Validate();
        if (!connection.Repositories.Contains(repository, StringComparer.OrdinalIgnoreCase))
            throw new ApiException(403, "repository_not_permitted", "The connection does not authorize this repository.");
        if (permissions.Values.Contains("write") && !connection.AllowPublication)
            throw new ApiException(403, "repository_write_disabled", "Publication is disabled for the source connection.");
        var now = clock.GetUtcNow();
        var jwt = Jwt(connection, now);
        var installation = await SendAsync(HttpMethod.Get, $"app/installations/{connection.InstallationId}", jwt, null, cancellationToken);
        if (!string.Equals(installation.GetProperty("account").GetProperty("login").GetString(),
            repository.Split('/')[0], StringComparison.OrdinalIgnoreCase))
            throw new ApiException(403, "repository_not_permitted", "The installation owner does not match the authorized repository owner.");
        var issued = await SendAsync(HttpMethod.Post, $"app/installations/{connection.InstallationId}/access_tokens", jwt,
            new { repository_names = new[] { repository.Split('/')[1] }, permissions }, cancellationToken);
        if (!issued.TryGetProperty("token", out var tokenProperty) || tokenProperty.GetString() is not { Length: > 0 } token ||
            !issued.TryGetProperty("expires_at", out var expiryProperty) || !expiryProperty.TryGetDateTimeOffset(out var expires) ||
            expires <= now.AddMinutes(1) || expires > now.AddHours(1).AddMinutes(5))
            throw new ApiException(502, "repository_response_invalid", "GitHub returned an invalid installation-token lifetime.");
        return new(repository, token, expires);
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, string token, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ApiException(response.StatusCode == HttpStatusCode.NotFound ? 409 : 502,
                response.StatusCode == HttpStatusCode.NotFound ? "workload_missing" : "repository_access_failed",
                $"GitHub repository access returned HTTP {(int)response.StatusCode}. Check the configured app installation, repository and ref.");
        try
        {
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        }
        catch (JsonException)
        {
            throw new ApiException(502, "repository_response_invalid", "GitHub returned invalid repository JSON.");
        }
    }

    private static string Jwt(GitHubAppConnection connection, DateTimeOffset now)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = connection.AppId
        }));
        using var key = RSA.Create();
        key.ImportFromPem(connection.PrivateKeyPem);
        var data = header + "." + payload;
        return data + "." + Encode(key.SignData(Encoding.ASCII.GetBytes(data), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    public void Dispose() => http.Dispose();
}
