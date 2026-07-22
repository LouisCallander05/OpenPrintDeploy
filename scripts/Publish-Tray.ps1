<#
.SYNOPSIS
Publishes the Tray as a self-contained win-x64 folder, ready to package.

.DESCRIPTION
Used by Publish-Client-Msi.ps1, which harvests this folder into the client MSI.
Includes the WindowsDesktop runtime-pack overlay that fixes the self-contained
WPF publish (see the inline notes), so any consumer gets a tray folder that
actually launches on a clean endpoint.

.PARAMETER OutDir
Destination folder for the published tray (created/overwritten).

.PARAMETER Version
SemVer-ish string baked into the assemblies. Blank for local dev.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$trayDir  = if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }

if (Test-Path $trayDir) { Remove-Item -Recurse -Force $trayDir }

$versionProps = @()
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    Write-Host "Stamping tray with version $Version"
    $versionProps = @("-p:Version=$Version")
}

Push-Location $repoRoot
try {
    Write-Host "Publishing tray (self-contained $Runtime) to $trayDir..."
    & dotnet publish src/OpenPrintDeploy.Client.Tray `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        @versionProps `
        -o $trayDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (tray) failed (exit $LASTEXITCODE)" }
}
finally {
    Pop-Location
}

$trayExe = Join-Path $trayDir "OpenPrintDeploy.Client.Tray.exe"
if (-not (Test-Path $trayExe)) { throw "Tray exe missing from tray publish folder." }

# Catch a class of silently-broken self-contained WPF publishes. If the build
# config ever drops the WindowsDesktop runtime pack from deps.json again, the
# tray would launch on a dev box (where the runtime is installed system-wide)
# but crash with FileNotFoundException for WindowsBase on a clean endpoint.
$depsJsonPath = Join-Path $trayDir "OpenPrintDeploy.Client.Tray.deps.json"
if (-not (Test-Path $depsJsonPath)) { throw "deps.json missing from tray publish -- did the publish silently fail?" }
$depsRaw = Get-Content $depsJsonPath -Raw
if ($depsRaw -notmatch 'runtimepack\.Microsoft\.WindowsDesktop\.App\.Runtime') {
    throw ("Self-contained publish is missing the WindowsDesktop runtime pack in deps.json. " +
           "Check EnableWindowsTargeting in OpenPrintDeploy.Client.Tray.csproj -- it must NOT be set on Windows.")
}
if ($depsRaw -notmatch 'WindowsBase\.dll') {
    throw "Self-contained publish does not register WindowsBase.dll in deps.json. The bundle will crash on clean machines."
}

# The publish merges files from both runtime packs (NETCore.App and
# WindowsDesktop.App), which share filenames -- most importantly WindowsBase.dll.
# NETCore ships a 16KB facade (AssemblyVersion 4.0.0.0); WindowsDesktop ships the
# real 2.2MB implementation (8.0.0.0). The publish target copies NETCore's facade
# *after* WindowsDesktop's real file, so the bundle ends up with the broken facade
# while deps.json still asks for 8.0.0.0 -> the tray crashes at startup. Fix:
# overlay every DLL the WindowsDesktop runtime pack ships onto the output.
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
    $dest = Join-Path $trayDir $_.Name
    if (Test-Path $dest) {
        Copy-Item -Force -LiteralPath $_.FullName -Destination $dest
        $overlaid++
    }
}
Write-Host "  Overlaid $overlaid DLL(s)."

# Final guard: WindowsBase.dll must be the real AssemblyVersion 8.x implementation.
$wbPath = Join-Path $trayDir "WindowsBase.dll"
$wbVer  = [System.Reflection.AssemblyName]::GetAssemblyName($wbPath).Version
if ($wbVer.Major -lt 8) {
    throw ("Bundled WindowsBase.dll is the wrong version ($wbVer). " +
           "Expected the AssemblyVersion 8.x implementation from the WindowsDesktop runtime pack, " +
           "not the 4.0.0.0 facade from the NETCore.App runtime pack.")
}

# Bundle the uninstall printer-cleanup tool alongside the tray. It's published as
# a self-contained single file so the uninstall flow can copy ONE exe to
# C:\ProgramData and have a per-user logon task run it long after the install
# directory is gone (no shared runtime to depend on). The MSI harvests this folder.
$cleanupTmp = Join-Path $repoRoot "publish/client-cleanup-tmp"
if (Test-Path $cleanupTmp) { Remove-Item -Recurse -Force $cleanupTmp }
Push-Location $repoRoot
try {
    Write-Host "Publishing uninstall cleanup tool (self-contained single-file $Runtime)..."
    & dotnet publish installer/OpenPrintDeploy.Client.Cleanup `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        @versionProps `
        -o $cleanupTmp
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (cleanup) failed (exit $LASTEXITCODE)" }
}
finally {
    Pop-Location
}

$cleanupExe = Join-Path $cleanupTmp "OpenPrintDeploy.Client.Cleanup.exe"
if (-not (Test-Path $cleanupExe)) { throw "Cleanup exe missing from its publish folder." }
Copy-Item -Force -LiteralPath $cleanupExe -Destination (Join-Path $trayDir "OpenPrintDeploy.Client.Cleanup.exe")
Remove-Item -Recurse -Force $cleanupTmp
Write-Host "  Bundled OpenPrintDeploy.Client.Cleanup.exe into the tray folder."

Write-Host "Tray published to $trayDir" -ForegroundColor Green
