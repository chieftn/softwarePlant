using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Factory.Contracts;

namespace Factory.Workbench.Worker;

public interface IWorkerPlantGateway
{
    Task AuthorizeAsync(DevelopmentMission mission, CancellationToken cancellationToken);
    Task<RepositoryAccessCredential> CredentialAsync(bool write, CancellationToken cancellationToken);
    Task<PublishedDevelopmentPullRequest> PublishAsync(string candidate, CancellationToken cancellationToken);
    Task<PublishedDevelopmentPullRequest?> PublishedAsync(string candidate, CancellationToken cancellationToken);
    Task<DevelopmentReview> ReviewAsync(CancellationToken cancellationToken);
    Task ReportAsync(DevelopmentMissionProgress progress, CancellationToken cancellationToken);
}

public sealed record WorkerCommandResult(int ExitCode, string Output, string Error, long DurationMilliseconds);

public sealed class WorkerCommandRunner
{
    public async Task<WorkerCommandResult> RunAsync(string directory, WorkloadCommand command,
        IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(command.TimeoutSeconds));
        var start = new ProcessStartInfo(command.Executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in command.Arguments)
            start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var item in environment)
                start.Environment[item.Key] = item.Value;
        using var process = new Process { StartInfo = start };
        var started = Stopwatch.GetTimestamp();
        if (!process.Start())
            throw new IOException("The declared command did not start.");
        var output = Capture(process.StandardOutput);
        var error = Capture(process.StandardError);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error);
            return new(process.ExitCode, await output, await error, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }

        async Task<string> Capture(StreamReader reader)
        {
            try { return await ReadBoundedAsync(reader, timeout.Token); }
            catch (IOException)
            {
                await timeout.CancelAsync();
                throw;
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (content.Length + read > 64 * 1024)
                throw new IOException("Declared command output exceeded 64 KiB.");
            content.Append(buffer, 0, read);
        }
        return content.ToString();
    }
}

