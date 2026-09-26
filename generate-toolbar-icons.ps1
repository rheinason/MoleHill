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
# Every tile is drawn procedurally below in one flat language: ink outlines, one green accent, white
# fills, no gradients, strokes heavy enough to survive 16 px, so the toolbar reads as a single set.
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

# ── Palette ──────────────────────────────────────────────────────────────────
# Every design uses only these three colours.
$ink = Argb 35 31 32
$green = Argb 25 157 73
$paper = Argb 255 255 255

# Stroke weights heavy enough to survive 16 px.
function InkPen { param($s, $w = 0.085) Pen $ink ([Math]::Max(1.4, $s * $w)) }
function AccentPen { param($s, $w = 0.095) Pen $green ([Math]::Max(1.5, $s * $w)) }

# ── Shared motifs ───────────────────────────────────────────────────────────
# A globe: ink outline with an equator and one meridian (project vs real-world coordinates).
function Draw-Globe {
    param($g, $s, $cx = 0.38, $cy = 0.42, $r = 0.30)
    $x = $s * $cx; $y = $s * $cy; $rr = $s * $r
    $b = Brush $paper; $g.FillEllipse($b, ($x - $rr), ($y - $rr), ($rr * 2), ($rr * 2)); $b.Dispose()
    $p = InkPen $s
    $g.DrawEllipse($p, ($x - $rr), ($y - $rr), ($rr * 2), ($rr * 2))
    $g.DrawLine($p, ($x - $rr), $y, ($x + $rr), $y)
    $p.Dispose()
}

# A green map pin.
function Draw-Pin {
    param($g, $s, $cx = 0.74, $cy = 0.36, $r = 0.15)
    $x = $s * $cx; $y = $s * $cy; $rr = $s * $r
    $b = Brush $green
    $g.FillEllipse($b, ($x - $rr), ($y - $rr), ($rr * 2), ($rr * 2))
    $g.FillPolygon($b, @((PtF ($x - $rr * 0.85) ($y + $rr * 0.55)), (PtF ($x + $rr * 0.85) ($y + $rr * 0.55)), (PtF $x ($y + $rr * 2.5))))
    $b.Dispose()
    $bw = Brush $paper; $g.FillEllipse($bw, ($x - $rr * 0.38), ($y - $rr * 0.38), ($rr * 0.76), ($rr * 0.76)); $bw.Dispose()
}

# A stack of three ink-outlined sheets (layer templates).
function Draw-Layers {
    param($g, $s, $cx = 0.46)
    $x = $s * $cx
    foreach ($f in 0.66, 0.48, 0.30) {
        $y = $s * $f; $hw = $s * 0.32; $hh = $s * 0.11
        $dia = @((PtF $x ($y - $hh)), (PtF ($x + $hw) $y), (PtF $x ($y + $hh)), (PtF ($x - $hw) $y))
        $b = Brush $paper; $g.FillPolygon($b, $dia); $b.Dispose()
        $p = InkPen $s 0.065; $g.DrawPolygon($p, $dia); $p.Dispose()
    }
}

# A document page with a folded corner and a small TIN mark (LandXML surface exchange).
function Draw-Page {
    param($g, $s, $ox = 0.10, $oy = 0.10, $w = 0.48, $h = 0.68)
    $x = $s * $ox; $y = $s * $oy; $ww = $s * $w; $hh = $s * $h; $fold = $ww * 0.36
    $body = @((PtF $x $y), (PtF ($x + $ww - $fold) $y), (PtF ($x + $ww) ($y + $fold)), (PtF ($x + $ww) ($y + $hh)), (PtF $x ($y + $hh)))
    $b = Brush $paper; $g.FillPolygon($b, $body); $b.Dispose()
    $p = InkPen $s 0.075; $g.DrawPolygon($p, $body)
    $g.DrawLine($p, ($x + $ww - $fold), $y, ($x + $ww - $fold), ($y + $fold))
    $g.DrawLine($p, ($x + $ww - $fold), ($y + $fold), ($x + $ww), ($y + $fold)); $p.Dispose()
    $tp = AccentPen $s 0.06
    $a = PtF ($x + $ww * 0.50) ($y + $hh * 0.46); $l = PtF ($x + $ww * 0.18) ($y + $hh * 0.80); $r = PtF ($x + $ww * 0.82) ($y + $hh * 0.80)
    $g.DrawPolygon($tp, @($a, $l, $r)); $tp.Dispose()
}

