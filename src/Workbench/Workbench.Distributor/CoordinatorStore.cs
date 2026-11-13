using System.Security.Cryptography;
using System.Text;
using Azure.AI.AgentServer.Core.Storage;
using Workbench.Contracts;

namespace Workbench.Distributor;

public sealed class DistributionConflictException(string message) : Exception(message);

public sealed class CoordinatorStore(FoundryStateStore store, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan PresenceLimit = TimeSpan.FromSeconds(90);

    public async Task<WorkbenchState> RegisterAsync(string id, string principal,
        WorkbenchRegistration registration, CancellationToken cancellationToken)
    {
        DispatchRequest.ValidateWorkbenchId(id);
        registration.Validate();
        return await ChangeAsync(plant =>
        {
            if (plant.Workbenches.TryGetValue(id, out var current) && current.PrincipalId != principal)
                throw new DistributionConflictException("Workbench is already bound to another identity.");
            var value = new WorkbenchState(id, principal, registration,
                current?.RegisteredAt ?? clock.GetUtcNow(), clock.GetUtcNow(), "online", null);
            plant.Workbenches[id] = value;
            return Presence(plant, value);
        }, cancellationToken);
    }

    public async Task<WorkbenchState[]> WorkbenchesAsync(CancellationToken cancellationToken)
    {
        var (plant, _) = await ReadAsync(cancellationToken);
        return plant.Workbenches.Values.OrderBy(item => item.WorkbenchId)
            .Select(item => Presence(plant, item)).ToArray();
    }

    public async Task SubmitAsync(DispatchRequest dispatch, CancellationToken cancellationToken)
    {
        dispatch.Validate();
        var (existing, _) = await ReadAsync(cancellationToken);
        RequireRegistered(existing, dispatch.WorkbenchId);
        await ImmutableAsync("request-" + dispatch.Request.Assignment.Id, dispatch, cancellationToken);
        await ChangeAsync(plant =>
        {
            RequireRegistered(plant, dispatch.WorkbenchId);
            if (!plant.Assignments.TryAdd(dispatch.Request.Assignment.Id,
                new(dispatch.WorkbenchId, clock.GetUtcNow(), dispatch.Request.PublishPullRequest)))
                throw new DistributionConflictException("Assignment ID already exists; inspect it rather than submitting again.");
            return true;
        }, cancellationToken);
    }

    public async Task<WorkClaim?> ClaimAsync(string id, string principal, string claimId, CancellationToken cancellationToken)
    {
        Assignment.Require(Guid.TryParseExact(claimId, "N", out _), "A claim ID in GUID N format is required.");
        var assignmentId = await ChangeAsync(plant =>
        {
            Touch(plant, id, principal);
            var previous = plant.Assignments.FirstOrDefault(item =>
                item.Value.ClaimId == claimId ||
                (item.Value.WorkbenchId == id && item.Value.ClaimId is not null && item.Value.ResultKey is null));
            if (previous.Value is { } active)
            {
                if (active.WorkbenchId != id || active.ClaimId != claimId || active.ResultKey is not null)
                    throw new DistributionConflictException("Workbench has unresolved work or this claim ID has already completed.");
                return previous.Key;
            }
            var next = plant.Assignments.Where(item => item.Value.WorkbenchId == id && item.Value.ClaimId is null)
                .OrderBy(item => item.Value.SubmittedAt).ThenBy(item => item.Key, StringComparer.Ordinal).FirstOrDefault();
            if (next.Value is null)
                return null;
            plant.Assignments[next.Key] = next.Value with { ClaimId = claimId, LastHeartbeat = clock.GetUtcNow() };
            return next.Key;
        }, cancellationToken);
        return assignmentId is null ? null : new(claimId,
            await PayloadAsync<DispatchRequest>("request-" + assignmentId, cancellationToken));
    }

    public Task HeartbeatAsync(string id, string workbench, string principal, string claim,
        CancellationToken cancellationToken) => ChangeAsync(plant =>
        {
            Touch(plant, workbench, principal);
            var current = RequireClaim(plant, id, workbench, claim);
            if (current.ResultKey is not null)
                throw new DistributionConflictException("Claim is no longer active.");
            plant.Assignments[id] = current with { LastHeartbeat = clock.GetUtcNow() };
            return true;
        }, cancellationToken);

    public async Task CompleteAsync(string id, string workbench, string? principal, string? claim,
        WorkerResult result, CancellationToken cancellationToken)
    {
        Assignment.Require(result.AssignmentId == id, "Result assignment identity does not match.");
        Assignment.Require(result.Status is "succeeded" or "failed" or "cancelled", "Invalid terminal status.");
        Assignment.Require(result.Status == "succeeded" || !string.IsNullOrWhiteSpace(result.Error),
            "Failed or cancelled results need an explanation.");
        var (existing, _) = await ReadAsync(cancellationToken);
        if (principal is not null)
            Touch(existing, workbench, principal);
        var entry = RequireClaim(existing, id, workbench, claim);
        if (result.Status == "succeeded" && entry.PublishPullRequest && string.IsNullOrWhiteSpace(result.PullRequestUrl))
            throw new InvalidDataException("Successful PR publication requires a PR URL.");
        var key = "result-" + id + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Wire.Write(result))))[..32];
        await ImmutableAsync(key, result, cancellationToken);
        await ChangeAsync(plant =>
        {
            if (principal is not null)
                Touch(plant, workbench, principal);
            var current = RequireClaim(plant, id, workbench, claim);
            if (result.Status == "succeeded" && current.PublishPullRequest && string.IsNullOrWhiteSpace(result.PullRequestUrl))
                throw new InvalidDataException("Successful PR publication requires a PR URL.");
            if (current.ResultKey is not null && current.ResultKey != key)
                throw new DistributionConflictException("A different terminal result is already recorded.");
            plant.Assignments[id] = current with { ResultKey = key, Status = result.Status };
            return true;
        }, cancellationToken);
    }

    public async Task<AssignmentState?> GetAsync(string id, CancellationToken cancellationToken)
    {
        var (plant, _) = await ReadAsync(cancellationToken);
        if (!plant.Assignments.TryGetValue(id, out var current))
            return null;
        var status = current.ResultKey is not null ? current.Status!
            : current.ClaimId is null ? "queued"
            : clock.GetUtcNow() - current.LastHeartbeat!.Value > PresenceLimit ? "attention" : "running";
        return new(id, current.WorkbenchId, status, current.SubmittedAt, current.LastHeartbeat,
            current.ResultKey is null ? null : await PayloadAsync<WorkerResult>(current.ResultKey, cancellationToken));
    }

    private WorkbenchState Presence(PlantState plant, WorkbenchState value) => value with
    {
        Presence = clock.GetUtcNow() - value.LastSeen > PresenceLimit ? "offline" : "online",
        ActiveAssignmentId = plant.Assignments.FirstOrDefault(item =>
            item.Value.WorkbenchId == value.WorkbenchId && item.Value.ClaimId is not null && item.Value.ResultKey is null).Key
    };

    private void Touch(PlantState plant, string id, string principal)
    {
        var workbench = RequireRegistered(plant, id);
        if (workbench.PrincipalId != principal)
            throw new DistributionConflictException("Workbench registration belongs to another identity.");
        plant.Workbenches[id] = workbench with { LastSeen = clock.GetUtcNow() };
    }

    private static WorkbenchState RequireRegistered(PlantState plant, string id) =>
        plant.Workbenches.GetValueOrDefault(id) ?? throw new DistributionConflictException("Workbench must register first.");

    private static AssignmentEntry RequireClaim(PlantState plant, string id, string workbench, string? claim)
    {
        if (!plant.Assignments.TryGetValue(id, out var current) || current.WorkbenchId != workbench ||
            current.ClaimId is null || (claim is not null && current.ClaimId != claim))
            throw new DistributionConflictException("Assignment has no matching claim.");
        return current;
    }

    private async Task<(PlantState State, StateStoreItem? Item)> ReadAsync(CancellationToken cancellationToken)
    {
        var item = await store.GetItemAsync("plant", cancellationToken);
        var plant = item is null ? new() : Decode<PlantState>(item);
        Assignment.Require(plant.SchemaVersion == "0.1", "Unsupported persisted plant schemaVersion.");
        return (plant, item);
    }

    private async Task<T> ChangeAsync<T>(Func<PlantState, T> change, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var (plant, item) = await ReadAsync(cancellationToken);
            var result = change(plant);
            var value = Encode(plant);
            try
            {
                if (item is null)
                    await store.CreateItemAsync("plant", value, cancellationToken: cancellationToken);
                else
                {
                    if (item.Etag == default)
                        throw new InvalidDataException("Plant storage did not provide an ETag; refusing an unconditional update.");
                    await store.SetItemAsync("plant", value, ifMatch: item.Etag, cancellationToken: cancellationToken);
                }
                return result;
            }
            catch (Exception error) when (error is FoundryStorageConflictException or FoundryStoragePreconditionException)
            {
                // Retry a pure state transition, never an agent invocation or external side effect.
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), cancellationToken);
            }
        }
        throw new DistributionConflictException("Concurrent plant updates did not settle. Retry the same operation identity.");
    }

    private async Task ImmutableAsync<T>(string key, T payload, CancellationToken cancellationToken)
    {
        var value = Encode(payload);
        try
        {
            await store.CreateItemAsync(key, value, cancellationToken: cancellationToken);
        }
        catch (FoundryStorageConflictException)
        {
            var previous = await store.GetItemAsync(key, cancellationToken)
                ?? throw new InvalidDataException("An immutable item disappeared after a conflict.");
            if (Wire.Write(Decode<T>(previous)) != Wire.Write(payload))
                throw new DistributionConflictException("The persisted operation identity has a different payload.");
        }
    }

    private async Task<T> PayloadAsync<T>(string key, CancellationToken cancellationToken) =>
        Decode<T>(await store.GetItemAsync(key, cancellationToken)
            ?? throw new InvalidDataException("Plant state references a missing payload: " + key));

    private static T Decode<T>(StateStoreItem item) => Wire.Read<T>(item.Value["data"].ToString());

    private static Dictionary<string, BinaryData> Encode<T>(T value)
    {
        var json = Wire.Write(value);
        Assignment.Require(Encoding.UTF8.GetByteCount(json) <= 900 * 1024,
            "Coordinator state or payload exceeds the 900 KiB safety limit. Archive/migrate state or store large artifacts externally.");
        return new() { ["data"] = BinaryData.FromString(json) };
    }
}

public sealed record AssignmentEntry(string WorkbenchId, DateTimeOffset SubmittedAt, bool PublishPullRequest)
{
    public string? ClaimId { get; init; }
    public DateTimeOffset? LastHeartbeat { get; init; }
    public string? ResultKey { get; init; }
    public string? Status { get; init; }
}

public sealed class PlantState
{
    public string SchemaVersion { get; init; } = "0.1";
    public Dictionary<string, WorkbenchState> Workbenches { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, AssignmentEntry> Assignments { get; init; } = new(StringComparer.Ordinal);
}
