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
$gameManagedDirectory = (Resolve-Path (
    Join-Path $modRoot '..\..\RimWorldWin64_Data\Managed')).Path
if ([string]::IsNullOrWhiteSpace($AssemblyPath)) {
    $AssemblyPath = Join-Path $modRoot '1.6\Assemblies\PioneerFramework.dll'
}

$dependencyDirectories = @(
    (Join-Path $modRoot 'Source\PioneerFramework\Libs\RimWorld'),
    (Join-Path $modRoot 'Source\PioneerFramework\Libs\Unity'),
    (Join-Path $modRoot 'Source\PioneerFramework\Libs\0Harmony'),
    (Join-Path $modRoot 'Source\PioneerFramework\packages\Microsoft.Bcl.HashCode.6.0.0\lib\net462'),
    $gameManagedDirectory,
    (Split-Path -Parent $AssemblyPath)
)

[System.AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $eventArgs)

    $fileName = ([System.Reflection.AssemblyName]$eventArgs.Name).Name + '.dll'
    foreach ($directory in $dependencyDirectories) {
        $candidate = Join-Path $directory $fileName
        if (Test-Path -LiteralPath $candidate) {
            return [System.Reflection.Assembly]::LoadFrom($candidate)
        }
    }

    return $null
})

Get-ChildItem -LiteralPath (Join-Path $modRoot 'Source\PioneerFramework\Libs\Unity') -Filter '*.dll' |
    ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
[void][System.Reflection.Assembly]::LoadFrom(
    (Join-Path $modRoot 'Source\PioneerFramework\Libs\0Harmony\0Harmony.dll'))
$gameAssembly = [System.Reflection.Assembly]::LoadFrom(
    (Join-Path $modRoot 'Source\PioneerFramework\Libs\RimWorld\Assembly-CSharp.dll'))

$assembly = [System.Reflection.Assembly]::LoadFrom((Resolve-Path $AssemblyPath).Path)
$specType = $assembly.GetType('PioneerFramework.RuntimePlanetLayerSpec', $false)
$serviceType = $assembly.GetType('PioneerFramework.RuntimePlanetLayerService', $false)

Assert-True ($null -ne $specType) 'RuntimePlanetLayerSpec type is missing.'
Assert-True ($null -ne $serviceType) 'RuntimePlanetLayerService type is missing.'

$createSurface = $specType.GetMethod(
    'CreateSurface',
    [System.Reflection.BindingFlags]'Public,Static')
Assert-True ($null -ne $createSurface) 'RuntimePlanetLayerSpec.CreateSurface is missing.'

$spec = $createSurface.Invoke(
    $null,
    @('PF_TestLayer', 'test layer', [single]42.5, 4, 'Desert'))
Assert-True ($spec.DefName -eq 'PF_TestLayer') 'CreateSurface did not preserve DefName.'
Assert-True ($spec.Label -eq 'test layer') 'CreateSurface did not preserve Label.'
Assert-True ([Math]::Abs($spec.Radius - [single]42.5) -lt [single]0.001) `
    'CreateSurface did not preserve Radius.'
Assert-True ($spec.Subdivisions -eq 4) 'CreateSurface did not preserve Subdivisions.'
Assert-True ($spec.BackgroundBiomeDefName -eq 'Desert') 'CreateSurface did not preserve the biome.'
Assert-True ($spec.WorldGenStepDefNames.Contains('Terrain')) `
    'Surface specs must use the Terrain step to construct SurfaceTile instances.'
Assert-True (-not $spec.WorldGenStepDefNames.Contains('Tiles')) `
    'Surface specs must not use the generic Tiles step.'
Assert-True ($spec.WorldDrawLayerTypes.Count -gt 0) 'Surface specs must define draw layers.'
Assert-True ($spec.WorldTabTypes.Count -gt 0) 'Surface specs must define world tabs.'

function Get-ConstructedTileTypeName {
    param([string]$WorldGenStepTypeName)

    $worldGenStepType = $gameAssembly.GetType($WorldGenStepTypeName, $true)
    $generateTileFor = $worldGenStepType.GetMethod(
        'GenerateTileFor',
        [System.Reflection.BindingFlags]'Public,NonPublic,Instance,DeclaredOnly')
    $il = $generateTileFor.GetMethodBody().GetILAsByteArray()
    $newObjectOpcode = [byte][System.Reflection.Emit.OpCodes]::Newobj.Value

    for ($index = 0; $index -le $il.Length - 5; $index++) {
        if ($il[$index] -ne $newObjectOpcode) {
            continue
        }

        $token = [BitConverter]::ToInt32($il, $index + 1)
        $constructor = $generateTileFor.Module.ResolveMethod($token)
        return $constructor.DeclaringType.FullName
    }

    throw "$WorldGenStepTypeName.GenerateTileFor does not construct a tile."
}

Assert-True (
    (Get-ConstructedTileTypeName 'RimWorld.Planet.WorldGenStep_Tiles') -eq
        'RimWorld.Planet.Tile') `
    'The vanilla Tiles step assumption changed.'
