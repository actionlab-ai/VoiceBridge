$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "apps/VoiceBridge.Desktop/VoiceBridge.Desktop.csproj"
$out = Join-Path $root "artifacts/publish"

if (Test-Path $out) {
  Remove-Item $out -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $out | Out-Null

dotnet restore $project
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out

Write-Host "Published to $out"
