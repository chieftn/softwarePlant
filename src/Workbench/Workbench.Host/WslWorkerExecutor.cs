using System.Diagnostics;
using System.Text;
using Workbench.Contracts;

namespace Workbench.Host;

public sealed class WslWorkerExecutor(string distribution, string workerPath, string workspaceRoot, string outputRoot)
{
    public string ResultPath(string id) => Path.Combine(Path.GetFullPath(outputRoot), id, "result.json");

    public async Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        var assignment = request.Assignment;
        var start = WslLaunch.Create(distribution, workerPath, workspaceRoot, request.TimeoutSeconds);
        var directory = Path.GetDirectoryName(ResultPath(assignment.Id))!;
        if (Directory.Exists(directory))
            throw new IOException("Host output directory exists; inspect it rather than replaying the assignment.");
        Directory.CreateDirectory(directory);
        await using (var snapshot = new FileStream(Path.Combine(directory, "request.json"), FileMode.CreateNew))
            await System.Text.Json.JsonSerializer.SerializeAsync(snapshot, request, Wire.Options, cancellationToken);
        WorkerResult result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
            using var process = Process.Start(start) ?? throw new IOException("Could not launch the WSL worker.");
            await using var events = new StreamWriter(Path.Combine(directory, "events.jsonl"), false, new UTF8Encoding(false));
            await using var errors = new StreamWriter(Path.Combine(directory, "worker-stderr.log"), false, new UTF8Encoding(false));
            WorkerResult? terminal = null;
            var stdout = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    await events.WriteLineAsync(line);
                    await events.FlushAsync();
                    var item = Wire.Read<WorkerEvent>(line);
                    if (item.AssignmentId != assignment.Id)
                        throw new InvalidDataException("Worker event has the wrong assignment identity.");
                    Console.WriteLine(line);
                    if (item.Type == "result")
                    {
                        if (terminal is not null || item.Result is null || item.Result.AssignmentId != assignment.Id)
                            throw new InvalidDataException("Worker emitted an invalid or duplicate terminal result.");
                        terminal = item.Result;
                    }
                }
            });
            var stderr = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    await errors.WriteLineAsync(line);
                    await errors.FlushAsync();
                    Console.Error.WriteLine(line);
                }
            });
            try
            {
                await process.StandardInput.WriteLineAsync(Wire.Write(request));
                await process.StandardInput.FlushAsync();
                var exited = process.WaitForExitAsync(timeout.Token);
                var first = await Task.WhenAny(exited, stdout, stderr);
                if (first.IsFaulted)
                    await first;
                await exited;
                await Task.WhenAll(stdout, stderr);
            }
            finally
            {
                if (!process.HasExited)
                {
                    try
                    {
                        await process.StandardInput.WriteLineAsync("{\"type\":\"cancel\"}");
                        await process.StandardInput.FlushAsync();
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    }
                    catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException)
                    {
                        Console.Error.WriteLine("Worker cancellation did not finish cleanly: " + error.Message);
                        if (!process.HasExited)
                            process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (terminal is null || terminal.Status is not ("succeeded" or "failed" or "cancelled") ||
                (terminal.Status == "succeeded" && process.ExitCode != 0))
                throw new InvalidDataException($"Worker exited {process.ExitCode} without a consistent terminal result.");
            if (request.PublishPullRequest && terminal.Status == "succeeded" && string.IsNullOrWhiteSpace(terminal.PullRequestUrl))
                throw new InvalidDataException("PR publication was requested but no PR was reported.");
            result = terminal;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            result = new WorkerResult
            {
                AssignmentId = assignment.Id,
                Status = error is OperationCanceledException ? "cancelled" : "failed",
                Error = error.Message
            };
        }
        await File.WriteAllTextAsync(ResultPath(assignment.Id), Wire.Write(result), CancellationToken.None);
        return result;
    }
}