Assert-True (
    (Get-ConstructedTileTypeName 'RimWorld.Planet.WorldGenStep_Terrain') -eq
        'RimWorld.Planet.SurfaceTile') `
    'The vanilla Terrain step must construct SurfaceTile.'

$validate = $serviceType.GetMethod(
    'ValidateSpec',
    [System.Reflection.BindingFlags]'Public,Static')
Assert-True ($null -ne $validate) 'RuntimePlanetLayerService.ValidateSpec is missing.'

$validArguments = @($spec, $null)
$validResult = [bool]$validate.Invoke($null, $validArguments)
Assert-True $validResult "A valid surface spec was rejected: $($validArguments[1])"

$invalidSpec = $createSurface.Invoke(
    $null,
    @('', 'invalid layer', [single]42.5, 4, 'Desert'))
$invalidArguments = @($invalidSpec, $null)
$invalidResult = [bool]$validate.Invoke($null, $invalidArguments)
Assert-True (-not $invalidResult) 'A blank DefName must be rejected.'
Assert-True (-not [string]::IsNullOrWhiteSpace([string]$invalidArguments[1])) `
    'Rejected specs must return an error message.'

foreach ($methodName in @('EnsureDefinition', 'FindByDefName', 'Create', 'CanRemove', 'TryRemove')) {
    $method = $serviceType.GetMethods(
        [System.Reflection.BindingFlags]'Public,Static') |
        Where-Object { $_.Name -eq $methodName } |
        Select-Object -First 1
    Assert-True ($null -ne $method) "RuntimePlanetLayerService.$methodName is missing."
}

$preservingGeneration = $serviceType.GetMethod(
    'RunWorldGenerationPreservingRandState',
    [System.Reflection.BindingFlags]'NonPublic,Static')
Assert-True ($null -ne $preservingGeneration) `
    'Runtime generation must preserve the active Rand state.'

$destroyLayerWorldObjects = $serviceType.GetMethod(
    'DestroyLayerWorldObjects',
    [System.Reflection.BindingFlags]'NonPublic,Static')
Assert-True ($null -ne $destroyLayerWorldObjects) `
    'Forced removal must destroy world objects while their layer is registered.'

$sourcePath = Join-Path $modRoot 'Source\PioneerFramework\Base\Game\RuntimePlanetLayerService.cs'
Assert-True (Test-Path -LiteralPath $sourcePath) 'RuntimePlanetLayerService.cs is missing.'
$source = Get-Content -Raw -Encoding UTF8 -LiteralPath $sourcePath
Assert-True ($source.Contains('Rand.PushState();')) `
    'Runtime generation must push Rand state before vanilla world generation.'
Assert-True ($source.Contains('Rand.PopState();')) `
    'Runtime generation must restore Rand state after vanilla world generation.'
Assert-True ($source.Contains(
        'RunWorldGenerationPreservingRandState(layer, seedPart);')) `
    'Create must use the Rand-preserving generation wrapper.'
Assert-True ($source.Contains('AllWorldObjectsOnLayer')) `
    'Removal must inspect world objects before deleting a layer.'
Assert-True ($source.Contains('IsRootSurface')) `
    'Removal must reject the root surface layer.'
Assert-True ($source.Contains('RemovePlanetLayer')) `
    'Removal must use WorldGrid.RemovePlanetLayer.'
Assert-True ($source.Contains('catch')) `
    'Creation must roll back a registered layer when generation fails.'

$tryRemoveStart = $source.IndexOf('public static bool TryRemove(')
Assert-True ($tryRemoveStart -ge 0) 'TryRemove source was not found.'
$tryRemoveSource = $source.Substring($tryRemoveStart)
$destroyWorldObjectsIndex =
    $tryRemoveSource.IndexOf('DestroyLayerWorldObjects(layer);')
$removeLayerIndex =
    $tryRemoveSource.IndexOf('worldGrid.RemovePlanetLayer(layer);')
Assert-True (
    $destroyWorldObjectsIndex -ge 0 -and
    $removeLayerIndex -gt $destroyWorldObjectsIndex) `
    'World objects must be destroyed before WorldGrid unregisters their layer.'

Write-Output 'RuntimePlanetLayerService assertions passed.'
