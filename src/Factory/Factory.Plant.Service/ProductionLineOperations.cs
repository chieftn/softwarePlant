using System.Security.Cryptography;
using System.Text;
using Factory.Contracts;
using Factory.Plant.Service.Storage;

namespace Factory.Plant.Service;

public sealed partial class PlantOperations
{
    public async Task<Page<ProductionLineView>> ListProductionLinesAsync(ApiCaller caller, int size,
        string? token, CancellationToken cancellationToken)
    {
        const string route = "production-lines";
        var after = pagination.Read(token, caller, route, size);
        var records = await store.QueryAsync(new("productionLine", size + 1, Principal: caller.Principal, AfterId: after),
            cancellationToken);
        var lines = records.Take(size).Select(record => RequireLine(record).Line).ToArray();
        if (lines.Any(line => line.Owner != caller.Principal))
            throw new InvalidOperationException("The stored production-line ownership is inconsistent.");
        return new("1", lines, Next(records, size, caller, route));
    }

    public async Task<ProductionLineView> GetProductionLineAsync(string lineId, ApiCaller caller,
        CancellationToken cancellationToken)
    {
        var line = RequireLine((await LineAsync(lineId, cancellationToken)).Record).Line;
        RequireLineOwner(line, caller);
        return line;
    }

    public async Task<ProductionLineView> CreateProductionLineAsync(ApiCaller caller, string key,
        ProductionLineWriteRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"Factory.Line.Create.v1:{caller.Key}:{key}"))[..16];
        bytes[6] = (byte)((bytes[6] & 15) | 128);
        bytes[8] = (byte)((bytes[8] & 63) | 128);
        var id = new Guid(bytes, bigEndian: true).ToString("D");
        var scope = Records.ProductionLineScope(id);
        var operation = OperationId(caller, "line.create", key);
        var digest = Digest(request);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var existing = await store.ReadAsync(scope, id, cancellationToken);
            if (existing is not null)
                RequireLineOwner(RequireLine(existing.Record).Line, caller);
            var replay = await LineReceiptAsync(scope, operation, digest, cancellationToken);
            if (replay is not null)
                return replay.ProductionLine ?? throw new InvalidOperationException("The line receipt has no result.");
            if (existing is not null)
                throw new ApiException(409, "line_state_conflict", "The line exists without this creation receipt.");
            var now = clock.GetUtcNow();
            var line = new ProductionLineView("1", id, Revision(), caller.Principal, now, now, request);
            if (await store.CommitAsync(scope,
            [
                new(new ProductionLineRecord { Id = id, ScopeId = scope, Principal = caller.Principal, Line = line }),
                new(LineRevision(scope, line)),
                new(new OperationRecord { Id = operation, ScopeId = scope, Digest = digest, RecordedAt = now, ProductionLine = line }),
                new(new AuditRecord { Id = operation + "-audit", ScopeId = scope, Principal = caller.Principal,
                    Action = "productionLine.created", RecordedAt = now })
            ], cancellationToken))
                return line;
        }
        throw ApiProblems.Unavailable();
    }

    public async Task<ProductionLineView> UpdateProductionLineAsync(string id, ApiCaller caller, string key,
        string ifMatch, ProductionLineWriteRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        var scope = Records.ProductionLineScope(id);
        var operation = OperationId(caller, "line.update", key);
        var digest = Digest(new { Request = request, IfMatch = ifMatch });
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var stored = await LineAsync(id, cancellationToken);
            var current = RequireLine(stored.Record);
            RequireLineOwner(current.Line, caller);
            var replay = await LineReceiptAsync(scope, operation, digest, cancellationToken);
            if (replay is not null)
                return replay.ProductionLine ?? throw new InvalidOperationException("The line receipt has no result.");
            if (current.Line.Revision != ifMatch)
                throw new ApiException(412, "etag_mismatch", "The line has changed. Refresh before updating it.");
            var now = clock.GetUtcNow();
            var line = current.Line with { Revision = Revision(), UpdatedAt = now, Definition = request };
            if (await store.CommitAsync(scope,
            [
                new(current with { Line = line }, stored.ETag), new(LineRevision(scope, line)),
                new(new OperationRecord { Id = operation, ScopeId = scope, Digest = digest, RecordedAt = now, ProductionLine = line }),
                new(new AuditRecord { Id = operation + "-audit", ScopeId = scope, Principal = caller.Principal,
                    Action = "productionLine.updated", RecordedAt = now })
            ], cancellationToken))
                return line;
        }
        throw ApiProblems.Unavailable();
    }

    public async Task<ProductionLineBinding> GetProductionLineBindingAsync(string plantId, string workbenchId,
        ApiCaller caller, CancellationToken cancellationToken)
    {
        var workbench = await GetWorkbenchAsync(plantId, workbenchId, caller, cancellationToken);
        var stored = await BindingAsync(plantId, workbenchId, cancellationToken);
        if (stored is null)
            return new("1", plantId, workbenchId, 0, null);
        var binding = Records.Require<ProductionLineBindingRecord>(stored.Record);
        var enrollment = workbench.EnrollmentId is null ? null : await EnrollmentAsync(workbench.EnrollmentId, cancellationToken);
        return EffectiveBinding(binding, enrollment);
    }

    public async Task<WorkbenchLineConfiguration?> GetWorkbenchConfigurationAsync(string plantId, string workbenchId,
        ApiCaller caller, CancellationToken cancellationToken)
    {
        var workbench = await GetWorkbenchAsync(plantId, workbenchId, caller, cancellationToken);
        if (workbench.EnrollmentId is not { } enrollmentId)
            return null;
        var enrollment = await EnrollmentAsync(enrollmentId, cancellationToken);
        if (enrollment is { State: "active", AssignedPlantId: null })
            return await MachineConfigurationAsync(enrollment, cancellationToken);
        return enrollment is { State: "active" } && enrollment.AssignedPlantId == plantId &&
            workbench.AdmissionState == "active"
                ? await MachineConfigurationAsync(enrollment, cancellationToken) : null;
    }

    public async Task<ProductionLineBinding> AssignProductionLineAsync(string plantId, string workbenchId,
        ApiCaller caller, ProductionLineAssignment request, CancellationToken cancellationToken)
    {
        request.Validate();
        if (request.ProductionLineId is { } requestedLine)
            request = request with { ProductionLineId = Guid.Parse(requestedLine).ToString("D") };
        var plant = Records.Require<PlantRecord>((await PlantAsync(plantId, cancellationToken)).Record);
        if (!Owner(plant, caller) || plant.Plant.State != "active")
            throw ApiProblems.Forbidden();
        if (request.ProductionLineId is { } lineId)
            await GetProductionLineAsync(lineId, caller, cancellationToken);
        var scope = Records.WorkbenchScope(plantId, workbenchId);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var member = await ReadAsync<WorkbenchRecord>(scope, workbenchId, plantId, cancellationToken)
                ?? throw new ApiException(404, "workbench_not_found", "The workbench does not exist in this plant.");
            var workbench = Records.Require<WorkbenchRecord>(member.Record);
            var enrollment = workbench.EnrollmentId is null ? null : await EnrollmentAsync(workbench.EnrollmentId, cancellationToken);
            if (workbench.AdmissionState != "active" || enrollment is not { State: "active" } ||
                enrollment.AssignedPlantId != plantId || enrollment.AssignmentVersion != workbench.AssignmentVersion)
                throw new ApiException(409, "workbench_not_assigned", "The workbench must be active in this plant.");
            var stored = await BindingAsync(plantId, workbenchId, cancellationToken);
            var previous = stored is null ? null : Records.Require<ProductionLineBindingRecord>(stored.Record);
            var version = previous?.Binding.Version ?? 0;
            var effective = previous is null ? null : EffectiveBinding(previous, enrollment).ProductionLineId;
            if (version != request.ExpectedVersion)
            {
                if (version == request.ExpectedVersion + 1 && effective == request.ProductionLineId &&
                    previous!.AssignmentVersion == enrollment.AssignmentVersion)
                    return previous.Binding;
                throw new ApiException(409, "line_assignment_changed", "The line assignment changed. Refresh before deciding.");
            }
            if (effective == request.ProductionLineId)
                return new("1", plantId, workbenchId, version, effective);
            var binding = new ProductionLineBinding("1", plantId, workbenchId, checked(version + 1), request.ProductionLineId);
            var now = clock.GetUtcNow();
            if (await store.CommitAsync(scope,
            [
                new(workbench, member.ETag),
                new(new ProductionLineBindingRecord
                {
                    Id = "production-line", ScopeId = scope, PlantId = plantId, Binding = binding,
                    EnrollmentId = enrollment.Id, AssignmentVersion = enrollment.AssignmentVersion
                }, stored?.ETag),
                new(new AuditRecord
                {
                    Id = $"line-assignment-{binding.Version}", ScopeId = scope, PlantId = plantId,
                    Principal = caller.Principal, Action = request.ProductionLineId is null
                        ? "productionLine.unassigned" : "productionLine.assigned", RecordedAt = now
                })
            ], cancellationToken))
                return binding;
        }
        throw ApiProblems.Unavailable();
    }

    internal async Task<WorkbenchLineConfiguration> MachineConfigurationAsync(EnrollmentRecord enrollment,
        CancellationToken cancellationToken)
    {
        if (enrollment.AssignedPlantId is not { } plantId)
            return new("1", enrollment.Id, enrollment.WorkbenchId, null, enrollment.AssignmentVersion, 0, null);
        var plant = Records.Require<PlantRecord>((await PlantAsync(plantId, cancellationToken)).Record);
        var workbench = await ReadAsync<WorkbenchRecord>(Records.WorkbenchScope(plantId, enrollment.WorkbenchId),
            enrollment.WorkbenchId, plantId, cancellationToken);
        if (plant.Plant.State != "active" || workbench?.Record is not WorkbenchRecord member ||
            member.AdmissionState != "active" || member.EnrollmentId != enrollment.Id ||
            member.AssignmentVersion != enrollment.AssignmentVersion)
            throw ApiProblems.Forbidden();
        var stored = await BindingAsync(plantId, enrollment.WorkbenchId, cancellationToken);
        var binding = stored is null ? new ProductionLineBinding("1", plantId, enrollment.WorkbenchId, 0, null)
            : EffectiveBinding(Records.Require<ProductionLineBindingRecord>(stored.Record), enrollment);
        var line = binding.ProductionLineId is null ? null
            : RequireLine((await LineAsync(binding.ProductionLineId, cancellationToken)).Record).Line;
        return new("1", enrollment.Id, enrollment.WorkbenchId, plantId, enrollment.AssignmentVersion, binding.Version, line);
    }

    private async Task<StoredRecord?> BindingAsync(string plantId, string workbenchId, CancellationToken cancellationToken)
    {
        var stored = await ReadAsync<ProductionLineBindingRecord>(Records.WorkbenchScope(plantId, workbenchId),
            "production-line", plantId, cancellationToken);
        if (stored is not null)
        {
            var binding = Records.Require<ProductionLineBindingRecord>(stored.Record).Binding;
            if (binding.SchemaVersion != "1" || binding.PlantId != plantId || binding.WorkbenchId != workbenchId || binding.Version < 1)
                throw new InvalidOperationException("The stored line binding is inconsistent.");
        }
        return stored;
    }

    private static ProductionLineBinding EffectiveBinding(ProductionLineBindingRecord stored, EnrollmentRecord? enrollment) =>
        enrollment is { State: "active" } && enrollment.Id == stored.EnrollmentId &&
        enrollment.AssignedPlantId == stored.PlantId && enrollment.AssignmentVersion == stored.AssignmentVersion
            ? stored.Binding : stored.Binding with { ProductionLineId = null };

    private async Task<StoredRecord> LineAsync(string id, CancellationToken cancellationToken) =>
        await store.ReadAsync(Records.ProductionLineScope(id), id, cancellationToken)
            ?? throw new ApiException(404, "production_line_not_found", "The production line was not found.");

    private static ProductionLineRecord RequireLine(StoreRecord record)
    {
        var line = Records.Require<ProductionLineRecord>(record);
        if (line.ScopeId != Records.ProductionLineScope(line.Id) || line.Line.Id != line.Id ||
            line.Principal != line.Line.Owner || line.Line.SchemaVersion != "1")
            throw new InvalidOperationException("The stored line identity is inconsistent.");
        return line;
    }

    private static void RequireLineOwner(ProductionLineView line, ApiCaller caller)
    {
        if (line.Owner != caller.Principal)
            throw ApiProblems.Forbidden();
    }

    private async Task<OperationRecord?> LineReceiptAsync(string scope, string id, string digest,
        CancellationToken cancellationToken)
    {
        var stored = await store.ReadAsync(scope, id, cancellationToken);
        if (stored is null)
            return null;
        var receipt = Records.Require<OperationRecord>(stored.Record);
        if (receipt.ScopeId != scope || receipt.Id != id)
            throw new InvalidOperationException("The stored line receipt address is inconsistent.");
        if (receipt.Digest != digest)
            throw ApiProblems.Conflict();
        return receipt;
    }

    private static ProductionLineRevisionRecord LineRevision(string scope, ProductionLineView line) =>
        new() { Id = "revision-" + line.Revision.Trim('"'), ScopeId = scope, Line = line };
}
