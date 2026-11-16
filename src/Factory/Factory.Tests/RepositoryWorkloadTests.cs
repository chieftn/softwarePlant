using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Contracts;
using Factory.Plant.Service;
using Xunit;

namespace Factory.Tests;

public sealed class RepositoryWorkloadTests
{
    internal static WorkbenchLineConfiguration Configuration() => new("1", PlantApiTests.Key(), "fixture-workbench",
        PlantApiTests.Key(), 1, 1, new("1", PlantApiTests.Key(), $"\"{Guid.NewGuid():N}\"",
            new(ApiTestApplication.Tenant, ApiTestApplication.OwnerId), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ProductionLineTests.Definition()));

    internal static Dictionary<string, string> Files(bool checks = false) => new(StringComparer.Ordinal)
    {
        ["workload.json"] = JsonSerializer.Serialize(new RepositoryWorkload("workload.schema.json", "1",
            "fixture-development", "1.0.0", new(new("agents/implementation.md", "read-write"),
                new("agents/alignment.md", "read-only")), new("implementation-alignment", 3, 30),
            new([]), new(checks ? [new("dotnet", ["test"], ".", 60)] : []),
            ["candidate", "verification-evidence", "alignment-report"]), RepositoryWorkload.Json),
        ["workload.schema.json"] = """{"type":"object"}""",
        ["agents/implementation.md"] = "Implement the approved issue. Do not publish.",
        ["agents/alignment.md"] = "Read-only alignment against frozen acceptance criteria."
    };

    [Fact]
    public void ValidDefinitionWithNoChecksIsNotExecutionReadiness()
    {
        var resolved = ResolvedRepositoryWorkload.Create(Configuration(), new('a', 40), Files());
        Assert.Empty(resolved.Validate().Verification.Commands);
        var tampered = resolved with { SourceCommit = new('b', 40) };
        Assert.Throws<ContractValidationException>(() => tampered.Validate());
    }

    [Theory]
    [InlineData("\"schemaVersion\":\"1\"", "\"schemaVersion\":\"1\",\"schemaVersion\":\"1\"")]
    [InlineData("\"read-only\"", "\"read-write\"")]
    [InlineData("\"agents/alignment.md\"", "\"../alignment.md\"")]
    [InlineData("\"maximumIterations\":3", "\"maximumIterations\":0")]
    [InlineData("\"version\":\"1.0.0\"", "\"version\":\"01.0.0\"")]
    [InlineData("\"type\":\"implementation-alignment\"", "\"type\":\"arbitrary-code\"")]
    public void InvalidOrDuplicateContractsAreRejected(string before, string after) =>
        Assert.Throws<ContractValidationException>(() => RepositoryWorkload.Parse(Files()["workload.json"].Replace(before, after)));

    [Fact]
    public void UnknownPropertiesAreRejected() => Assert.Throws<JsonException>(() =>
        RepositoryWorkload.Parse(Files()["workload.json"].Replace("\"schemaVersion\":", "\"secret\":\"not-allowed\",\"schemaVersion\":")));

