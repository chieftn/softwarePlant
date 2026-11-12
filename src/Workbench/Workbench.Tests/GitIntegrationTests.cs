using Workbench.Contracts;
using Workbench.Worker;
using Xunit;

namespace Workbench.Tests;

public sealed class GitIntegrationTests
{
    [Fact]
    public async Task WorkerCreatesAVerifiedCommitAgainstALocalRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "workbench-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var commands = new CommandRunner();
            var seed = Path.Combine(root, "seed");
            var remote = Path.Combine(root, "remote.git");
            Directory.CreateDirectory(seed);
            async Task Run(string directory, params string[] arguments)
            {
                var result = await commands.RunAsync(directory,
                    new() { Executable = "git", Arguments = arguments }, CancellationToken.None);
                Assert.True(result.ExitCode == 0, result.StandardError);
            }
            await Run(seed, "init", "--initial-branch=main");
            await Run(seed, "config", "user.name", "Workbench Tests");
            await Run(seed, "config", "user.email", "workbench-tests@example.invalid");
            await Run(seed, "config", "commit.gpgsign", "false");
            var hooks = Path.Combine(root, "empty-hooks");
            Directory.CreateDirectory(hooks);
            await Run(seed, "config", "core.hooksPath", hooks);
            await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "Initial content.\n");
            await Run(seed, "add", "README.md");
            await Run(seed, "commit", "-m", "Initial fixture");
            await Run(root, "clone", "--bare", "--", seed, remote);

            var initial = WorkbenchTests.Request();
            var request = initial with
            {
                Assignment = initial.Assignment with
                {
                    VerificationCommands = [new() { Executable = "git", Arguments = ["diff", "--check"] }]
                }
            };
            var runner = new WorkerRunner(new LocalRepositoryCommands(commands, remote), new EditingAgent());
            var result = await runner.RunAsync(request, Path.Combine(root, "assignments"), _ => { }, CancellationToken.None);
            Assert.Equal("succeeded", result.Status);
            Assert.Matches("^[a-f0-9]{40,64}$", result.Commit!);
            Assert.Null(result.PullRequestUrl);
            Assert.Equal(0, Assert.Single(result.Verification).Result.ExitCode);
            Assert.Equal("Updated by the worker.\n",
                await File.ReadAllTextAsync(Path.Combine(result.Workspace!, "README.md")));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class EditingAgent : IAgentRunner
    {
        public async Task<string> RunAsync(WorkerRequest request, string workspace,
            Action<WorkerEvent> progress, CancellationToken cancellationToken)
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "Updated by the worker.\n", cancellationToken);
            return "Updated README.";
        }
    }

    private sealed class LocalRepositoryCommands(ICommandRunner inner, string remote) : ICommandRunner
    {
        public async Task<CommandResult> RunAsync(string directory, CommandSpec command, CancellationToken cancellationToken)
        {
            if (command.Arguments.Contains("clone"))
            {
                var args = (string[])command.Arguments.Clone();
                args[^2] = remote;
                var result = await inner.RunAsync(directory, command with { Arguments = args }, cancellationToken);
                if (result.ExitCode == 0)
                {
                    foreach (var configuration in new[]
                    {
                        new[] { "remote", "set-url", "origin", "https://github.com/chieftn/productionOutput1.git" },
                        new[] { "config", "user.name", "Workbench Tests" },
                        new[] { "config", "user.email", "workbench-tests@example.invalid" },
                        new[] { "config", "commit.gpgsign", "false" }
                    })
                    {
                        var configured = await inner.RunAsync(args[^1],
                            new() { Executable = "git", Arguments = configuration }, cancellationToken);
                        Assert.True(configured.ExitCode == 0, configured.StandardError);
                    }
                }
                return result;
            }
            if (command.Arguments is ["ls-remote", "--heads", "origin", var branch])
                command = command with { Arguments = ["ls-remote", "--heads", remote, branch] };
            return await inner.RunAsync(directory, command, cancellationToken);
        }
    }
}
