#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Join-Path ([IO.Path]::GetTempPath()) "factory-enrollment-bootstrap-tests-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root | Out-Null
$tenant = '11111111-1111-1111-1111-111111111111'
$api = '22222222-2222-2222-2222-222222222222'
$provisioner = '33333333-3333-3333-3333-333333333333'
$installer = '44444444-4444-4444-4444-444444444444'
$tenantPath = Join-Path $root 'tenant.json'
$output = Join-Path $root 'enrollment.json'
$scriptPath = Join-Path $PSScriptRoot '..\tenant\Initialize-WorkbenchEnrollment.ps1'
$localPath = Join-Path $PSScriptRoot '..\development\Set-EnrollmentConfiguration.ps1'
$state = @{
    Creates = 0; Writes = 0; Installer = $null; HasPrincipal = $false; Assignments = @(); FailGrant = $false; SecretInput = $null
    Api = @{
        id = $api; appId = $api
        appRoles = @(@{ id = [guid]::NewGuid().ToString(); value = 'Workbench.Connect'; isEnabled = $true; allowedMemberTypes = @('Application') })
        api = @{
            oauth2PermissionScopes = @(@{ id = [guid]::NewGuid().ToString(); value = 'Plant.Access'; isEnabled = $true })
            preAuthorizedApplications = @(@{ appId = '55555555-5555-5555-5555-555555555555'; delegatedPermissionIds = @([guid]::NewGuid().ToString()) })
        }
    }
    Graph = @{ id = [guid]::NewGuid().ToString(); appRoles = @(
        'Application.ReadWrite.OwnedBy', 'Application.Read.All', 'AppRoleAssignment.ReadWrite.All' | ForEach-Object {
            @{ id = [guid]::NewGuid().ToString(); value = $_; isEnabled = $true; allowedMemberTypes = @('Application') }
        }
    ) }
}
@{ schemaVersion = 1; kind = 'tenant'; environment = 'development'; ready = $true; tenantId = $tenant;
    clientId = $api; publicOrigin = 'https://plant.example'; servicePrincipalObjectId = $api } |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $tenantPath
