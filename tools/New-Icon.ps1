<#
    .SYNOPSIS
    Draws the VFS365 icon (a cloud with a drive on a navy tile, JSolve colours) and writes assets\vfs365.ico (16 to 256 px, PNG frames)
    and assets\vfs365-256.png. Runs in Windows PowerShell 5.1 (System.Drawing).
#>
param([String]$OutputFolder = (Join-Path $PSScriptRoot '..\assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$navy = [Drawing.ColorTranslator]::FromHtml('#003765')
$navyDark = [Drawing.ColorTranslator]::FromHtml('#002845')
$sky = [Drawing.ColorTranslator]::FromHtml('#71c2f2')
$leaf = [Drawing.ColorTranslator]::FromHtml('#408a3f')

function New-RoundedPath([Single]$x, [Single]$y, [Single]$w, [Single]$h, [Single]$r){
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-Frame([Int]$size){
    $bitmap = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([Drawing.Color]::Transparent)
    $s = [Single]$size

    #tile
    $tile = New-RoundedPath 0 0 $s $s ($s * 0.22)
    $gradient = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF 0, $s), $navy, $navyDark)
    $g.FillPath($gradient, $tile)

    #cloud: three lobes on a flat base
    $cloud = New-Object Drawing.SolidBrush $sky
    $g.FillEllipse($cloud, $s * 0.14, $s * 0.36, $s * 0.32, $s * 0.32)
    $g.FillEllipse($cloud, $s * 0.30, $s * 0.20, $s * 0.40, $s * 0.40)
    $g.FillEllipse($cloud, $s * 0.54, $s * 0.34, $s * 0.32, $s * 0.32)
    $g.FillPath($cloud, (New-RoundedPath ($s * 0.14) ($s * 0.46) ($s * 0.72) ($s * 0.22) ($s * 0.11)))

    #drive slot in the cloud, with a leaf-green activity light from 24 px up
    $slot = New-RoundedPath ($s * 0.27) ($s * 0.53) ($s * 0.46) ($s * 0.10) ($s * 0.05)
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::White)), $slot)
    if($size -ge 24){
        $g.FillEllipse((New-Object Drawing.SolidBrush $leaf), $s * 0.60, $s * 0.555, $s * 0.07, $s * 0.07)
    }

    $g.Dispose()
    $stream = New-Object IO.MemoryStream
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return ,$stream.ToArray()
}

New-Item -ItemType Directory -Force -Path $OutputFolder | Out-Null
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = @($sizes | ForEach-Object { ,(New-Frame $_) })

#ICO: header, one 16-byte entry per frame, then the PNG data
$ico = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter $ico
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for($i = 0; $i -lt $sizes.Count; $i++){
    $dimension = if($sizes[$i] -ge 256){ 0 }else{ $sizes[$i] }
    $writer.Write([Byte]$dimension); $writer.Write([Byte]$dimension); $writer.Write([Byte]0); $writer.Write([Byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$frames[$i].Length); $writer.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach($frame in $frames){ $writer.Write($frame) }
$writer.Flush()
[IO.File]::WriteAllBytes((Join-Path $OutputFolder 'vfs365.ico'), $ico.ToArray())
[IO.File]::WriteAllBytes((Join-Path $OutputFolder 'vfs365-256.png'), $frames[-1])
Write-Host "Wrote vfs365.ico ($($sizes -join ', ') px) and vfs365-256.png to $OutputFolder"
