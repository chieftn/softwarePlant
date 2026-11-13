using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Contracts;
using Factory.Plant.Service.Storage;

namespace Factory.Plant.Service;

public sealed record PlantResult(PlantView Plant, string Revision);

public sealed class PlantOperations(IRecordStore store, Pagination pagination, TimeProvider clock, ApiSettings settings)
{
    private const int Attempts = 3;

    public async Task<Page<PlantView>> ListPlantsAsync(ApiCaller caller, int pageSize, string? token, CancellationToken cancellationToken)
    {
        var after = pagination.Read(token, caller, "plants", pageSize);
        var candidates = await store.QueryAsync(new("plant", pageSize + 1, AfterId: after), cancellationToken);
        var items = new List<PlantView>();
        foreach (var candidate in candidates.Take(pageSize))
        {
            var plant = Records.Require<PlantRecord>(candidate);
            if (plant.ScopeId != Records.PlantScope(plant.PlantId) || plant.Plant.Id != plant.PlantId || plant.Id != plant.PlantId)
                throw new InvalidOperationException("The stored plant address is inconsistent.");
            var access = await AccessAsync(plant.PlantId, caller, cancellationToken);
            if (CanReadPlant(plant, access, caller))
                items.Add(plant.Plant);
        }
        return new("1", items, Next(candidates, pageSize, caller, "plants"));
    }

    public async Task<PlantResult> GetPlantAsync(string plantId, ApiCaller caller, CancellationToken cancellationToken)
    {
        var current = await PlantAsync(plantId, cancellationToken);
        var plant = Records.Require<PlantRecord>(current.Record);
        if (!CanReadPlant(plant, await AccessAsync(plantId, caller, cancellationToken), caller))
            throw ApiProblems.Forbidden();
        return new(plant.Plant, plant.Revision);
    }

