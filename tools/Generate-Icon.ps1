param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$rootDirectory = Split-Path $PSScriptRoot -Parent
$assetsDirectory = Join-Path $rootDirectory 'TermLume.Windows/Assets'
$iconPath = Join-Path $assetsDirectory 'TermLume.ico'

function New-RoundedRectangle([float]$left, [float]$top, [float]$width, [float]$height, [float]$radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($left, $top, $diameter, $diameter, 180, 90)
    $path.AddArc($left + $width - $diameter, $top, $diameter, $diameter, 270, 90)
    $path.AddArc($left + $width - $diameter, $top + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($left, $top + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

$source = [System.Drawing.Bitmap]::new(1024, 1024)
$graphics = [System.Drawing.Graphics]::FromImage($source)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.ScaleTransform(16, 16)
$surface = New-RoundedRectangle 4 4 56 56 15
$gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new([System.Drawing.Point]::new(4, 4), [System.Drawing.Point]::new(60, 60), [System.Drawing.ColorTranslator]::FromHtml('#8492ff'), [System.Drawing.ColorTranslator]::FromHtml('#7753d5'))
$graphics.FillPath($gradient, $surface)
$outline = New-RoundedRectangle 5 5 54 54 14
$outlinePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(89, 197, 203, 255), 1)
$graphics.DrawPath($outlinePen, $outline)
$promptPen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 5)
$promptPen.StartCap = $promptPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$promptPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
$points = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(18, 22), [System.Drawing.PointF]::new(29, 32), [System.Drawing.PointF]::new(18, 42))
$graphics.DrawLines($promptPen, $points)
$cursorPen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#a9f5db'), 5)
$cursorPen.StartCap = $cursorPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$graphics.DrawLine($cursorPen, 35, 42, 46, 42)
$source.Save((Join-Path $assetsDirectory 'TermLume-mac.png'), [System.Drawing.Imaging.ImageFormat]::Png)

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $target = [System.Drawing.Graphics]::FromImage($bitmap)
    $target.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $target.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $target.DrawImage($source, 0, 0, $size, $size)
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $bitmap.Save((Join-Path $assetsDirectory 'TermLume.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    ,$stream.ToArray()
    $stream.Dispose()
    $target.Dispose()
    $bitmap.Dispose()
}

$file = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($index = 0; $index -lt $sizes.Count; $index++) {
    $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$index].Length)
    $writer.Write([uint32]$offset)
    $offset += $images[$index].Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image) }
$writer.Dispose()
$graphics.Dispose()
$source.Dispose()
$surface.Dispose()
$outline.Dispose()
$gradient.Dispose()
$outlinePen.Dispose()
$promptPen.Dispose()
$cursorPen.Dispose()
Copy-Item -LiteralPath (Join-Path $assetsDirectory 'TermLume.svg') -Destination (Join-Path $rootDirectory 'TermLume.Windows/Web/favicon.svg') -Force
Write-Output "Generated $iconPath with $($sizes.Count) sizes."
