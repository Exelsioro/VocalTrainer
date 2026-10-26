# Vocal Trainer v0.5.1 - Windows PowerShell 5.1 compatible build script.
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -SelfContained
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -SkipTests
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Debug
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -SelfContained -Package
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$SkipTests,
    [switch]$Package,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location -LiteralPath $root
$project = Join-Path $root 'src\VocalTrainer\VocalTrainer.csproj'
$tests = Join-Path $root 'tests\VocalTrainer.AlgorithmTests\VocalTrainer.AlgorithmTests.csproj'
$windowsTests = Join-Path $root 'tests\VocalTrainer.WindowsTests\VocalTrainer.WindowsTests.csproj'
$dist = Join-Path $root 'dist'
$out = Join-Path $dist $Configuration
$artifacts = Join-Path $root 'artifacts'
$stage = Join-Path $artifacts ("staging\$Configuration")
$archive = Join-Path $artifacts ("packages\VocalTrainer-$Configuration-$Runtime.zip")

function Assert-WorkspacePath([string]$path, [string]$expected) {
    $absolute = [IO.Path]::GetFullPath($path)
    if ($absolute -ne [IO.Path]::GetFullPath($expected) -or
        -not $absolute.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected build output path: $absolute"
    }
    if ((Test-Path -LiteralPath $absolute) -and
        ((Get-Item -LiteralPath $absolute -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Build output must not be a link: $absolute"
    }
}

function Run-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE"
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 10 SDK is required: https://dotnet.microsoft.com/download/dotnet/10.0'
}
$version = (dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to determine the .NET SDK version.'
}
if (-not $version.StartsWith('10.')) {
    throw "Expected .NET SDK 10.x; found $version"
}
if (-not (Test-Path -LiteralPath $project)) {
    throw "Project file not found: $project"
}
if (-not (Test-Path -LiteralPath $tests)) {
    throw "Tests project not found: $tests"
}

Write-Host "=== Vocal Trainer v0.5.1 | SDK $version | $Configuration | $Runtime ===" -ForegroundColor Cyan
if (-not $SkipTests) {
    Write-Host '[1/3] Running algorithm tests' -ForegroundColor Cyan
    Run-Dotnet run --project $tests --configuration $Configuration
    Write-Host '[1/3] Checking WPF startup and all tabs' -ForegroundColor Cyan
    Run-Dotnet run --project $windowsTests --configuration $Configuration
} else {
    Write-Host '[1/3] Tests skipped' -ForegroundColor Yellow
}

Write-Host '[2/3] Publishing Windows app' -ForegroundColor Cyan
Assert-WorkspacePath $dist (Join-Path $root 'dist')
Assert-WorkspacePath $artifacts (Join-Path $root 'artifacts')
Assert-WorkspacePath (Join-Path $artifacts 'staging') (Join-Path $root 'artifacts\staging')
Assert-WorkspacePath $out (Join-Path $root "dist\$Configuration")
Assert-WorkspacePath $stage (Join-Path $root "artifacts\staging\$Configuration")
if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$sc = if ($SelfContained) { 'true' } else { 'false' }
Run-Dotnet publish $project --configuration $Configuration --runtime $Runtime --self-contained $sc --output $stage -p:PublishSingleFile=true "-p:EnableCompressionInSingleFile=$sc" -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false

if (-not (Test-Path -LiteralPath (Join-Path $stage 'VocalTrainer.exe'))) {
    throw "Published executable not found in $stage"
}
if (Test-Path -LiteralPath $out) {
    try { Remove-Item -LiteralPath $out -Recurse -Force }
    catch { throw "Cannot replace $out. Close the running app and retry. Published files remain in $stage. $($_.Exception.Message)" }
}
New-Item -ItemType Directory -Path $dist -Force | Out-Null
Move-Item -LiteralPath $stage -Destination $out

Write-Host '[3/3] Keeping only latest Release and Debug in dist' -ForegroundColor Cyan
foreach ($item in Get-ChildItem -LiteralPath $dist -Force) {
    if ($item.PSIsContainer -and $item.Name -in @('Release', 'Debug')) { continue }
    Assert-WorkspacePath $item.FullName (Join-Path $root ("dist\" + $item.Name))
    Remove-Item -LiteralPath $item.FullName -Recurse -Force
}
if ($Package) {
    Assert-WorkspacePath (Join-Path $artifacts 'packages') (Join-Path $root 'artifacts\packages')
    Assert-WorkspacePath $archive (Join-Path $root "artifacts\packages\VocalTrainer-$Configuration-$Runtime.zip")
    New-Item -ItemType Directory -Path (Split-Path -Parent $archive) -Force | Out-Null
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $archive -CompressionLevel Optimal -Force
    Write-Host "Archive: $archive" -ForegroundColor Green
}
$exe = Join-Path $out 'VocalTrainer.exe'
Write-Host "EXE: $exe" -ForegroundColor Green
if (-not $SelfContained) {
    Write-Host 'The .NET 10 Desktop Runtime is required to run this build.' -ForegroundColor Yellow
}
