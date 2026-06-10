<#
.SYNOPSIS
Produces the deployable client artifact: a single self-extracting installer exe.

.DESCRIPTION
Publishes the Tray (via Publish-Tray.ps1), zips that folder, and embeds the zip
into the Client installer so the installer publishes as ONE self-extracting exe.
The operator deploys a single file:

    OpenPrintDeploy.Client.Installer.exe   <- carries the tray inside it

Running it (UAC) extracts the tray to Program Files, configures the server, and
registers logon auto-start. The server URL can be passed with --server, or
encoded in the filename — rename the exe to "OpenPrintDeploy - <host>.exe" and
it configures http://<host>:5080 with no arguments.

For Intune, prefer the MSI (scripts/Publish-Client-Msi.ps1): Intune auto-fills
install/uninstall/detection from the MSI, and the same "<name> - <host>"
filename trick still works (the tray reads the server from the registry the MSI
writes).

.PARAMETER OutDir
Where the publish output lands. Relative paths resolve against the repo root.
Default: publish/client.

.PARAMETER Version
SemVer-ish string baked into the assemblies. CI passes the git tag with the
leading v stripped; local devs leave blank.

.EXAMPLE
.\scripts\Publish-Client.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutDir = "publish/client",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedOut = if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }
$trayDir     = Join-Path $repoRoot "publish/client-tray-tmp"
$payloadZip  = Join-Path $repoRoot "publish/client-tray-payload.zip"

if (Test-Path $resolvedOut) { Remove-Item -Recurse -Force $resolvedOut }
if (Test-Path $payloadZip)  { Remove-Item -Force $payloadZip }

# Publish the self-contained tray (shared with the MSI packager).
& (Join-Path $PSScriptRoot "Publish-Tray.ps1") `
    -OutDir $trayDir -Configuration $Configuration -Runtime $Runtime -Version $Version
if ($LASTEXITCODE -ne 0) { throw "Publish-Tray.ps1 failed (exit $LASTEXITCODE)" }

$versionProps = @()
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $versionProps = @("-p:Version=$Version")
}

# Zip the tray folder so it can be embedded in the installer.
# CreateFromDirectory with includeBaseDirectory=false puts the tray's files at
# the zip root, so the installer extracts them straight into the install dir.
# Windows PowerShell 5.1 (what CI's `shell: powershell` uses) doesn't auto-load
# these assemblies, so load them explicitly before referencing the types.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Write-Host "Zipping tray payload -> $payloadZip..."
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $trayDir, $payloadZip,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

Push-Location $repoRoot
try {
    # Publish the installer as a single self-contained exe with the tray zip
    # embedded (TrayPayloadZip -> <EmbeddedResource> in the .csproj).
    Write-Host "Publishing single-file installer (tray embedded) to $resolvedOut..."
    & dotnet publish installer/OpenPrintDeploy.Client.Installer `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        "-p:TrayPayloadZip=$payloadZip" `
        @versionProps `
        -o $resolvedOut
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (client installer) failed (exit $LASTEXITCODE)" }
}
finally {
    Pop-Location
}

$installerExe = Join-Path $resolvedOut "OpenPrintDeploy.Client.Installer.exe"
if (-not (Test-Path $installerExe)) { throw "Single-file installer exe missing from publish folder." }

# Tidy the intermediates; the single exe in $resolvedOut is the whole artifact.
Remove-Item -Recurse -Force $trayDir
Remove-Item -Force $payloadZip

$sizeMb = [math]::Round((Get-Item $installerExe).Length / 1MB, 1)

Write-Host ""
Write-Host "Publish complete:" -ForegroundColor Green
Write-Host "  $installerExe  (${sizeMb} MB, self-extracting)"
Write-Host ""
Write-Host "Next:" -ForegroundColor Green
Write-Host "  - Test on a workstation (either form works):"
Write-Host "      OpenPrintDeploy.Client.Installer.exe install --server http://printsrv01.corp.local:5080"
Write-Host "      # ...or rename to 'OpenPrintDeploy - printsrv01.corp.local.exe' and just run it"
Write-Host "  - For Intune, build the MSI instead: scripts\Publish-Client-Msi.ps1"
