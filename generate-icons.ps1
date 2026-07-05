Add-Type -AssemblyName System.Drawing

$ghDir = "$PSScriptRoot\src\MoleHill.Grasshopper\Resources"
if (-not (Test-Path $ghDir)) { New-Item -ItemType Directory -Path $ghDir | Out-Null }

$rhinoDir = "$PSScriptRoot\src\MoleHill.Rhino\Resources"
if (-not (Test-Path $rhinoDir)) { New-Item -ItemType Directory -Path $rhinoDir | Out-Null }

$rhinoEmbeddedDir = "$PSScriptRoot\src\MoleHill.Rhino\EmbeddedResources"
if (-not (Test-Path $rhinoEmbeddedDir)) { New-Item -ItemType Directory -Path $rhinoEmbeddedDir | Out-Null }

function New-Icon {
    param([string]$path, [scriptblock]$draw, [int]$size = 24)
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    & $draw $g
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "Created: $path"
}

# Builds a multi-resolution .ico from a size-aware draw block (receives $g and the pixel size).
# Frames are uncompressed 32bpp BGRA DIBs (not PNG), so System.Drawing.Icon.ToBitmap() — the path
# Rhino/Eto use to render the panel icon through GDI — can decode them on Windows.
function New-Ico {
    param([string]$path, [scriptblock]$draw, [int[]]$sizes = @(16, 20, 24, 32, 40, 48, 64))
    $frames = @()
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)
        & $draw $g $s
        $g.Dispose()

        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($ms)
        # BITMAPINFOHEADER — biHeight doubled to cover the (empty) AND mask.
        $bw.Write([UInt32]40); $bw.Write([Int32]$s); $bw.Write([Int32]($s * 2))
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
        $bw.Write([UInt32]0); $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
        # XOR color data: 32bpp BGRA, bottom-up.
        for ($y = $s - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $s; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([Byte]$c.B); $bw.Write([Byte]$c.G); $bw.Write([Byte]$c.R); $bw.Write([Byte]$c.A)
            }
        }
        # AND mask: 1bpp, rows padded to 32 bits, left zeroed (alpha drives transparency).
        $maskRow = New-Object byte[] ([Math]::Floor(($s + 31) / 32) * 4)
        for ($y = 0; $y -lt $s; $y++) { $bw.Write($maskRow) }
        $bw.Flush()
        $bmp.Dispose()
        $frames += , ($ms.ToArray())
        $ms.Dispose()
    }

    $fs = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([UInt16]0)              # reserved
    $bw.Write([UInt16]1)              # type = icon
    $bw.Write([UInt16]$frames.Count)  # image count
    $offset = 6 + (16 * $frames.Count)
    for ($i = 0; $i -lt $frames.Count; $i++) {
        $len = $frames[$i].Length
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $bw.Write([Byte]$dim)         # width  (0 => 256)
        $bw.Write([Byte]$dim)         # height (0 => 256)
        $bw.Write([Byte]0)            # palette color count
        $bw.Write([Byte]0)            # reserved
        $bw.Write([UInt16]1)          # color planes
        $bw.Write([UInt16]32)         # bits per pixel
        $bw.Write([UInt32]$len)       # image data size
        $bw.Write([UInt32]$offset)    # image data offset
        $offset += $len
    }
    foreach ($f in $frames) { $bw.Write($f) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($path, $fs.ToArray())
    $bw.Dispose(); $fs.Dispose()
    Write-Host "Created: $path"
}

function PtF { param([double]$x, [double]$y) New-Object System.Drawing.PointF($x, $y) }
function Argb { param([int]$r, [int]$g, [int]$b, [int]$a = 255) [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }

# ── 24×24 Grasshopper icons ────────────────────────────────────────────────

# TinSurface - filled dark-blue triangle with interior mesh lines
New-Icon "$ghDir\TinSurface.png" {
    param($g)
    # Fill main triangle
    $pts = @(
        (New-Object System.Drawing.PointF(2, 21)),
        (New-Object System.Drawing.PointF(12, 3)),
        (New-Object System.Drawing.PointF(22, 21))
    )
    $fillBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(40, 80, 160))
    $g.FillPolygon($fillBrush, $pts)
    $fillBrush.Dispose()
    # Internal edge lines
    $innerPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(110, 160, 230), 1.2)
    $g.DrawLine($innerPen, 12, 3, 7, 13)
    $g.DrawLine($innerPen, 12, 3, 17, 13)
    $g.DrawLine($innerPen, 7, 13, 17, 13)
    $g.DrawLine($innerPen, 7, 13, 12, 21)
    $innerPen.Dispose()
    # Outer triangle outline
    $outPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(20, 50, 120), 2.5)
    $g.DrawPolygon($outPen, $pts)
    $outPen.Dispose()
}

# SlopeAnalysis - bold filled gradient triangle
New-Icon "$ghDir\SlopeAnalysis.png" {
    param($g)
    $pts = @(
        (New-Object System.Drawing.PointF(2, 21)),
        (New-Object System.Drawing.PointF(12, 2)),
        (New-Object System.Drawing.PointF(22, 21))
    )
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 21)),
        (New-Object System.Drawing.PointF(0, 2)),
        [System.Drawing.Color]::FromArgb(80, 180, 80),
        [System.Drawing.Color]::FromArgb(220, 60, 60))
    $g.FillPolygon($brush, $pts)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(30, 30, 30), 2)
    $g.DrawPolygon($pen, $pts)
    $pen.Dispose()
    $brush.Dispose()
}

