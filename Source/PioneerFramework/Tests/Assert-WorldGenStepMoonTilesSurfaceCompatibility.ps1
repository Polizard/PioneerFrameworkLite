param(
    [string]$AssemblyPath
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$modRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$gameManagedDirectory = Join-Path $modRoot '..\..\RimWorldWin64_Data\Managed'
if (-not (Test-Path -LiteralPath $gameManagedDirectory)) {
    $gameManagedDirectory = $null
}
if ([string]::IsNullOrWhiteSpace($AssemblyPath)) {
    $AssemblyPath = Join-Path $modRoot '1.6\Assemblies\PioneerFrameworkLite.dll'
}

$dependencyDirectories = @(
    (Join-Path $modRoot 'Source\PioneerFramework\Libs\RimWorld'),
    (Join-Path $modRoot 'Source\PioneerFramework\Libs\Unity'),
    (Join-Path $modRoot 'Source\PioneerFramework\Libs\0Harmony'),
    (Split-Path -Parent $AssemblyPath)
)
if (-not [string]::IsNullOrWhiteSpace($gameManagedDirectory)) {
    $dependencyDirectories += $gameManagedDirectory
}

# Fallback for machines without the game folder: .NET Framework compatible netstandard facade from the SDK
$dotnetSdk = 'C:\Program Files\dotnet\sdk'
if (Test-Path $dotnetSdk) {
    $sdkDir = Get-ChildItem $dotnetSdk -Directory |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if ($null -ne $sdkDir) {
        $netstandardDir = Join-Path $sdkDir.FullName 'Microsoft\Microsoft.NET.Build.Extensions\net461\lib'
        if (Test-Path $netstandardDir) { $dependencyDirectories += $netstandardDir }
    }
}

[System.AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $eventArgs)

    $fileName = ([System.Reflection.AssemblyName]$eventArgs.Name).Name + '.dll'
    foreach ($directory in $script:dependencyDirectories) {
        $candidate = Join-Path $directory $fileName
        if (Test-Path -LiteralPath $candidate) {
            return [System.Reflection.Assembly]::LoadFrom($candidate)
        }
    }

    return $null
})

Get-ChildItem -LiteralPath (
    Join-Path $modRoot 'Source\PioneerFramework\Libs\Unity') -Filter '*.dll' |
    ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
[void][System.Reflection.Assembly]::LoadFrom(
    (Join-Path $modRoot 'Source\PioneerFramework\Libs\0Harmony\0Harmony.dll'))
[void][System.Reflection.Assembly]::LoadFrom(
    (Join-Path $modRoot 'Source\PioneerFramework\Libs\RimWorld\Assembly-CSharp.dll'))
$assembly = [System.Reflection.Assembly]::LoadFrom(
    (Resolve-Path $AssemblyPath).Path)

$layerDefsPath = Join-Path $modRoot (
    '1.6\Defs\PlanetLayerDefs\PlanetLayers_Celestial.xml')
