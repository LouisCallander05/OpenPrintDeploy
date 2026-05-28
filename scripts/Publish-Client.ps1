<#
.SYNOPSIS
Produces the deployable client artifact (tray app + installer).

.DESCRIPTION
Publishes the Tray as a self-contained win-x64 folder build and the Client
installer alongside it (also self-contained), merging both into one folder.
Both targets are self-contained on the same .NET 8 win-x64 runtime, so the
shared runtime DLLs are byte-identical and safe to overwrite.

The result is one folder the operator can copy to a workstation and run:

    OpenPrintDeploy.Client.Tray.exe          <- the tray
    OpenPrintDeploy.Client.Installer.exe     <- per-machine install (UAC)
    appsettings.json                         <- placeholder; installer rewrites
    ... runtime DLLs ...

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
$installerTmp = Join-Path $repoRoot "publish/client-installer-tmp"

if (Test-Path $resolvedOut)  { Remove-Item -Recurse -Force $resolvedOut }
if (Test-Path $installerTmp) { Remove-Item -Recurse -Force $installerTmp }

$versionProps = @()
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    Write-Host "Stamping build with version $Version"
    $versionProps = @("-p:Version=$Version")
}

Push-Location $repoRoot
try {
    Write-Host "Publishing tray (self-contained $Runtime) to $resolvedOut..."
    & dotnet publish src/OpenPrintDeploy.Client.Tray `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        @versionProps `
        -o $resolvedOut
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (tray) failed (exit $LASTEXITCODE)" }

    Write-Host "Publishing client installer (self-contained $Runtime) to $installerTmp..."
    & dotnet publish installer/OpenPrintDeploy.Client.Installer `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        @versionProps `
        -o $installerTmp
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (client installer) failed (exit $LASTEXITCODE)" }
}
finally {
    Pop-Location
}

Write-Host "Merging installer into $resolvedOut..."
Copy-Item -Path (Join-Path $installerTmp "*") -Destination $resolvedOut -Recurse -Force
Remove-Item -Recurse -Force $installerTmp

$trayExe      = Join-Path $resolvedOut "OpenPrintDeploy.Client.Tray.exe"
$installerExe = Join-Path $resolvedOut "OpenPrintDeploy.Client.Installer.exe"
if (-not (Test-Path $trayExe))      { throw "Tray exe missing from publish folder." }
if (-not (Test-Path $installerExe)) { throw "Client installer exe missing from publish folder." }

# Catch a class of silently-broken self-contained WPF publishes. If the build
# config ever drops the WindowsDesktop runtime pack from deps.json again, the
# tray would launch on a dev box (where the runtime is installed system-wide)
# but crash with FileNotFoundException for WindowsBase on a clean endpoint.
# Better to fail the publish here than to ship that to Intune.
$depsJsonPath = Join-Path $resolvedOut "OpenPrintDeploy.Client.Tray.deps.json"
if (-not (Test-Path $depsJsonPath)) { throw "deps.json missing from tray publish -- did the publish silently fail?" }
$depsRaw = Get-Content $depsJsonPath -Raw
if ($depsRaw -notmatch 'runtimepack\.Microsoft\.WindowsDesktop\.App\.Runtime') {
    throw ("Self-contained publish is missing the WindowsDesktop runtime pack in deps.json. " +
           "Check EnableWindowsTargeting in OpenPrintDeploy.Client.Tray.csproj -- it must NOT be set on Windows.")
}
if ($depsRaw -notmatch 'WindowsBase\.dll') {
    throw "Self-contained publish does not register WindowsBase.dll in deps.json. The bundle will crash on clean machines."
}

Write-Host ""
Write-Host "Publish complete:" -ForegroundColor Green
Write-Host "  $resolvedOut"
Write-Host ""
Write-Host "Next:" -ForegroundColor Green
Write-Host "  - Test on a workstation:"
Write-Host "      OpenPrintDeploy.Client.Installer.exe install --server http://printsrv01.corp.local:5080"
Write-Host "  - To deploy via Intune, wrap the folder as .intunewin:"
Write-Host "      IntuneWinAppUtil.exe -c $resolvedOut -s OpenPrintDeploy.Client.Installer.exe -o <out>"