# GradePad - filled trapezoid pad over terrain profile
New-Icon "$ghDir\GradePad.png" {
    param($g)
    # Fill pad trapezoid
    $padPts = @(
        (New-Object System.Drawing.PointF(4, 14)),
        (New-Object System.Drawing.PointF(20, 14)),
        (New-Object System.Drawing.PointF(22, 20)),
        (New-Object System.Drawing.PointF(2, 20))
    )
    $padBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(60, 120, 220))
    $g.FillPolygon($padBrush, $padPts)
    $padBrush.Dispose()
    # Terrain profile polyline
    $terrainPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, 60, 60), 2)
    $g.DrawLine($terrainPen, 1, 10, 6, 7)
    $g.DrawLine($terrainPen, 6, 7, 12, 9)
    $g.DrawLine($terrainPen, 12, 9, 18, 6)
    $g.DrawLine($terrainPen, 18, 6, 23, 7)
    $terrainPen.Dispose()
    # Slope lines from pad corners down to terrain
    $slopePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(30, 70, 160), 1.5)
    $g.DrawLine($slopePen, 4, 14, 6, 7)
    $g.DrawLine($slopePen, 20, 14, 18, 6)
    $slopePen.Dispose()
    # Pad outline
    $padPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(20, 60, 150), 1.5)
    $g.DrawPolygon($padPen, $padPts)
    $padPen.Dispose()
}

# Remesh - filled green triangle with internal white subdivision lines
New-Icon "$ghDir\Remesh.png" {
    param($g)
    $pts = @(
        (New-Object System.Drawing.PointF(2, 21)),
        (New-Object System.Drawing.PointF(12, 3)),
        (New-Object System.Drawing.PointF(22, 21))
    )
    $fillBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(50, 140, 60))
    $g.FillPolygon($fillBrush, $pts)
    $fillBrush.Dispose()
    # Internal subdivision edges in white
    $innerPen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 1.5)
    $g.DrawLine($innerPen, 7, 12, 17, 12)
    $g.DrawLine($innerPen, 7, 12, 12, 21)
    $g.DrawLine($innerPen, 17, 12, 12, 21)
    $g.DrawLine($innerPen, 12, 3, 7, 12)
    $g.DrawLine($innerPen, 12, 3, 17, 12)
    $innerPen.Dispose()
    # Outline
    $outPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(20, 80, 30), 2.5)
    $g.DrawPolygon($outPen, $pts)
    $outPen.Dispose()
}

# MeshAreas - 4 colored quadrants with bold dividers
New-Icon "$ghDir\MeshAreas.png" {
    param($g)
    $b1 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(80, 170, 80))
    $b2 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(70, 130, 210))
    $b3 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(210, 130, 50))
    $b4 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(140, 80, 190))
    $g.FillRectangle($b1, 2, 2, 10, 10)
    $g.FillRectangle($b2, 12, 2, 10, 10)
    $g.FillRectangle($b3, 2, 12, 10, 10)
    $g.FillRectangle($b4, 12, 12, 10, 10)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(30, 30, 30), 2)
    $g.DrawRectangle($pen, 2, 2, 20, 20)
    $g.DrawLine($pen, 12, 2, 12, 22)
    $g.DrawLine($pen, 2, 12, 22, 12)
    $pen.Dispose()
    $b1.Dispose(); $b2.Dispose(); $b3.Dispose(); $b4.Dispose()
}

# MeshCollage - overlapping shapes, more opaque fills and bold outlines
New-Icon "$ghDir\MeshCollage.png" {
    param($g)
    $brush1 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(210, 120, 180, 100))
    $brush2 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(210, 180, 150, 80))
    $brush3 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(210, 80, 140, 210))
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(40, 40, 40), 2)
    $g.FillRectangle($brush1, 1, 5, 14, 14)
    $g.DrawRectangle($pen, 1, 5, 14, 14)
    $g.FillEllipse($brush2, 8, 1, 14, 14)
    $g.DrawEllipse($pen, 8, 1, 14, 14)
    $pts = @(
        (New-Object System.Drawing.PointF(6, 10)),
        (New-Object System.Drawing.PointF(18, 10)),
        (New-Object System.Drawing.PointF(12, 22))
    )
    $g.FillPolygon($brush3, $pts)
    $g.DrawPolygon($pen, $pts)
    $pen.Dispose()
    $brush1.Dispose(); $brush2.Dispose(); $brush3.Dispose()
}

# Smoothing - jagged line + smooth bezier, both thicker
New-Icon "$ghDir\Smoothing.png" {
    param($g)
    $penJag = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(120, 120, 120), 2)
    $g.DrawLine($penJag, 2, 16, 6, 12)
    $g.DrawLine($penJag, 6, 12, 10, 16)
    $g.DrawLine($penJag, 10, 16, 14, 12)
    $g.DrawLine($penJag, 14, 12, 18, 16)
    $g.DrawLine($penJag, 18, 16, 22, 12)
    $penJag.Dispose()
    $penSmooth = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, 120, 210), 3)
    $g.DrawBezier($penSmooth, 2.0, 16.0, 7.0, 10.0, 14.0, 18.0, 22.0, 12.0)
    $penSmooth.Dispose()
}

