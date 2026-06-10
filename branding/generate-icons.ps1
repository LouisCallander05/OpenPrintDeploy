<#
.SYNOPSIS
  Turns branding/source-logo.png into the tray client's app assets:
  a transparent, square multi-size appicon.ico and a trimmed logo.png.

.DESCRIPTION
  The source art (the Gemini-generated Open Print Deploy logo) sits on a near-
  white background. A flat white box looks wrong in the Windows tray and on a
  dialog, so we knock the background out to transparent. We flood-fill ONLY from
  the image border, so white *inside* the artwork (the highlights inside the
  arrows) is preserved — a naive "make all white transparent" would punch holes
  through the logo.

  Output (regenerated in place, safe to re-run):
    src/OpenPrintDeploy.Client.Tray/Assets/appicon.ico   (16..256, PNG frames)
    src/OpenPrintDeploy.Client.Tray/Assets/logo.png      (trimmed, 512px max)

  Run from anywhere:  pwsh -File branding/generate-icons.ps1
#>
[CmdletBinding()]
param(
    [int]   $WhiteThreshold = 234,   # R,G,B all above this == background white
    [double]$PadFraction    = 0.06   # square padding around trimmed content
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root      = Split-Path -Parent $PSScriptRoot           # repo root (OpenPrintDeploy)
$srcPath   = Join-Path $PSScriptRoot 'source-logo.png'
$assetsDir = Join-Path $root 'src/OpenPrintDeploy.Client.Tray/Assets'
$icoPath   = Join-Path $assetsDir 'appicon.ico'
$logoPath  = Join-Path $assetsDir 'logo.png'

if (-not (Test-Path $srcPath)) { throw "Source logo not found: $srcPath" }
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null

# --- load source into a 32bpp ARGB bitmap we can edit pixel-by-pixel ----------
$src = [System.Drawing.Bitmap]::new($srcPath)
$bmp = [System.Drawing.Bitmap]::new($src.Width, $src.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g0  = [System.Drawing.Graphics]::FromImage($bmp)
$g0.DrawImage($src, 0, 0, $src.Width, $src.Height)
$g0.Dispose(); $src.Dispose()

$w = $bmp.Width; $h = $bmp.Height
$rect = [System.Drawing.Rectangle]::new(0, 0, $w, $h)
$data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadWrite,
                      [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$stride = $data.Stride
$bytes  = [byte[]]::new($stride * $h)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)

# Memory order for Format32bppArgb is little-endian: B,G,R,A.
$isWhite = {
    param($i)
    ($bytes[$i + 3] -gt 0) -and
    ($bytes[$i]     -gt $WhiteThreshold) -and   # B
    ($bytes[$i + 1] -gt $WhiteThreshold) -and   # G
    ($bytes[$i + 2] -gt $WhiteThreshold)        # R
}

# --- flood fill the background from the border, knocking alpha to 0 -----------
$visited = [bool[]]::new($w * $h)
$stack   = [System.Collections.Generic.Stack[int]]::new()
for ($x = 0; $x -lt $w; $x++) { $stack.Push($x); $stack.Push(($h - 1) * $w + $x) }
for ($y = 0; $y -lt $h; $y++) { $stack.Push($y * $w); $stack.Push($y * $w + ($w - 1)) }

while ($stack.Count -gt 0) {
    $p = $stack.Pop()
    if ($visited[$p]) { continue }
    $px = $p % $w; $py = [math]::Floor($p / $w)
    $bi = $py * $stride + $px * 4
    if (-not (& $isWhite $bi)) { continue }
    $visited[$p] = $true
    $bytes[$bi + 3] = 0                          # make transparent
    if ($px -gt 0)        { $stack.Push($p - 1) }
    if ($px -lt $w - 1)   { $stack.Push($p + 1) }
    if ($py -gt 0)        { $stack.Push($p - $w) }
    if ($py -lt $h - 1)   { $stack.Push($p + $w) }
}

[System.Runtime.InteropServices.Marshal]::Copy($bytes, 0, $data.Scan0, $bytes.Length)
$bmp.UnlockBits($data)

# --- find the tight content bounding box (any non-transparent pixel) ----------
$minX = $w; $minY = $h; $maxX = -1; $maxY = -1
for ($y = 0; $y -lt $h; $y++) {
    $row = $y * $stride
    for ($x = 0; $x -lt $w; $x++) {
        if ($bytes[$row + $x * 4 + 3] -gt 0) {
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}
if ($maxX -lt 0) { throw "Everything was treated as background — lower -WhiteThreshold." }

$cw = $maxX - $minX + 1
$ch = $maxY - $minY + 1
$side = [int]([math]::Ceiling([math]::Max($cw, $ch) * (1 + 2 * $PadFraction)))

# Square canvas, content centered, fully transparent elsewhere.
$square = [System.Drawing.Bitmap]::new($side, $side, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$gs = [System.Drawing.Graphics]::FromImage($square)
$gs.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$gs.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$destX = [int](($side - $cw) / 2)
$destY = [int](($side - $ch) / 2)
$gs.DrawImage($bmp,
    [System.Drawing.Rectangle]::new($destX, $destY, $cw, $ch),
    [System.Drawing.Rectangle]::new($minX, $minY, $cw, $ch),
    [System.Drawing.GraphicsUnit]::Pixel)
$gs.Dispose(); $bmp.Dispose()

# --- helper: high-quality resize to an NxN transparent PNG (as byte[]) --------
function Resize-ToPng([System.Drawing.Bitmap]$image, [int]$size) {
    $out = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($out)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.DrawImage($image, [System.Drawing.Rectangle]::new(0, 0, $size, $size))
    $g.Dispose()
    $ms = [System.IO.MemoryStream]::new()
    $out.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $out.Dispose()
    return ,$ms.ToArray()
}

# --- write the trimmed brand logo (used by the sign-in dialog) ----------------
$logoBytes = Resize-ToPng $square 512
[System.IO.File]::WriteAllBytes($logoPath, $logoBytes)
Write-Host "Wrote $logoPath ($($logoBytes.Length) bytes)"

# --- server admin UI assets: sidebar logo + browser favicon -------------------
$serverWww = Join-Path $root 'src/OpenPrintDeploy.Server/wwwroot'
if (Test-Path $serverWww) {
    $brandPng   = Join-Path $serverWww 'brand-logo.png'
    $faviconPng = Join-Path $serverWww 'favicon.png'
    [System.IO.File]::WriteAllBytes($brandPng,   (Resize-ToPng $square 256))
    [System.IO.File]::WriteAllBytes($faviconPng, (Resize-ToPng $square 64))
    Write-Host "Wrote $brandPng"
    Write-Host "Wrote $faviconPng"
}

# --- build the multi-size .ico (PNG-compressed frames; Win10/11 targets) ------
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($s in $sizes) { ,(Resize-ToPng $square $s) }
$square.Dispose()

$ms  = [System.IO.MemoryStream]::new()
$bw  = [System.IO.BinaryWriter]::new($ms)
$bw.Write([uint16]0)            # reserved
$bw.Write([uint16]1)            # type: 1 = icon
$bw.Write([uint16]$sizes.Count) # image count

$offset = 6 + 16 * $sizes.Count # header + all directory entries
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $png = $frames[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # width  (0 == 256)
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # height (0 == 256)
    $bw.Write([byte]0)          # palette count
    $bw.Write([byte]0)          # reserved
    $bw.Write([uint16]1)        # color planes
    $bw.Write([uint16]32)       # bits per pixel
    $bw.Write([uint32]$png.Length)
    $bw.Write([uint32]$offset)
    $offset += $png.Length
}
foreach ($png in $frames) { $bw.Write($png) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()
Write-Host "Wrote $icoPath ($($sizes.Count) frames: $($sizes -join ', '))"
