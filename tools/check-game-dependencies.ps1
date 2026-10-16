# Verify the exact bundled game references used by builds and tests.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$libs = Join-Path $root 'Source/PioneerFramework/Libs'
$manifestPath = Join-Path $libs 'dependencies.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'Missing dependency provenance: run tools/update-game-dependencies.ps1' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.gameVersion -notmatch '^1\.7\.') { throw "Expected RimWorld 1.7, found $($manifest.gameVersion)" }
if ($manifest.harmonyVersion -notmatch '^3\.') { throw "Expected Harmony 3, found $($manifest.harmonyVersion)" }
foreach ($entry in (@($manifest.files) + @($manifest.testFiles))) {
    $path = Join-Path $libs $entry.path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing reference: $($entry.path)" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) {
        throw "Reference hash mismatch: $($entry.path)"
    }
}
if (Get-ChildItem -LiteralPath $libs -Recurse -File -Filter 'UnityEngine*.dll') { throw 'Stale UnityEngine references remain' }
if (Test-Path -LiteralPath (Join-Path $libs 'RimWorld/netstandard.dll')) { throw 'Stale netstandard shim remains' }
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'Source/PioneerFramework/PioneerFramework.csproj') -Raw
if ($project.Project.PropertyGroup.TargetFramework -ne 'net10.0') { throw 'Main project must target net10.0' }
Write-Host "Dependencies OK: RimWorld $($manifest.gameVersion), Harmony $($manifest.harmonyVersion), $($manifest.files.Count) game + $(@($manifest.testFiles).Count) test SHA256-verified assemblies"