    [Fact]
    public async Task GitHubAppReadsOnlyConfiguredRepositoryAndPinsEveryFileToCommitTree()
    {
        using var key = RSA.Create(2048);
        var configuration = Configuration();
        var clock = new TestClock();
        var handler = new GitHubFixture(clock, key);
        using var reader = new GitHubRepositoryReader(Settings(key), clock, handler);
        var resolved = await reader.ResolveAsync(configuration, CancellationToken.None);
        Assert.Equal(new string('a', 40), resolved.SourceCommit);
        resolved.Validate();
        Assert.Equal(Files(), resolved.Files);
        Assert.All(handler.Paths.Where(path => !path.StartsWith("/app/", StringComparison.Ordinal)),
            path => Assert.StartsWith("/repos/product/source/", path));
        Assert.Single(handler.Paths, path => path.Contains("/commits/", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Paths, path => path.Contains("/contents/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("120000", "blob")]
    [InlineData("160000", "commit")]
    public async Task LinkAndSubmoduleFilesAreRejected(string mode, string type)
    {
        using var key = RSA.Create(2048);
        var clock = new TestClock();
        using var reader = new GitHubRepositoryReader(Settings(key), clock, new GitHubFixture(clock, key) { Mode = mode, Type = type });
        var error = await Assert.ThrowsAsync<ApiException>(() => reader.ResolveAsync(Configuration(), CancellationToken.None));
        Assert.Equal("workload_invalid", error.Code);
    }

    [Fact]
    public async Task MissingOrOutOfScopeConnectionsNeverAttemptNetworkAccess()
    {
        using var key = RSA.Create(2048);
        var clock = new TestClock();
        var handler = new GitHubFixture(clock, key);
        var settings = Settings(key);
        settings.Connections["source-app"].Repositories = ["product/other"];
        using var reader = new GitHubRepositoryReader(settings, clock, handler);
        var error = await Assert.ThrowsAsync<ApiException>(() => reader.ResolveAsync(Configuration(), CancellationToken.None));
        Assert.Equal("repository_not_permitted", error.Code);
        Assert.Empty(handler.Paths);
        settings.Connections.Clear();
        error = await Assert.ThrowsAsync<ApiException>(() => reader.ResolveAsync(Configuration(), CancellationToken.None));
        Assert.Equal("repository_connection_unconfigured", error.Code);
        Assert.Empty(handler.Paths);
    }

    private static GitHubConnectionsSettings Settings(RSA key) => new()
    {
        Connections = new()
        {
            ["source-app"] = new()
            {
                AppId = 42,
                InstallationId = 84,
                Repositories = ["product/source"],
                PrivateKeyPem = key.ExportRSAPrivateKeyPem()
            }
        }
    };

    private sealed class GitHubFixture(TestClock clock, RSA key) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public string Mode { get; set; } = "100644";
        public string Type { get; set; } = "blob";
        private readonly Dictionary<string, string> files = Files();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            object response;
            if (path == "/app/installations/84")
            {
                Assert.NotEqual("fixture-installation-token", request.Headers.Authorization!.Parameter);
                response = new { account = new { login = "product" } };
            }
            else if (path == "/app/installations/84/access_tokens")
            {
                var jwt = request.Headers.Authorization!.Parameter!.Split('.');
                static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/')
                    .PadRight((value.Length + 3) / 4 * 4, '='));
                Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), Decode(jwt[2]),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                using var payload = JsonDocument.Parse(Decode(jwt[1]));
                Assert.Equal(42, payload.RootElement.GetProperty("iss").GetInt64());
                Assert.InRange(payload.RootElement.GetProperty("exp").GetInt64() - clock.Now.ToUnixTimeSeconds(), 1, 540);
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                Assert.Equal(["source"], body.GetProperty("repository_names").EnumerateArray().Select(item => item.GetString()!).ToArray());
                Assert.Single(body.GetProperty("permissions").EnumerateObject());
                Assert.Equal("read", body.GetProperty("permissions").GetProperty("contents").GetString());
                response = new { token = "fixture-installation-token", expires_at = clock.Now.AddHours(1) };
            }
            else
            {
                Assert.Equal("fixture-installation-token", request.Headers.Authorization!.Parameter);
                if (path.EndsWith("/commits/main", StringComparison.Ordinal))
                    response = new { sha = new string('a', 40), commit = new { tree = new { sha = new string('b', 40) } } };
                else if (path.EndsWith("/git/trees/" + new string('b', 40), StringComparison.Ordinal))
                    response = Tree(new { path = "productionLine", type = "tree", mode = "040000", sha = new string('c', 40) });
                else if (path.EndsWith("/git/trees/" + new string('c', 40), StringComparison.Ordinal))
                    response = Tree(new { path = "agents", type = "tree", mode = "040000", sha = new string('d', 40) },
                        Blob("workload.json"), Blob("workload.schema.json"));
                else if (path.EndsWith("/git/trees/" + new string('d', 40), StringComparison.Ordinal))
                    response = Tree(Blob("agents/implementation.md"), Blob("agents/alignment.md"));
                else
                {
                    var content = files.Single(item => Hash(item.Key) == path.Split('/')[^1]).Value;
                    var bytes = Encoding.UTF8.GetBytes(content);
                    response = new { size = bytes.Length, encoding = "base64", content = Convert.ToBase64String(bytes) };
                }
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }
        private object Blob(string path) => new { path = path.Split('/')[^1], type = Type, mode = Mode, sha = Hash(path) };
        private static string Hash(string path) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
        private static object Tree(params object[] entries) => new { truncated = false, tree = entries };
    }
}
