param(
    [string]$AssemblyPath
)

$ErrorActionPreference = 'Stop'

function Assert-True
{
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition)
    {
        throw $Message
    }
}

function Get-CalledMethods
{
    param(
        [System.Reflection.MethodBase]$Method
    )

    $body = $Method.GetMethodBody()
    if ($null -eq $body)
    {
        return @()
    }

    $bytes = $body.GetILAsByteArray()
    $module = $Method.Module
    $typeArguments = if ($null -ne $Method.DeclaringType)
    {
        $Method.DeclaringType.GetGenericArguments()
    }
    else
    {
        @()
    }
    $methodArguments = if ($Method.IsGenericMethod)
    {
        $Method.GetGenericArguments()
    }
    else
    {
        @()
    }

    $called = New-Object System.Collections.Generic.List[object]
    for ($index = 0; $index -le $bytes.Length - 5; $index++)
    {
        if ($bytes[$index] -ne 0x28 -and
            $bytes[$index] -ne 0x6f -and
            $bytes[$index] -ne 0x73)
        {
            continue
        }

        $token = [BitConverter]::ToInt32($bytes, $index + 1)
        try
        {
            $resolved = $module.ResolveMethod(
                $token,
                $typeArguments,
                $methodArguments)
            if ($null -ne $resolved)
            {
                $called.Add($resolved)
            }
        }
        catch
        {
            # Ignore method opcode bytes embedded in another operand.
        }
    }

    return $called.ToArray()
}

function Invoke-TryGetBundleName
{
    param(
        [System.Reflection.MethodInfo]$Method,
        [object]$Platform,
        [bool]$ExpectedSuccess,
        [string]$ExpectedName
    )

    $arguments = @($Platform, $null)
    $success = [bool]$Method.Invoke($null, $arguments)
    Assert-True ($success -eq $ExpectedSuccess) `
        "Unexpected platform support result for $Platform."
    Assert-True ([string]$arguments[1] -eq $ExpectedName) `
        "Unexpected Bundle name for $Platform."
}

$modRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$gameManagedDirectory = (Resolve-Path (
    Join-Path $modRoot '..\..\RimWorldWin64_Data\Managed')).Path
