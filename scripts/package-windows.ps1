$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "artifacts/publish"
$dist = Join-Path $root "artifacts/dist"
$zip = Join-Path $dist "VoiceBridge-win-x64.zip"

if (!(Test-Path $publish)) {
  & (Join-Path $PSScriptRoot "build-windows.ps1")
}

if (Test-Path $dist) {
  Remove-Item $dist -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zip -Force
Write-Host "Package: $zip"
