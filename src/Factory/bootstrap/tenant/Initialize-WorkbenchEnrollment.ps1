#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$TenantConfiguration,
    [ValidateSet('development', 'production')][string]$Environment = 'development',
    [Parameter(Mandatory)][string]$ProvisioningClientId,
    [ValidateSet('ManagedIdentity', 'ClientSecret')][string]$ProvisioningCredential = 'ManagedIdentity',
    [string]$InstallerClientId,
    [string]$EnrollmentAdministratorObjectId,
    [Parameter(Mandatory)][switch]$GrantProvisioningPermissions,
    [string]$OutputPath
)

. (Join-Path $PSScriptRoot '..\Common.ps1')
if (!$GrantProvisioningPermissions) {
    throw 'Explicit -GrantProvisioningPermissions acknowledgement is required for broad tenant Graph application grants.'
}
if ($Environment -eq 'production' -and $ProvisioningCredential -ne 'ManagedIdentity') {
    throw 'Production provisioning requires a dedicated user-assigned managed identity.'
}
$tenant = Read-FactoryMetadata $TenantConfiguration 'tenant' $Environment
Assert-FactoryAzureContext -TenantId $tenant.tenantId
Assert-FactoryGuid $ProvisioningClientId 'ProvisioningClientId'
if ($EnrollmentAdministratorObjectId) { Assert-FactoryGuid $EnrollmentAdministratorObjectId 'EnrollmentAdministratorObjectId' }
if ($ProvisioningClientId -eq $tenant.clientId) { throw 'Use a separate provisioner, not the web/API application.' }
if (!$tenant['publicOrigin']) { throw 'Tenant metadata must contain the deployed HTTPS publicOrigin.' }
if (!$OutputPath) { $OutputPath = Join-Path $script:FactoryRoot "artifacts\bootstrap\enrollment-$Environment.json" }
if (Test-Path -LiteralPath $OutputPath) {
    $previous = Read-FactoryMetadata $OutputPath 'workbench-enrollment' $Environment -AllowIncomplete
    if ($previous.tenantId -ne $tenant.tenantId -or $previous.apiClientId -ne $tenant.clientId -or
        $previous.provisioningClientId -ne $ProvisioningClientId -or
        ($InstallerClientId -and $InstallerClientId -ne $previous.installerClientId)) {
        throw 'Existing enrollment metadata targets another deployment. Use the original metadata or a new OutputPath.'
    }
    $InstallerClientId = $previous.installerClientId
}
$apiApp = Invoke-FactoryAz @('ad', 'app', 'show', '--id', $tenant.clientId)
$scopes = @($apiApp.api.oauth2PermissionScopes | Where-Object { $_.value -eq 'Plant.Access' -and $_.isEnabled })
if ($scopes.Count -ne 1) { throw 'Initialize the plant API with one enabled Plant.Access scope first.' }
$connect = @($apiApp.appRoles | Where-Object { $_.value -eq 'Workbench.Connect' -and $_.isEnabled -and $_.allowedMemberTypes -contains 'Application' })
if ($connect.Count -ne 1) { throw 'Initialize the plant API with one enabled application Workbench.Connect role first.' }
$principals = @(Invoke-FactoryAz @('ad', 'sp', 'list', '--filter', "appId eq '$ProvisioningClientId'"))
if ($principals.Count -ne 1) { throw 'ProvisioningClientId must identify one existing, dedicated service principal.' }
$provisioner = $principals[0]
if (($ProvisioningCredential -eq 'ManagedIdentity' -and $provisioner.servicePrincipalType -ne 'ManagedIdentity') -or
    ($ProvisioningCredential -eq 'ClientSecret' -and $provisioner.servicePrincipalType -ne 'Application')) {
    throw 'The provisioner service-principal type does not match the explicitly selected credential.'
}
$displayName = "Factory-$Environment-Workbench-Installer"
if ($InstallerClientId) {
    Assert-FactoryGuid $InstallerClientId 'InstallerClientId'
    if ($InstallerClientId -in @($tenant.clientId, $ProvisioningClientId)) { throw 'Installer must be a separate public client.' }
    $installer = Invoke-FactoryAz @('ad', 'app', 'show', '--id', $InstallerClientId)
    if ($installer.signInAudience -ne 'AzureADMyOrg' -or @($installer.passwordCredentials).Count -or
        @($installer.keyCredentials).Count -or @($installer.requiredResourceAccess | Where-Object resourceAppId -NE $tenant.clientId).Count) {
        throw 'Only a single-tenant public installer without credentials or unrelated API/Graph grants may be adopted.'
    }
}
else {
    $matches = @(Invoke-FactoryAz @('ad', 'app', 'list', '--display-name', $displayName) | Where-Object displayName -EQ $displayName)
    if ($matches.Count) { throw 'Installer name already exists. Supply its exact -InstallerClientId to resume; names are not trusted identities.' }
    $installer = $null
}
if (!$PSCmdlet.ShouldProcess("$($tenant.tenantId) / $Environment",
    'Configure the public installer, enrollment-administrator role and any explicitly requested administrator assignment; grant the approved dedicated-provisioner Graph permissions')) { return }
