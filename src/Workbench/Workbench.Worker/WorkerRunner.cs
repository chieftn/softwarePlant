using System.Text.Json;
using Workbench.Contracts;

namespace Workbench.Worker;

public sealed class WorkerRunner(ICommandRunner commands, IAgentRunner agent)
{
    public async Task<WorkerResult> RunAsync(WorkerRequest request, string workspaceRoot,
        Action<WorkerEvent> progress, CancellationToken cancellationToken)
    {
        request.Validate();
        var assignment = request.Assignment;
        var root = Path.GetFullPath(workspaceRoot);
        Directory.CreateDirectory(root);
        var run = Path.Combine(root, assignment.Id);
        if (Directory.Exists(run))
            throw new IOException("Assignment directory already exists; inspect it rather than replaying work.");
        Directory.CreateDirectory(run);
        await using var claim = new FileStream(Path.Combine(run, "request.json"), FileMode.CreateNew);
        await JsonSerializer.SerializeAsync(claim, request, Wire.Options, cancellationToken);
        await claim.FlushAsync(cancellationToken);
        var checkout = Path.Combine(run, "repository");
        var verification = new List<VerificationResult>();
        string? response = null;
        string? commit = null;
        string? prUrl = null;
        WorkerResult result;

        void Report(string stage, string message) => progress(new("progress", assignment.Id, stage, message));
        async Task<CommandResult> Execute(string directory, string executable, params string[] arguments)
        {
            var command = new CommandSpec { Executable = executable, Arguments = arguments };
            var output = await commands.RunAsync(directory, command, cancellationToken);
            if (output.ExitCode != 0)
                throw new IOException($"{executable} exited {output.ExitCode}: {output.StandardError}\n{output.StandardOutput}");
            return output;
        }
        Task<CommandResult> Git(params string[] arguments) => Execute(checkout, "git", arguments);

        try
        {
            Report("prepare", "Checking tools and preparing a dedicated checkout.");
            await Execute(root, "git", "check-ref-format", "--branch", assignment.BaseBranch);
            if (request.PublishPullRequest)
            {
                await Execute(root, "gh", "--version");
                await Execute(root, "gh", "auth", "status", "--hostname", "github.com");
            }
            await Execute(root, "git", "clone", "--single-branch", "--no-tags",
                "--branch", assignment.BaseBranch, "--",
                $"https://github.com/{assignment.Repository}.git", checkout);
            var remoteBranch = await Git("ls-remote", "--heads", "origin", $"refs/heads/{assignment.Branch}");
            if (!string.IsNullOrWhiteSpace(remoteBranch.StandardOutput))
                throw new IOException("The assignment's remote branch already exists; work is not automatically repeated.");
            await Git("switch", "-c", assignment.Branch);
            var initialCommit = (await Git("rev-parse", "HEAD")).StandardOutput.Trim();
            var hooks = Path.Combine(run, "empty-hooks");
            Directory.CreateDirectory(hooks);
            await Git("config", "--local", "core.hooksPath", hooks);
            await Git("var", "GIT_AUTHOR_IDENT");
            Report("agent", "Starting the MAF Copilot coding worker.");
            response = await agent.RunAsync(request, checkout, progress, cancellationToken);

            async Task CheckRepository()
            {
                if ((await Git("rev-parse", "HEAD")).StandardOutput.Trim() != initialCommit ||
                    (await Git("branch", "--show-current")).StandardOutput.Trim() != assignment.Branch ||
                    (await Git("remote", "get-url", "origin")).StandardOutput.Trim()
                        != $"https://github.com/{assignment.Repository}.git")
                    throw new IOException("Worker or verification changed the base commit, branch or origin.");
            }
            await CheckRepository();
            Report("verify", "Running the assignment's verification commands independently of the agent response.");
            foreach (var command in assignment.VerificationCommands)
            {
                var output = await commands.RunAsync(checkout, command, cancellationToken);
                verification.Add(new(command, output));
                if (output.ExitCode != 0)
                    throw new IOException($"Verification failed: {command.Executable} exited {output.ExitCode}.");
            }
            await CheckRepository();
            await Git("add", "--all");
            var diff = await Git("diff", "--cached", "--stat");
            if (string.IsNullOrWhiteSpace(diff.StandardOutput))
                throw new IOException("The agent produced no changes; no commit or PR is created.");
            await Git("-c", $"core.hooksPath={hooks}", "commit", "-m", assignment.PullRequestTitle);
            commit = (await Git("rev-parse", "HEAD")).StandardOutput.Trim();
            if (request.PublishPullRequest)
            {
                Report("publish", "Pushing the verified branch and creating its PR.");
                await Git("push", "--set-upstream", "origin", $"HEAD:refs/heads/{assignment.Branch}");
                var bodyPath = Path.Combine(run, "pr-body.md");
                await File.WriteAllTextAsync(bodyPath,
                    $"Assignment: {assignment.Id}\n\n" +
                    $"Production line: {request.Methods.LineId} @ {request.Methods.LineVersion}\n\n" +
                    "Acceptance criteria:\n" + string.Join("\n", assignment.AcceptanceCriteria.Select(item => "- " + item)) +
                    "\n\nVerification:\n" + string.Join("\n", verification.Select(item =>
                        $"- `{item.Command.Executable}`: exit {item.Result.ExitCode}")),
                    cancellationToken);
                await Execute(checkout, "gh", "pr", "create", "--repo", assignment.Repository,
                    "--base", assignment.BaseBranch, "--head", assignment.Branch,
                    "--title", assignment.PullRequestTitle, "--body-file", bodyPath);
                var inspected = await Execute(checkout, "gh", "pr", "view", assignment.Branch,
                    "--repo", assignment.Repository, "--json", "url,headRefName,headRefOid,baseRefName,state");
                using var pr = JsonDocument.Parse(inspected.StandardOutput);
                var data = pr.RootElement;
                prUrl = data.GetProperty("url").GetString();
                if (data.GetProperty("headRefName").GetString() != assignment.Branch ||
                    data.GetProperty("headRefOid").GetString() != commit ||
                    data.GetProperty("baseRefName").GetString() != assignment.BaseBranch ||
                    data.GetProperty("state").GetString() != "OPEN" ||
                    prUrl is null || !prUrl.StartsWith($"https://github.com/{assignment.Repository}/pull/", StringComparison.Ordinal))
                    throw new IOException("Created PR did not match the expected repository, branch, commit and base.");
            }
            result = new()
            {
                AssignmentId = assignment.Id,
                Status = "succeeded",
                Workspace = checkout,
                Branch = assignment.Branch,
                Commit = commit,
                PullRequestUrl = prUrl,
                AgentResponse = response,
                Verification = verification.ToArray()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new()
            {
                AssignmentId = assignment.Id,
                Status = "cancelled",
                Workspace = checkout,
                Branch = assignment.Branch,
                Commit = commit,
                PullRequestUrl = prUrl,
                Error = "Execution was cancelled or timed out. Inspect artifacts before submitting new work.",
                AgentResponse = response,
                Verification = verification.ToArray()
            };
        }
        catch (Exception error)
        {
            Report("failed", error.Message);
            Console.Error.WriteLine(error);
            result = new()
            {
                AssignmentId = assignment.Id,
                Status = "failed",
                Workspace = checkout,
                Branch = assignment.Branch,
                Commit = commit,
                PullRequestUrl = prUrl,
                Error = error.Message,
                AgentResponse = response,
                Verification = verification.ToArray()
            };
        }
        await File.WriteAllTextAsync(Path.Combine(run, "result.json"), Wire.Write(result), CancellationToken.None);
        return result;
    }
}
