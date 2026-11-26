param(
    [string]$Dotnet = 'dotnet',
    [string]$InnoCompiler,
    [string]$LocalLoadingGlyphs,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/GlyphLatch.Windows/GlyphLatch.Windows.csproj'
$localBuild = [bool]$LocalLoadingGlyphs
$suffix = if ($localBuild) { '-local' } else { '' }
$publish = Join-Path $root "artifacts/release$suffix-win-x64"
$artifacts = Join-Path $root 'artifacts'
$license = Join-Path $root 'src/GlyphLatch.Windows/Assets/Fonts/OFL-Medeo.txt'
$iconLicense = Join-Path $root 'src/GlyphLatch.Windows/Assets/Lucide/LICENSE-Lucide.txt'

if (!$InnoCompiler) {
    $InnoCompiler = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
}
if (!$InnoCompiler) {
    $locations = @("${env:ProgramFiles(x86)}", $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs'))
    $InnoCompiler = foreach ($location in $locations) {
        if (!$location) { continue }
        foreach ($version in 7, 6) {
            $path = Join-Path $location "Inno Setup $version/ISCC.exe"
            if (Test-Path -LiteralPath $path -PathType Leaf) { $path }
        }
    }
    $InnoCompiler = $InnoCompiler | Select-Object -First 1
}
if (!$InnoCompiler) { throw 'Укажите путь к ISCC.exe в параметре -InnoCompiler.' }

$arguments = @(
    'publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None', '-o', $publish
)
if ($NoRestore) { $arguments += '--no-restore' }
& $Dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Сборка GlyphLatch завершилась с кодом $LASTEXITCODE." }

$exe = Join-Path $publish 'GlyphLatch.exe'
$version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion.Split('+')[0]
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Ожидалась числовая версия из трёх частей. Получено $version." }

$setupScript = Join-Path $root 'installer/GlyphLatch.iss'
$installerArgs = @("/DAppVersion=$version", "/DPublishDir=$publish", "/O$artifacts")
$portableFiles = @($exe, $license, $iconLicense)
if ($localBuild) {
    $source = (Resolve-Path -LiteralPath $LocalLoadingGlyphs).Path
    $sidecar = Join-Path $publish 'LoadingGlyphs.local.txt'
    if ($source -ne $sidecar) { Copy-Item -LiteralPath $source -Destination $sidecar -Force }
    $installerArgs += @('/DLocalLoadingGlyphs', "/FGlyphLatch-Setup-$version-win-x64$suffix")
    $portableFiles += $sidecar
}
& $InnoCompiler @installerArgs $setupScript
if ($LASTEXITCODE -ne 0) { throw "Сборка установщика завершилась с кодом $LASTEXITCODE." }

$setup = Join-Path $artifacts "GlyphLatch-Setup-$version-win-x64$suffix.exe"
$portable = Join-Path $artifacts "GlyphLatch-Portable-$version-win-x64$suffix.zip"
Compress-Archive -LiteralPath $portableFiles -DestinationPath $portable -Force
Get-Item -LiteralPath @($setup, $portable) | Select-Object FullName, Length
