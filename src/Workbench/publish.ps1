param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "artifacts"),
    [switch]$UseInstalledCopilot
)

$ErrorActionPreference = "Stop"
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count -gt 0) {
    throw "Output directory is not empty. Choose a fresh -OutputDirectory to avoid mixing package versions or runtime modes."
}
$hostProject = Join-Path $PSScriptRoot "Workbench.Host\Workbench.Host.csproj"
$workerProject = Join-Path $PSScriptRoot "Workbench.Worker\Workbench.Worker.csproj"

& dotnet publish $hostProject -c Release -r win-x64 --self-contained false --output (Join-Path $output "win-x64\host")
if ($LASTEXITCODE -ne 0) { throw "Windows host publish failed." }

$workerArguments = @(
    "publish", $workerProject, "-c", "Release", "-r", "linux-x64",
    "--self-contained", "false", "--output", (Join-Path $output "linux-x64\worker")
)
if ($UseInstalledCopilot) {
    $workerArguments += "-p:CopilotSkipCliDownload=true"
}
& dotnet @workerArguments
if ($LASTEXITCODE -ne 0) { throw "Linux worker publish failed." }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot "README.md") -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "deploy.ps1") -Destination $output
Write-Output "Published to $output"
if ($UseInstalledCopilot) {
    Write-Output "Worker requires COPILOT_CLI_PATH in WSL pointing to a compatible installed Copilot runtime."
}
