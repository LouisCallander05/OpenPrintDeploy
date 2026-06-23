<#
.SYNOPSIS
Authenticode-signs a file with signtool, if a signing certificate is configured.

.DESCRIPTION
No-op unless a code-signing certificate thumbprint is supplied (parameter or the
OPD_SIGN_THUMBPRINT environment variable), so unsigned dev/CI builds keep working
unchanged. When a thumbprint IS supplied, the cert must be in the build machine's
certificate store (CurrentUser\My or LocalMachine\My). Locates signtool.exe from
PATH or the Windows SDK. SHA-256 file digest + RFC3161 timestamp.

.PARAMETER Path
The file to sign (an .msi or .exe).

.PARAMETER Thumbprint
SHA-1 thumbprint of the code-signing cert. Defaults to $env:OPD_SIGN_THUMBPRINT.

.EXAMPLE
.\scripts\Sign-File.ps1 -Path publish\OpenPrintDeploy.Server.msi -Thumbprint AB12...
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [string]$Thumbprint = $env:OPD_SIGN_THUMBPRINT,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Thumbprint)) {
    Write-Host "Signing skipped for $(Split-Path -Leaf $Path) (no OPD_SIGN_THUMBPRINT / -Thumbprint)."
    return
}

if (-not (Test-Path $Path)) { throw "Sign-File: '$Path' does not exist." }

# Prefer signtool on PATH; otherwise take the newest x64 build from the Windows SDK.
$signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
if (-not $signtool) {
    $signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $signtool) {
    throw "Sign-File: signtool.exe not found. Install the Windows SDK, or add signtool to PATH."
}

Write-Host "Signing $(Split-Path -Leaf $Path) with certificate $Thumbprint..."
& $signtool sign /sha1 $Thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $Path
if ($LASTEXITCODE -ne 0) { throw "signtool failed (exit $LASTEXITCODE) signing $Path." }
