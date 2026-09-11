<#
  Generates assets\icon.ico and assets\icon.png.

  The icon is drawn rather than committed as an opaque binary, so it stays
  reviewable and tweakable. It used to be produced by a hand-written PNG and
  ICO encoder in Node; this does the same job with the drawing library that
  is already part of Windows, which is the last thing that stood between the
  project and needing no toolchain at all.

  Run it only after changing the shape - the output is committed.

    .\assets\make-icon.ps1
#>
[CmdletBinding()]
param([switch]$Quiet)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$out = $PSScriptRoot
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

# A dark rounded square with a red record dot, matching the tray mark.
$bg  = [System.Drawing.Color]::FromArgb(0x28, 0x2b, 0x30)
$rec = [System.Drawing.Color]::FromArgb(0xff, 0x3b, 0x30)

function New-IconBitmap([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size,
         ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.Clear([System.Drawing.Color]::Transparent)

  # Rounded square covering most of the canvas.
  $pad = [math]::Max(1, [int]($size * 0.055))
  $side = $size - $pad * 2
  $radius = $side * 0.28
  $d = $radius * 2

  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc($pad, $pad, $d, $d, 180, 90)
  $path.AddArc($pad + $side - $d, $pad, $d, $d, 270, 90)
  $path.AddArc($pad + $side - $d, $pad + $side - $d, $d, $d, 0, 90)
  $path.AddArc($pad, $pad + $side - $d, $d, $d, 90, 90)
  $path.CloseFigure()

  $brush = New-Object System.Drawing.SolidBrush $bg
  $g.FillPath($brush, $path)
  $brush.Dispose(); $path.Dispose()

  # The record dot.
  $dot = $size * 0.52
  $dotBrush = New-Object System.Drawing.SolidBrush $rec
  $g.FillEllipse($dotBrush, ($size - $dot) / 2, ($size - $dot) / 2, $dot, $dot)
  $dotBrush.Dispose()

  $g.Dispose()
  return $bmp
}

# ---- render every size to an in-memory PNG -------------------------------

$images = @()
foreach ($size in $sizes) {
  $bmp = New-IconBitmap $size
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $images += [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
  if ($size -eq 256) { $bmp.Save((Join-Path $out 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
  $ms.Dispose(); $bmp.Dispose()
}

# ---- assemble the ICO ----------------------------------------------------
#
# ICONDIR, then one 16-byte ICONDIRENTRY per image, then the image data.
# Every entry is a PNG, which Windows has accepted inside .ico since Vista and
# which keeps the file a fraction of the size of the equivalent DIBs.

$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico

$w.Write([uint16]0)                    # reserved
$w.Write([uint16]1)                    # type: icon
$w.Write([uint16]$images.Count)

$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
  # 0 means 256 in this field, which is why the format tops out there.
  $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
  $w.Write([byte]$dim)                 # width
  $w.Write([byte]$dim)                 # height
  $w.Write([byte]0)                    # palette size
  $w.Write([byte]0)                    # reserved
  $w.Write([uint16]1)                  # colour planes
  $w.Write([uint16]32)                 # bits per pixel
  $w.Write([uint32]$img.Bytes.Length)
  $w.Write([uint32]$offset)
  $offset += $img.Bytes.Length
}
foreach ($img in $images) { $w.Write($img.Bytes) }

$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $out 'icon.ico'), $ico.ToArray())
$w.Dispose(); $ico.Dispose()

if (-not $Quiet) {
  $kb = [math]::Round((Get-Item (Join-Path $out 'icon.ico')).Length / 1KB, 1)
  Write-Host "Wrote icon.ico ($kb KB, $($images.Count) sizes) and icon.png" -ForegroundColor Green
}
