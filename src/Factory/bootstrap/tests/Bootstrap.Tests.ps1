#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\Common.ps1')

$testRoot = Join-Path ([IO.Path]::GetTempPath()) "factory-bootstrap-tests-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $testRoot | Out-Null
$tenantId = '11111111-1111-1111-1111-111111111111'
$subscriptionId = '22222222-2222-2222-2222-222222222222'
$clientId = '33333333-3333-3333-3333-333333333333'
$principalId = '44444444-4444-4444-4444-444444444444'
$userId = '55555555-5555-5555-5555-555555555555'
$tenantPath = Join-Path $testRoot 'tenant-development.json'
$developmentPath = Join-Path $testRoot 'development.json'
$productionPath = Join-Path $testRoot 'production.json'
$tenantScript = Join-Path $PSScriptRoot '..\tenant\Initialize-Tenant.ps1'
$developmentScript = Join-Path $PSScriptRoot '..\development\Deploy-Development.ps1'
$productionScript = Join-Path $PSScriptRoot '..\production\Deploy-Production.ps1'
$localScript = Join-Path $PSScriptRoot '..\development\Set-LocalConfiguration.ps1'
$script:Checks = 0
$script:Fixture = @{
    ActiveTenant = $tenantId; TargetTenant = $tenantId
    AppExists = $false; AppCreates = 0; Writes = 0; FailGraph = $false; FailDotnet = $false
    Groups = @{}; Assignments = @(); Deployments = @(); Bodies = @(); JsonPaths = @()
    Prompts = 0; CertificateCalls = 0; SecretInput = $null; SecretArguments = @()
    SecretValue = 'fixture-secret-"quoted"-\value'
    App = @{
        id = '66666666-6666-6666-6666-666666666666'; appId = $clientId
        displayName = 'Factory-development'; signInAudience = 'AzureADMyOrg'
        identifierUris = @()
        appRoles = @(@{
            id = '77777777-7777-7777-7777-777777777777'; value = 'Existing.Role'
            displayName = 'Existing role'; description = 'Keep this'; isEnabled = $true
            allowedMemberTypes = @('User'); origin = 'Application'
        })
        api = @{
            knownClientApplications = @('88888888-8888-8888-8888-888888888888')
            oauth2PermissionScopes = @()
        }
        web = @{
            homePageUrl = 'https://existing.example/'
            redirectUris = @('https://existing.example/signin-oidc')
        }
    }
}

function Assert {
    param([bool]$Condition, [string]$Message)
    if (!$Condition) { throw "FAILED: $Message" }
    $script:Checks++
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Pattern)
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_ }
    Assert ($null -ne $caught) "Expected failure matching '$Pattern'"
    Assert ($caught.Exception.Message -match $Pattern) "Unexpected failure: $($caught.Exception.Message)"
}