# GradePath - filled road corridor with center line
New-Icon "$ghDir\GradePath.png" {
    param($g)
    # Fill road band using a GraphicsPath between two offset beziers
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    # Top edge of corridor
    $path.AddBezier(4.0, 18.0, 10.0, 2.0, 18.0, 18.0, 22.0, 6.0)
    # Bottom edge reversed
    $path.AddBezier(22.0, 10.0, 16.0, 22.0, 8.0, 6.0, 2.0, 22.0)
    $path.CloseFigure()
    $roadBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(90, 65, 50))
    $g.FillPath($roadBrush, $path)
    $roadBrush.Dispose()
    $path.Dispose()
    # Road edges
    $edgePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(50, 35, 25), 1.5)
    $g.DrawBezier($edgePen, 4.0, 18.0, 10.0, 2.0, 18.0, 18.0, 22.0, 6.0)
    $g.DrawBezier($edgePen, 2.0, 22.0, 8.0, 6.0, 16.0, 22.0, 22.0, 10.0)
    $edgePen.Dispose()
    # Center line in white
    $centerPen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 1.5)
    $g.DrawBezier($centerPen, 3.0, 20.0, 9.0, 4.0, 17.0, 20.0, 22.0, 8.0)
    $centerPen.Dispose()
}

# Assembly icon - M in triangle, bolder strokes
New-Icon "$ghDir\MoleHill.png" {
    param($g)
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(50, 100, 180))
    $pts = @(
        (New-Object System.Drawing.PointF(2, 21)),
        (New-Object System.Drawing.PointF(12, 2)),
        (New-Object System.Drawing.PointF(22, 21))
    )
    $g.FillPolygon($brush, $pts)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 2.5)
    $g.DrawLine($pen, 7, 18, 7, 10)
    $g.DrawLine($pen, 7, 10, 12, 15)
    $g.DrawLine($pen, 12, 15, 17, 10)
    $g.DrawLine($pen, 17, 10, 17, 18)
    $pen.Dispose()
    $brush.Dispose()
}

# Retaining Wall - keep as-is (already good)
New-Icon "$ghDir\RetainingWall.png" {
    param($g)
    $brown = [System.Drawing.Color]::FromArgb(121, 85, 72)
    $darkBrown = [System.Drawing.Color]::FromArgb(78, 52, 46)
    $brush = New-Object System.Drawing.SolidBrush($brown)
    $pen = New-Object System.Drawing.Pen($darkBrown, 1)
    # Post on left
    $g.FillRectangle($brush, 2, 4, 4, 17)
    $g.DrawRectangle($pen, 2, 4, 4, 17)
    # Three horizontal blocks
    $g.FillRectangle($brush, 7, 5, 14, 4)
    $g.DrawRectangle($pen, 7, 5, 14, 4)
    $g.FillRectangle($brush, 7, 10, 14, 4)
    $g.DrawRectangle($pen, 7, 10, 14, 4)
    $g.FillRectangle($brush, 7, 15, 14, 4)
    $g.DrawRectangle($pen, 7, 15, 14, 4)
    # Block dividers
    $g.DrawLine($pen, 14, 10, 14, 14)
    $g.DrawLine($pen, 11, 15, 11, 19)
    $pen.Dispose()
    $brush.Dispose()
}

# ── 16×16 Rhino panel tab icons ────────────────────────────────────────────

# TabModifiers - 3 solid bars with left-side blue accent strip
New-Icon "$rhinoDir\TabModifiers.png" {
    param($g)
    # Bars
    $barBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(70, 70, 70))
    $g.FillRectangle($barBrush, 2, 2, 12, 3)
    $g.FillRectangle($barBrush, 2, 6, 12, 3)
    $g.FillRectangle($barBrush, 2, 10, 12, 3)
    $barBrush.Dispose()
    # Left accent strip
    $accentBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(80, 80, 200))
    $g.FillRectangle($accentBrush, 0, 2, 2, 11)
    $accentBrush.Dispose()
} -size 16

# TabZones - divided rectangle with colored regions, bolder stroke
New-Icon "$rhinoDir\TabZones.png" {
    param($g)
    $b1 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(150, 200, 150))
    $b2 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(140, 180, 220))
    $b3 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(220, 180, 140))
    $g.FillRectangle($b1, 1, 1, 6, 14)
    $g.FillRectangle($b2, 8, 1, 7, 7)
    $g.FillRectangle($b3, 8, 9, 7, 6)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, 60, 60), 1.5)
    $g.DrawRectangle($pen, 1, 1, 14, 14)
    $g.DrawLine($pen, 8, 1, 8, 15)
    $g.DrawLine($pen, 8, 8, 15, 8)
    $pen.Dispose()
    $b1.Dispose(); $b2.Dispose(); $b3.Dispose()
} -size 16