# Arrowhead at (x,y) pointing along (dx,dy).
function Draw-Arrowhead {
    param($g, $s, $pen, $x, $y, $dx, $dy)
    $len = $s * 0.22
    $n = [Math]::Sqrt($dx * $dx + $dy * $dy); if ($n -eq 0) { return }
    $ux = $dx / $n; $uy = $dy / $n; $px = -$uy; $py = $ux
    $bx = $x - $ux * $len; $by = $y - $uy * $len
    $g.DrawLine($pen, $x, $y, ($bx + $px * $len * 0.52), ($by + $py * $len * 0.52))
    $g.DrawLine($pen, $x, $y, ($bx - $px * $len * 0.52), ($by - $py * $len * 0.52))
}

# Two arrows forming a ring (update / reset).
function Draw-Refresh {
    param($g, $s, $color, $cx = 0.5, $cy = 0.5, $rad = 0.26)
    $x = $s * $cx; $y = $s * $cy; $r = $s * $rad
    $p = Pen $color ([Math]::Max(1.5, $s * 0.09))
    $rect = [System.Drawing.RectangleF]::new(($x - $r), ($y - $r), ($r * 2), ($r * 2))
    $g.DrawArc($p, $rect, 30, 140); $g.DrawArc($p, $rect, 210, 140)
    Draw-Arrowhead $g $s $p ($x + $r * 0.95) ($y - $r * 0.30) (-0.7) (-1)
    Draw-Arrowhead $g $s $p ($x - $r * 0.95) ($y + $r * 0.30) (0.7) (1)
    $p.Dispose()
}

# The green tick used wherever a command confirms or applies something.
function Draw-Check {
    param($g, $s, $ox = 0.46, $oy = 0.44)
    $p = AccentPen $s 0.13
    $g.DrawLines($p, @((PtF ($s * $ox) ($s * ($oy + 0.20))), (PtF ($s * ($ox + 0.14)) ($s * ($oy + 0.36))), (PtF ($s * ($ox + 0.46)) ($s * ($oy - 0.06))))); $p.Dispose()
}

# A filled point marker.
function Draw-Dot {
    param($g, $s, $cx, $cy, $r, $color)
    $b = Brush $color; $g.FillEllipse($b, ($s * ($cx - $r)), ($s * ($cy - $r)), ($s * $r * 2), ($s * $r * 2)); $b.Dispose()
}

# A page carrying a location pin (a georeferenced file).
function Draw-GeoPage {
    param($g, $s)
    $x = $s * 0.08; $y = $s * 0.08; $ww = $s * 0.52; $hh = $s * 0.76; $fold = $ww * 0.34
    $body = @((PtF $x $y), (PtF ($x + $ww - $fold) $y), (PtF ($x + $ww) ($y + $fold)), (PtF ($x + $ww) ($y + $hh)), (PtF $x ($y + $hh)))
    $b = Brush $paper; $g.FillPolygon($b, $body); $b.Dispose()
    $p = InkPen $s 0.075; $g.DrawPolygon($p, $body); $p.Dispose()
    Draw-Pin $g $s 0.34 0.40 0.13
}

# A raster tile: an ink frame with a checker of filled cells.
function Draw-Raster {
    param($g, $s, $ox, $oy, $size)
    $cell = $s * $size / 3; $x0 = $s * $ox; $y0 = $s * $oy
    $b = Brush $ink
    for ($r = 0; $r -lt 3; $r++) { for ($c = 0; $c -lt 3; $c++) {
        if ((($r + $c) % 2) -eq 0) { $g.FillRectangle($b, ($x0 + $c * $cell), ($y0 + $r * $cell), $cell, $cell) }
    } }
    $b.Dispose()
    $p = InkPen $s 0.065; $g.DrawRectangle($p, $x0, $y0, ($s * $size), ($s * $size)); $p.Dispose()
}

# A block: a paper square with an ink frame and a green insertion corner.
function Draw-Block {
    param($g, $s, $ox, $oy, $size)
    $x = $s * $ox; $y = $s * $oy; $w = $s * $size
    $b = Brush $paper; $g.FillRectangle($b, $x, $y, $w, $w); $b.Dispose()
    $p = InkPen $s 0.08; $g.DrawRectangle($p, $x, $y, $w, $w); $p.Dispose()
    $bg = Brush $green; $g.FillPolygon($bg, @((PtF $x ($y + $w)), (PtF $x ($y + $w * 0.55)), (PtF ($x + $w * 0.45) ($y + $w)))); $bg.Dispose()
}

# An elevation marker (a downward ink triangle on a level line) between green brackets.
function Draw-BracketedMarker {
    param($g, $s)
    $tri = @((PtF ($s * 0.30) ($s * 0.30)), (PtF ($s * 0.58) ($s * 0.30)), (PtF ($s * 0.44) ($s * 0.52)))
    $b = Brush $ink; $g.FillPolygon($b, $tri); $b.Dispose()
    $p = InkPen $s 0.06; $g.DrawLine($p, ($s * 0.24), ($s * 0.56), ($s * 0.64), ($s * 0.56)); $p.Dispose()
    $pa = AccentPen $s 0.08
    $g.DrawArc($pa, ($s * 0.06), ($s * 0.14), ($s * 0.24), ($s * 0.56), 110, 140)
    $g.DrawArc($pa, ($s * 0.58), ($s * 0.14), ($s * 0.24), ($s * 0.56), -70, 140); $pa.Dispose()
}

