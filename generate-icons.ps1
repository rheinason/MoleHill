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

# TIN Surface - triangle mesh wireframe
New-Icon "$ghDir\TinSurface.png" {
    param($g)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60,60,60), 1.5)
    $g.DrawLine($pen, 2, 20, 12, 3)
    $g.DrawLine($pen, 12, 3, 22, 20)
    $g.DrawLine($pen, 2, 20, 22, 20)
    $g.DrawLine($pen, 12, 3, 7, 13)
    $g.DrawLine($pen, 7, 13, 2, 20)
    $g.DrawLine($pen, 7, 13, 17, 13)
    $g.DrawLine($pen, 17, 13, 22, 20)
    $g.DrawLine($pen, 12, 3, 17, 13)
    $g.DrawLine($pen, 7, 13, 12, 20)
    $g.DrawLine($pen, 17, 13, 12, 20)
    $pen.Dispose()
}

# Slope Analysis - gradient triangle
New-Icon "$ghDir\SlopeAnalysis.png" {
    param($g)
    $pts = @(
        (New-Object System.Drawing.PointF(2, 21)),
        (New-Object System.Drawing.PointF(12, 2)),
        (New-Object System.Drawing.PointF(22, 21))
    )
    $brush1 = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 21)),
        (New-Object System.Drawing.PointF(0, 2)),
        [System.Drawing.Color]::FromArgb(80, 180, 80),
        [System.Drawing.Color]::FromArgb(220, 60, 60))
    $g.FillPolygon($brush1, $pts)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60,60,60), 1)
    $g.DrawPolygon($pen, $pts)
    $pen.Dispose()
    $brush1.Dispose()
}

# Grade Pad - flat rectangle on terrain
New-Icon "$ghDir\GradePad.png" {
    param($g)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(100,100,100), 1.5)
    $g.DrawLine($pen, 1, 18, 5, 14)
    $g.DrawLine($pen, 19, 14, 23, 18)
    $pen.Dispose()
    $penBlue = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(50,100,200), 2)
    $g.DrawLine($penBlue, 5, 14, 19, 14)
    $penBlue.Dispose()
    $penTerrain = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(100,100,100), 1)
    $g.DrawLine($penTerrain, 1, 8, 8, 5)
    $g.DrawLine($penTerrain, 8, 5, 16, 7)
    $g.DrawLine($penTerrain, 16, 7, 23, 4)
    $penTerrain.Dispose()
}

# Remesh - subdivided triangle
New-Icon "$ghDir\Remesh.png" {
    param($g)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60,60,60), 1.2)
    $g.DrawLine($pen, 2, 21, 12, 3)
    $g.DrawLine($pen, 12, 3, 22, 21)
    $g.DrawLine($pen, 2, 21, 22, 21)
    $penThin = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(120,120,120), 0.8)
    $g.DrawLine($penThin, 7, 12, 17, 12)
    $g.DrawLine($penThin, 7, 12, 12, 21)
    $g.DrawLine($penThin, 17, 12, 12, 21)
    $g.DrawLine($penThin, 12, 3, 7, 12)
    $g.DrawLine($penThin, 12, 3, 17, 12)
    $dotBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(50,120,200))
    $g.FillEllipse($dotBrush, 6, 11, 3, 3)
    $g.FillEllipse($dotBrush, 16, 11, 3, 3)
    $g.FillEllipse($dotBrush, 11, 20, 3, 3)
    $g.FillEllipse($dotBrush, 11, 6, 3, 3)
    $dotBrush.Dispose()
    $pen.Dispose()
    $penThin.Dispose()
}

# Mesh Areas - divided colored regions
New-Icon "$ghDir\MeshAreas.png" {
    param($g)
    $brush1 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(150, 200, 150))
    $brush2 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(200, 180, 140))
    $brush3 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(180, 160, 200))
    $g.FillRectangle($brush1, 2, 2, 10, 10)
    $g.FillRectangle($brush2, 12, 2, 10, 10)
    $g.FillRectangle($brush3, 2, 12, 10, 10)
    $g.FillRectangle($brush1, 12, 12, 10, 10)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60,60,60), 1)
    $g.DrawRectangle($pen, 2, 2, 20, 20)
    $g.DrawLine($pen, 12, 2, 12, 22)
    $g.DrawLine($pen, 2, 12, 22, 12)
    $pen.Dispose()
    $brush1.Dispose(); $brush2.Dispose(); $brush3.Dispose()
}

