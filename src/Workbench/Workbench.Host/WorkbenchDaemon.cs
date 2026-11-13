using Workbench.Contracts;

namespace Workbench.Host;

public sealed record DaemonPolicy(string[] Repositories, bool AllowShellExecution, bool PublishPullRequest)
{
    public void Validate(WorkerRequest request)
    {
        request.Validate();
        Assignment.Require(Repositories.Contains(request.Assignment.Repository, StringComparer.OrdinalIgnoreCase),
            "Repository is not in this daemon's local allowlist.");
        Assignment.Require(!request.AllowShellExecution || AllowShellExecution,
            "This daemon does not permit agent shell execution.");
        Assignment.Require(!request.PublishPullRequest || PublishPullRequest,
            "This daemon does not permit PR publication.");
    }
}

public sealed class WorkbenchDaemon(
    DistributorClient client, string workbench, string stateDirectory, DaemonPolicy policy,
    Func<WorkerRequest, CancellationToken, Task<WorkerResult>> execute, Func<string, string> resultPath)
{
    private readonly string root = Path.GetFullPath(stateDirectory);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        DispatchRequest.ValidateWorkbenchId(workbench);
        Assignment.Require(policy.Repositories.Length > 0, "At least one allowed repository is required.");
        Directory.CreateDirectory(root);
        using var exclusive = new FileStream(Path.Combine(root, "daemon.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var pendingPath = Path.Combine(root, "pending.json");
        var intentPath = Path.Combine(root, "claim-intent.json");
        var capabilities = new List<string> { "wsl", "copilot-sdk", "workspace", "verification", "source-control" };
        if (policy.AllowShellExecution)
            capabilities.Add("agent-shell");
        if (policy.PublishPullRequest)
            capabilities.Add("publish-pr");
        var registration = await RetryAsync(() => client.RegisterAsync(workbench,
            new(Environment.MachineName, capabilities.ToArray()), cancellationToken), cancellationToken);
        if (registration.WorkbenchId != workbench)
            throw new InvalidDataException("Coordinator registered the wrong workbench.");
        Console.WriteLine($"Workbench {workbench} registered; one assignment at a time.");
        while (!cancellationToken.IsCancellationRequested)
        {
            WorkClaim? claim;
            var recovering = File.Exists(pendingPath);
            if (recovering)
                claim = Wire.Read<WorkClaim>(await File.ReadAllTextAsync(pendingPath, cancellationToken));
            else
            {
                if (!File.Exists(intentPath))
                    await SaveAsync(intentPath, new ClaimRequest(Guid.NewGuid().ToString("N")));
                var intent = Wire.Read<ClaimRequest>(await File.ReadAllTextAsync(intentPath, cancellationToken));
                claim = await RetryAsync(() => client.ClaimAsync(workbench, intent.ClaimId, cancellationToken), cancellationToken);
            }
            if (claim is null)
            {
                await Task.Delay(PollInterval, cancellationToken);
                continue;
            }
            claim.Dispatch.Validate();
            if (claim.Dispatch.WorkbenchId != workbench ||
                !Guid.TryParseExact(claim.ClaimId, "N", out _))
                throw new InvalidDataException("Distributor returned a claim with the wrong identity.");
            if (!recovering)
                await SaveAsync(pendingPath, claim);
            File.Delete(intentPath);
            var resultFile = Path.Combine(root, $"result-{claim.ClaimId}.json");
            WorkerResult result;
            if (recovering)
            {
                var state = await RetryAsync(() => client.GetAsync(claim.Dispatch.Request.Assignment.Id, cancellationToken), cancellationToken);
                if (state.WorkbenchId != workbench)
                    throw new InvalidDataException("Recovered assignment belongs to another workbench.");
                if (state.Result is not null)
                {
                    File.Delete(pendingPath);
                    File.Delete(resultFile);
                    continue;
                }
                var savedResult = File.Exists(resultFile) ? resultFile : resultPath(claim.Dispatch.Request.Assignment.Id);
                if (!File.Exists(savedResult))
                    throw new InvalidOperationException(
                        $"Assignment {claim.Dispatch.Request.Assignment.Id} was interrupted. Do not replay. Inspect WSL and PR state, then resolve it through the distributor before restarting.");
                result = Wire.Read<WorkerResult>(await File.ReadAllTextAsync(savedResult, cancellationToken));
            }
            else
            {
                using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var heartbeat = HeartbeatLoopAsync(claim, heartbeatStop.Token);
                try
                {
                    string? rejection = null;
                    try
                    {
                        policy.Validate(claim.Dispatch.Request);
                    }
                    catch (InvalidDataException error)
                    {
                        Console.Error.WriteLine($"Assignment rejected by local policy: {error.Message}");
                        rejection = error.Message;
                    }
                    result = rejection is null ? await execute(claim.Dispatch.Request, cancellationToken)
                        : new() { AssignmentId = claim.Dispatch.Request.Assignment.Id, Status = "failed", Error = rejection };
                }
                finally
                {
                    await heartbeatStop.CancelAsync();
                    await heartbeat;
                }
            }
            await SaveAsync(resultFile, result);
            await RetryAsync(async () => { await client.CompleteAsync(claim, result, cancellationToken); return true; }, cancellationToken);
            Console.WriteLine($"Assignment {result.AssignmentId}: {result.Status}; distributor acknowledged.");
            File.Delete(pendingPath);
            File.Delete(resultFile);
        }
    }

    private async Task HeartbeatLoopAsync(WorkClaim claim, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
                try
                {
                    await client.HeartbeatAsync(claim, cancellationToken);
                }
                catch (Exception error) when (error is HttpRequestException or InvalidDataException ||
                    (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    Console.Error.WriteLine("Heartbeat failed; assignment remains reserved: " + error.Message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping the heartbeat never releases or replays an assignment.
        }
    }

    private static async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await action();
            }
            catch (Exception error) when (error is HttpRequestException ||
                (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                Console.Error.WriteLine("Distributor connection failed; retrying without replaying work: " + error.Message);
                await Task.Delay(PollInterval, cancellationToken);
            }
        }
    }

    private static async Task SaveAsync<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, Wire.Write(value), CancellationToken.None);
        File.Move(temporary, path, overwrite: true);
    }
}
