#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:FactoryRoot = Split-Path $PSScriptRoot -Parent

function Invoke-FactoryAz {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $PSNativeCommandUseErrorActionPreference = $false
    $result = & az @Arguments --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) {
        throw 'Azure CLI failed. Resolve the error above before rerunning; no automatic login or retry is attempted.'
    }
    if ($result) {
        $result | Out-String | ConvertFrom-Json -AsHashtable
    }
}

function Invoke-FactoryAzWithJson {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][hashtable]$Document
    )

    $path = [IO.Path]::GetTempFileName()
    try {
        # Only non-secret Graph bodies and ARM parameters may use this helper.
        $Document | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $path -Encoding utf8
        Invoke-FactoryAz -Arguments ($Arguments + "@$path")
    }
    finally {
        Remove-Item -LiteralPath $path -Force
    }
}

function Invoke-FactoryGraph {
    param(
        [ValidateSet('GET', 'POST', 'PATCH')][string]$Method = 'GET',
        [Parameter(Mandatory)][string]$Path,
        [hashtable]$Body
    )

    $arguments = @('rest', '--method', $Method, '--url', "https://graph.microsoft.com/v1.0/$Path")
    if ($null -ne $Body) {
        Invoke-FactoryAzWithJson -Arguments ($arguments + '--body') -Document $Body
    }
    else {
        Invoke-FactoryAz -Arguments $arguments
    }
}

function Assert-FactoryGuid {
    param([object]$Value, [string]$Name)

    $parsed = [guid]::Empty
    if (![guid]::TryParseExact([string]$Value, 'D', [ref]$parsed) -or $parsed -eq [guid]::Empty) {
        throw "$Name must be a nonempty GUID."
    }
}

function Assert-FactoryAzureContext {
    param([string]$TenantId, [string]$SubscriptionId)

    Assert-FactoryGuid $TenantId 'TenantId'
    $cloud = Invoke-FactoryAz @('cloud', 'show')
    if ($cloud.name -ne 'AzureCloud') {
        throw 'These templates target AzureCloud, not a sovereign cloud.'
    }
    $account = Invoke-FactoryAz @('account', 'show')
    if ($account.tenantId -ne $TenantId) {
        throw "Wrong active tenant. Run az login --tenant $TenantId yourself, then rerun."
    }
    if ($SubscriptionId) {
        Assert-FactoryGuid $SubscriptionId 'SubscriptionId'
        $target = Invoke-FactoryAz @('account', 'show', '--subscription', $SubscriptionId)
        if ($target.id -ne $SubscriptionId -or $target.tenantId -ne $TenantId -or $target.state -ne 'Enabled') {
            throw 'The target subscription is not enabled in the requested tenant.'
        }
    }
}

function Read-FactoryMetadata {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Kind,
        [Parameter(Mandatory)][string]$Environment,
        [switch]$AllowIncomplete
    )

    $value = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    if ($value['schemaVersion'] -ne 1 -or $value['kind'] -ne $Kind -or $value['environment'] -ne $Environment) {
        throw "Wrong bootstrap metadata type or environment: $Path"
    }
    Assert-FactoryGuid $value['tenantId'] 'metadata tenantId'
    if (!$AllowIncomplete -and $value['ready'] -ne $true) {
        throw "Bootstrap is incomplete. Rerun its provisioning script first: $Path"
    }
    return $value
}

function Write-FactoryMetadata {
    param([string]$Path, [System.Collections.IDictionary]$Value)

    $fullPath = [IO.Path]::GetFullPath($Path)
    New-Item -ItemType Directory -Path (Split-Path $fullPath -Parent) -Force | Out-Null
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $fullPath -Encoding utf8
}

function Merge-FactoryAppRoles {
    param([object[]]$Existing)

    $roles = @($Existing | ForEach-Object {
        @{
            id = $_.id; value = $_.value; displayName = $_.displayName
            description = $_.description; isEnabled = $_.isEnabled
            allowedMemberTypes = @($_.allowedMemberTypes)
        }
    })
    foreach ($definition in @(
        @{ value = 'Factory.User'; member = 'User'; description = 'Sign in to Factory.' },
        @{ value = 'Factory.PlantCreator'; member = 'User'; description = 'Create Factory plants.' },
        @{ value = 'Factory.EnrollmentAdministrator'; member = 'User'; description = 'Enroll, assign and retire Factory devices.' },
        @{ value = 'Workbench.Connect'; member = 'Application'; description = 'Connect an admitted workbench.' }
    )) {
        $matches = @($roles | Where-Object value -EQ $definition.value)
        if ($matches.Count -gt 1) { throw "Duplicate app role: $($definition.value)" }
        if ($matches.Count -eq 1) {
            if ($matches[0].allowedMemberTypes -notcontains $definition.member) {
                throw "Existing $($definition.value) role has incompatible member types. Correct it in Entra first."
            }
            $matches[0].isEnabled = $true
        }
        else {
            $roles += @{
                id = [guid]::NewGuid().ToString(); value = $definition.value
                displayName = $definition.value; description = $definition.description
                allowedMemberTypes = @($definition.member); isEnabled = $true
            }
        }
    }
    return ,$roles
}

