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
    $AssemblyPath = Join-Path $modRoot '1.6\Assemblies\PioneerFramework.dll'
}

$dependencyDirectories = @(
    (Join-Path $modRoot 'Source\PioneerFramework\Libs\RimWorld'),
    (Join-Path $modRoot 'Source\PioneerFramework\Libs\Unity'),
    (Join-Path $modRoot 'Source\PioneerFramework\Libs\0Harmony'),
    (Join-Path $modRoot 'Source\PioneerFramework\packages\Microsoft.Bcl.HashCode.6.0.0\lib\net462'),
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
[void][System.Reflection.Assembly]::LoadFrom(
    (Join-Path $modRoot 'Source\PioneerFramework\Libs\RimWorld\Assembly-CSharp.dll'))

$assembly = [System.Reflection.Assembly]::LoadFrom((Resolve-Path $AssemblyPath).Path)

# ---- Type resolution ----
$spacePartType = $assembly.GetType('PioneerFramework.Combat.SpacePart', $true)
$shipPartDefType = $assembly.GetType('PioneerFramework.ShipPartDef', $true)
$engineType = $assembly.GetType('PioneerFramework.Components.EngineComponentData', $true)
$tankType = $assembly.GetType('PioneerFramework.Components.PropellantTankComponentData', $true)
$cargoType = $assembly.GetType('PioneerFramework.Components.CargoComponentData', $true)
$weaponMountType = $assembly.GetType('PioneerFramework.Components.WeaponMountComponentData', $true)
$roomType = $assembly.GetType('PioneerFramework.Components.RoomComponentData', $true)
$registryType = $assembly.GetType('PioneerFramework.Components.ComponentRegistry', $true)
$statsType = $assembly.GetType('PioneerFramework.Components.ShipStats', $true)

$flagsInstance = [System.Reflection.BindingFlags]'Public,Instance'

# ---- Trigger component static constructors (no StaticConstructorOnStartup in pure reflection) ----
foreach ($compType in @($engineType, $tankType, $cargoType, $weaponMountType, $roomType)) {
    [System.Runtime.CompilerServices.RuntimeHelpers]::RunClassConstructor($compType.TypeHandle)
}

# ---- 1. Registry completeness ----
Assert-True ($null -ne $registryType) 'ComponentRegistry type is missing.'
$registryCount = $registryType.GetProperty('Count', [System.Reflection.BindingFlags]'Public,Static')
Assert-True ($registryCount -ne $null) 'ComponentRegistry.Count is missing.'
Assert-True ([int]$registryCount.GetValue($null, $null) -ge 5) `
    "ComponentRegistry must register at least 5 framework components, got $($registryCount.GetValue($null, $null))."

$createMethod = $registryType.GetMethod('Create', [System.Reflection.BindingFlags]'Public,Static')
foreach ($typeId in @('EngineComponentData', 'PropellantTankComponentData', 'CargoComponentData',
        'WeaponMountComponentData', 'RoomComponentData')) {
    $comp = $createMethod.Invoke($null, @($typeId))
    Assert-True ($null -ne $comp) "ComponentRegistry.Create('$typeId') returned null."
    $typeIdProp = $comp.GetType().GetProperty('TypeId')
    Assert-True ($typeIdProp.GetValue($comp, $null) -eq $typeId) "TypeId mismatch for '$typeId'."
}

# ---- 2. Part-level stats aggregation ----
$def = [Activator]::CreateInstance($shipPartDefType)
$shipPartDefType.GetField('massBase', $flagsInstance).SetValue($def, [single]100.0)

$part = [Activator]::CreateInstance($spacePartType)
Assert-True ($null -ne $part) 'SpacePart creation returned null.'
$spacePartType.GetField('cachedDef', $flagsInstance).SetValue($part, $def)

$componentsField = $spacePartType.GetField('components', $flagsInstance)
# SpacePart ctor already initialized List<PartComponentData>; use it directly
$componentsList = $componentsField.GetValue($part)

$engine = [Activator]::CreateInstance($engineType)
$engineType.GetField('exhaustVelocity', $flagsInstance).SetValue($engine, [single]30000.0)
$engineType.GetField('massFlowRate', $flagsInstance).SetValue($engine, [single]5.0)
$engineType.GetField('dryMass', $flagsInstance).SetValue($engine, [single]500.0)
$componentsList.Add($engine)

$tank = [Activator]::CreateInstance($tankType)
$tankType.GetField('dryMass', $flagsInstance).SetValue($tank, [single]500.0)
$tankType.GetField('wetMass', $flagsInstance).SetValue($tank, [single]5000.0)
$componentsList.Add($tank)

$statsProp = $spacePartType.GetProperty('Stats')
$stats = $statsProp.GetValue($part, $null)

function Get-StatsField {
    param($stats, [string]$field)
    return [double]$statsType.GetField($field).GetValue($stats)
}

# mass = massBase(100) + engine dry(500) + tank wet(5000) = 5600
$expectedMass = 100.0 + 500.0 + 5000.0
Assert-True ([Math]::Abs((Get-StatsField $stats 'mass') - $expectedMass) -lt 0.01) `
    "Stats.mass mismatch: expected $expectedMass, got $(Get-StatsField $stats 'mass')."
# thrust = ve * mdot = 30000 * 5 = 150000
$expectedThrust = 30000.0 * 5.0
Assert-True ([Math]::Abs((Get-StatsField $stats 'thrust') - $expectedThrust) -lt 0.01) `
    "Stats.thrust mismatch: expected $expectedThrust, got $(Get-StatsField $stats 'thrust')."
# propellant = wet - dry = 4500
$expectedPropellant = 5000.0 - 500.0
Assert-True ([Math]::Abs((Get-StatsField $stats 'propellant') - $expectedPropellant) -lt 0.01) `
    "Stats.propellant mismatch: expected $expectedPropellant, got $(Get-StatsField $stats 'propellant')."

# ---- 3. Invalidation boundary: component removal must trigger recalculation ----
$removeMethod = $spacePartType.GetMethod('RemoveComponent', [System.Reflection.BindingFlags]'Public,Instance')
$removed = [bool]$removeMethod.Invoke($part, @($tank))
Assert-True $removed 'RemoveComponent returned false.'
$stats2 = $statsProp.GetValue($part, $null)
# mass = massBase(100) + engine dry(500) = 600
$expectedMass2 = 100.0 + 500.0
Assert-True ([Math]::Abs((Get-StatsField $stats2 'mass') - $expectedMass2) -lt 0.01) `
    "InvalidateStats not effective: expected mass $expectedMass2, got $(Get-StatsField $stats2 'mass')."

# ---- 4. Clone deep-copy (template -> runtime) ----
$cloneMethod = $engineType.GetMethod('Clone')
$clone = $cloneMethod.Invoke($engine, $null)
Assert-True ($null -ne $clone) 'Clone returned null.'
Assert-True (-not [object]::ReferenceEquals($clone, $engine)) 'Clone must return a new instance.'
Assert-True ([Math]::Abs([single]$engineType.GetField('exhaustVelocity', $flagsInstance).GetValue($clone) - 30000.0) -lt 0.01) `
    'Clone did not preserve exhaustVelocity.'

# ---- 5. Geometry enum is pure ----
$shapeType = $assembly.GetType('PioneerFramework.ShipPartShape', $true)
$shapeNames = [System.Enum]::GetNames($shapeType)
Assert-True ($shapeNames.Count -eq 2) "ShipPartShape must be pure geometry (2 members), got $($shapeNames -join ', ')."
Assert-True ($shapeNames -contains 'Cube') 'ShipPartShape.Cube missing.'
Assert-True ($shapeNames -contains 'SphericalCorner') 'ShipPartShape.SphericalCorner missing.'

Write-Output 'Assert-ShipStats assertions passed.'
