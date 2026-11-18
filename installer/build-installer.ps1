param(
    [string]$Version = "",
    [string]$Configuration = "Release",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$installerDir = $PSScriptRoot
$winRoot = Split-Path -Parent $installerDir
$csproj = Join-Path $winRoot "DiffuseWindows.csproj"
$bootstrapCsproj = Join-Path $installerDir "SetupBootstrapper\SetupBootstrapper.csproj"
$buildRoot = Join-Path $winRoot "build"
$publishDir = Join-Path $buildRoot "publish-win-x64"
$bootstrapOutDir = Join-Path $buildRoot "setup-stage"
$outDir = Join-Path $buildRoot "installer"

$gitCmd = Get-Command git -ErrorAction SilentlyContinue
$commitHash = $null
$commitCount = 1
if ($gitCmd) {
    $h = (& $gitCmd.Source -C $winRoot rev-parse --short=7 HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($h)) {
        $commitHash = $h.Trim()
    }
    $c = (& $gitCmd.Source -C $winRoot rev-list --count HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and ($c -match '^\d+$')) {
        $commitCount = [Math]::Max(1, [Math]::Min(65535, [int]$c))
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = if ($commitHash) { $commitHash } else { "dev" }
}

$packageLabel = $Version.Trim() -replace '[^0-9A-Za-z._-]', '-'
$cleanSemver = $packageLabel.TrimStart('v', 'V')

if ($cleanSemver -match '^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?$') {
    $asmVer = "{0}.{1}.{2}.{3}" -f $Matches[1], $Matches[2], $Matches[3], $(if ($Matches[4]) { $Matches[4] } else { "0" })
}
elseif ($cleanSemver -match '^(\d+)\.(\d+)$') {
    $asmVer = "{0}.{1}.0.0" -f $Matches[1], $Matches[2]
}
else {
    $asmVer = "0.1.{0}.0" -f $commitCount
}

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
        (Join-Path (Split-Path -Parent $winRoot) "diffuse\tools\dotnet8\dotnet.exe"),
        (Join-Path (Split-Path -Parent $winRoot) "..\tools\dotnet8\dotnet.exe")
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
    throw "No .NET 8 SDK found."
}

if (-not $SkipPublish) {
    if (Test-Path $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    Write-Host "Publishing self-contained DiffuseWindows ($packageLabel, ver=$asmVer)..."
    & $dotnet publish $csproj `
        -c $Configuration `
        -p:Platform=x64 `
        -r win-x64 `
        --self-contained true `
        -p:WindowsAppSDKSelfContained=true `
        -p:Version=$asmVer `
        -p:FileVersion=$asmVer `
        -p:AssemblyVersion=$asmVer `
        -p:InformationalVersion=$packageLabel `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
}

# Remove debug symbol files from installer payload to reduce size.
Get-ChildItem -Path $publishDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$zipPath = Join-Path $outDir ("Diffuse-{0}-win-x64-portable.zip" -f $packageLabel)
$setupExePath = Join-Path $outDir ("DiffuseSetup-{0}-win-x64.exe" -f $packageLabel)
if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
if (Test-Path $setupExePath) { Remove-Item -LiteralPath $setupExePath -Force }

Write-Host "Packaging portable zip $zipPath..."
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

if (Test-Path $bootstrapOutDir) {
    Remove-Item -LiteralPath $bootstrapOutDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $bootstrapOutDir | Out-Null

Write-Host "Building WinUI 3 single-file installer $setupExePath..."
& $dotnet publish $bootstrapCsproj `
    -c $Configuration `
    -p:PayloadZipPath="$zipPath" `
    -p:Version=$asmVer `
    -p:FileVersion=$asmVer `
    -p:AssemblyVersion=$asmVer `
    -p:InformationalVersion=$packageLabel `
    -o $bootstrapOutDir
if ($LASTEXITCODE -ne 0) {
    throw "SetupBootstrapper publish failed with exit code $LASTEXITCODE"
}

$builtSetup = Join-Path $bootstrapOutDir "DiffuseSetup.exe"
if (-not (Test-Path $builtSetup)) {
    throw "Expected bootstrapper output not found: $builtSetup"
}
Copy-Item -LiteralPath $builtSetup -Destination $setupExePath -Force

Write-Host "Created WinUI 3 installer: $setupExePath"
Write-Host "Created portable zip: $zipPath"
