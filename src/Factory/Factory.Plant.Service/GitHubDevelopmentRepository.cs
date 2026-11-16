using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Factory.Contracts;

namespace Factory.Plant.Service;

public interface IGitHubDevelopmentRepository
{
    Task<DevelopmentIssue> IssueAsync(ProductionLineTasks tasks, long number, CancellationToken cancellationToken);
    Task<RepositoryAccessCredential> SourceCredentialAsync(DevelopmentMission mission, bool write, CancellationToken cancellationToken);
    Task<PublishedDevelopmentPullRequest> PublishAsync(DevelopmentMission mission, string candidate, CancellationToken cancellationToken);
    Task<PublishedDevelopmentPullRequest?> PublishedAsync(DevelopmentMission mission, string candidate, CancellationToken cancellationToken);
    Task<DevelopmentReview> ReviewAsync(DevelopmentMission mission, PublishedDevelopmentPullRequest pullRequest, CancellationToken cancellationToken);
}

public sealed partial class GitHubRepositoryReader
{
    public async Task<DevelopmentIssue> IssueAsync(ProductionLineTasks tasks, long number, CancellationToken cancellationToken)
    {
        if (number <= 0)
            throw ApiProblems.Invalid("An explicitly assigned issue number is required.");
        var access = await InstallationTokenAsync(tasks.ConnectionReference, tasks.Repository,
            new() { ["issues"] = "read" }, cancellationToken);
        var issue = await SendAsync(HttpMethod.Get, $"repos/{tasks.Repository}/issues/{number}", access.Token, null, cancellationToken);
        if (issue.TryGetProperty("pull_request", out _) || issue.GetProperty("state").GetString() != "open")
            throw new ApiException(409, "issue_not_eligible", "The assigned task must be an open issue, not a pull request.");
        var body = issue.GetProperty("body").GetString() ?? "";
        var criteria = AcceptanceCriteria(body);
        var resolved = new DevelopmentIssue(number, tasks.Repository, issue.GetProperty("title").GetString()!, body,
            criteria, issue.GetProperty("labels").EnumerateArray().Select(label => label.GetProperty("name").GetString()!).ToArray(),
            issue.GetProperty("updated_at").GetDateTimeOffset());
        try { resolved.Validate(tasks); }
        catch (ContractValidationException)
        {
            throw new ApiException(409, "issue_not_eligible",
                "The assigned issue needs all configured approval labels and explicit bullet-list Acceptance criteria.");
        }
        return resolved;
    }

    public Task<RepositoryAccessCredential> SourceCredentialAsync(DevelopmentMission mission, bool write, CancellationToken cancellationToken)
    {
        var source = mission.Workload.Configuration.ProductionLine!.Definition.Source;
        return InstallationTokenAsync(source.ConnectionReference, source.Repository,
            new() { ["contents"] = write ? "write" : "read" }, cancellationToken);
    }

    public async Task<PublishedDevelopmentPullRequest> PublishAsync(DevelopmentMission mission, string candidate, CancellationToken cancellationToken)
    {
        RepositoryWorkload.Commit(candidate);
        var existing = await PublishedAsync(mission, candidate, cancellationToken);
        if (existing is not null)
            return existing;
        var definition = mission.Workload.Configuration.ProductionLine!.Definition;
        var latestIssue = await IssueAsync(definition.Tasks, mission.Issue.Number, cancellationToken);
        if (latestIssue.UpdatedAt != mission.Issue.UpdatedAt || latestIssue.Body != mission.Issue.Body || latestIssue.Title != mission.Issue.Title)
            throw new ApiException(409, "issue_changed", "The approved issue changed after freezing. Publication requires operator reconciliation.");
        var access = await InstallationTokenAsync(definition.Source.ConnectionReference, definition.Source.Repository,
            new() { ["contents"] = "read", ["pull_requests"] = "write" }, cancellationToken);
        var prefix = $"repos/{definition.Source.Repository}/";
        var branch = await SendAsync(HttpMethod.Get, prefix + "git/ref/heads/" + mission.Branch, access.Token, null, cancellationToken);
        if (branch.GetProperty("object").GetProperty("sha").GetString() != candidate)
            throw new ApiException(409, "publication_changed", "Remote branch is not the verified candidate.");
        var created = await SendAsync(HttpMethod.Post, prefix + "pulls", access.Token, new
        {
            title = mission.Issue.Title,
            head = mission.Branch,
            @base = definition.Source.BaseBranch,
            draft = true,
            body = $"{Marker(mission)}\n\nImplements {mission.Issue.Repository}#{mission.Issue.Number}.\n\n" +
                "Candidate independently verified and aligned by Factory. Human review is required; Factory does not merge."
        }, cancellationToken);
        var number = created.GetProperty("number").GetInt64();
        var readBack = await SendAsync(HttpMethod.Get, prefix + $"pulls/{number}", access.Token, null, cancellationToken);
        return PullRequest(mission, candidate, readBack);
    }

