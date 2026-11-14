using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Factory.Contracts;
using Factory.Plant.Service.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace Factory.Plant.Service;

public sealed class EnrollmentOperations(IRecordStore store, PlantOperations plants, TimeProvider clock,
    IDataProtectionProvider protection, ApiSettings api, EnrollmentSettings settings)
{
    private readonly IDataProtector challengeProtector = protection.CreateProtector(
        "Factory.Workbench.Enrollment.Challenge.v1", api.TenantId, api.ClientId);
    private sealed record ChallengeData(string PlantId, string Caller, string Digest, DateTimeOffset ExpiresAt);

    public async Task<EnrollmentChallenge> ChallengeAsync(string plantId, ApiCaller caller, EnrollmentDraft draft,
        CancellationToken cancellationToken)
    {
        Enabled();
        await plants.RequireRegistrarAsync(plantId, caller, cancellationToken);
        draft.Validate();
        using var certificate = Certificate(draft.PublicCertificate, clock.GetUtcNow());
        var expires = clock.GetUtcNow().AddMinutes(5);
        var token = challengeProtector.Protect(JsonSerializer.Serialize(
            new ChallengeData(plantId, caller.Key, Digest(draft), expires), Records.Json));
        return new("1", token, expires);
    }

    public async Task<EnrollmentView> SubmitAsync(string plantId, ApiCaller caller, EnrollmentSubmission submission,
        CancellationToken cancellationToken)
    {
        Enabled();
        await plants.RequireRegistrarAsync(plantId, caller, cancellationToken);
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
            if (challenge.PlantId != plantId || challenge.Caller != caller.Key ||
                challenge.Digest != Digest(draft) || challenge.ExpiresAt <= clock.GetUtcNow() ||
                !rsa.VerifyData(Encoding.UTF8.GetBytes(submission.Challenge),
                    Convert.FromBase64String(submission.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new CryptographicException("Challenge proof does not match.");
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException or ArgumentException)
        {
            throw new ApiException(400, "invalid_enrollment_proof", "A current challenge and matching machine-key signature are required.");
        }
        var scope = Records.EnrollmentScope(plantId, draft.OperationId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var existing = await ReadAsync(plantId, draft.OperationId, cancellationToken);
            if (existing is not null)
            {
                var previous = Records.Require<EnrollmentRecord>(existing.Record);
                if (previous.Requester != caller.Principal || previous.Digest != Digest(draft))
                    throw ApiProblems.Conflict();
                return View(previous);
            }
            var operation = new EnrollmentRecord
            {
                Id = draft.OperationId, ScopeId = scope, PlantId = plantId, Draft = draft,
                Requester = caller.Principal, RequesterClientId = caller.ClientId, Digest = Digest(draft),
                WorkbenchId = $"wb-{Guid.Parse(draft.InstallationId):N}",
                State = "authorized", UpdatedAt = clock.GetUtcNow(), AuthorizedAt = clock.GetUtcNow()
            };
            if (await store.CommitAsync(scope, [new(operation), new(new AuditRecord
            {
                Id = "authorization", ScopeId = scope, PlantId = plantId, Principal = caller.Principal,
                Action = "workbench.enrollment.authorized", RecordedAt = operation.AuthorizedAt
            })], cancellationToken))
                return View(operation);
        }
        throw ApiProblems.Unavailable();
    }

    public async Task<EnrollmentView> GetAsync(string plantId, string operationId, ApiCaller caller, CancellationToken cancellationToken)
    {
        Enabled();
        await plants.RequireRegistrarAsync(plantId, caller, cancellationToken);
        var current = await ReadAsync(plantId, operationId, cancellationToken)
            ?? throw new ApiException(404, "enrollment_not_found", "Enrollment operation was not found.");
        var record = Records.Require<EnrollmentRecord>(current.Record);
        if (record.Requester != caller.Principal)
            throw ApiProblems.Forbidden();
        return View(record);
    }

    public async Task ActivateAsync(string plantId, string operationId, ApiCaller caller, CancellationToken cancellationToken)
    {
        Enabled();
        var current = await ReadAsync(plantId, operationId, cancellationToken)
            ?? throw ApiProblems.Forbidden();
        var enrollment = Records.Require<EnrollmentRecord>(current.Record);
        if (enrollment.State is not ("ready" or "active") ||
            enrollment.ClientId != caller.ClientId || enrollment.ServicePrincipalId != caller.Principal.ObjectId ||
            caller.Principal.TenantId != api.TenantId)
            throw ApiProblems.Forbidden();
        var scope = Records.WorkbenchScope(plantId, enrollment.WorkbenchId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stored = await store.ReadAsync(scope, enrollment.WorkbenchId, cancellationToken)
                ?? throw new ApiException(409, "enrollment_not_ready", "The machine binding is not ready.");
            var workbench = Records.Require<WorkbenchRecord>(stored.Record);
            if (workbench.ScopeId != scope || workbench.PlantId != plantId || workbench.EnrollmentId != operationId ||
                workbench.Identity != new EntraApplicationPrincipal(api.TenantId, caller.Principal.ObjectId, caller.ClientId))
                throw ApiProblems.Forbidden();
            if (workbench.AdmissionState == "active")
                break;
            if (workbench.AdmissionState != "pending")
                throw ApiProblems.Forbidden();
            await plants.RequireRegistrarAsync(plantId, new(enrollment.Requester, enrollment.RequesterClientId), cancellationToken);
            if (await store.CommitAsync(scope,
                [new(workbench with { AdmissionState = "active" }, stored.ETag),
                 new(new AuditRecord { Id = "enrollment-activated", ScopeId = scope, PlantId = plantId,
                     Principal = caller.Principal, Action = "workbench.activated", RecordedAt = clock.GetUtcNow() })], cancellationToken))
                break;
            if (attempt == 2)
                throw ApiProblems.Unavailable();
        }
        if (enrollment.State != "active" && !await store.CommitAsync(enrollment.ScopeId, [new(enrollment with
                { State = "active", UpdatedAt = clock.GetUtcNow() }, current.ETag)], cancellationToken))
            throw ApiProblems.Unavailable();
    }

    public async Task<StoredRecord?> ReadAsync(string plantId, string id, CancellationToken cancellationToken)
    {
        var scope = Records.EnrollmentScope(plantId, id);
        var record = await store.ReadAsync(scope, id, cancellationToken);
        if (record is not null && (record.Record is not EnrollmentRecord ||
            record.Record.ScopeId != scope || record.Record.Id != id || record.Record.PlantId != plantId))
            throw new InvalidOperationException("Stored enrollment address is inconsistent.");
        if (record is not null)
            Records.Require<EnrollmentRecord>(record.Record);
        return record;
    }

    public static EnrollmentView View(EnrollmentRecord record) => new("1", record.Id, record.PlantId,
        record.WorkbenchId, record.State, record.UpdatedAt, record.ClientId, record.ServicePrincipalId, record.Error);

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
