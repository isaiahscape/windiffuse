param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csproj = Join-Path $root "DiffuseWindows.csproj"
$outDir = Join-Path $root "build"

$dotnet = $null
$sysDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($sysDotnet) {
    $sdks = & $sysDotnet.Source --list-sdks 2>$null
    if ($sdks) {
        $dotnet = $sysDotnet.Source
    }
}
if (-not $dotnet) {
    $candidates = @(
        (Join-Path (Split-Path -Parent $root) "diffuse\tools\dotnet8\dotnet.exe"),
        (Join-Path (Split-Path -Parent $root) "..\tools\dotnet8\dotnet.exe")
    )
    foreach ($cand in $candidates) {
        $resolved = [System.IO.Path]::GetFullPath($cand)
        if (Test-Path $resolved) {
            $dotnet = $resolved
            break
        }
    }
}
if (-not $dotnet) {
    throw "No .NET 8 SDK found. Install .NET 8 SDK or run tools/dotnet8 installer."
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

& $dotnet build $csproj -c $Configuration -p:Platform=x64 -o $outDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

Write-Host "Built $(Join-Path $outDir 'DiffuseWindows.exe')"
