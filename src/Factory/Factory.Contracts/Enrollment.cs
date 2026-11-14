using System.Text.Json.Serialization;

namespace Factory.Contracts;

public sealed record EnrollmentDraft
{
    public required string SchemaVersion { get; init; }
    public required string OperationId { get; init; }
    public required string InstallationId { get; init; }
    public required string MachineName { get; init; }
    public required string PublicCertificate { get; init; }

    public void Validate()
    {
        ContractValidation.Version(SchemaVersion);
        ContractValidation.Require(Guid.TryParseExact(OperationId, "D", out var operation) && operation != Guid.Empty,
            "operationId must be a nonempty UUID.");
        ContractValidation.Require(Guid.TryParseExact(InstallationId, "D", out var installation) && installation != Guid.Empty,
            "installationId must be a nonempty UUID.");
        ContractValidation.Text(MachineName, 128, nameof(MachineName));
        ContractValidation.Text(PublicCertificate, 16384, nameof(PublicCertificate));
    }
}

public sealed record EnrollmentChallengeRequest(EnrollmentDraft Draft);
public sealed record EnrollmentChallenge(string SchemaVersion, string Challenge, DateTimeOffset ExpiresAt);
public sealed record EnrollmentSubmission(EnrollmentDraft Draft, string Challenge, string Signature);
public sealed record EnrollmentView(string SchemaVersion, string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PlantId, string WorkbenchId,
    string State, DateTimeOffset UpdatedAt, string? ClientId = null, string? ServicePrincipalId = null, string? Error = null,
    string? MachineName = null, long AssignmentVersion = 0, WorkbenchPresence? Presence = null, DateTimeOffset? LastSeenAt = null);
public sealed record WorkbenchBinding(string SchemaVersion, string InstallationId, string? PlantId,
    string WorkbenchId, string TenantId, string ClientId, string ServicePrincipalId, string CertificateThumbprint,
    string EnrollmentId);
public sealed record EnrollmentAssignment(string SchemaVersion, long ExpectedVersion, string? PlantId = null)
{
    public void Validate()
    {
        ContractValidation.Version(SchemaVersion);
        ContractValidation.Require(ExpectedVersion is >= 0 and < long.MaxValue, "ExpectedVersion must be nonnegative and below Int64.MaxValue.");
        ContractValidation.Require(PlantId is null || Guid.TryParseExact(PlantId, "D", out var id) && id != Guid.Empty,
            "PlantId must be a nonempty UUID or omitted to unassign.");
    }
}
public sealed record WorkbenchAssignment(string SchemaVersion, string EnrollmentId, string WorkbenchId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PlantId, long Version);

public sealed record WorkbenchInstallationProfile(string SchemaVersion, string PlantServiceUrl,
    string TenantId, string ApiClientId, string InstallerClientId)
{
    public void Validate()
    {
        ContractValidation.Version(SchemaVersion);
        foreach (var value in new[] { TenantId, ApiClientId, InstallerClientId })
            ContractValidation.Require(Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty,
                "Installation profile IDs must be nonempty UUIDs.");
        ContractValidation.Require(Uri.TryCreate(PlantServiceUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme == "https" && uri.AbsolutePath == "/" && uri.UserInfo.Length == 0 &&
            uri.Query.Length == 0 && uri.Fragment.Length == 0, "Installation profile must name an HTTPS origin.");
    }
}
