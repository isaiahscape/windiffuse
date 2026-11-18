param(
    [switch]$Rebuild,
    [switch]$Preview,
    [switch]$ShowUi
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$exePath = Join-Path $root "build\DiffuseWindows.exe"

if ($Rebuild -or -not (Test-Path $exePath)) {
    & (Join-Path $root "build.ps1")
}

$argsList = @()
if ($Preview) {
    $argsList += "--preview"
}
if ($ShowUi) {
    $argsList += "--show-ui"
}

Start-Process -FilePath $exePath -ArgumentList $argsList -WorkingDirectory (Split-Path -Parent $exePath)
