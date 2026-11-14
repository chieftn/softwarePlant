#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$TenantId,
    [Parameter(Mandatory)][string]$SubscriptionId,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9_-]{1,90}$')][string]$ResourceGroup,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9]+$')][string]$Location,
    [ValidatePattern('^[a-z][a-z0-9]{2,7}$')][string]$NamePrefix = 'factory',
    [ValidateRange(1000, 1000000)][ValidateScript({ $_ % 1000 -eq 0 })][int]$MaxThroughput = 1000,
    [string]$VnetAddressPrefix = '10.42.0.0/24',
    [string]$AppSubnetPrefix = '10.42.0.0/26',
    [string]$EndpointSubnetPrefix = '10.42.0.64/27',
    [string]$OutputPath
)

. (Join-Path $PSScriptRoot '..\Common.ps1')
Assert-FactoryAzureContext -TenantId $TenantId -SubscriptionId $SubscriptionId
$exists = Test-FactoryResourceGroup $SubscriptionId $ResourceGroup $Location 'production'
if (!$OutputPath) { $OutputPath = Join-Path $script:FactoryRoot 'artifacts\bootstrap\production.json' }
if (Test-Path -LiteralPath $OutputPath) {
    $previous = Read-FactoryMetadata $OutputPath 'production' 'production'
    if ($previous.tenantId -ne $TenantId -or $previous.subscriptionId -ne $SubscriptionId -or
        $previous.resourceGroup -ne $ResourceGroup -or $previous.namePrefix -ne $NamePrefix) {
        throw 'Output metadata belongs to another production deployment. Use a separate OutputPath.'
    }
}
if (!$PSCmdlet.ShouldProcess("$TenantId / $SubscriptionId / $ResourceGroup / $Location", "Deploy private production Cosmos backend, autoscale ceiling $MaxThroughput RU/s (billable)")) { return }
Initialize-FactoryResourceGroup $SubscriptionId $ResourceGroup $Location 'production' $exists @(
    'Microsoft.DocumentDB', 'Microsoft.Network', 'Microsoft.ManagedIdentity'
)
$outputs = Invoke-FactoryBackendDeployment $SubscriptionId $ResourceGroup 'production' @{
    namePrefix = $NamePrefix; location = $Location; maxThroughput = $MaxThroughput
    vnetAddressPrefix = $VnetAddressPrefix; appSubnetPrefix = $AppSubnetPrefix; endpointSubnetPrefix = $EndpointSubnetPrefix
    tags = @{ application = 'Factory'; environment = 'production' }
}
Write-FactoryMetadata $OutputPath @{
    schemaVersion = 1; kind = 'production'; environment = 'production'; ready = $true
    tenantId = $TenantId; subscriptionId = $SubscriptionId; resourceGroup = $ResourceGroup
    location = $Location; namePrefix = $NamePrefix
    cosmosEndpoint = $outputs.cosmosEndpoint.value; cosmosDatabase = $outputs.cosmosDatabase.value
    cosmosContainer = $outputs.cosmosContainer.value
    dataIdentityResourceId = $outputs.dataIdentityResourceId.value
    dataIdentityClientId = $outputs.dataIdentityClientId.value
    dataIdentityPrincipalId = $outputs.dataIdentityPrincipalId.value
    appSubnetId = $outputs.appSubnetId.value
}
Write-Host "Production backend deployed. Metadata: $OutputPath"
Write-Host 'No website or code was deployed. Cosmos is private; the later service must join the application subnet and use the output managed identity.'
