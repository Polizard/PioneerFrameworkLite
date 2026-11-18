# Deploy the NFexp content package (PFL companion mod) to the game's Mods folder.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/Deploy-NFexpToGame.ps1
#   powershell -ExecutionPolicy Bypass -File tools/Deploy-NFexpToGame.ps1 -DeployDir "<path>"
#
# 2026-11-17: created when the celestial content Defs (systems / bodies / profiles /
# visuals / local-map recipes) moved out of PFL into NFexp. PFL alone no longer
# provides a celestial system, so this package is REQUIRED for world generation.
#
# ASCII-only on purpose (Windows PowerShell 5.1 reads BOM-less files as ANSI).

param(
    [string]$DeployDir = "C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\NFexp"
)

$ErrorActionPreference = 'Stop'
$srcRoot = Split-Path -Parent $PSScriptRoot
$modRoot = Join-Path $srcRoot 'NFexp'

Write-Host "=== Deploy NFexp ===" -ForegroundColor Cyan

foreach ($need in @('About\About.xml', 'LoadFolders.xml', '1.7\Defs')) {
    if (-not (Test-Path -LiteralPath (Join-Path $modRoot $need))) {
        Write-Host "Gate red: NFexp\$need missing -- the content package is not assembled." -ForegroundColor Red
        exit 1
    }
}
Write-Host "Gate green: NFexp package present." -ForegroundColor Green

if (-not (Test-Path -LiteralPath $DeployDir)) {
    Write-Host "Deploy directory does not exist, creating: $DeployDir"
    New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null
}

# Sub-packages: clean-then-copy (files deleted in the repo must not linger in the game copy).
foreach ($d in @('1.7\Defs', '1.7\Patches', '1.7\Languages', '1.7\Textures', '1.7\Assemblies')) {
    $src = Join-Path $modRoot $d
    if (-not (Test-Path -LiteralPath $src)) {
        Write-Host "  skip (not present): $d" -ForegroundColor Yellow
        continue
    }
    $dst = Join-Path $DeployDir $d
    if (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Split-Path $dst -Parent) | Out-Null
    Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force
    Write-Host "  synced $d"
}

foreach ($d in @('About')) {
    $src = Join-Path $modRoot $d
    if (-not (Test-Path -LiteralPath $src)) { continue }
    $dst = Join-Path $DeployDir $d
    if (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Recurse -Force }
    Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force
    Write-Host "  synced $d"
}

Copy-Item -LiteralPath (Join-Path $modRoot 'LoadFolders.xml') -Destination (Join-Path $DeployDir 'LoadFolders.xml') -Force
Write-Host "  synced LoadFolders.xml"
Write-Host "Deploy sync complete: $DeployDir" -ForegroundColor Green
