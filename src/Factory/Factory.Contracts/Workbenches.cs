using System.Text.Json.Serialization;

namespace Factory.Contracts;

public sealed record WorkbenchRegistration
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AssignmentVersion { get; init; }
    public required string SchemaVersion { get; init; }
    public required string MachineName { get; init; }
    public required string HostVersion { get; init; }
    public required string WorkerVersion { get; init; }
    public required string ProtocolVersion { get; init; }
    public required string[] AssignmentSchemaVersions { get; init; }
    public required string[] Capabilities { get; init; }

    public void Validate()
    {
        ContractValidation.Version(SchemaVersion);
        ContractValidation.Require(AssignmentVersion is null or > 0, "AssignmentVersion must be positive when supplied.");
        ContractValidation.Text(MachineName, 128, nameof(MachineName));
        ContractValidation.Text(HostVersion, 128, nameof(HostVersion));
        ContractValidation.Text(WorkerVersion, 128, nameof(WorkerVersion));
        ContractValidation.Text(ProtocolVersion, 32, nameof(ProtocolVersion));
        ValidateSet(AssignmentSchemaVersions, 16, 32, nameof(AssignmentSchemaVersions));
        ValidateSet(Capabilities, 32, 128, nameof(Capabilities));
    }

    private static void ValidateSet(string[]? values, int count, int length, string field)
    {
        ContractValidation.Require(values is { Length: > 0 } && values.Length <= count,
            $"{field} must contain between 1 and {count} items.");
        foreach (var value in values!)
            ContractValidation.Text(value, length, field);
        ContractValidation.Require(values.Distinct(StringComparer.Ordinal).Count() == values.Length,
            $"{field} must contain distinct items.");
    }
}

public sealed record WorkbenchPresence
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AssignmentVersion { get; init; }
    public required string SchemaVersion { get; init; }
    public required string Status { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    public void Validate()
    {
        ContractValidation.Version(SchemaVersion);
        ContractValidation.Require(AssignmentVersion is null or > 0, "AssignmentVersion must be positive when supplied.");
        ContractValidation.Require(Status is "idle" or "busy" or "notReady", "Unknown presence status.");
        if (Reason is not null)
            ContractValidation.Text(Reason, 256, nameof(Reason));
    }
}

public sealed record WorkbenchView(string SchemaVersion, string Id, string PlantId, string Name,
    EntraApplicationPrincipal Identity, string AdmissionState, string ContactState, DateTimeOffset CreatedAt,
    WorkbenchRegistration? Registration = null, DateTimeOffset? RegisteredAt = null, WorkbenchPresence? Presence = null,
    DateTimeOffset? LastSeenAt = null, string? EnrollmentId = null, string? EnrollmentState = null,
    string? EnrollmentError = null, WorkbenchConfigurationObservation? ConfigurationObservation = null,
    DateTimeOffset? ConfigurationObservedAt = null);