# TabMarkers - bold pin: filled circle + filled triangle stem + white dot
New-Icon "$rhinoDir\TabMarkers.png" {
    param($g)
    $red = [System.Drawing.Color]::FromArgb(200, 50, 50)
    $redBrush = New-Object System.Drawing.SolidBrush($red)
    # Filled circle
    $g.FillEllipse($redBrush, 3, 1, 8, 8)
    # Filled stem triangle
    $stemPts = @(
        (New-Object System.Drawing.PointF(6, 8)),
        (New-Object System.Drawing.PointF(10, 8)),
        (New-Object System.Drawing.PointF(8, 13))
    )
    $g.FillPolygon($redBrush, $stemPts)
    $redBrush.Dispose()
    # White dot inside circle
    $whiteBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.FillEllipse($whiteBrush, 5.5, 3.5, 3, 3)
    $whiteBrush.Dispose()
} -size 16

# TabAnalysis - ascending bar chart with distinct greens
New-Icon "$rhinoDir\TabAnalysis.png" {
    param($g)
    $b1 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(60, 140, 80))
    $b2 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(80, 165, 60))
    $b3 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(110, 200, 60))
    $g.FillRectangle($b1, 1, 9, 4, 5)
    $g.FillRectangle($b2, 6, 5, 4, 9)
    $g.FillRectangle($b3, 11, 1, 4, 13)
    $b1.Dispose(); $b2.Dispose(); $b3.Dispose()
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(50, 50, 50), 1.5)
    $g.DrawLine($pen, 0, 14, 15, 14)
    $pen.Dispose()
} -size 16

# TabAnnotation - leader line with text label dot (annotation symbol)
New-Icon "$rhinoDir\TabAnnotation.png" {
    param($g)
    # Horizontal contour lines (3 wavy-ish lines of different widths)
    $pen1 = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(0, 121, 107), 1.5)
    $pen2 = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(0, 121, 107), 2.0)
    $pen3 = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(0, 121, 107), 1.0)
    $g.DrawLine($pen1, 1, 4, 15, 4)
    $g.DrawLine($pen2, 1, 8, 13, 8)
    $g.DrawLine($pen3, 1, 12, 11, 12)
    $pen1.Dispose(); $pen2.Dispose(); $pen3.Dispose()
    # Small leader dot at end of middle line
    $dot = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(21, 101, 192))
    $g.FillEllipse($dot, 13, 6, 3, 3)
    $dot.Dispose()
} -size 16

# ── 16×16 Rhino panel modifier type badges ──────────────────────────────────

# ModTriangulate - filled blue triangle (solid)
New-Icon "$rhinoDir\ModTriangulate.png" {
    param($g)
    $pts = @(
        (New-Object System.Drawing.PointF(8, 2)),
        (New-Object System.Drawing.PointF(2, 14)),
        (New-Object System.Drawing.PointF(14, 14))
    )
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(25, 118, 210))
    $g.FillPolygon($brush, $pts)
    $brush.Dispose()
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(10, 60, 140), 1.5)
    $g.DrawPolygon($pen, $pts)
    $pen.Dispose()
} -size 16

# ModAddGeometry - triangulate badge with additive plus marker
New-Icon "$rhinoDir\ModAddGeometry.png" {
    param($g)
    $pts = @(
        (New-Object System.Drawing.PointF(8, 2)),
        (New-Object System.Drawing.PointF(2, 14)),
        (New-Object System.Drawing.PointF(14, 14))
    )
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(2, 136, 209))
    $g.FillPolygon($brush, $pts)
    $brush.Dispose()
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(0, 84, 132), 1.5)
    $g.DrawPolygon($pen, $pts)
    $pen.Dispose()
    $plusPen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 1.6)
    $g.DrawLine($plusPen, 8, 5, 8, 11)
    $g.DrawLine($plusPen, 5, 8, 11, 8)
    $plusPen.Dispose()
} -size 16

# ModRemesh - filled green triangle with white internal lines
New-Icon "$rhinoDir\ModRemesh.png" {
    param($g)
    $pts = @(
        (New-Object System.Drawing.PointF(8, 2)),
        (New-Object System.Drawing.PointF(2, 14)),
        (New-Object System.Drawing.PointF(14, 14))
    )
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(56, 142, 60))
    $g.FillPolygon($brush, $pts)
    $brush.Dispose()
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(20, 80, 30), 1.5)
    $g.DrawPolygon($pen, $pts)
    $pen.Dispose()
    # Internal subdivision lines in white
    $innerPen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 1)
    $g.DrawLine($innerPen, 5, 8, 11, 8)
    $g.DrawLine($innerPen, 5, 8, 8, 14)
    $g.DrawLine($innerPen, 11, 8, 8, 14)
    $innerPen.Dispose()
} -size 16

# ModRetopo - indigo quad grid with white field-aligned cross marks
New-Icon "$rhinoDir\ModRetopo.png" {
    param($g)
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(63, 81, 181))
    $g.FillRectangle($brush, 2, 2, 12, 12)
    $brush.Dispose()
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(30, 40, 100), 1.5)
    $g.DrawRectangle($pen, 2, 2, 12, 12)
    $pen.Dispose()
    # Quad grid divider lines
    $gridPen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 1)
    $g.DrawLine($gridPen, 8, 2, 8, 14)
    $g.DrawLine($gridPen, 2, 8, 14, 8)
    $gridPen.Dispose()
    # Field-aligned cross marks in two quads
    $crossPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 202, 40), 1.2)
    $g.DrawLine($crossPen, 4, 3.5, 4, 6.5)
    $g.DrawLine($crossPen, 2.5, 5, 5.5, 5)
    $g.DrawLine($crossPen, 10, 9.5, 10, 12.5)
    $g.DrawLine($crossPen, 8.5, 11, 11.5, 11)
    $crossPen.Dispose()
} -size 16