$mockModule = New-Module -Name FactoryBootstrapTestCommands -ArgumentList $script:Fixture, $clientId, $principalId, $userId -ScriptBlock {
param($State, $Client, $Principal, $User)
$script:Fixture = $State
$clientId = $Client
$principalId = $Principal
$userId = $User

function Assert {
    param([bool]$Condition, [string]$Message)
    if (!$Condition) { throw "FAILED in command mock: $Message" }
}

function Get-TestArgument {
    param([object[]]$Arguments, [string]$Name)
    $index = [array]::IndexOf($Arguments, $Name)
    if ($index -lt 0 -or $index + 1 -ge $Arguments.Count) { throw "Missing mock argument $Name" }
    return $Arguments[$index + 1]
}

# All external commands used by the bootstrap scripts are replaced in this process.
function az {
    $arguments = @($args)
    $global:LASTEXITCODE = 0
    $command = $arguments[0..1] -join ' '
    $result = switch ($command) {
        'cloud show' { @{ name = 'AzureCloud' }; break }
        'account show' {
            if ($arguments -contains '--subscription') {
                @{ id = (Get-TestArgument $arguments '--subscription'); tenantId = $script:Fixture.TargetTenant; state = 'Enabled' }
            }
            else { @{ tenantId = $script:Fixture.ActiveTenant } }
            break
        }
        'ad app' {
            switch ($arguments[2]) {
                'list' { if ($script:Fixture.AppExists) { ,@($script:Fixture.App) } else { ,@() } }
                'show' { $script:Fixture.App }
                'create' {
                    $script:Fixture.Writes++
                    $script:Fixture.AppCreates++
                    $script:Fixture.AppExists = $true
                    $script:Fixture.App
                }
                default { throw "Unexpected app command: $arguments" }
            }
            break
        }
        'ad sp' {
            if ($arguments[2] -ne 'list') { throw "Unexpected SP command: $arguments" }
            ,@(@{ id = $principalId; appId = $clientId })
            break
        }
        'ad signed-in-user' { @{ id = $userId }; break }
        'rest --method' {
            $method = Get-TestArgument $arguments '--method'
            $url = Get-TestArgument $arguments '--url'
            if ($method -eq 'GET') {
                @{ value = @($script:Fixture.Assignments) }
            }
            else {
                $script:Fixture.Writes++
                $path = (Get-TestArgument $arguments '--body').Substring(1)
                $script:Fixture.JsonPaths += $path
                $body = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
                $script:Fixture.Bodies += $body
                if ($script:Fixture.FailGraph) {
                    $global:LASTEXITCODE = 1
                    return
                }
                if ($url -match '/applications/') {
                    foreach ($key in $body.Keys) { $script:Fixture.App[$key] = $body[$key] }
                }
                elseif ($url -match '/appRoleAssignedTo$') { $script:Fixture.Assignments += $body }
                else {
                    Assert ($url -match '/servicePrincipals/' -and $body.appRoleAssignmentRequired) 'Only the expected service-principal patch is permitted'
                }
            }
            break
        }
        'group exists' { $script:Fixture.Groups.ContainsKey((Get-TestArgument $arguments '--name')); break }
        'group show' { $script:Fixture.Groups[(Get-TestArgument $arguments '--name')]; break }
        'group create' {
            $script:Fixture.Writes++
            $tags = if ($arguments -contains 'environment=production') { 'production' } else { 'nonproduction' }
            $script:Fixture.Groups[(Get-TestArgument $arguments '--name')] = @{
                location = (Get-TestArgument $arguments '--location')
                tags = @{ application = 'Factory'; environment = $tags }
            }
            break
        }
        'provider show' { @{ registrationState = 'Registered' }; break }
        'deployment group' {
            Assert ($arguments[2] -eq 'create') 'Expected only a group deployment'
            $script:Fixture.Writes++
            $path = (Get-TestArgument $arguments '--parameters').Substring(1)
            $script:Fixture.JsonPaths += $path
            $parameters = (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable).parameters
            $script:Fixture.Deployments += @{ arguments = $arguments; parameters = $parameters }
            @{ properties = @{
                provisioningState = 'Succeeded'
                outputs = @{
                    cosmosEndpoint = @{ value = 'https://fixture.documents.azure.com:443/' }
                    cosmosDatabase = @{ value = 'plant' }; cosmosContainer = @{ value = 'records' }
                    dataIdentityResourceId = @{ value = '/subscriptions/fixture/resourceGroups/prod/providers/Microsoft.ManagedIdentity/userAssignedIdentities/data' }
                    dataIdentityClientId = @{ value = $clientId }; dataIdentityPrincipalId = @{ value = $principalId }
                    appSubnetId = @{ value = '/subscriptions/fixture/resourceGroups/prod/providers/Microsoft.Network/virtualNetworks/network/subnets/app-service' }
                }
            } }
            break
        }
        default { throw "Unexpected Azure command (no real CLI invocation is allowed): $arguments" }
    }
    if ($null -ne $result) { ConvertTo-Json -InputObject $result -Depth 50 -Compress }
}

function dotnet {
    $arguments = @($args)
    $global:LASTEXITCODE = 0
    if ($arguments[0] -eq 'dev-certs') {
        $script:Fixture.CertificateCalls++
    }
    elseif ($arguments[0] -eq 'user-secrets' -and $arguments[1] -eq 'set') {
        $script:Fixture.SecretArguments = $arguments
        $script:Fixture.SecretInput = ($input | Out-String) | ConvertFrom-Json -AsHashtable
        if ($script:Fixture.FailDotnet) { $global:LASTEXITCODE = 1 }
    }
    else { throw 'Unexpected dotnet invocation; no real user-secrets access is allowed.' }
}

function Read-Host {
    param([string]$Prompt, [switch]$AsSecureString)
    Assert ([bool]$AsSecureString) 'Secrets must use a hidden prompt'
    $script:Fixture.Prompts++
    ConvertTo-SecureString $script:Fixture.SecretValue -AsPlainText -Force
}

Export-ModuleMember -Function az, dotnet, Read-Host, Get-TestArgument
}
Import-Module $mockModule

