<#
.SYNOPSIS
Builds the three platform-specific PFView outline Shader AssetBundles.

.DESCRIPTION
The script always reads the three authoritative Shader sources and the shared
PFViewOutlineProjection source from the Mod. The projection source lets Unity
validate that Camera and CommandBuffer rasterization stay aligned. Use
-PrepareOnly to inspect the generated Unity project without launching Unity:

    powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
        Tools\PFViewShaderBundle\Build-PFViewOutlineBundles.ps1 `
        -PrepareOnly `
        -TemporaryProjectRoot C:\Temp\PFViewOutlineProject

Normal build usage:

    powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
        Tools\PFViewShaderBundle\Build-PFViewOutlineBundles.ps1

It publishes these extensionless files to 1.6\AssetBundles:

    pfview_outline_win
    pfview_outline_linux
    pfview_outline_mac

The script never stages PF_Toon.shader and never publishes .manifest files.
Runtime integration remains separate: load the three Shader assets for the
active platform, create PFViewOutlineShaderSet, and pass it through the
existing IPFViewOutlineShaderProvider interface.
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

$requiredShaderNames = @(
    'PFView_OutlineMask.shader',
    'PFView_OutlineDilate.shader',
    'PFView_OutlineComposite.shader'
)

$toolsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ModRoot))
{
    $ModRoot = Join-Path $toolsRoot '..\..'
}
$resolvedModRoot = [System.IO.Path]::GetFullPath($ModRoot)
$shaderSourceRoot = Join-Path $resolvedModRoot 'Shaders'
$projectionSourcePath = Join-Path `
    $resolvedModRoot `
    'Source\PioneerFramework\PFView\ToonOutline\PFViewOutlineProjection.cs'

# Validate every input before creating the destination. This guarantees that a
# missing source cannot leave a partially prepared Unity project behind.
foreach ($shaderName in $requiredShaderNames)
{
    $sourcePath = Join-Path $shaderSourceRoot $shaderName
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf))
    {
        throw "Required PFView outline shader is missing: $sourcePath"
    }
}
if (-not (Test-Path -LiteralPath $projectionSourcePath -PathType Leaf))
{
    throw "Required PFView outline projection source is missing: " +
        $projectionSourcePath
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
        ('PFViewOutlineBundle-' + [Guid]::NewGuid().ToString('N'))
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

    foreach ($shaderName in $requiredShaderNames)
    {
        Copy-Item `
            -LiteralPath (Join-Path $shaderSourceRoot $shaderName) `
            -Destination (Join-Path $stagedShaderRoot $shaderName)
    }

    Copy-Item `
        -LiteralPath $projectionSourcePath `
        -Destination (Join-Path `
            $resolvedProjectRoot `
            'Assets\Editor\PFViewOutlineProjection.cs')

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
    if ($unityVersion -ne $requiredUnityProductVersion)
    {
        throw "Expected Unity $requiredUnityProductVersion, found " +
            $unityVersion
    }

    $bundleOutputRoot = Join-Path `
        $resolvedModRoot `
        '1.6\AssetBundles'
    $stagedBundleRoot = Join-Path `
        $resolvedProjectRoot `
        'PublishedBundles'
    $logRoot = Join-Path $toolsRoot 'Logs'
    $buildLog = Join-Path $logRoot 'PFViewOutlineBundleBuild.log'
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
        'PFViewOutlineBundleBuilder.BuildAll',
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
    # Start-Process -Wait also waits for Unity's long-lived licensing and
    # ShaderCompiler child processes on Windows. WaitForExit targets only the
    # Editor process whose exit code defines this build.
    $unityProcess.WaitForExit()
    $unityExitCode = $unityProcess.ExitCode
    if ($unityExitCode -ne 0)
    {
        throw "Unity AssetBundle build failed with exit code $unityExitCode. " +
            "See $buildLog"
    }

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
    # Every staged platform bundle must expose all three exact Shader names
    # before any file is copied into the Mod.
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
        foreach ($shaderName in $shaderNames)
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
                    "[PFViewOutlineBundle] Verified shader: $shaderName " +
                    "bundle=$bundleName")
        }

        if ($serializedText.Contains('PF_Toon'))
        {
            throw "Bundle $bundleName unexpectedly contains PF_Toon."
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

        foreach ($entry in $publicationEntries)
        {
            $staleManifest = $entry.Final + '.manifest'
            if (Test-Path -LiteralPath $staleManifest)
            {
                Remove-Item -LiteralPath $staleManifest -Force
            }
        }

        $publicationSucceeded = $true
    }
    catch
    {
        $publicationFailure = $_
        $rollbackErrors =
            [System.Collections.Generic.List[string]]::new()
        for ($index = $publicationEntries.Count - 1;
             $index -ge 0;
             $index--)
        {
            $entry = $publicationEntries[$index]
            try
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
            }
            catch
            {
                $rollbackErrors.Add($_.Exception.Message)
            }
        }

        if ($rollbackErrors.Count -gt 0)
        {
            throw "Bundle publication failed: " +
                $publicationFailure.Exception.Message +
                ". Rollback also failed: " +
                ($rollbackErrors -join ' | ')
        }
        throw $publicationFailure
    }
    finally
    {
        foreach ($entry in $publicationEntries)
        {
            if (Test-Path -LiteralPath $entry.Candidate)
            {
                Remove-Item -LiteralPath $entry.Candidate -Force
            }
            if ($publicationSucceeded -and
                (Test-Path -LiteralPath $entry.Backup))
            {
                Remove-Item -LiteralPath $entry.Backup -Force
            }
        }
    }

    foreach ($bundleName in $bundleNames)
    {
        $publishedBundlePath = Join-Path $bundleOutputRoot $bundleName

        Add-Content `
            -LiteralPath $buildLog `
            -Encoding UTF8 `
            -Value "[PFViewOutlineBundle] Published bundle: $bundleName"

        $bundleItem = Get-Item -LiteralPath $publishedBundlePath
        $hash = Get-FileHash `
            -Algorithm SHA256 `
            -LiteralPath $publishedBundlePath
        Write-Output (
            "Built {0} ({1} bytes, SHA256 {2})" -f
                $bundleName,
                $bundleItem.Length,
                $hash.Hash)
    }

    $publishedManifests = @(
        Get-ChildItem `
            -LiteralPath $bundleOutputRoot `
            -File `
            -Filter 'pfview_outline_*.manifest' `
            -ErrorAction SilentlyContinue)
    if ($publishedManifests.Count -ne 0)
    {
        throw 'PFView outline .manifest files were published unexpectedly.'
    }
}
finally
{
    if ($cleanOwnedProject -and
        (Test-Path -LiteralPath $resolvedProjectRoot))
    {
        $systemTemp = [System.IO.Path]::GetFullPath(
            [System.IO.Path]::GetTempPath())
        Remove-PFViewUnityTemporaryProject `
            -Path $resolvedProjectRoot `
            -AllowedRoot $systemTemp
    }
}
