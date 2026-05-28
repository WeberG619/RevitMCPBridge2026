$src = "D:\RevitMCPBridge2026\bin\Release"
$dst = "C:\Users\weber\AppData\Roaming\Autodesk\Revit\Addins\2026"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"

foreach ($name in @("RevitMCPBridge2026.dll", "RevitMCPBridge2026.deps.json", "RevitMCPBridge2026.pdb")) {
    $sourcePath = Join-Path $src $name
    $destPath = Join-Path $dst $name

    if (-not (Test-Path $sourcePath)) {
        Write-Host "skipped missing $name"
        continue
    }

    if (Test-Path $destPath) {
        Copy-Item $destPath "$destPath.bak-$stamp" -Force
    }

    Copy-Item $sourcePath $destPath -Force
    Write-Host "deployed $name"
}
