# deploy_truss.ps1 — deploys the freshly-built bridge DLL (with the new truss + roof-layer
# methods) into the Revit 2026 Addins folder. RUN THIS WITH REVIT CLOSED (the loaded DLL is
# locked while Revit runs). Builds first if needed, backs up the current DLL, then copies.
$ErrorActionPreference = 'Stop'
$proj  = 'D:\RevitMCPBridge2026\RevitMCPBridge2026.csproj'
$src   = 'D:\RevitMCPBridge2026\bin\Debug'
$dst   = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2026'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

if (Get-Process -Name 'Revit' -ErrorAction SilentlyContinue) {
    Write-Host 'Revit is still running — close it completely, then re-run this script.' -ForegroundColor Red
    exit 1
}

Write-Host 'Building (Debug)...' -ForegroundColor Cyan
dotnet build $proj -c Debug -v minimal --nologo
if ($LASTEXITCODE -ne 0) { Write-Host 'Build failed — aborting.' -ForegroundColor Red; exit 1 }

if (Test-Path (Join-Path $dst 'RevitMCPBridge2026.dll')) {
    Copy-Item (Join-Path $dst 'RevitMCPBridge2026.dll') (Join-Path $dst "RevitMCPBridge2026.dll.bak-$stamp-pretruss") -Force
    Write-Host "Backed up current DLL -> RevitMCPBridge2026.dll.bak-$stamp-pretruss"
}

Copy-Item (Join-Path $src 'RevitMCPBridge2026.dll') (Join-Path $dst 'RevitMCPBridge2026.dll') -Force
if (Test-Path (Join-Path $src 'RevitMCPBridge2026.deps.json')) {
    Copy-Item (Join-Path $src 'RevitMCPBridge2026.deps.json') (Join-Path $dst 'RevitMCPBridge2026.deps.json') -Force
}

Write-Host "Deployed new bridge to $dst" -ForegroundColor Green
Write-Host 'Next: reopen Revit + your model, start the MCP Bridge server (ribbon -> MCP Bridge -> Start Server),' -ForegroundColor Cyan
Write-Host '      then restart Claude Code so the new createTruss / setRoofLayers tools appear.' -ForegroundColor Cyan
