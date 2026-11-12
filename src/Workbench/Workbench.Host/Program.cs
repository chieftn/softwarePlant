using System.Text;
using Workbench.Contracts;
using Workbench.Host;

Console.OutputEncoding = new UTF8Encoding(false);
if (args is ["--help"])
{
    Console.WriteLine("""
        Workbench.Host --assignment <JSON> --line <JSON> --distro <WSL-name>
          --worker-path <Linux-Worker.dll-path> --workspace-root <Linux-path> --output <Windows-directory>
          [--worker coder] [--model <id>] [--timeout-seconds 1800] [--allow-shell] [--publish-pr]

        Workbench.Host --daemon --distributor <HTTPS-URL> --workbench <id> --state <directory>
          --repositories <owner/repo,owner/repo> --distro <WSL-name>
          --worker-path <Linux-Worker.dll-path> --workspace-root <Linux-path> --output <Windows-directory>
          [--allow-shell] [--publish-pr]
        Daemon authentication: WORKBENCH_TOKEN environment variable.

        Workbench.Host --submit --distributor <HTTPS-URL> --workbench <id>
          --assignment <JSON> --line <JSON>
          [--worker coder] [--model <id>] [--timeout-seconds 1800] [--allow-shell] [--publish-pr]
        Submission authentication: DISTRIBUTOR_ADMIN_TOKEN environment variable.
        """);
    return 0;
}

try
{
    var options = new Arguments(args,
        ["--assignment", "--line", "--distro", "--worker-path", "--workspace-root", "--output",
            "--worker", "--model", "--timeout-seconds", "--distributor", "--workbench", "--state", "--repositories"],
        ["--allow-shell", "--publish-pr", "--daemon", "--submit"]);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, item) => { item.Cancel = true; cancellation.Cancel(); };
    if (options.Flag("--submit"))
    {
        if (options.Flag("--daemon") ||
            new[] { "--distro", "--worker-path", "--workspace-root", "--output", "--state", "--repositories" }
                .Any(name => options.Optional(name) is not null))
            throw new ArgumentException("Submission does not run a daemon or a local worker.");
        var dispatch = new DispatchRequest
        {
            WorkbenchId = options.Required("--workbench"),
            Request = await LoadRequestAsync(options)
        };
        dispatch.Validate();
        using var submitClient = DistributorClient.Create(options.Required("--distributor"),
            Environment.GetEnvironmentVariable("DISTRIBUTOR_ADMIN_TOKEN")
                ?? throw new ArgumentException("DISTRIBUTOR_ADMIN_TOKEN is required."));
        Console.WriteLine(Wire.Write(await submitClient.SubmitAsync(dispatch, cancellation.Token)));
        return 0;
    }
    if (!OperatingSystem.IsWindows())
        throw new PlatformNotSupportedException("The workbench host runs on Windows.");
    var executor = new WslWorkerExecutor(options.Required("--distro"), options.Required("--worker-path"),
        options.Required("--workspace-root"), options.Required("--output"));
    _ = WslLaunch.Create(options.Required("--distro"), options.Required("--worker-path"),
        options.Required("--workspace-root"), 1800);
    if (options.Flag("--daemon"))
    {
        if (new[] { "--assignment", "--line", "--worker", "--model", "--timeout-seconds" }
            .Any(name => options.Optional(name) is not null))
            throw new ArgumentException("Daemon requests supply their own assignment, methods, model and timeout.");
        var id = options.Required("--workbench");
        DispatchRequest.ValidateWorkbenchId(id);
        var token = Environment.GetEnvironmentVariable("WORKBENCH_TOKEN")
            ?? throw new ArgumentException("WORKBENCH_TOKEN is required.");
        using var client = DistributorClient.Create(options.Required("--distributor"), token);
        var daemon = new WorkbenchDaemon(client, id, options.Required("--state"),
            new DaemonPolicy(options.Required("--repositories").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                options.Flag("--allow-shell"), options.Flag("--publish-pr")),
            executor.RunAsync, executor.ResultPath);
        await daemon.RunAsync(cancellation.Token);
        return 0;
    }
    if (new[] { "--distributor", "--workbench", "--state", "--repositories" }
        .Any(name => options.Optional(name) is not null))
        throw new ArgumentException("Distributor connection options require --daemon.");
    var request = await LoadRequestAsync(options);
    var result = await executor.RunAsync(request, cancellation.Token);
    return result.Status == "succeeded" ? 0 : 1;
}

catch (OperationCanceledException)
{
    Console.Error.WriteLine("Workbench host stopped.");
    return 1;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

static async Task<WorkerRequest> LoadRequestAsync(Arguments options) => new()
{
    Assignment = Wire.Read<Assignment>(await File.ReadAllTextAsync(options.Required("--assignment"))),
    Methods = ProductionMethods.Load(options.Required("--line"), options.Optional("--worker") ?? "coder"),
    AllowShellExecution = options.Flag("--allow-shell"),
    PublishPullRequest = options.Flag("--publish-pr"),
    TimeoutSeconds = options.TimeoutSeconds(),
    Model = options.Optional("--model")
};
