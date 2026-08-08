[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True
{
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if (-not $Condition)
    {
        throw "ASSERTION FAILED: $Message"
    }
}

function Assert-Throws
{
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action,

        [Parameter(Mandatory = $true)]
        [string]$MessagePattern
    )

    $caught = $null
    try
    {
        & $Action
    }
    catch
    {
        $caught = $_
    }

    Assert-True ($null -ne $caught) 'Expected the action to throw.'
    Assert-True `
        ($caught.Exception.Message -like $MessagePattern) `
        "Unexpected failure message: $($caught.Exception.Message)"
}

$toolsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildScript = Join-Path $toolsRoot 'Build-PFViewOutlineBundles.ps1'
$builderSourcePath = Join-Path `
    $toolsRoot `
    'ProjectTemplate\Assets\Editor\BuildPFViewOutlineBundles.cs'

Assert-True `
    (Test-Path -LiteralPath $buildScript -PathType Leaf) `
    'Build-PFViewOutlineBundles.ps1 is missing.'

$buildScriptSource = Get-Content -Raw -LiteralPath $buildScript
Assert-True `
    ($buildScriptSource.Contains('2022.3.35f1_011206c7a712')) `
    'Build entry point must require the exact RimWorld Unity changeset.'
Assert-True `
    ($buildScriptSource.Contains('ConvertTo-UnityCommandLineArgument')) `
    'Build entry point must quote Unity path arguments explicitly.'
$builderSource = Get-Content -Raw -LiteralPath $builderSourcePath
Assert-True `
    ($builderSource.Contains('bundle.name')) `
    'Runtime validation must verify the loaded Bundle identity.'

$systemTemp = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::GetTempPath())
$fixtureRoot = Join-Path `
    $systemTemp `
    ('PFView Outline Tooling Test ' + [Guid]::NewGuid().ToString('N'))
$fixtureModRoot = Join-Path $fixtureRoot 'Mod'
$shaderSourceRoot = Join-Path $fixtureModRoot 'Shaders'
$projectionSourceRoot = Join-Path `
    $fixtureModRoot `
    'Source\PioneerFramework\PFView\ToonOutline'
$preparedProject = Join-Path $fixtureRoot 'PreparedProject'
$missingProject = Join-Path $fixtureRoot 'MissingSourceProject'

$requiredShaders = [ordered]@{
    'PFView_OutlineMask.shader' = @'
Shader "Hidden/PioneerFramework/PFView/OutlineMask" { }
'@
    'PFView_OutlineDilate.shader' = @'
Shader "Hidden/PioneerFramework/PFView/OutlineDilate" { }
'@
    'PFView_OutlineComposite.shader' = @'
Shader "Hidden/PioneerFramework/PFView/OutlineComposite" { }
'@
}

