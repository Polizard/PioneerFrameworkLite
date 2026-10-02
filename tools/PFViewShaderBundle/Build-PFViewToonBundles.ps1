<#
.SYNOPSIS
Builds the three platform-specific PF_Toon Shader AssetBundles.

.DESCRIPTION
PF_Toon (cel shading: two-band shadows + per-object inverted-hull outline) is a
team-authored asset. Unity shaders must be compiled by the Editor into an
AssetBundle before RimWorld can load them -- the mod's loose Shaders/ folder is
inert at runtime (the very item tracked by the full-version MasterPlan and by
Lite's TODO M5-2).

This script stages PF_Toon.shader into a temporary Unity project (copied from
ProjectTemplate), runs PFViewToonBundleBuilder.BuildAll in batch mode, verifies
the serialized shader name in every staged bundle with Unity's own
WebExtract/binary2text tools, and publishes the validated set to 1.6\AssetBundles.

Use -PrepareOnly to inspect the generated Unity project without launching Unity:

    powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
        Tools\PFViewShaderBundle\Build-PFViewToonBundles.ps1 `
        -PrepareOnly `
        -TemporaryProjectRoot C:\Temp\PFViewToonProject

Normal build usage (requires Unity 2022.3.35f1):

    powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
        Tools\PFViewShaderBundle\Build-PFViewToonBundles.ps1

It publishes these extensionless files to 1.6\AssetBundles:

    pfview_toon_win
    pfview_toon_linux
    pfview_toon_mac

Boundary guard: the script refuses to publish if a staged bundle also contains a
reserved outline shader name (the outline set has its own tooling). It never
stages outline shaders and never publishes .manifest files.

NOTE: this file is intentionally ASCII-only -- Windows PowerShell 5.1 reads
      BOM-less files as ANSI, which would garble any non-ASCII literal
      (same rule as tools/test-all.ps1's NOTE 1).
#>
[CmdletBinding()]
param(
    [string]$UnityEditorPath =
        'E:\Unity\Hub\Editor\2022.3.35f1\Editor\Unity.exe',

    [string]$ModRoot,

    [string]$TemporaryProjectRoot,

    [switch]$PrepareOnly,

    [switch]$KeepTemporaryProject
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$requiredUnityProductVersion = '2022.3.35f1_011206c7a712'

function ConvertTo-UnityCommandLineArgument
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ($Value.Contains('"'))
    {
        throw "Unity command-line path cannot contain a quote: $Value"
    }

    # Start-Process flattens ArgumentList into one Windows command line. Keep
    # each filesystem path quoted so spaces cannot split a Unity argument.
    return '"' + $Value + '"'
}

function Remove-PFViewUnityTemporaryProject
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
        throw "Refusing to clean an unexpected path: $resolvedPath"
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

$requiredShaderNames = @('PF_Toon.shader')
$requiredShaderSerializedNames = @('PioneerFramework/Toon')
$forbiddenShaderSerializedNames = @(
    'Hidden/PioneerFramework/PFView/OutlineMask',
    'Hidden/PioneerFramework/PFView/OutlineDilate',
    'Hidden/PioneerFramework/PFView/OutlineComposite'
)
$bundleNames = @(
    'pfview_toon_win',
    'pfview_toon_linux',
    'pfview_toon_mac'
)

$toolsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ModRoot))
{
    $ModRoot = Join-Path $toolsRoot '..\..'
}
$resolvedModRoot = [System.IO.Path]::GetFullPath($ModRoot)
$shaderSourceRoot = Join-Path $resolvedModRoot 'Shaders'

# Validate every input before creating the destination. This guarantees that a
# missing source cannot leave a partially prepared Unity project behind.
foreach ($shaderName in $requiredShaderNames)
{
    $sourcePath = Join-Path $shaderSourceRoot $shaderName
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf))
    {
        throw "Required PFView toon shader is missing: $sourcePath"
    }
}

$projectTemplateRoot = Join-Path $toolsRoot 'ProjectTemplate'
if (-not (Test-Path -LiteralPath $projectTemplateRoot -PathType Container))
{
    throw "Unity project template is missing: $projectTemplateRoot"
}

$ownsTemporaryProject = [string]::IsNullOrWhiteSpace(
    $TemporaryProjectRoot)