[xml]$layerDefs = Get-Content -Raw -Encoding UTF8 -LiteralPath $layerDefsPath
# Lite 裁剪：火星已删除（跨行星系不做），仅验证月球
foreach ($defName in @('Moon')) {
    $layerDef = $layerDefs.Defs.PlanetLayerDef |
        Where-Object { $_.defName -eq $defName }
    Assert-True ($null -ne $layerDef) "$defName PlanetLayerDef is missing."
    Assert-True ($layerDef.layerType -eq 'SurfaceLayer') `
        "$defName must use SurfaceLayer."
    Assert-True ($layerDef.tileType -eq 'SurfaceTile') `
        "$defName must declare SurfaceTile."
    Assert-True ($layerDef.worldGenSteps.li -contains 'PF_MoonTiles') `
        "$defName must use PF_MoonTiles."
}

$biomeDefsPath = Join-Path $modRoot '1.6\Defs\BiomesDefs\Biome_Planets.xml'
[xml]$biomeDefs = Get-Content -Raw -Encoding UTF8 -LiteralPath $biomeDefsPath
$expectedBiomeTextures = [ordered]@{
    'PF_MoonBiomeWater'    = 'World/Biomes/IceSheetOcean'
    'PF_MoonBiomeBasalt'   = 'World/Biomes/ExtremeDesert'
    'PF_MoonBiomeRegolith' = 'World/Biomes/Desert'
}
foreach ($entry in $expectedBiomeTextures.GetEnumerator()) {
    $biomeDef = $biomeDefs.Defs.BiomeDef |
        Where-Object { $_.defName -eq $entry.Key }
    Assert-True ($null -ne $biomeDef) "$($entry.Key) BiomeDef is missing."
    Assert-True ($biomeDef.texture -eq $entry.Value) "$($entry.Key) must use $($entry.Value)."
}
$activeTextures = @(
    $expectedBiomeTextures['PF_MoonBiomeWater'],
    $expectedBiomeTextures['PF_MoonBiomeBasalt'],
    $expectedBiomeTextures['PF_MoonBiomeRegolith'])
Assert-True ((@($activeTextures | Select-Object -Unique)).Count -eq 3) 'The three active Moon biomes must use distinct placeholder textures.'
$moonLayerDef = $layerDefs.Defs.PlanetLayerDef |
    Where-Object { $_.defName -eq 'Moon' }
Assert-True ($moonLayerDef.backgroundBiome -eq 'PF_MoonBiomeWater') 'Moon backgroundBiome must be PF_MoonBiomeWater.'

$terrainDefsPath = Join-Path $modRoot '1.6\Defs\TerrainDefs\Terrain_Planets.xml'
[xml]$terrainDefs = Get-Content -Raw -Encoding UTF8 -LiteralPath $terrainDefsPath
$moonDirt = $terrainDefs.Defs.TerrainDef |
    Where-Object { $_.defName -eq 'PF_MoonDirt' }
Assert-True ($null -ne $moonDirt) 'PF_MoonDirt TerrainDef is missing.'

foreach ($defName in @(
    'PF_MoonBiomeWater',
    'PF_MoonBiomeBasalt',
    'PF_MoonBiomeRegolith')) {
    $biomeDef = $biomeDefs.Defs.BiomeDef |
        Where-Object { $_.defName -eq $defName }
    $baseTerrains = @($biomeDef.terrainsByFertility.li |
        ForEach-Object { [string]$_.terrain })
    Assert-True ($baseTerrains.Count -eq 1 -and $baseTerrains[0] -eq 'PF_MoonDirt') "$defName must generate PF_MoonDirt as its base terrain."
}

$waterIceBiome = $biomeDefs.Defs.BiomeDef |
    Where-Object { $_.defName -eq 'PF_MoonBiomeWater' }
$waterIcePatchTerrains = @(
    $waterIceBiome.terrainPatchMakers.li.thresholds.li |
        ForEach-Object { [string]$_.terrain })
Assert-True ($waterIcePatchTerrains -contains 'Ice') 'PF_MoonBiomeWater must add Ice terrain patches over moon dirt.'

$basaltBiome = $biomeDefs.Defs.BiomeDef |
    Where-Object { $_.defName -eq 'PF_MoonBiomeBasalt' }
$basaltRockTypes = @($basaltBiome.forceRockTypes.li |
    ForEach-Object { [string]$_.InnerText })
Assert-True ($basaltRockTypes -contains 'Vacstone') 'PF_MoonBiomeBasalt must force Odyssey Vacstone for generated rock masses.'

$workerType = $assembly.GetType(
    'PioneerFramework.WorldGenStep_MoonTiles',
    $true)
$waterIceLatitudeMethod = $workerType.GetMethod(
    'IsWaterIceLatitude',
    [System.Reflection.BindingFlags]'Public,NonPublic,Static,DeclaredOnly')
Assert-True ($null -ne $waterIceLatitudeMethod) 'WorldGenStep_MoonTiles.IsWaterIceLatitude is missing.'
foreach ($case in @(
    @{ Latitude = [float]75; Expected = $true },
    @{ Latitude = [float]-75; Expected = $true },
    @{ Latitude = [float]74.99; Expected = $false },
    @{ Latitude = [float]-74.99; Expected = $false })) {
    $actual = [bool]$waterIceLatitudeMethod.Invoke(
        $null, [object[]]@($case.Latitude))
    Assert-True ($actual -eq $case.Expected) "Latitude $($case.Latitude) produced $actual instead of $($case.Expected)."
}

$moonBiomeCategoryMethod = $workerType.GetMethod(
    'SelectMoonBiomeCategory',
    [System.Reflection.BindingFlags]'Public,NonPublic,Static,DeclaredOnly')
Assert-True ($null -ne $moonBiomeCategoryMethod) 'WorldGenStep_MoonTiles.SelectMoonBiomeCategory is missing.'
foreach ($case in @(
    @{ Latitude = [float]75; SelectBasalt = $false; Expected = 'WaterIce' },
    @{ Latitude = [float]-75; SelectBasalt = $true; Expected = 'WaterIce' },
    @{ Latitude = [float]0; SelectBasalt = $true; Expected = 'Basalt' },
    @{ Latitude = [float]0; SelectBasalt = $false; Expected = 'Regolith' })) {
    $actual = $moonBiomeCategoryMethod.Invoke(
        $null, [object[]]@($case.Latitude, $case.SelectBasalt)).ToString()
    Assert-True ($actual -eq $case.Expected) "Latitude $($case.Latitude) with SelectBasalt=$($case.SelectBasalt) produced $actual instead of $($case.Expected)."
}

$hillinessMethod = $workerType.GetMethod(
    'SelectHillinessForBiomeDefName',
    [System.Reflection.BindingFlags]'Public,NonPublic,Static,DeclaredOnly')
Assert-True ($null -ne $hillinessMethod) 'WorldGenStep_MoonTiles.SelectHillinessForBiomeDefName is missing.'
foreach ($case in @(
    @{ BiomeDefName = 'PF_MoonBiomeBasalt'; Expected = 'SmallHills' },
    @{ BiomeDefName = 'PF_MoonBiomeRegolith'; Expected = 'Flat' },
    @{ BiomeDefName = 'PF_MoonBiomeWater'; Expected = 'Flat' },
    @{ BiomeDefName = 'Desert'; Expected = 'Flat' })) {
    $actual = $hillinessMethod.Invoke(
        $null, [object[]]@($case.BiomeDefName)).ToString()
    Assert-True ($actual -eq $case.Expected) "$($case.BiomeDefName) produced $actual hilliness instead of $($case.Expected)."
}

$moonSurfaceGenStepDefsPath = Join-Path $modRoot (
    '1.6\Defs\MapGeneration\MoonSurfaceMapGeneration.xml')
Assert-True (Test-Path -LiteralPath $moonSurfaceGenStepDefsPath) `
    'Moon surface map-generation Defs are missing.'
[xml]$moonSurfaceGenStepDefs = Get-Content -Raw -Encoding UTF8 `
    -LiteralPath $moonSurfaceGenStepDefsPath
$moonSurfaceTerrainStepDef = $moonSurfaceGenStepDefs.Defs.GenStepDef |
    Where-Object { $_.defName -eq 'PF_MoonSurfaceTerrain' }
Assert-True ($null -ne $moonSurfaceTerrainStepDef) `
    'PF_MoonSurfaceTerrain GenStepDef is missing.'
Assert-True ($moonSurfaceTerrainStepDef.order -eq '230') `
    'PF_MoonSurfaceTerrain must run after the base Terrain GenStep.'
Assert-True (
    $moonSurfaceTerrainStepDef.genStep.Class -eq `
    'PioneerFramework.GenStep_MoonSurfaceTerrain') `
    'PF_MoonSurfaceTerrain must use the moon terrain cleanup GenStep.'

$moonSurfacePatchPath = Join-Path $modRoot (
    '1.6\Patches\MoonSurfaceMapGeneration.xml')
Assert-True (Test-Path -LiteralPath $moonSurfacePatchPath) `
    'Moon surface Base_Player patch is missing.'
