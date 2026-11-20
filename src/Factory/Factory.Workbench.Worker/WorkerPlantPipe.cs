using System.Collections.Concurrent;
using System.Text.Json;
using Factory.Contracts;
using Factory.Workbench;

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
        {
            try
            {
                var failure = response.Payload.Deserialize<WorkerOperationFailure>(RepositoryWorkload.Json)
                    ?? throw new InvalidDataException("Worker operation failure details are missing.");
                if (failure.Message is null || failure.Message.Length > 2048 ||
                    failure.Code is null || !System.Text.RegularExpressions.Regex.IsMatch(failure.Code, @"\A[A-Za-z0-9_-]{1,128}\z"))
                    throw new InvalidDataException("Worker operation failure details are invalid.");
                completion.SetException(new MissionOperationException(response.Operation, MissionTelemetry.Safe(failure.Message),
                    code: failure.Code, httpStatus: failure.HttpStatus, correlationId: failure.CorrelationId));
            }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                completion.SetException(new InvalidDataException("Worker operation failure details could not be validated.", error));
            }
        }
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
        timeout.CancelAfter(TimeSpan.FromSeconds(operation == "model-credential" ? 65 : 30));
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
    public Task<ModelAccessCredential> ModelCredentialAsync(DevelopmentMission mission, CancellationToken cancellationToken) =>
        Request<ModelAccessCredential>("model-credential", new MissionModelCredentialRequest(mission.Id, mission.Digest), cancellationToken);
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
