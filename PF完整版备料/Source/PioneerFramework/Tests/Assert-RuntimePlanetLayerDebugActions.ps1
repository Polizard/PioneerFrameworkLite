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

$actionsType = $assembly.GetType(
    'PioneerFramework.RuntimePlanetLayerDebugActions',
    $false)
$bootstrapType = $assembly.GetType(
    'PioneerFramework.RuntimePlanetLayerDebugDefinitionBootstrap',
    $false)

Assert-True ($null -ne $actionsType) `
    'RuntimePlanetLayerDebugActions type is missing.'
Assert-True ($null -ne $bootstrapType) `
    'RuntimePlanetLayerDebugDefinitionBootstrap type is missing.'

$defNameField = $actionsType.GetField(
    'TestLayerDefName',
    [System.Reflection.BindingFlags]'Public,Static')
Assert-True ($null -ne $defNameField) 'TestLayerDefName is missing.'
Assert-True ($defNameField.GetRawConstantValue() -eq 'PF_Debug_RuntimeSurface') `
    'The debug layer must use the fixed PF_Debug_RuntimeSurface DefName.'

$buildSpec = $actionsType.GetMethod(
    'BuildTestSpec',
    [System.Reflection.BindingFlags]'NonPublic,Static')
Assert-True ($null -ne $buildSpec) 'BuildTestSpec is missing.'
$spec = $buildSpec.Invoke($null, @())

Assert-True ($spec.DefName -eq 'PF_Debug_RuntimeSurface') `
    'BuildTestSpec returned the wrong DefName.'
Assert-True ($spec.BackgroundBiomeDefName -eq 'Desert') `
    'The debug layer must use Desert.'
Assert-True ($spec.Subdivisions -eq 4) `
    'The debug layer must use subdivisions=4 for quick tests.'
Assert-True ($spec.WorldGenStepDefNames.Contains('Terrain')) `
    'The debug surface layer must run the Terrain generation step.'
Assert-True (-not $spec.WorldGenStepDefNames.Contains('Tiles')) `
    'The debug surface layer must not create base Tile instances.'

$serviceType = $assembly.GetType(
    'PioneerFramework.RuntimePlanetLayerService',
    $true)
$validateSpec = $serviceType.GetMethod(
    'ValidateSpec',
    [System.Reflection.BindingFlags]'Public,Static')
$validationArguments = @($spec, $null)
$valid = [bool]$validateSpec.Invoke($null, $validationArguments)
Assert-True $valid "The fixed debug spec is invalid: $($validationArguments[1])"

$debugActionAttributeType = $gameAssembly.GetType(
    'LudeonTK.DebugActionAttribute',
    $true)
$allowedGameStatesType = $gameAssembly.GetType(
    'LudeonTK.AllowedGameStates',
    $true)
$playingOnWorld = [Enum]::Parse($allowedGameStatesType, 'PlayingOnWorld')

$expectedActions = @(
    'CreateTestLayer',
    'SelectTestLayer',
    'ValidateTestLayer',
    'RunLifecycleSmokeTest',
    'RemoveTestLayerSafe',
    'RemoveTestLayerForce'
)

foreach ($methodName in $expectedActions) {
    $method = $actionsType.GetMethod(
        $methodName,
        [System.Reflection.BindingFlags]'Public,Static')
    Assert-True ($null -ne $method) "Debug action $methodName is missing."

    $attributes = $method.GetCustomAttributes($debugActionAttributeType, $false)
    Assert-True ($attributes.Count -eq 1) `
        "$methodName must have exactly one DebugAction attribute."
    Assert-True (
        [int]$attributes[0].allowedGameStates -eq [int]$playingOnWorld) `
        "$methodName must be restricted to PlayingOnWorld."
}

$startupAttributeType = $gameAssembly.GetType(
    'Verse.StaticConstructorOnStartup',
    $true)
$startupAttributes = $bootstrapType.GetCustomAttributes(
    $startupAttributeType,
    $false)
Assert-True ($startupAttributes.Count -eq 1) `
    'The debug Def bootstrap must run at startup for save compatibility.'

$sourcePath = Join-Path $modRoot (
    'Source\PioneerFramework\Base\Debug\RuntimePlanetLayerDebugActions.cs')
Assert-True (Test-Path -LiteralPath $sourcePath) `
    'RuntimePlanetLayerDebugActions.cs is missing.'
$source = Get-Content -Raw -Encoding UTF8 -LiteralPath $sourcePath

foreach ($requiredText in @(
    'EnsureDefinition',
    'QueueLongEvent',
    'PrimaryBiome',
    'TilesCount',
    'ReferenceEquals(first, second)',
    'ReferenceEquals(PlanetLayer.Selected, first)',
    'ReferenceEquals(PlanetLayer.Selected, contentLayer)',
    'RuntimePlanetLayerService.CanRemove(',
    'Find.WorldGrid.Surface',
    'WorldObjectMaker.MakeWorldObject',
    'WorldObjectDefOf.DestroyedSettlement',
    'new PlanetTile(0, layer)',
    'marker.Destroyed',
    'allowContentDestruction: false',
    'allowContentDestruction: true'
)) {
    Assert-True ($source.Contains($requiredText)) `
        "Debug actions are missing required coverage: $requiredText"
}

Write-Output 'RuntimePlanetLayerDebugActions assertions passed.'