[xml]$moonSurfacePatch = Get-Content -Raw -Encoding UTF8 `
    -LiteralPath $moonSurfacePatchPath
$moonSurfacePatchOperation = $moonSurfacePatch.SelectSingleNode(
    '/Patch/Operation[@Class="PatchOperationAdd"]')
Assert-True ($null -ne $moonSurfacePatchOperation) `
    'Moon surface map generation must add a GenStep to Base_Player.'
Assert-True (
    $moonSurfacePatchOperation.xpath -eq
    '/Defs/MapGeneratorDef[defName="Base_Player"]/genSteps') `
    'Moon surface terrain cleanup must target Base_Player genSteps.'
Assert-True (
    @($moonSurfacePatchOperation.value.li) -contains 'PF_MoonSurfaceTerrain') `
    'Moon surface terrain cleanup must be registered in Base_Player.'

$moonSurfaceMapGenerationType = $assembly.GetType(
    'PioneerFramework.MoonSurfaceMapGeneration',
    $false)
Assert-True ($null -ne $moonSurfaceMapGenerationType) `
    'MoonSurfaceMapGeneration is missing.'

$isMoonSurfaceBiomeMethod = $moonSurfaceMapGenerationType.GetMethod(
    'IsMoonSurfaceBiomeDefName',
    [System.Reflection.BindingFlags]'Public,NonPublic,Static,DeclaredOnly')
Assert-True ($null -ne $isMoonSurfaceBiomeMethod) `
    'MoonSurfaceMapGeneration.IsMoonSurfaceBiomeDefName is missing.'