    public async Task<PublishedDevelopmentPullRequest?> PublishedAsync(DevelopmentMission mission, string candidate, CancellationToken cancellationToken)
    {
        var source = mission.Workload.Configuration.ProductionLine!.Definition.Source;
        var access = await InstallationTokenAsync(source.ConnectionReference, source.Repository,
            new() { ["contents"] = "read", ["pull_requests"] = "read" }, cancellationToken);
        var matches = await SendAsync(HttpMethod.Get, $"repos/{source.Repository}/pulls?state=all&per_page=2&head=" +
            Uri.EscapeDataString(source.Repository.Split('/')[0] + ":" + mission.Branch), access.Token, null, cancellationToken);
        var items = matches.EnumerateArray().ToArray();
        if (items.Length == 0)
            return null;
        if (items.Length != 1)
            throw new ApiException(409, "publication_changed", "The mission branch has ambiguous PR ownership.");
        return PullRequest(mission, candidate, items[0]);
    }

    public async Task<DevelopmentReview> ReviewAsync(DevelopmentMission mission, PublishedDevelopmentPullRequest pullRequest,
        CancellationToken cancellationToken)
    {
        var source = mission.Workload.Configuration.ProductionLine!.Definition.Source;
        var access = await InstallationTokenAsync(source.ConnectionReference, source.Repository,
            new() { ["pull_requests"] = "read", ["issues"] = "read" }, cancellationToken);
        var prefix = $"repos/{source.Repository}/";
        var current = await SendAsync(HttpMethod.Get, prefix + $"pulls/{pullRequest.Number}", access.Token, null, cancellationToken);
        var head = current.GetProperty("head").GetProperty("sha").GetString()!;
        if (current.GetProperty("head").GetProperty("ref").GetString() != mission.Branch ||
            current.GetProperty("base").GetProperty("ref").GetString() != source.BaseBranch ||
            !(current.GetProperty("body").GetString() ?? "").Contains(Marker(mission), StringComparison.Ordinal))
            throw new ApiException(409, "publication_changed", "PR ownership or base changed outside this mission.");
        var feedback = new List<DevelopmentReviewFeedback>();
        foreach (var review in await Pages(prefix + $"pulls/{pullRequest.Number}/reviews"))
        {
            var kind = review.GetProperty("state").GetString() switch
            {
                "APPROVED" => "approved",
                "CHANGES_REQUESTED" => "changesRequested",
                "DISMISSED" => "dismissed",
                _ => null
            };
            if (kind is null || review.GetProperty("submitted_at").ValueKind == JsonValueKind.Null)
                continue;
            var body = review.GetProperty("body").GetString() ?? "";
            if (kind == "changesRequested" && string.IsNullOrWhiteSpace(body))
                body = "Address the current-head changes-requested review and its inline comments.";
            feedback.Add(new("review/" + review.GetProperty("id").GetInt64(), Actor(review),
                review.GetProperty("commit_id").GetString()!, kind, body, review.GetProperty("submitted_at").GetDateTimeOffset()));
        }
        foreach (var comment in await Pages(prefix + $"pulls/{pullRequest.Number}/comments"))
        {
            var body = comment.GetProperty("body").GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(body))
                feedback.Add(new(CommentId("inline", comment), Actor(comment), comment.GetProperty("commit_id").GetString()!,
                    "comment", body, comment.GetProperty("updated_at").GetDateTimeOffset()));
        }
        foreach (var comment in await Pages(prefix + $"issues/{pullRequest.Number}/comments"))
        {
            var body = comment.GetProperty("body").GetString() ?? "";
            var match = Regex.Match(body, @"\A/factory revise ([a-fA-F0-9]{40})(?:\s|$)");
            if (match.Success)
                feedback.Add(new(CommentId("comment", comment), Actor(comment), match.Groups[1].Value,
                    "comment", body, comment.GetProperty("updated_at").GetDateTimeOffset()));
        }
        if (feedback.Any(item => item.Body.Length > 16 * 1024))
            throw new ApiException(409, "review_too_large", "Review feedback exceeds the mission intake bound.");
        var result = new DevelopmentReview(head, current.GetProperty("state").GetString()!, feedback.ToArray());
        if (JsonSerializer.SerializeToUtf8Bytes(result, RepositoryWorkload.Json).Length > 96 * 1024)
            throw new ApiException(409, "review_too_large", "Aggregate review feedback exceeds the bounded worker transport.");
        return result;

