param(
    [Parameter(Mandatory)][string[]]$Font,
    [Parameter(Mandatory)][string]$Output
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$geometry = foreach ($source in $Font) {
    $path = (Resolve-Path -LiteralPath $source).Path
    $face = [Windows.Media.GlyphTypeface]::new([Uri]::new($path))
    if (!$face.CharacterToGlyphMap.ContainsKey([int][char]'G')) { throw 'В выбранном шрифте нет латинской G.' }
    $outline = $face.GetGlyphOutline($face.CharacterToGlyphMap[[int][char]'G'], 100, 100)
    if ($outline.Bounds.IsEmpty) { throw 'Контур G пуст.' }
    $outline.ToString([Globalization.CultureInfo]::InvariantCulture)
}
[IO.File]::WriteAllLines([IO.Path]::GetFullPath($Output), [string[]]$geometry)