foreach ($case in @(
    @{ BiomeDefName = 'PF_MoonBiomeBasalt'; Expected = $true },
    @{ BiomeDefName = 'PF_MoonBiomeRegolith'; Expected = $true },
    @{ BiomeDefName = 'PF_MoonBiomeWater'; Expected = $true },
    @{ BiomeDefName = 'PF_MoonBiome'; Expected = $false },
    @{ BiomeDefName = 'Desert'; Expected = $false })) {
    $actual = [bool]$isMoonSurfaceBiomeMethod.Invoke(
        $null, [object[]]@($case.BiomeDefName))
    Assert-True ($actual -eq $case.Expected) `
        "$($case.BiomeDefName) moon-surface classification was $actual instead of $($case.Expected)."
}

$mapGeneratorType = [System.AppDomain]::CurrentDomain.GetAssemblies() |
    ForEach-Object { $_.GetType('Verse.MapGenerator', $false) } |
    Where-Object { $null -ne $_ } |
    Select-Object -First 1
Assert-True ($null -ne $mapGeneratorType) `
    'Verse.MapGenerator could not be loaded for Moon surface patch validation.'
$generateContentsIntoMap = $mapGeneratorType.GetMethods(
    [System.Reflection.BindingFlags]'Public,NonPublic,Static') |
    Where-Object {
        $_.Name -eq 'GenerateContentsIntoMap' -and
        $_.GetParameters().Count -eq 4
    } |
    Select-Object -First 1
Assert-True ($null -ne $generateContentsIntoMap) `
    'MapGenerator.GenerateContentsIntoMap was not found.'
$mapGenerationPatchInfo = [HarmonyLib.Harmony]::GetPatchInfo(
    $generateContentsIntoMap)
$mapGenerationPrefixOwners = @(
    $mapGenerationPatchInfo.Prefixes |
        ForEach-Object { $_.owner })
Assert-True (
    $mapGenerationPrefixOwners -contains
    'PioneerFramework.MoonSurfaceMapGeneration') `
    'MoonSurfaceMapGeneration did not register its MapGenerator prefix.'

$skipMoonSurfaceGenStepMethod = $moonSurfaceMapGenerationType.GetMethod(
    'ShouldSkipGenStepForBiomeDefName',
    [System.Reflection.BindingFlags]'Public,NonPublic,Static,DeclaredOnly')
Assert-True ($null -ne $skipMoonSurfaceGenStepMethod) `
    'MoonSurfaceMapGeneration.ShouldSkipGenStepForBiomeDefName is missing.'
foreach ($case in @(
    @{ BiomeDefName = 'PF_MoonBiomeBasalt'; GenStepDefName = 'RockChunks'; Expected = $true },
    @{ BiomeDefName = 'PF_MoonBiomeWater'; GenStepDefName = 'RockChunks'; Expected = $true },
    @{ BiomeDefName = 'PF_MoonBiomeBasalt'; GenStepDefName = 'Snow'; Expected = $true },
    @{ BiomeDefName = 'PF_MoonBiomeRegolith'; GenStepDefName = 'Snow'; Expected = $true },
    @{ BiomeDefName = 'PF_MoonBiomeWater'; GenStepDefName = 'Snow'; Expected = $true },
    @{ BiomeDefName = 'PF_MoonBiomeRegolith'; GenStepDefName = 'ScatterRuinsSimple'; Expected = $true },
    @{ BiomeDefName = 'PF_MoonBiomeBasalt'; GenStepDefName = 'ScatterShrines'; Expected = $true },
    @{ BiomeDefName = 'PF_MoonBiomeWater'; GenStepDefName = 'AncientUtilityBuilding'; Expected = $true },
    @{ BiomeDefName = 'PF_MoonBiomeRegolith'; GenStepDefName = 'MechanoidRemains'; Expected = $true },
    @{ BiomeDefName = 'PF_MoonBiomeWater'; GenStepDefName = 'RocksFromGrid'; Expected = $true },
    @{ BiomeDefName = 'PF_MoonBiomeRegolith'; GenStepDefName = 'RocksFromGrid_NoMinerals'; Expected = $true },
    @{ BiomeDefName = 'PF_MoonBiomeBasalt'; GenStepDefName = 'RocksFromGrid'; Expected = $false },
    @{ BiomeDefName = 'PF_MoonBiomeBasalt'; GenStepDefName = 'ScenParts'; Expected = $false },
    @{ BiomeDefName = 'Desert'; GenStepDefName = 'RockChunks'; Expected = $false },
    @{ BiomeDefName = 'Desert'; GenStepDefName = 'Snow'; Expected = $false })) {
    $actual = [bool]$skipMoonSurfaceGenStepMethod.Invoke(
        $null, [object[]]@($case.BiomeDefName, $case.GenStepDefName))
    Assert-True ($actual -eq $case.Expected) `
        "$($case.BiomeDefName) / $($case.GenStepDefName) skip policy was $actual instead of $($case.Expected)."
}

