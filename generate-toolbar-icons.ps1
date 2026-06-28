# Regenerates the command-button icons inside the Rhino toolbar file
# (src/MoleHill.Rhino/Toolbars/MoleHill.Toolbar.rui).
#
# The .rui stores button bitmaps as three vertical sprite strips — <small_bitmap> (16px),
# <normal_bitmap> (24px), <large_bitmap> (32px) — each a 32bpp PNG, base64 encoded, with one tile
# per row. A macro references its tile by `bitmap_id` GUID, which the <bitmap_item> list maps to a
# row index. This script renders one coherent icon per command (keyed by the macro script name),
# repacks the three strips, and replaces ONLY the base64 inside each <bitmap> element so the rest of
# the .rui (layout, GUIDs, indices) is left byte-for-byte intact.
#
# Re-run after changing a design. Rhino caches opened toolbars, so reopen the .rui (or restart Rhino)
# to see changes; ToolbarInstaller re-copies it to %AppData% when the packaged copy is newer.

Add-Type -AssemblyName System.Drawing

$ruiPath = "$PSScriptRoot\src\MoleHill.Rhino\Toolbars\MoleHill.Toolbar.rui"

function PtF { param([double]$x, [double]$y) New-Object System.Drawing.PointF($x, $y) }
function Argb { param([int]$r, [int]$g, [int]$b, [int]$a = 255) [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }
function Pen { param($color, [double]$w = 1.0)
    $p = New-Object System.Drawing.Pen($color, $w)
    $p.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $p.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $p.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $p
}
function Brush { param($color) New-Object System.Drawing.SolidBrush($color) }

# ── Shared motifs ───────────────────────────────────────────────────────────
# A blue TIN triangle (the MoleHill terrain mark). $inset is a fraction of $s.
function Draw-Triangle {
    param($g, $s, $fillA = (Argb 60 120 200), $fillB = (Argb 30 70 140), [bool]$mesh = $true)
    $apex = PtF ($s * 0.5) ($s * 0.14)
    $bl = PtF ($s * 0.12) ($s * 0.84)
    $br = PtF ($s * 0.88) ($s * 0.84)
    $pts = @($apex, $bl, $br)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush((PtF 0 ($s * 0.14)), (PtF 0 ($s * 0.84)), $fillA, $fillB)
    $g.FillPolygon($grad, $pts); $grad.Dispose()
    if ($mesh) {
        $lm = PtF ($s * 0.31) ($s * 0.49); $rm = PtF ($s * 0.69) ($s * 0.49); $mid = PtF ($s * 0.5) ($s * 0.84)
        $ip = Pen (Argb 170 205 245) ([Math]::Max(0.8, $s * 0.03))
        $g.DrawLine($ip, $lm, $rm); $g.DrawLine($ip, $lm, $mid); $g.DrawLine($ip, $rm, $mid)
        $g.DrawLine($ip, $apex, $lm); $g.DrawLine($ip, $apex, $rm); $ip.Dispose()
    }
    $op = Pen (Argb 18 50 110) ([Math]::Max(1.0, $s * 0.07)); $g.DrawPolygon($op, $pts); $op.Dispose()
}

# A globe/grid (georef). Returns nothing.
function Draw-Globe {
    param($g, $s, $color = (Argb 0 121 107))
    $r = [System.Drawing.RectangleF]::new($s * 0.14, $s * 0.14, $s * 0.62, $s * 0.62)
    $p = Pen $color ([Math]::Max(1.0, $s * 0.055))
    $g.DrawEllipse($p, $r)
    $g.DrawLine($p, ($s * 0.14), ($s * 0.45), ($s * 0.76), ($s * 0.45))
    $g.DrawArc($p, ($s * 0.30), ($s * 0.14), ($s * 0.30), ($s * 0.62), 90, 180)
    $g.DrawArc($p, ($s * 0.30), ($s * 0.14), ($s * 0.30), ($s * 0.62), 270, 180)
    $p.Dispose()
}

# A small block/cube (blocks). Origin-ish top-left in fractional coords.
function Draw-Cube {
    param($g, $s, $ox = 0.16, $oy = 0.22, $w = 0.46, $color = (Argb 230 126 34))
    $x = $s * $ox; $y = $s * $oy; $ww = $s * $w; $d = $ww * 0.4
    $front = @((PtF $x ($y + $d)), (PtF ($x + $ww) ($y + $d)), (PtF ($x + $ww) ($y + $d + $ww)), (PtF $x ($y + $d + $ww)))
    $top = @((PtF $x ($y + $d)), (PtF ($x + $d) $y), (PtF ($x + $ww + $d) $y), (PtF ($x + $ww) ($y + $d)))
    $side = @((PtF ($x + $ww) ($y + $d)), (PtF ($x + $ww + $d) $y), (PtF ($x + $ww + $d) ($y + $ww)), (PtF ($x + $ww) ($y + $d + $ww)))
    $bf = Brush $color; $bt = Brush ([System.Drawing.Color]::FromArgb($color.A, [Math]::Min(255, $color.R + 40), [Math]::Min(255, $color.G + 40), [Math]::Min(255, $color.B + 40)))
    $bs = Brush ([System.Drawing.Color]::FromArgb($color.A, [Math]::Max(0, $color.R - 50), [Math]::Max(0, $color.G - 50), [Math]::Max(0, $color.B - 50)))
    $g.FillPolygon($bt, $top); $g.FillPolygon($bs, $side); $g.FillPolygon($bf, $front)
    $op = Pen (Argb 90 50 10) ([Math]::Max(0.8, $s * 0.03))
    $g.DrawPolygon($op, $top); $g.DrawPolygon($op, $side); $g.DrawPolygon($op, $front)
    $bf.Dispose(); $bt.Dispose(); $bs.Dispose(); $op.Dispose()
}

# A map pin (markers).
function Draw-Pin {
    param($g, $s, $color = (Argb 211 47 47), $cx = 0.5, $cy = 0.40)
    $x = $s * $cx; $y = $s * $cy; $r = $s * 0.20
    $tip = PtF $x ($s * 0.86)
    $bw = Brush $color
    $g.FillEllipse($bw, ($x - $r), ($y - $r), ($r * 2), ($r * 2))
    $g.FillPolygon($bw, @((PtF ($x - $r * 0.8) ($y + $r * 0.5)), (PtF ($x + $r * 0.8) ($y + $r * 0.5)), $tip))
    $bw.Dispose()
    $bw2 = Brush ([System.Drawing.Color]::White); $g.FillEllipse($bw2, ($x - $r * 0.45), ($y - $r * 0.45), ($r * 0.9), ($r * 0.9)); $bw2.Dispose()
}

# Stacked layers (layer templates).
function Draw-Layers {
    param($g, $s, $color = (Argb 123 31 162))
    $cx = $s * 0.5
    foreach ($pair in @(@(0.30, ((Argb 186 104 200))), @(0.46, ((Argb 149 60 190))), @(0.62, $color))) {
        $y = $s * $pair[0]; $hw = $s * 0.30; $hh = $s * 0.10
        $dia = @((PtF $cx ($y - $hh)), (PtF ($cx + $hw) $y), (PtF $cx ($y + $hh)), (PtF ($cx - $hw) $y))
        $b = Brush $pair[1]; $g.FillPolygon($b, $dia); $b.Dispose()
        $p = Pen (Argb 70 20 95) ([Math]::Max(0.7, $s * 0.025)); $g.DrawPolygon($p, $dia); $p.Dispose()
    }
}

# Arrowhead helper at point (x,y) pointing in direction (dx,dy), length = $s*0.18.
function Draw-Arrowhead {
    param($g, $s, $pen, $x, $y, $dx, $dy)
    $len = $s * 0.20
    $n = [Math]::Sqrt($dx * $dx + $dy * $dy); if ($n -eq 0) { return }
    $ux = $dx / $n; $uy = $dy / $n
    $px = -$uy; $py = $ux
    $bx = $x - $ux * $len; $by = $y - $uy * $len
    $g.DrawLine($pen, $x, $y, ($bx + $px * $len * 0.5), ($by + $py * $len * 0.5))
    $g.DrawLine($pen, $x, $y, ($bx - $px * $len * 0.5), ($by - $py * $len * 0.5))
}

# Two refresh arrows forming a ring (update / reset).
function Draw-Refresh {
    param($g, $s, $color, $cx = 0.5, $cy = 0.5, $rad = 0.26)
    $x = $s * $cx; $y = $s * $cy; $r = $s * $rad
    $p = Pen $color ([Math]::Max(1.0, $s * 0.06))
    $rect = [System.Drawing.RectangleF]::new(($x - $r), ($y - $r), ($r * 2), ($r * 2))
    $g.DrawArc($p, $rect, 30, 140)
    $g.DrawArc($p, $rect, 210, 140)
    Draw-Arrowhead $g $s $p ($x + $r * 0.95) ($y - $r * 0.30) (-0.7) (-1)
    Draw-Arrowhead $g $s $p ($x - $r * 0.95) ($y + $r * 0.30) (0.7) (1)
    $p.Dispose()
}

$green = Argb 56 142 60
$blue = Argb 40 90 170
$teal = Argb 0 121 107

# ── Per-command icon designs (keyed by macro script name) ────────────────────
$designs = @{
    'MoleHillTwoPointInterpolation' = {
        param($g, $s)
        $p = Pen $green ([Math]::Max(1.0, $s * 0.06)); $g.DrawLine($p, ($s * 0.16), ($s * 0.78), ($s * 0.84), ($s * 0.22)); $p.Dispose()
        $b1 = Brush (Argb 25 70 130); $b2 = Brush $green
        $g.FillEllipse($b1, ($s * 0.10), ($s * 0.66), ($s * 0.20), ($s * 0.20))
        $g.FillEllipse($b1, ($s * 0.70), ($s * 0.10), ($s * 0.20), ($s * 0.20))
        $g.FillEllipse($b2, ($s * 0.40), ($s * 0.40), ($s * 0.18), ($s * 0.18))
        $b1.Dispose(); $b2.Dispose()
    }
    'MoleHillGradientInterpolation' = {
        param($g, $s)
        $p = Pen (Argb 120 120 120) ([Math]::Max(0.8, $s * 0.04)); $g.DrawLine($p, ($s * 0.14), ($s * 0.80), ($s * 0.86), ($s * 0.24)); $p.Dispose()
        $b = Brush $green; $i = 0
        foreach ($f in 0.16, 0.36, 0.56, 0.76) {
            $r = $s * (0.05 + 0.035 * $i)
            $cx = $s * $f; $cy = $s * (0.80 - ($f - 0.14) * (0.56 / 0.72))
            $g.FillEllipse($b, ($cx - $r), ($cy - $r), ($r * 2), ($r * 2)); $i++
        }
        $b.Dispose()
    }
    'MoleHillOffset3dPolyline' = {
        param($g, $s)
        $p1 = Pen $green ([Math]::Max(1.0, $s * 0.06))
        $g.DrawLines($p1, @((PtF ($s * 0.14) ($s * 0.42)), (PtF ($s * 0.40) ($s * 0.20)), (PtF ($s * 0.66) ($s * 0.42)), (PtF ($s * 0.86) ($s * 0.26))))
        $p1.Dispose()
        $p2 = Pen (Argb 120 190 130) ([Math]::Max(0.9, $s * 0.05)); $p2.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
        $g.DrawLines($p2, @((PtF ($s * 0.14) ($s * 0.66)), (PtF ($s * 0.40) ($s * 0.44)), (PtF ($s * 0.66) ($s * 0.66)), (PtF ($s * 0.86) ($s * 0.50))))
        $p2.Dispose()
    }
    'MoleHillReplaceCurveSection' = {
        param($g, $s)
        $p = Pen (Argb 120 120 120) ([Math]::Max(0.9, $s * 0.05)); $p.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
        $g.DrawBezier($p, ($s * 0.12), ($s * 0.74), ($s * 0.30), ($s * 0.30), ($s * 0.55), ($s * 0.90), ($s * 0.88), ($s * 0.40)); $p.Dispose()
        $p2 = Pen $green ([Math]::Max(1.2, $s * 0.08)); $g.DrawBezier($p2, ($s * 0.34), ($s * 0.52), ($s * 0.45), ($s * 0.70), ($s * 0.55), ($s * 0.72), ($s * 0.66), ($s * 0.56)); $p2.Dispose()
    }
    'MoleHillSlopeCurve' = {
        param($g, $s)
        $p = Pen $green ([Math]::Max(1.2, $s * 0.07)); $g.DrawBezier($p, ($s * 0.14), ($s * 0.82), ($s * 0.45), ($s * 0.74), ($s * 0.55), ($s * 0.28), ($s * 0.86), ($s * 0.20))
        Draw-Arrowhead $g $s $p ($s * 0.86) ($s * 0.20) (1) (-0.4); $p.Dispose()
    }
    'MoleHillTrimBoundary' = {
        param($g, $s)
        $p = Pen $green ([Math]::Max(1.0, $s * 0.06))
        $g.DrawPolygon($p, @((PtF ($s * 0.18) ($s * 0.22)), (PtF ($s * 0.82) ($s * 0.28)), (PtF ($s * 0.74) ($s * 0.80)), (PtF ($s * 0.22) ($s * 0.72))))
        $p.Dispose()
        $pc = Pen (Argb 200 60 60) ([Math]::Max(0.9, $s * 0.05)); $pc.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dot
        $g.DrawLine($pc, ($s * 0.50), ($s * 0.10), ($s * 0.50), ($s * 0.90)); $pc.Dispose()
    }
    'CurveThroughPt' = {
        param($g, $s)
        $p = Pen $green ([Math]::Max(1.2, $s * 0.07)); $g.DrawBezier($p, ($s * 0.12), ($s * 0.72), ($s * 0.36), ($s * 0.18), ($s * 0.62), ($s * 0.86), ($s * 0.88), ($s * 0.30)); $p.Dispose()
        $b = Brush (Argb 25 70 130)
        foreach ($pt in @(@(0.16, 0.66), @(0.46, 0.46), @(0.82, 0.36))) { $g.FillEllipse($b, ($s * $pt[0] - $s * 0.07), ($s * $pt[1] - $s * 0.07), ($s * 0.14), ($s * 0.14)) }
        $b.Dispose()
    }
    'MoleHillOrientToOrigin' = {
        param($g, $s)
        $ox = $s * 0.30; $oy = $s * 0.70
        $pz = Pen (Argb 41 128 185) ([Math]::Max(1.2, $s * 0.07)); $g.DrawLine($pz, $ox, $oy, $ox, ($s * 0.16)); Draw-Arrowhead $g $s $pz $ox ($s * 0.16) (0) (-1); $pz.Dispose()
        $px = Pen (Argb 192 57 43) ([Math]::Max(1.2, $s * 0.07)); $g.DrawLine($px, $ox, $oy, ($s * 0.86), $oy); Draw-Arrowhead $g $s $px ($s * 0.86) $oy 1 0; $px.Dispose()
        $py = Pen (Argb 39 174 96) ([Math]::Max(1.2, $s * 0.07)); $g.DrawLine($py, $ox, $oy, ($s * 0.60), ($s * 0.40)); Draw-Arrowhead $g $s $py ($s * 0.60) ($s * 0.40) (0.7) (-0.7); $py.Dispose()
    }
    'MoleHillApplyLayerTemplate' = {
        param($g, $s)
        Draw-Layers $g $s
        $p = Pen (Argb 39 174 96) ([Math]::Max(1.4, $s * 0.09)); $g.DrawLines($p, @((PtF ($s * 0.58) ($s * 0.66)), (PtF ($s * 0.70) ($s * 0.80)), (PtF ($s * 0.92) ($s * 0.42)))); $p.Dispose()
    }
    'MoleHillSoftEditCurves' = {
        param($g, $s)
        $p = Pen $green ([Math]::Max(1.2, $s * 0.07)); $g.DrawBezier($p, ($s * 0.12), ($s * 0.74), ($s * 0.40), ($s * 0.10), ($s * 0.60), ($s * 0.90), ($s * 0.88), ($s * 0.30)); $p.Dispose()
        $ph = Pen (Argb 150 150 150) ([Math]::Max(0.7, $s * 0.03)); $ph.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dot; $g.DrawLine($ph, ($s * 0.40), ($s * 0.10), ($s * 0.40), ($s * 0.50)); $ph.Dispose()
        $b = Brush (Argb 230 126 34); $g.FillEllipse($b, ($s * 0.33), ($s * 0.04), ($s * 0.14), ($s * 0.14)); $b.Dispose()
    }
    'MoleHillExternalizeBlock' = {
        param($g, $s)
        Draw-Cube $g $s 0.12 0.30 0.40
        $p = Pen (Argb 41 128 185) ([Math]::Max(1.4, $s * 0.08)); $g.DrawLine($p, ($s * 0.58), ($s * 0.42), ($s * 0.90), ($s * 0.20)); Draw-Arrowhead $g $s $p ($s * 0.90) ($s * 0.20) (1) (-0.7); $p.Dispose()
    }
    'MoleHillUpdateAllLinkedBlocks' = {
        param($g, $s)
        Draw-Cube $g $s 0.16 0.34 0.38
        Draw-Refresh $g $s (Argb 41 128 185) 0.5 0.5 0.40
    }
    'MoleHillSetSunNorth' = {
        param($g, $s)
        $b = Brush (Argb 245 176 26); $g.FillEllipse($b, ($s * 0.34), ($s * 0.42), ($s * 0.32), ($s * 0.32)); $b.Dispose()
        $p = Pen (Argb 245 176 26) ([Math]::Max(1.0, $s * 0.05))
        for ($a = 0; $a -lt 360; $a += 45) {
            $rad = [Math]::PI * $a / 180; $cx = $s * 0.5; $cy = $s * 0.58
            $g.DrawLine($p, ($cx + [Math]::Cos($rad) * $s * 0.22), ($cy + [Math]::Sin($rad) * $s * 0.22), ($cx + [Math]::Cos($rad) * $s * 0.30), ($cy + [Math]::Sin($rad) * $s * 0.30))
        }
        $p.Dispose()
        $pn = Pen (Argb 192 57 43) ([Math]::Max(1.2, $s * 0.07)); $g.DrawLine($pn, ($s * 0.5), ($s * 0.34), ($s * 0.5), ($s * 0.08)); Draw-Arrowhead $g $s $pn ($s * 0.5) ($s * 0.08) (0) (-1); $pn.Dispose()
    }
    'MoleHillEditLayerTemplates' = {
        param($g, $s)
        Draw-Layers $g $s
        $p = Pen (Argb 80 80 80) ([Math]::Max(1.2, $s * 0.07)); $g.DrawLine($p, ($s * 0.62), ($s * 0.82), ($s * 0.90), ($s * 0.40)); $p.Dispose()
        $b = Brush (Argb 245 176 26); $g.FillPolygon($b, @((PtF ($s * 0.58) ($s * 0.88)), (PtF ($s * 0.66) ($s * 0.78)), (PtF ($s * 0.62) ($s * 0.92)))); $b.Dispose()
    }
    'MoleHillImportWithGeoref' = {
        param($g, $s)
        Draw-Globe $g $s
        $p = Pen (Argb 39 174 96) ([Math]::Max(1.4, $s * 0.08)); $g.DrawLine($p, ($s * 0.66), ($s * 0.20), ($s * 0.66), ($s * 0.66)); Draw-Arrowhead $g $s $p ($s * 0.66) ($s * 0.66) 0 1; $p.Dispose()
    }
    'MoleHillExportWithGeoref' = {
        param($g, $s)
        Draw-Globe $g $s
        $p = Pen (Argb 41 128 185) ([Math]::Max(1.4, $s * 0.08)); $g.DrawLine($p, ($s * 0.66), ($s * 0.66), ($s * 0.66), ($s * 0.20)); Draw-Arrowhead $g $s $p ($s * 0.66) ($s * 0.20) (0) (-1); $p.Dispose()
    }
    'MoleHillLiftCurvesWithLine' = {
        param($g, $s)
        $p = Pen $green ([Math]::Max(1.2, $s * 0.07)); $g.DrawBezier($p, ($s * 0.12), ($s * 0.84), ($s * 0.38), ($s * 0.64), ($s * 0.62), ($s * 0.88), ($s * 0.88), ($s * 0.66)); $p.Dispose()
        $pa = Pen (Argb 41 128 185) ([Math]::Max(1.4, $s * 0.08)); $g.DrawLine($pa, ($s * 0.5), ($s * 0.70), ($s * 0.5), ($s * 0.14)); Draw-Arrowhead $g $s $pa ($s * 0.5) ($s * 0.14) (0) (-1); $pa.Dispose()
    }
    'MoleHillRemoveSavedGeoref' = {
        param($g, $s)
        Draw-Globe $g $s (Argb 120 120 120)
        $p = Pen (Argb 211 47 47) ([Math]::Max(1.4, $s * 0.09)); $g.DrawLine($p, ($s * 0.56), ($s * 0.30), ($s * 0.86), ($s * 0.60)); $g.DrawLine($p, ($s * 0.86), ($s * 0.30), ($s * 0.56), ($s * 0.60)); $p.Dispose()
    }
    'MoleHillCreateTerrain' = {
        param($g, $s)
        Draw-Triangle $g $s
        $b = Brush (Argb 39 174 96); $g.FillEllipse($b, ($s * 0.60), ($s * 0.60), ($s * 0.34), ($s * 0.34)); $b.Dispose()
        $p = Pen ([System.Drawing.Color]::White) ([Math]::Max(1.2, $s * 0.07)); $cx = $s * 0.77; $cy = $s * 0.77
        $g.DrawLine($p, $cx, ($cy - $s * 0.10), $cx, ($cy + $s * 0.10)); $g.DrawLine($p, ($cx - $s * 0.10), $cy, ($cx + $s * 0.10), $cy); $p.Dispose()
    }
    'MoleHillImportGeoTiff' = {
        param($g, $s)
        $cols = 4; $cell = $s * 0.50 / $cols; $x0 = $s * 0.14; $y0 = $s * 0.14
        for ($r = 0; $r -lt 4; $r++) { for ($c = 0; $c -lt 4; $c++) {
            $shade = 80 + (($r + $c) % 3) * 55
            $b = Brush (Argb $shade ([int]($shade * 0.9)) ([int]($shade * 0.6)))
            $g.FillRectangle($b, ($x0 + $c * $cell), ($y0 + $r * $cell), ($cell + 0.6), ($cell + 0.6)); $b.Dispose()
        } }
        $op = Pen (Argb 60 60 60) ([Math]::Max(0.7, $s * 0.03)); $g.DrawRectangle($op, $x0, $y0, ($s * 0.50), ($s * 0.50)); $op.Dispose()
        $p = Pen (Argb 39 174 96) ([Math]::Max(1.4, $s * 0.08)); $g.DrawLine($p, ($s * 0.74), ($s * 0.40), ($s * 0.74), ($s * 0.84)); Draw-Arrowhead $g $s $p ($s * 0.74) ($s * 0.84) 0 1; $p.Dispose()
    }
    'MoleHillApplySavedGeoref' = {
        param($g, $s)
        Draw-Globe $g $s
        $p = Pen (Argb 39 174 96) ([Math]::Max(1.4, $s * 0.09)); $g.DrawLines($p, @((PtF ($s * 0.52) ($s * 0.58)), (PtF ($s * 0.64) ($s * 0.72)), (PtF ($s * 0.90) ($s * 0.34)))); $p.Dispose()
    }
    'MoleHillPanel' = {
        param($g, $s)
        Draw-Triangle $g $s
    }
    'MoleHillConvertToRhino' = {
        param($g, $s)
        Draw-Triangle $g ($s * 0.62) $blue (Argb 30 70 140) $true
        $p = Pen (Argb 80 80 80) ([Math]::Max(1.2, $s * 0.07)); $g.DrawLine($p, ($s * 0.42), ($s * 0.62), ($s * 0.66), ($s * 0.62)); Draw-Arrowhead $g $s $p ($s * 0.66) ($s * 0.62) 1 0; $p.Dispose()
        Draw-Cube $g $s 0.58 0.50 0.30 (Argb 120 130 140)
    }
    'MoleHillResetTerrainBuild' = {
        param($g, $s)
        Draw-Triangle $g $s $blue (Argb 30 70 140) $false
        Draw-Refresh $g $s ([System.Drawing.Color]::White) 0.5 0.56 0.26
    }
    'MoleHillAddMarkerParentheses' = {
        param($g, $s)
        $p = Pen (Argb 41 128 185) ([Math]::Max(1.6, $s * 0.10))
        $g.DrawArc($p, ($s * 0.14), ($s * 0.18), ($s * 0.30), ($s * 0.50), 110, 140)
        $g.DrawArc($p, ($s * 0.40), ($s * 0.18), ($s * 0.30), ($s * 0.50), 290, 140); $p.Dispose()
        $pp = Pen (Argb 39 174 96) ([Math]::Max(1.4, $s * 0.09)); $cx = $s * 0.78; $cy = $s * 0.74
        $g.DrawLine($pp, $cx, ($cy - $s * 0.13), $cx, ($cy + $s * 0.13)); $g.DrawLine($pp, ($cx - $s * 0.13), $cy, ($cx + $s * 0.13), $cy); $pp.Dispose()
    }
    'MoleHillRemoveMarkerParentheses' = {
        param($g, $s)
        $p = Pen (Argb 120 120 120) ([Math]::Max(1.6, $s * 0.10))
        $g.DrawArc($p, ($s * 0.14), ($s * 0.18), ($s * 0.30), ($s * 0.50), 110, 140)
        $g.DrawArc($p, ($s * 0.40), ($s * 0.18), ($s * 0.30), ($s * 0.50), 290, 140); $p.Dispose()
        $pp = Pen (Argb 211 47 47) ([Math]::Max(1.4, $s * 0.09)); $cy = $s * 0.74; $g.DrawLine($pp, ($s * 0.65), $cy, ($s * 0.91), $cy); $pp.Dispose()
    }
    'UpdateElevationMarkers' = {
        param($g, $s)
        Draw-Pin $g $s (Argb 211 47 47) 0.38 0.36
        Draw-Refresh $g $s (Argb 41 128 185) 0.62 0.62 0.22
    }
    'SetPathWidth' = {
        param($g, $s)
        $b = Brush (Argb 90 90 95); $g.FillPolygon($b, @((PtF ($s * 0.20) ($s * 0.84)), (PtF ($s * 0.44) ($s * 0.16)), (PtF ($s * 0.62) ($s * 0.16)), (PtF ($s * 0.80) ($s * 0.84)))); $b.Dispose()
        $pc = Pen ([System.Drawing.Color]::White) ([Math]::Max(0.8, $s * 0.04)); $pc.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash; $g.DrawLine($pc, ($s * 0.51), ($s * 0.80), ($s * 0.53), ($s * 0.20)); $pc.Dispose()
        $pa = Pen (Argb 245 176 26) ([Math]::Max(1.2, $s * 0.07)); $g.DrawLine($pa, ($s * 0.24), ($s * 0.50), ($s * 0.76), ($s * 0.50))
        Draw-Arrowhead $g $s $pa ($s * 0.24) ($s * 0.50) (-1) (0); Draw-Arrowhead $g $s $pa ($s * 0.76) ($s * 0.50) (1) (0); $pa.Dispose()
    }
    # Note: 'SetPathwidth' (lowercase w) resolves to the 'SetPathWidth' design — hashtable lookup is
    # case-insensitive, so no separate entry is needed (and would collide as a duplicate key).
    'MoleHillSlopeCheckAndMark' = {
        param($g, $s)
        $pts = @((PtF ($s * 0.14) ($s * 0.82)), (PtF ($s * 0.80) ($s * 0.82)), (PtF ($s * 0.14) ($s * 0.26)))
        $b = Brush $green; $g.FillPolygon($b, $pts); $b.Dispose()
        $op = Pen (Argb 25 90 35) ([Math]::Max(0.9, $s * 0.05)); $g.DrawPolygon($op, $pts); $op.Dispose()
        $bw = Brush (Argb 245 176 26); $g.FillEllipse($bw, ($s * 0.30), ($s * 0.40), ($s * 0.18), ($s * 0.18)); $bw.Dispose()
    }
}

# ── Render + repack ──────────────────────────────────────────────────────────
[xml]$rui = Get-Content $ruiPath -Raw
$raw = Get-Content $ruiPath -Raw

$sizeNodes = @{
    'small_bitmap'  = 16
    'normal_bitmap' = 24
    'large_bitmap'  = 32
}

# macro bitmap_id -> command script name
$macroByBitmap = @{}
foreach ($m in $rui.SelectNodes("//macro_item")) {
    if ($m.bitmap_id) { $macroByBitmap[$m.bitmap_id] = $m.script }
}

function Render-Tile {
    param([string]$command, [int]$s)
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $design = $designs[$command]
    if ($design) { & $design $g $s }
    else {
        # Fallback: blue MoleHill triangle so nothing is ever blank.
        Draw-Triangle $g $s
    }
    $g.Dispose()
    return $bmp
}

$newBase64 = @()  # in document order: small, normal, large
foreach ($nodeName in 'small_bitmap', 'normal_bitmap', 'large_bitmap') {
    $node = $rui.SelectNodes("//$nodeName")[0]
    $item = $sizeNodes[$nodeName]
    $entries = $node.SelectNodes("bitmap_item")
    $count = $entries.Count
    $sheet = New-Object System.Drawing.Bitmap($item, ($item * $count), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $sg = [System.Drawing.Graphics]::FromImage($sheet)
    $sg.Clear([System.Drawing.Color]::Transparent)
    foreach ($bi in $entries) {
        $i = [int]$bi.index
        $cmd = $macroByBitmap[$bi.guid]
        $tile = Render-Tile $cmd $item
        $sg.DrawImage($tile, 0, ($i * $item), $item, $item)
        $tile.Dispose()
    }
    $sg.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $sheet.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $sheet.Dispose()
    $b64 = [Convert]::ToBase64String($ms.ToArray(), [Base64FormattingOptions]::InsertLineBreaks)
    $ms.Dispose()
    $newBase64 += $b64
    Write-Host ("Rendered {0}: {1} tiles @ {2}px" -f $nodeName, $count, $item)
}

# Replace the three <bitmap>...</bitmap> inner blobs in document order, leaving all else intact.
$rx = [regex]'(?s)<bitmap>.*?</bitmap>'
$idx = 0
$result = $rx.Replace($raw, {
        param($match)
        $b = $newBase64[$script:idx]; $script:idx++
        "<bitmap>$b</bitmap>"
    })

[System.IO.File]::WriteAllText($ruiPath, $result, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Updated $ruiPath"
