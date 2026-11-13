using System.Text;

namespace Factory.Contracts;

public sealed record EntraPrincipal(string TenantId, string ObjectId);
public sealed record EntraApplicationPrincipal(string TenantId, string ObjectId, string ClientId);

public sealed record PlantWriteRequest
{
    public required string SchemaVersion { get; init; }
    public required string Name { get; init; }

    public void Validate()
    {
        ContractValidation.Version(SchemaVersion);
        ContractValidation.Text(Name, 128, nameof(Name));
    }
}

public sealed record PlantView(string SchemaVersion, string Id, string Name, EntraPrincipal Owner,
    string State, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record Page<T>(string SchemaVersion, IReadOnlyList<T> Items, string? ContinuationToken);

public sealed class ContractValidationException(string message) : Exception(message);

internal static class ContractValidation
{
    public static void Version(string? version) =>
        Require(version == "1", "schemaVersion must be \"1\".");

    public static void Text(string? value, int maximumLength, string field) =>
        Require(!string.IsNullOrWhiteSpace(value) && value.EnumerateRunes().Count() <= maximumLength,
            $"{field} must contain non-whitespace text of at most {maximumLength} characters.");

    public static void Require(bool condition, string message)
    {
        if (!condition)
            throw new ContractValidationException(message);
    }
}
