#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param()

$ErrorActionPreference = 'Stop'
if (![Environment]::Is64BitOperatingSystem -or ![Environment]::Is64BitProcess) {
    throw 'Run this repair in 64-bit PowerShell on the Windows test machine.'
}
$parent = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Factory'
$root = Join-Path $parent 'Workbench'
$logs = Join-Path $root 'logs'
$runtime = Join-Path $root 'runtime'
$administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$users = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')

function Assert-RegularEntry([IO.FileSystemInfo]$entry) {
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to repair a linked entry: $($entry.FullName)"
    }
}

foreach ($path in @($parent, $root, $logs)) {
    $entry = Get-Item -LiteralPath $path
    Assert-RegularEntry $entry
    if ($entry -isnot [IO.DirectoryInfo]) { throw "Expected a directory: $path" }
    $owner = (Get-Acl -LiteralPath $path).GetOwner([Security.Principal.SecurityIdentifier])
    if ($path -ne $logs -and $owner -ne $administrators -and $owner -ne $system) {
        throw "Refusing to repair a directory with an unexpected owner: $path"
    }
}
if (Test-Path -LiteralPath 'HKLM:\SOFTWARE\Factory\WorkbenchInstaller') {
    throw 'An MSI registration exists. This repair is only for a failed fresh installation; use installer maintenance instead.'
}
if (@(Get-Service -Name 'Factory.Workbench.*').Count -ne 0) {
    throw 'A workbench service exists. Refusing to repair state without installer maintenance.'
}
$siblings = @(Get-ChildItem -LiteralPath $parent -Force)
if ($siblings.Count -ne 1 -or $siblings[0].FullName -ne $root) {
    throw 'Factory contains other entries. Refusing to change a shared parent directory.'
}
foreach ($entry in Get-ChildItem -LiteralPath $root -Force) {
    Assert-RegularEntry $entry
    if ($entry -is [IO.DirectoryInfo]) {
        if ($entry.Name -notin @('logs', 'runtime')) { throw 'Unknown state directory; repair refused.' }
    } elseif ($entry.Name -ne 'maintenance.lock') {
        throw 'Configuration, enrollment, installation or unknown state exists; repair refused.'
    }
}
$logFiles = @(Get-ChildItem -LiteralPath $logs -Force)
if ($logFiles.Count -eq 0) { throw 'No failed-install diagnostic evidence exists; repair refused.' }
foreach ($entry in $logFiles) {
    Assert-RegularEntry $entry
    if ($entry -isnot [IO.FileInfo] -or $entry.Name -notmatch '^setup-[a-f0-9]{32}\.jsonl$' -or $entry.Length -gt 262144) {
        throw 'Unknown or oversized log entry; repair refused.'
    }
    foreach ($line in Get-Content -LiteralPath $entry.FullName) {
        $record = $line | ConvertFrom-Json
        if ($record.stage -notmatch '^(msi-rollback|msi-validate-helper|helper-[a-z-]+|setup)$') {
            throw 'Logs are not limited to pre-enrollment installer diagnostics; repair refused.'
        }
    }
}
$runtimeFiles = @()
if (Test-Path -LiteralPath $runtime) {
    Assert-RegularEntry (Get-Item -LiteralPath $runtime)
    $runtimeFiles = @(Get-ChildItem -LiteralPath $runtime -Force)
    foreach ($entry in $runtimeFiles) {
        Assert-RegularEntry $entry
        if ($entry -isnot [IO.FileInfo] -or $entry.Name -notin @('runtime.lock', 'plant-reports.lock')) {
            throw 'Unknown runtime state; repair refused.'
        }
    }
}

if ($PSCmdlet.ShouldProcess("$parent and its verified logs-only Workbench state",
        'Restore protected directory permissions; preserve all diagnostic and lock files')) {
    foreach ($path in @($parent, $root, $logs) + @($runtime | Where-Object { Test-Path -LiteralPath $_ })) {
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetOwner($administrators)
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($principal in @($administrators, $system)) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($principal, 'FullControl',
                'ContainerInherit, ObjectInherit', 'None', 'Allow'))
        }
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($users, 'ReadAndExecute', 'Allow'))
        Set-Acl -LiteralPath $path -AclObject $acl
    }
    $privateFiles = @($logFiles) + @($runtimeFiles)
    $maintenance = Join-Path $root 'maintenance.lock'
    if (Test-Path -LiteralPath $maintenance) { $privateFiles += Get-Item -LiteralPath $maintenance }
    foreach ($entry in $privateFiles) {
        $acl = [Security.AccessControl.FileSecurity]::new()
        $acl.SetOwner($administrators)
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($principal in @($administrators, $system)) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($principal, 'FullControl', 'Allow'))
        }
        Set-Acl -LiteralPath $entry.FullName -AclObject $acl
    }
    Write-Output 'Repaired verified failed-install state permissions. No files, services, certificates or enrollments were removed.'
}
