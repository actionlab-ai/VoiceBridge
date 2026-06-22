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

$bitmap = [System.Drawing.Bitmap]::new(64, 64)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$brush = $null
$pen = $null
$stream = $null
$icon = $null

try {
  $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $graphics.Clear([System.Drawing.Color]::Transparent)

  $rect = [System.Drawing.Rectangle]::new(4, 4, 56, 56)
  $startColor = [System.Drawing.Color]::FromArgb(33, 132, 255)
  $endColor = [System.Drawing.Color]::FromArgb(111, 66, 193)
  $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, $startColor, $endColor, [single]45.0)
  $graphics.FillEllipse($brush, $rect)

  $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [single]5.0)
  $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
  $graphics.DrawLine($pen, 32, 15, 32, 35)
  $graphics.DrawArc($pen, 20, 20, 24, 22, 0, 180)
  $graphics.DrawLine($pen, 32, 42, 32, 50)
  $graphics.DrawLine($pen, 22, 50, 42, 50)

  $hIcon = $bitmap.GetHicon()
  $icon = [System.Drawing.Icon]::FromHandle($hIcon)
  $stream = [System.IO.File]::Open($iconPath, [System.IO.FileMode]::Create)
  $icon.Save($stream)
}
finally {
  if ($null -ne $stream) { $stream.Dispose() }
  if ($null -ne $icon) { $icon.Dispose() }
  if ($null -ne $pen) { $pen.Dispose() }
  if ($null -ne $brush) { $brush.Dispose() }
  $graphics.Dispose()
  $bitmap.Dispose()
}

Write-Host "Generated icon: $iconPath"
