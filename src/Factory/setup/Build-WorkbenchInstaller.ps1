#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$ReleaseId,
    [Parameter(Mandatory)][ValidatePattern('\A\d+\.\d+\.\d+\z')][string]$Version,
    [Parameter(Mandatory)][string]$ProfilePath,
    [Parameter(Mandatory)][switch]$AllowUnsignedPackage,
    [switch]$SkipMsiValidation,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\artifacts\installer\$Version-$ReleaseId")
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'Build the Windows x64 installer on Windows x64.'
}
if (!$AllowUnsignedPackage) { throw 'This development MSI is unsigned; explicit -AllowUnsignedPackage is required.' }
$parsedVersion = [version]$Version
if ($parsedVersion.Major -gt 255 -or $parsedVersion.Minor -gt 255 -or $parsedVersion.Build -gt 65535) {
    throw 'MSI version components must not exceed 255.255.65535.'
}
$profile = Get-Content -LiteralPath $ProfilePath -Raw | ConvertFrom-Json -AsHashtable
if ($profile.schemaVersion -ne '1' -or
    @($profile.Keys | Where-Object { $_ -notin @('schemaVersion', 'plantServiceUrl', 'tenantId', 'apiClientId', 'installerClientId') }).Count) {
    throw 'Supply a version-1 workbench installation profile, not a bootstrap metadata/credential file.'
}
foreach ($key in @('tenantId', 'apiClientId', 'installerClientId')) {
    $id = [guid]::Empty
    if (![guid]::TryParseExact([string]$profile[$key], 'D', [ref]$id) -or $id -eq [guid]::Empty) {
        throw "Profile $key must be a nonempty UUID."
    }
}
$origin = $null
if (![uri]::TryCreate($profile.plantServiceUrl, [UriKind]::Absolute, [ref]$origin) -or $origin.Scheme -ne 'https' -or
    $origin.UserInfo -or $origin.AbsolutePath -ne '/' -or $origin.Query -or $origin.Fragment) {
    throw 'Profile plantServiceUrl must be an HTTPS origin.'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1)) {
    throw 'OutputDirectory must be new or empty. Use a fresh directory for each build.'
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$package = Join-Path $output 'runtime-package'
& (Join-Path $PSScriptRoot 'Publish-Workbench.ps1') -ReleaseId $ReleaseId -OutputDirectory $package
$image = Join-Path $output 'image'
& dotnet publish (Join-Path $PSScriptRoot '..\Factory.Workbench.Installer\Factory.Workbench.Installer.csproj') `
    --configuration Release --runtime win-x64 --self-contained true "-p:Version=$Version" --output $image --nologo
if ($LASTEXITCODE -ne 0) { throw 'Graphical setup publication failed.' }
New-Item -ItemType Directory -Path (Join-Path $image 'payload') | Out-Null
Copy-Item -LiteralPath (Join-Path $package 'host') -Destination (Join-Path $image 'payload') -Recurse
Copy-Item -LiteralPath (Join-Path $package 'workbench-package.json') -Destination (Join-Path $image 'payload')
$profile | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $image 'installation-profile.json') -Encoding utf8NoBOM
$helper = Join-Path $output 'helper'
& dotnet publish (Join-Path $PSScriptRoot '..\Factory.Workbench.Setup\Factory.Workbench.Setup.csproj') `
    --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true "-p:Version=$Version" --output $helper --nologo
if ($LASTEXITCODE -ne 0) { throw 'Embedded lifecycle helper publication failed.' }
Copy-Item -LiteralPath (Join-Path $helper 'Factory.Workbench.Setup.exe') -Destination $image -Force

$namespace = 'http://wixtoolset.org/schemas/v4/wxs'
$document = [xml]"<Wix xmlns='$namespace'><Fragment><DirectoryRef Id='INSTALLFOLDER'/><ComponentGroup Id='PublishedPayload'/></Fragment></Wix>"
$fragment = $document.DocumentElement.FirstChild
$directories = @{ '' = $fragment.FirstChild }
$group = $fragment.LastChild
function Get-Identifier([string]$value) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($value.ToLowerInvariant()))).Substring(0, 24)
}
function Add-Directory([string]$relative) {
    if ($directories.ContainsKey($relative)) { return }
    $parent = [IO.Path]::GetDirectoryName($relative)
    if (!$parent) { $parent = '' }
    Add-Directory $parent
    $element = $document.CreateElement('Directory', $namespace)
    $element.SetAttribute('Id', "D$(Get-Identifier $relative)")
    $element.SetAttribute('Name', [IO.Path]::GetFileName($relative))
    $directories[$parent].AppendChild($element) | Out-Null
    $directories[$relative] = $element
}
Get-ChildItem -LiteralPath $image -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($image, $_.FullName)
    $directory = [IO.Path]::GetDirectoryName($relative)
    if (!$directory) { $directory = '' }
    Add-Directory $directory
    $id = Get-Identifier $relative
    $component = $document.CreateElement('Component', $namespace)
    $component.SetAttribute('Id', "C$id")
    $component.SetAttribute('Guid', '*')
    $component.SetAttribute('Directory', $(if ($directory) { "D$(Get-Identifier $directory)" } else { 'INSTALLFOLDER' }))
    $file = $document.CreateElement('File', $namespace)
    $file.SetAttribute('Id', $(if ($relative -eq 'Factory.Workbench.Installer.exe') { 'WorkbenchSetup' }
        elseif ($relative -eq 'Factory.Workbench.Setup.exe') { 'LifecycleTool' } else { "F$id" }))
    $file.SetAttribute('Source', $_.FullName)
    $file.SetAttribute('KeyPath', 'yes')
    $component.AppendChild($file) | Out-Null
    $group.AppendChild($component) | Out-Null
}
$generated = Join-Path $output 'PublishedPayload.wxs'
$document.Save($generated)
$project = Join-Path $PSScriptRoot '..\Factory.Workbench.Package\Factory.Workbench.Package.wixproj'
& dotnet build $project --configuration Release "-p:ProductVersion=$Version" "-p:BuildRoot=$image" `
    "-p:HelperPath=$(Join-Path $helper 'Factory.Workbench.Setup.exe')" "-p:GeneratedPayload=$generated" `
    "-p:SuppressValidation=$($SkipMsiValidation.IsPresent)" `
    "-p:OutputPath=$(Join-Path $output 'msi')" --nologo --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Windows Installer package build failed.' }
$built = @(Get-ChildItem -LiteralPath (Join-Path $output 'msi') -Filter "Factory.Workbench.$Version.msi" -File -Recurse)
if ($built.Count -ne 1) { throw 'Expected exactly one completed MSI output.' }
Write-Output "Unsigned development MSI: $($built[0].FullName)"
Write-Warning 'End users launch the MSI, not PowerShell. Entra/Graph provisioning must be configured by the service administrator first.'
if ($SkipMsiValidation) { Write-Warning 'Windows Installer ICE validation was explicitly skipped. Run it in the isolated installer acceptance environment.' }
