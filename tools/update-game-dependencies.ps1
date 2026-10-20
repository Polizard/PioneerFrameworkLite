# Refresh compile/test references from an installed RimWorld 1.7 and its Harmony mod.
param(
    [string]$GamePath = (Join-Path $PSScriptRoot '../../..'),
    [string]$HarmonyPath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$GamePath = (Resolve-Path -LiteralPath $GamePath).Path
if (-not $HarmonyPath) {
    $HarmonyPath = Join-Path $GamePath '../../workshop/content/294100/2009463077/1.7/Assemblies/0Harmony.dll'
}
$version = (Get-Content -LiteralPath (Join-Path $GamePath 'Version.txt') -Raw).Trim()
if ($version -notmatch '^1\.7\.') { throw "Expected RimWorld 1.7, found $version" }
$harmony = [Reflection.AssemblyName]::GetAssemblyName((Resolve-Path -LiteralPath $HarmonyPath).Path)
if ($harmony.Version.Major -ne 3) { throw "Expected Harmony 3, found $($harmony.Version)" }
$libs = Join-Path $root 'Source/PioneerFramework/Libs'
$names = @('Assembly-CSharp', 'UnityStubs', 'Waylith', 'Waylith.UnityCompat',
    'Brutal.Core.Numerics', 'Brutal.Core.Logging', 'Brutal.Core.Common', 'Brutal.Core.Strings')
$testNames = @('Microsoft.Extensions.Logging.Abstractions', 'Microsoft.Extensions.ObjectPool',
    'com.rlabrecque.steamworks.net')
# Validate every source before replacing any reference.
foreach ($name in ($names + $testNames)) {
    $source = Join-Path $GamePath "$name.dll"
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing game assembly: $source" }
}
$entries = @()
foreach ($name in $names) {
    $relative = "RimWorld/$name.dll"
    $target = Join-Path $libs $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $GamePath "$name.dll") -Destination $target
    $entries += [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash }
}
$harmonyTarget = Join-Path $libs '0Harmony/0Harmony.dll'
$testEntries = @()
foreach ($name in $testNames) {
    $relative = "TestRuntime/$name.dll"
    $target = Join-Path $libs $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $GamePath "$name.dll") -Destination $target
    $testEntries += [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash }
}
Copy-Item -LiteralPath $HarmonyPath -Destination $harmonyTarget
$entries += [ordered]@{ path = '0Harmony/0Harmony.dll'; sha256 = (Get-FileHash -LiteralPath $harmonyTarget -Algorithm SHA256).Hash }
# Remove only the known obsolete reference files; preserve unrelated local files.
$obsolete = @('RimWorld/netstandard.dll', 'Unity/UnityEngine.dll',
    'Unity/UnityEngine.CoreModule.dll', 'Unity/UnityEngine.AssetBundleModule.dll',
    'Unity/UnityEngine.IMGUIModule.dll', 'Unity/UnityEngine.InputLegacyModule.dll',
    'Unity/UnityEngine.TextRenderingModule.dll', 'Unity/UnityEngine.PhysicsModule.dll')
foreach ($relative in $obsolete) {
    $path = Join-Path $libs $relative
    if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path }
}
[ordered]@{
    gameVersion = $version
    targetFramework = 'net10.0'
    harmonyVersion = $harmony.Version.ToString()
    gameSource = 'Installed RimWorld root'
    harmonySource = 'Harmony Workshop item 2009463077, 1.7/Assemblies/0Harmony.dll'
    files = $entries
    testFiles = $testEntries
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $libs 'dependencies.json') -Encoding UTF8
& (Join-Path $PSScriptRoot 'check-game-dependencies.ps1')