    public async Task<PlantResult> CreatePlantAsync(ApiCaller caller, string key, PlantWriteRequest request,
        CancellationToken cancellationToken)
    {
        request.Validate();
        // A stable server-derived ID keeps creation and its retry receipt in one Cosmos partition.
        var idBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"Factory.Plant.Create.v1:{caller.Key}:{key}"))[..16];
        idBytes[6] = (byte)((idBytes[6] & 15) | 128);
        idBytes[8] = (byte)((idBytes[8] & 63) | 128);
        var plantId = new Guid(idBytes, bigEndian: true).ToString("D");
        var scope = Records.PlantScope(plantId);
        var operationId = OperationId(caller, "create", key);
        var digest = Digest(request);

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var existing = await ReadAsync<PlantRecord>(scope, plantId, plantId, cancellationToken);
            if (existing is not null && Records.Require<PlantRecord>(existing.Record).Plant.Owner != caller.Principal)
                throw ApiProblems.Forbidden();
            var replay = await ReplayAsync(scope, plantId, operationId, digest, cancellationToken);
            if (replay is not null)
                return PlantReplay(replay);
            if (existing is not null)
                throw new ApiException(409, "plant_state_conflict", "The plant exists without this creation receipt.");

            var now = clock.GetUtcNow();
            var plant = new PlantView("1", plantId, request.Name, caller.Principal, "active", now, now);
            var revision = Revision();
            if (await store.CommitAsync(scope,
            [
                new(new PlantRecord { Id = plantId, PlantId = plantId, ScopeId = scope, Plant = plant, Revision = revision }),
                new(new OwnerRecord { Id = "owner", PlantId = plantId, ScopeId = scope, Principal = caller.Principal }),
                new(Receipt(scope, plantId, operationId, digest, now, plant, revision)),
                new(Audit(scope, plantId, operationId, caller, "plant.created", now))
            ], cancellationToken))
                return new(plant, revision);
        }
        throw ApiProblems.Unavailable();
    }

    public async Task<PlantResult> UpdatePlantAsync(string plantId, ApiCaller caller, string key, string ifMatch,
        PlantWriteRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        var scope = Records.PlantScope(plantId);
        var operationId = OperationId(caller, "update", key);
        var digest = Digest(new { Request = request, IfMatch = ifMatch });
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var current = await PlantAsync(plantId, cancellationToken);
            var plant = Records.Require<PlantRecord>(current.Record);
            if (plant.Plant.Owner != caller.Principal)
                throw ApiProblems.Forbidden();
            var replay = await ReplayAsync(scope, plantId, operationId, digest, cancellationToken);
            if (replay is not null)
                return PlantReplay(replay);
            if (plant.Plant.State != "active")
                throw new ApiException(409, "plant_state_conflict", "The plant does not accept updates.");
            if (plant.Revision != ifMatch)
                throw new ApiException(412, "etag_mismatch", "The plant has changed. Read it before deciding on a new update.");
            var now = clock.GetUtcNow();
            var updated = plant with { Plant = plant.Plant with { Name = request.Name, UpdatedAt = now }, Revision = Revision() };
            if (await store.CommitAsync(scope,
            [
                new(updated, current.ETag),
                new(Receipt(scope, plantId, operationId, digest, now, updated.Plant, updated.Revision)),
                new(Audit(scope, plantId, operationId, caller, "plant.updated", now))
            ], cancellationToken))
                return new(updated.Plant, updated.Revision);
        }
        throw ApiProblems.Unavailable();
    }

    public async Task<Page<WorkbenchView>> ListWorkbenchesAsync(string plantId, ApiCaller caller, int pageSize,
        string? token, CancellationToken cancellationToken)
    {
        var plant = Records.Require<PlantRecord>((await PlantAsync(plantId, cancellationToken)).Record);
        var access = await AccessAsync(plantId, caller, cancellationToken);
        if (!Owner(plant, caller) && !access.Any(WorkbenchRole))
            throw ApiProblems.Forbidden();
        var route = $"plants/{plantId}/workbenches";
        var after = pagination.Read(token, caller, route, pageSize);
        var candidates = await store.QueryAsync(new("workbench", pageSize + 1, plantId, AfterId: after), cancellationToken);
        var items = new List<WorkbenchView>();
        foreach (var candidate in candidates.Take(pageSize))
        {
            var workbench = Records.Require<WorkbenchRecord>(candidate);
            if (workbench.PlantId != plantId || workbench.ScopeId != Records.WorkbenchScope(plantId, workbench.Id))
                throw new InvalidOperationException("The stored workbench address is inconsistent.");
            if (CanReadWorkbench(plant, access, caller, workbench.Id))
                items.Add(await ViewAsync(workbench, cancellationToken));
        }
        return new("1", items, Next(candidates, pageSize, caller, route));
    }

    public async Task<WorkbenchView> GetWorkbenchAsync(string plantId, string workbenchId, ApiCaller caller,
        CancellationToken cancellationToken)
    {
        var plant = Records.Require<PlantRecord>((await PlantAsync(plantId, cancellationToken)).Record);
        if (!CanReadWorkbench(plant, await AccessAsync(plantId, caller, cancellationToken), caller, workbenchId))
            throw ApiProblems.Forbidden();
        var workbench = await ReadAsync<WorkbenchRecord>(Records.WorkbenchScope(plantId, workbenchId), workbenchId,
            plantId, cancellationToken) ?? throw new ApiException(404, "workbench_not_found", "No workbench exists in this plant at that ID.");
        return await ViewAsync(Records.Require<WorkbenchRecord>(workbench.Record), cancellationToken);
    }

    public async Task RegisterAsync(string plantId, string workbenchId, ApiCaller caller, string key,
        WorkbenchRegistration request, CancellationToken cancellationToken)
    {
        request.Validate();
        var scope = Records.WorkbenchScope(plantId, workbenchId);
        var operationId = OperationId(caller, "registration", key);
        var digest = Digest(request);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            await ActiveWorkbenchAsync(plantId, workbenchId, caller, cancellationToken);
            if (await ReplayAsync(scope, plantId, operationId, digest, cancellationToken) is not null)
                return;
            if (!settings.SupportedProtocolVersions.Contains(request.ProtocolVersion, StringComparer.Ordinal) ||
                !request.AssignmentSchemaVersions.Intersect(settings.SupportedAssignmentSchemaVersions, StringComparer.Ordinal).Any())
                throw new ApiException(409, "incompatible_version", "No supported protocol/assignment schema combination is advertised.");
            var current = await ReadAsync<RegistrationRecord>(scope, "registration", plantId, cancellationToken);
            var now = clock.GetUtcNow();
            if (await store.CommitAsync(scope,
            [
                new(new RegistrationRecord
                {
                    Id = "registration", ScopeId = scope, PlantId = plantId, Registration = request, RecordedAt = now
                }, current?.ETag),
                new(Receipt(scope, plantId, operationId, digest, now)),
                new(Audit(scope, plantId, operationId, caller, "workbench.registered", now))
            ], cancellationToken))
                return;
        }
        throw ApiProblems.Unavailable();
    }

    public async Task PresenceAsync(string plantId, string workbenchId, ApiCaller caller, string key,
        WorkbenchPresence request, CancellationToken cancellationToken)
    {
        request.Validate();
        var scope = Records.WorkbenchScope(plantId, workbenchId);
        var operationId = OperationId(caller, "presence", key);
        var digest = Digest(request);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            await ActiveWorkbenchAsync(plantId, workbenchId, caller, cancellationToken);
            if (await ReplayAsync(scope, plantId, operationId, digest, cancellationToken) is not null)
                return;
            if (await ReadAsync<RegistrationRecord>(scope, "registration", plantId, cancellationToken) is null)
                throw new ApiException(409, "workbench_not_registered", "Registration is required before reporting presence.");
            var current = await ReadAsync<PresenceRecord>(scope, "presence", plantId, cancellationToken);
            var now = clock.GetUtcNow();
            if (await store.CommitAsync(scope,
            [
                new(new PresenceRecord { Id = "presence", ScopeId = scope, PlantId = plantId, Presence = request, RecordedAt = now }, current?.ETag),
                new(Receipt(scope, plantId, operationId, digest, now))
            ], cancellationToken))
                return;
        }
        throw ApiProblems.Unavailable();
    }

    private async Task ActiveWorkbenchAsync(string plantId, string workbenchId, ApiCaller caller, CancellationToken cancellationToken)
    {
        var plant = Records.Require<PlantRecord>((await PlantAsync(plantId, cancellationToken)).Record);
        var current = await ReadAsync<WorkbenchRecord>(Records.WorkbenchScope(plantId, workbenchId), workbenchId, plantId, cancellationToken);
        if (current is null)
            throw ApiProblems.Forbidden();
        var workbench = Records.Require<WorkbenchRecord>(current.Record);
        if (plant.Plant.State != "active" || workbench.AdmissionState != "active" ||
            workbench.Identity != new EntraApplicationPrincipal(caller.Principal.TenantId, caller.Principal.ObjectId, caller.ClientId))
            throw ApiProblems.Forbidden();
    }

    private async Task<WorkbenchView> ViewAsync(WorkbenchRecord workbench, CancellationToken cancellationToken)
    {
        var registration = await ReadAsync<RegistrationRecord>(workbench.ScopeId, "registration", workbench.PlantId, cancellationToken);
        var presence = await ReadAsync<PresenceRecord>(workbench.ScopeId, "presence", workbench.PlantId, cancellationToken);
        var registered = registration is null ? null : Records.Require<RegistrationRecord>(registration.Record);
        var observed = presence is null ? null : Records.Require<PresenceRecord>(presence.Record);
        var contact = observed is null ? "unknown" :
            clock.GetUtcNow() - observed.RecordedAt <= TimeSpan.FromSeconds(90) ? "online" : "stale";
        return new("1", workbench.Id, workbench.PlantId, workbench.Name, workbench.Identity, workbench.AdmissionState,
            contact, workbench.CreatedAt, registered?.Registration, registered?.RecordedAt, observed?.Presence, observed?.RecordedAt);
    }

    private async Task<StoredRecord> PlantAsync(string plantId, CancellationToken cancellationToken) =>
        await ReadAsync<PlantRecord>(Records.PlantScope(plantId), plantId, plantId, cancellationToken)
            ?? throw new ApiException(404, "plant_not_found", "No plant exists at that ID.");

    private async Task<StoredRecord?> ReadAsync<T>(string scope, string id, string plantId, CancellationToken cancellationToken)
        where T : StoreRecord
    {
        var result = await store.ReadAsync(scope, id, cancellationToken);
        if (result is not null)
        {
            var record = Records.Require<T>(result.Record);
            if (record.ScopeId != scope || record.Id != id || record.PlantId != plantId)
                throw new InvalidOperationException("The stored record address is inconsistent.");
            if (record is PlantRecord plant && plant.Plant.Id != plantId)
                throw new InvalidOperationException("The stored plant identity is inconsistent.");
        }
        return result;
    }

    private async Task<IReadOnlyList<AccessRecord>> AccessAsync(string plantId, ApiCaller caller, CancellationToken cancellationToken)
    {
        var records = new List<AccessRecord>();
        string? after = null;
        while (true)
        {
            var page = await store.QueryAsync(new("access", 100, plantId, Records.PlantScope(plantId), caller.Principal, after),
                cancellationToken);
            foreach (var record in page)
            {
                var access = Records.Require<AccessRecord>(record);
                if (access.ScopeId != Records.PlantScope(plantId) || access.PlantId != plantId || access.Principal != caller.Principal)
                    throw new InvalidOperationException("The stored role assignment scope is inconsistent.");
                records.Add(access);
            }
            if (page.Count < 100)
                return records;
            after = page[^1].Id;
        }
    }

    private async Task<OperationRecord?> ReplayAsync(string scope, string plantId, string id, string digest,
        CancellationToken cancellationToken)
    {
        var current = await ReadAsync<OperationRecord>(scope, id, plantId, cancellationToken);
        if (current is null)
            return null;
        var receipt = Records.Require<OperationRecord>(current.Record);
        if (receipt.Digest != digest)
            throw ApiProblems.Conflict();
        return receipt;
    }

    private static PlantResult PlantReplay(OperationRecord receipt) =>
        new(receipt.Plant ?? throw new InvalidOperationException("The plant receipt has no recorded result."),
            receipt.Revision ?? throw new InvalidOperationException("The plant receipt has no recorded revision."));

    private static bool Owner(PlantRecord plant, ApiCaller caller) => plant.Plant.Owner == caller.Principal;
    private static bool WorkbenchRole(AccessRecord access) => access.Role is "Plant.Reader" or "Plant.Operator" or "Workbench.Registrar";
    private static bool CanReadPlant(PlantRecord plant, IReadOnlyList<AccessRecord> access, ApiCaller caller) =>
        Owner(plant, caller) || access.Any(item => item.WorkbenchId is null &&
            (WorkbenchRole(item) || item.Role == "Plant.EnrollmentRequester"));
    private static bool CanReadWorkbench(PlantRecord plant, IReadOnlyList<AccessRecord> access, ApiCaller caller, string id) =>
        Owner(plant, caller) || access.Any(item => WorkbenchRole(item) && (item.WorkbenchId is null || item.WorkbenchId == id));

    private string? Next(IReadOnlyList<StoreRecord> candidates, int size, ApiCaller caller, string route) =>
        candidates.Count > size ? pagination.Create(caller, route, size, candidates[size - 1].Id) : null;

    private static string Revision() => $"\"{Guid.NewGuid():N}\"";
    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Records.Json)));
    private static string OperationId(ApiCaller caller, string operation, string key) =>
        "operation-" + Digest(new { Actor = caller.Key, Operation = operation, Key = key });

    private static OperationRecord Receipt(string scope, string plant, string id, string digest, DateTimeOffset now,
        PlantView? result = null, string? revision = null) =>
        new() { Id = id, ScopeId = scope, PlantId = plant, Digest = digest, RecordedAt = now, Plant = result, Revision = revision };

    private static AuditRecord Audit(string scope, string plant, string operation, ApiCaller caller, string action, DateTimeOffset now) =>
        new() { Id = operation + "-audit", ScopeId = scope, PlantId = plant, Principal = caller.Principal, Action = action, RecordedAt = now };
}