        async Task<JsonElement[]> Pages(string path)
        {
            var values = new List<JsonElement>();
            for (var page = 1; page <= 5; page++)
            {
                var result = await SendAsync(HttpMethod.Get, path + $"?per_page=100&page={page}", access.Token, null, cancellationToken);
                var items = result.EnumerateArray().ToArray();
                values.AddRange(items);
                if (items.Length < 100)
                    return values.ToArray();
            }
            throw new ApiException(409, "review_too_large", "Review history exceeds the bounded pagination limit; reconcile it explicitly.");
        }
    }

    private static string Actor(JsonElement value) => value.GetProperty("user").GetProperty("login").GetString()!;
    private static string CommentId(string prefix, JsonElement value) =>
        $"{prefix}/{value.GetProperty("id").GetInt64()}/{value.GetProperty("updated_at").GetDateTimeOffset():O}";
    private static string Marker(DevelopmentMission mission) => $"<!-- factory-mission:{mission.Id}:{mission.Digest} -->";

    private static PublishedDevelopmentPullRequest PullRequest(DevelopmentMission mission, string candidate, JsonElement value)
    {
        var source = mission.Workload.Configuration.ProductionLine!.Definition.Source;
        if (value.GetProperty("state").GetString() != "open" ||
            value.GetProperty("head").GetProperty("ref").GetString() != mission.Branch ||
            value.GetProperty("head").GetProperty("sha").GetString() != candidate ||
            value.GetProperty("head").GetProperty("repo").GetProperty("full_name").GetString() != source.Repository ||
            value.GetProperty("base").GetProperty("ref").GetString() != source.BaseBranch ||
            !(value.GetProperty("body").GetString() ?? "").Contains(Marker(mission), StringComparison.Ordinal))
            throw new ApiException(409, "publication_changed", "Published PR does not match the exact owned branch, commit, base and mission marker.");
        return new(value.GetProperty("number").GetInt64(), source.Repository, mission.Branch, source.BaseBranch, candidate,
            value.GetProperty("html_url").GetString()!);
    }

    internal static string[] AcceptanceCriteria(string body)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var collecting = false;
        var criteria = new List<string>();
        foreach (var line in lines)
        {
            if (Regex.IsMatch(line, @"^#{1,6}\s+Acceptance criteria\s*:?\s*$", RegexOptions.IgnoreCase))
            {
                collecting = true;
                continue;
            }
            if (!collecting)
                continue;
            if (Regex.IsMatch(line, @"^#{1,6}\s"))
                break;
            var match = Regex.Match(line, @"^\s*[-*]\s+(?:\[[ xX]\]\s+)?(.+)$");
            if (match.Success)
                criteria.Add(match.Groups[1].Value.Trim());
        }
        return criteria.ToArray();
    }
}