try
{
    New-Item -ItemType Directory -Path $shaderSourceRoot -Force |
        Out-Null
    New-Item -ItemType Directory -Path $projectionSourceRoot -Force |
        Out-Null

    foreach ($entry in $requiredShaders.GetEnumerator())
    {
        [System.IO.File]::WriteAllText(
            (Join-Path $shaderSourceRoot $entry.Key),
            $entry.Value,
            [System.Text.UTF8Encoding]::new($false))
    }

    [System.IO.File]::WriteAllText(
        (Join-Path $shaderSourceRoot 'PF_Toon.shader'),
        'Shader "PioneerFramework/Toon" { }',
        [System.Text.UTF8Encoding]::new($false))
    $projectionSourcePath = Join-Path `
        $projectionSourceRoot `
        'PFViewOutlineProjection.cs'
    [System.IO.File]::WriteAllText(
        $projectionSourcePath,
        @'
using UnityEngine;
namespace PioneerFramework.PFView
{
    internal static class PFViewOutlineProjection
    {
        internal static Matrix4x4 GetForCommandBuffer(Camera camera)
        {
            return camera.projectionMatrix;
        }
    }
}
'@,
        [System.Text.UTF8Encoding]::new($false))

    & $buildScript `
        -ModRoot $fixtureModRoot `
        -TemporaryProjectRoot $preparedProject `
        -PrepareOnly | Out-Null

    $projectVersionPath = Join-Path `
        $preparedProject `
        'ProjectSettings\ProjectVersion.txt'
    Assert-True `
        (Test-Path -LiteralPath $projectVersionPath -PathType Leaf) `
        'Prepared project is missing ProjectVersion.txt.'
    Assert-True `
        ((Get-Content -Raw -LiteralPath $projectVersionPath) -match
            'm_EditorVersion:\s*2022\.3\.35f1') `
        'Prepared project does not request Unity 2022.3.35f1.'

    $packageManifestPath = Join-Path `
        $preparedProject `
        'Packages\manifest.json'
    Assert-True `
        (Test-Path -LiteralPath $packageManifestPath -PathType Leaf) `
        'Prepared project is missing Packages/manifest.json.'
    $packageManifest = Get-Content -Raw -LiteralPath $packageManifestPath |
        ConvertFrom-Json
    Assert-True `
        ($packageManifest.dependencies.'com.unity.modules.assetbundle' -eq
            '1.0.0') `
        'Prepared project must enable Unity AssetBundle runtime support.'
    $renderPipelinePackages = @(
        $packageManifest.dependencies.PSObject.Properties |
            Where-Object { $_.Name -like 'com.unity.render-pipelines.*' })
    Assert-True `
        ($renderPipelinePackages.Count -eq 0) `
        'Prepared project must use the Built-in Render Pipeline.'

    $stagedShaderRoot = Join-Path `
        $preparedProject `
        'Assets\Data\NewFrontierTeam.PioneerFramework\Materials\PFView'
    $stagedShaders = @(
        Get-ChildItem -LiteralPath $stagedShaderRoot -File -Filter '*.shader')
    Assert-True `
        ($stagedShaders.Count -eq 3) `
        "Expected exactly 3 staged shaders, found $($stagedShaders.Count)."

    foreach ($shaderName in $requiredShaders.Keys)
    {
        $sourcePath = Join-Path $shaderSourceRoot $shaderName
        $stagedPath = Join-Path $stagedShaderRoot $shaderName
        Assert-True `
            (Test-Path -LiteralPath $stagedPath -PathType Leaf) `
            "Required shader was not staged: $shaderName"
        Assert-True `
            ((Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash -eq
                (Get-FileHash -Algorithm SHA256 -LiteralPath $stagedPath).Hash) `
            "Staged shader differs from its source: $shaderName"
    }

    $stagedProjectionPath = Join-Path `
        $preparedProject `
        'Assets\Editor\PFViewOutlineProjection.cs'
    Assert-True `
        (Test-Path -LiteralPath $stagedProjectionPath -PathType Leaf) `
        'Prepared project is missing the shared projection selector.'
    Assert-True `
        ((Get-FileHash -Algorithm SHA256 `
            -LiteralPath $projectionSourcePath).Hash -eq
        (Get-FileHash -Algorithm SHA256 `
            -LiteralPath $stagedProjectionPath).Hash) `
        'Staged projection selector differs from its runtime source.'

    Assert-True `
        (-not (Test-Path -LiteralPath (
            Join-Path $stagedShaderRoot 'PF_Toon.shader'))) `
        'PF_Toon.shader must not be staged in the outline bundle project.'

    Remove-Item -LiteralPath (
        Join-Path $shaderSourceRoot 'PFView_OutlineComposite.shader')

    Assert-Throws `
        -MessagePattern '*Required PFView outline shader is missing*' `
        -Action {
            & $buildScript `
                -ModRoot $fixtureModRoot `
                -TemporaryProjectRoot $missingProject `
                -PrepareOnly | Out-Null
        }
    Assert-True `
        (-not (Test-Path -LiteralPath $missingProject)) `
        'Missing-source validation must not publish a partial project.'

    Write-Output 'PFView outline bundle tooling assertions passed.'
}
finally
{
    $resolvedFixtureRoot = [System.IO.Path]::GetFullPath($fixtureRoot)
    if (-not $resolvedFixtureRoot.StartsWith(
        $systemTemp,
        [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to clean an unexpected test path: $resolvedFixtureRoot"
    }

    if (Test-Path -LiteralPath $resolvedFixtureRoot)
    {
        Remove-Item -LiteralPath $resolvedFixtureRoot -Recurse -Force
    }
}
