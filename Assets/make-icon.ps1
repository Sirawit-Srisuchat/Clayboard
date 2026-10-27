# Regenerates Assets/app.ico: a clay rounded square holding a cream keycap.
# Each size is drawn on its own so small icons stay crisp. Run: powershell -File Assets/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$clay  = [System.Drawing.Color]::FromArgb(0x8B, 0x5E, 0x3C)
$cream = [System.Drawing.Color]::FromArgb(0xFB, 0xF8, 0xF3)
$sand  = [System.Drawing.Color]::FromArgb(0xE6, 0xDC, 0xCB)

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $s = [single]$size

    # Tile
    $g.FillPath((New-Object System.Drawing.SolidBrush $clay), (New-RoundedRect 0 0 $s $s ($s * 0.22)))

    # Keycap: a sand base peeking out below the cream top gives it depth
    $kx = $s * 0.22; $kw = $s * 0.56; $ky = $s * 0.25; $kh = $s * 0.48; $kr = $s * 0.10
    $g.FillPath((New-Object System.Drawing.SolidBrush $sand), (New-RoundedRect $kx ($ky + $s * 0.06) $kw $kh $kr))
    $g.FillPath((New-Object System.Drawing.SolidBrush $cream), (New-RoundedRect $kx $ky $kw $kh $kr))

    # A lightning-bolt legend on the key (one press, lots done), dropped at sizes where it would only be noise
    if ($size -ge 32) {
        $cx = $s / 2; $cy = $ky + $kh / 2; $u = $s * 0.012
        $bolt = @((3, -12), (-6, 2), (0, 2), (-3, 12), (6, -2), (0, -2))
        $points = [System.Drawing.PointF[]]($bolt | ForEach-Object {
            New-Object System.Drawing.PointF ($cx + $_[0] * $u), ($cy + $_[1] * $u) })
        $g.FillPolygon((New-Object System.Drawing.SolidBrush $clay), $points)
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container with PNG-compressed entries (supported since Windows Vista)
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = $sizes | ForEach-Object { , (New-IconPng $_) }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
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
$w.Flush()

[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'app.ico'), $out.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'app-256.png'), $images[-1])
