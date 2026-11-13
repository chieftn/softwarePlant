#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$TenantConfiguration,
    [string]$DevelopmentConfiguration
)

. (Join-Path $PSScriptRoot '..\Common.ps1')
if (!$TenantConfiguration) { $TenantConfiguration = Join-Path $script:FactoryRoot 'artifacts\bootstrap\tenant-development.json' }
if (!$DevelopmentConfiguration) { $DevelopmentConfiguration = Join-Path $script:FactoryRoot 'artifacts\bootstrap\development.json' }
$tenant = Read-FactoryMetadata $TenantConfiguration 'tenant' 'development'
$development = Read-FactoryMetadata $DevelopmentConfiguration 'development' 'development'
Assert-FactoryLocalConfiguration $tenant $development
$project = Join-Path $script:FactoryRoot 'Factory.Plant.Service\Factory.Plant.Service.csproj'
if (!$PSCmdlet.ShouldProcess($project, 'Trust the standard ASP.NET HTTPS certificate and set development user-secrets')) { return }
$PSNativeCommandUseErrorActionPreference = $false
& dotnet dev-certs https --trust
if ($LASTEXITCODE -ne 0) { throw 'HTTPS certificate setup failed. Resolve the certificate prompt/error before configuring secrets.' }
Set-FactoryLocalSecrets $tenant $development $project
Write-Host 'Local configuration saved with standard .NET user-secrets (not encrypted). No Azure CLI login is needed at application runtime.'
Write-Host 'From the repository root: dotnet run --project .\src\Factory\Factory.Plant.Service --launch-profile https'
Write-Host 'Open https://localhost:7443/plants, sign in, and create a plant.'
