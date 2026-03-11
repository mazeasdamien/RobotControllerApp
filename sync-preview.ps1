# sync-preview.ps1
# Instantly copies Assets\preview.html to ALL build output folders.
# Run this after editing preview.html — no full rebuild needed.

$src = "$PSScriptRoot\Assets\preview.html"

if (-not (Test-Path $src)) {
    Write-Host "Source file not found: $src" -ForegroundColor Red
    exit 1
}

$targets = Get-ChildItem -Path "$PSScriptRoot\bin" -Recurse -Filter "Assets" -Directory

$count = 0
foreach ($dir in $targets) {
    $dst = Join-Path $dir.FullName "preview.html"
    if (Test-Path $dst) {
        try {
            Copy-Item $src $dst -Force
            Write-Host "Copied -> $dst" -ForegroundColor Cyan
            $count++
        } catch {
            Write-Host "LOCKED (close the app first): $dst" -ForegroundColor Yellow
        }
    }
}

if ($count -eq 0) {
    Write-Host "No output Assets folders found. Run 'dotnet build -p:Platform=x64' first." -ForegroundColor Yellow
} else {
    Write-Host "`nDone. $count folder(s) updated." -ForegroundColor Green
}
