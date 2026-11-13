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
public abstract record StoreRecord
{
    public required string Id { get; init; }
    public required string ScopeId { get; init; }
    public required string PlantId { get; init; }
    public string SchemaVersion { get; init; } = "1";
}

public sealed record PlantRecord : StoreRecord
{
    public required PlantView Plant { get; init; }
    public required string Revision { get; init; }
}

public sealed record OwnerRecord : StoreRecord
{
    public required EntraPrincipal Principal { get; init; }
}

public sealed record AccessRecord : StoreRecord
{
    public required EntraPrincipal Principal { get; init; }
    public required string Role { get; init; }
    public string? WorkbenchId { get; init; }
}

public sealed record WorkbenchRecord : StoreRecord
{
    public required string Name { get; init; }
    public required EntraApplicationPrincipal Identity { get; init; }
    public required string AdmissionState { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record RegistrationRecord : StoreRecord
{
    public required WorkbenchRegistration Registration { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
}

public sealed record PresenceRecord : StoreRecord
{
    public required WorkbenchPresence Presence { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
}

public sealed record OperationRecord : StoreRecord
{
    public required string Digest { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
    public PlantView? Plant { get; init; }
    public string? Revision { get; init; }
}

public sealed record AuditRecord : StoreRecord
{
    public required EntraPrincipal Principal { get; init; }
    public required string Action { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
}

public sealed record StoredRecord(StoreRecord Record, string ETag);
public sealed record RecordWrite(StoreRecord Record, string? ExpectedETag = null);
public sealed record RecordQuery(string Type, int Limit, string? PlantId = null, string? ScopeId = null,
    EntraPrincipal? Principal = null, string? AfterId = null);

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

    public static T Require<T>(StoreRecord record) where T : StoreRecord =>
        record is T value && record.SchemaVersion == "1" ? value
            : throw new InvalidOperationException("The stored record type or schema version is unsupported.");
}
