<#
.SYNOPSIS
Builds the Open Print Deploy tray client as a per-machine MSI (for Intune).

.DESCRIPTION
Publishes the tray (self-contained win-x64, via Publish-Tray.ps1) and builds an
MSI from it with the WiX Toolset. The result is a single file:

    publish/OpenPrintDeploy.Client.msi

Because it's an MSI, uploading the wrapped .intunewin to Intune auto-fills the
install command, uninstall command, and detection rule. Configure the server one
of two ways (both survive into Intune):

    msiexec /i "OpenPrintDeploy.Client.msi" SERVER="https://printsrv01:5443"

...or rename the MSI to "OpenPrintDeploy - printsrv01.msi" before wrapping it,
and the tray derives https://printsrv01:5443 from the filename.

The *build* machine needs the .NET 8 SDK; the WiX SDK restores from NuGet during
`dotnet build` (no global tool required).

.PARAMETER Version
SemVer-ish string. CI passes the git tag with the leading v stripped; local devs
leave it blank. Sanitized to a numeric MSI ProductVersion below.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

# MSI ProductVersion must be numeric major.minor.build. Drop any pre-release
# suffix and pad missing/non-numeric parts with 0. Blank (local dev) -> 0.0.0.
function Get-MsiVersion([string]$v) {
    if ([string]::IsNullOrWhiteSpace($v)) { return "0.0.0" }
    $core  = ($v -split '[-+]')[0]
    $parts = $core.Split('.')
    $nums  = for ($i = 0; $i -lt 3; $i++) {
        if ($i -lt $parts.Count -and $parts[$i] -match '^\d+$') { $parts[$i] } else { '0' }
    }
    return ($nums -join '.')
}

$repoRoot   = Split-Path -Parent $PSScriptRoot
$trayDir    = Join-Path $repoRoot "publish/client-tray"
$msiVersion = Get-MsiVersion $Version

# Publish the self-contained tray (shared with the EXE packager).
& (Join-Path $PSScriptRoot "Publish-Tray.ps1") `
    -OutDir $trayDir -Configuration $Configuration -Runtime $Runtime -Version $Version
if ($LASTEXITCODE -ne 0) { throw "Publish-Tray.ps1 failed (exit $LASTEXITCODE)" }

# Build the MSI from the tray publish folder. The WiX SDK restores from NuGet on
# first build. HarvestDir points WiX at the payload; ProductVersion is numeric.
$msiProj    = Join-Path $repoRoot "installer/OpenPrintDeploy.Client.Msi/OpenPrintDeploy.Client.Msi.wixproj"
$msiProjDir = Split-Path -Parent $msiProj
$msiBin     = Join-Path $msiProjDir "bin"
if (Test-Path $msiBin) { Remove-Item -Recurse -Force $msiBin }

Write-Host "Building client MSI (WiX) from $trayDir..."
& dotnet build $msiProj `
    -c $Configuration `
    "-p:HarvestDir=$trayDir" `
    "-p:ProductVersion=$msiVersion"
if ($LASTEXITCODE -ne 0) { throw "dotnet build (client MSI) failed (exit $LASTEXITCODE)" }

$builtMsi = Get-ChildItem -Path $msiBin -Filter *.msi -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1
if (-not $builtMsi) { throw "MSI not found under $msiBin after build." }

$finalMsi = Join-Path $repoRoot "publish/OpenPrintDeploy.Client.msi"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $finalMsi) | Out-Null
Copy-Item -Path $builtMsi.FullName -Destination $finalMsi -Force

# Tidy the tray intermediate; the MSI is the whole artifact.
Remove-Item -Recurse -Force $trayDir

Write-Host ""
Write-Host "Publish complete:" -ForegroundColor Green
Write-Host "  $finalMsi"
Write-Host ""
Write-Host "Deploy via Intune:" -ForegroundColor Green
Write-Host "  1. (Optional) rename to 'OpenPrintDeploy - <host>.msi' to bake in the server,"
Write-Host "     or pass SERVER=... in the install command instead."
Write-Host "  2. Wrap it:  IntuneWinAppUtil.exe -c <folder> -s OpenPrintDeploy.Client.msi -o <out>"
Write-Host "  3. New Win32 app in Intune -> upload the .intunewin. Install/uninstall/detection auto-fill."
Write-Host "     If you didn't rename, add SERVER=`"https://<host>:5443`" to the install command."
