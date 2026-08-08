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
$gameManagedDirectory = Join-Path $modRoot '..\..\RimWorldWin64_Data\Managed'
if (-not (Test-Path -LiteralPath $gameManagedDirectory)) {
    $gameManagedDirectory = $null
}
if ([string]::IsNullOrWhiteSpace($AssemblyPath))
{
    $AssemblyPath = Join-Path $modRoot (
        '1.6\Assemblies\PioneerFrameworkLite.dll')
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

    $fileName =
        ([System.Reflection.AssemblyName]$eventArgs.Name).Name + '.dll'
    foreach ($directory in $script:dependencyDirectories) {
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

# Lite 裁剪：DrawDesignParts（Ship3D 设计部件绘制）已删除，仅保留运行时部件绘制
$drawRuntimeParts = $pfViewType.GetMethod(
    'DrawRuntimeParts',
    $allFlags)
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

# Lite 裁剪：Docs/PFView_Outline_Interface_Usage.md 不在 Lite 仓库，usage guide 文档断言跳过

[xml]$project = Get-Content -Raw -Encoding UTF8 -LiteralPath (
    Join-Path $modRoot 'Source\PioneerFramework\PioneerFramework.csproj')
$namespace = New-Object System.Xml.XmlNamespaceManager(
    $project.NameTable)
$namespace.AddNamespace('msb', $project.Project.NamespaceURI)
$compileNodes = @($project.SelectNodes('//msb:Compile', $namespace))
# Lite：保留通配 Compile（obj/bin/.vs 排除）——AutoTest 目录已删，Components 显式 include 保留
$wildcardCompile = $compileNodes | Where-Object { $_.Include -eq '**\*.cs' } | Select-Object -First 1
Assert-True ($null -ne $wildcardCompile) `
    'The project must retain a wildcard Compile item.'
Assert-True ($wildcardCompile.Exclude -match 'obj\\\*\*;bin\\\*\*;\.vs\\\*\*') `
    'The protected wildcard Compile Exclude must cover obj/bin/.vs.'

$assetBundleReference = $project.SelectSingleNode(
    "//msb:Reference[@Include='UnityEngine.AssetBundleModule']",
    $namespace)
Assert-True ($null -ne $assetBundleReference -and
    $assetBundleReference.HintPath -eq
        'Libs\Unity\UnityEngine.AssetBundleModule.dll') `
    'The reserved AssetBundle provider reference is missing or invalid.'

Write-Output 'PFView outline isolation and interface assertions passed.'
