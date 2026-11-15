using System.Text.Json.Serialization;

namespace Factory.Contracts;

public sealed record ProductionLineSource(string Provider, string Repository, string BaseBranch,
    string WorkloadPath, string ConnectionReference);

public sealed record ProductionLineTasks(string Provider, string Repository, string ConnectionReference,
    string[] RequiredLabels);

public sealed record ProductionLinePolicy(bool AllowShellExecution, bool AllowPublication,
    int MaximumIterations, int TimeoutMinutes);

public sealed record ProductionLineWriteRequest
{
    public required string SchemaVersion { get; init; }
    public required string Name { get; init; }
    public required ProductionLineSource Source { get; init; }
    public required ProductionLineTasks Tasks { get; init; }
    public required ProductionLinePolicy Policy { get; init; }

    public void Validate()
    {
        ContractValidation.Version(SchemaVersion);
        ContractValidation.Text(Name, 128, nameof(Name));
        ContractValidation.Require(Source is not null && Tasks is not null && Policy is not null,
            "Source, tasks and policy are required.");
        ContractValidation.Require(Source!.Provider == "github" && Tasks!.Provider == "github-issues",
            "Only github source and github-issues tasks are supported.");
        Repository(Source.Repository, "Source repository");
        Repository(Tasks!.Repository, "Issue repository");
        ContractValidation.Text(Source.BaseBranch, 128, "baseBranch");
        ContractValidation.Require(!Source.BaseBranch.StartsWith('-') &&
            !Source.BaseBranch.Any(char.IsWhiteSpace) && !Source.BaseBranch.Any(char.IsControl) &&
            !Source.BaseBranch.Contains("..", StringComparison.Ordinal) &&
            !Source.BaseBranch.Contains("@{", StringComparison.Ordinal) && Source.BaseBranch != "@" &&
            !Source.BaseBranch.EndsWith('.') &&
            Source.BaseBranch.Split('/').All(part => part.Length > 0 && !part.StartsWith('.') &&
                !part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) &&
            !Source.BaseBranch.Any(character => "~^:?*[\\\u007f".Contains(character)),
            "baseBranch must be a Git branch name.");
        ContractValidation.Text(Source.WorkloadPath, 256, "workloadPath");
        ContractValidation.Require(!Source.WorkloadPath.StartsWith('/') &&
            Source.WorkloadPath.Split('/').All(part => part.Length > 0 && part is not ("." or "..") &&
                part.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')),
            "workloadPath must be a relative repository folder path without traversal.");
        Connection(Source.ConnectionReference);
        Connection(Tasks.ConnectionReference);
        ContractValidation.Require(Tasks.RequiredLabels is { Length: > 0 and <= 16 },
            "Specify between 1 and 16 required approval/selection labels.");
        foreach (var label in Tasks.RequiredLabels!)
            ContractValidation.Text(label, 64, "requiredLabels");
        ContractValidation.Require(Tasks.RequiredLabels.Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
            Tasks.RequiredLabels.Length, "requiredLabels must be distinct.");
        ContractValidation.Require(Policy!.MaximumIterations is >= 1 and <= 20 &&
            Policy.TimeoutMinutes is >= 1 and <= 240, "Execution limits must be bounded: 1-20 iterations and 1-240 minutes.");
        ContractValidation.Require(!Policy.AllowPublication || Policy.AllowShellExecution,
            "Publication requires shell execution approval.");
    }

    private static void Repository(string value, string field)
    {
        ContractValidation.Text(value, 200, field);
        var parts = value.Split('/');
        ContractValidation.Require(parts.Length == 2 && parts.All(part =>
            part.Length > 0 && part is not ("." or "..") &&
            part.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')),
            $"{field} must be owner/name, not a URL or credential.");
    }

    private static void Connection(string value)
    {
        ContractValidation.Text(value, 128, "connectionReference");
        ContractValidation.Require(value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'),
            "connectionReference must be a logical identifier, not a credential or URL.");
    }
}

public sealed record ProductionLineView(string SchemaVersion, string Id, string Revision, EntraPrincipal Owner,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, ProductionLineWriteRequest Definition);

public sealed record ProductionLineAssignment(string SchemaVersion, long ExpectedVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProductionLineId = null)
{
    public void Validate()
    {
        ContractValidation.Version(SchemaVersion);
        ContractValidation.Require(ExpectedVersion is >= 0 and < long.MaxValue, "expectedVersion must be nonnegative and below Int64.MaxValue.");
        ContractValidation.Require(ProductionLineId is null ||
            Guid.TryParseExact(ProductionLineId, "D", out var id) && id != Guid.Empty,
            "productionLineId must be a nonempty UUID when supplied.");
    }
}

public sealed record ProductionLineBinding(string SchemaVersion, string PlantId,
    long Version, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ProductionLineId);

public sealed record WorkbenchLineConfiguration(string SchemaVersion, string EnrollmentId, string WorkbenchId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PlantId, long AssignmentVersion, long BindingVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ProductionLineView? ProductionLine);

public sealed record WorkbenchConfigurationObservation
{
    public required string SchemaVersion { get; init; }
    public required long AssignmentVersion { get; init; }
    public required long BindingVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProductionLineId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProductionLineRevision { get; init; }
    public required string State { get; init; }
    public required string Reason { get; init; }

    public void Validate()
    {
        ContractValidation.Version(SchemaVersion);
        ContractValidation.Require(AssignmentVersion >= 0 && BindingVersion >= 0, "Configuration versions must not be negative.");
        ContractValidation.Require(State is "unconfigured" or "preparing" or "blocked" or "stopping" or "needsAttention",
            "Unknown configuration state. Execution readiness is not supported in this slice.");
        ContractValidation.Require((ProductionLineId is null) == (ProductionLineRevision is null),
            "Line identity and revision must be supplied together.");
        ContractValidation.Require(State != "unconfigured" || ProductionLineId is null,
            "An unconfigured report cannot acknowledge an applied line.");
        ContractValidation.Text(Reason, 256, nameof(Reason));
    }
}
