using System.Text.Json.Nodes;
using GitHub.Copilot;
using Workbench.Contracts;
using Workbench.Host;
using Workbench.Worker;
using Xunit;

namespace Workbench.Tests;

public sealed class WorkbenchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "workbench-tests-" + Guid.NewGuid().ToString("N"));

    public WorkbenchTests() => Directory.CreateDirectory(root);

    internal static WorkerRequest Request(bool publish = false) => new()
    {
        Assignment = new()
        {
            Id = "test-task",
            Repository = "chieftn/productionOutput1",
            Instructions = "Implement a bounded change.",
            PullRequestTitle = "Test change",
            AcceptanceCriteria = ["Behavior is verified."],
            VerificationCommands = [new() { Executable = "verify", Arguments = ["--check"] }]
        },
        Methods = ProductionMethods.Load(Path.Combine(AppContext.BaseDirectory, "production-line.json"), "coder"),
        PublishPullRequest = publish
    };

    [Fact]
    public void LineSelectionResolvesSkillsAndRejectsBrokenReferences()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "production-line.json");
        var selected = ProductionMethods.Load(source, "coder");
        Assert.NotEmpty(selected.SkillInstructions);
        Assert.Throws<InvalidDataException>(() => ProductionMethods.Load(source, "unknown"));
        var line = JsonNode.Parse(File.ReadAllText(source))!;
        line["productionMethods"]!["workers"]![1]!["skillRefs"] = new JsonArray("missing-skill");
        var broken = Path.Combine(root, "broken.json");
        File.WriteAllText(broken, line.ToJsonString());
        Assert.Throws<InvalidDataException>(() => ProductionMethods.Load(broken, "coder"));
    }

    [Theory]
    [InlineData("../escape", "chieftn/productionOutput1")]
    [InlineData("valid", "https://github.com/chieftn/productionOutput1")]
    public void AssignmentRejectsUnsafeIdentityAndRepository(string id, string repository)
    {
        var assignment = Request().Assignment with { Id = id, Repository = repository };
        Assert.Throws<InvalidDataException>(assignment.Validate);
    }

    [Fact]
    public void WslLauncherPassesPathsAsArgumentsWithoutShellInterpolation()
    {
        var command = WslLaunch.Create("Ubuntu Custom", "/opt/worker path/Workbench.Worker.dll", "/home/user/work", 90);
        Assert.Equal("wsl.exe", command.FileName);
        Assert.False(command.UseShellExecute);
        Assert.Contains("/opt/worker path/Workbench.Worker.dll", command.ArgumentList);
        Assert.Contains("110s", command.ArgumentList);
        Assert.DoesNotContain("bash", command.ArgumentList);
        Assert.Throws<ArgumentException>(() => WslLaunch.Create("Ubuntu", @"C:\worker.dll", "/home/user/work", 90));
    }

    [Fact]
    public void PermissionPolicyRejectsOutsideFilesAndRequiresExplicitShellPermission()
    {
        var workspace = Path.Combine(root, "repository");
        Assert.True(PermissionPolicy.Allows(new PermissionRequestRead
        { Path = Path.Combine(workspace, "file.txt"), Intention = "Read code" }, workspace, false));
        Assert.False(PermissionPolicy.Allows(new PermissionRequestWrite
        {
            FileName = Path.Combine(root, "outside.txt"),
            Intention = "Write code",
            CanOfferSessionApproval = false,
            Diff = ""
        }, workspace, true));
        Assert.False(PermissionPolicy.Allows(new PermissionRequestRead
        { Path = Path.Combine(workspace, ".git", "config"), Intention = "Read config" }, workspace, true));
        Assert.False(PermissionPolicy.Allows(Shell(), workspace, false));
        Assert.True(PermissionPolicy.Allows(Shell(), workspace, true));
        Assert.False(PermissionPolicy.Allows(Shell(bypass: true), workspace, true));
        Assert.False(PermissionPolicy.Allows(new PermissionRequestRead
        { Path = "file.txt", Intention = "Read code", ManagedApprovalRequired = true }, workspace, true));
    }

    [Fact]
    public async Task VerifiedChangeProducesACommitWithoutPublishingUnlessRequested()
    {
        var commands = new FakeCommands();
        var runner = new WorkerRunner(commands, new FakeAgent());
        var result = await runner.RunAsync(Request(), root, _ => { }, CancellationToken.None);
        Assert.Equal("succeeded", result.Status);
        Assert.Equal("new-commit", result.Commit);
        Assert.Null(result.PullRequestUrl);
        Assert.Single(result.Verification);
        Assert.DoesNotContain(commands.Calls, command => command.Executable == "gh" || command.Arguments.Contains("push"));
        Assert.True(File.Exists(Path.Combine(root, "test-task", "result.json")));
        await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(Request(), root, _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task FailedVerificationNeverCommitsOrPublishes()
    {
        var commands = new FakeCommands { VerificationExit = 3 };
        var result = await new WorkerRunner(commands, new FakeAgent())
            .RunAsync(Request(true), root, _ => { }, CancellationToken.None);
        Assert.Equal("failed", result.Status);
        Assert.Equal(3, Assert.Single(result.Verification).Result.ExitCode);
        Assert.DoesNotContain(commands.Calls, command =>
            command.Arguments.Contains("commit") || command.Arguments.Contains("push") || command.Arguments.Contains("create"));
    }

    [Fact]
    public async Task PublicationRequiresInspectedPrIdentityNotAnAgentClaim()
    {
        var commands = new FakeCommands();
        var result = await new WorkerRunner(commands, new FakeAgent())
            .RunAsync(Request(true), root, _ => { }, CancellationToken.None);
        Assert.Equal("succeeded", result.Status);
        Assert.Equal("https://github.com/chieftn/productionOutput1/pull/1", result.PullRequestUrl);
        Assert.Contains(commands.Calls, command => command.Executable == "gh" && command.Arguments.Contains("view"));
    }

    [Fact]
    public async Task MismatchedPublishedPrIsReportedAsFailure()
    {
        var commands = new FakeCommands { PrHead = "another-branch" };
        var result = await new WorkerRunner(commands, new FakeAgent())
            .RunAsync(Request(true), root, _ => { }, CancellationToken.None);
        Assert.Equal("failed", result.Status);
        Assert.Contains("did not match", result.Error);
    }

    [Fact]
    public async Task CancelledAgentDoesNotCommitOrPublish()
    {
        using var cancellation = new CancellationTokenSource();
        var commands = new FakeCommands();
        var result = await new WorkerRunner(commands, new FakeAgent(cancellation))
            .RunAsync(Request(true), root, _ => { }, cancellation.Token);
        Assert.Equal("cancelled", result.Status);
        Assert.DoesNotContain(commands.Calls, command => command.Arguments.Contains("commit") || command.Arguments.Contains("push"));
    }

    [Fact]
    public async Task CommandRunnerDrainsOutputAndHonorsCancellation()
    {
        var runner = new CommandRunner();
        var echo = new CommandSpec { Executable = "git", Arguments = ["--version"] };
        var output = await runner.RunAsync(root, echo, CancellationToken.None);
        Assert.Equal(0, output.ExitCode);
        Assert.Contains("git version", output.StandardOutput);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(root, echo, cancelled.Token));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private static PermissionRequestShell Shell(bool bypass = false) => new()
    {
        CanOfferSessionApproval = false,
        Commands = [],
        FullCommandText = "git status",
        HasWriteFileRedirection = false,
        Intention = "Inspect code",
        PossiblePaths = [],
        PossibleUrls = [],
        RequestSandboxBypass = bypass
    };

    private sealed class FakeAgent(CancellationTokenSource? cancellation = null) : IAgentRunner
    {
        public Task<string> RunAsync(WorkerRequest request, string workspace,
            Action<WorkerEvent> progress, CancellationToken cancellationToken)
        {
            cancellation?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("The agent claims a PR exists; this is not trusted as evidence.");
        }
    }

    private sealed class FakeCommands : ICommandRunner
    {
        public List<CommandSpec> Calls { get; } = [];
        public int VerificationExit { get; init; }
        public string PrHead { get; init; } = "workbench/test-task";
        private bool committed;

        public Task<CommandResult> RunAsync(string directory, CommandSpec command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(command);
            var args = command.Arguments;
            if (args.Contains("clone"))
                Directory.CreateDirectory(args[^1]);
            if (args.Contains("commit"))
                committed = true;
            var output = args switch
            {
                ["rev-parse", "HEAD"] => committed ? "new-commit\n" : "base-commit\n",
                ["branch", "--show-current"] => "workbench/test-task\n",
                ["remote", "get-url", "origin"] => "https://github.com/chieftn/productionOutput1.git\n",
                ["diff", "--cached", "--stat"] => "file.txt | 1 +\n",
                _ => ""
            };
            if (command.Executable == "gh" && args.Contains("view"))
                output = $$"""{"url":"https://github.com/chieftn/productionOutput1/pull/1","headRefName":"{{PrHead}}","headRefOid":"new-commit","baseRefName":"main","state":"OPEN"}""";
            return Task.FromResult(new CommandResult(command.Executable == "verify" ? VerificationExit : 0, output, ""));
        }
    }
}
