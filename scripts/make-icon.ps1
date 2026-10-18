# Generates src/Spotlight/Assets/Spotlight.ico (graphite squircle with a white magnifier, PNG frames 16-256 px).
param([string]$Out = (Join-Path $PSScriptRoot "..\src\Spotlight\Assets\Spotlight.ico"))

Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $inset = [Math]::Max(0.5, $s * 0.03)
    $size = $s - 2 * $inset
    $bg = New-RoundedRect $inset $inset $size $size ($s * 0.23)
    $top = [System.Drawing.Color]::FromArgb(255, 0x4A, 0x4A, 0x4F)
    $bottom = [System.Drawing.Color]::FromArgb(255, 0x1C, 0x1C, 0x1F)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s), $top, $bottom
    $g.FillPath($brush, $bg)
    if ($s -ge 32) {
        $rim = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(60, 255, 255, 255)), ([Math]::Max(1, $s / 128))
        $g.DrawPath($rim, $bg)
    }

    # Magnifier: ring + handle
    $white = [System.Drawing.Color]::FromArgb(255, 0xF5, 0xF5, 0xF7)
    $ringWidth = [Math]::Max(1.6, $s * 0.085)
    $ring = New-Object System.Drawing.Pen $white, $ringWidth
    $cx = $s * 0.445; $cy = $s * 0.445; $rad = $s * 0.205
    $g.DrawEllipse($ring, $cx - $rad, $cy - $rad, 2 * $rad, 2 * $rad)
    $handle = New-Object System.Drawing.Pen $white, ([Math]::Max(2, $s * 0.11))
    $handle.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $handle.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $a = $rad + $ringWidth / 2
    $g.DrawLine($handle, $cx + $a * 0.72, $cy + $a * 0.72, $s * 0.745, $s * 0.745)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($s in $sizes) { , (New-Frame $s) }

$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ms
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $frames[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $frames) { $w.Write([byte[]]$data) }
$w.Flush()

New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
[System.IO.File]::WriteAllBytes($Out, $ms.ToArray())
Write-Output "Wrote $Out ($($ms.Length) bytes)"
