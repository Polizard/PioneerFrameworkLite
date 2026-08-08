<#
.SYNOPSIS
Runs the real Unity build and validates its consumer-visible Bundle outputs.

.DESCRIPTION
This is an integration assertion. It requires Unity 2022.3.35f1 and invokes
the same Build-PFViewOutlineBundles.ps1 entry point documented for maintainers.
#>
[CmdletBinding()]
param(
    [string]$UnityEditorPath =
        'E:\Unity\Hub\Editor\2022.3.35f1\Editor\Unity.exe'
)

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

function Get-AsciiFilePrefix
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [int]$Length
    )

    $stream = [System.IO.File]::OpenRead($Path)
    try
    {
        $buffer = [byte[]]::new($Length)
        $bytesRead = $stream.Read($buffer, 0, $buffer.Length)
        return [System.Text.Encoding]::ASCII.GetString(
            $buffer,
            0,
            $bytesRead)
    }
    finally
    {
        $stream.Dispose()
    }
}

function Remove-IntegrationProject
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$AllowedRoot
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedAllowedRoot =
        [System.IO.Path]::GetFullPath($AllowedRoot).
            TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith(
        $resolvedAllowedRoot,
        [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to clean an unexpected test path: $resolvedPath"
    }

    for ($attempt = 1; $attempt -le 20; $attempt++)
    {
        try
        {
            if (Test-Path -LiteralPath $resolvedPath)
            {
                Remove-Item `
                    -LiteralPath $resolvedPath `
                    -Recurse `
                    -Force
            }
            return
        }
        catch [System.IO.IOException], [System.UnauthorizedAccessException]
        {
            if ($attempt -eq 20)
            {
                throw
            }
            Start-Sleep -Milliseconds 250
        }
    }
}

$toolsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$modRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $toolsRoot '..\..'))
$buildScript = Join-Path $toolsRoot 'Build-PFViewOutlineBundles.ps1'
$bundleRoot = Join-Path $modRoot '1.6\AssetBundles'
$buildLog = Join-Path `
    $toolsRoot `
    'Logs\PFViewOutlineBundleBuild.log'
$bundleNames = @(
    'pfview_outline_win',
    'pfview_outline_linux',
    'pfview_outline_mac'
)
$shaderNames = @(
    'Hidden/PioneerFramework/PFView/OutlineMask',
    'Hidden/PioneerFramework/PFView/OutlineDilate',
    'Hidden/PioneerFramework/PFView/OutlineComposite'
)

Assert-True `
    (Test-Path -LiteralPath $UnityEditorPath -PathType Leaf) `
    "Unity Editor is missing: $UnityEditorPath"
$unityVersion = (Get-Item -LiteralPath $UnityEditorPath).
    VersionInfo.ProductVersion
Assert-True `
    ($unityVersion -eq '2022.3.35f1_011206c7a712') `
    "Expected Unity 2022.3.35f1_011206c7a712, found $unityVersion"

$systemTemp = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::GetTempPath())
$integrationProjectRoot = Join-Path `
    $systemTemp `
    ('PFView Outline Bundle Integration ' +
        [Guid]::NewGuid().ToString('N'))

try
{
    & $buildScript `
        -UnityEditorPath $UnityEditorPath `
        -ModRoot $modRoot `
        -TemporaryProjectRoot $integrationProjectRoot | Out-Host
}
finally
{
    Remove-IntegrationProject `
        -Path $integrationProjectRoot `
        -AllowedRoot $systemTemp
}

foreach ($bundleName in $bundleNames)
{
    $bundlePath = Join-Path $bundleRoot $bundleName
    Assert-True `
        (Test-Path -LiteralPath $bundlePath -PathType Leaf) `
        "Bundle is missing: $bundleName"
    Assert-True `
        ((Get-Item -LiteralPath $bundlePath).Length -gt 7) `
        "Bundle is empty: $bundleName"
    Assert-True `
        ((Get-AsciiFilePrefix -Path $bundlePath -Length 7) -eq 'UnityFS') `
        "Bundle does not have a UnityFS header: $bundleName"
}

$publishedManifests = @(
    Get-ChildItem `
        -LiteralPath $bundleRoot `
        -File `
        -Filter 'pfview_outline_*.manifest' `
        -ErrorAction SilentlyContinue)
Assert-True `
    ($publishedManifests.Count -eq 0) `
    'PFView outline .manifest files must not be published to the Mod.'

Assert-True `
    (Test-Path -LiteralPath $buildLog -PathType Leaf) `
    'Unity build log is missing.'
$logText = Get-Content -Raw -LiteralPath $buildLog
Assert-True `
    ($logText.Contains(
        '[PFViewOutlineBundle] Runtime-loaded bundle: ' +
        'pfview_outline_win')) `
    'Unity log does not confirm runtime loading of the Windows bundle.'
Assert-True `
    ($logText.Contains(
        '[PFViewOutlineBundle] Verified exterior-only composite: ' +
        'pfview_outline_win')) `
    'Unity log does not confirm exterior-only composite behavior.'
Assert-True `
    ($logText.Contains(
        '[PFViewOutlineBundle] Verified projection alignment: ' +
        'pfview_outline_win')) `
    'Unity log does not confirm Camera/CommandBuffer projection alignment.'

foreach ($bundleName in $bundleNames)
{
    Assert-True `
        ($logText.Contains(
            "[PFViewOutlineBundle] Published bundle: $bundleName")) `
        "Unity log does not confirm publication: $bundleName"

    foreach ($shaderName in $shaderNames)
    {
        Assert-True `
            ($logText.Contains(
                "[PFViewOutlineBundle] Verified shader: $shaderName " +
                "bundle=$bundleName")) `
            ("Unity log does not confirm Shader verification: " +
                "$shaderName in $bundleName")
    }
}

Write-Output 'PFView outline AssetBundle integration assertions passed.'
