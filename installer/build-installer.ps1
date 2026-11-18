param(
    [string]$Version = "1.1.0",
    [string]$Configuration = "Release",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$installerDir = $PSScriptRoot
$winRoot = Split-Path -Parent $installerDir
$csproj = Join-Path $winRoot "DiffuseWindows.csproj"
$assetsDir = Join-Path $winRoot "assets"
$buildRoot = Join-Path $winRoot "build"
$publishDir = Join-Path $buildRoot "publish-win-x64"
$outDir = Join-Path $buildRoot "installer"

# Normalize tag/semver input (e.g. "v1.1.0-beta" -> "1.1.0.0" for MSI ProductVersion).
$cleanVer = $Version.TrimStart('v', 'V')
if ($cleanVer -match '^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?') {
    $msiVer = "{0}.{1}.{2}.{3}" -f $Matches[1], $Matches[2], $Matches[3], $(if ($Matches[4]) { $Matches[4] } else { "0" })
}
elseif ($cleanVer -match '^(\d+)\.(\d+)$') {
    $msiVer = "{0}.{1}.0.0" -f $Matches[1], $Matches[2]
}
else {
    throw "Invalid version format '$Version'. Expected semantic version like 1.1.0."
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
    $localDotnet = [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $winRoot) "..\tools\dotnet8\dotnet.exe"))
    if (Test-Path $localDotnet) {
        $dotnet = $localDotnet
    }
}
if (-not $dotnet) {
    throw "No .NET 8 SDK found."
}

$wix = $null
$sysWix = Get-Command wix -ErrorAction SilentlyContinue
if ($sysWix) {
    $wix = $sysWix.Source
}
if (-not $wix) {
    $localWixDir = [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $winRoot) "..\tools\wix"))
    $localWixExe = Join-Path $localWixDir "wix.exe"
    if (-not (Test-Path $localWixExe)) {
        New-Item -ItemType Directory -Force -Path $localWixDir | Out-Null
        & $dotnet tool install wix --version 6.0.2 --tool-path $localWixDir
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to install WiX Toolset 6.0.2 CLI."
        }
    }
    $wix = $localWixExe
}

$extList = & $wix extension list -g 2>$null | Out-String
if ($extList -notmatch "WixToolset\.UI\.wixext") {
    & $wix extension add -g WixToolset.UI.wixext/6.0.2
    if ($LASTEXITCODE -ne 0) { throw "Failed to add WixToolset.UI.wixext." }
}
if ($extList -notmatch "WixToolset\.Util\.wixext") {
    & $wix extension add -g WixToolset.Util.wixext/6.0.2
    if ($LASTEXITCODE -ne 0) { throw "Failed to add WixToolset.Util.wixext." }
}

if (-not $SkipPublish) {
    if (Test-Path $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    Write-Host "Publishing self-contained DiffuseWindows v$msiVer..."
    & $dotnet publish $csproj `
        -c $Configuration `
        -p:Platform=x64 `
        -r win-x64 `
        --self-contained true `
        -p:WindowsAppSDKSelfContained=true `
        -p:Version=$msiVer `
        -p:FileVersion=$msiVer `
        -p:AssemblyVersion=$msiVer `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
}

# Remove debug symbol files from installer payload to reduce size.
Get-ChildItem -Path $publishDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$msiPath = Join-Path $outDir ("Diffuse-{0}-win-x64.msi" -f $cleanVer)
$zipPath = Join-Path $outDir ("Diffuse-{0}-win-x64-portable.zip" -f $cleanVer)
if (Test-Path $msiPath) { Remove-Item -LiteralPath $msiPath -Force }
if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

Write-Host "Building MSI package $msiPath..."
& $wix build (Join-Path $installerDir "Package.wxs") `
    -ext WixToolset.UI.wixext `
    -ext WixToolset.Util.wixext `
    -arch x64 `
    -d "Version=$msiVer" `
    -d "PublishDir=$publishDir" `
    -d "AssetsDir=$assetsDir" `
    -d "InstallerDir=$installerDir" `
    -o $msiPath
if ($LASTEXITCODE -ne 0) {
    throw "wix build failed with exit code $LASTEXITCODE"
}

Write-Host "Packaging portable zip $zipPath..."
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Created installer: $msiPath"
Write-Host "Created portable zip: $zipPath"
