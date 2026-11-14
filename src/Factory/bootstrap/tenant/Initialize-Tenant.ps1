#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$TenantId,
    [ValidateSet('development', 'production')][string]$Environment = 'development',
    [ValidatePattern('^[a-zA-Z0-9 ._-]{1,120}$')][string]$DisplayName,
    [string]$ClientId,
    [string]$PublicOrigin,
    [switch]$AssignCurrentUser,
    [string]$UserObjectId,
    [string]$OutputPath
)

. (Join-Path $PSScriptRoot '..\Common.ps1')
Assert-FactoryAzureContext -TenantId $TenantId
if (!$DisplayName) { $DisplayName = "Factory-$Environment" }
if (!$OutputPath) { $OutputPath = Join-Path $script:FactoryRoot "artifacts\bootstrap\tenant-$Environment.json" }
if ($Environment -eq 'development') {
    if ($PublicOrigin -and $PublicOrigin -ne 'https://localhost:7443') {
        throw 'Development uses the existing https://localhost:7443 launch profile.'
    }
    $PublicOrigin = 'https://localhost:7443'
}
elseif ($PublicOrigin) {
    $origin = $null
    if (![uri]::TryCreate($PublicOrigin, [UriKind]::Absolute, [ref]$origin) -or
        $origin.Scheme -ne 'https' -or $origin.UserInfo -or $origin.Query -or $origin.Fragment -or $origin.AbsolutePath -ne '/') {
        throw 'PublicOrigin must be an HTTPS origin without a path, credentials, query or fragment.'
    }
    $PublicOrigin = $PublicOrigin.TrimEnd('/')
}
if ($AssignCurrentUser -and $UserObjectId) { throw 'Choose AssignCurrentUser or UserObjectId, not both.' }
if ($UserObjectId) { Assert-FactoryGuid $UserObjectId 'UserObjectId' }
if (Test-Path -LiteralPath $OutputPath) {
    $previous = Read-FactoryMetadata $OutputPath 'tenant' $Environment -AllowIncomplete
    if ($previous.tenantId -ne $TenantId -or ($ClientId -and $ClientId -ne $previous.clientId)) {
        throw 'Existing output metadata targets a different tenant/application. Use the intended metadata or a different OutputPath.'
    }
    $ClientId = $previous.clientId
    if (!$PublicOrigin) { $PublicOrigin = $previous['publicOrigin'] }
}
if ($ClientId) {
    Assert-FactoryGuid $ClientId 'ClientId'
    $app = Invoke-FactoryAz @('ad', 'app', 'show', '--id', $ClientId)
}
else {
    $matches = @(Invoke-FactoryAz @('ad', 'app', 'list', '--display-name', $DisplayName) | Where-Object displayName -EQ $DisplayName)
    if ($matches.Count -ne 0) {
        throw "An application named $DisplayName already exists. Supply its exact -ClientId to adopt it; names alone are not trusted."
    }
    $app = $null
}
if ($app -and $app.signInAudience -ne 'AzureADMyOrg') {
    throw 'Only an existing single-tenant application may be adopted.'
}
if (!$PSCmdlet.ShouldProcess("$TenantId / $Environment", 'Create or update Factory Entra application, service principal, roles and requested user assignments')) { return }
if (!$app) {
    $app = Invoke-FactoryAz @('ad', 'app', 'create', '--display-name', $DisplayName, '--sign-in-audience', 'AzureADMyOrg')
}
$ClientId = $app.appId
$metadata = @{
    schemaVersion = 1; kind = 'tenant'; environment = $Environment; ready = $false
    tenantId = $TenantId; clientId = $ClientId; applicationObjectId = $app.id
    servicePrincipalObjectId = $null; publicOrigin = $PublicOrigin
}
# Record the application immediately so interrupted runs can resume without name-based adoption.
Write-FactoryMetadata $OutputPath $metadata
$roles = Merge-FactoryAppRoles -Existing @($app.appRoles)
$api = if ($app['api']) { $app.api } else { @{} }
$scopes = @($api['oauth2PermissionScopes'])
$plantScopes = @($scopes | Where-Object value -EQ 'Plant.Access')
if ($plantScopes.Count -gt 1) { throw 'Duplicate Plant.Access scopes. Correct the registration first.' }
if ($plantScopes.Count -eq 1) {
    $plantScopes[0].isEnabled = $true
}
else {
    $scopes += @{
        id = [guid]::NewGuid().ToString(); value = 'Plant.Access'; type = 'User'; isEnabled = $true
        adminConsentDisplayName = 'Access Factory plants'; adminConsentDescription = 'Access plants as the signed-in user.'
        userConsentDisplayName = 'Access Factory plants'; userConsentDescription = 'Access plants as you.'
    }
}
$api['oauth2PermissionScopes'] = @($scopes | Where-Object { $null -ne $_ })
$api['requestedAccessTokenVersion'] = 2
$web = if ($app['web']) { $app.web } else { @{} }
$redirects = @($web['redirectUris'] | Where-Object { $_ })
if ($PublicOrigin) {
    $redirects += "$PublicOrigin/signin-oidc", "$PublicOrigin/signout-callback-oidc"
}
$web['redirectUris'] = @($redirects | Select-Object -Unique)
$web['implicitGrantSettings'] = @{ enableAccessTokenIssuance = $false; enableIdTokenIssuance = $false }
Invoke-FactoryGraph -Method PATCH -Path "applications/$($app.id)" -Body @{
    api = $api; web = $web; appRoles = $roles
    identifierUris = @((@($app.identifierUris) + "api://$ClientId") | Select-Object -Unique)
} | Out-Null
$principals = @(Invoke-FactoryAz @('ad', 'sp', 'list', '--filter', "appId eq '$ClientId'"))
if ($principals.Count -gt 1) { throw 'More than one service principal matched the client ID.' }
$principal = if ($principals.Count -eq 1) { $principals[0] } else { Invoke-FactoryAz @('ad', 'sp', 'create', '--id', $ClientId) }
Invoke-FactoryGraph -Method PATCH -Path "servicePrincipals/$($principal.id)" -Body @{ appRoleAssignmentRequired = $true } | Out-Null
if ($AssignCurrentUser) {
    $user = Invoke-FactoryAz @('ad', 'signed-in-user', 'show')
    $UserObjectId = $user.id
}
if ($UserObjectId) {
    Assert-FactoryGuid $UserObjectId 'UserObjectId'
    $assignments = @()
    $page = Invoke-FactoryGraph -Path "servicePrincipals/$($principal.id)/appRoleAssignedTo"
    while ($null -ne $page) {
        $assignments += @($page.value)
        $page = if ($page['@odata.nextLink']) {
            Invoke-FactoryAz @('rest', '--method', 'GET', '--url', $page['@odata.nextLink'])
        } else { $null }
    }
    foreach ($role in @($roles | Where-Object { $_.value -in @('Factory.User', 'Factory.PlantCreator') })) {
        if (!($assignments | Where-Object { $_.principalId -eq $UserObjectId -and $_.appRoleId -eq $role.id })) {
            Invoke-FactoryGraph -Method POST -Path "servicePrincipals/$($principal.id)/appRoleAssignedTo" -Body @{
                principalId = $UserObjectId; resourceId = $principal.id; appRoleId = $role.id
            } | Out-Null
        }
    }
}
$metadata.servicePrincipalObjectId = $principal.id
$metadata.ready = $true
Write-FactoryMetadata $OutputPath $metadata
Write-Host "Tenant configuration saved: $OutputPath"
if ($Environment -eq 'development') {
    Write-Host "Before local configuration, create or reuse a client secret in Entra > App registrations > $($app.displayName) ($ClientId) > Certificates & secrets."
    Write-Host 'Copy the secret VALUE into your password manager. Set-LocalConfiguration.ps1 will prompt for it without echoing it. No credential was created or rotated by this script.'
}
if (!$UserObjectId) {
    Write-Host 'No user roles were assigned. An Entra administrator must assign Factory.User and, for plant creation, Factory.PlantCreator before browser use.'
}
