param(
    [string]$Version = "0.8.0",
    [switch]$SelfContained = $true,
    [string]$OutputDir = "$PSScriptRoot\..\dist\win-x64"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path "$PSScriptRoot\.."

Write-Host "=== 1. Ensuring PDFium & QPDF binaries ==="
if (-not (Test-Path "$RepoRoot\third_party\pdfium\bin\pdfium.dll")) {
    & "$PSScriptRoot\fetch-pdfium.ps1"
}
if (-not (Test-Path "$RepoRoot\third_party\qpdf\qpdf-12.4.2-msvc64\bin\qpdf30.dll")) {
    & "$PSScriptRoot\fetch-qpdf.ps1"
}

Write-Host "=== 2. Building Native C++20 Engine (Release x64) ==="
cmake -S "$RepoRoot" -B "$RepoRoot\build" -G "Visual Studio 17 2022" -A x64
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed" }
cmake --build "$RepoRoot\build" --config Release
if ($LASTEXITCODE -ne 0) { throw "CMake build failed" }

Write-Host "=== 3. Publishing PDFVisor.App ($Version, SelfContained=$SelfContained) ==="
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$scFlag = if ($SelfContained) { "true" } else { "false" }
dotnet publish "$RepoRoot\src\desktop\PDFVisor.App\PDFVisor.App.csproj" `
    -c Release -r win-x64 --self-contained $scFlag `
    -p:Version=$Version `
    -o "$OutputDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish PDFVisor.App failed" }

Write-Host "=== 4. Publishing PDFVisor.OcrWorker ==="
$WorkerDir = Join-Path $OutputDir "workers"
dotnet publish "$RepoRoot\src\workers\PDFVisor.OcrWorker\PDFVisor.OcrWorker.csproj" `
    -c Release -r win-x64 --self-contained $scFlag `
    -p:Version=$Version `
    -o "$WorkerDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish PDFVisor.OcrWorker failed" }

Write-Host "=== 5. Ensuring native DLLs in publish root ==="
Copy-Item "$RepoRoot\build\src\engine\Release\*.dll" "$OutputDir\" -Force

Write-Host "Publish complete: $OutputDir"
