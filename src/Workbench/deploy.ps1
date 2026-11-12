param(
    [Parameter(Mandatory = $true)]
    [string]$Distribution,
    [string]$PackageDirectory = $PSScriptRoot,
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA "Workbench")
)

$ErrorActionPreference = "Stop"
$package = [System.IO.Path]::GetFullPath($PackageDirectory)
$hostSource = Join-Path $package "win-x64\host"
$workerSource = Join-Path $package "linux-x64\worker"
if (!(Test-Path -LiteralPath (Join-Path $hostSource "Workbench.Host.exe"))) {
    throw "Published Windows host is missing."
}
if (!(Test-Path -LiteralPath (Join-Path $workerSource "Workbench.Worker.dll"))) {
    throw "Published Linux worker is missing."
}

$runtimes = & dotnet --list-runtimes
if ($LASTEXITCODE -ne 0 -or !($runtimes -match "^Microsoft.NETCore.App 10\.")) {
    throw "Windows .NET 10 runtime is required."
}
$linuxRuntimes = & wsl.exe --distribution $Distribution --exec dotnet --list-runtimes
if ($LASTEXITCODE -ne 0 -or !($linuxRuntimes -match "^Microsoft.NETCore.App 10\.")) {
    throw ".NET 10 runtime is required in the selected WSL distribution."
}
foreach ($tool in @("git", "timeout")) {
    & wsl.exe --distribution $Distribution --exec $tool --version
    if ($LASTEXITCODE -ne 0) { throw "$tool is required in WSL." }
}
$linuxHome = (& wsl.exe --distribution $Distribution --exec printenv HOME | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or !$linuxHome.StartsWith("/")) {
    throw "Could not resolve the WSL user's home."
}
$linuxSource = (& wsl.exe --distribution $Distribution --exec wslpath -a -u $workerSource | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or !$linuxSource.StartsWith("/")) {
    throw "Could not resolve the package's WSL path."
}
$linuxRoot = "$linuxHome/.local/share/workbench"
& wsl.exe --distribution $Distribution --exec mkdir -p -- "$linuxRoot/worker" "$linuxRoot/assignments"
if ($LASTEXITCODE -ne 0) { throw "Could not prepare WSL deployment directories." }
& wsl.exe --distribution $Distribution --exec cp -a -- "$linuxSource/." "$linuxRoot/worker/"
if ($LASTEXITCODE -ne 0) { throw "Could not copy the WSL worker." }
& wsl.exe --distribution $Distribution --exec chmod +x "$linuxRoot/worker/Workbench.Worker"
if ($LASTEXITCODE -ne 0) { throw "Could not mark the worker executable." }
if (Test-Path -LiteralPath (Join-Path $workerSource "runtimes\linux-x64\native")) {
    & wsl.exe --distribution $Distribution --exec chmod -R u+rx "$linuxRoot/worker/runtimes/linux-x64/native"
    if ($LASTEXITCODE -ne 0) { throw "Could not mark bundled Copilot executables." }
}
New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $hostSource "*") -Destination $InstallDirectory -Recurse -Force

Write-Output "Windows host: $(Join-Path $InstallDirectory 'Workbench.Host.exe')"
Write-Output "WSL worker: $linuxRoot/worker/Workbench.Worker.dll"
Write-Output "WSL workspace root: $linuxRoot/assignments"
