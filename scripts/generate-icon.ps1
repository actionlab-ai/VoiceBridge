param(
  [string]$ProjectDir = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ProjectDir)) {
  $ProjectDir = Join-Path (Split-Path -Parent $PSScriptRoot) "apps/VoiceBridge.Desktop"
}

$assetsDir = Join-Path $ProjectDir "Assets"
$iconPath = Join-Path $assetsDir "app.ico"
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null

Add-Type -AssemblyName System.Drawing

$bitmap = New-Object System.Drawing.Bitmap 64, 64
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

$rect = New-Object System.Drawing.Rectangle 4, 4, 56, 56
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
  $rect `
  ([System.Drawing.Color]::FromArgb(33, 132, 255)) `
  ([System.Drawing.Color]::FromArgb(111, 66, 193)) `
  45
$graphics.FillEllipse($brush, $rect)

$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 5
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$graphics.DrawLine($pen, 32, 15, 32, 35)
$graphics.DrawArc($pen, 20, 20, 24, 22, 0, 180)
$graphics.DrawLine($pen, 32, 42, 32, 50)
$graphics.DrawLine($pen, 22, 50, 42, 50)

$hIcon = $bitmap.GetHicon()
try {
  $icon = [System.Drawing.Icon]::FromHandle($hIcon)
  $stream = [System.IO.File]::Open($iconPath, [System.IO.FileMode]::Create)
  try {
    $icon.Save($stream)
  }
  finally {
    $stream.Dispose()
    $icon.Dispose()
  }
}
finally {
  $graphics.Dispose()
  $brush.Dispose()
  $pen.Dispose()
  $bitmap.Dispose()
}

Write-Host "Generated icon: $iconPath"
