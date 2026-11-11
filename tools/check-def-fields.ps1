# Guard for a real breakage (2026-09-29): an S1 rename of a C# field silently broke the Def XML binding.
#
# WHAT HAPPENED: `MissileDef.mainAxisLocal` -> `mainAxisShip` (a pure rename; build 0 errors, gate 9/9 green),
# but `1.6/Defs/Combat/MissileDefs.xml` still said `<mainAxisLocal>`. RimWorld's loader then could NOT bind the
# field and it stayed at its default (0,0,0). The only red text was in the game log:
#   `XML error: <mainAxisLocal>(0, 0, -1) doesn't correspond to any field in type MissileDef`
# and `tools/validate_config.py` had reported "ok" => the gate had NO judge for "C# field name <-> Def element".
#
# WHY a blacklist (and not "every element must be a C# field"): that general form was tried first and produced
# hundreds of false positives -- RimWorld's own `BiomeDef`/`ThingDef` fields (`animalDensity`, `allowRoads`, ...)
# are legitimately not ours. A blacklist of names WE renamed is decisive, has zero false positives, and catches
# exactly this failure class.
#
# ⚠ MAINTENANCE: whenever an S1 rename touches a name that a Def XML element or a Patch could reference,
#    ADD THE OLD NAME HERE. (The new name must NOT be listed -- it is the one the XML should now say.)
#
# ASCII-only on purpose (Windows PowerShell 5.1 reads BOM-less files as ANSI).
#
# Usage (from repo root):
#   powershell -ExecutionPolicy Bypass -File tools/check-def-fields.ps1
# Exit code: 0 = clean / 1 = a renamed-away name still appears in a version package XML
# (1.6/ or 1.7/; printed with file:line).

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Old names retired by S1 (2026-09-28/29). Add a line whenever another one is renamed.
$retired = @(
    # --- 批6 S1：字段/属性 ---
    'mainAxisLocal', 'plannedWorldDv', 'burnArcDv', 'burnDirection',
    'localPosition', 'localVelocity', 'localBoundsSize',
    'aimOffsetLocal', 'landingLocalOffset', 'preciseAimOffsetLocal', 'PreciseAimOffsetLocal',
    'MainAxisLocalNormalized',
    # --- 批6 S1：局部/形参（若将来被 XML/Patch 引用会在这里被抓住）---
    'worldDv', 'worldDir', 'vCircWorld', 'exitWorld', 'entityWorldPos', 'mainAxisWorld',
    'nodeLocalPos', 'nodeLocalVel', 'postBurnNodeLocalVel', 'departurePosWorld', 'departureVelWorld',
    'centerWorld', 'r1World', 'v1World', 'hitLocal', 'hitLocalMeters', 'entryLocal', 'entryLocalMeters',
    'muzzleLocal', 'dirLocal', 'exitLocal', 'targetLocal',
    # --- 2026-11-03 LatitudeBands retirement (moon moved to Parameterized + per-biome hilliness) ---
    # These names no longer have a matching C# field/enum value; a leftover XML element would silently keep a default.
    'LatitudeBands', 'polarBiome', 'primaryBiome', 'secondaryBiome',
    'polarLatitude', 'secondaryChance', 'polarHilliness', 'primaryHilliness', 'secondaryHilliness'
)

# Scan every version package folder that exists (1.6 = frozen, 1.7 = mainline; 2026-10-13 tree split).
$versionDirs = @('1.6', '1.7') | ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path -LiteralPath $_ }
$xmlFiles = $versionDirs | ForEach-Object { Get-ChildItem -Path $_ -Recurse -Filter *.xml -File }
# NOTE: names must be REGEX-ESCAPED and word-bounded. Writing "[name]" in a .NET regex means a CHARACTER CLASS
# (any single char from the set) -- that produced a flood of false positives on the first attempt.
$retiredRe = @{}
foreach ($n in $retired) {
    $retiredRe[$n] = New-Object System.Text.RegularExpressions.Regex(
        ('\b{0}\b' -f [System.Text.RegularExpressions.Regex]::Escape($n)))
}
$hits = 0
# Celestial scalar fields are retired only on the active line. The frozen 1.6 schema remains intact.
$celestialRetired = @('bodyId', 'bodyType', 'radius', 'gravitationalParameter', 'hasSurface',
    'atmosphereThickness', 'atmosphereRho0', 'atmosphereScaleHeight', 'atmosphereRestoreScaleHeights',
    'atmosphereColor', 'baseSphereColor', 'dayLengthTicks', 'planetLayerDefName', 'orbitRadius',
    'orbitPeriod', 'orbitNormal', 'initialPhase', 'parentBodyDefName', 'moonBodies')
$activeDefs = Join-Path $root '1.7/Defs'
foreach ($file in Get-ChildItem -LiteralPath $activeDefs -Recurse -Filter *.xml -File) {
    [xml]$document = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
    foreach ($definition in $document.SelectNodes('/Defs/PioneerFramework.CelestialBodyDef')) {
        foreach ($field in $celestialRetired) {
            if ($definition.SelectSingleNode($field)) {
                $hits++
                Write-Host ("{0}: CelestialBodyDef still references retired scalar '{1}'" -f $file.FullName.Substring($root.Length + 1), $field)
            }
        }
    }
}
# The old standalone map step is retired only in the active package.
$activePrefix = (Join-Path $root '1.7') + [IO.Path]::DirectorySeparatorChar
foreach ($f in $xmlFiles) {
    $activeLine = $f.FullName.StartsWith($activePrefix, [StringComparison]::OrdinalIgnoreCase)
    $lines = [System.IO.File]::ReadAllLines($f.FullName, [System.Text.Encoding]::UTF8)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($activeLine -and $lines[$i] -cmatch '\b(PF_MoonSurfaceTerrain|GenStep_MoonSurfaceTerrain)\b') {
            $hits++
            Write-Host ("{0}({1}): retired standalone moon terrain step" -f $f.FullName.Substring($root.Length + 1), ($i + 1))
        }
        foreach ($name in $retired) {
            if ($retiredRe[$name].IsMatch($lines[$i])) {
                $hits++
                Write-Host ("{0}({1}): still references retired name '{2}' -> rename it in the XML too" -f `
                    $f.FullName.Substring($root.Length + 1), ($i + 1), $name)
            }
        }
    }
}

if ($hits -eq 0) {
    Write-Host ("Def/Patch XML clean: no retired (S1-renamed) name appears in {0} XML files under version folders (1.6/ + 1.7/)." -f $xmlFiles.Count)
    exit 0
} else {
    Write-Host ("Def/Patch XML FAILED: {0} occurrence(s) of a retired name (XML was left behind by a rename)." -f $hits)
    exit 1
}