$module = New-Module -Name EnrollmentBootstrapMocks -ArgumentList $state, $tenant, $api, $provisioner, $installer -ScriptBlock {
param($State, $Tenant, $Api, $Provisioner, $Installer)
function Argument($Values, $Name) { $Values[[array]::IndexOf($Values, $Name) + 1] }
function az {
    $values = @($args)
    $global:LASTEXITCODE = 0
    $result = switch ($values[0..1] -join ' ') {
        'cloud show' { @{ name = 'AzureCloud' }; break }
        'account show' { @{ tenantId = $Tenant }; break }
        'ad app' {
            switch ($values[2]) {
                'show' { if ((Argument $values '--id') -eq $Api) { $State.Api } else { $State.Installer } }
                'list' { ,@($State.Installer | Where-Object { $_ }) }
                'create' {
                    $State.Creates++; $State.Writes++
                    $State.Installer = @{ id = $Installer; appId = $Installer; displayName = 'Factory-development-Workbench-Installer';
                        signInAudience = 'AzureADMyOrg'; passwordCredentials = @(); keyCredentials = @(); requiredResourceAccess = @() }
                    $State.Installer
                }
                default { throw "Unexpected app command: $values" }
            }
            break
        }
        'ad sp' {
            if ($values[2] -eq 'show' -and (Argument $values '--id') -eq '00000003-0000-0000-c000-000000000000') {
                $State.Graph
            }
            elseif ($values[2] -eq 'create') {
                $State.HasPrincipal = $true; $State.Writes++
                @{ id = $Installer; appId = $Installer }
            }
            elseif ((Argument $values '--filter') -like "*$Provisioner*") {
                ,@(@{ id = $Provisioner; appId = $Provisioner; servicePrincipalType = 'Application' })
            }
            else { ,@($(if ($State.HasPrincipal) { @{ id = $Installer; appId = $Installer } })) }
            break
        }
        'rest --method' {
            $method = Argument $values '--method'
            $url = Argument $values '--url'
            if ($method -eq 'GET') {
                if ($url -match '/appRoleAssignments$') { @{ value = @($State.Assignments) } }
                else { throw "Unexpected Graph read: $url" }
            }
            else {
                $State.Writes++
                $body = Get-Content -LiteralPath ((Argument $values '--body').Substring(1)) -Raw | ConvertFrom-Json -AsHashtable
                if ($method -eq 'PATCH' -and $url -match "/applications/$Installer$") {
                    foreach ($key in $body.Keys) { $State.Installer[$key] = $body[$key] }
                }
                elseif ($method -eq 'PATCH' -and $url -match "/applications/$Api$") { $State.Api.api = $body.api }
                elseif ($method -eq 'POST' -and $url -match "/servicePrincipals/$Provisioner/appRoleAssignments$") {
                    if ($State.FailGrant) { $global:LASTEXITCODE = 1; return }
                    $State.Assignments += $body
                }
                else { throw "Unexpected Graph mutation: $method $url" }
            }
            break
        }
        default { throw "Unexpected Azure command; real cloud access is forbidden: $values" }
    }
    if ($null -ne $result) { ConvertTo-Json -InputObject $result -Depth 30 -Compress }
}
function dotnet {
    if ($args[0] -ne 'user-secrets' -or $args[1] -ne 'set') { throw 'Unexpected real tooling invocation.' }
    $State.SecretInput = ($input | Out-String) | ConvertFrom-Json -AsHashtable
    $global:LASTEXITCODE = 0
}
function Read-Host {
    param([string]$Prompt, [switch]$AsSecureString)
    if (!$AsSecureString) { throw 'Secret prompt must be hidden.' }
    ConvertTo-SecureString 'fixture-secret-not-a-real-credential' -AsPlainText -Force
}
Export-ModuleMember -Function az, dotnet, Read-Host
}
Import-Module $module
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw "FAILED: $Message" } }
try {
    $parameters = @{ TenantConfiguration = $tenantPath; ProvisioningClientId = $provisioner;
        ProvisioningCredential = 'ClientSecret'; GrantProvisioningPermissions = $true; OutputPath = $output }
    & $scriptPath @parameters -WhatIf
    Assert ($state.Writes -eq 0 -and !(Test-Path -LiteralPath $output)) 'WhatIf must not mutate Entra or handoff files.'
    $state.FailGrant = $true
    $failed = $false
    try { & $scriptPath @parameters -Confirm:$false } catch { $failed = $true }
    Assert $failed 'A Graph grant failure must stop setup.'
    $partial = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json -AsHashtable
    Assert (!$partial.ready -and $partial.installerClientId -eq $installer) 'The installer identity must be journaled before later mutations.'
    $state.FailGrant = $false
    & $scriptPath @parameters -Confirm:$false
    & $scriptPath @parameters -Confirm:$false
    Assert ($state.Creates -eq 1) 'Resume must reuse the installer application.'
    Assert ($state.Assignments.Count -eq 3) 'Resume must not duplicate directory role grants.'
    Assert ($state.Api.api.preAuthorizedApplications.Count -eq 2) 'Unrelated API preauthorizations must survive.'
    Assert ($state.Installer.isFallbackPublicClient -and $state.Installer.publicClient.redirectUris[0] -eq 'http://localhost') 'Installer must use a loopback public client.'
    Assert ($state.Installer.requiredResourceAccess[0].resourceAppId -eq $api) 'Installer must not receive Graph permissions.'
    $metadata = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json -AsHashtable
    $profile = Get-Content -LiteralPath $metadata.installationProfile -Raw | ConvertFrom-Json -AsHashtable
    Assert ($metadata.ready -and $profile.Count -eq 5 -and $profile.installerClientId -eq $installer) 'A non-secret build profile must be produced only after bootstrap completes.'
    & $localPath -EnrollmentConfiguration $output -WhatIf
    Assert ($null -eq $state.SecretInput) 'Local WhatIf must not read or write credentials.'
    & $localPath -EnrollmentConfiguration $output -Confirm:$false
    Assert ($state.SecretInput['Factory:Enrollment:ProvisioningClientSecret'] -eq 'fixture-secret-not-a-real-credential') 'Provisioner credentials must be passed through stdin.'
    Assert (!$state.SecretInput.ContainsKey('Factory:ClientSecret') -and !$state.SecretInput.ContainsKey('Factory:DataCredential')) 'Enrollment configuration must not replace web/data credentials.'
    Write-Output 'Enrollment bootstrap checks passed (mocked Azure/dotnet; no live changes).'
}
finally {
    Remove-Module $module
    Remove-Item -LiteralPath $root -Recurse -Force
}