# Mesh Collage - overlapping colored shapes
New-Icon "$ghDir\MeshCollage.png" {
    param($g)
    $brush1 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(180, 120, 180, 100))
    $brush2 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(180, 180, 150, 100))
    $brush3 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(180, 100, 150, 200))
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60,60,60), 1)
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

# Smoothing - wavy terrain contour
New-Icon "$ghDir\Smoothing.png" {
    param($g)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(100,100,100), 1.2)
    $g.DrawLine($pen, 2, 16, 6, 12)
    $g.DrawLine($pen, 6, 12, 10, 16)
    $g.DrawLine($pen, 10, 16, 14, 12)
    $g.DrawLine($pen, 14, 12, 18, 16)
    $g.DrawLine($pen, 18, 16, 22, 12)
    $pen.Dispose()
    $penSmooth = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(80,130,200), 1.5)
    $g.DrawBezier($penSmooth, 2.0, 16.0, 7.0, 10.0, 14.0, 18.0, 22.0, 12.0)
    $penSmooth.Dispose()
}

# GradePath - road/path curve
New-Icon "$ghDir\GradePath.png" {
    param($g)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60,60,60), 1.5)
    $g.DrawBezier($pen, 2.0, 20.0, 8.0, 4.0, 16.0, 20.0, 22.0, 8.0)
    $pen.Dispose()
    $penEdge = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(120,80,60), 1)
    $g.DrawBezier($penEdge, 4.0, 20.0, 10.0, 6.0, 18.0, 20.0, 22.0, 10.0)
    $g.DrawBezier($penEdge, 1.0, 18.0, 6.0, 2.0, 14.0, 18.0, 20.0, 6.0)
    $penEdge.Dispose()
}

# Assembly icon - M in triangle
New-Icon "$ghDir\MoleHill.png" {
    param($g)
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(50,100,180))
    $pts = @(
        (New-Object System.Drawing.PointF(2, 21)),
        (New-Object System.Drawing.PointF(12, 2)),
        (New-Object System.Drawing.PointF(22, 21))
    )
    $g.FillPolygon($brush, $pts)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 2)
    $g.DrawLine($pen, 7, 18, 7, 10)
    $g.DrawLine($pen, 7, 10, 12, 15)
    $g.DrawLine($pen, 12, 15, 17, 10)
    $g.DrawLine($pen, 17, 10, 17, 18)
    $pen.Dispose()
    $brush.Dispose()
}

# Retaining Wall - stacked stone blocks with post
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
    # Block dividers in middle rows
    $g.DrawLine($pen, 14, 10, 14, 14)
    $g.DrawLine($pen, 11, 15, 11, 19)
    $pen.Dispose()
    $brush.Dispose()
}

# ── 16×16 Rhino panel tab icons ────────────────────────────────────────────

# TabModifiers - stacked horizontal bars
New-Icon "$rhinoDir\TabModifiers.png" {
    param($g)
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(80,80,80))
    $g.FillRectangle($brush, 2, 3, 12, 2)
    $g.FillRectangle($brush, 2, 7, 12, 2)
    $g.FillRectangle($brush, 2, 11, 12, 2)
    $brush.Dispose()
} -size 16

# TabZones - divided rectangle with colored regions
New-Icon "$rhinoDir\TabZones.png" {
    param($g)
    $b1 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(150, 200, 150))
    $b2 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(140, 180, 220))
    $b3 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(220, 180, 140))
    $g.FillRectangle($b1, 1, 1, 6, 14)
    $g.FillRectangle($b2, 8, 1, 7, 7)
    $g.FillRectangle($b3, 8, 9, 7, 6)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(80,80,80), 1)
    $g.DrawRectangle($pen, 1, 1, 14, 14)
    $g.DrawLine($pen, 8, 1, 8, 15)
    $g.DrawLine($pen, 8, 8, 15, 8)
    $pen.Dispose()
    $b1.Dispose(); $b2.Dispose(); $b3.Dispose()
} -size 16