$replaceMoonSurfaceTerrainMethod = $moonSurfaceMapGenerationType.GetMethod(
    'ShouldReplaceMoonSurfaceTerrainForBiomeDefName',
    [System.Reflection.BindingFlags]'Public,NonPublic,Static,DeclaredOnly')
Assert-True ($null -ne $replaceMoonSurfaceTerrainMethod) `
    'MoonSurfaceMapGeneration.ShouldReplaceMoonSurfaceTerrainForBiomeDefName is missing.'
foreach ($case in @(
    @{ BiomeDefName = 'PF_MoonBiomeRegolith'; TerrainDefName = 'RoughGranite'; IsRock = $true; Expected = $true },
    @{ BiomeDefName = 'PF_MoonBiomeWater'; TerrainDefName = 'RoughSlate'; IsRock = $true; Expected = $true },
    @{ BiomeDefName = 'PF_MoonBiomeRegolith'; TerrainDefName = 'Gravel'; IsRock = $false; Expected = $true },
    @{ BiomeDefName = 'PF_MoonBiomeWater'; TerrainDefName = 'Ice'; IsRock = $false; Expected = $false },
    @{ BiomeDefName = 'PF_MoonBiomeRegolith'; TerrainDefName = 'PF_MoonDirt'; IsRock = $false; Expected = $false },
    @{ BiomeDefName = 'PF_MoonBiomeBasalt'; TerrainDefName = 'Gravel'; IsRock = $false; Expected = $true },
    @{ BiomeDefName = 'PF_MoonBiomeBasalt'; TerrainDefName = 'RoughVacstone'; IsRock = $true; Expected = $false },
    @{ BiomeDefName = 'Desert'; TerrainDefName = 'RoughGranite'; IsRock = $true; Expected = $false })) {
    $actual = [bool]$replaceMoonSurfaceTerrainMethod.Invoke(
        $null,
        [object[]]@(
            $case.BiomeDefName,
            $case.TerrainDefName,
            $case.IsRock))
    Assert-True ($actual -eq $case.Expected) `
        "$($case.BiomeDefName) / $($case.TerrainDefName) terrain replacement policy was $actual instead of $($case.Expected)."
}

