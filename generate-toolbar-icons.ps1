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
# Two sources feed a tile. Where the original hand-drawn artwork covers a command, the committed PNG
# in Toolbars/icons/<command>-<size>.png wins — it is rendered from the Illustrator vector source by
# tools/render-toolbar-artboards.py and is both clearer and more specific than anything drawn here.
# Everything else is drawn procedurally below, in that same flat language: ink outlines, one green
# accent, white fills, no gradients — so the toolbar reads as a single set.
#
# Re-run after changing a design. Rhino caches opened toolbars, so reopen the .rui (or restart Rhino)
# to see changes; ToolbarInstaller re-copies it to %AppData% when the packaged copy is newer.

Add-Type -AssemblyName System.Drawing

$ruiPath = "$PSScriptRoot\src\MoleHill.Rhino\Toolbars\MoleHill.Toolbar.rui"
$iconDir = "$PSScriptRoot\src\MoleHill.Rhino\Toolbars\icons"

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
# Sampled from Master.ai. Procedural designs use only these, so a drawn icon sits beside a
# hand-drawn one without looking like it came from a different set.
$ink = Argb 35 31 32
$green = Argb 25 157 73
$paper = Argb 255 255 255

# Stroke weights matched to the hand-drawn line — heavy enough to survive 16 px.
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

# A map pin, green like the hand-drawn set's location mark.
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

# ── Per-command icon designs (keyed by macro script name) ────────────────────
# Only commands with no hand-drawn artboard appear here; the rest come from the committed PNGs
# rendered by tools/render-toolbar-artboards.py.
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
    # A raster resolved into a terrain — right-click sibling of the hand-drawn Import GeoTIFF.
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
    # The Slope Curve mark limited to a picked stretch — right-click sibling of the hand-drawn one.
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
    # A curve through picked points — the same family as the hand-drawn 2 Point Interpolation.
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
    $asset = Join-Path $iconDir ("{0}-{1}.png" -f $command, $s)
    if (Test-Path $asset) {
        # Hand-drawn original, rendered from the vector source. Draw it 1:1 — never resample.
        $src = [System.Drawing.Image]::FromFile($asset)
        $g.DrawImage($src, 0, 0, $s, $s)
        $src.Dispose()
        $g.Dispose()
        return $bmp
    }
    $design = $designs[$command]
    if ($design) { & $design $g $s }
    else {
        # No design and no asset for this macro. Record it and draw a plain placeholder; the run
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