# ModSmooth - two curves: jagged gray above, smooth purple below
New-Icon "$rhinoDir\ModSmooth.png" {
    param($g)
    # Jagged line
    $penJag = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(120, 120, 120), 1.5)
    $g.DrawLine($penJag, 1, 10, 5, 6)
    $g.DrawLine($penJag, 5, 6, 9, 10)
    $g.DrawLine($penJag, 9, 10, 13, 6)
    $g.DrawLine($penJag, 13, 6, 15, 10)
    $penJag.Dispose()
    # Smooth bezier
    $penSmooth = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(123, 31, 162), 2.5)
    $g.DrawBezier($penSmooth, 1.0, 10.0, 5.0, 3.0, 11.0, 13.0, 15.0, 7.0)
    $penSmooth.Dispose()
} -size 16

# ModSculpt - teal sculpted mound with white brush cursor ring
New-Icon "$rhinoDir\ModSculpt.png" {
    param($g)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddBezier(1.0, 14.0, 5.0, 3.0, 11.0, 3.0, 15.0, 14.0)
    $path.AddLine(15, 14, 1, 14)
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 137, 123))
    $g.FillPath($brush, $path)
    $brush.Dispose()
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(0, 77, 64), 1.5)
    $g.DrawPath($pen, $path)
    $pen.Dispose()
    $path.Dispose()
    # Brush cursor ring hovering over the crest
    $ringPen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 1.5)
    $g.DrawEllipse($ringPen, 5.5, 5.0, 5, 5)
    $ringPen.Dispose()
} -size 16

# ModRetainingWall - block wall, bolder outlines
New-Icon "$rhinoDir\ModRetainingWall.png" {
    param($g)
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(230, 74, 25))
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(160, 45, 10), 1.5)
    $g.FillRectangle($brush, 2, 3, 12, 3)
    $g.DrawRectangle($pen, 2, 3, 12, 3)
    $g.FillRectangle($brush, 2, 7, 12, 3)
    $g.DrawRectangle($pen, 2, 7, 12, 3)
    $g.FillRectangle($brush, 2, 11, 12, 3)
    $g.DrawRectangle($pen, 2, 11, 12, 3)
    $pen.Dispose()
    $brush.Dispose()
} -size 16

# ModGradePad - small filled pad trapezoid over slope
New-Icon "$rhinoDir\ModGradePad.png" {
    param($g)
    $padPts = @(
        (New-Object System.Drawing.PointF(5, 7)),
        (New-Object System.Drawing.PointF(11, 7)),
        (New-Object System.Drawing.PointF(13, 11)),
        (New-Object System.Drawing.PointF(3, 11))
    )
    $padBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(245, 124, 0))
    $g.FillPolygon($padBrush, $padPts)
    $padBrush.Dispose()
    $padPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(160, 80, 0), 1.5)
    $g.DrawPolygon($padPen, $padPts)
    $padPen.Dispose()
    # Slope/terrain line
    $slopePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(50, 50, 50), 1.5)
    $g.DrawLine($slopePen, 1, 14, 4, 11)
    $g.DrawLine($slopePen, 4, 11, 12, 11)
    $g.DrawLine($slopePen, 12, 11, 15, 14)
    $slopePen.Dispose()
} -size 16

# ModGradePath - road band: two offset beziers filled brown, center line pale
New-Icon "$rhinoDir\ModGradePath.png" {
    param($g)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddBezier(2.0, 10.0, 5.0, 2.0, 11.0, 12.0, 15.0, 4.0)
    $path.AddBezier(15.0, 7.0, 11.0, 15.0, 5.0, 5.0, 2.0, 13.0)
    $path.CloseFigure()
    $roadBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(93, 64, 55))
    $g.FillPath($roadBrush, $path)
    $roadBrush.Dispose()
    $path.Dispose()
    # Edges
    $edgePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(50, 30, 20), 1.2)
    $g.DrawBezier($edgePen, 2.0, 10.0, 5.0, 2.0, 11.0, 12.0, 15.0, 4.0)
    $g.DrawBezier($edgePen, 2.0, 13.0, 5.0, 5.0, 11.0, 15.0, 15.0, 7.0)
    $edgePen.Dispose()
    # Center line
    $centerPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(200, 180, 160), 1)
    $g.DrawBezier($centerPen, 2.0, 11.5, 5.0, 3.5, 11.0, 13.5, 15.0, 5.5)
    $centerPen.Dispose()
} -size 16