# A small ink plus (add) or minus (remove) badge in the lower-right corner.
function Draw-Badge {
    param($g, $s, [bool]$add)
    $cx = $s * 0.80; $cy = $s * 0.80; $h = $s * 0.14
    $p = InkPen $s 0.11
    $g.DrawLine($p, ($cx - $h), $cy, ($cx + $h), $cy)
    if ($add) { $g.DrawLine($p, $cx, ($cy - $h), $cx, ($cy + $h)) }
    $p.Dispose()
}

# ── Per-command icon designs (keyed by macro script name) ────────────────────
# Every toolbar button is drawn here; there is no bitmap source.
$designs = @{
    # ── Coordinates ──────────────────────────────────────────────────────────
    # World -> project: the globe hands off to a located pin.
    'mhConvertToProjectCoordinates' = {
        param($g, $s)
        Draw-Globe $g $s 0.30 0.34 0.28
        Draw-Pin $g $s 0.74 0.62 0.20
        $p = AccentPen $s 0.10; $g.DrawLine($p, ($s * 0.34), ($s * 0.76), ($s * 0.52), ($s * 0.86))
        Draw-Arrowhead $g $s $p ($s * 0.52) ($s * 0.86) (1) (0.55); $p.Dispose()
    }
    # Project -> world: the pin hands back to the globe.
    'mhConvertToRealWorldCoordinates' = {
        param($g, $s)
        Draw-Globe $g $s 0.70 0.64 0.28
        Draw-Pin $g $s 0.26 0.26 0.20
        $p = AccentPen $s 0.10; $g.DrawLine($p, ($s * 0.66), ($s * 0.24), ($s * 0.48), ($s * 0.14))
        Draw-Arrowhead $g $s $p ($s * 0.48) ($s * 0.14) (-1) (-0.55); $p.Dispose()
    }
    # ── Terrain ──────────────────────────────────────────────────────────────
    'mhCreateTerrain' = {
        param($g, $s)
        $pts = @((PtF ($s * 0.44) ($s * 0.14)), (PtF ($s * 0.06) ($s * 0.76)), (PtF ($s * 0.82) ($s * 0.76)))
        $b = Brush $green; $g.FillPolygon($b, $pts); $b.Dispose()
        $p = InkPen $s; $g.DrawPolygon($p, $pts); $g.DrawLine($p, ($s * 0.44), ($s * 0.14), ($s * 0.44), ($s * 0.76)); $p.Dispose()
        $pp = InkPen $s 0.11; $cx = $s * 0.80; $cy = $s * 0.80
        $g.DrawLine($pp, $cx, ($cy - $s * 0.16), $cx, ($cy + $s * 0.16))
        $g.DrawLine($pp, ($cx - $s * 0.16), $cy, ($cx + $s * 0.16), $cy); $pp.Dispose()
    }
    # A raster resolved into a terrain — right-click sibling of Import GeoTIFF.
    'mhImportGeoTiffTerrain' = {
        param($g, $s)
        $cell = $s * 0.42 / 3; $x0 = $s * 0.06; $y0 = $s * 0.08
        for ($r = 0; $r -lt 3; $r++) { for ($c = 0; $c -lt 3; $c++) {
            if ((($r + $c) % 2) -eq 0) { continue }
            $b = Brush $ink; $g.FillRectangle($b, ($x0 + $c * $cell), ($y0 + $r * $cell), $cell, $cell); $b.Dispose()
        } }
        $p = InkPen $s 0.065; $g.DrawRectangle($p, $x0, $y0, ($s * 0.42), ($s * 0.42)); $p.Dispose()
        $pts = @((PtF ($s * 0.70) ($s * 0.46)), (PtF ($s * 0.44) ($s * 0.94)), (PtF ($s * 0.96) ($s * 0.94)))
        $b2 = Brush $green; $g.FillPolygon($b2, $pts); $b2.Dispose()
        $p2 = InkPen $s 0.075; $g.DrawPolygon($p2, $pts); $p2.Dispose()
    }
    # Coded survey points resolving into linework: the run is the product, the points the by-product.
    'mhImportSurveyPoints' = {
        param($g, $s)
        # A solid triangle, not an arrow: a shaft plus a head inside five pixels is mud at 16 px.
        $tri = @((PtF ($s * 0.30) ($s * 0.08)), (PtF ($s * 0.70) ($s * 0.08)), (PtF ($s * 0.50) ($s * 0.42)))
        $b = Brush $ink; $g.FillPolygon($b, $tri); $b.Dispose()
        $pts = @((PtF ($s * 0.12) ($s * 0.72)), (PtF ($s * 0.38) ($s * 0.56)), (PtF ($s * 0.64) ($s * 0.84)), (PtF ($s * 0.90) ($s * 0.60)))
        $pa = AccentPen $s 0.12; $g.DrawLines($pa, $pts); $pa.Dispose()
        $bd = Brush $ink
        foreach ($pt in $pts) { $g.FillEllipse($bd, ($pt.X - $s * 0.07), ($pt.Y - $s * 0.07), ($s * 0.14), ($s * 0.14)) }
        $bd.Dispose()
    }
    'mhTrimBoundary' = {
        param($g, $s)
        $poly = @((PtF ($s * 0.14) ($s * 0.22)), (PtF ($s * 0.84) ($s * 0.28)), (PtF ($s * 0.76) ($s * 0.82)), (PtF ($s * 0.18) ($s * 0.74)))
        $b = Brush $paper; $g.FillPolygon($b, $poly); $b.Dispose()
        $p = InkPen $s; $g.DrawPolygon($p, $poly); $p.Dispose()
        $pc = AccentPen $s 0.10; $pc.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
        $g.DrawLine($pc, ($s * 0.50), ($s * 0.06), ($s * 0.50), ($s * 0.94)); $pc.Dispose()
    }
    # A wall in elevation: base rail, its offset top rail, and the face between.
    'mhCreateWall' = {
        param($g, $s)
        $face = @((PtF ($s * 0.10) ($s * 0.42)), (PtF ($s * 0.92) ($s * 0.30)), (PtF ($s * 0.92) ($s * 0.72)), (PtF ($s * 0.10) ($s * 0.80)))
        $b = Brush $paper; $g.FillPolygon($b, $face); $b.Dispose()
        $pj = InkPen $s 0.05
        foreach ($f in 0.34, 0.58, 0.80) {
            $t = ($f - 0.10) / 0.82
            $g.DrawLine($pj, ($s * $f), ($s * (0.42 - 0.12 * $t)), ($s * $f), ($s * (0.80 - 0.08 * $t)))
        }
        $pj.Dispose()
        $p = InkPen $s; $g.DrawPolygon($p, $face); $p.Dispose()
        $pt = AccentPen $s 0.10; $g.DrawLine($pt, ($s * 0.10), ($s * 0.42), ($s * 0.92), ($s * 0.30)); $pt.Dispose()
    }
    # ── Curve utilities ──────────────────────────────────────────────────────
    # A flat curve dropping onto the undulating ground it will follow. Deliberately NOT a pair of
    # parallel curves — that is the Offset Feature mark, and the two sit side by side on the toolbar.
    'mhDrapeCurve' = {
        param($g, $s)
        $pc = AccentPen $s 0.11; $g.DrawLine($pc, ($s * 0.04), ($s * 0.22), ($s * 0.96), ($s * 0.22)); $pc.Dispose()
        $pg = InkPen $s 0.10
        $g.DrawLines($pg, @((PtF ($s * 0.04) ($s * 0.72)), (PtF ($s * 0.30) ($s * 0.92)), (PtF ($s * 0.62) ($s * 0.66)), (PtF ($s * 0.96) ($s * 0.84)))); $pg.Dispose()
        $pa = InkPen $s 0.07
        foreach ($f in @(@(0.22, 0.62), @(0.50, 0.68), @(0.78, 0.60))) {
            $g.DrawLine($pa, ($s * $f[0]), ($s * 0.34), ($s * $f[0]), ($s * $f[1]))
            Draw-Arrowhead $g $s $pa ($s * $f[0]) ($s * $f[1]) 0 1
        }
        $pa.Dispose()
    }

    # Two curves crossing, opened up where they meet.
    'mhSplitAtIntersections' = {
        param($g, $s)
        $p = InkPen $s 0.075; $g.DrawLine($p, ($s * 0.26), ($s * 0.08), ($s * 0.70), ($s * 0.92)); $p.Dispose()
        $pa = AccentPen $s 0.10
        $g.DrawLine($pa, ($s * 0.06), ($s * 0.74), ($s * 0.38), ($s * 0.59))
        $g.DrawLine($pa, ($s * 0.62), ($s * 0.47), ($s * 0.94), ($s * 0.32)); $pa.Dispose()
        $b = Brush $ink; $r = $s * 0.09
        $g.FillEllipse($b, ($s * 0.50 - $r), ($s * 0.53 - $r), ($r * 2), ($r * 2)); $b.Dispose()
    }
    # The Slope Curve mark limited to a picked stretch — its right-click sibling.
    'mhSlopeCurveSection' = {
        param($g, $s)
        $p = InkPen $s 0.05; $p.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
        $g.DrawLine($p, ($s * 0.06), ($s * 0.90), ($s * 0.94), ($s * 0.16)); $p.Dispose()
        $pa = AccentPen $s 0.14; $g.DrawLine($pa, ($s * 0.30), ($s * 0.70), ($s * 0.66), ($s * 0.40))
        Draw-Arrowhead $g $s $pa ($s * 0.66) ($s * 0.40) (1) (-0.85); $pa.Dispose()
        $b = Brush $ink; $r = $s * 0.075
        foreach ($pt in @(@(0.30, 0.70), @(0.66, 0.40))) { $g.FillEllipse($b, ($s * $pt[0] - $r), ($s * $pt[1] - $r), ($r * 2), ($r * 2)) }
        $b.Dispose()
    }
    # A curve through picked points — the same family as 2 Point Interpolation.
    'CurveThroughPt' = {
        param($g, $s)
        $p = AccentPen $s 0.10; $g.DrawBezier($p, ($s * 0.08), ($s * 0.74), ($s * 0.34), ($s * 0.14), ($s * 0.62), ($s * 0.88), ($s * 0.92), ($s * 0.28)); $p.Dispose()
        $b = Brush $ink; $bw = Brush $paper; $r = $s * 0.10
        foreach ($pt in @(@(0.12, 0.66), @(0.46, 0.47), @(0.88, 0.34))) {
            $g.FillEllipse($b, ($s * $pt[0] - $r), ($s * $pt[1] - $r), ($r * 2), ($r * 2))
            $g.FillEllipse($bw, ($s * $pt[0] - $r * 0.4), ($s * $pt[1] - $r * 0.4), ($r * 0.8), ($r * 0.8))
        }
        $b.Dispose(); $bw.Dispose()
    }
    # Points and a curve cleared by a tick.
    'mhValidateTerrainInputs' = {
        param($g, $s)
        $p = InkPen $s 0.075; $g.DrawBezier($p, ($s * 0.06), ($s * 0.54), ($s * 0.28), ($s * 0.10), ($s * 0.50), ($s * 0.60), ($s * 0.78), ($s * 0.20)); $p.Dispose()
        $b = Brush $ink; $r = $s * 0.085
        foreach ($pt in @(@(0.10, 0.48), @(0.36, 0.32), @(0.62, 0.40))) { $g.FillEllipse($b, ($s * $pt[0] - $r), ($s * $pt[1] - $r), ($r * 2), ($r * 2)) }
        $b.Dispose()
        Draw-Check $g $s 0.40 0.42
    }
    # ── Layer templates ──────────────────────────────────────────────────────
    'mhEditLayerTemplates' = {
        param($g, $s)
        Draw-Layers $g $s 0.42
        $p = InkPen $s 0.075; $g.DrawLine($p, ($s * 0.58), ($s * 0.84), ($s * 0.90), ($s * 0.40)); $p.Dispose()
        $b = Brush $green; $g.FillPolygon($b, @((PtF ($s * 0.53) ($s * 0.92)), (PtF ($s * 0.64) ($s * 0.80)), (PtF ($s * 0.58) ($s * 0.96)))); $b.Dispose()
    }
    'mhResetLayerStyles' = {
        param($g, $s)
        Draw-Layers $g $s 0.40
        Draw-Refresh $g $s $green 0.70 0.62 0.24
    }
    # ── Document exchange ────────────────────────────────────────────────────
    'mhImportLandXml' = {
        param($g, $s)
        Draw-Page $g $s
        $p = AccentPen $s 0.09; $g.DrawLine($p, ($s * 0.80), ($s * 0.26), ($s * 0.80), ($s * 0.72))
        Draw-Arrowhead $g $s $p ($s * 0.80) ($s * 0.72) 0 1; $p.Dispose()
    }
    'mhExportLandXml' = {
        param($g, $s)
        Draw-Page $g $s
        $p = AccentPen $s 0.09; $g.DrawLine($p, ($s * 0.80), ($s * 0.72), ($s * 0.80), ($s * 0.26))
        Draw-Arrowhead $g $s $p ($s * 0.80) ($s * 0.26) (0) (-1); $p.Dispose()
    }
    # A georeferenced file in (pin on the page, arrow down) and out (arrow up).
    'mhImportWithGeoref' = {
        param($g, $s)
        Draw-GeoPage $g $s
        $p = AccentPen $s 0.09; $g.DrawLine($p, ($s * 0.82), ($s * 0.24), ($s * 0.82), ($s * 0.72))
        Draw-Arrowhead $g $s $p ($s * 0.82) ($s * 0.72) 0 1; $p.Dispose()
    }
    'mhExportWithGeoref' = {
        param($g, $s)
        Draw-GeoPage $g $s
        $p = AccentPen $s 0.09; $g.DrawLine($p, ($s * 0.82), ($s * 0.72), ($s * 0.82), ($s * 0.24))
        Draw-Arrowhead $g $s $p ($s * 0.82) ($s * 0.24) (0) (-1); $p.Dispose()
    }
    # A raster arriving: the checker tile with a green arrow dropping onto it.
    'mhImportGeoTiff' = {
        param($g, $s)
        Draw-Raster $g $s 0.10 0.40 0.54
        $p = AccentPen $s 0.10; $g.DrawLine($p, ($s * 0.80), ($s * 0.06), ($s * 0.80), ($s * 0.54))
        Draw-Arrowhead $g $s $p ($s * 0.80) ($s * 0.54) 0 1; $p.Dispose()
    }
    # ── Curve elevation ──────────────────────────────────────────────────────
    # Two fixed ends, the points between them set on the straight grade.
    'mhTwoPointInterpolation' = {
        param($g, $s)
        $p = InkPen $s 0.06; $g.DrawLine($p, ($s * 0.14), ($s * 0.80), ($s * 0.86), ($s * 0.24)); $p.Dispose()
        Draw-Dot $g $s 0.14 0.80 0.11 $ink
        Draw-Dot $g $s 0.86 0.24 0.11 $ink
        foreach ($t in 0.35, 0.65) { Draw-Dot $g $s (0.14 + 0.72 * $t) (0.80 - 0.56 * $t) 0.075 $green }
    }
    # A run held to a stated grade: the rise over run under a green grade line.
    'mhGradientInterpolation' = {
        param($g, $s)
        $p = InkPen $s 0.06
        $g.DrawLine($p, ($s * 0.12), ($s * 0.84), ($s * 0.88), ($s * 0.84))
        $g.DrawLine($p, ($s * 0.88), ($s * 0.84), ($s * 0.88), ($s * 0.30)); $p.Dispose()
        $pa = AccentPen $s 0.12; $g.DrawLine($pa, ($s * 0.12), ($s * 0.84), ($s * 0.88), ($s * 0.30)); $pa.Dispose()
        Draw-Dot $g $s 0.12 0.84 0.09 $ink
    }
    # The slope line itself: a green grade arrow over a dashed level reference.
    'mhSlopeCurve' = {
        param($g, $s)
        $p = InkPen $s 0.05; $p.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
        $g.DrawLine($p, ($s * 0.08), ($s * 0.86), ($s * 0.92), ($s * 0.86)); $p.Dispose()
        $pa = AccentPen $s 0.13; $g.DrawLine($pa, ($s * 0.10), ($s * 0.80), ($s * 0.82), ($s * 0.22))
        Draw-Arrowhead $g $s $pa ($s * 0.82) ($s * 0.22) (1) (-0.8); $pa.Dispose()
    }
    # Stacked contours read off where one picked line crosses them.
    'mhLiftCurvesWithLine' = {
        param($g, $s)
        $p = InkPen $s 0.06
        foreach ($y in 0.26, 0.50, 0.74) {
            $g.DrawBezier($p, ($s * 0.06), ($s * $y), ($s * 0.36), ($s * ($y - 0.10)), ($s * 0.64), ($s * ($y + 0.10)), ($s * 0.94), ($s * $y))
        }
        $p.Dispose()
        $pa = AccentPen $s 0.10; $g.DrawLine($pa, ($s * 0.24), ($s * 0.92), ($s * 0.76), ($s * 0.08)); $pa.Dispose()
        foreach ($f in @(@(0.64, 0.26), @(0.50, 0.50), @(0.36, 0.74))) { Draw-Dot $g $s $f[0] $f[1] 0.075 $ink }
    }
    # A stretch of an ink curve swapped for a new green run between two cut points.
    'mhReplaceCurveSection' = {
        param($g, $s)
        $p = InkPen $s 0.08
        $g.DrawLine($p, ($s * 0.06), ($s * 0.70), ($s * 0.30), ($s * 0.56))
        $g.DrawLine($p, ($s * 0.70), ($s * 0.56), ($s * 0.94), ($s * 0.70)); $p.Dispose()
        $pa = AccentPen $s 0.11; $g.DrawBezier($pa, ($s * 0.30), ($s * 0.56), ($s * 0.40), ($s * 0.16), ($s * 0.60), ($s * 0.16), ($s * 0.70), ($s * 0.56)); $pa.Dispose()
        Draw-Dot $g $s 0.30 0.56 0.085 $ink
        Draw-Dot $g $s 0.70 0.56 0.085 $ink
    }
    # A curve eased up in one place, the rest following smoothly.
    'mhSoftEditCurves' = {
        param($g, $s)
        # The original run dashed, so the eased green curve reads as the result rather than closing
        # into a triangle with it (a solid baseline made the whole mark read as an "A").
        $p = InkPen $s 0.05; $p.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
        $g.DrawLine($p, ($s * 0.30), ($s * 0.80), ($s * 0.70), ($s * 0.80)); $p.Dispose()
        $pa = AccentPen $s 0.10
        $g.DrawBezier($pa, ($s * 0.06), ($s * 0.80), ($s * 0.34), ($s * 0.80), ($s * 0.38), ($s * 0.36), ($s * 0.50), ($s * 0.36))
        $g.DrawBezier($pa, ($s * 0.50), ($s * 0.36), ($s * 0.62), ($s * 0.36), ($s * 0.66), ($s * 0.80), ($s * 0.94), ($s * 0.80)); $pa.Dispose()
        $pu = InkPen $s 0.07; $g.DrawLine($pu, ($s * 0.50), ($s * 0.30), ($s * 0.50), ($s * 0.06))
        Draw-Arrowhead $g $s $pu ($s * 0.50) ($s * 0.06) 0 (-1); $pu.Dispose()
    }
    # A feature copied sideways: the ink original and its green offset, with the gap marked.
    'mhOffsetFeature' = {
        param($g, $s)
        $p = InkPen $s 0.08; $g.DrawBezier($p, ($s * 0.06), ($s * 0.66), ($s * 0.34), ($s * 0.40), ($s * 0.62), ($s * 0.90), ($s * 0.94), ($s * 0.62)); $p.Dispose()
        $pa = AccentPen $s 0.10; $g.DrawBezier($pa, ($s * 0.06), ($s * 0.36), ($s * 0.34), ($s * 0.10), ($s * 0.62), ($s * 0.60), ($s * 0.94), ($s * 0.32)); $pa.Dispose()
        $pg = InkPen $s 0.05; $g.DrawLine($pg, ($s * 0.50), ($s * 0.44), ($s * 0.50), ($s * 0.70)); $pg.Dispose()
    }
    # The curve under a magnifier, its profile showing inside the lens.
    'mhInspectCurve' = {
        param($g, $s)
        $cx = $s * 0.42; $cy = $s * 0.42; $r = $s * 0.30
        $b = Brush $paper; $g.FillEllipse($b, ($cx - $r), ($cy - $r), ($r * 2), ($r * 2)); $b.Dispose()
        $pa = AccentPen $s 0.08
        $g.DrawLines($pa, @((PtF ($s * 0.20) ($s * 0.52)), (PtF ($s * 0.34) ($s * 0.34)), (PtF ($s * 0.48) ($s * 0.46)), (PtF ($s * 0.64) ($s * 0.30)))); $pa.Dispose()
        $p = InkPen $s 0.08; $g.DrawEllipse($p, ($cx - $r), ($cy - $r), ($r * 2), ($r * 2)); $p.Dispose()
        $ph = InkPen $s 0.15; $g.DrawLine($ph, ($s * 0.66), ($s * 0.66), ($s * 0.90), ($s * 0.90)); $ph.Dispose()
    }
    # ── Model placement ──────────────────────────────────────────────────────
    # A shape carried back to the world axes.
    'mhOrientToOrigin' = {
        param($g, $s)
        $p = InkPen $s 0.08
        $g.DrawLine($p, ($s * 0.14), ($s * 0.08), ($s * 0.14), ($s * 0.86))
        $g.DrawLine($p, ($s * 0.14), ($s * 0.86), ($s * 0.92), ($s * 0.86)); $p.Dispose()
        $sq = @((PtF ($s * 0.58) ($s * 0.14)), (PtF ($s * 0.88) ($s * 0.20)), (PtF ($s * 0.82) ($s * 0.50)), (PtF ($s * 0.52) ($s * 0.44)))
        $b = Brush $green; $g.FillPolygon($b, $sq); $b.Dispose()
        $pa = InkPen $s 0.07; $g.DrawLine($pa, ($s * 0.54), ($s * 0.52), ($s * 0.28), ($s * 0.72))
        Draw-Arrowhead $g $s $pa ($s * 0.28) ($s * 0.72) (-1) (0.8); $pa.Dispose()
    }
    # The sun with a north arrow beside it.
    'mhSetSunNorth' = {
        param($g, $s)
        $cx = $s * 0.36; $cy = $s * 0.60; $r = $s * 0.16
        $b = Brush $green; $g.FillEllipse($b, ($cx - $r), ($cy - $r), ($r * 2), ($r * 2)); $b.Dispose()
        $pr = AccentPen $s 0.07
        foreach ($k in 0..7) {
            $a = $k * [Math]::PI / 4
            $g.DrawLine($pr, ($cx + [Math]::Cos($a) * $r * 1.45), ($cy + [Math]::Sin($a) * $r * 1.45), ($cx + [Math]::Cos($a) * $r * 1.95), ($cy + [Math]::Sin($a) * $r * 1.95))
        }
        $pr.Dispose()
        $n = @((PtF ($s * 0.80) ($s * 0.06)), (PtF ($s * 0.92) ($s * 0.44)), (PtF ($s * 0.80) ($s * 0.36)), (PtF ($s * 0.68) ($s * 0.44)))
        $bi = Brush $ink; $g.FillPolygon($bi, $n); $bi.Dispose()
        $p = InkPen $s 0.07; $g.DrawLine($p, ($s * 0.80), ($s * 0.36), ($s * 0.80), ($s * 0.92)); $p.Dispose()
    }
    # ── Layers and blocks ────────────────────────────────────────────────────
    'mhApplyLayerTemplate' = {
        param($g, $s)
        Draw-Layers $g $s 0.40
        Draw-Check $g $s 0.48 0.52
    }
    # A block handed out to its own file.
    'mhExternalizeBlock' = {
        param($g, $s)
        Draw-Block $g $s 0.08 0.40 0.46
        Draw-Page $g $s 0.56 0.08 0.36 0.50
        $p = AccentPen $s 0.09; $g.DrawLine($p, ($s * 0.40), ($s * 0.52), ($s * 0.62), ($s * 0.72))
        Draw-Arrowhead $g $s $p ($s * 0.62) ($s * 0.72) (1) (0.9); $p.Dispose()
    }
    # Linked blocks brought back in step with their files.
    'mhUpdateAllLinkedBlocks' = {
        param($g, $s)
        Draw-Block $g $s 0.08 0.10 0.46
        Draw-Refresh $g $s $green 0.68 0.68 0.24
    }
    # ── Elevation markers ────────────────────────────────────────────────────
    # A level marker wrapped in brackets; the badge says whether they go on or come off.
    'mhAddMarkerParentheses' = {
        param($g, $s)
        Draw-BracketedMarker $g $s
        Draw-Badge $g $s $true
    }
    'mhRemoveMarkerParentheses' = {
        param($g, $s)
        Draw-BracketedMarker $g $s
        Draw-Badge $g $s $false
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
# A macro's script may be a raw Rhino macro ("! _CurveThroughPt" plus option lines), so the design key
# is the first line with the "!"/"_" macro prefix stripped.
function Design-Key { param([string]$script)
    if (-not $script) { return '' }
    ($script -split "`n")[0].Trim().TrimStart('!', ' ', '_')
}

# Two macros can share one bitmap_id (a button and its right-click partner). Rhino paints a button
# from its LEFT macro, so that is the macro the shared tile must be drawn for.
$leftMacros = @{}
foreach ($item in $rui.SelectNodes("//tool_bar_item/left_macro_id")) {
    if ($item.InnerText) { $leftMacros[$item.InnerText.Trim()] = $true }
}

$macroByBitmap = @{}
foreach ($m in $rui.SelectNodes("//macro_item")) {
    if (-not $m.bitmap_id) { continue }
    if ((-not $macroByBitmap.ContainsKey($m.bitmap_id)) -or $leftMacros.ContainsKey($m.guid)) {
        $macroByBitmap[$m.bitmap_id] = (Design-Key $m.script)
    }
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
        # No design for this macro. Record it and draw a plain placeholder; the run
        # aborts before writing, so this never ships — a fallback icon would be indistinguishable
        # from a real one.
        $script:missingDesigns[$command] = $true
        $p = InkPen $s; $g.DrawRectangle($p, ($s * 0.16), ($s * 0.16), ($s * 0.68), ($s * 0.68)); $p.Dispose()
    }
    $g.Dispose()
    return $bmp
}

$missingDesigns = @{}
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

if ($missingDesigns.Count -gt 0) {
    throw ("No icon design for: {0}. Add an entry to `$designs (or alias an existing one) before regenerating." -f (($missingDesigns.Keys | Sort-Object) -join ', '))
}

# Replace the three <bitmap>...</bitmap> inner blobs in document order, leaving all else intact.
$rx = [regex]'(?s)<bitmap>.*?</bitmap>'
if ($rx.Matches($raw).Count -ne 3) {
    throw ("Expected 3 <bitmap> blobs in the .rui, found {0}. Refusing to write a toolbar with no icon data." -f $rx.Matches($raw).Count)
}
$idx = 0
$result = $rx.Replace($raw, {
        param($match)
        $b = $newBase64[$script:idx]; $script:idx++
        "<bitmap>$b</bitmap>"
    })

[System.IO.File]::WriteAllText($ruiPath, $result, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Updated $ruiPath"