if ([string]::IsNullOrWhiteSpace($AssemblyPath))
{
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
    foreach ($directory in $dependencyDirectories)
    {
        $candidate = Join-Path $directory $fileName
        if (Test-Path -LiteralPath $candidate)
        {
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
$staticFlags = [System.Reflection.BindingFlags]'Static,Public,NonPublic'
$instanceFlags = [System.Reflection.BindingFlags]'Instance,Public,NonPublic'
$allFlags = [System.Reflection.BindingFlags](
    'Instance,Static,Public,NonPublic')

$providerInterface = $assembly.GetType(
    'PioneerFramework.PFView.IPFViewOutlineShaderProvider', $true)
$rendererInterface = $assembly.GetType(
    'PioneerFramework.PFView.IPFViewOutlineRenderer', $true)
$rendererType = $assembly.GetType(
    'PioneerFramework.PFView.PFViewMaskOutlineRenderer', $true)
$providerType = $assembly.GetType(
    'PioneerFramework.PFView.PFViewOutlineAssetBundleProvider', $true)
$projectionType = $assembly.GetType(
    'PioneerFramework.PFView.PFViewOutlineProjection', $true)

Assert-True ($providerInterface.IsAssignableFrom($providerType)) `
    'Reserved AssetBundle provider must implement the shader interface.'
Assert-True ($rendererInterface.IsAssignableFrom($rendererType)) `
    'Reserved mask renderer must implement the outline interface.'
Assert-True ([IDisposable].IsAssignableFrom($providerType)) `
    'Reserved AssetBundle provider must remain disposable.'
Assert-True ($null -ne $projectionType) `
    'Shared command-buffer projection rule must remain available.'

$tryGetBundleName = $providerType.GetMethod(
    'TryGetBundleName',
    $staticFlags)
Invoke-TryGetBundleName $tryGetBundleName `
    ([UnityEngine.RuntimePlatform]::WindowsPlayer) $true `
    'pfview_outline_win'
Invoke-TryGetBundleName $tryGetBundleName `
    ([UnityEngine.RuntimePlatform]::LinuxPlayer) $true `
    'pfview_outline_linux'
Invoke-TryGetBundleName $tryGetBundleName `
    ([UnityEngine.RuntimePlatform]::OSXPlayer) $true `
    'pfview_outline_mac'

$pfViewType = $assembly.GetType(
    'PioneerFramework.MainTabWindow_PFView', $true)
$outlineFields = @(
    $pfViewType.GetFields($allFlags) |
        Where-Object {
            $rendererInterface.IsAssignableFrom($_.FieldType) -or
            $providerInterface.IsAssignableFrom($_.FieldType) -or
            $_.FieldType -eq $providerType
        })
Assert-True ($outlineFields.Count -eq 0) `
    'PFView must not own outline renderer or provider state.'

$outlineCalls = @()
foreach ($method in $pfViewType.GetMethods($allFlags))
{
    foreach ($call in @(Get-CalledMethods $method))
    {
        if ($null -ne $call.DeclaringType -and
            $call.DeclaringType.Namespace -eq 'PioneerFramework.PFView')
        {
            $outlineCalls += "$($method.Name) -> " +
                "$($call.DeclaringType.Name).$($call.Name)"
        }
    }
}
Assert-True ($outlineCalls.Count -eq 0) `
    ('PFView must not call the reserved outline module: ' +
        ($outlineCalls -join ', '))

$drawDesignParts = $pfViewType.GetMethod(
    'DrawDesignParts',
    $allFlags)
$drawRuntimeParts = $pfViewType.GetMethod(
    'DrawRuntimeParts',
    $allFlags)
Assert-True ($drawDesignParts.IsStatic) `
    'DrawDesignParts must be restored to its pre-outline static form.'
Assert-True ($drawRuntimeParts.IsStatic) `
    'DrawRuntimeParts must be restored to its pre-outline static form.'

$renderScene = $pfViewType.GetMethod('RenderScene', $instanceFlags)
$renderCalls = @(Get-CalledMethods $renderScene)
$cameraRenderCalls = @(
    $renderCalls |
        Where-Object {
            $_.DeclaringType.FullName -eq 'UnityEngine.Camera' -and
            $_.Name -eq 'Render'
        })
Assert-True ($cameraRenderCalls.Count -eq 1) `
    'PFView RenderScene must retain one direct Camera.Render call.'

$pfViewSourcePath = Join-Path $modRoot (
    'Source\PioneerFramework\PFView\MainTabWindow_PFView.cs')
$pfViewSource = Get-Content -Raw -Encoding UTF8 `
    -LiteralPath $pfViewSourcePath
foreach ($forbiddenToken in @(
    'PioneerFramework.PFView',
    'outlineShaderProvider',
    'outlineRenderer',
    'InitializeShipOutlineResources',
    'BeginShipOutlineFrame',
    'SubmitShipOutline',
    'CompleteShipOutlineFrame',
    'CleanupShipOutlineResources'
))
{
    Assert-True (-not $pfViewSource.Contains($forbiddenToken)) `
        "PFView still contains outline integration token: $forbiddenToken"
}

$contractsSource = Get-Content -Raw -Encoding UTF8 -LiteralPath (
    Join-Path $modRoot (
        'Source\PioneerFramework\PFView\ToonOutline\' +
        'PFViewOutlineContracts.cs'))
$providerSource = Get-Content -Raw -Encoding UTF8 -LiteralPath (
    Join-Path $modRoot (
        'Source\PioneerFramework\PFView\ToonOutline\' +
        'PFViewOutlineAssetBundleProvider.cs'))
foreach ($usageToken in @(
    '<example>',
    'BeginFrame',
    'Submit',
    'Camera.Render',
    'Composite',
    'Dispose'
))
{
    Assert-True ($contractsSource.Contains($usageToken)) `
        "Outline interface usage documentation is missing: $usageToken"
}
Assert-True ($providerSource.Contains('<example>') -and
    $providerSource.Contains('TryLoad')) `
    'AssetBundle provider usage documentation must remain in source.'

$matchingVisibleDrawPattern =
    'Graphics\.DrawMesh\(\s*part\.Mesh,\s*part\.Matrix,\s*' +
    'part\.Material,\s*sceneLayer,\s*renderCamera,\s*' +
    'part\.SubMeshIndex\s*\);'
$contractsExampleText = [regex]::Replace(
    $contractsSource,
    '(?m)^\s*/// ?',
    '')
Assert-True ([regex]::IsMatch(
    $contractsExampleText,
    $matchingVisibleDrawPattern,
    [System.Text.RegularExpressions.RegexOptions]::Singleline)) `
    ('The interface example must draw the same camera and submesh that it ' +
        'submits to the outline mask.')

$usageGuideSource = Get-Content -Raw -Encoding UTF8 -LiteralPath (
    Join-Path $modRoot 'Docs\PFView_Outline_Interface_Usage.md')
Assert-True ([regex]::IsMatch(
    $usageGuideSource,
    $matchingVisibleDrawPattern,
    [System.Text.RegularExpressions.RegexOptions]::Singleline)) `
    ('The Chinese usage guide must draw the same camera and submesh that it ' +
        'submits to the outline mask.')
Assert-True ([regex]::IsMatch(
    $contractsExampleText,
    'Remains false until\s+the first valid BeginFrame')) `
    'The IsReady contract must document lazy GPU initialization.'
Assert-True ([regex]::IsMatch(
    $usageGuideSource,
    '(?m)^- `IsReady`.*`BeginFrame`.*`false`')) `
    'The Chinese usage guide must document lazy GPU initialization.'

[xml]$project = Get-Content -Raw -Encoding UTF8 -LiteralPath (
    Join-Path $modRoot 'Source\PioneerFramework\PioneerFramework.csproj')
$namespace = New-Object System.Xml.XmlNamespaceManager(
    $project.NameTable)
$namespace.AddNamespace('msb', $project.Project.NamespaceURI)
$compileNodes = @($project.SelectNodes('//msb:Compile', $namespace))
Assert-True ($compileNodes.Count -eq 1) `
    'The project must retain exactly one wildcard Compile item.'
Assert-True ($compileNodes[0].Include -eq '**\*.cs' -and
    $compileNodes[0].Exclude -eq 'obj\**;bin\**;.vs\**') `
    'The protected wildcard Compile item changed.'

$assetBundleReference = $project.SelectSingleNode(
    "//msb:Reference[@Include='UnityEngine.AssetBundleModule']",
    $namespace)
Assert-True ($null -ne $assetBundleReference -and
    $assetBundleReference.HintPath -eq
        'Libs\Unity\UnityEngine.AssetBundleModule.dll') `
    'The reserved AssetBundle provider reference is missing or invalid.'

Write-Output 'PFView outline isolation and interface assertions passed.'
