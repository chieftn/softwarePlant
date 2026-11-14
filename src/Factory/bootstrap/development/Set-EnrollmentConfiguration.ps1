#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param([string]$EnrollmentConfiguration)

. (Join-Path $PSScriptRoot '..\Common.ps1')
if (!$EnrollmentConfiguration) {
    $EnrollmentConfiguration = Join-Path $script:FactoryRoot 'artifacts\bootstrap\enrollment-development.json'
}
$enrollment = Read-FactoryMetadata $EnrollmentConfiguration 'workbench-enrollment' 'development'
$configuration = $enrollment.serviceConfiguration.Factory.Enrollment
foreach ($key in @('InstallerClientId', 'ProvisioningClientId', 'ProvisioningObjectId')) {
    Assert-FactoryGuid $configuration[$key] $key
}
if ($configuration.ProvisioningCredential -notin @('ManagedIdentity', 'ClientSecret')) {
    throw 'Select an explicit supported provisioning credential.'
}
$project = Join-Path $script:FactoryRoot 'Factory.Plant.Service\Factory.Plant.Service.csproj'
if (!$PSCmdlet.ShouldProcess($project, 'Set explicit development enrollment user-secrets without changing data-access credentials')) { return }
$settings = @{}
$secret = $null
$pointer = [IntPtr]::Zero
$json = $null
try {
    foreach ($entry in $configuration.GetEnumerator()) { $settings["Factory:Enrollment:$($entry.Key)"] = $entry.Value }
    if ($configuration.ProvisioningCredential -eq 'ClientSecret') {
        $secret = Read-Host 'Dedicated provisioner client secret VALUE (Development only)' -AsSecureString
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
        $settings['Factory:Enrollment:ProvisioningClientSecret'] = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        if ([string]::IsNullOrWhiteSpace($settings['Factory:Enrollment:ProvisioningClientSecret'])) {
            throw 'A nonempty dedicated provisioner secret value is required.'
        }
    }
    $json = $settings | ConvertTo-Json -Compress
    $PSNativeCommandUseErrorActionPreference = $false
    $json | & dotnet user-secrets set --project $project
    if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets failed; enrollment configuration was not completed.' }
}
finally {
    if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    $settings.Clear()
    $json = $null
    if ($secret) { $secret.Dispose() }
}
Write-Host 'Development enrollment configuration saved. Restart the plant service to enable the provisioning worker.'
Write-Warning 'Standard .NET user-secrets are not encrypted. Never put this provisioner credential in the installer profile.'
