# Deploy script: sync the mod package from this repo to the game's Mods folder.
#
# Usage (run in PowerShell):
#   powershell -ExecutionPolicy Bypass -File tools/Deploy-LiteToGame.ps1                # 1.6 (frozen) - default
#   powershell -ExecutionPolicy Bypass -File tools/Deploy-LiteToGame.ps1 -Version 1.7    # 1.7 mainline
#   powershell -ExecutionPolicy Bypass -File tools/Deploy-LiteToGame.ps1 -DeployDir "<path>"
#
# Version semantics (2026-10-13 tree split):
#   -Version 1.6 : copy the FROZEN package verbatim; NO build.
#                  Gate = tools/check-frozen-1.6.ps1 (hash-pinned). This is the way to
#                  re-deploy the accepted 1.6 baseline for local play.
#   -Version 1.7 : build Release (writes 1.7/Assemblies) then copy the 1.7 package.
#                  Requires 1.7/Defs + 1.7/Assemblies to exist (packaging task P3 of the migration plan).
#
# History note: the 2026-09-19 "build + artifact-freshness" gate was replaced on 2026-10-13 because
# 1.6 is frozen (never rebuilt): 1.6 -> freeze guard; 1.7 -> build-then-copy in one shot.
#
# ASCII-only on purpose (Windows PowerShell 5.1 reads BOM-less files as ANSI).
param(
    [ValidateSet("1.6", "1.7")]
    [string]$Version = "1.6",
    [string]$DeployDir = "C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\PioneerFrameworkLite"
)

$ErrorActionPreference = 'Stop'
$srcRoot = Split-Path -Parent $PSScriptRoot

Write-Host "=== Deploy gate ($Version) ===" -ForegroundColor Cyan

if ($Version -eq "1.6") {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "check-frozen-1.6.ps1")
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Gate red: frozen 1.6 package is NOT intact => refusing to deploy." -ForegroundColor Red
        exit 1
    }
    Write-Host "Gate green: frozen 1.6 package intact." -ForegroundColor Green
    $subDirs = @("1.6\Defs", "1.6\Textures", "1.6\Patches", "1.6\AssetBundles")
    $dllSrc = "1.6\Assemblies\PioneerFrameworkLite.dll"
    $modSubfolder = "1.6"
    $rootDirs = @("About", "Shaders")
}
else {
    $proj = Join-Path $srcRoot 'Source\PioneerFramework\PioneerFramework.csproj'
    $buildLog = & dotnet build $proj -c Release --nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Gate red: Release build failed => refusing to deploy (fix the build first)." -ForegroundColor Red
        $buildLog | Select-Object -Last 12 | ForEach-Object { Write-Host "  $_" }
        exit 1
    }
    foreach ($need in @("1.7\Defs", "1.7\Assemblies\PioneerFrameworkLite.dll")) {
        if (-not (Test-Path -LiteralPath (Join-Path $srcRoot $need))) {
            Write-Host "Gate red: $need missing -- the 1.7 package is not assembled yet (migration plan P3)." -ForegroundColor Red
            exit 1
        }
    }
    Write-Host "Gate green: Release built, 1.7 package present." -ForegroundColor Green
    $subDirs = @("1.7\Defs", "1.7\Textures", "1.7\Patches", "1.7\Shaders", "1.7\AssetBundles")
    $dllSrc = "1.7\Assemblies\PioneerFrameworkLite.dll"
    $modSubfolder = "1.7"
    $rootDirs = @("About")
}

if (-not (Test-Path -LiteralPath $DeployDir)) {
    Write-Host "Deploy directory does not exist, creating: $DeployDir"
    New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null
}

# Sub-packages: clean-then-copy (files deleted in the repo must not linger in the game copy).
foreach ($d in $subDirs) {
    $src = Join-Path $srcRoot $d
    if (-not (Test-Path -LiteralPath $src)) {
        Write-Host "  skip (not present): $d" -ForegroundColor Yellow
        continue
    }
    $leaf = Split-Path $d -Leaf
    $dst = Join-Path $DeployDir (Join-Path $modSubfolder $leaf)
    if (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Split-Path $dst -Parent) | Out-Null
    Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force
    Write-Host "  synced $d"
}
foreach ($d in $rootDirs) {
    $src = Join-Path $srcRoot $d
    if (-not (Test-Path -LiteralPath $src)) { continue }
    $dst = Join-Path $DeployDir $d
    if (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Recurse -Force }
    Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force
    Write-Host "  synced $d"
}

# Root FILES that must ship at the mod root (LoadFolders.xml declares the per-version folders).
foreach ($f in @("LoadFolders.xml")) {
    $src = Join-Path $srcRoot $f
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination $DeployDir -Force
        Write-Host "  synced $f"
    }
}

# Assemblies: only the deliverable DLL (no stale dll/pdb leftovers).
$dllDst = Join-Path $DeployDir (Join-Path $modSubfolder "Assemblies")
if (Test-Path -LiteralPath $dllDst) { Remove-Item -LiteralPath $dllDst -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dllDst | Out-Null
$dllFull = Join-Path $srcRoot $dllSrc
if (Test-Path -LiteralPath $dllFull) {
    Copy-Item -LiteralPath $dllFull -Destination $dllDst
    Write-Host "  deployed $(Split-Path $dllSrc -Leaf)"
}
else {
    Write-Host "  WARNING: deliverable DLL not found ($dllSrc); code will not load." -ForegroundColor Yellow
}

Write-Host "Deploy sync complete: $DeployDir (version $Version)"
