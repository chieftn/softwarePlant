#Requires -Version 7.0
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('\A[A-Za-z0-9][A-Za-z0-9._-]{0,79}\z')]
    [string]$ReleaseId,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\artifacts\workbench"),
    [string]$SourceRevision = "local-development"
)

$ErrorActionPreference = "Stop"
if ($ReleaseId.EndsWith(".")) {
    throw "ReleaseId must not end with a dot."
}
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and
    (!(Test-Path -LiteralPath $output -PathType Container) -or
        (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1))) {
    throw "OutputDirectory must be a new or empty directory. Use a fresh directory for each package."
}
if ([string]::IsNullOrWhiteSpace($SourceRevision)) {
    throw "SourceRevision must identify this build."
}
$hostOutput = Join-Path $output "host"
$project = Join-Path $PSScriptRoot "..\Factory.Workbench.Host\Factory.Workbench.Host.csproj"
& dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --output $hostOutput --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Workbench publication failed. No complete package manifest was produced; use a fresh output directory to retry."
}
$workerOutput = Join-Path $hostOutput "worker\linux-x64"
$workerProject = Join-Path $PSScriptRoot "..\Factory.Workbench.Worker\Factory.Workbench.Worker.csproj"
& dotnet publish $workerProject --configuration Release --runtime linux-x64 --self-contained true `
    -p:CopilotSkipCliDownload=false --output $workerOutput --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Linux worker publication failed. No complete package manifest was produced; use a fresh output directory to retry."
}
foreach ($required in @("Factory.Workbench.Worker", "Factory.Workbench.Worker.dll", "libhostpolicy.so",
    "runtimes\linux-x64\native\copilot", "runtimes\linux-x64\native\copilot-runtime",
    "runtimes\linux-x64\native\runtime.node")) {
    if (!(Test-Path -LiteralPath (Join-Path $workerOutput $required) -PathType Leaf)) {
        throw "Linux worker payload is incomplete: $required. No complete package manifest was produced."
    }
}
$image = Join-Path $hostOutput "worker\ubuntu-base-24.04.5-base-amd64.tar.gz"
Invoke-WebRequest -Uri 'https://cdimage.ubuntu.com/ubuntu-base/releases/24.04/release/ubuntu-base-24.04.5-base-amd64.tar.gz' `
    -OutFile $image -TimeoutSec 600
if ((Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash -ne
    'E77B6F10C2590CEF872B33EE9F635A0E3FD1F57FB074C0E52B5C7F56147A0C86') {
    throw "Ubuntu base image does not match its pinned checksum. No complete package manifest was produced."
}
$setupOutput = Join-Path $output "setup"
$setupProject = Join-Path $PSScriptRoot "..\Factory.Workbench.Setup\Factory.Workbench.Setup.csproj"
& dotnet publish $setupProject --configuration Release --runtime win-x64 --self-contained true --output $setupOutput --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Workbench setup publication failed. Use a fresh output directory to retry."
}
$files = [ordered]@{}
Get-ChildItem -LiteralPath $hostOutput -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = [System.IO.Path]::GetRelativePath($hostOutput, $_.FullName)
    $files[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
$manifest = [ordered]@{
    schemaVersion = "1"
    releaseId = $ReleaseId
    runtime = "win-x64"
    sourceRevision = $SourceRevision
    files = $files
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Setup-Workbench.ps1") -Destination $output
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output "workbench-package.json") -Encoding utf8NoBOM
Write-Output "Published local development workbench package: $output"
Write-Warning "This package is unsigned. Install only from a trusted local build with -AllowUnsignedPackage."
Write-Output "Contains self-contained Windows x64 setup/service-host executables and the Linux x64 SDK worker/Copilot runtime payload."
Write-Output "The Linux payload is staged under host\worker\linux-x64. Publishing does not provision WSL or enable execution."
