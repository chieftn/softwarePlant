#Requires -Version 7.0
param(
    [ValidateSet("Inspect", "Configure", "Install", "Run", "Stop", "Status", "Recover", "Update", "Rollback", "Uninstall")]
    [string]$Action = "Inspect",
    [string]$PackageDirectory = $PSScriptRoot,
    [string]$StateDirectory = (Join-Path $env:ProgramData "Factory\Workbench"),
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles "Factory\Workbench"),
    [switch]$AllowUnsignedPackage
)

$ErrorActionPreference = "Stop"
if (![System.OperatingSystem]::IsWindows() -or
    [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne
        [System.Runtime.InteropServices.Architecture]::X64) {
    throw "Workbench setup requires Windows x64."
}
$package = [System.IO.Path]::GetFullPath($PackageDirectory)
$executable = Join-Path $package "setup\Factory.Workbench.Setup.exe"
if (!(Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published setup host is missing. Run Publish-Workbench.ps1 and use the setup script in its output."
}
if ($AllowUnsignedPackage -and $Action -notin @("Install", "Update")) {
    throw "-AllowUnsignedPackage applies only to Install or Update."
}
if ($Action -ne "Install" -and $PSBoundParameters.ContainsKey("InstallDirectory")) {
    throw "-InstallDirectory applies only to Install. Subsequent commands use the saved binary location."
}
$arguments = @($Action.ToLowerInvariant(), "--root", [System.IO.Path]::GetFullPath($StateDirectory))
if ($Action -in @("Install", "Update")) {
    $arguments += @("--package", $package)
    if ($AllowUnsignedPackage) {
        $arguments += "--allow-unsigned-package"
    }
    if ($Action -eq "Install") {
        $arguments += @("--install-directory", [System.IO.Path]::GetFullPath($InstallDirectory))
    }
}
& $executable @arguments
exit $LASTEXITCODE