# ── Plugin / dockable-panel icon (multi-size .ico) ──────────────────────────
# MoleHill brand mark: a filled blue TIN triangle (mountain) with interior mesh
# edges and a bold dark outline. Scales cleanly from 16 px tab glyph to 256 px.
New-Ico "$rhinoEmbeddedDir\plugin-utility.ico" {
    param($g, $s)
    $apex = PtF ($s * 0.50) ($s * 0.12)
    $bl = PtF ($s * 0.10) ($s * 0.86)
    $br = PtF ($s * 0.90) ($s * 0.86)
    $pts = @($apex, $bl, $br)

    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (PtF 0 ($s * 0.12)), (PtF 0 ($s * 0.86)),
        (Argb 60 120 200), (Argb 30 70 140))
    $g.FillPolygon($fill, $pts)
    $fill.Dispose()

    # Interior mesh edges (TIN look)
    $mid = PtF ($s * 0.50) ($s * 0.86)
    $lm = PtF ($s * 0.30) ($s * 0.49)
    $rm = PtF ($s * 0.70) ($s * 0.49)
    $innerPen = New-Object System.Drawing.Pen((Argb 150 195 245), [Math]::Max(1.0, $s * 0.025))
    $g.DrawLine($innerPen, $lm, $rm)
    $g.DrawLine($innerPen, $lm, $mid)
    $g.DrawLine($innerPen, $rm, $mid)
    $g.DrawLine($innerPen, $apex, $lm)
    $g.DrawLine($innerPen, $apex, $rm)
    $innerPen.Dispose()

    $outPen = New-Object System.Drawing.Pen((Argb 18 50 110), [Math]::Max(1.5, $s * 0.07))
    $outPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPolygon($outPen, $pts)
    $outPen.Dispose()
}

# ── 16×16 Rhino panel: Objects tab icon ─────────────────────────────────────
# Three scattered terrain objects (filled diamonds) sitting on a ground line.
New-Icon "$rhinoDir\TabObjects.png" {
    param($g)
    $ground = New-Object System.Drawing.Pen((Argb 90 90 90), 1.5)
    $g.DrawLine($ground, 1, 13, 15, 13)
    $ground.Dispose()
    $b1 = New-Object System.Drawing.SolidBrush((Argb 67 160 71))
    $b2 = New-Object System.Drawing.SolidBrush((Argb 30 136 229))
    $b3 = New-Object System.Drawing.SolidBrush((Argb 142 68 173))
    $g.FillPolygon($b1, @((PtF 4 5), (PtF 6 9), (PtF 4 13), (PtF 2 9)))
    $g.FillPolygon($b2, @((PtF 9 2), (PtF 11 7), (PtF 9 12), (PtF 7 7)))
    $g.FillPolygon($b3, @((PtF 13 6), (PtF 15 10), (PtF 13 13), (PtF 11 10)))
    $b1.Dispose(); $b2.Dispose(); $b3.Dispose()
} -size 16

# ── 16×16 Object card icons ─────────────────────────────────────────────────

# ObjLowestPoint - down arrow dropping into a basin dot
New-Icon "$rhinoDir\ObjLowestPoint.png" {
    param($g)
    $blue = Argb 30 136 229
    $basin = New-Object System.Drawing.Pen((Argb 30 136 229), 1.5)
    $g.DrawArc($basin, 3, 7, 10, 9, 20, 140)
    $basin.Dispose()
    $arrow = New-Object System.Drawing.Pen($blue, 2)
    $g.DrawLine($arrow, 8, 2, 8, 9)
    $g.DrawLine($arrow, 5, 6, 8, 9)
    $g.DrawLine($arrow, 11, 6, 8, 9)
    $arrow.Dispose()
    $dot = New-Object System.Drawing.SolidBrush($blue)
    $g.FillEllipse($dot, 6, 11, 4, 4)
    $dot.Dispose()
} -size 16

# ObjScatter - several scattered filled dots
New-Icon "$rhinoDir\ObjScatter.png" {
    param($g)
    $b = New-Object System.Drawing.SolidBrush((Argb 142 68 173))
    $g.FillEllipse($b, 2, 3, 4, 4)
    $g.FillEllipse($b, 8, 1, 3, 3)
    $g.FillEllipse($b, 11, 6, 4, 4)
    $g.FillEllipse($b, 4, 9, 3, 3)
    $g.FillEllipse($b, 8, 11, 4, 4)
    $g.FillEllipse($b, 1, 13, 2, 2)
    $b.Dispose()
} -size 16

# ObjSurfaceOriented - tilted plane with a normal arrow
New-Icon "$rhinoDir\ObjSurfaceOriented.png" {
    param($g)
    $plane = New-Object System.Drawing.SolidBrush((Argb 67 160 71))
    $g.FillPolygon($plane, @((PtF 2 11), (PtF 9 13), (PtF 14 10), (PtF 7 8)))
    $plane.Dispose()
    $edge = New-Object System.Drawing.Pen((Argb 30 90 35), 1)
    $g.DrawPolygon($edge, @((PtF 2 11), (PtF 9 13), (PtF 14 10), (PtF 7 8)))
    $edge.Dispose()
    # Normal arrow rising off the plane
    $arrow = New-Object System.Drawing.Pen((Argb 30 90 35), 2)
    $g.DrawLine($arrow, 8, 10, 10, 2)
    $g.DrawLine($arrow, 10, 2, 8, 4)
    $g.DrawLine($arrow, 10, 2, 12, 5)
    $arrow.Dispose()
} -size 16

# ── 16×16 Analysis card icons ───────────────────────────────────────────────

# AnEarthwork - ground line with a fill mound above and a cut notch below
New-Icon "$rhinoDir\AnEarthwork.png" {
    param($g)
    $fill = New-Object System.Drawing.SolidBrush((Argb 141 110 99))
    $g.FillPolygon($fill, @((PtF 1 8), (PtF 5 3), (PtF 9 8)))      # fill mound (above)
    $cut = New-Object System.Drawing.SolidBrush((Argb 191 165 156))
    $g.FillPolygon($cut, @((PtF 8 8), (PtF 12 13), (PtF 15 8)))    # cut notch (below)
    $fill.Dispose(); $cut.Dispose()
    $line = New-Object System.Drawing.Pen((Argb 70 50 42), 1.5)
    $g.DrawLine($line, 1, 8, 15, 8)
    $line.Dispose()
} -size 16

