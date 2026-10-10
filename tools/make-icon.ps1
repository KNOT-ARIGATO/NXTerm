# Generates NXTerm\Resources\NXTerm.ico (16-256 px) and docs\icon.png.
# Run from the repository root:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Render([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = [Math]::Max(1, $s * 0.04)            # outer margin
    $w = $s - 2 * $m
    $r = [Math]::Max(2, $s * 0.14)            # corner radius
    $bar = [Math]::Max(3, $s * 0.17)          # title bar height

    # window body + title bar
    $body = RoundRect $m $m $w $w $r
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 24, 30, 38))), $body)
    $g.SetClip($body)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 52, 66, 82))), $m, $m, $w, $bar)
    $g.ResetClip()
    if ($s -ge 32) {
        $dot = $bar * 0.36
        $cols = @([System.Drawing.Color]::FromArgb(255, 255, 95, 86), [System.Drawing.Color]::FromArgb(255, 255, 189, 46), [System.Drawing.Color]::FromArgb(255, 39, 201, 63))
        for ($i = 0; $i -lt 3; $i++) {
            $g.FillEllipse((New-Object System.Drawing.SolidBrush $cols[$i]), $m + $bar * 0.45 + $i * $dot * 1.6, $m + ($bar - $dot) / 2, $dot, $dot)
        }
    }
    $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 80, 96, 114)), ([Math]::Max(1, $s * 0.02))
    $g.DrawPath($edge, $body)

    # green ">_" prompt
    $green = [System.Drawing.Color]::FromArgb(255, 61, 220, 132)
    $pen = New-Object System.Drawing.Pen $green, ([Math]::Max(1.6, $s * 0.085))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $top = $m + $bar
    $areaH = $s - $m - $top
    $cx = $m + $w * 0.20
    $cy = $top + $areaH * 0.48
    $ch = $areaH * 0.22
    $g.DrawLines($pen, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF $cx, ($cy - $ch)),
        (New-Object System.Drawing.PointF ($cx + $ch * 1.1), $cy),
        (New-Object System.Drawing.PointF $cx, ($cy + $ch))))
    $uy = $cy + $ch
    $g.DrawLine($pen, ($cx + $ch * 1.7), $uy, ($cx + $ch * 3.5), $uy)
    $g.Dispose()
    return $bmp
}

# 32-bit DIB as stored inside .ico files: header with doubled height, bottom-up BGRA rows, empty AND mask.
function ToDib([System.Drawing.Bitmap]$b) {
    $s = $b.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([UInt32]40); $w.Write([Int32]$s); $w.Write([Int32]($s * 2)); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]0); $w.Write([UInt32]0); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $b.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $maskRow = [Math]::Ceiling($s / 32) * 4
    $w.Write((New-Object byte[] ($maskRow * $s)))
    $w.Flush()
    return , $ms.ToArray()
}

$root = Split-Path $PSScriptRoot -Parent
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $b = Render $s
    if ($s -eq 256) {
        $ms = New-Object System.IO.MemoryStream
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        New-Item -ItemType Directory -Force (Join-Path $root 'docs') | Out-Null
        $b.Save((Join-Path $root 'docs\icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
    }
    else { $bytes = ToDib $b }
    $b.Dispose()
    , $bytes
}

# ICO container: DIB images, plus PNG for the 256 px image (supported since Windows Vista)
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
$icoDir = Join-Path $root 'NXTerm\Resources'
New-Item -ItemType Directory -Force $icoDir | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $icoDir 'NXTerm.ico'), $out.ToArray())
"wrote NXTerm\Resources\NXTerm.ico ($($out.Length) bytes) and docs\icon.png"
