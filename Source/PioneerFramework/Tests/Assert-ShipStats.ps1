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
    foreach ($directory in $script:dependencyDirectories) {
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
# Lite 裁剪：SpacePart 已从 Combat 命名空间迁出；ShipPartDef/ShipPartShape/WeaponMountComponentData 已删除；
# Engine/PropellantTank/Cargo 组件已删除（F1 dV 体系前提前置，Lite 首发不用），仅剩 Room 组件
$allTypes = @()
try {
    $allTypes = $assembly.GetTypes()
} catch {
    $allTypes = $_.Exception.Types | Where-Object { $null -ne $_ }
}
$spacePartType = $allTypes | Where-Object { $_.FullName -eq 'PioneerFramework.SpacePart' } | Select-Object -First 1
$roomType = $allTypes | Where-Object { $_.FullName -eq 'PioneerFramework.Components.RoomComponentData' } | Select-Object -First 1
$registryType = $allTypes | Where-Object { $_.FullName -eq 'PioneerFramework.Components.ComponentRegistry' } | Select-Object -First 1
$statsType = $allTypes | Where-Object { $_.FullName -eq 'PioneerFramework.Components.ShipStats' } | Select-Object -First 1

$flagsInstance = [System.Reflection.BindingFlags]'Public,Instance'

# ---- Trigger component static constructors (no StaticConstructorOnStartup in pure reflection) ----
foreach ($compType in @($roomType)) {
    [System.Runtime.CompilerServices.RuntimeHelpers]::RunClassConstructor($compType.TypeHandle)
}

# ---- 1. Registry completeness ----
Assert-True ($null -ne $registryType) 'ComponentRegistry type is missing.'
$registryCount = $registryType.GetProperty('Count', [System.Reflection.BindingFlags]'Public,Static')
Assert-True ($registryCount -ne $null) 'ComponentRegistry.Count is missing.'
Assert-True ([int]$registryCount.GetValue($null, $null) -ge 1) `
    "ComponentRegistry must register at least 1 framework component (only Room in Lite), got $($registryCount.GetValue($null, $null))."

$createMethod = $registryType.GetMethod('Create', [System.Reflection.BindingFlags]'Public,Static')
foreach ($typeId in @('RoomComponentData')) {
    $comp = $createMethod.Invoke($null, @($typeId))
    Assert-True ($null -ne $comp) "ComponentRegistry.Create('$typeId') returned null."
    $typeIdProp = $comp.GetType().GetProperty('TypeId')
    Assert-True ($typeIdProp.GetValue($comp, $null) -eq $typeId) "TypeId mismatch for '$typeId'."
}

# ---- 2. Part-level stats aggregation ----
$part = [Activator]::CreateInstance([type]$spacePartType)
Assert-True ($null -ne $part) 'SpacePart creation returned null.'

$componentsField = $spacePartType.GetField('components', $flagsInstance)
# SpacePart ctor already initialized List<PartComponentData>; use it directly
$componentsList = $componentsField.GetValue($part)

$room = [Activator]::CreateInstance([type]$roomType)
$roomType.GetField('roomTypeDefName', $flagsInstance).SetValue($room, 'PF_RoomModule')
$roomType.GetField('dryMass', $flagsInstance).SetValue($room, [single]400.0)
$componentsList.Add($room)

$statsProp = $spacePartType.GetProperty('Stats')
$stats = $statsProp.GetValue($part, $null)

function Get-StatsField {
    param($stats, [string]$field)
    return [double]$statsType.GetField($field).GetValue($stats)
}

# Lite：唯一组件 Room，质量 = room dryMass(400)
$expectedMass = 400.0
Assert-True ([Math]::Abs((Get-StatsField $stats 'mass') - $expectedMass) -lt 0.01) `
    "Stats.mass mismatch: expected $expectedMass, got $(Get-StatsField $stats 'mass')."

# ---- 3. Invalidation boundary: component removal must trigger recalculation ----
$removeMethod = $spacePartType.GetMethod('RemoveComponent', [System.Reflection.BindingFlags]'Public,Instance')
$removed = [bool]$removeMethod.Invoke($part, @($room))
Assert-True $removed 'RemoveComponent returned false.'
$stats2 = $statsProp.GetValue($part, $null)
# 无组件 → mass 兜底 1kg
$expectedMass2 = 1.0
Assert-True ([Math]::Abs((Get-StatsField $stats2 'mass') - $expectedMass2) -lt 0.01) `
    "InvalidateStats not effective: expected mass $expectedMass2, got $(Get-StatsField $stats2 'mass')."

# ---- 4. Clone deep-copy (template -> runtime) ----
$cloneMethod = $roomType.GetMethod('Clone')
$clone = $cloneMethod.Invoke($room, $null)
Assert-True ($null -ne $clone) 'Clone returned null.'
Assert-True (-not [object]::ReferenceEquals($clone, $room)) 'Clone must return a new instance.'
Assert-True ([string]$roomType.GetField('roomTypeDefName', $flagsInstance).GetValue($clone) -eq 'PF_RoomModule') `
    'Clone did not preserve roomTypeDefName.'

Write-Output 'Assert-ShipStats assertions passed.'
