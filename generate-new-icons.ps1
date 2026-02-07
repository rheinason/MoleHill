Add-Type -AssemblyName System.Drawing

function New-Icon($name, $drawAction) {
    $bmp = New-Object System.Drawing.Bitmap(24, 24)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    & $drawAction $g
    $g.Dispose()
    $path = "src\TopoTIN.Grasshopper\Resources\$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "Created $path"
}

# Grade Path - road/path icon (two parallel lines with dashes)
New-Icon "GradePath" {
    param($g)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(80, 80, 80), 2)
    # Road edges
    $g.DrawLine($pen, 2, 20, 10, 8)
    $g.DrawLine($pen, 10, 8, 22, 4)
    $g.DrawLine($pen, 2, 22, 10, 14)
    $g.DrawLine($pen, 10, 14, 22, 10)
    # Center dashes
    $dashPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(200, 160, 60), 1)
    $dashPen.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
    $g.DrawLine($dashPen, 2, 21, 10, 11)
    $g.DrawLine($dashPen, 10, 11, 22, 7)
    $pen.Dispose()
    $dashPen.Dispose()
}

# Smoothing - wave becoming flat
New-Icon "Smoothing" {
    param($g)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, 130, 180), 2)
    # Jagged line (before)
    $g.DrawLine($pen, 2, 8, 6, 4)
    $g.DrawLine($pen, 6, 4, 10, 10)
    $g.DrawLine($pen, 10, 10, 14, 3)
    $g.DrawLine($pen, 14, 3, 18, 9)
    $g.DrawLine($pen, 18, 9, 22, 5)
    # Smooth line (after)
    $smoothPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(100, 180, 100), 2)
    $g.DrawLine($smoothPen, 2, 18, 8, 16)
    $g.DrawLine($smoothPen, 8, 16, 12, 17)
    $g.DrawLine($smoothPen, 12, 17, 16, 16)
    $g.DrawLine($smoothPen, 16, 16, 22, 17)
    # Arrow
    $arrowPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150, 150, 150), 1)
    $g.DrawLine($arrowPen, 12, 11, 12, 14)
    $g.DrawLine($arrowPen, 10, 13, 12, 15)
    $g.DrawLine($arrowPen, 14, 13, 12, 15)
    $pen.Dispose()
    $smoothPen.Dispose()
    $arrowPen.Dispose()
}

Write-Host "Done!"
