<#
    .SYNOPSIS
    Draws Explorer's overlay icons for vfs365shell.dll: a round badge in the bottom-left corner of a transparent square (Explorer draws
    overlays over the whole item icon). cached: green with a check, uploading and downloading: blue with an arrow, problem: red with a
    cross, online-only: a blue cloud outline on white. Writes assets\overlay\*.ico (16, 32, 48 and 256 px, each size drawn on its own)
    and assets\overlay\preview.png. Runs in Windows PowerShell 5.1 (System.Drawing).
#>
param([String]$OutputFolder = (Join-Path $PSScriptRoot '..\assets\overlay'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$colors = @{
    cached = [Drawing.ColorTranslator]::FromHtml('#107C10')
    uploading = [Drawing.ColorTranslator]::FromHtml('#0F6CBD')
    downloading = [Drawing.ColorTranslator]::FromHtml('#0F6CBD')
    problem = [Drawing.ColorTranslator]::FromHtml('#C42B1C')
    'online-only' = [Drawing.Color]::White
}
$cloudColor = [Drawing.ColorTranslator]::FromHtml('#0F6CBD')
$sizes = 16, 32, 48, 256
#The badge's share of the square: larger on small icons, where it must stay readable
$share = @{ 16 = 0.625; 32 = 0.5; 48 = 0.46; 256 = 0.36 }

function New-Badge([Int]$size, [String]$state){
    $bitmap = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([Drawing.Color]::Transparent)

    #badge: a white ring sets it off from any icon below
    $d = [Single][Math]::Round($size * $share[$size])
    $ring = [Math]::Max(1, $d * 0.08)
    $x = [Single]0; $y = [Single]($size - $d)
    $g.FillEllipse([Drawing.Brushes]::White, $x, $y, $d, $d)
    $g.FillEllipse((New-Object Drawing.SolidBrush $colors[$state]), $x + $ring, $y + $ring, $d - 2 * $ring, $d - 2 * $ring)
    if($state -eq 'online-only'){
        $edge = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(140, 96, 96, 96)), ([Math]::Max(1, $d * 0.04))
        $g.DrawEllipse($edge, $x + $ring / 2, $y + $ring / 2, $d - $ring, $d - $ring)
    }

    #glyph in the badge's inner square
    $c = $x + $d / 2; $m = $y + $d / 2; $r = ($d - 2 * $ring) / 2
    $width = [Single][Math]::Max(1.2, $d * 0.13)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), $width
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    function P([Double]$u, [Double]$v){ New-Object Drawing.PointF ([Single]($c + $u * $r)), ([Single]($m + $v * $r)) }
    switch($state){
        'cached' { $g.DrawLines($pen, [Drawing.PointF[]]@((P -0.45 0.02), (P -0.12 0.36), (P 0.48 -0.32))) }
        'uploading' {
            $g.DrawLine($pen, (P 0 0.5), (P 0 -0.42))
            $g.DrawLines($pen, [Drawing.PointF[]]@((P -0.4 -0.04), (P 0 -0.46), (P 0.4 -0.04)))
        }
        'downloading' {
            $g.DrawLine($pen, (P 0 -0.5), (P 0 0.42))
            $g.DrawLines($pen, [Drawing.PointF[]]@((P -0.4 0.04), (P 0 0.46), (P 0.4 0.04)))
        }
        'problem' {
            $g.DrawLine($pen, (P -0.36 -0.36), (P 0.36 0.36))
            $g.DrawLine($pen, (P 0.36 -0.36), (P -0.36 0.36))
        }
        'online-only' {
            #cloud outline: three lobes and a base filled in the cloud colour, then the same shapes inset by the line width in white
            $line = [Math]::Max(1, $d * 0.06) / $r
            $ink = New-Object Drawing.SolidBrush $cloudColor
            foreach($pass in 0, 1){
                $brush = if($pass -eq 0){ $ink }else{ [Drawing.Brushes]::White }
                $inset = $pass * $line
                #lobes as centre (u, v) and radius, then the base's left, top, right and bottom, in badge radii
                foreach($lobe in @(-0.38, 0.2, 0.28), @(0.0, -0.08, 0.38), @(0.38, 0.14, 0.26)){
                    $lr = $lobe[2] - $inset
                    $corner = P ($lobe[0] - $lr) ($lobe[1] - $lr)
                    $g.FillEllipse($brush, $corner.X, $corner.Y, [Single](2 * $lr * $r), [Single](2 * $lr * $r))
                }
                $topLeft = P (-0.38) (0.14 + $inset)
                $g.FillRectangle($brush, $topLeft.X, $topLeft.Y, [Single](0.76 * $r), [Single]((0.34 - 2 * $inset) * $r))
            }
            $ink.Dispose()
        }
    }
    $g.Dispose()
    return $bitmap
}

function Get-Png([Drawing.Bitmap]$bitmap){
    $stream = New-Object IO.MemoryStream
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    return ,$stream.ToArray()
}

#Frames as PNG; 256 is written as 0 in the directory, as the ICO format asks
function Write-Ico([String]$path, $frames){
    $ico = New-Object IO.MemoryStream
    $writer = New-Object IO.BinaryWriter $ico
    $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach($frame in $frames){
        $dimension = [Byte]($frame.Size % 256)
        $writer.Write($dimension); $writer.Write($dimension); $writer.Write([Byte]0); $writer.Write([Byte]0)
        $writer.Write([UInt16]1); $writer.Write([UInt16]32)
        $writer.Write([UInt32]$frame.Png.Length); $writer.Write([UInt32]$offset)
        $offset += $frame.Png.Length
    }
    foreach($frame in $frames){ $writer.Write($frame.Png) }
    $writer.Flush()
    [IO.File]::WriteAllBytes($path, $ico.ToArray())
}

New-Item -ItemType Directory -Force -Path $OutputFolder | Out-Null
$states = 'cached', 'uploading', 'downloading', 'problem', 'online-only'

#preview: each overlay at 16, 32 and 48 px (4x, no smoothing) over a folder-coloured square, on light and dark
$cell = 4 * (16 + 32 + 48) + 48
$preview = New-Object Drawing.Bitmap ($cell * $states.Count), (2 * 4 * 56)
$pg = [Drawing.Graphics]::FromImage($preview)
$pg.InterpolationMode = 'NearestNeighbor'
$pg.PixelOffsetMode = 'Half'
$row = 0
foreach($background in '#F3F3F3', '#202020'){
    $top = $row * 4 * 56
    $pg.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml($background))), 0, $top, $preview.Width, 4 * 56)
    $column = 0
    foreach($state in $states){
        $frames = foreach($size in $sizes){
            $bitmap = New-Badge $size $state
            [PSCustomObject]@{ Size = $size; Png = (Get-Png $bitmap) }
            $bitmap.Dispose()
        }
        Write-Ico (Join-Path $OutputFolder "$state.ico") $frames
        $x = $column * $cell + 16
        foreach($size in 16, 32, 48){
            $pg.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#E8B83E'))), $x, $top + 16, $size * 4, $size * 4)
            $bitmap = New-Badge $size $state
            $pg.DrawImage($bitmap, $x, $top + 16, $size * 4, $size * 4)
            $bitmap.Dispose()
            $x += $size * 4 + 8
        }
        $column++
    }
    $row++
}
$pg.Dispose()
$preview.Save((Join-Path $OutputFolder 'preview.png'), [Drawing.Imaging.ImageFormat]::Png)
Write-Host "Wrote $($states -join ', ') .ico ($($sizes -join ', ') px) and preview.png to $OutputFolder"
