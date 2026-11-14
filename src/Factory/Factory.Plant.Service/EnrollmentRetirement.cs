using Factory.Contracts;
using Factory.Plant.Service.Storage;

namespace Factory.Plant.Service;

public sealed partial class EnrollmentOperations
{
    public async Task<Page<EnrollmentView>> ListAsync(ApiCaller caller, int pageSize, string? token,
        CancellationToken cancellationToken)
    {
        caller.RequireEnrollmentAdministrator();
        var after = pagination.Read(token, caller, "enrollments", pageSize);
        var records = await store.QueryAsync(new("enrollment", pageSize + 1, AfterId: after), cancellationToken);
        var items = new List<EnrollmentView>();
        foreach (var record in records.Take(pageSize))
            items.Add(await InventoryViewAsync(Records.Require<EnrollmentRecord>(record), cancellationToken));
        return new("1", items, records.Count > pageSize
            ? pagination.Create(caller, "enrollments", pageSize, records[pageSize - 1].Id) : null);
    }

    private async Task<EnrollmentView> InventoryViewAsync(EnrollmentRecord record, CancellationToken cancellationToken)
    {
        var presence = await store.ReadAsync(record.ScopeId, "presence", cancellationToken);
        var report = presence is null ? null : Records.Require<PresenceRecord>(presence.Record);
        return View(record) with { Presence = report?.Presence, LastSeenAt = report?.RecordedAt };
    }

    public Task<EnrollmentView> RevokeAsync(string operationId, ApiCaller caller,
        CancellationToken cancellationToken, EnrollmentDraft? unsubmitted = null)
    {
        caller.RequireEnrollmentAdministrator();
        return RetireAsync(operationId, caller, cancellationToken, unsubmitted, false);
    }

    public Task<EnrollmentView> RetireSelfAsync(string operationId, ApiCaller caller,
        CancellationToken cancellationToken) => RetireAsync(operationId, caller, cancellationToken, null, true);

    private async Task<EnrollmentView> RetireAsync(string operationId, ApiCaller caller,
        CancellationToken cancellationToken, EnrollmentDraft? unsubmitted, bool self)
    {
        var canRetire = Guid.TryParse(settings.ProvisioningClientId, out var provisioner) && provisioner != Guid.Empty;
        var retirementState = canRetire ? "revoking" : "retirementNeedsAttention";
        var retirementError = canRetire ? null : "Access is revoked, but directory retirement requires restoring the provisioning configuration and retrying.";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stored = await ReadAsync(operationId, cancellationToken);
            if (self && (stored?.Record is not EnrollmentRecord machine ||
                caller.Principal.TenantId != api.TenantId || machine.ClientId != caller.ClientId ||
                machine.ServicePrincipalId != caller.Principal.ObjectId ||
                machine.State is not ("ready" or "active" or "revoking" or "retirementNeedsAttention" or "retired")))
                throw ApiProblems.Forbidden();
            if (unsubmitted is not null)
            {
                unsubmitted.Validate();
                if (unsubmitted.OperationId != operationId)
                    throw ApiProblems.Invalid("Revocation draft must match the enrollment operation ID.");
            }
            if (stored is null)
            {
                if (unsubmitted is null)
                    throw new ApiException(404, "enrollment_not_found", "An unsubmitted operation requires its original draft.");
                var now = clock.GetUtcNow();
                var cancelled = new EnrollmentRecord
                {
                    Id = operationId, ScopeId = Records.EnrollmentScope(operationId), Draft = unsubmitted,
                    Requester = caller.Principal, RequesterClientId = caller.ClientId, Digest = Digest(unsubmitted),
                    WorkbenchId = $"wb-{Guid.Parse(unsubmitted.InstallationId):N}", State = retirementState, Error = retirementError,
                    UpdatedAt = now, AuthorizedAt = now, RetirementRequester = caller.Principal
                };
                if (!await store.CommitAsync(cancelled.ScopeId, [new(cancelled), new(new AuditRecord
                {
                    Id = "retirement-requested", ScopeId = cancelled.ScopeId,
                    Principal = caller.Principal, Action = "workbench.enrollment.cancelled", RecordedAt = now
                })], cancellationToken))
                    continue;
                return View(cancelled);
            }
            var operation = Records.Require<EnrollmentRecord>(stored.Record);
            if (unsubmitted is not null && operation.Digest != Digest(unsubmitted))
                throw ApiProblems.Conflict();
            if (operation.State != "retired" && (operation.State != "revoking" || !canRetire))
            {
                var now = clock.GetUtcNow();
                var next = operation with { State = retirementState, UpdatedAt = now, RetryAt = null, Failures = 0,
                    Error = retirementError, RetirementRequester = caller.Principal };
                var auditId = operation.State is "retirementNeedsAttention" or "revoking"
                    ? $"retirement-retry-{Guid.NewGuid():N}" : "retirement-requested";
                // Preserve the provisioning lease until any in-flight Graph create has settled.
                if (!await store.CommitAsync(operation.ScopeId,
                    [new(next, stored.ETag), new(new AuditRecord
                    {
                        Id = auditId, ScopeId = operation.ScopeId, Principal = caller.Principal,
                        Action = "workbench.retirement.requested", RecordedAt = now
                    })], cancellationToken))
                    continue;
                operation = next;
            }
            return View(operation);
        }
        throw ApiProblems.Unavailable();
    }
}
