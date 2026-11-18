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

function Assert-Contains {
    param(
        [string]$Text,
        [string]$Expected,
        [string]$Message
    )

    Assert-True ($Text.Contains($Expected)) $Message
}

function Assert-NotContains {
    param(
        [string]$Text,
        [string]$Unexpected,
        [string]$Message
    )

    Assert-True (-not $Text.Contains($Unexpected)) $Message
}

$modRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$gameManagedDirectory = (Resolve-Path (
    Join-Path $modRoot '..\..\RimWorldWin64_Data\Managed')).Path
if ([string]::IsNullOrWhiteSpace($AssemblyPath)) {
    $AssemblyPath = Join-Path $modRoot (
        '1.6\Assemblies\PioneerFramework.dll')
}

$dependencyDirectories = @(
    (Join-Path $modRoot 'Source\PioneerFramework\Libs\RimWorld'),
    (Join-Path $modRoot 'Source\PioneerFramework\Libs\Unity'),
    (Join-Path $modRoot 'Source\PioneerFramework\Libs\0Harmony'),
    $gameManagedDirectory,
    (Split-Path -Parent $AssemblyPath)
)

[System.AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $eventArgs)

    $fileName =
        ([System.Reflection.AssemblyName]$eventArgs.Name).Name + '.dll'
    foreach ($directory in $dependencyDirectories) {
        $candidate = Join-Path $directory $fileName
        if (Test-Path -LiteralPath $candidate) {
            return [System.Reflection.Assembly]::LoadFrom($candidate)
        }
    }

    return $null
})

Get-ChildItem -LiteralPath (
    Join-Path $modRoot 'Source\PioneerFramework\Libs\Unity') `
    -Filter '*.dll' |
    ForEach-Object {
        [void][System.Reflection.Assembly]::LoadFrom($_.FullName)
    }
[void][System.Reflection.Assembly]::LoadFrom(
    (Join-Path $modRoot (
        'Source\PioneerFramework\Libs\0Harmony\0Harmony.dll')))
[void][System.Reflection.Assembly]::LoadFrom(
    (Join-Path $modRoot (
        'Source\PioneerFramework\Libs\RimWorld\Assembly-CSharp.dll')))

$assembly = [System.Reflection.Assembly]::LoadFrom(
    (Resolve-Path $AssemblyPath).Path)

$settingsType = $assembly.GetType(
    'PioneerFramework.PFView.PFViewOutlineSettings', $false)
Assert-True ($null -ne $settingsType) `
    'PFViewOutlineSettings type is missing.'

$defaultSettings = $settingsType.GetProperty(
    'Default',
    [System.Reflection.BindingFlags]'Public,Static').GetValue($null)
Assert-True ($defaultSettings.Enabled) `
    'Default outline settings must be enabled.'
Assert-True ($defaultSettings.WidthPixels -eq 2) `
    'Default outline width must be 2 pixels.'
Assert-True (
    [Math]::Abs([single]$defaultSettings.Color.r) -lt [single]0.0001 -and
    [Math]::Abs([single]$defaultSettings.Color.g) -lt [single]0.0001 -and
    [Math]::Abs([single]$defaultSettings.Color.b) -lt [single]0.0001 -and
    [Math]::Abs(
        [single]$defaultSettings.Color.a - [single]1) -lt [single]0.0001) `
    'Default outline color must be opaque black.'

$settingsConstructor = $settingsType.GetConstructor(@(
    [bool],
    [int],
    $defaultSettings.Color.GetType()
))
Assert-True ($null -ne $settingsConstructor) `
    'PFViewOutlineSettings constructor is missing.'

$lowWidth = $settingsConstructor.Invoke(@(
    $true,
    -10,
    $defaultSettings.Color
))
$highWidth = $settingsConstructor.Invoke(@(
    $true,
    99,
    $defaultSettings.Color
))
Assert-True ($lowWidth.WidthPixels -eq 1) `
    'Outline widths below 1 must clamp to 1.'
Assert-True ($highWidth.WidthPixels -eq 8) `
    'Outline widths above 8 must clamp to 8.'

$rendererInterface = $assembly.GetType(
    'PioneerFramework.PFView.IPFViewOutlineRenderer', $false)
$providerInterface = $assembly.GetType(
    'PioneerFramework.PFView.IPFViewOutlineShaderProvider', $false)
Assert-True ($null -ne $rendererInterface) `
    'IPFViewOutlineRenderer type is missing.'
Assert-True ($null -ne $providerInterface) `
    'IPFViewOutlineShaderProvider type is missing.'

