param(
    [string]$Version = "12.4.2",
    [string]$DestDir = "$PSScriptRoot\..\third_party\qpdf"
)

$ErrorActionPreference = "Stop"
$url = "https://github.com/qpdf/qpdf/releases/download/v$Version/qpdf-$Version-msvc64.zip"
$archive = Join-Path $env:TEMP "qpdf-$Version-msvc64.zip"

Write-Host "Downloading QPDF ($Version) from $url ..."
New-Item -ItemType Directory -Force -Path $DestDir | Out-Null
Invoke-WebRequest -Uri $url -OutFile $archive
Expand-Archive -Path $archive -DestinationPath $DestDir -Force
Remove-Item $archive -Force
Write-Host "QPDF extracted to $DestDir"