function Test-FactoryResourceGroup {
    param([string]$SubscriptionId, [string]$ResourceGroup, [string]$Location, [string]$Environment)

    $exists = Invoke-FactoryAz @('group', 'exists', '--subscription', $SubscriptionId, '--name', $ResourceGroup)
    if ($exists) {
        $group = Invoke-FactoryAz @('group', 'show', '--subscription', $SubscriptionId, '--name', $ResourceGroup)
        if (!$group['tags'] -or $group.tags['application'] -ne 'Factory' -or $group.tags['environment'] -ne $Environment) {
            throw 'Refusing to use an unrelated or untagged resource group. Use a new group or explicitly review and tag the intended Factory group.'
        }
        if ($group.location -ne $Location) {
            throw 'The existing resource group location differs. Supply its original location; do not move existing Cosmos resources.'
        }
    }
    return [bool]$exists
}

function Initialize-FactoryResourceGroup {
    param(
        [string]$SubscriptionId, [string]$ResourceGroup, [string]$Location,
        [string]$Environment, [bool]$Exists, [string[]]$Providers
    )

    foreach ($provider in $Providers) {
        $state = Invoke-FactoryAz @('provider', 'show', '--subscription', $SubscriptionId, '--namespace', $provider)
        if ($state.registrationState -ne 'Registered') {
            Write-Host "Registering $provider in subscription $SubscriptionId..."
            Invoke-FactoryAz @('provider', 'register', '--subscription', $SubscriptionId, '--namespace', $provider, '--wait') | Out-Null
        }
    }
    if (!$Exists) {
        Invoke-FactoryAz @(
            'group', 'create', '--subscription', $SubscriptionId, '--name', $ResourceGroup,
            '--location', $Location, '--tags', 'application=Factory', "environment=$Environment"
        ) | Out-Null
    }
}

function Invoke-FactoryBackendDeployment {
    param(
        [string]$SubscriptionId, [string]$ResourceGroup, [string]$Environment,
        [Parameter(Mandatory)][hashtable]$Parameters
    )

    $armParameters = @{}
    foreach ($entry in $Parameters.GetEnumerator()) {
        $armParameters[$entry.Key] = @{ value = $entry.Value }
    }
    $template = if ($Environment -eq 'production') { 'cosmos.production.bicep' } else { 'cosmos.nonproduction.bicep' }
    Write-Host "Deploying $Environment Cosmos infrastructure in $SubscriptionId / $ResourceGroup. Azure provisioning can take several minutes..."
    $deployment = Invoke-FactoryAzWithJson -Arguments @(
        'deployment', 'group', 'create', '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroup, '--name', "factory-bootstrap-$Environment",
        '--template-file', (Join-Path $script:FactoryRoot "deploy\$template"), '--parameters'
    ) -Document @{ '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'; contentVersion = '1.0.0.0'; parameters = $armParameters }
    if ($deployment.properties.provisioningState -ne 'Succeeded') {
        throw 'ARM did not report a successful deployment. Inspect the resource group deployment before rerunning.'
    }
    return $deployment.properties.outputs
}

function Assert-FactoryLocalConfiguration {
    param([hashtable]$Tenant, [hashtable]$Development)

    foreach ($name in @('tenantId', 'clientId', 'servicePrincipalObjectId')) {
        Assert-FactoryGuid $Tenant[$name] "tenant $name"
        if ($Tenant[$name] -ne $Development[$name]) {
            throw "Tenant and development metadata disagree on $name. Do not mix identities or environments."
        }
    }
    $endpoint = $null
    if (![uri]::TryCreate([string]$Development['cosmosEndpoint'], [UriKind]::Absolute, [ref]$endpoint) -or
        $endpoint.Scheme -ne 'https' -or !$endpoint.Host.EndsWith('.documents.azure.com') -or
        $endpoint.UserInfo -or $endpoint.Query -or $endpoint.Fragment -or $endpoint.AbsolutePath -ne '/') {
        throw 'Development metadata must contain a public Azure Cosmos HTTPS account endpoint.'
    }
    if (!$Development['cosmosDatabase'] -or !$Development['cosmosContainer']) {
        throw 'Development metadata is missing its Cosmos database/container.'
    }
    if ($Tenant['publicOrigin'] -ne 'https://localhost:7443') {
        throw 'Development registration must use the existing https://localhost:7443 launch profile.'
    }
}

function Set-FactoryLocalSecrets {
    param([hashtable]$Tenant, [hashtable]$Development, [string]$Project)

    $PSNativeCommandUseErrorActionPreference = $false
    Assert-FactoryLocalConfiguration $Tenant $Development
    Write-Host "Enter the client secret VALUE for development application $($Tenant.clientId). It will only be sent to dotnet through stdin."
    $secret = Read-Host 'Entra client secret VALUE (not Secret ID)' -AsSecureString
    $pointer = [IntPtr]::Zero
    $settings = $null
    $json = $null
    try {
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
        $settings = @{
            'Factory:TenantId' = $Tenant.tenantId
            'Factory:ClientId' = $Tenant.clientId
            'Factory:ClientSecret' = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
            'Factory:CosmosEndpoint' = $Development.cosmosEndpoint
            'Factory:CosmosDatabase' = $Development.cosmosDatabase
            'Factory:CosmosContainer' = $Development.cosmosContainer
        }
        if ([string]::IsNullOrWhiteSpace($settings['Factory:ClientSecret'])) {
            throw 'A nonempty client secret value is required.'
        }
        $json = $settings | ConvertTo-Json -Compress
        $json | & dotnet user-secrets set --project $Project
        if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets failed; local configuration was not completed.' }
    }
    finally {
        if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
        if ($settings) { $settings.Clear() }
        $json = $null
        $secret.Dispose()
    }
}