if (!$installer) {
    $installer = Invoke-FactoryAz @('ad', 'app', 'create', '--display-name', $displayName, '--sign-in-audience', 'AzureADMyOrg')
}
$metadata = @{
    schemaVersion = 1; kind = 'workbench-enrollment'; environment = $Environment; ready = $false
    tenantId = $tenant.tenantId; apiClientId = $tenant.clientId; installerClientId = $installer.appId
    provisioningClientId = $ProvisioningClientId; provisioningObjectId = $provisioner.id
}
Write-FactoryMetadata $OutputPath $metadata
Invoke-FactoryGraph -Method PATCH -Path "applications/$($installer.id)" -Body @{
    isFallbackPublicClient = $true
    publicClient = @{ redirectUris = @('http://localhost') }
    requiredResourceAccess = @(@{
        resourceAppId = $tenant.clientId; resourceAccess = @(@{ id = $scopes[0].id; type = 'Scope' })
    })
} | Out-Null
$installerPrincipals = @(Invoke-FactoryAz @('ad', 'sp', 'list', '--filter', "appId eq '$($installer.appId)'"))
if ($installerPrincipals.Count -gt 1) { throw 'Installer service-principal lookup is ambiguous.' }
if ($installerPrincipals.Count -eq 0) { Invoke-FactoryAz @('ad', 'sp', 'create', '--id', $installer.appId) | Out-Null }
$api = $apiApp.api
$authorized = @($api['preAuthorizedApplications'] | Where-Object { $_ -and $_.appId -ne $installer.appId })
$authorized += @{ appId = $installer.appId; delegatedPermissionIds = @($scopes[0].id) }
$api['preAuthorizedApplications'] = $authorized
$apiRoles = Merge-FactoryAppRoles -Existing @($apiApp.appRoles)
Invoke-FactoryGraph -Method PATCH -Path "applications/$($apiApp.id)" -Body @{ api = $api; appRoles = $apiRoles } | Out-Null
if ($EnrollmentAdministratorObjectId) {
    $assigned = @()
    $page = Invoke-FactoryGraph -Path "servicePrincipals/$($tenant.servicePrincipalObjectId)/appRoleAssignedTo"
    while ($null -ne $page) {
        $assigned += @($page.value)
        $page = if ($page['@odata.nextLink']) {
            Invoke-FactoryAz @('rest', '--method', 'GET', '--url', $page['@odata.nextLink'])
        } else { $null }
    }
    foreach ($role in @($apiRoles | Where-Object value -In @('Factory.User', 'Factory.EnrollmentAdministrator'))) {
        if (!($assigned | Where-Object { $_.principalId -eq $EnrollmentAdministratorObjectId -and $_.appRoleId -eq $role.id })) {
            Invoke-FactoryGraph -Method POST -Path "servicePrincipals/$($tenant.servicePrincipalObjectId)/appRoleAssignedTo" -Body @{
                principalId = $EnrollmentAdministratorObjectId; resourceId = $tenant.servicePrincipalObjectId; appRoleId = $role.id
            } | Out-Null
        }
    }
}
$graph = Invoke-FactoryAz @('ad', 'sp', 'show', '--id', '00000003-0000-0000-c000-000000000000', '--query', '{id:id,appRoles:appRoles}')
$assignments = @()
$page = Invoke-FactoryGraph -Path "servicePrincipals/$($provisioner.id)/appRoleAssignments"
while ($null -ne $page) {
    $assignments += @($page.value)
    $page = if ($page['@odata.nextLink']) {
        Invoke-FactoryAz @('rest', '--method', 'GET', '--url', $page['@odata.nextLink'])
    } else { $null }
}
foreach ($permission in @('Application.ReadWrite.OwnedBy', 'Application.Read.All', 'AppRoleAssignment.ReadWrite.All')) {
    $roles = @($graph.appRoles | Where-Object { $_.value -eq $permission -and $_.isEnabled -and $_.allowedMemberTypes -contains 'Application' })
    if ($roles.Count -ne 1) { throw "Graph permission is missing or ambiguous: $permission" }
    if (!($assignments | Where-Object { $_.resourceId -eq $graph.id -and $_.appRoleId -eq $roles[0].id })) {
        Invoke-FactoryGraph -Method POST -Path "servicePrincipals/$($provisioner.id)/appRoleAssignments" -Body @{
            principalId = $provisioner.id; resourceId = $graph.id; appRoleId = $roles[0].id
        } | Out-Null
    }
}
$profilePath = Join-Path (Split-Path ([IO.Path]::GetFullPath($OutputPath)) -Parent) "workbench-profile-$Environment.json"
Write-FactoryMetadata $profilePath @{
    schemaVersion = '1'; plantServiceUrl = $tenant.publicOrigin; tenantId = $tenant.tenantId
    apiClientId = $tenant.clientId; installerClientId = $installer.appId
}
$metadata['installationProfile'] = $profilePath
$metadata['serviceConfiguration'] = @{ Factory = @{ Enrollment = @{
    Enabled = $true; InstallerClientId = $installer.appId; ProvisioningCredential = $ProvisioningCredential
    ProvisioningClientId = $ProvisioningClientId; ProvisioningObjectId = $provisioner.id
} } }
$metadata.ready = $true
$metadata['enrollmentAdministratorRoleId'] = @($apiRoles | Where-Object value -EQ 'Factory.EnrollmentAdministrator')[0].id
Write-FactoryMetadata $OutputPath $metadata
Write-Host "Enrollment metadata: $OutputPath"
Write-Host "Installer profile: $profilePath"
if (!$EnrollmentAdministratorObjectId) {
    Write-Host 'No enrollment administrator was assigned. Assign Factory.User and Factory.EnrollmentAdministrator to an explicitly approved operator before enrolling. Plant ownership alone is insufficient.'
}
Write-Host 'Apply serviceConfiguration to the plant service. ClientSecret additionally requires a provisioner secret in Development user-secrets, never in the profile.'
Write-Warning 'AppRoleAssignment.ReadWrite.All is tenant-wide authority. The service limits its use to its configured API Workbench.Connect role; this does not narrow the Graph grant.'
