namespace Factory.Plant.Service;

public sealed class EnrollmentSettings
{
    public bool Enabled { get; set; }
    public string InstallerClientId { get; set; } = "";
    public string ProvisioningCredential { get; set; } = "";
    public string ProvisioningClientId { get; set; } = "";
    public string ProvisioningObjectId { get; set; } = "";
    public string ProvisioningClientSecret { get; set; } = "";

    public void Validate(IHostEnvironment environment)
    {
        if (!Enabled && string.IsNullOrEmpty(ProvisioningClientId))
            return;
        if (!Guid.TryParseExact(InstallerClientId, "D", out var installer) || installer == Guid.Empty ||
            !Guid.TryParseExact(ProvisioningClientId, "D", out var client) || client == Guid.Empty ||
            !Guid.TryParseExact(ProvisioningObjectId, "D", out var principal) || principal == Guid.Empty ||
            ProvisioningCredential is not ("ManagedIdentity" or "ClientSecret") ||
            ProvisioningCredential == "ClientSecret" && (!environment.IsDevelopment() ||
                string.IsNullOrWhiteSpace(ProvisioningClientSecret) ||
                ProvisioningClientSecret.StartsWith("@Microsoft.KeyVault(", StringComparison.Ordinal)))
            throw new InvalidOperationException("Factory:Enrollment requires explicit installer/provisioner IDs and a supported provisioning credential; ClientSecret is Development-only.");
    }
}
