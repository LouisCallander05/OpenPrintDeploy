<#
.SYNOPSIS
Produces a self-contained OpenPrintDeploy server artifact for the print server.

.DESCRIPTION
Publishes the server (folder layout) and the installer (also self-contained,
merged into the same folder — they share the same .NET 8 win-x64 runtime
DLLs, so collisions are byte-identical and safe to overwrite). The result is
one folder containing both exes plus the PS install scripts as a fallback:

    OpenPrintDeploy.Server.exe       <- the service
    OpenPrintDeploy.Installer.exe    <- right-click > Run as administrator
    Install-Service.ps1              <- same work in PowerShell
    Uninstall-Service.ps1
    appsettings.json
    ... runtime DLLs ...

.PARAMETER OutDir
Where the publish output lands. Relative paths are resolved against the repo
root. Default: publish/server.

.EXAMPLE
.\scripts\Publish-Server.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutDir = "publish/server",
    # SemVer-ish string baked into AssemblyInformationalVersion. CI passes the
    # git tag with the leading v stripped (e.g. "0.1.3"); local devs leave it
    # blank and the csproj fallback ("0.0.0-dev") wins.
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedOut = if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }
$installerTmp = Join-Path $repoRoot "publish/installer-tmp"

if (Test-Path $resolvedOut)   { Remove-Item -Recurse -Force $resolvedOut }
if (Test-Path $installerTmp)  { Remove-Item -Recurse -Force $installerTmp }

$versionProps = @()
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    Write-Host "Stamping build with version $Version"
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

    Write-Host "Publishing installer (self-contained $Runtime) to $installerTmp..."
    & dotnet publish installer/OpenPrintDeploy.Installer `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        @versionProps `
        -o $installerTmp
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (installer) failed (exit $LASTEXITCODE)" }
}
finally {
    Pop-Location
}

# Merge installer output into the server folder. Shared runtime DLLs are
# byte-identical between the two self-contained publishes on the same RID,
# so overwriting them is safe and saves the user a confusing subfolder.
Write-Host "Merging installer into $resolvedOut..."
Copy-Item -Path (Join-Path $installerTmp "*") -Destination $resolvedOut -Recurse -Force
Remove-Item -Recurse -Force $installerTmp

# Bundle the PowerShell helpers as a fallback (some operators prefer them).
Copy-Item -Path (Join-Path $PSScriptRoot "Install-Service.ps1")   -Destination $resolvedOut -Force
Copy-Item -Path (Join-Path $PSScriptRoot "Uninstall-Service.ps1") -Destination $resolvedOut -Force

# Remove the gitignored dev config so it doesn't leak production-default
# overrides. The published exe will use appsettings.json (Negotiate + Ldap).
$devCfg = Join-Path $resolvedOut "appsettings.Development.json"
if (Test-Path $devCfg) { Remove-Item $devCfg -Force }

$installerExe = Join-Path $resolvedOut "OpenPrintDeploy.Installer.exe"
$serverExe    = Join-Path $resolvedOut "OpenPrintDeploy.Server.exe"
if (-not (Test-Path $installerExe)) { throw "Installer exe missing from publish folder." }
if (-not (Test-Path $serverExe))    { throw "Server exe missing from publish folder." }

Write-Host ""
Write-Host "Publish complete:" -ForegroundColor Green
Write-Host "  $resolvedOut"
Write-Host ""
Write-Host "Next:" -ForegroundColor Green
Write-Host "  1. Copy the folder to the print server."
Write-Host "  2. Right-click OpenPrintDeploy.Installer.exe > Run as administrator."
Write-Host "     (Or use Install-Service.ps1 if PowerShell isn't blocked.)"
