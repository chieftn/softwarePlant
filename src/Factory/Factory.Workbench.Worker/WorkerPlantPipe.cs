using System.Collections.Concurrent;
using System.Text.Json;
using Factory.Contracts;

namespace Factory.Workbench.Worker;

public sealed class WorkerPlantPipe(Action<WorkerPipeMessage> emit) : IWorkerPlantGateway
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new(StringComparer.Ordinal);
    private int disconnected;

    public void Receive(WorkerPipeMessage response)
    {
        if (!pending.TryRemove(response.Id, out var completion))
            throw new InvalidDataException("Worker received an unsolicited or duplicate plant response.");
        if (response.Type == "response")
            completion.SetResult(response.Payload);
        else if (response.Type == "error")
            completion.SetException(new IOException("Plant rejected the owned worker operation."));
        else
            completion.SetException(new InvalidDataException("Invalid plant response type."));
    }

    public void Disconnect()
    {
        Interlocked.Exchange(ref disconnected, 1);
        foreach (var entry in pending)
            if (pending.TryRemove(entry.Key, out var completion))
                completion.TrySetException(new IOException("Windows host disconnected; execution authority cannot be retained."));
    }

    private async Task<T> Request<T>(string operation, object body, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disconnected) != 0)
            throw new IOException("Windows host disconnected; no new worker operation is permitted.");
        var id = Guid.NewGuid().ToString("D");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.TryAdd(id, completion);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (Volatile.Read(ref disconnected) != 0)
                throw new IOException("Windows host disconnected before the worker request.");
            emit(new("request", id, operation, JsonSerializer.SerializeToElement(body, RepositoryWorkload.Json)));
            var result = await completion.Task.WaitAsync(timeout.Token);
            return result.Deserialize<T>(RepositoryWorkload.Json) ?? throw new InvalidDataException("Plant returned an empty worker response.");
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    public async Task AuthorizeAsync(DevelopmentMission mission, CancellationToken cancellationToken) =>
        _ = await Request<JsonElement>("authorize", new { missionId = mission.Id }, cancellationToken);
    public Task<RepositoryAccessCredential> CredentialAsync(bool write, CancellationToken cancellationToken) =>
        Request<RepositoryAccessCredential>("credential", new { purpose = write ? "publish" : "read" }, cancellationToken);
    public Task<PublishedDevelopmentPullRequest> PublishAsync(string candidate, CancellationToken cancellationToken) =>
        Request<PublishedDevelopmentPullRequest>("publish", new { candidateCommit = candidate }, cancellationToken);
    public async Task<PublishedDevelopmentPullRequest?> PublishedAsync(string candidate, CancellationToken cancellationToken)
    {
        var value = await Request<JsonElement>("published", new { candidateCommit = candidate }, cancellationToken);
        return value.ValueKind == JsonValueKind.Null ? null : value.Deserialize<PublishedDevelopmentPullRequest>(RepositoryWorkload.Json);
    }
    public Task<DevelopmentReview> ReviewAsync(CancellationToken cancellationToken) =>
        Request<DevelopmentReview>("review", new { }, cancellationToken);
    public async Task ReportAsync(DevelopmentMissionProgress progress, CancellationToken cancellationToken) =>
        _ = await Request<JsonElement>("progress", progress, cancellationToken);
}
