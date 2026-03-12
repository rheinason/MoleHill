Add-Type -AssemblyName System.Drawing

$ghDir = "$PSScriptRoot\src\MoleHill.Grasshopper\Resources"
if (-not (Test-Path $ghDir)) { New-Item -ItemType Directory -Path $ghDir | Out-Null }

$rhinoDir = "$PSScriptRoot\src\MoleHill.Rhino\Resources"
if (-not (Test-Path $rhinoDir)) { New-Item -ItemType Directory -Path $rhinoDir | Out-Null }

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

Write-Host "All icons generated successfully."