public sealed class GitMissionRepository(DevelopmentMission mission, string runDirectory,
    string authorName, string authorEmail, IWorkerPlantGateway plant, WorkerCommandRunner commands) : IDevelopmentMissionRepository
{
    private readonly string checkout = Path.Combine(runDirectory, "repository");
    private readonly string hooks = Path.Combine(runDirectory, "empty-hooks");
    private readonly string configurationPath = Path.Combine(runDirectory, "git-config.digest");
    private string? configurationDigest;
    public string Checkout => checkout;

    public async Task PrepareAsync(DevelopmentMission approved, CancellationToken cancellationToken)
    {
        if (Directory.Exists(checkout))
            throw new IOException("A checkout already exists; interrupted execution cannot overwrite or replay it.");
        Directory.CreateDirectory(checkout);
        Directory.CreateDirectory(hooks);
        await Git(["init", "--quiet"], cancellationToken);
        await Git(["remote", "add", "origin", Url], cancellationToken);
        var credential = await plant.CredentialAsync(false, cancellationToken);
        await Git(["fetch", "--no-tags", "--depth=1", "origin", mission.Workload.SourceCommit], cancellationToken, credential);
        await Git(["switch", "--quiet", "--detach", mission.Workload.SourceCommit], cancellationToken);
        await Git(["switch", "--quiet", "-c", mission.Branch], cancellationToken);
        configurationDigest = await ConfigurationDigest(cancellationToken);
        await File.WriteAllTextAsync(configurationPath, configurationDigest, cancellationToken);
        await AssertOwnedAsync(mission.Workload.SourceCommit, cancellationToken);
        foreach (var file in mission.Workload.Files)
        {
            var relative = mission.Workload.Configuration.ProductionLine!.Definition.Source.WorkloadPath + "/" + file.Key;
            var path = Path.Combine(checkout, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!MissionPermissionPolicy.Within(checkout, path) ||
                (await File.ReadAllTextAsync(path, cancellationToken)).TrimStart('\uFEFF') != file.Value)
                throw new IOException("Checkout workload files do not match the approved pinned snapshot.");
        }
        foreach (var command in mission.Workload.Validate().Preparation.Commands)
        {
            var result = await Declared(command, cancellationToken);
            if (result.ExitCode != 0)
                throw new IOException("Declared preparation failed; no agent was started.");
        }
        await AssertOwnedAsync(mission.Workload.SourceCommit, cancellationToken);
    }

    public async Task<string> CommitAsync(DevelopmentMission approved, string? expectedParent, CancellationToken cancellationToken)
    {
        await AssertOwnedAsync(expectedParent ?? mission.Workload.SourceCommit, cancellationToken);
        await Git(["add", "--all"], cancellationToken);
        var difference = await Git(["diff", "--cached", "--name-only"], cancellationToken);
        if (string.IsNullOrWhiteSpace(difference.Output))
            throw new IOException("Implementation produced no candidate changes; no PR is manufactured.");
        await Git(["commit", "--quiet", "-m", $"Factory mission {mission.Id}: {mission.Issue.Title}"], cancellationToken);
        var candidate = (await Git(["rev-parse", "HEAD"], cancellationToken)).Output.Trim();
        await AssertCandidateAsync(candidate, cancellationToken);
        return candidate;
    }

    public async Task<CandidateVerification[]> VerifyAsync(DevelopmentMission approved, string candidate, CancellationToken cancellationToken)
    {
        await AssertCandidateAsync(candidate, cancellationToken);
        var evidence = new List<CandidateVerification>();
        foreach (var command in mission.Workload.Validate().Verification.Commands)
        {
            var result = await Declared(command, cancellationToken);
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.Output + "\0" + result.Error)));
            evidence.Add(new(candidate, command, result.ExitCode, digest, result.DurationMilliseconds));
            if (result.ExitCode != 0)
                break;
        }
        await AssertCandidateAsync(candidate, cancellationToken);
        return evidence.ToArray();
    }

    public async Task AssertCandidateAsync(string candidate, CancellationToken cancellationToken)
    {
        await AssertOwnedAsync(candidate, cancellationToken);
        if (!string.IsNullOrWhiteSpace((await Git(["status", "--porcelain"], cancellationToken)).Output))
            throw new IOException("Verification or Alignment changed the candidate workspace.");
    }

    public async Task<PublishedDevelopmentPullRequest> PublishAsync(DevelopmentMission approved, string candidate,
        PublishedDevelopmentPullRequest? previous, CancellationToken cancellationToken)
    {
        await AssertCandidateAsync(candidate, cancellationToken);
        await plant.AuthorizeAsync(mission, cancellationToken);
        var credential = await plant.CredentialAsync(true, cancellationToken);
        var remote = await Git(["ls-remote", "--heads", "origin", $"refs/heads/{mission.Branch}"], cancellationToken, credential);
        var actualHead = remote.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (actualHead != candidate)
        {
            var expectedHead = previous?.CandidateCommit;
            if (actualHead != expectedHead)
                throw new IOException("Another actor changed the remote branch; publication cannot overwrite it.");
            if (expectedHead is not null)
                await Git(["merge-base", "--is-ancestor", expectedHead, candidate], cancellationToken);
            await Git(["push", $"--force-with-lease=refs/heads/{mission.Branch}:{expectedHead ?? ""}",
                "origin", $"{candidate}:refs/heads/{mission.Branch}"], cancellationToken, credential);
        }
        return await plant.PublishAsync(candidate, cancellationToken);
    }

    public Task<PublishedDevelopmentPullRequest?> ReconcilePublicationAsync(DevelopmentMission approved, string candidate,
        CancellationToken cancellationToken) => plant.PublishedAsync(candidate, cancellationToken);
    public Task<DevelopmentReview> ReviewAsync(PublishedDevelopmentPullRequest pullRequest, CancellationToken cancellationToken) =>
        plant.ReviewAsync(cancellationToken);

    private async Task AssertOwnedAsync(string expected, CancellationToken cancellationToken)
    {
        configurationDigest ??= await File.ReadAllTextAsync(configurationPath, cancellationToken);
        if ((await Git(["rev-parse", "HEAD"], cancellationToken)).Output.Trim() != expected ||
            (await Git(["branch", "--show-current"], cancellationToken)).Output.Trim() != mission.Branch ||
            (await Git(["remote", "get-url", "origin"], cancellationToken)).Output.Trim() != Url ||
            await ConfigurationDigest(cancellationToken) != configurationDigest)
            throw new IOException("Repository HEAD, branch, origin or local configuration changed outside worker ownership.");
    }

    private async Task<string> ConfigurationDigest(CancellationToken cancellationToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            (await Git(["config", "--local", "--null", "--list"], cancellationToken)).Output)));

    private async Task<WorkerCommandResult> Declared(WorkloadCommand command, CancellationToken cancellationToken)
    {
        var directory = Path.GetFullPath(command.WorkingDirectory.Replace('/', Path.DirectorySeparatorChar), checkout);
        if (!MissionPermissionPolicy.Within(checkout, directory) || !Directory.Exists(directory))
            throw new IOException("Declared command working directory is missing or outside the contained checkout.");
        return await commands.RunAsync(directory, command, null, cancellationToken);
    }

    private string Url => $"https://github.com/{mission.Workload.Configuration.ProductionLine!.Definition.Source.Repository}.git";

    private async Task<WorkerCommandResult> Git(string[] arguments, CancellationToken cancellationToken,
        RepositoryAccessCredential? credential = null)
    {
        var environment = new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            ["GIT_CONFIG_SYSTEM"] = "/dev/null"
        };
        var fixedArguments = new List<string> { "-c", $"core.hooksPath={hooks}", "-c", "core.fsmonitor=false",
            "-c", "protocol.file.allow=never", "-c", "protocol.ext.allow=never",
            "-c", "submodule.recurse=false", "-c", "commit.gpgsign=false",
            "-c", $"user.name={authorName}", "-c", $"user.email={authorEmail}", "-c", "credential.helper=" };
        if (credential is not null)
        {
            if (credential.Repository != mission.Workload.Configuration.ProductionLine!.Definition.Source.Repository ||
                credential.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30) || string.IsNullOrWhiteSpace(credential.Token))
                throw new IOException("Source credential is missing, expired or outside mission repository authority.");
            static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
            var executable = Environment.ProcessPath ?? throw new IOException("Worker executable path is unavailable.");
            var helper = "!" + Quote(executable);
            if (Path.GetFileNameWithoutExtension(executable) == "dotnet")
                helper += " " + Quote(Assembly.GetEntryAssembly()!.Location);
            fixedArguments.AddRange(["-c", "credential.useHttpPath=true", "-c", $"credential.helper={helper} git-credential"]);
            environment["FACTORY_GITHUB_TOKEN"] = credential.Token;
            environment["FACTORY_GITHUB_REPOSITORY"] = credential.Repository;
        }
        fixedArguments.AddRange(arguments);
        var result = await commands.RunAsync(checkout, new("git", fixedArguments.ToArray(), ".", 120), environment, cancellationToken);
        if (result.ExitCode != 0)
            throw new IOException($"Owned Git operation failed with exit {result.ExitCode}; inspect redacted evidence before retrying.");
        return result;
    }
}
