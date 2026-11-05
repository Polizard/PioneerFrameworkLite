# check-architecture.ps1 -- layer guard for the class reorg (batch 5).
# ASCII-only on purpose: Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/check-architecture.ps1            # report
#   powershell -ExecutionPolicy Bypass -File tools/check-architecture.ps1 -Fail      # exit 1 on regression
#   powershell -ExecutionPolicy Bypass -File tools/check-architecture.ps1 -Update    # rewrite baseline
#
# Semantics:
#   * Presentation/** may only touch physics through PhysicsApi / PhysicsCommands /
#     PhysicsQueries / SpacecraftSnapshot / PartSnapshot / RadarSnapshot.
#   * Any other engine type (SpaceEntityGroup, SpaceEntity, SpacePart, SpaceGameComp,
#     ManeuverPlan, PlannedManeuver, OrbitState, PropulsionData, SpaceMapComp,
#     ThrustAxis, RadarAim) counts as a violation.
#   * Ratchet mode: the committed baseline (tools/architecture-baseline.json) records
#     the current per-file counts. Regression = a file exceeds its baseline, or a new
#     file introduces violations. Goal: baseline goes to zero, then the file is deleted.

param(
    [switch]$Fail,
    [switch]$Update
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$presentation = Join-Path $root 'Source/PioneerFramework/Presentation'
$baselinePath = Join-Path $PSScriptRoot 'architecture-baseline.json'

$types = @('SpaceEntityGroup','SpaceEntity','SpacePart','SpaceGameComp','ManeuverPlan',
           'PlannedManeuver','OrbitState','PropulsionData','SpaceMapComp','ThrustAxis','RadarAim')
$pattern = '\b(' + ($types -join '|') + ')\b'

$actual = @{}
$total = 0
Get-ChildItem $presentation -Recurse -Filter *.cs | ForEach-Object {
    $rel = $_.FullName.Substring($presentation.Length + 1)
    $count = 0
    foreach ($line in [System.IO.File]::ReadAllLines($_.FullName, [System.Text.Encoding]::UTF8)) {
        $trim = $line.TrimStart()
        if ($trim.StartsWith('//')) { continue }          # doc/line comments do not count
        $count += ([regex]::Matches($line, $pattern)).Count
    }
    if ($count -gt 0) { $actual[$rel] = $count; $total += $count }
}

if ($Update) {
    $actual | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $baselinePath -Encoding UTF8
    Write-Host ("baseline written: {0} files, {1} references" -f $actual.Count, $total) -ForegroundColor Green
    exit 0
}

$baseline = @{}
if (Test-Path -LiteralPath $baselinePath) {
    $json = Get-Content -LiteralPath $baselinePath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($p in $json.PSObject.Properties) { $baseline[$p.Name] = [int]$p.Value }
}

Write-Host "=== Architecture check (Presentation -> PhysicsEngine) ===" -ForegroundColor Cyan
Write-Host ("baseline: {0} files / {1} references   |   actual: {2} files / {3} references" -f `
    $baseline.Count, (($baseline.Values | Measure-Object -Sum).Sum), $actual.Count, $total)

$regressions = @()
foreach ($rel in $actual.Keys) {
    $was = 0
    if ($baseline.ContainsKey($rel)) { $was = $baseline[$rel] }
    if ($actual[$rel] -gt $was) { $regressions += ("NEW/UP  {0}: {1} -> {2}" -f $rel, $was, $actual[$rel]) }
}
foreach ($rel in $baseline.Keys) {
    if (-not $actual.ContainsKey($rel)) { Write-Host ("DOWN    {0}: {1} -> 0" -f $rel, $baseline[$rel]) -ForegroundColor Green }
    elseif ($actual[$rel] -lt $baseline[$rel]) { Write-Host ("DOWN    {0}: {1} -> {2}" -f $rel, $baseline[$rel], $actual[$rel]) -ForegroundColor Green }
}

if ($regressions.Count -eq 0) {
    Write-Host "OK: no new Presentation -> physics-core references." -ForegroundColor Green
    exit 0
}

Write-Host "Regressions:" -ForegroundColor Yellow
$regressions | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Red }
Write-Host "Fix: go through PhysicsApi / PhysicsCommands / PhysicsQueries. If the change is intentional, run -Update." -ForegroundColor Yellow
if ($Fail) { exit 1 }
exit 0