# AnSlope - right triangle with an angle arc
New-Icon "$rhinoDir\AnSlope.png" {
    param($g)
    $pts = @((PtF 2 14), (PtF 14 14), (PtF 2 3))
    $brush = New-Object System.Drawing.SolidBrush((Argb 67 160 71))
    $g.FillPolygon($brush, $pts)
    $brush.Dispose()
    $pen = New-Object System.Drawing.Pen((Argb 25 90 35), 1.5)
    $g.DrawPolygon($pen, $pts)
    $pen.Dispose()
    $arc = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 1.2)
    $g.DrawArc($arc, 8, 10, 8, 8, 180, 60)
    $arc.Dispose()
} -size 16

# AnElevation - stacked terrain bands (elevation layers)
New-Icon "$rhinoDir\AnElevation.png" {
    param($g)
    $c1 = New-Object System.Drawing.SolidBrush((Argb 144 202 249))
    $c2 = New-Object System.Drawing.SolidBrush((Argb 66 165 245))
    $c3 = New-Object System.Drawing.SolidBrush((Argb 21 101 192))
    $g.FillPolygon($c3, @((PtF 1 13), (PtF 15 13), (PtF 13 10), (PtF 3 10)))
    $g.FillPolygon($c2, @((PtF 3 10), (PtF 13 10), (PtF 11 7), (PtF 5 7)))
    $g.FillPolygon($c1, @((PtF 5 7), (PtF 11 7), (PtF 9 4), (PtF 7 4)))
    $c1.Dispose(); $c2.Dispose(); $c3.Dispose()
} -size 16

# AnCutFill - ground line with fill arrow up and cut arrow down (+/-)
New-Icon "$rhinoDir\AnCutFill.png" {
    param($g)
    $line = New-Object System.Drawing.Pen((Argb 90 90 90), 1.5)
    $g.DrawLine($line, 1, 8, 15, 8)
    $line.Dispose()
    $up = New-Object System.Drawing.SolidBrush((Argb 239 108 0))      # fill
    $g.FillPolygon($up, @((PtF 4 2), (PtF 7 7), (PtF 1 7)))
    $down = New-Object System.Drawing.SolidBrush((Argb 25 118 210))   # cut
    $g.FillPolygon($down, @((PtF 11 14), (PtF 8 9), (PtF 14 9)))
    $up.Dispose(); $down.Dispose()
} -size 16

# AnContour - three nested contour rings
New-Icon "$rhinoDir\AnContour.png" {
    param($g)
    $pen = New-Object System.Drawing.Pen((Argb 0 121 107), 1.5)
    $g.DrawEllipse($pen, 1, 2, 14, 12)
    $g.DrawEllipse($pen, 4, 4, 8, 8)
    $g.DrawEllipse($pen, 6, 6, 4, 4)
    $pen.Dispose()
} -size 16

# AnCurveElevation - a curve with a small elevation tag
New-Icon "$rhinoDir\AnCurveElevation.png" {
    param($g)
    $curve = New-Object System.Drawing.Pen((Argb 21 101 192), 2)
    $g.DrawBezier($curve, 1.0, 13.0, 5.0, 4.0, 9.0, 14.0, 15.0, 5.0)
    $curve.Dispose()
    $tag = New-Object System.Drawing.SolidBrush((Argb 21 101 192))
    $g.FillRectangle($tag, 9, 1, 6, 4)
    $tag.Dispose()
    $tick = New-Object System.Drawing.Pen((Argb 21 101 192), 1)
    $g.DrawLine($tick, 12, 5, 12, 8)
    $tick.Dispose()
} -size 16

# AnCurveSlope - a curve with a direction arrow along it
New-Icon "$rhinoDir\AnCurveSlope.png" {
    param($g)
    $curve = New-Object System.Drawing.Pen((Argb 46 125 50), 2)
    $g.DrawBezier($curve, 1.0, 13.0, 6.0, 12.0, 9.0, 4.0, 15.0, 3.0)
    $curve.Dispose()
    $arrow = New-Object System.Drawing.Pen((Argb 46 125 50), 1.6)
    $g.DrawLine($arrow, 15, 3, 11, 3)
    $g.DrawLine($arrow, 15, 3, 13, 7)
    $arrow.Dispose()
} -size 16

# AnProjElevation - point projected down to a baseline with a tag
New-Icon "$rhinoDir\AnProjElevation.png" {
    param($g)
    $base = New-Object System.Drawing.Pen((Argb 90 90 90), 1.5)
    $g.DrawLine($base, 1, 14, 15, 14)
    $base.Dispose()
    $proj = New-Object System.Drawing.Pen((Argb 21 101 192), 1)
    $proj.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
    $g.DrawLine($proj, 6, 4, 6, 14)
    $proj.Dispose()
    $dot = New-Object System.Drawing.SolidBrush((Argb 21 101 192))
    $g.FillEllipse($dot, 4, 2, 4, 4)
    $dot.Dispose()
    $tag = New-Object System.Drawing.SolidBrush((Argb 21 101 192))
    $g.FillRectangle($tag, 9, 6, 6, 4)
    $tag.Dispose()
} -size 16

