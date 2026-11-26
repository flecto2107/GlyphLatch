$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$assets = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/GlyphLatch.Windows/Assets'
$brand = Join-Path $assets 'Brand'

function Save-Icon($source, $target, [switch]$Tile) {
    [xml]$svg = Get-Content -LiteralPath $source -Raw
    $style = $svg.SelectSingleNode("//*[local-name()='style']").InnerText
    $color = [regex]::Match($style, 'fill:\s*(#[0-9a-fA-F]{6})').Groups[1].Value
    $brush = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($color))
    $path = [Windows.Media.Geometry]::Parse('F1 ' + $svg.SelectSingleNode("//*[local-name()='path']").GetAttribute('d'))
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $frames = foreach ($size in $sizes) {
        $visual = [Windows.Media.DrawingVisual]::new()
        $drawing = $visual.RenderOpen()
        try {
            $padding = if ($Tile) { $size * 0.15 } else { $size * 0.055 }
            if ($Tile) {
                $background = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString('#171717'))
                $radius = $size * 0.2
                $drawing.DrawRoundedRectangle($background, $null, [Windows.Rect]::new(0, 0, $size, $size), $radius, $radius)
            }
            $scale = ($size - 2 * $padding) / 32
            $drawing.PushTransform([Windows.Media.MatrixTransform]::new($scale, 0, 0, $scale, $padding, $padding))
            $drawing.DrawGeometry($brush, $null, $path)
            $drawing.Pop()
        }
        finally { $drawing.Close() }
        $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($visual)
        $png = [Windows.Media.Imaging.PngBitmapEncoder]::new()
        $png.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream = [IO.MemoryStream]::new()
        try {
            $png.Save($stream)
            [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
        }
        finally { $stream.Dispose() }
    }
    $output = [IO.BinaryWriter]::new([IO.File]::Create($target))
    try {
        $output.Write([uint16]0)
        $output.Write([uint16]1)
        $output.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $output.Write([byte]($frame.Size % 256))
            $output.Write([byte]($frame.Size % 256))
            $output.Write([uint16]0)
            $output.Write([uint16]1)
            $output.Write([uint16]32)
            $output.Write([uint32]$frame.Bytes.Length)
            $output.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach ($frame in $frames) { $output.Write([byte[]]$frame.Bytes) }
    }
    finally { $output.Dispose() }
}

Save-Icon (Join-Path $brand 'icon_dark.svg') (Join-Path $brand 'icon_dark.ico')
Save-Icon (Join-Path $brand 'icon_light.svg') (Join-Path $brand 'icon_light.ico')
Save-Icon (Join-Path $brand 'icon_light.svg') (Join-Path $assets 'glyphlatch.ico') -Tile
