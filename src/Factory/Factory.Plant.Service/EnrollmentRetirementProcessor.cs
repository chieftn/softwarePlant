using System.Net;
using System.Text.Json;
using Azure.Identity;
using Factory.Plant.Service.Storage;

namespace Factory.Plant.Service;

public sealed partial class EnrollmentProcessor
{
    private async Task RetireAsync(StoredRecord stored, CancellationToken cancellationToken)
    {
        var operation = Records.Require<EnrollmentRecord>(stored.Record);
        if (operation.LeaseUntil > clock.GetUtcNow() || operation.RetryAt > clock.GetUtcNow())
            return;
        var owner = Guid.NewGuid().ToString("N");
        operation = operation with { LeaseOwner = owner, LeaseUntil = clock.GetUtcNow().AddMinutes(2) };
        if (!await store.CommitAsync(operation.ScopeId, [new(operation, stored.ETag)], cancellationToken))
            return;
        stored = await operations.ReadAsync(operation.Id, cancellationToken)
            ?? throw new InvalidOperationException("Reserved retirement disappeared.");
        operation = Records.Require<EnrollmentRecord>(stored.Record);
        if (operation.LeaseOwner != owner || operation.State != "revoking")
            return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            if (operation.ApplicationObjectId is null && operation.ApplicationCreateStartedAt is not null)
            {
                var application = await directory.FindApplicationAsync(
                    $"Factory Workbench {api.ClientId} {operation.Id}", operation.Draft.PublicCertificate, budget.Token)
                    ?? throw new DirectoryProvisioningException("The earlier application create outcome remains uncertain; retry retirement after reconciliation.");
                await Save(operation with { ApplicationObjectId = application.ObjectId, ClientId = application.ClientId });
                return;
            }
            if (operation.PrincipalCreateStartedAt is not null && operation.ServicePrincipalId is null)
            {
                var principal = await directory.FindPrincipalAsync(operation.ClientId
                    ?? throw new InvalidOperationException("Retirement has no application client ID."), budget.Token)
                    ?? throw new DirectoryProvisioningException("The earlier principal create outcome remains uncertain; retry retirement after reconciliation.");
                await Save(operation with { ServicePrincipalId = principal.ObjectId });
                return;
            }
            if (operation.ApplicationObjectId is not null)
            {
                var clientId = operation.ClientId ?? throw new InvalidOperationException("Retirement has no application client ID.");
                if (operation.ServicePrincipalId is not null)
                    await directory.DeletePrincipalAsync(operation.ServicePrincipalId, clientId, budget.Token);
                await directory.DeleteApplicationAsync(operation.ApplicationObjectId, clientId, budget.Token);
            }
            await Save(operation with { State = "retired" });
        }
        catch (LeaseLostException)
        {
            logger.LogWarning("Retirement {Operation} lost its lease; the durable request remains resumable.", operation.Id);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is
            (HttpRequestException or AuthenticationFailedException or DirectoryProvisioningException or JsonException or
             FormatException or InvalidOperationException or KeyNotFoundException or OperationCanceledException))
        {
            logger.LogWarning(error, "Directory retirement failed for enrollment {Operation}; plant admission remains revoked.", operation.Id);
            var failures = operation.Failures + 1;
            var attention = failures >= 12 || error is DirectoryProvisioningException or AuthenticationFailedException or
                JsonException or FormatException or InvalidOperationException or KeyNotFoundException ||
                error is HttpRequestException { StatusCode: HttpStatusCode.Forbidden };
            await Save(operation with
            {
                State = attention ? "retirementNeedsAttention" : "revoking", Failures = failures,
                Error = attention
                    ? "Plant access is revoked, but identity retirement needs reconciliation. Check ownership, consent and uncertain creates, then retry removal."
                    : "Plant access is revoked. Directory retirement is temporarily unavailable and will retry.",
                RetryAt = clock.GetUtcNow().AddSeconds(10)
            });
        }

        async Task Save(EnrollmentRecord next)
        {
            if (operation.LeaseOwner != owner || operation.LeaseUntil <= clock.GetUtcNow())
                throw new LeaseLostException();
            next = next with { LeaseOwner = null, LeaseUntil = null, UpdatedAt = clock.GetUtcNow() };
            var writes = new List<RecordWrite> { new(next, stored.ETag) };
            if (next.State == "retired")
                writes.Add(new(new AuditRecord
                {
                    Id = "retired", ScopeId = operation.ScopeId,
                    Principal = operation.RetirementRequester ?? throw new InvalidOperationException("Retirement requester is missing."),
                    Action = "workbench.retired", RecordedAt = clock.GetUtcNow()
                }));
            if (!await store.CommitAsync(operation.ScopeId, writes, cancellationToken))
                throw new LeaseLostException();
        }
    }
}
