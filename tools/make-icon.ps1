# Génère src/Corral/corral.ico (multi-tailles, PNG) et un aperçu 256 px.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

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

function Draw([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Fond : carré arrondi vert sapin en dégradé
    $m = [math]::Max(0.5, $s * 0.03)
    $bg = RoundRect $m $m ($s - 2 * $m) ($s - 2 * $m) ($s * 0.22)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.Color]::FromArgb(255, 38, 150, 128)), ([System.Drawing.Color]::FromArgb(255, 12, 70, 62))
    $g.FillPath($grad, $bg)

    $cx = $s * 0.45; $cy = $s * 0.54
    $gold = [System.Drawing.Color]::FromArgb(255, 246, 196, 72)

    # Puce CPU au centre
    $c = $s * 0.27
    $chipX = $cx - $c / 2; $chipY = $cy - $c / 2
    if ($s -ge 32) {
        $pinPen = New-Object System.Drawing.Pen $gold, ([math]::Max(1, $s * 0.025))
        $len = $s * 0.06
        foreach ($i in 1..3) {
            $o = $c * $i / 4
            $g.DrawLine($pinPen, $chipX + $o, $chipY - $len, $chipX + $o, $chipY)
            $g.DrawLine($pinPen, $chipX + $o, $chipY + $c, $chipX + $o, $chipY + $c + $len)
            $g.DrawLine($pinPen, $chipX - $len, $chipY + $o, $chipX, $chipY + $o)
            $g.DrawLine($pinPen, $chipX + $c, $chipY + $o, $chipX + $c + $len, $chipY + $o)
        }
    }
    $chip = RoundRect $chipX $chipY $c $c ($c * 0.18)
    $g.FillPath((New-Object System.Drawing.SolidBrush $gold), $chip)
    if ($s -ge 48) {
        $core = $c * 0.42
        $corePath = RoundRect ($cx - $core / 2) ($cy - $core / 2) $core $core ($core * 0.2)
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 12, 70, 62))), $corePath)
    }

    # Lasso : boucle blanche autour de la puce + corde qui part en bas à droite
    $rope = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([math]::Max(1.4, $s * 0.075))
    $rope.StartCap = 'Round'; $rope.EndCap = 'Round'; $rope.LineJoin = 'Round'
    $rw = $s * 0.64; $rh = $s * 0.54
    $cy2 = $cy
    $a = 318
    $g.DrawArc($rope, $cx - $rw / 2, $cy2 - $rh / 2, $rw, $rh, $a + 12, 336)
    $sx = $cx + ($rw / 2) * [math]::Cos($a * [math]::PI / 180)
    $sy = $cy2 + ($rh / 2) * [math]::Sin($a * [math]::PI / 180)
    # Corde ondulée vers le coin haut droit
    $g.DrawBezier($rope, $sx, $sy, $sx + $s * 0.14, $sy + $s * 0.03, $s * 0.66, $s * 0.12, $s * 0.82, $s * 0.15)
    # Nœud coulant
    $k = $rope.Width * 1.35
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)), $sx - $k / 2, $sy - $k / 2, $k, $k)

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = Draw $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $bmp.Save((Join-Path $PSScriptRoot 'icon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $root 'src\Corral\corral.ico'), $out.ToArray())
"OK"