$rendererMethodNames = @(
    $rendererInterface.GetMethods() |
        ForEach-Object { $_.Name }
)
foreach ($methodName in @(
    'BeginFrame',
    'Submit',
    'Composite',
    'CancelFrame')) {
    Assert-True ($rendererMethodNames -contains $methodName) `
        "IPFViewOutlineRenderer.$methodName is missing."
}

$maskPath = Join-Path $modRoot 'Shaders\PFView_OutlineMask.shader'
$dilatePath = Join-Path $modRoot 'Shaders\PFView_OutlineDilate.shader'
$compositePath = Join-Path $modRoot 'Shaders\PFView_OutlineComposite.shader'

foreach ($path in @($maskPath, $dilatePath, $compositePath)) {
    Assert-True (Test-Path -LiteralPath $path) "Missing shader: $path"
}

$maskSource = Get-Content -Raw -Encoding UTF8 -LiteralPath $maskPath
Assert-Contains $maskSource `
    'Shader "Hidden/PioneerFramework/PFView/OutlineMask"' `
    'Mask shader name changed.'
Assert-Contains $maskSource 'ZTest LEqual' `
    'Mask shader must reuse scene depth.'
Assert-Contains $maskSource 'ZWrite Off' `
    'Mask shader must preserve scene depth.'
Assert-Contains $maskSource 'ColorMask R' `
    'Mask shader must write one mask channel.'

$dilateSource = Get-Content -Raw -Encoding UTF8 -LiteralPath $dilatePath
Assert-Contains $dilateSource `
    'Shader "Hidden/PioneerFramework/PFView/OutlineDilate"' `
    'Dilate shader name changed.'
Assert-Contains $dilateSource `
    'Name "HORIZONTAL_DISTANCE"' `
    'Horizontal squared-distance pass is missing.'
Assert-Contains $dilateSource `
    'Name "VERTICAL_DISTANCE"' `
    'Vertical squared-distance pass is missing.'
Assert-Contains $dilateSource `
    'for (int offset = -radius; offset <= radius; offset++)' `
    'Distance passes must sample only the configured pixel radius.'
Assert-Contains $dilateSource `
    'offset * offset * PFVIEW_DISTANCE_SCALE' `
    'Distance passes must encode squared pixel distance.'
Assert-Contains $dilateSource '_PFViewOutlineRadius' `
    'Dilate shader must use the configured pixel radius.'
Assert-NotContains $dilateSource `
    'for (int offsetY = -8; offsetY <= 8; offsetY++)' `
    'Dilation must not execute a full 17 by 17 kernel per pixel.'

$compositeSource = Get-Content -Raw -Encoding UTF8 `
    -LiteralPath $compositePath
Assert-Contains $compositeSource `
    'Shader "Hidden/PioneerFramework/PFView/OutlineComposite"' `
    'Composite shader name changed.'
Assert-Contains $compositeSource '_PFViewOriginalMask' `
    'Composite shader must sample the original mask.'
Assert-Contains $compositeSource 'saturate(dilated - original)' `
    'Composite shader must remove the original silhouette interior.'
Assert-Contains $compositeSource 'radius * radius' `
    'Composite must threshold the encoded squared distance as a disk.'
Assert-Contains $compositeSource 'Blend SrcAlpha OneMinusSrcAlpha' `
    'Composite shader must alpha-blend the outer ring.'
Assert-Contains $compositeSource 'ColorMask RGB' `
    'Composite shader must preserve destination alpha.'

$shaderSetType = $assembly.GetType(
    'PioneerFramework.PFView.PFViewOutlineShaderSet', $true)
$fixedProviderType = $assembly.GetType(
    'PioneerFramework.PFView.PFViewFixedOutlineShaderProvider', $true)
$rendererType = $assembly.GetType(
    'PioneerFramework.PFView.PFViewMaskOutlineRenderer', $false)
Assert-True ($null -ne $rendererType) `
    'PFViewMaskOutlineRenderer type is missing.'

$emptyShaderSet = [Activator]::CreateInstance($shaderSetType)
$providerConstructor = $fixedProviderType.GetConstructor(@($shaderSetType))
Assert-True ($null -ne $providerConstructor) `
    'PFViewFixedOutlineShaderProvider constructor is missing.'
$provider = $providerConstructor.Invoke(@($emptyShaderSet))

$providerMethod = $fixedProviderType.GetMethod('TryGetShaderSet')
$providerArguments = [object[]]@($emptyShaderSet, $null)
$providerResult = [bool]$providerMethod.Invoke(
    $provider,
    $providerArguments)
Assert-True (-not $providerResult) `
    'An incomplete fixed shader provider must fail.'
Assert-True (-not [string]::IsNullOrWhiteSpace(
    [string]$providerArguments[1])) `
    'An incomplete fixed shader provider must explain its failure.'

$rendererConstructor = $rendererType.GetConstructor(@($providerInterface))
Assert-True ($null -ne $rendererConstructor) `
    'PFViewMaskOutlineRenderer provider constructor is missing.'
$renderer = $rendererConstructor.Invoke(@($provider))

Assert-True (-not $renderer.IsReady) `
    'Renderer with an empty shader set must not be ready.'
Assert-True ($renderer.Status.Code.ToString() -eq 'MissingShaders') `
    'Renderer must report MissingShaders for an incomplete set.'

$disabledSettings = $settingsConstructor.Invoke(@(
    $false,
    2,
    $defaultSettings.Color
))
$beginFrame = $rendererType.GetMethod('BeginFrame')
$beginFrame.Invoke(
    $renderer,
    [object[]]@($null, $null, $disabledSettings))
Assert-True ($renderer.Status.Code.ToString() -eq 'Disabled') `
    'Disabled settings must short-circuit before frame validation.'

$beginFrame.Invoke(
    $renderer,
    [object[]]@($null, $null, $defaultSettings))
Assert-True ($renderer.Status.Code.ToString() -eq 'InvalidFrame') `
    'Null camera and destination must report InvalidFrame.'

$composite = $rendererType.GetMethod('Composite')
Assert-True (-not [bool]$composite.Invoke($renderer, @())) `
    'Composite must fail safely without an active valid frame.'

$dispose = $rendererType.GetMethod('Dispose')
$dispose.Invoke($renderer, @())
$dispose.Invoke($renderer, @())
Assert-True ($renderer.Status.Code.ToString() -eq 'Disposed') `
    'Dispose must be idempotent and report Disposed.'

Write-Output 'PFView outline core assertions passed.'