# TabMarkers - pin dot with crosshair
New-Icon "$rhinoDir\TabMarkers.png" {
    param($g)
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(180, 60, 60))
    $g.FillEllipse($brush, 5, 2, 6, 6)
    $brush.Dispose()
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(80,80,80), 1)
    $g.DrawLine($pen, 8, 8, 8, 14)
    $g.DrawLine($pen, 4, 5, 8, 2)
    $g.DrawLine($pen, 12, 5, 8, 2)
    $pen.Dispose()
} -size 16

# TabAnalysis - ascending bar chart
New-Icon "$rhinoDir\TabAnalysis.png" {
    param($g)
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(60, 140, 80))
    $g.FillRectangle($brush, 2, 10, 3, 4)
    $brush.Dispose()
    $brush2 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(80, 160, 60))
    $g.FillRectangle($brush2, 7, 6, 3, 8)
    $brush2.Dispose()
    $brush3 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(100, 190, 60))
    $g.FillRectangle($brush3, 12, 2, 3, 12)
    $brush3.Dispose()
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60,60,60), 1)
    $g.DrawLine($pen, 1, 14, 15, 14)
    $pen.Dispose()
} -size 16

# ── 16×16 Rhino panel modifier type badges ──────────────────────────────────

# ModTriangulate - triangle wireframe, blue
New-Icon "$rhinoDir\ModTriangulate.png" {
    param($g)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(25, 118, 210), 1.5)
    $g.DrawLine($pen, 8, 2, 2, 14)
    $g.DrawLine($pen, 8, 2, 14, 14)
    $g.DrawLine($pen, 2, 14, 14, 14)
    $pen.Dispose()
} -size 16

# ModRemesh - subdivided triangle, green
New-Icon "$rhinoDir\ModRemesh.png" {
    param($g)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(56, 142, 60), 1.2)
    $g.DrawLine($pen, 8, 2, 2, 14)
    $g.DrawLine($pen, 8, 2, 14, 14)
    $g.DrawLine($pen, 2, 14, 14, 14)
    $penThin = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(56, 142, 60), 0.8)
    $g.DrawLine($penThin, 5, 8, 11, 8)
    $g.DrawLine($penThin, 5, 8, 8, 14)
    $g.DrawLine($penThin, 11, 8, 8, 14)
    $pen.Dispose()
    $penThin.Dispose()
} -size 16

# ModSmooth - wavy line, purple
New-Icon "$rhinoDir\ModSmooth.png" {
    param($g)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(123, 31, 162), 1.5)
    $g.DrawBezier($pen, 1.0, 8.0, 5.0, 3.0, 11.0, 13.0, 15.0, 8.0)
    $pen.Dispose()
} -size 16

# ModRetainingWall - block wall side view, deep orange
New-Icon "$rhinoDir\ModRetainingWall.png" {
    param($g)
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(230, 74, 25))
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(180, 50, 10), 1)
    $g.FillRectangle($brush, 2, 3, 12, 3)
    $g.DrawRectangle($pen, 2, 3, 12, 3)
    $g.FillRectangle($brush, 2, 7, 12, 3)
    $g.DrawRectangle($pen, 2, 7, 12, 3)
    $g.FillRectangle($brush, 2, 11, 12, 3)
    $g.DrawRectangle($pen, 2, 11, 12, 3)
    $pen.Dispose()
    $brush.Dispose()
} -size 16

# ModGradePad - flat line over slope, amber
New-Icon "$rhinoDir\ModGradePad.png" {
    param($g)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(245, 124, 0), 1.5)
    $g.DrawLine($pen, 1, 12, 5, 8)
    $g.DrawLine($pen, 11, 8, 15, 12)
    $penFlat = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(245, 124, 0), 2)
    $g.DrawLine($penFlat, 5, 8, 11, 8)
    $pen.Dispose()
    $penFlat.Dispose()
} -size 16

# ModGradePath - curved path, brown
New-Icon "$rhinoDir\ModGradePath.png" {
    param($g)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(93, 64, 55), 1.5)
    $g.DrawBezier($pen, 1.0, 12.0, 5.0, 3.0, 11.0, 13.0, 15.0, 5.0)
    $pen.Dispose()
} -size 16

Write-Host "All icons generated successfully."
