<#
.SYNOPSIS
Builds the OpenPrintDeploy server MSI for the print server.

.DESCRIPTION
Publishes the server as a self-contained win-x64 folder, bundles the tray-client
installer under client/ so the server can hand it out at /download/client, then
builds a per-machine MSI from that folder with the WiX Toolset. The result is a
single file:

    publish/OpenPrintDeploy.Server.msi

Double-click it (UAC elevates) or run `msiexec /i OpenPrintDeploy.Server.msi /quiet`.
The MSI installs to C:\Program Files\OpenPrintDeploy, registers the
OpenPrintDeployServer Windows service (Local SYSTEM, autostart) with restart-on-
failure recovery, opens TCP 5080 in the firewall, and adds a Start Menu shortcut
to the admin UI. Uninstall via Add/Remove Programs reverses all of it.

The target machine doesn't need the .NET 8 hosting bundle (self-contained). The
*build* machine needs the .NET 8 SDK; the WiX SDK + extensions restore from
NuGet automatically during `dotnet build` (no global tool required).

.PARAMETER OutDir
Where the intermediate self-contained publish folder lands (this becomes the
MSI payload). Relative paths resolve against the repo root. Default: publish/server.

.EXAMPLE
.\scripts\Publish-Server.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutDir = "publish/server",
    # SemVer-ish string. CI passes the git tag with the leading v stripped
    # (e.g. "0.1.3"); local devs leave it blank. It's sanitized to a numeric
    # MSI ProductVersion below (MSI versions can't carry a pre-release suffix).
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

# MSI ProductVersion must be numeric major.minor.build. Drop any pre-release
# suffix ("-rc1", "+build") and pad missing/non-numeric parts with 0. Blank
# (local dev) becomes 0.0.0.
function Get-MsiVersion([string]$v) {
    if ([string]::IsNullOrWhiteSpace($v)) { return "0.0.0" }
    $core  = ($v -split '[-+]')[0]
    $parts = $core.Split('.')
    $nums  = for ($i = 0; $i -lt 3; $i++) {
        if ($i -lt $parts.Count -and $parts[$i] -match '^\d+$') { $parts[$i] } else { '0' }
    }
    return ($nums -join '.')
}

$repoRoot    = Split-Path -Parent $PSScriptRoot
$resolvedOut = if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }
$msiVersion  = Get-MsiVersion $Version

if (Test-Path $resolvedOut) { Remove-Item -Recurse -Force $resolvedOut }

$versionProps = @()
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    Write-Host "Stamping build with version $Version (MSI $msiVersion)"
    $versionProps = @("-p:Version=$Version")
}

Push-Location $repoRoot
try {
    Write-Host "Publishing server (self-contained $Runtime) to $resolvedOut..."
    & dotnet publish src/OpenPrintDeploy.Server `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        @versionProps `
        -o $resolvedOut
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (server) failed (exit $LASTEXITCODE)" }
}
finally {
    Pop-Location
}

# Bundle the single-file tray-client installer under client/ so admins can
# download it straight from the server UI (GET /download/client), pre-named for
# this host. Built fresh here so it always matches this release — and it must
# land in the payload BEFORE the MSI is built so WiX harvests it.
$clientTmp = Join-Path $repoRoot "publish/server-client-tmp"
if (Test-Path $clientTmp) { Remove-Item -Recurse -Force $clientTmp }
Write-Host "Building tray-client installer to bundle for download..."
& (Join-Path $PSScriptRoot "Publish-Client.ps1") `
    -Configuration $Configuration -Runtime $Runtime -OutDir $clientTmp -Version $Version
$clientSrc = Join-Path $clientTmp "OpenPrintDeploy.Client.Installer.exe"
if (-not (Test-Path $clientSrc)) { throw "Client installer exe missing after Publish-Client.ps1." }
$clientDest = Join-Path $resolvedOut "client"
New-Item -ItemType Directory -Force -Path $clientDest | Out-Null
Copy-Item -Path $clientSrc -Destination $clientDest -Force
Remove-Item -Recurse -Force $clientTmp

# Also bundle the client MSI (for Intune) so the server serves it pre-named at
# /download/client-msi. Lands in publish/OpenPrintDeploy.Client.msi (also a
# release asset) and is copied into the payload before WiX harvests it.
Write-Host "Building tray-client MSI to bundle for download..."
& (Join-Path $PSScriptRoot "Publish-Client-Msi.ps1") `
    -Configuration $Configuration -Runtime $Runtime -Version $Version
$clientMsiSrc = Join-Path $repoRoot "publish/OpenPrintDeploy.Client.msi"
if (-not (Test-Path $clientMsiSrc)) { throw "Client MSI missing after Publish-Client-Msi.ps1." }
Copy-Item -Path $clientMsiSrc -Destination $clientDest -Force

# Remove the gitignored dev config so it doesn't leak production-default
# overrides into the MSI. The service uses appsettings.json (Negotiate + Ldap).
$devCfg = Join-Path $resolvedOut "appsettings.Development.json"
if (Test-Path $devCfg) { Remove-Item $devCfg -Force }

$serverExe = Join-Path $resolvedOut "OpenPrintDeploy.Server.exe"
if (-not (Test-Path $serverExe)) { throw "Server exe missing from publish folder." }

# Build the MSI from the publish folder. The WiX SDK + extensions restore from
# NuGet on first build. HarvestDir points WiX at the payload; ProductVersion is
# the sanitized numeric version.
$msiProj    = Join-Path $repoRoot "installer/OpenPrintDeploy.Server.Msi/OpenPrintDeploy.Server.Msi.wixproj"
$msiProjDir = Split-Path -Parent $msiProj
$msiBin     = Join-Path $msiProjDir "bin"
if (Test-Path $msiBin) { Remove-Item -Recurse -Force $msiBin }

Write-Host "Building MSI (WiX) from $resolvedOut..."
& dotnet build $msiProj `
    -c $Configuration `
    "-p:HarvestDir=$resolvedOut" `
    "-p:ProductVersion=$msiVersion"
if ($LASTEXITCODE -ne 0) { throw "dotnet build (MSI) failed (exit $LASTEXITCODE)" }

$builtMsi = Get-ChildItem -Path $msiBin -Filter *.msi -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1
if (-not $builtMsi) { throw "MSI not found under $msiBin after build." }

$finalMsi = Join-Path $repoRoot "publish/OpenPrintDeploy.Server.msi"
Copy-Item -Path $builtMsi.FullName -Destination $finalMsi -Force

Write-Host ""
Write-Host "Publish complete:" -ForegroundColor Green
Write-Host "  $finalMsi"
Write-Host ""
Write-Host "Next:" -ForegroundColor Green
Write-Host "  1. Copy OpenPrintDeploy.Server.msi to the print server."
Write-Host "  2. Double-click it (UAC elevates), or: msiexec /i OpenPrintDeploy.Server.msi /quiet"
Write-Host "  3. Open the admin UI from the Start Menu (OpenPrintDeploy > OpenPrintDeploy Admin),"
Write-Host "     or browse to https://localhost:5443/."
Write-Host "  4. Admins can then download the tray client from the dashboard"
Write-Host "     (or GET /download/client) pre-named for this server."
