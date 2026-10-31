# Draws the app icon (a camera-ish box with the Spectrum rainbow) and writes assets\app.ico.
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "assets\app.ico"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 32.0
    # body
    $body = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(30, 30, 34))
    $g.FillRectangle($body, 2 * $s, 6 * $s, 28 * $s, 20 * $s)
    # rainbow stripes (red, yellow, green, cyan) running diagonally across the bottom-right
    $colors = @(0xD70000, 0xD7D700, 0x00D700, 0x00D7D7)
    for ($i = 0; $i -lt 4; $i++) {
        $c = [System.Drawing.Color]::FromArgb(255, ($colors[$i] -shr 16) -band 255, ($colors[$i] -shr 8) -band 255, $colors[$i] -band 255)
        $brush = New-Object System.Drawing.SolidBrush $c
        $x = (18 + $i * 3) * $s
        $pts = [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF ($x, (26 * $s))),
            (New-Object System.Drawing.PointF (($x + 3 * $s), (26 * $s))),
            (New-Object System.Drawing.PointF (($x + 3 * $s + 6 * $s), (14 * $s))),
            (New-Object System.Drawing.PointF (($x + 6 * $s), (14 * $s))))
        $g.FillPolygon($brush, $pts)
    }
    $g.FillRectangle($body, 30 * $s, 6 * $s, 4 * $s, 20 * $s)
    # record dot
    $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(240, 50, 50))
    $g.FillEllipse($red, 6 * $s, 10 * $s, 9 * $s, 9 * $s)
    $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 256)
$images = @()
foreach ($sz in $sizes) {
    $bmp = Draw-Icon $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += , $ms.ToArray()
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
"Wrote $out"