# AnPointSlope - a point with a slope arrow and tag
New-Icon "$rhinoDir\AnPointSlope.png" {
    param($g)
    $dot = New-Object System.Drawing.SolidBrush((Argb 2 136 209))
    $g.FillEllipse($dot, 2, 9, 4, 4)
    $dot.Dispose()
    $arrow = New-Object System.Drawing.Pen((Argb 2 136 209), 2)
    $g.DrawLine($arrow, 4, 11, 13, 4)
    $g.DrawLine($arrow, 13, 4, 9, 4)
    $g.DrawLine($arrow, 13, 4, 13, 8)
    $arrow.Dispose()
} -size 16

# AnTerrainSection - filled cut profile with vertical hatch
New-Icon "$rhinoDir\AnTerrainSection.png" {
    param($g)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddLines(@((PtF 1 6), (PtF 4 3), (PtF 8 7), (PtF 12 4), (PtF 15 8), (PtF 15 14), (PtF 1 14)))
    $path.CloseFigure()
    $fill = New-Object System.Drawing.SolidBrush((Argb 123 31 162 80))
    $g.FillPath($fill, $path)
    $fill.Dispose()
    $top = New-Object System.Drawing.Pen((Argb 123 31 162), 1.8)
    $g.DrawLines($top, @((PtF 1 6), (PtF 4 3), (PtF 8 7), (PtF 12 4), (PtF 15 8)))
    $top.Dispose()
    $path.Dispose()
    $hatch = New-Object System.Drawing.Pen((Argb 123 31 162), 0.8)
    for ($x = 3; $x -le 14; $x += 3) { $g.DrawLine($hatch, $x, 9, $x, 14) }
    $hatch.Dispose()
} -size 16

# AnCrossSection - baseline with perpendicular station cuts
New-Icon "$rhinoDir\AnCrossSection.png" {
    param($g)
    $base = New-Object System.Drawing.Pen((Argb 142 36 170), 2)
    $g.DrawLine($base, 1, 8, 15, 8)
    $base.Dispose()
    $tick = New-Object System.Drawing.Pen((Argb 142 36 170), 1.5)
    foreach ($x in 4, 8, 12) { $g.DrawLine($tick, $x, 3, $x, 13) }
    $tick.Dispose()
    $cap = New-Object System.Drawing.SolidBrush((Argb 142 36 170))
    foreach ($x in 4, 8, 12) { $g.FillEllipse($cap, ($x - 1), 2, 2, 2) }
    $cap.Dispose()
} -size 16

# AnLongSection - baseline with an unrolled longitudinal profile above
New-Icon "$rhinoDir\AnLongSection.png" {
    param($g)
    $base = New-Object System.Drawing.Pen((Argb 94 53 177), 1.5)
    $g.DrawLine($base, 1, 14, 15, 14)
    $base.Dispose()
    $profile = New-Object System.Drawing.Pen((Argb 94 53 177), 2)
    $g.DrawLines($profile, @((PtF 1 9), (PtF 4 5), (PtF 7 8), (PtF 10 3), (PtF 13 7), (PtF 15 5)))
    $profile.Dispose()
    $drop = New-Object System.Drawing.Pen((Argb 94 53 177), 0.8)
    foreach ($x in 4, 10) { $g.DrawLine($drop, $x, 5, $x, 14) }
    $drop.Dispose()
} -size 16

# AnFlowArrows - downhill arrows scattered on a loose grid
New-Icon "$rhinoDir\AnFlowArrows.png" {
    param($g)
    $pen = New-Object System.Drawing.Pen((Argb 2 136 209), 1.6)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    foreach ($o in @(@(2, 2), @(9, 4), @(4, 8), @(11, 10))) {
        $x = $o[0]; $y = $o[1]
        $g.DrawLine($pen, $x, $y, ($x + 3.2), ($y + 3.2))           # shaft (downhill = down-right)
        $g.DrawLine($pen, ($x + 3.2), ($y + 3.2), ($x + 0.8), ($y + 3.2)) # arrowhead back-x
        $g.DrawLine($pen, ($x + 3.2), ($y + 3.2), ($x + 3.2), ($y + 0.8)) # arrowhead back-y
    }
    $pen.Dispose()
} -size 16

# AnGradeCallout - two endpoint dots joined by a downhill slope line with an arrowhead
New-Icon "$rhinoDir\AnGradeCallout.png" {
    param($g)
    $teal = Argb 0 131 143
    $pen = New-Object System.Drawing.Pen($teal, 1.8)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($pen, 3, 3, 13, 12)            # slope line A -> B
    $g.DrawLine($pen, 13, 12, 8.4, 12)         # arrowhead
    $g.DrawLine($pen, 13, 12, 13, 7.4)
    $pen.Dispose()
    $b = New-Object System.Drawing.SolidBrush($teal)
    $g.FillEllipse($b, 1, 1, 4, 4)             # endpoint A (high)
    $g.FillEllipse($b, 11, 10, 4, 4)           # endpoint B (low)
    $b.Dispose()
} -size 16

Write-Host "All icons generated successfully."
