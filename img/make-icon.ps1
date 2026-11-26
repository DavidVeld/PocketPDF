# Draws the PurpleStreamPDF icon (a drawing sheet with a red revision cloud) and writes src/PurpleStreamPDF/app.ico and img/icon.png.
# Run from the repo root: powershell -ExecutionPolicy Bypass -File img/make-icon.ps1 [-PreviewDir <folder for each size as PNG>]
param([string]$PreviewDir)
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90); $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure(); $path
}

# A revision cloud: bumps around an ellipse, each a Bezier pushed outward.
function New-Cloud([float]$cx, [float]$cy, [float]$rx, [float]$ry, [int]$bumps) {
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    for ($i = 0; $i -lt $bumps; $i++) {
        $a0 = 2 * [Math]::PI * $i / $bumps; $a1 = 2 * [Math]::PI * ($i + 1) / $bumps
        $p = { param($a, $k) New-Object Drawing.PointF ([float]($cx + $rx * $k * [Math]::Cos($a))), ([float]($cy + $ry * $k * [Math]::Sin($a))) }
        $path.AddBezier((& $p $a0 1), (& $p ($a0 + ($a1 - $a0) * 0.15) 1.2), (& $p ($a0 + ($a1 - $a0) * 0.85) 1.2), (& $p $a1 1))
    }
    $path.CloseFigure(); $path
}

function Draw-Icon([int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([Drawing.Color]::Transparent)
    $s = $size / 256.0
    $small = $size -le 24

    # Tile: deep blue to bright blue.
    $tile = New-RoundedRect (8 * $s) (8 * $s) (240 * $s) (240 * $s) (52 * $s)
    $gradient = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF $size, $size), ([Drawing.Color]::FromArgb(255, 30, 58, 138)), ([Drawing.Color]::FromArgb(255, 14, 165, 233))
    $g.FillPath($gradient, $tile)

    # Sheet with a folded corner, and a soft shadow.
    $sx = 58 * $s; $sy = 40 * $s; $sw = 140 * $s; $sh = 176 * $s; $fold = 38 * $s
    $sheet = New-Object Drawing.Drawing2D.GraphicsPath
    $sheet.AddLines([Drawing.PointF[]]@(
        (New-Object Drawing.PointF $sx, $sy), (New-Object Drawing.PointF ($sx + $sw - $fold), $sy),
        (New-Object Drawing.PointF ($sx + $sw), ($sy + $fold)), (New-Object Drawing.PointF ($sx + $sw), ($sy + $sh)),
        (New-Object Drawing.PointF $sx, ($sy + $sh))))
    $sheet.CloseFigure()
    if (-not $small) {
        $shadow = $sheet.Clone(); $m = New-Object Drawing.Drawing2D.Matrix; $m.Translate(6 * $s, 8 * $s); $shadow.Transform($m)
        $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(70, 0, 0, 40))), $shadow)
    }
    $g.FillPath([Drawing.Brushes]::White, $sheet)
    $foldPath = New-Object Drawing.Drawing2D.GraphicsPath
    $foldPath.AddLines([Drawing.PointF[]]@((New-Object Drawing.PointF ($sx + $sw - $fold), $sy), (New-Object Drawing.PointF ($sx + $sw - $fold), ($sy + $fold)), (New-Object Drawing.PointF ($sx + $sw), ($sy + $fold))))
    $foldPath.CloseFigure()
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 191, 219, 254))), $foldPath)

    # Drawing line work: a grid and a beam, left out at small sizes where it would be mush.
    if (-not $small) {
        $grid = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(255, 148, 163, 184)), ([float][Math]::Max(1, 3 * $s))
        foreach ($x in 86, 128, 170) { $g.DrawLine($grid, [float]($x * $s), [float](86 * $s), [float]($x * $s), [float](196 * $s)) }
        foreach ($y in 100, 148, 190) { $g.DrawLine($grid, [float](74 * $s), [float]($y * $s), [float](184 * $s), [float]($y * $s)) }
        $ink = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(255, 30, 41, 59)), ([float][Math]::Max(1.5, 7 * $s))
        $g.DrawLine($ink, [float](74 * $s), [float](62 * $s), [float](140 * $s), [float](62 * $s))
    }

    # The red revision cloud, spilling over the sheet edge so it reads as a markup.
    $cloud = New-Cloud (152 * $s) (168 * $s) (60 * $s) (38 * $s) ($(if ($small) { 8 } else { 16 }))
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(40, 229, 57, 53))), $cloud)
    $red = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(255, 229, 57, 53)), ([float][Math]::Max(1.5, 8 * $s))
    $red.LineJoin = 'Round'
    $g.DrawPath($red, $cloud)

    $g.Dispose()
    $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($size in $sizes) {
    $bmp = Draw-Icon $size
    $stream = New-Object IO.MemoryStream
    $bmp.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    if ($PreviewDir) { $bmp.Save("$PreviewDir\icon-$size.png", [Drawing.Imaging.ImageFormat]::Png) }
    if ($size -eq 256) { $bmp.Save("$root\img\icon.png", [Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $stream.ToArray()
}

# ICO file: header, one directory entry per size, then the PNG images.
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $w.Write($png) }
[IO.File]::WriteAllBytes("$root\src\PurpleStreamPDF\app.ico", $out.ToArray())
"Wrote app.ico ($($out.Length) bytes) and img/icon.png"
