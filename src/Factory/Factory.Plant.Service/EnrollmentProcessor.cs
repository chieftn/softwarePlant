using System.Net;
using System.Text.Json;
using Azure.Identity;
using Factory.Contracts;
using Factory.Plant.Service.Storage;

namespace Factory.Plant.Service;

public sealed partial class EnrollmentProcessor(IRecordStore store, EnrollmentOperations operations,
    IDirectoryProvisioner directory, TimeProvider clock, ApiSettings api, ILogger<EnrollmentProcessor> logger)
{
    private sealed class LeaseLostException : InvalidOperationException;

    public async Task ProcessAsync(string operationId, CancellationToken cancellationToken)
    {
        var stored = await operations.ReadAsync(operationId, cancellationToken);
        if (stored?.Record is EnrollmentRecord { State: "revoking" })
        {
            await RetireAsync(stored, cancellationToken);
            return;
        }
        if (stored?.Record is not EnrollmentRecord operation ||
            operation.State is not ("authorized" or "provisioning") ||
            operation.LeaseUntil > clock.GetUtcNow() || operation.RetryAt > clock.GetUtcNow())
            return;
        var owner = Guid.NewGuid().ToString("N");
        operation = operation with { State = "provisioning", LeaseOwner = owner, LeaseUntil = clock.GetUtcNow().AddMinutes(2) };
        if (!await store.CommitAsync(operation.ScopeId, [new(operation, stored.ETag)], cancellationToken))
            return;
        stored = await operations.ReadAsync(operationId, cancellationToken)
            ?? throw new InvalidOperationException("Reserved enrollment disappeared.");
        operation = Records.Require<EnrollmentRecord>(stored.Record);
        if (operation.LeaseOwner != owner || operation.State != "provisioning")
            return;
        using var directoryBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        directoryBudget.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var reservationScope = Records.InstallationScope(operation.Draft.InstallationId);
            var reservation = await store.ReadAsync(reservationScope, "enrollment", cancellationToken);
            if (reservation is null)
            {
                if (!await store.CommitAsync(reservationScope, [new(new EnrollmentReservationRecord
                {
                    Id = "enrollment", ScopeId = reservationScope, EnrollmentId = operation.Id
                })], cancellationToken))
                    throw new LeaseLostException();
            }
            else if (reservation.Record is not EnrollmentReservationRecord claim)
                throw new DirectoryProvisioningException("The machine enrollment reservation is inconsistent.");
            else if (claim.EnrollmentId != operation.Id)
            {
                var previous = await operations.ReadAsync(claim.EnrollmentId, cancellationToken);
                if (previous?.Record is not EnrollmentRecord { State: "retired" })
                    throw new DirectoryProvisioningException("This machine already has another enrollment operation. Resume or retire that operation first.");
                if (!await store.CommitAsync(reservationScope,
                    [new(claim with { EnrollmentId = operation.Id }, reservation.ETag)], cancellationToken))
                    throw new LeaseLostException();
            }
            if (operation.ApplicationObjectId is null)
            {
                var marker = $"Factory Workbench {api.ClientId} {operation.Id}";
                DirectoryApplication? application;
                if (operation.ApplicationCreateStartedAt is null)
                {
                    await Save(operation with { ApplicationCreateStartedAt = clock.GetUtcNow() }, false);
                    application = await directory.CreateApplicationAsync(marker, operation.Draft.PublicCertificate, directoryBudget.Token);
                }
                else
                    application = await directory.FindApplicationAsync(marker, operation.Draft.PublicCertificate, directoryBudget.Token);
                if (application is null)
                {
                    await Uncertain(operation.ApplicationCreateStartedAt!.Value);
                    return;
                }
                await Save(operation with { ApplicationObjectId = application.ObjectId, ClientId = application.ClientId }, true);
                return;
            }
            if (operation.ServicePrincipalId is null)
            {
                DirectoryPrincipal? principal;
                if (operation.PrincipalCreateStartedAt is null)
                {
                    await Save(operation with { PrincipalCreateStartedAt = clock.GetUtcNow() }, false);
                    principal = await directory.CreatePrincipalAsync(operation.ClientId!, directoryBudget.Token);
                }
                else
                {
                    principal = await directory.FindPrincipalAsync(operation.ClientId!, directoryBudget.Token);
                    if (principal is null && operation.PrincipalCreateRetryRequested)
                    {
                        var marker = $"Factory Workbench {api.ClientId} {operation.Id}";
                        var application = await directory.FindApplicationAsync(marker,
                            operation.Draft.PublicCertificate, directoryBudget.Token);
                        if (application?.ObjectId != operation.ApplicationObjectId ||
                            application.ClientId != operation.ClientId)
                            throw new DirectoryProvisioningException("Principal retry requires the same owned application and authorized public certificate.");
                        // The signed retry authorizes another principal attempt for this existing appId, never another application.
                        await Save(operation with
                        {
                            PrincipalCreateStartedAt = null, PrincipalCreateRetryRequested = false
                        }, true);
                        return;
                    }
                }
                if (principal is null)
                {
                    await Uncertain(operation.PrincipalCreateStartedAt!.Value);
                    return;
                }
                await Save(operation with { ServicePrincipalId = principal.ObjectId, PrincipalCreateRetryRequested = false }, true);
                return;
            }
            await directory.EnsureConnectRoleAsync(operation.ServicePrincipalId, directoryBudget.Token);
            await Save(operation with { State = "ready", Error = null }, true);
        }
        catch (LeaseLostException)
        {
            logger.LogWarning("Enrollment {Operation} lost its reservation; no further directory mutations were attempted.", operationId);
        }
        catch (ApiException error) when (error.Status == 400)
        {
            await Save(operation with { State = "needsAttention", Error = "The recorded machine certificate is no longer valid. Authorized credential recovery is required." }, true);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is (HttpRequestException or AuthenticationFailedException or
            DirectoryProvisioningException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException or
            OperationCanceledException))
        {
            logger.LogWarning(error, "Enrollment provisioning step failed for {Operation}.", operationId);
            var failures = operation.Failures + 1;
            var attention = error is DirectoryProvisioningException or AuthenticationFailedException or JsonException or
                FormatException or InvalidOperationException or KeyNotFoundException ||
                failures >= 12 || error is HttpRequestException { StatusCode: HttpStatusCode.Forbidden };
            await Save(operation with
            {
                State = attention ? "needsAttention" : "provisioning", Failures = failures,
                Error = attention ? "Provisioning requires administrator reconciliation. Check directory consent, object ownership, and service logs."
                    : "Directory provisioning is temporarily unavailable; the recorded operation will be retried.",
                RetryAt = clock.GetUtcNow().AddSeconds(10)
            }, true);
        }

        async Task Uncertain(DateTimeOffset started)
        {
            var attention = clock.GetUtcNow() - started > TimeSpan.FromMinutes(2);
            await Save(operation with
            {
                State = attention ? "needsAttention" : "provisioning",
                Error = attention ? "Directory create outcome is uncertain. Reconcile this operation; do not submit a replacement."
                    : "Reconciling a previously started directory create; no duplicate identity will be created.",
                RetryAt = clock.GetUtcNow().AddSeconds(10)
            }, true);
        }

        async Task Save(EnrollmentRecord next, bool release)
        {
            if (operation.LeaseOwner != owner || operation.LeaseUntil <= clock.GetUtcNow())
                throw new LeaseLostException();
            next = next with
            {
                UpdatedAt = clock.GetUtcNow(),
                LeaseOwner = release ? null : owner,
                LeaseUntil = release ? null : operation.LeaseUntil
            };
            if (!await store.CommitAsync(next.ScopeId, [new(next, stored.ETag)], cancellationToken))
                throw new LeaseLostException();
            stored = await operations.ReadAsync(operationId, cancellationToken)
                ?? throw new InvalidOperationException("Enrollment checkpoint disappeared.");
            operation = Records.Require<EnrollmentRecord>(stored.Record);
            if (!release && (operation.State != "provisioning" || operation.LeaseOwner != owner))
                throw new LeaseLostException();
        }
    }
}

public sealed class EnrollmentWorker(IServiceScopeFactory scopes, EnrollmentSettings settings,
    ILogger<EnrollmentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled && !Guid.TryParse(settings.ProvisioningClientId, out _))
            return;
        string? after = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IRecordStore>();
                var page = await store.QueryAsync(new("enrollment", 20, AfterId: after,
                    States: settings.Enabled ? ["authorized", "provisioning", "revoking"] : ["revoking"]), stoppingToken);
                if (page.Count > 0)
                {
                    var processor = scope.ServiceProvider.GetRequiredService<EnrollmentProcessor>();
                    foreach (var record in page)
                        await processor.ProcessAsync(record.Id, stoppingToken);
                }
                after = page.Count == 20 ? page[^1].Id : null;
            }
            catch (Exception error) when (error is Microsoft.Azure.Cosmos.CosmosException or InvalidOperationException or ApiException)
            {
                logger.LogError(error, "Enrollment processing could not advance; durable operations remain pending.");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
