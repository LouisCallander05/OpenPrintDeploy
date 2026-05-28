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

# The publish merges files from both runtime packs (Microsoft.NETCore.App.Runtime
# and Microsoft.WindowsDesktop.App.Runtime). They share several filenames --
# the most important being WindowsBase.dll. NETCore.App ships a 16KB legacy
# facade with AssemblyVersion 4.0.0.0; WindowsDesktop.App ships the real 2.2MB
# implementation with AssemblyVersion 8.0.0.0. The MSBuild publish target
# happens to copy NETCore's facade *after* WindowsDesktop's real file, so the
# bundled WindowsBase ends up as the broken facade. deps.json still says
# "look up WindowsBase.dll under the WindowsDesktop runtime pack", so the .NET
# loader asks for Version=8.0.0.0, gets the file but sees Version=4.0.0.0
# metadata, and the tray crashes at startup on every clean endpoint with
# "Could not load file or assembly 'WindowsBase'".
#
# Fix: after the publish, overlay every DLL the WindowsDesktop runtime pack
# ships onto the publish output. WindowsDesktop always wins -> the real
# WindowsBase.dll (and any other overlap) ends up in the bundle.
$wpfVersionMatch = [regex]::Match($depsRaw, 'runtimepack\.Microsoft\.WindowsDesktop\.App\.Runtime\.win-x64/(?<v>\d+\.\d+\.\d+)')
if (-not $wpfVersionMatch.Success) {
    throw "Could not determine the WindowsDesktop runtime pack version from deps.json."
}
$wpfVersion = $wpfVersionMatch.Groups['v'].Value

$wpfRuntimePackCandidates = @(
    (Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windowsdesktop.app.runtime.win-x64\$wpfVersion\runtimes\win-x64\lib\net8.0"),
    "C:\Program Files\dotnet\packs\Microsoft.WindowsDesktop.App.Runtime.win-x64\$wpfVersion\runtimes\win-x64\lib\net8.0"
)
$wpfRuntimePackDir = $wpfRuntimePackCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $wpfRuntimePackDir) {
    throw ("Could not find WindowsDesktop runtime pack $wpfVersion locally. Searched:`n  " +
           ($wpfRuntimePackCandidates -join "`n  "))
}

Write-Host "Overlaying WindowsDesktop runtime pack DLLs from $wpfRuntimePackDir..."
$overlaid = 0
Get-ChildItem $wpfRuntimePackDir -Filter *.dll | ForEach-Object {
    $dest = Join-Path $resolvedOut $_.Name
    if (Test-Path $dest) {
        Copy-Item -Force -LiteralPath $_.FullName -Destination $dest
        $overlaid++
    }
}
Write-Host "  Overlaid $overlaid DLL(s)."

# Final guard: WindowsBase.dll in the published output must be the real
# AssemblyVersion 8.0.0.0 implementation, not the 4.0.0.0 facade.
$wbPath = Join-Path $resolvedOut "WindowsBase.dll"
$wbVer  = [System.Reflection.AssemblyName]::GetAssemblyName($wbPath).Version
if ($wbVer.Major -lt 8) {
    throw ("Bundled WindowsBase.dll is the wrong version ($wbVer). " +
           "Expected the AssemblyVersion 8.x.x.x implementation from the WindowsDesktop runtime pack, " +
           "not the AssemblyVersion 4.0.0.0 facade from the NETCore.App runtime pack.")
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
