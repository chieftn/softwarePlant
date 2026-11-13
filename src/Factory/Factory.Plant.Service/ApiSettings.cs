namespace Factory.Plant.Service;

public sealed class ApiSettings
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string CosmosEndpoint { get; set; } = "";
    public string CosmosDatabase { get; set; } = "plant";
    public string CosmosContainer { get; set; } = "records";
    public string DataCredential { get; set; } = "";
    public string DataIdentityClientId { get; set; } = "";
    public string[] SupportedProtocolVersions { get; set; } = [];
    public string[] SupportedAssignmentSchemaVersions { get; set; } = [];

    public string Issuer => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    public IReadOnlyList<string> Validate(IHostEnvironment environment)
    {
        var errors = new List<string>();
        if (!Guid.TryParseExact(TenantId, "D", out var tenant) || tenant == Guid.Empty)
            errors.Add("Factory:TenantId");
        if (!Guid.TryParseExact(ClientId, "D", out var client) || client == Guid.Empty)
            errors.Add("Factory:ClientId");
        if (!Uri.TryCreate(CosmosEndpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            errors.Add("Factory:CosmosEndpoint");
        if (string.IsNullOrWhiteSpace(CosmosDatabase) || string.IsNullOrWhiteSpace(CosmosContainer))
            errors.Add("Factory:CosmosDatabase/CosmosContainer");
        if (DataCredential is not ("ManagedIdentity" or "AzureCli") ||
            (DataCredential == "AzureCli" && !environment.IsDevelopment()))
            errors.Add("Factory:DataCredential (AzureCli is Development-only)");
        if (DataCredential == "ManagedIdentity" && (!Guid.TryParseExact(DataIdentityClientId, "D", out var identity) || identity == Guid.Empty))
            errors.Add("Factory:DataIdentityClientId");
        if (!ValidVersions(SupportedProtocolVersions))
            errors.Add("Factory:SupportedProtocolVersions");
        if (!ValidVersions(SupportedAssignmentSchemaVersions))
            errors.Add("Factory:SupportedAssignmentSchemaVersions");
        return errors;
    }

    private static bool ValidVersions(string[]? values) =>
        values is { Length: > 0 and <= 16 } &&
        values.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 32) &&
        values.Distinct(StringComparer.Ordinal).Count() == values.Length;
}
