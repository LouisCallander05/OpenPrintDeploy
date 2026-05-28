<#
.SYNOPSIS
Stops and removes the OpenPrintDeploy.Server Windows service.

.DESCRIPTION
Mirror of Install-Service.ps1. Run elevated. By default leaves the install
directory and the database in place so an upgrade-by-reinstall is non-destructive;
pass -RemoveData to also delete %ProgramData%\OpenPrintDeploy.

.PARAMETER RemoveData
Delete the install directory and the database directory as well. Off by default
so an accidental uninstall doesn't lose your printer/zone config.

.EXAMPLE
.\Uninstall-Service.ps1
.\Uninstall-Service.ps1 -RemoveData
#>
[CmdletBinding()]
param(
    [string]$InstallDir  = "C:\Program Files\OpenPrintDeploy",
    [string]$ServiceName = "OpenPrintDeployServer",
    [int]$Port           = 5080,
    [switch]$RemoveData
)

$ErrorActionPreference = "Stop"

function Assert-Elevated {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $p  = New-Object System.Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Uninstall-Service.ps1 must be run from an elevated PowerShell session."
    }
}

Assert-Elevated

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping service $ServiceName..."
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    Write-Host "Removing service $ServiceName..."
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
} else {
    Write-Host "Service $ServiceName was not registered; nothing to remove."
}

$ruleName = "OpenPrintDeploy ($Port/tcp)"
$rule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
if ($rule) {
    Write-Host "Removing firewall rule '$ruleName'..."
    Remove-NetFirewallRule -DisplayName $ruleName
}

if ($RemoveData) {
    if (Test-Path $InstallDir) {
        Write-Host "Removing $InstallDir..."
        Remove-Item -Recurse -Force $InstallDir
    }
    $dataDir = Join-Path $env:ProgramData "OpenPrintDeploy"
    if (Test-Path $dataDir) {
        Write-Host "Removing $dataDir (database)..."
        Remove-Item -Recurse -Force $dataDir
    }
} else {
    Write-Host "Left $InstallDir and %ProgramData%\OpenPrintDeploy in place."
    Write-Host "Pass -RemoveData to delete them as well."
}

Write-Host ""
Write-Host "Uninstalled." -ForegroundColor Green