$moonSurfaceTerrainStepType = $assembly.GetType(
    'PioneerFramework.GenStep_MoonSurfaceTerrain',
    $false)
Assert-True ($null -ne $moonSurfaceTerrainStepType) `
    'GenStep_MoonSurfaceTerrain is missing.'

$generateTileFor = $workerType.GetMethod(
    'GenerateTileFor',
    [System.Reflection.BindingFlags]'Public,NonPublic,Instance,DeclaredOnly')
Assert-True ($null -ne $generateTileFor) `
    'WorldGenStep_MoonTiles.GenerateTileFor is missing.'

$il = $generateTileFor.GetMethodBody().GetILAsByteArray()
$newObjectOpcode = [byte][System.Reflection.Emit.OpCodes]::Newobj.Value
$constructedTypeName = $null
for ($index = 0; $index -le $il.Length - 5; $index++) {
    if ($il[$index] -ne $newObjectOpcode) {
        continue
    }

    $token = [BitConverter]::ToInt32($il, $index + 1)
    $constructor = $generateTileFor.Module.ResolveMethod($token)
    $constructedTypeName = $constructor.DeclaringType.FullName
    break
}

Assert-True (
    $constructedTypeName -eq 'RimWorld.Planet.SurfaceTile') `
    "PF_MoonTiles constructs $constructedTypeName instead of SurfaceTile."

$sourcePath = Join-Path $modRoot (
    'Source\PioneerFramework\WorldGenStep\WorldGenStep_MoonTiles.cs')
$source = Get-Content -Raw -Encoding UTF8 -LiteralPath $sourcePath
foreach ($requiredText in @(
    'new SurfaceTile(tile)',
    'BiomeDef primaryBiome = SelectBiomeFor(tile);',
    'PrimaryBiome = primaryBiome',
    'tile.LayerDef.defName != "Moon"',
    'Find.WorldGrid.LongLatOf(tile).y',
    'IsWaterIceLatitude(latitude)',
    'SelectMoonBiomeCategory(latitude, Rand.Chance(0.5f))',
    'Rand.Chance(0.5f)',
    'PFDefs.PF_MoonBiomeWater',
    'PFDefs.PF_MoonBiomeBasalt',
    'PFDefs.PF_MoonBiomeRegolith',
    'hilliness = SelectHillinessForBiomeDefName(primaryBiome.defName)',
    'Hilliness.SmallHills',
    'temperature = 0f',
    'rainfall = 0f',
    'swampiness = 0f'
)) {
    Assert-True ($source.Contains($requiredText)) `
        "PF_MoonTiles is missing required SurfaceTile initialization: $requiredText"
}

$moonSurfaceSourcePath = Join-Path $modRoot (
    'Source\PioneerFramework\MapGeneration\MoonSurfaceMapGeneration.cs')
Assert-True (Test-Path -LiteralPath $moonSurfaceSourcePath) `
    'Moon surface map-generation source is missing.'
$moonSurfaceSource = Get-Content -Raw -Encoding UTF8 `
    -LiteralPath $moonSurfaceSourcePath
foreach ($requiredText in @(
    'GenerateContentsIntoMap',
    'GenStep_RockChunks',
    'GenStep_RocksFromGrid',
    'ScatterRuinsSimple',
    'ScatterShrines',
    'ShouldReplaceMoonSurfaceTerrainForBiomeDefName',
    '!MoonSurfaceMapGeneration.IsMoonSurfaceBiomeDefName(',
    '"Gravel"',
    'terrain.IsRock',
    'PF_MoonDirt',
    'SetTerrain')) {
    Assert-True ($moonSurfaceSource.Contains($requiredText)) `
        "Moon surface map generation is missing required behavior: $requiredText"
}

Write-Output 'WorldGenStep_MoonTiles SurfaceTile assertions passed.'