if ($ownsTemporaryProject)
{
    $TemporaryProjectRoot = Join-Path `
        ([System.IO.Path]::GetTempPath()) `
        ('PFViewToonBundle-' + [Guid]::NewGuid().ToString('N'))
}
$resolvedProjectRoot = [System.IO.Path]::GetFullPath(
    $TemporaryProjectRoot)

if (Test-Path -LiteralPath $resolvedProjectRoot)
{
    throw "Temporary Unity project path already exists: $resolvedProjectRoot"
}

$cleanOwnedProject = $ownsTemporaryProject -and
    (-not $PrepareOnly) -and
    (-not $KeepTemporaryProject)

try
{
    New-Item -ItemType Directory -Path $resolvedProjectRoot -Force |
        Out-Null

    foreach ($templateEntry in Get-ChildItem `
        -LiteralPath $projectTemplateRoot `
        -Force)
    {
        Copy-Item `
            -LiteralPath $templateEntry.FullName `
            -Destination $resolvedProjectRoot `
            -Recurse `
            -Force
    }

    $stagedShaderRoot = Join-Path `
        $resolvedProjectRoot `
        'Assets\Data\NewFrontierTeam.PioneerFramework\Materials\PFView'
    New-Item -ItemType Directory -Path $stagedShaderRoot -Force |
        Out-Null

    # Boundary: drop the outline builder from the staged project. It compiles
    # against the outline projection source, which this script deliberately
    # never stages (see the assert that keeps PF_Toon out of the outline
    # project); leaving it in fails the editor assembly with CS0246.
    $stagedOutlineBuilder = Join-Path `
        $resolvedProjectRoot `
        'Assets\Editor\BuildPFViewOutlineBundles.cs'
    if (Test-Path -LiteralPath $stagedOutlineBuilder)
    {
        Remove-Item -LiteralPath $stagedOutlineBuilder -Force
    }

    foreach ($shaderName in $requiredShaderNames)
    {
        Copy-Item `
            -LiteralPath (Join-Path $shaderSourceRoot $shaderName) `
            -Destination (Join-Path $stagedShaderRoot $shaderName)
    }

    if ($PrepareOnly)
    {
        Write-Output $resolvedProjectRoot
        return
    }

    $resolvedUnityEditorPath = [System.IO.Path]::GetFullPath(
        $UnityEditorPath)
    if (-not (Test-Path `
        -LiteralPath $resolvedUnityEditorPath `
        -PathType Leaf))
    {
        throw "Unity Editor is missing: $resolvedUnityEditorPath"
    }

    $unityVersion = (Get-Item -LiteralPath $resolvedUnityEditorPath).
        VersionInfo.ProductVersion
    # Pinned to the same patch (2022.3.35f1); the Unity China suffix is accepted
    # (the CN CDN only serves `2022.3.35f1c1` -- same changeset, same shader
    # compiler and bundle format; verified 2026-10-02).
    # NOTE: do not split a method call across lines -- PS 5.1 does not accept an
    #       argument list that starts on the next line after "Foo(".
    $samePatch = $unityVersion.StartsWith('2022.3.35f1', [System.StringComparison]::Ordinal)
    if (($unityVersion -ne $requiredUnityProductVersion) -and (-not $samePatch))
    {
        throw "Expected Unity $requiredUnityProductVersion, found " +
            $unityVersion
    }
    if ($unityVersion -ne $requiredUnityProductVersion)
    {
        Write-Warning ("Unity version = $unityVersion (same-patch CN variant); " +
            "if the build misbehaves, switch back to $requiredUnityProductVersion")
    }

    $bundleOutputRoot = Join-Path `
        $resolvedModRoot `
        '1.6\AssetBundles'
    $stagedBundleRoot = Join-Path `
        $resolvedProjectRoot `
        'PublishedBundles'
    $logRoot = Join-Path $toolsRoot 'Logs'
    $buildLog = Join-Path $logRoot 'PFViewToonBundleBuild.log'
    New-Item -ItemType Directory -Path $logRoot -Force |
        Out-Null
    if (Test-Path -LiteralPath $buildLog)
    {
        Remove-Item -LiteralPath $buildLog -Force
    }

    $unityArguments = @(
        '-batchmode',
        '-quit',
        '-projectPath',
        (ConvertTo-UnityCommandLineArgument $resolvedProjectRoot),
        '-executeMethod',
        'PFViewToonBundleBuilder.BuildAll',
        '-pfViewOutput',
        (ConvertTo-UnityCommandLineArgument $stagedBundleRoot),
        '-logFile',
        (ConvertTo-UnityCommandLineArgument $buildLog)
    )

    # Unity.exe is a Windows GUI-subsystem executable. PowerShell's call
    # operator can return before that process exits, which would let finally
    # remove the temporary project while Unity is importing it. Start-Process
    # with -Wait makes the temporary-project lifetime match the Editor process.
    $unityProcess = Start-Process `
        -FilePath $resolvedUnityEditorPath `
        -ArgumentList $unityArguments `
        -PassThru `
        -WindowStyle Hidden
    $unityProcess.WaitForExit()
    $unityExitCode = $unityProcess.ExitCode
    if ($unityExitCode -ne 0)
    {
        throw "Unity AssetBundle build failed with exit code $unityExitCode. " +
            "See $buildLog"
    }

    $unityToolsRoot = Join-Path `
        (Split-Path -Parent $resolvedUnityEditorPath) `
        'Data\Tools'
    $webExtractPath = Join-Path $unityToolsRoot 'WebExtract.exe'
    $binaryToTextPath = Join-Path $unityToolsRoot 'binary2text.exe'
    foreach ($inspectionTool in @($webExtractPath, $binaryToTextPath))
    {
        if (-not (Test-Path -LiteralPath $inspectionTool -PathType Leaf))
        {
            throw "Required Unity bundle inspection tool is missing: " +
                $inspectionTool
        }
    }

    $inspectionRoot = Join-Path $resolvedProjectRoot 'BundleInspection'
    New-Item -ItemType Directory -Path $inspectionRoot -Force |
        Out-Null

    # Editor-side AssetBundle.LoadFromFile is sensitive to its active runtime
    # context after command-line cross-platform builds. Unity's own extraction
    # tools validate the serialized files without depending on that context.
    # Every staged platform bundle must expose the exact toon Shader name (and
    # must not smuggle in the reserved outline shaders) before any file is
    # copied into the Mod.
    foreach ($bundleName in $bundleNames)
    {
        $stagedBundlePath = Join-Path $stagedBundleRoot $bundleName
        if (-not (Test-Path -LiteralPath $stagedBundlePath -PathType Leaf))
        {
            throw "Unity did not stage the expected bundle: " +
                $stagedBundlePath
        }

        $bundleItem = Get-Item -LiteralPath $stagedBundlePath
        if ($bundleItem.Length -le 7)
        {
            throw "Staged bundle is empty: $stagedBundlePath"
        }

        $stream = [System.IO.File]::OpenRead($stagedBundlePath)
        try
        {
            $headerBytes = [byte[]]::new(7)
            $bytesRead = $stream.Read(
                $headerBytes,
                0,
                $headerBytes.Length)
            $header = [System.Text.Encoding]::ASCII.GetString(
                $headerBytes,
                0,
                $bytesRead)
        }
        finally
        {
            $stream.Dispose()
        }

        if ($header -ne 'UnityFS')
        {
            throw "Staged bundle has an invalid header: $stagedBundlePath"
        }

        $extractOutput = & $webExtractPath $stagedBundlePath 2>&1
        if ($LASTEXITCODE -ne 0)
        {
            throw "Unity WebExtract failed for $bundleName`: " +
                ($extractOutput -join [Environment]::NewLine)
        }

        $extractedRoot = $stagedBundlePath + '_data'
        $serializedFiles = @(
            Get-ChildItem `
                -LiteralPath $extractedRoot `
                -File `
                -Filter 'CAB-*')
        if ($serializedFiles.Count -ne 1)
        {
            throw "Expected one serialized CAB file in $bundleName, found " +
                $serializedFiles.Count
        }

        $serializedTextPath = Join-Path `
            $inspectionRoot `
            ($bundleName + '.txt')
        $binaryOutput = & $binaryToTextPath `
            $serializedFiles[0].FullName `
            $serializedTextPath `
            2>&1
        if ($LASTEXITCODE -ne 0)
        {
            throw "Unity binary2text failed for $bundleName`: " +
                ($binaryOutput -join [Environment]::NewLine)
        }

        $serializedText = Get-Content `
            -Raw `
            -LiteralPath $serializedTextPath
        foreach ($shaderName in $requiredShaderSerializedNames)
        {
            $serializedName = 'm_Name "' + $shaderName + '"'
            if (-not $serializedText.Contains($serializedName))
            {
                throw "Bundle $bundleName omitted Shader: $shaderName"
            }

            Add-Content `
                -LiteralPath $buildLog `
                -Encoding UTF8 `
                -Value (
                    "[PFViewToonBundle] Verified shader: $shaderName " +
                    "bundle=$bundleName")
        }

        foreach ($forbiddenName in $forbiddenShaderSerializedNames)
        {
            if ($serializedText.Contains($forbiddenName))
            {
                throw "Bundle $bundleName unexpectedly contains the " +
                    "reserved outline shader: $forbiddenName"
            }
        }
    }

    [System.IO.Directory]::CreateDirectory($bundleOutputRoot) | Out-Null
    $transactionId = [Guid]::NewGuid().ToString('N')
    $publicationEntries = @(
        foreach ($bundleName in $bundleNames)
        {
            [PSCustomObject]@{
                Name = $bundleName
                Source = Join-Path $stagedBundleRoot $bundleName
                Final = Join-Path $bundleOutputRoot $bundleName
                Candidate = Join-Path `
                    $bundleOutputRoot `
                    ('.' + $bundleName + '.' + $transactionId + '.new')
                Backup = Join-Path `
                    $bundleOutputRoot `
                    ('.' + $bundleName + '.' + $transactionId + '.bak')
                HadOriginal = $false
                Promoted = $false
            }
        })

    $publicationSucceeded = $false
    try
    {
        # Copy and hash-check all candidates before replacing any live bundle.
        foreach ($entry in $publicationEntries)
        {
            Copy-Item `
                -LiteralPath $entry.Source `
                -Destination $entry.Candidate
            $sourceHash = (Get-FileHash `
                -Algorithm SHA256 `
                -LiteralPath $entry.Source).Hash
            $candidateHash = (Get-FileHash `
                -Algorithm SHA256 `
                -LiteralPath $entry.Candidate).Hash
            if ($sourceHash -ne $candidateHash)
            {
                throw "Candidate copy hash mismatch: $($entry.Name)"
            }
        }

        # Move every old bundle aside before promoting the validated set. A
        # failure at any later point restores the complete previous set.
        foreach ($entry in $publicationEntries)
        {
            if (Test-Path -LiteralPath $entry.Final)
            {
                Move-Item `
                    -LiteralPath $entry.Final `
                    -Destination $entry.Backup
                $entry.HadOriginal = $true
            }
        }

        foreach ($entry in $publicationEntries)
        {
            Move-Item `
                -LiteralPath $entry.Candidate `
                -Destination $entry.Final
            $entry.Promoted = $true
        }

        $publicationSucceeded = $true
    }
    finally
    {
        if ($publicationSucceeded)
        {
            foreach ($entry in $publicationEntries)
            {
                if ($entry.HadOriginal -and
                    (Test-Path -LiteralPath $entry.Backup))
                {
                    Remove-Item -LiteralPath $entry.Backup -Force
                }
            }
        }
        else
        {
            foreach ($entry in $publicationEntries)
            {
                if ($entry.Promoted -and
                    (Test-Path -LiteralPath $entry.Final))
                {
                    Remove-Item -LiteralPath $entry.Final -Force
                }
                if ($entry.HadOriginal -and
                    (Test-Path -LiteralPath $entry.Backup))
                {
                    Move-Item `
                        -LiteralPath $entry.Backup `
                        -Destination $entry.Final
                }
                if (Test-Path -LiteralPath $entry.Candidate)
                {
                    Remove-Item -LiteralPath $entry.Candidate -Force
                }
            }
        }
    }

    foreach ($bundleName in $bundleNames)
    {
        Add-Content `
            -LiteralPath $buildLog `
            -Encoding UTF8 `
            -Value "[PFViewToonBundle] Published: $bundleName"
    }
    Write-Output "PFView toon bundles published to $bundleOutputRoot"
}
finally
{
    if ($cleanOwnedProject -and
        (Test-Path -LiteralPath $resolvedProjectRoot))
    {
        Remove-PFViewUnityTemporaryProject `
            -Path $resolvedProjectRoot `
            -AllowedRoot ([System.IO.Path]::GetTempPath())
    }
}
