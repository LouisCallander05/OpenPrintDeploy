<#
.SYNOPSIS
Installs OpenPrintDeploy.Server as a Windows service running under Local SYSTEM.

.DESCRIPTION
Run from inside the publish folder (the one produced by Publish-Server.ps1)
in an *elevated* PowerShell session. The script:

  1. Verifies it's running elevated.
  2. Copies the contents of this folder to -InstallDir.
  3. Registers a Windows service (Local SYSTEM, autostart).
  4. Adds an inbound TCP firewall rule on -Port.
  5. Starts the service.

The service authenticates to AD as the computer account (PRINTSRV01$).
First-time setup: browse to http://<this-host>:<port>/admin/directory and click
"Test connection" to confirm the LDAP bind and auto-discovered DC.

.PARAMETER InstallDir
Where the service binaries land. Default: C:\Program Files\OpenPrintDeploy.

.PARAMETER ServiceName
SCM name for the service. Default: OpenPrintDeployServer.

.PARAMETER Port
TCP port for the admin UI and /sync. Must match the Urls config. Default: 5080.

.EXAMPLE
.\Install-Service.ps1
#>
[CmdletBinding()]
param(
    [string]$InstallDir   = "C:\Program Files\OpenPrintDeploy",
    [string]$ServiceName  = "OpenPrintDeployServer",
    [string]$DisplayName  = "OpenPrintDeploy Server",
    [int]$Port            = 5080
)

$ErrorActionPreference = "Stop"

function Assert-Elevated {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $p  = New-Object System.Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Install-Service.ps1 must be run from an elevated PowerShell session."
    }
}

Assert-Elevated

$sourceDir = $PSScriptRoot
$exeName   = "OpenPrintDeploy.Server.exe"
$sourceExe = Join-Path $sourceDir $exeName
if (-not (Test-Path $sourceExe)) {
    throw "Could not find $exeName in $sourceDir. Run this script from the publish folder."
}

# Stop + remove any prior install so this script is idempotent.
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing service $ServiceName..."
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    Write-Host "Removing existing service $ServiceName..."
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

if (-not (Test-Path $InstallDir)) {
    Write-Host "Creating $InstallDir..."
    New-Item -ItemType Directory -Path $InstallDir | Out-Null
}

Write-Host "Copying binaries to $InstallDir..."
# robocopy survives files locked by an unrelated process and gives a sane exit
# code on partial-success scenarios; we accept 0-3 (no error / files copied).
& robocopy $sourceDir $InstallDir /MIR /NFL /NDL /NP /NJH /NJS | Out-Null
if ($LASTEXITCODE -gt 7) { throw "robocopy failed with exit $LASTEXITCODE." }
$global:LASTEXITCODE = 0

$installedExe = Join-Path $InstallDir $exeName

Write-Host "Registering service $ServiceName..."
New-Service `
    -Name $ServiceName `
    -DisplayName $DisplayName `
    -Description "OpenPrintDeploy admin server + /sync API for zone-driven printer deployment." `
    -BinaryPathName "`"$installedExe`"" `
    -StartupType Automatic | Out-Null

$ruleName = "OpenPrintDeploy ($Port/tcp)"
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    Write-Host "Adding firewall rule '$ruleName'..."
    New-NetFirewallRule `
        -DisplayName $ruleName `
        -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port `
        -Profile Any | Out-Null
}

Write-Host "Starting $ServiceName..."
Start-Service $ServiceName

Write-Host ""
Write-Host "Installed." -ForegroundColor Green
Write-Host "  Service:    $ServiceName ($DisplayName)"
Write-Host "  Identity:   LocalSystem (computer account in AD)"
Write-Host "  Install:    $InstallDir"
Write-Host "  Database:   $env:ProgramData\OpenPrintDeploy\app.db"
Write-Host "  Admin URL:  http://$(hostname):$Port/"
Write-Host "  Diagnostics: http://$(hostname):$Port/admin/directory"
Write-Host ""
Write-Host "Logs: Event Viewer > Windows Logs > Application (Source: $ServiceName)."
