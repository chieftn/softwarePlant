using System.Text.Json;
using System.Text.Json.Serialization;
using Factory.Contracts;

namespace Factory.Plant.Service.Storage;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PlantRecord), "plant")]
[JsonDerivedType(typeof(OwnerRecord), "owner")]
[JsonDerivedType(typeof(AccessRecord), "access")]
[JsonDerivedType(typeof(WorkbenchRecord), "workbench")]
[JsonDerivedType(typeof(RegistrationRecord), "registration")]
[JsonDerivedType(typeof(PresenceRecord), "presence")]
[JsonDerivedType(typeof(OperationRecord), "operation")]
[JsonDerivedType(typeof(AuditRecord), "audit")]
[JsonDerivedType(typeof(EnrollmentRecord), "enrollment")]
[JsonDerivedType(typeof(EnrollmentReservationRecord), "enrollmentReservation")]
[JsonDerivedType(typeof(ProductionLineRecord), "productionLine")]
[JsonDerivedType(typeof(ProductionLineRevisionRecord), "productionLineRevision")]
[JsonDerivedType(typeof(ProductionLineBindingRecord), "productionLineBinding")]
[JsonDerivedType(typeof(ConfigurationObservationRecord), "configurationObservation")]
public abstract record StoreRecord
{
    public required string Id { get; init; }
    public required string ScopeId { get; init; }
    public string SchemaVersion { get; init; } = "1";
}

public abstract record PlantScopedRecord : StoreRecord
{
    public required string PlantId { get; init; }
}

public sealed record PlantRecord : PlantScopedRecord
{
    public required PlantView Plant { get; init; }
    public required string Revision { get; init; }
}

public sealed record OwnerRecord : PlantScopedRecord
{
    public required EntraPrincipal Principal { get; init; }
}

public sealed record AccessRecord : PlantScopedRecord
{
    public required EntraPrincipal Principal { get; init; }
    public required string Role { get; init; }
    public string? WorkbenchId { get; init; }
}

public sealed record WorkbenchRecord : PlantScopedRecord
{
    public string? EnrollmentId { get; init; }
    public long AssignmentVersion { get; init; }
    public required string Name { get; init; }
    public required EntraApplicationPrincipal Identity { get; init; }
    public required string AdmissionState { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record EnrollmentRecord : StoreRecord
{
    public string? AssignedPlantId { get; init; }
    public long AssignmentVersion { get; init; }
    public required EnrollmentDraft Draft { get; init; }
    public required EntraPrincipal Requester { get; init; }
    public required string RequesterClientId { get; init; }
    public required string Digest { get; init; }
    public required string WorkbenchId { get; init; }
    public required string State { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required DateTimeOffset AuthorizedAt { get; init; }
    public string? ApplicationObjectId { get; init; }
    public string? ClientId { get; init; }
    public string? ServicePrincipalId { get; init; }
    public DateTimeOffset? ApplicationCreateStartedAt { get; init; }
    public DateTimeOffset? PrincipalCreateStartedAt { get; init; }
    public bool PrincipalCreateRetryRequested { get; init; }
    public string? LeaseOwner { get; init; }
    public DateTimeOffset? LeaseUntil { get; init; }
    public DateTimeOffset? RetryAt { get; init; }
    public string? Error { get; init; }
    public int Failures { get; init; }
    public EntraPrincipal? RetirementRequester { get; init; }
}
public sealed record EnrollmentReservationRecord : StoreRecord
{
    public required string EnrollmentId { get; init; }
}
public sealed record RegistrationRecord : PlantScopedRecord
{
    public string? EnrollmentId { get; init; }
    public long AssignmentVersion { get; init; }
    public required WorkbenchRegistration Registration { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
}

public sealed record PresenceRecord : StoreRecord
{
    public string? PlantId { get; init; }
    public string? EnrollmentId { get; init; }
    public long AssignmentVersion { get; init; }
    public required WorkbenchPresence Presence { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
}

public sealed record OperationRecord : StoreRecord
{
    public string? PlantId { get; init; }
    public required string Digest { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
    public PlantView? Plant { get; init; }
    public string? Revision { get; init; }
    public ProductionLineView? ProductionLine { get; init; }
}

public sealed record ProductionLineRecord : StoreRecord
{
    public required EntraPrincipal Principal { get; init; }
    public required ProductionLineView Line { get; init; }
}

public sealed record ProductionLineRevisionRecord : StoreRecord
{
    public required ProductionLineView Line { get; init; }
}

public sealed record ProductionLineBindingRecord : PlantScopedRecord
{
    public required ProductionLineBinding Binding { get; init; }
}

public sealed record ConfigurationObservationRecord : StoreRecord
{
    public required string EnrollmentId { get; init; }
    public string? PlantId { get; init; }
    public required WorkbenchConfigurationObservation Observation { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
}

public sealed record AuditRecord : StoreRecord
{
    public string? PlantId { get; init; }
    public required EntraPrincipal Principal { get; init; }
    public required string Action { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
}

public sealed record StoredRecord(StoreRecord Record, string ETag);
public sealed record RecordWrite(StoreRecord Record, string? ExpectedETag = null);
public sealed record RecordQuery(string Type, int Limit, string? PlantId = null, string? ScopeId = null,
    EntraPrincipal? Principal = null, string? AfterId = null, string[]? States = null);

public interface IRecordStore
{
    Task<StoredRecord?> ReadAsync(string scopeId, string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoreRecord>> QueryAsync(RecordQuery query, CancellationToken cancellationToken);
    Task<bool> CommitAsync(string scopeId, IReadOnlyList<RecordWrite> writes, CancellationToken cancellationToken);
}

public static class Records
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string PlantScope(string plantId) => $"plant:{plantId}:settings";
    public static string WorkbenchScope(string plantId, string workbenchId) => $"plant:{plantId}:workbench:{workbenchId}";
    public static string EnrollmentScope(string operationId) => $"enrollment:{operationId}";
    public static string InstallationScope(string installationId) => $"installation:{installationId}";
    public static string ProductionLineScope(string lineId) => $"line:{lineId}";

    public static T Require<T>(StoreRecord record) where T : StoreRecord =>
        record is T value && record.SchemaVersion == "1" ? value
            : throw new InvalidOperationException("The stored record type or schema version is unsupported.");
}
