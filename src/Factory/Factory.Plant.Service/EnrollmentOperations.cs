using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Factory.Contracts;
using Factory.Plant.Service.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace Factory.Plant.Service;

public sealed partial class EnrollmentOperations(IRecordStore store, PlantOperations plants, TimeProvider clock,
    IDataProtectionProvider protection, ApiSettings api, EnrollmentSettings settings, Pagination pagination)
{
    private readonly IDataProtector challengeProtector = protection.CreateProtector(
        "Factory.Workbench.Enrollment.Challenge.v2", api.TenantId, api.ClientId);
    private sealed record ChallengeData(string Caller, string Digest, DateTimeOffset ExpiresAt);

    public Task<EnrollmentChallenge> ChallengeAsync(ApiCaller caller, EnrollmentDraft draft,
        CancellationToken cancellationToken)
    {
        Enabled();
        cancellationToken.ThrowIfCancellationRequested();
        caller.RequireEnrollmentAdministrator();
        draft.Validate();
        using var certificate = Certificate(draft.PublicCertificate, clock.GetUtcNow());
        var expires = clock.GetUtcNow().AddMinutes(5);
        var token = challengeProtector.Protect(JsonSerializer.Serialize(
            new ChallengeData(caller.Key, Digest(draft), expires), Records.Json));
        return Task.FromResult(new EnrollmentChallenge("1", token, expires));
    }

    public async Task<EnrollmentView> SubmitAsync(ApiCaller caller, EnrollmentSubmission submission,
        CancellationToken cancellationToken)
    {
        Enabled();
        caller.RequireEnrollmentAdministrator();
        submission.Draft.Validate();
        var draft = submission.Draft;
        using var certificate = Certificate(draft.PublicCertificate, clock.GetUtcNow());
        ChallengeData challenge;
        try
        {
            challenge = JsonSerializer.Deserialize<ChallengeData>(
                challengeProtector.Unprotect(submission.Challenge), Records.Json)
                ?? throw new CryptographicException("Empty challenge.");
            using var rsa = certificate.GetRSAPublicKey()!;
            if (challenge.Caller != caller.Key ||
                challenge.Digest != Digest(draft) || challenge.ExpiresAt <= clock.GetUtcNow() ||
                !rsa.VerifyData(Encoding.UTF8.GetBytes(submission.Challenge),
                    Convert.FromBase64String(submission.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new CryptographicException("Challenge proof does not match.");
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException or ArgumentException)
        {
            throw new ApiException(400, "invalid_enrollment_proof", "A current challenge and matching machine-key signature are required.");
        }
        var scope = Records.EnrollmentScope(draft.OperationId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var existing = await ReadAsync(draft.OperationId, cancellationToken);
            if (existing is not null)
            {
                var previous = Records.Require<EnrollmentRecord>(existing.Record);
                if (previous.Requester != caller.Principal || previous.Digest != Digest(draft))
                    throw ApiProblems.Conflict();
                return View(previous);
            }
            var operation = new EnrollmentRecord
            {
                Id = draft.OperationId, ScopeId = scope, Draft = draft,
                Requester = caller.Principal, RequesterClientId = caller.ClientId, Digest = Digest(draft),
                WorkbenchId = $"wb-{Guid.Parse(draft.InstallationId):N}",
                State = "authorized", UpdatedAt = clock.GetUtcNow(), AuthorizedAt = clock.GetUtcNow()
            };
            if (await store.CommitAsync(scope, [new(operation), new(new AuditRecord
            {
                Id = "authorization", ScopeId = scope, Principal = caller.Principal,
                Action = "workbench.enrollment.authorized", RecordedAt = operation.AuthorizedAt
            })], cancellationToken))
                return View(operation);
        }
        throw ApiProblems.Unavailable();
    }

    public async Task<EnrollmentView> GetAsync(string operationId, ApiCaller caller, CancellationToken cancellationToken)
    {
        caller.RequireEnrollmentAdministrator();
        var current = await ReadAsync(operationId, cancellationToken)
            ?? throw new ApiException(404, "enrollment_not_found", "Enrollment operation was not found.");
        var record = Records.Require<EnrollmentRecord>(current.Record);
        return await InventoryViewAsync(record, cancellationToken);
    }

    public async Task ActivateAsync(string operationId, ApiCaller caller, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stored = await RequireMachineAsync(operationId, caller, cancellationToken);
            var enrollment = Records.Require<EnrollmentRecord>(stored.Record);
            if (enrollment.State == "active")
                return;
            if (await store.CommitAsync(enrollment.ScopeId,
                [new(enrollment with { State = "active", UpdatedAt = clock.GetUtcNow() }, stored.ETag),
                 new(new AuditRecord { Id = "enrollment-activated", ScopeId = enrollment.ScopeId,
                     Principal = caller.Principal, Action = "workbench.activated", RecordedAt = clock.GetUtcNow() })], cancellationToken))
                return;
        }
        throw ApiProblems.Unavailable();
    }

    public async Task<StoredRecord?> ReadAsync(string id, CancellationToken cancellationToken)
    {
        var scope = Records.EnrollmentScope(id);
        var record = await store.ReadAsync(scope, id, cancellationToken);
        if (record is not null && (record.Record is not EnrollmentRecord ||
            record.Record.ScopeId != scope || record.Record.Id != id))
            throw new InvalidOperationException("Stored enrollment address is inconsistent.");
        if (record is not null)
            Records.Require<EnrollmentRecord>(record.Record);
        return record;
    }

    public static EnrollmentView View(EnrollmentRecord record) => new("1", record.Id, record.AssignedPlantId,
        record.WorkbenchId, record.State, record.UpdatedAt, record.ClientId, record.ServicePrincipalId, record.Error,
        record.Draft.MachineName, record.AssignmentVersion);

    private void Enabled()
    {
        if (!settings.Enabled)
            throw new ApiException(503, "enrollment_disabled", "Workbench enrollment is not configured on this plant service.");
    }

    private static string Digest(EnrollmentDraft draft) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(draft, Records.Json)));

    public static X509Certificate2 Certificate(string encoded, DateTimeOffset now)
    {
        X509Certificate2 certificate;
        try { certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(encoded)); }
        catch (Exception error) when (error is FormatException or CryptographicException)
        { throw new ApiException(400, "invalid_machine_certificate", "Supply a public RSA X.509 certificate."); }
        using var rsa = certificate.GetRSAPublicKey();
        if (certificate.HasPrivateKey || rsa is null || rsa.KeySize < 2048 ||
            certificate.NotBefore.ToUniversalTime() > now.UtcDateTime.AddMinutes(5) ||
            certificate.NotAfter.ToUniversalTime() <= now.UtcDateTime ||
            certificate.NotAfter.ToUniversalTime() > now.UtcDateTime.AddDays(200))
        {
            certificate.Dispose();
            throw new ApiException(400, "invalid_machine_certificate", "Machine certificate must use RSA >= 2048 and be current with a lifetime of at most 200 days.");
        }
        return certificate;
    }
}
