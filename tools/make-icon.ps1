# Builds src/MistyStep.App/Assets/MistyStep.ico from icon/wingfoot.png.
# The icon is "Wingfoot" by Lorc (game-icons.net, CC BY 3.0): the white glyph is drawn on the
# app's blue rounded square at each Windows icon size. See README.md, Credits.
# Run from the repo root:  powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'icon/wingfoot.png'
$target = Join-Path $root 'src/MistyStep.App/Assets/MistyStep.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$blue = [System.Drawing.Color]::FromArgb(255, 0x24, 0x57, 0xD6)

$art = [System.Drawing.Image]::FromFile($source)

# White-on-black artwork -> white glyph whose alpha is the source brightness.
$matrix = New-Object System.Drawing.Imaging.ColorMatrix
$matrix.Matrix00 = 0; $matrix.Matrix11 = 0; $matrix.Matrix22 = 0; $matrix.Matrix33 = 0
$matrix.Matrix03 = 1                                   # alpha <- red
$matrix.Matrix40 = 1; $matrix.Matrix41 = 1; $matrix.Matrix42 = 1  # colour = white
$attributes = New-Object System.Drawing.Imaging.ImageAttributes
$attributes.SetColorMatrix($matrix)

$glyph = New-Object System.Drawing.Bitmap 512, 512, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($glyph)
$g.Clear([System.Drawing.Color]::Transparent)
$g.DrawImage($art, (New-Object System.Drawing.Rectangle 0, 0, 512, 512), 0, 0, 512, 512, ([System.Drawing.GraphicsUnit]::Pixel), $attributes)
$g.Dispose()

function New-Frame([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $radius = $size / 4.0
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = New-Object System.Drawing.RectangleF 0, 0, $size, $size
    $path.AddArc($r.X, $r.Y, $radius * 2, $radius * 2, 180, 90)
    $path.AddArc($r.Right - $radius * 2, $r.Y, $radius * 2, $radius * 2, 270, 90)
    $path.AddArc($r.Right - $radius * 2, $r.Bottom - $radius * 2, $radius * 2, $radius * 2, 0, 90)
    $path.AddArc($r.X, $r.Bottom - $radius * 2, $radius * 2, $radius * 2, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.SolidBrush $blue
    $g.FillPath($brush, $path)

    # Small sizes get less padding so the feathers stay readable.
    $pad = if ($size -le 24) { [Math]::Round($size * 0.10) } else { [Math]::Round($size * 0.14) }
    $g.DrawImage($glyph, (New-Object System.Drawing.Rectangle $pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad)))
    $g.Dispose(); $brush.Dispose(); $path.Dispose()

    $bytes = if ($size -ge 256) { ConvertTo-Png $bitmap } else { ConvertTo-Dib $bitmap }
    $bitmap.Dispose()
    return ,$bytes
}

function ConvertTo-Png($bitmap) {
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$stream.ToArray()
}

# Classic icon frame: BITMAPINFOHEADER (height doubled), 32-bit BGRA rows bottom-up, then a 1-bit AND mask.
function ConvertTo-Dib($bitmap) {
    $size = $bitmap.Width
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($data.Stride * $size)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bitmap.UnlockBits($data)

    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $stream = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $stream
    $w.Write([UInt32]40); $w.Write([Int32]$size); $w.Write([Int32]($size * 2))
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
    $w.Write([UInt32]($size * $size * 4 + $maskStride * $size))
    $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $size - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $data.Stride, $size * 4) }
    $mask = New-Object byte[] ($maskStride * $size)  # all zero: alpha channel decides
    $w.Write($mask)
    $w.Flush()
    return ,$stream.ToArray()
}

$frames = @()
foreach ($size in $sizes) { $frames += ,(New-Frame $size) }

# ICO: ICONDIR, one ICONDIRENTRY per size, then the PNG payloads.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$frames[$i].Length); $w.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $w.Write($frame) }
$w.Flush()

New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
$art.Dispose(); $glyph.Dispose()
Write-Output "Wrote $target ($($sizes -join ', ') px)"
