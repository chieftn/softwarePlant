using System.Security.Cryptography;
using System.Text.Json;
using Factory.Contracts;
using Factory.Plant.Service.Storage;

namespace Factory.Plant.Service;

public sealed partial class EnrollmentOperations
{
    private async Task<StoredRecord> RequireMachineAsync(string id, ApiCaller caller, CancellationToken cancellationToken)
    {
        var stored = await ReadAsync(id, cancellationToken) ?? throw ApiProblems.Forbidden();
        var enrollment = Records.Require<EnrollmentRecord>(stored.Record);
        if (enrollment.State is not ("ready" or "active") || caller.Principal.TenantId != api.TenantId ||
            enrollment.ClientId != caller.ClientId || enrollment.ServicePrincipalId != caller.Principal.ObjectId)
            throw ApiProblems.Forbidden();
        return stored;
    }

    public async Task<WorkbenchAssignment> ConnectionAsync(string id, ApiCaller caller, CancellationToken cancellationToken)
    {
        var enrollment = Records.Require<EnrollmentRecord>((await RequireMachineAsync(id, caller, cancellationToken)).Record);
        return new("1", id, enrollment.WorkbenchId, enrollment.AssignedPlantId, enrollment.AssignmentVersion);
    }

    public async Task<EnrollmentView> AssignAsync(string id, ApiCaller caller, EnrollmentAssignment request,
        CancellationToken cancellationToken)
    {
        caller.RequireEnrollmentAdministrator();
        request.Validate();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stored = await ReadAsync(id, cancellationToken)
                ?? throw new ApiException(404, "enrollment_not_found", "Enrollment was not found.");
            var enrollment = Records.Require<EnrollmentRecord>(stored.Record);
            if (enrollment.State is not ("ready" or "active"))
                throw new ApiException(409, "enrollment_not_ready", "Only a provisioned, non-retired enrollment can be assigned.");
            if (request.PlantId is { } target)
                await plants.RequireRegistrarAsync(target, caller, cancellationToken);
            if (enrollment.AssignedPlantId is { } source)
                await plants.RequireRegistrarAsync(source, caller, cancellationToken);
            if (enrollment.AssignmentVersion != request.ExpectedVersion)
            {
                if (enrollment.AssignmentVersion != request.ExpectedVersion + 1 || enrollment.AssignedPlantId != request.PlantId)
                    throw new ApiException(409, "assignment_changed", "Assignment changed. Refresh before deciding on a new assignment.");
                await EnsureAssignmentAsync(enrollment, cancellationToken);
                return View(enrollment);
            }
            if (enrollment.AssignedPlantId == request.PlantId)
            {
                await EnsureAssignmentAsync(enrollment, cancellationToken);
                return View(enrollment);
            }
            var next = enrollment with
            {
                AssignedPlantId = request.PlantId, AssignmentVersion = checked(enrollment.AssignmentVersion + 1),
                UpdatedAt = clock.GetUtcNow()
            };
            if (!await store.CommitAsync(enrollment.ScopeId, [new(next, stored.ETag), new(new AuditRecord
            {
                Id = $"assignment-{next.AssignmentVersion}", ScopeId = enrollment.ScopeId, PlantId = request.PlantId,
                Principal = caller.Principal, Action = request.PlantId is null ? "workbench.unassigned" : "workbench.assigned",
                RecordedAt = clock.GetUtcNow()
            })], cancellationToken))
                continue;
            await EnsureAssignmentAsync(next, cancellationToken);
            return View(next);
        }
        throw ApiProblems.Unavailable();
    }

    private async Task EnsureAssignmentAsync(EnrollmentRecord enrollment, CancellationToken cancellationToken)
    {
        if (enrollment.AssignedPlantId is not { } plantId)
            return;
        var scope = Records.WorkbenchScope(plantId, enrollment.WorkbenchId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stored = await store.ReadAsync(scope, enrollment.WorkbenchId, cancellationToken);
            var previous = stored is null ? null : Records.Require<WorkbenchRecord>(stored.Record);
            if (previous?.EnrollmentId == enrollment.Id && previous.AssignmentVersion >= enrollment.AssignmentVersion)
                return;
            if (previous is not null && previous.EnrollmentId != enrollment.Id &&
                (previous.EnrollmentId is null || (await ReadAsync(previous.EnrollmentId, cancellationToken))?.Record
                    is not EnrollmentRecord { State: "retired" }))
                throw new ApiException(409, "workbench_identity_conflict", "Another non-retired enrollment owns this workbench.");
            var current = Records.Require<EnrollmentRecord>((await ReadAsync(enrollment.Id, cancellationToken))!.Record);
            if (current.AssignedPlantId != plantId || current.AssignmentVersion != enrollment.AssignmentVersion ||
                current.State is not ("ready" or "active"))
                return;
            var workbench = new WorkbenchRecord
            {
                Id = enrollment.WorkbenchId, ScopeId = scope, PlantId = plantId, EnrollmentId = enrollment.Id,
                AssignmentVersion = enrollment.AssignmentVersion, AdmissionState = "active",
                CreatedAt = clock.GetUtcNow(), Name = enrollment.Draft.MachineName,
                Identity = new(api.TenantId, enrollment.ServicePrincipalId!, enrollment.ClientId!)
            };
            if (await store.CommitAsync(scope, [new(workbench, stored?.ETag)], cancellationToken))
                return;
        }
        throw ApiProblems.Unavailable();
    }

    public async Task PresenceAsync(string id, ApiCaller caller, string key, WorkbenchPresence request,
        CancellationToken cancellationToken)
    {
        request.Validate();
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, Records.Json)));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stored = await RequireMachineAsync(id, caller, cancellationToken);
            var enrollment = Records.Require<EnrollmentRecord>(stored.Record);
            if (enrollment.State != "active")
                throw ApiProblems.Forbidden();
            await EnsureAssignmentAsync(enrollment, cancellationToken);
            var receiptId = $"presence-{key}";
            var receipt = await store.ReadAsync(enrollment.ScopeId, receiptId, cancellationToken);
            if (receipt is not null)
            {
                if (Records.Require<OperationRecord>(receipt.Record).Digest != digest)
                    throw ApiProblems.Conflict();
                return;
            }
            var previous = await store.ReadAsync(enrollment.ScopeId, "presence", cancellationToken);
            var now = clock.GetUtcNow();
            if (await store.CommitAsync(enrollment.ScopeId,
                [new(enrollment, stored.ETag), new(new PresenceRecord
                {
                    Id = "presence", ScopeId = enrollment.ScopeId, EnrollmentId = id,
                    RecordedAt = now, Presence = request
                }, previous?.ETag), new(new OperationRecord
                {
                    Id = receiptId, ScopeId = enrollment.ScopeId, Digest = digest, RecordedAt = now
                })], cancellationToken))
                return;
        }
        throw ApiProblems.Unavailable();
    }
}
