#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$SubscriptionId,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9_-]{1,90}$')][string]$ResourceGroup,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9]+$')][string]$Location,
    [ValidatePattern('^[a-z][a-z0-9]{2,7}$')][string]$NamePrefix = 'factory',
    [ValidateRange(1000, 1000000)][ValidateScript({ $_ % 1000 -eq 0 })][int]$MaxThroughput = 1000,
    [string[]]$AllowedIpAddresses = @(),
    [string]$TenantConfiguration,
    [string]$OutputPath
)

. (Join-Path $PSScriptRoot '..\Common.ps1')
if (!$TenantConfiguration) { $TenantConfiguration = Join-Path $script:FactoryRoot 'artifacts\bootstrap\tenant-development.json' }
if (!$OutputPath) { $OutputPath = Join-Path $script:FactoryRoot 'artifacts\bootstrap\development.json' }
$tenant = Read-FactoryMetadata $TenantConfiguration 'tenant' 'development'
Assert-FactoryGuid $tenant['clientId'] 'clientId'
Assert-FactoryGuid $tenant['servicePrincipalObjectId'] 'servicePrincipalObjectId'
Assert-FactoryAzureContext -TenantId $tenant.tenantId -SubscriptionId $SubscriptionId
$exists = Test-FactoryResourceGroup $SubscriptionId $ResourceGroup $Location 'nonproduction'
if (Test-Path -LiteralPath $OutputPath) {
    $previous = Read-FactoryMetadata $OutputPath 'development' 'development'
    foreach ($name in @('tenantId', 'clientId', 'servicePrincipalObjectId')) {
        if ($previous[$name] -ne $tenant[$name]) { throw "Existing development deployment uses another $name. Use its original tenant metadata." }
    }
    if ($previous.subscriptionId -ne $SubscriptionId -or $previous.resourceGroup -ne $ResourceGroup -or $previous.namePrefix -ne $NamePrefix) {
        throw 'Output metadata belongs to another development deployment. Use a separate OutputPath.'
    }
}
$network = if ($AllowedIpAddresses.Count) { "allowlist $($AllowedIpAddresses -join ', ')" } else { 'ALL networks; Entra/RBAC still required' }
if (!$PSCmdlet.ShouldProcess("$($tenant.tenantId) / $SubscriptionId / $ResourceGroup / $Location", "Deploy development Cosmos, $network, autoscale ceiling $MaxThroughput RU/s (billable)")) { return }
Initialize-FactoryResourceGroup $SubscriptionId $ResourceGroup $Location 'nonproduction' $exists @('Microsoft.DocumentDB')
$outputs = Invoke-FactoryBackendDeployment $SubscriptionId $ResourceGroup 'development' @{
    namePrefix = $NamePrefix; location = $Location; maxThroughput = $MaxThroughput
    servicePrincipalObjectId = $tenant.servicePrincipalObjectId; allowedIpAddresses = $AllowedIpAddresses
    tags = @{ application = 'Factory'; environment = 'nonproduction' }
}
Write-FactoryMetadata $OutputPath @{
    schemaVersion = 1; kind = 'development'; environment = 'development'; ready = $true
    tenantId = $tenant.tenantId; clientId = $tenant.clientId; servicePrincipalObjectId = $tenant.servicePrincipalObjectId
    subscriptionId = $SubscriptionId; resourceGroup = $ResourceGroup; location = $Location; namePrefix = $NamePrefix
    cosmosEndpoint = $outputs.cosmosEndpoint.value; cosmosDatabase = $outputs.cosmosDatabase.value
    cosmosContainer = $outputs.cosmosContainer.value
}
Write-Host "Development harness deployed. Metadata: $OutputPath"
Write-Host 'Next: run Set-LocalConfiguration.ps1. Azure role/firewall propagation can delay the first data request.'