try {
    Assert ((Get-Command az).CommandType -eq 'Function') 'Azure CLI mock must be installed'
    Assert ((Get-Command dotnet).CommandType -eq 'Function') 'dotnet mock must be installed'
    & $tenantScript -TenantId $tenantId -OutputPath $tenantPath -AssignCurrentUser -WhatIf
    Assert ($script:Fixture.Writes -eq 0 -and !(Test-Path -LiteralPath $tenantPath)) 'Tenant WhatIf must not create apps or metadata'

    $script:Fixture.FailGraph = $true
    Assert-Throws { & $tenantScript -TenantId $tenantId -OutputPath $tenantPath -AssignCurrentUser -Confirm:$false } 'Azure CLI failed'
    $partial = Read-FactoryMetadata $tenantPath 'tenant' 'development' -AllowIncomplete
    Assert ($partial.ready -eq $false -and $partial.clientId -eq $clientId) 'Interrupted tenant setup must record its application for recovery'
    Assert-Throws { Read-FactoryMetadata $tenantPath 'tenant' 'development' } 'incomplete'
    $script:Fixture.FailGraph = $false
    & $tenantScript -TenantId $tenantId -OutputPath $tenantPath -AssignCurrentUser -Confirm:$false
    $firstRoleIds = @($script:Fixture.App.appRoles.id) -join ','
    $firstScopeIds = @($script:Fixture.App.api.oauth2PermissionScopes.id) -join ','
    & $tenantScript -TenantId $tenantId -OutputPath $tenantPath -AssignCurrentUser -Confirm:$false
    Assert ($script:Fixture.AppCreates -eq 1) 'Recovery/reruns must not create duplicate applications'
    Assert ((@($script:Fixture.App.appRoles.id) -join ',') -eq $firstRoleIds) 'Reruns must retain role IDs'
    Assert ((@($script:Fixture.App.api.oauth2PermissionScopes.id) -join ',') -eq $firstScopeIds) 'Reruns must retain scope IDs'
    Assert ($script:Fixture.Assignments.Count -eq 2) 'Reruns must not duplicate user assignments'
    Assert ($script:Fixture.App.appRoles.Count -eq 5) 'Unrelated app roles must survive alongside the enrollment-administrator role'
    Assert (!($script:Fixture.Assignments | Where-Object appRoleId -EQ (@($script:Fixture.App.appRoles | Where-Object value -EQ 'Factory.EnrollmentAdministrator')[0].id))) 'Enrollment administration must not be granted implicitly'
    Assert (!$script:Fixture.App.appRoles[0].ContainsKey('origin')) 'Read-only role origin must not be sent to Graph'
    Assert ($script:Fixture.App.api.knownClientApplications.Count -eq 1) 'Unrelated API settings must survive'
    Assert ($script:Fixture.App.api.requestedAccessTokenVersion -eq 2) 'API tokens must be version 2'
    Assert ($script:Fixture.App.web.redirectUris.Count -eq 3) 'Callbacks must be merged without duplicates'
    Assert ($script:Fixture.App.web.homePageUrl -eq 'https://existing.example/') 'Unrelated web settings must survive'
    $before = $script:Fixture.Writes
    Assert-Throws { & $tenantScript -TenantId $tenantId -OutputPath (Join-Path $testRoot 'other.json') -Confirm:$false } 'exact -ClientId'
    Assert ($script:Fixture.Writes -eq $before) 'Display-name adoption must fail without writes'

    $script:Fixture.ActiveTenant = $subscriptionId
    Assert-Throws { & $tenantScript -TenantId $tenantId -OutputPath $tenantPath -Confirm:$false } 'Wrong active tenant'
    Assert ($script:Fixture.Writes -eq $before) 'Wrong tenant must fail without writes'
    $script:Fixture.ActiveTenant = $tenantId
    $script:Fixture.TargetTenant = $subscriptionId
    Assert-Throws {
        & $productionScript -TenantId $tenantId -SubscriptionId $subscriptionId -ResourceGroup 'prod' -Location 'westus2' -OutputPath $productionPath -Confirm:$false
    } 'target subscription'
    Assert ($script:Fixture.Writes -eq $before) 'Wrong subscription tenant must fail without writes'
    $script:Fixture.TargetTenant = $tenantId

    & $developmentScript -SubscriptionId $subscriptionId -ResourceGroup 'dev' -Location 'westus2' -TenantConfiguration $tenantPath -OutputPath $developmentPath -WhatIf
    Assert ($script:Fixture.Writes -eq $before -and !(Test-Path -LiteralPath $developmentPath)) 'Development WhatIf must not deploy or create metadata'
    & $developmentScript -SubscriptionId $subscriptionId -ResourceGroup 'dev' -Location 'westus2' -TenantConfiguration $tenantPath -OutputPath $developmentPath -Confirm:$false
    $development = Read-FactoryMetadata $developmentPath 'development' 'development'
    $parameters = $script:Fixture.Deployments[0].parameters
    Assert ($parameters.servicePrincipalObjectId.value -eq $principalId) 'Development must grant the app principal, not the user or client ID'
    Assert ($parameters.allowedIpAddresses.value.Count -eq 0) 'Default development harness must not guess an egress IP'
    Assert ((Get-TestArgument $script:Fixture.Deployments[0].arguments '--subscription') -eq $subscriptionId) 'Deployments must explicitly target a subscription'
    Assert ($development.cosmosEndpoint -eq 'https://fixture.documents.azure.com:443/') 'Deployment outputs must feed local configuration'
    & $productionScript -TenantId $tenantId -SubscriptionId $subscriptionId -ResourceGroup 'prod' -Location 'westus2' -OutputPath $productionPath -Confirm:$false
    $productionDeployment = $script:Fixture.Deployments[1]
    Assert ((Get-TestArgument $productionDeployment.arguments '--template-file') -like '*cosmos.production.bicep') 'Production must use the existing private backend template, not the full-stack template'
    Assert (!$productionDeployment.parameters.ContainsKey('servicePrincipalObjectId')) 'Production must create its managed identity, not reuse the development application'
    Assert-Throws { Read-FactoryMetadata $productionPath 'development' 'development' } 'Wrong bootstrap metadata'
    $script:Fixture.Groups['unrelated'] = @{ location = 'westus2'; tags = @{ application = 'Other'; environment = 'production' } }
    $before = $script:Fixture.Writes
    Assert-Throws {
        & $productionScript -TenantId $tenantId -SubscriptionId $subscriptionId -ResourceGroup 'unrelated' -Location 'westus2' -OutputPath $productionPath -Confirm:$false
    } 'unrelated or untagged'
    Assert ($script:Fixture.Writes -eq $before) 'Unrelated resource groups must not be changed'

    $logs = & $localScript -TenantConfiguration $tenantPath -DevelopmentConfiguration $developmentPath 6>&1 | Out-String
    Assert ($script:Fixture.CertificateCalls -eq 1 -and $script:Fixture.Prompts -eq 1) 'Local setup must use standard certificate tooling and a hidden secret prompt'
    Assert ($script:Fixture.SecretInput['Factory:ClientSecret'] -ceq 'fixture-secret-"quoted"-\value') 'Secret JSON stdin must preserve quotes and backslashes'
    Assert ($script:Fixture.SecretInput['Factory:ClientId'] -eq $clientId) 'Local identity must come from matching metadata'
    Assert (($script:Fixture.SecretArguments -join ' ') -notmatch 'fixture-secret') 'Secrets must not appear in command arguments'
    Assert ($logs -notmatch 'fixture-secret') 'Secrets must not be echoed'
    $development.clientId = $userId
    Write-FactoryMetadata $developmentPath $development
    Assert-Throws { & $localScript -TenantConfiguration $tenantPath -DevelopmentConfiguration $developmentPath } 'disagree on clientId'
    Assert ($script:Fixture.Prompts -eq 1 -and $script:Fixture.CertificateCalls -eq 1) 'Mismatched metadata must fail before certificate changes or secret prompts'
    $development.clientId = $clientId
    Write-FactoryMetadata $developmentPath $development
    $script:Fixture.FailDotnet = $true
    Assert-Throws { & $localScript -TenantConfiguration $tenantPath -DevelopmentConfiguration $developmentPath } 'user-secrets failed'
    $script:Fixture.FailDotnet = $false
    $script:Fixture.SecretValue = '   '
    $script:Fixture.SecretInput = $null
    Assert-Throws { & $localScript -TenantConfiguration $tenantPath -DevelopmentConfiguration $developmentPath } 'nonempty client secret'
    Assert ($null -eq $script:Fixture.SecretInput) 'Whitespace secrets must not be persisted'
    $prompts = $script:Fixture.Prompts
    $certificates = $script:Fixture.CertificateCalls
    & $localScript -TenantConfiguration $tenantPath -DevelopmentConfiguration $developmentPath -WhatIf
    Assert ($script:Fixture.Prompts -eq $prompts -and $script:Fixture.CertificateCalls -eq $certificates) 'Local WhatIf must not prompt or change certificates'
    foreach ($path in $script:Fixture.JsonPaths) {
        Assert (!(Test-Path -LiteralPath $path)) 'Temporary non-secret JSON must be removed after success and failure'
    }
    foreach ($path in @($tenantPath, $developmentPath, $productionPath)) {
        Assert ((Get-Content -LiteralPath $path -Raw) -notmatch 'fixture-secret|ClientSecret|passwordCredentials') 'Bootstrap metadata must not contain credentials'
    }
    Write-Host "Passed $script:Checks bootstrap assertions. All Azure and dotnet commands were mocked."
}
finally {
    Remove-Module $mockModule
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
