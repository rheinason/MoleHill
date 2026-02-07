Add-Type -AssemblyName System.Drawing

$dir = "$PSScriptRoot\src\TopoTIN.Grasshopper\Resources"
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

function New-Icon {
    param([string]$path, [scriptblock]$draw)
    $bmp = New-Object System.Drawing.Bitmap(24, 24)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    & $draw $g
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "Created: $path"
}

# TIN Surface - triangle mesh wireframe
New-Icon "$dir\TinSurface.png" {
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
New-Icon "$dir\SlopeAnalysis.png" {
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
New-Icon "$dir\GradePad.png" {
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
New-Icon "$dir\Remesh.png" {
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
New-Icon "$dir\MeshAreas.png" {
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
New-Icon "$dir\MeshCollage.png" {
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

# Assembly icon - T in triangle
New-Icon "$dir\TopoTIN.png" {
    param($g)
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(50,100,180))
    $pts = @(
        (New-Object System.Drawing.PointF(2, 21)),
        (New-Object System.Drawing.PointF(12, 2)),
        (New-Object System.Drawing.PointF(22, 21))
    )
    $g.FillPolygon($brush, $pts)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 2)
    $g.DrawLine($pen, 7, 10, 17, 10)
    $g.DrawLine($pen, 12, 10, 12, 19)
    $pen.Dispose()
    $brush.Dispose()
}

Write-Host "All icons generated successfully."
