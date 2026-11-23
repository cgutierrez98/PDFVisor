param(
    [string]$Tag = "chromium/8165",
    [string]$DestDir = "$PSScriptRoot\..\third_party\pdfium"
)

$ErrorActionPreference = "Stop"
$url = "https://github.com/bblanchon/pdfium-binaries/releases/download/$Tag/pdfium-win-x64.tgz"
$archive = Join-Path $env:TEMP "pdfium-win-x64.tgz"

Write-Host "Downloading PDFium ($Tag) from $url ..."
New-Item -ItemType Directory -Force -Path $DestDir | Out-Null
Invoke-WebRequest -Uri $url -OutFile $archive
tar -xzf $archive -C $DestDir
Remove-Item $archive -Force
Write-Host "PDFium extracted to $DestDir"
