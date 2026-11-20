using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Factory.Contracts;
using Factory.Plant.Service;
using Xunit;

namespace Factory.Tests;

public sealed class GitHubDevelopmentRepositoryTests
{
    [Fact]
    public async Task DiscoveryPagesPastPullRequestsAndSkipsMissingCriteriaOrLabels()
    {
        using var fixture = new Fixture(uri =>
        {
            Assert.StartsWith("/repos/product/tasks/issues?", uri.PathAndQuery);
            Assert.Contains("state=open&sort=created&direction=asc&labels=approved", uri.Query);
            if (uri.Query.Contains("&page=1", StringComparison.Ordinal))
                return Enumerable.Range(1, 100).Select(_ => new { pull_request = new { } }).ToArray();
            Assert.Contains("&page=2", uri.Query);
            return new object[]
            {
                Issue(101, "No acceptance criteria.", ["approved"]),
                Issue(102, "## Acceptance criteria\n- Preserve ownership.", []),
                Issue(103, "## Acceptance criteria\n- Preserve ownership.", ["approved"])
            };
        });
        var issues = new List<DevelopmentIssue>();
        await foreach (var issue in fixture.Reader.EligibleIssuesAsync(ProductionLineTests.Definition().Tasks, CancellationToken.None))
            issues.Add(issue);
        Assert.Equal(103, Assert.Single(issues).Number);
        Assert.Equal(2, fixture.RepositoryCalls);
    }

    [Fact]
    public async Task DiscoveryScanLimitIsNotReportedAsAnEmptyQueue()
    {
        using var fixture = new Fixture(_ => Enumerable.Range(1, 100).Select(_ => new { pull_request = new { } }).ToArray());
        var error = await Assert.ThrowsAsync<ApiException>(async () =>
        {
            await foreach (var _ in fixture.Reader.EligibleIssuesAsync(ProductionLineTests.Definition().Tasks, CancellationToken.None)) { }
        });
        Assert.Equal("issue_queue_limit", error.Code);
        Assert.Equal(5, fixture.RepositoryCalls);
    }

    [Theory]
    [InlineData(true, "merged")]
    [InlineData(false, "closed")]
    public async Task CompletionReadsActualMergeStateWithoutFetchingReviewHistory(bool merged, string expected)
    {
        var mission = DevelopmentMissionTests.Mission();
        using var fixture = new Fixture(uri =>
        {
            Assert.Equal("/repos/product/source/pulls/9", uri.AbsolutePath);
            return PullRequest(mission, merged);
        });
        var review = await fixture.Reader.ReviewAsync(mission, Receipt(mission), CancellationToken.None);
        Assert.Equal(expected, review.State);
        Assert.Equal(new string('b', 40), review.CandidateCommit);
        Assert.Empty(review.Feedback);
        Assert.Equal(1, fixture.RepositoryCalls);
    }

    [Theory]
    [InlineData("other/source", "product/source", false)]
    [InlineData("product/source", "other/source", false)]
    [InlineData("product/source", "product/source", true)]
    public async Task MergeFromForeignRepositoryOrWithoutMissionMarkerCannotComplete(string head, string @base, bool removeMarker)
    {
        var mission = DevelopmentMissionTests.Mission();
        using var fixture = new Fixture(_ => PullRequest(mission, true, head, @base, removeMarker));
        var error = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.Reader.ReviewAsync(mission, Receipt(mission), CancellationToken.None));
        Assert.Equal("publication_changed", error.Code);
    }

    private static object Issue(long number, string body, string[] labels) => new
    {
        number, body, title = "Approved task", state = "open", updated_at = DateTimeOffset.UtcNow,
        labels = labels.Select(name => new { name }).ToArray()
    };

    private static PublishedDevelopmentPullRequest Receipt(DevelopmentMission mission) =>
        new(9, "product/source", mission.Branch, "main", new('b', 40), "https://github.com/product/source/pull/9");

    private static object PullRequest(DevelopmentMission mission, bool merged, string head = "product/source",
        string @base = "product/source", bool removeMarker = false) => new
        {
            number = 9, state = "closed", merged, html_url = Receipt(mission).Url,
            body = removeMarker ? "" : $"<!-- factory-mission:{mission.Id}:{mission.Digest} -->",
            head = new { sha = new string('b', 40), @ref = mission.Branch, repo = new { full_name = head } },
            @base = new { @ref = "main", repo = new { full_name = @base } }
        };

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly RSA key = RSA.Create(2048);
        private readonly TestClock clock = new();
        private readonly Func<Uri, object> response;
        public GitHubRepositoryReader Reader { get; }
        public int RepositoryCalls { get; private set; }

        public Fixture(Func<Uri, object> response)
        {
            this.response = response;
            var connection = new GitHubAppConnection
            {
                AppId = 42, InstallationId = 84, Repositories = ["product/source", "product/tasks"],
                PrivateKeyPem = key.ExportRSAPrivateKeyPem()
            };
            Reader = new(new GitHubConnectionsSettings
            {
                Connections = new() { ["source-app"] = connection, ["task-app"] = connection }
            }, clock, this);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            object body;
            if (uri.AbsolutePath == "/app/installations/84")
                body = new { account = new { login = "product" } };
            else if (uri.AbsolutePath == "/app/installations/84/access_tokens")
                body = new { token = "fixture-token", expires_at = clock.Now.AddHours(1) };
            else
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("fixture-token", request.Headers.Authorization!.Parameter);
                RepositoryCalls++;
                body = response(uri);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                key.Dispose();
            base.Dispose(disposing);
        }
    }
}
