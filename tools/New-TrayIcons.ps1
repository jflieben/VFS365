<#
    .SYNOPSIS
    Draws the tray icons: the VFS365 cloud with its drive slot as a single-colour glyph, white for a dark taskbar and near-black for a
    light one, each plain, with an amber badge (uploads waiting) and with a red badge (sign-in problem). Writes assets\tray\*.ico
    (16 to 64 px, each size drawn on its own) and assets\tray\preview.png. Runs in Windows PowerShell 5.1 (System.Drawing).
#>
param([String]$OutputFolder = (Join-Path $PSScriptRoot '..\assets\tray'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$themes = @{ dark = [Drawing.Color]::White; light = [Drawing.ColorTranslator]::FromHtml('#1F1F1F') }
$badges = @{ normal = $null; waiting = [Drawing.ColorTranslator]::FromHtml('#F2A33A'); error = [Drawing.ColorTranslator]::FromHtml('#E5484D') }
$sizes = 16, 20, 24, 32, 40, 48, 64

function New-RoundedPath([Single]$x, [Single]$y, [Single]$w, [Single]$h, [Single]$r){
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-Glyph([Int]$size, [Drawing.Color]$color, $badge){
    $bitmap = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([Drawing.Color]::Transparent)
    $s = [Single]$size
    $ink = New-Object Drawing.SolidBrush $color
    $clear = New-Object Drawing.SolidBrush ([Drawing.Color]::Transparent)

    #cloud: the app icon's three lobes on a rounded base, scaled to fill the square and centred
    function Lobe([Single]$x, [Single]$y, [Single]$r){ $g.FillEllipse($ink, $s * ($x - $r), $s * ($y - $r), $s * 2 * $r, $s * 2 * $r) }
    Lobe 0.25 0.585 0.20
    Lobe 0.50 0.435 0.25
    Lobe 0.75 0.56 0.20
    $g.FillPath($ink, (New-RoundedPath ($s * 0.05) ($s * 0.51) ($s * 0.90) ($s * 0.275) ($s * 0.1375)))

    #drive slot cut out of the cloud; at least 1.5 px high so it reads at 16 px
    $slotHeight = [Math]::Max(1.5, $s * 0.105)
    $g.CompositingMode = 'SourceCopy'
    $g.FillPath($clear, (New-RoundedPath ($s * 0.23) ($s * 0.60) ($s * 0.54) $slotHeight ($slotHeight / 2)))

    #status badge in the corner, set off from the cloud by a transparent ring
    if($badge){
        $r = [Math]::Max(2.5, $s * 0.165)
        $cx = $s - $r - [Math]::Max(0.5, $s * 0.02); $cy = $cx
        $ring = $r + [Math]::Max(1, $s * 0.05)
        $g.FillEllipse($clear, $cx - $ring, $cy - $ring, $ring * 2, $ring * 2)
        $g.CompositingMode = 'SourceOver'
        $g.FillEllipse((New-Object Drawing.SolidBrush $badge), $cx - $r, $cy - $r, $r * 2, $r * 2)
    }
    $g.Dispose()
    return $bitmap
}

function Get-Png([Drawing.Bitmap]$bitmap){
    $stream = New-Object IO.MemoryStream
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    return ,$stream.ToArray()
}

function Write-Ico([String]$path, $frames){
    $ico = New-Object IO.MemoryStream
    $writer = New-Object IO.BinaryWriter $ico
    $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach($frame in $frames){
        $writer.Write([Byte]$frame.Size); $writer.Write([Byte]$frame.Size); $writer.Write([Byte]0); $writer.Write([Byte]0)
        $writer.Write([UInt16]1); $writer.Write([UInt16]32)
        $writer.Write([UInt32]$frame.Png.Length); $writer.Write([UInt32]$offset)
        $offset += $frame.Png.Length
    }
    foreach($frame in $frames){ $writer.Write($frame.Png) }
    $writer.Flush()
    [IO.File]::WriteAllBytes($path, $ico.ToArray())
}

New-Item -ItemType Directory -Force -Path $OutputFolder | Out-Null

#preview: each variant at 16, 24 and 32 px on its taskbar colour, magnified 4x without smoothing
$cell = 4 * (16 + 24 + 32 + 24)
$preview = New-Object Drawing.Bitmap ($cell * 3), (4 * 40 * 2)
$pg = [Drawing.Graphics]::FromImage($preview)
$pg.InterpolationMode = 'NearestNeighbor'
$pg.PixelOffsetMode = 'Half'
$row = 0
foreach($theme in 'dark', 'light'){
    $pg.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml($(if($theme -eq 'dark'){ '#202020' }else{ '#F3F3F3' })))), 0, $row * 160, $preview.Width, 160)
    $column = 0
    foreach($state in 'normal', 'waiting', 'error'){
        $frames = foreach($size in $sizes){
            $bitmap = New-Glyph $size $themes[$theme] $badges[$state]
            [PSCustomObject]@{ Size = $size; Png = (Get-Png $bitmap) }
            $bitmap.Dispose()
        }
        Write-Ico (Join-Path $OutputFolder "tray-$theme-$state.ico") $frames
        $x = $column * $cell + 16
        foreach($size in 16, 24, 32){
            $bitmap = New-Glyph $size $themes[$theme] $badges[$state]
            $pg.DrawImage($bitmap, $x, $row * 160 + 16, $size * 4, $size * 4)
            $bitmap.Dispose()
            $x += $size * 4 + 8
        }
        $column++
    }
    $row++
}
$pg.Dispose()
$preview.Save((Join-Path $OutputFolder 'preview.png'), [Drawing.Imaging.ImageFormat]::Png)
Write-Host "Wrote tray-{dark,light}-{normal,waiting,error}.ico ($($sizes -join ', ') px) and preview.png to $OutputFolder"
