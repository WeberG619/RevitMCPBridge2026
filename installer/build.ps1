# RevitMCPBridge Installer Build Script
# Run from the installer/ directory: .\build.ps1
#
# Prerequisites:
#   - Inno Setup 6 installed (https://jrsoftware.org/isdl.php)
#   - Both RevitMCPBridge2025 and 2026 built in Release mode

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "=== RevitMCPBridge Installer Build ===" -ForegroundColor Cyan
Write-Host ""

# --- Step 1: Copy build artifacts ---
Write-Host "Copying Revit 2026 files..." -ForegroundColor Yellow
$src2026 = "D:\RevitMCPBridge2026\bin\Release"
$dst2026 = "$scriptDir\files\2026"
Copy-Item "$src2026\RevitMCPBridge2026.dll" "$dst2026\" -Force
Copy-Item "$src2026\appsettings.json" "$dst2026\" -Force
Write-Host "  OK" -ForegroundColor Green

Write-Host "Copying Revit 2025 files..." -ForegroundColor Yellow
$src2025 = "D:\RevitMCPBridge2025\bin\Release"
$dst2025 = "$scriptDir\files\2025"
Copy-Item "$src2025\RevitMCPBridge2025.dll" "$dst2025\" -Force
Copy-Item "$src2025\Newtonsoft.Json.dll" "$dst2025\" -Force
Copy-Item "$src2025\Serilog.dll" "$dst2025\" -Force
Copy-Item "$src2025\Serilog.Sinks.File.dll" "$dst2025\" -Force
# Copy appsettings from 2026 as base (same format, just change pipe name)
$settings = Get-Content "$src2026\appsettings.json" | ConvertFrom-Json
$settings.Pipe.Name = "RevitMCPBridge2025"
$settings.Version.RevitVersion = "2025"
$settings | ConvertTo-Json -Depth 10 | Set-Content "$dst2025\appsettings.json"
Write-Host "  OK" -ForegroundColor Green

# --- Step 2: Check for Inno Setup ---
Write-Host ""
Write-Host "Looking for Inno Setup compiler..." -ForegroundColor Yellow
$iscc = $null
$searchPaths = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe"
)
foreach ($path in $searchPaths) {
    if (Test-Path $path) {
        $iscc = $path
        break
    }
}

if (-not $iscc) {
    Write-Host "  Inno Setup not found!" -ForegroundColor Red
    Write-Host "  Download from: https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  After installing, run this script again." -ForegroundColor Yellow
    exit 1
}
Write-Host "  Found: $iscc" -ForegroundColor Green

# --- Step 3: Check for icon/images (optional) ---
if (-not (Test-Path "$scriptDir\assets\icon.ico")) {
    Write-Host ""
    Write-Host "Warning: No icon.ico found in assets\. Using Inno defaults." -ForegroundColor Yellow
    Write-Host "  To add branding, place icon.ico, wizard.bmp, wizard-small.bmp in assets\" -ForegroundColor Yellow
    # Comment out icon lines in .iss to avoid build error
    $issContent = Get-Content "$scriptDir\RevitMCPBridge.iss" -Raw
    $issContent = $issContent -replace 'SetupIconFile=assets\\icon.ico', ';SetupIconFile=assets\icon.ico'
    $issContent = $issContent -replace 'WizardImageFile=assets\\wizard.bmp', ';WizardImageFile=assets\wizard.bmp'
    $issContent = $issContent -replace 'WizardSmallImageFile=assets\\wizard-small.bmp', ';WizardSmallImageFile=assets\wizard-small.bmp'
    $issContent = $issContent -replace 'UninstallDisplayIcon=\{app\}\\icon.ico', ';UninstallDisplayIcon={app}\icon.ico'
    Set-Content "$scriptDir\RevitMCPBridge.iss" $issContent
}

# --- Step 4: Compile installer ---
Write-Host ""
Write-Host "Compiling installer..." -ForegroundColor Yellow
& $iscc "$scriptDir\RevitMCPBridge.iss"

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "=== BUILD SUCCESSFUL ===" -ForegroundColor Green
    $outputFile = Get-ChildItem "$scriptDir\output\*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Write-Host "Installer: $($outputFile.FullName)" -ForegroundColor Cyan
    Write-Host "Size: $([math]::Round($outputFile.Length / 1MB, 1)) MB" -ForegroundColor Cyan
} else {
    Write-Host ""
    Write-Host "=== BUILD FAILED ===" -ForegroundColor Red
    exit 1
}
