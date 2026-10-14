# Check whether committed 1.7/Assemblies/PioneerFrameworkLite.dll matches a fresh Release build.
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/check-committed-dll.ps1
#   powershell -ExecutionPolicy Bypass -File tools/check-committed-dll.ps1 -Configuration Debug
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'Use-ProjectDotnet.ps1')
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $env:TEMP "pfl_check"
$committedDll = Join-Path $root "1.7/Assemblies/PioneerFrameworkLite.dll"

# WHY a diagnosis (2026-10-01): a DLL's bytes depend on the *compiler version*, and which SDK this
# machine uses is decided by global.json's `rollForward` (so it can be NEWER than CI's), while CI pins
# one exact version in .github/workflows/ci.yml. Measured on this repo: same source, SDK 9.0.200 vs
# 8.0.100 -> 796,350 of 1,064,448 bytes differ (a whole-layout shift, not just a version string).
# So "mismatch" has two very different causes, and reporting them as one sends the reader down the
# wrong path (it read as "your artifact is stale" when the artifact was in fact the CI-invisible half):
#   1) the artifact is stale / a Debug build overwrote it      -> rebuild it
#   2) the artifact was built by a different compiler than CI's -> it is fine, just not CI's bytes
function Get-SdkVersion {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"   # a native stderr write must not throw inside try
    try {
        $lines = @(& dotnet --version 2>&1)
        if ($lines.Count -gt 0) { return "$($lines[0])".Trim() }
    }
    catch { }
    finally { $ErrorActionPreference = $prev }
    return "(unavailable)"
}

# Single source of truth for the pin: read it out of CI's workflow instead of copying the number here
# (two copies drift -- the very reason CI calls this script rather than inlining the check; see D3 in ci.yml).
function Get-CiPinnedSdk {
    $ci = Join-Path $root ".github/workflows/ci.yml"
    if (-not (Test-Path -LiteralPath $ci)) { return $null }
    $m = [regex]::Match((Get-Content -LiteralPath $ci -Raw), "dotnet-version:\s*'?([0-9]+\.[0-9]+\.[0-9]+)")
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

function Show-MismatchDiagnosis {
    $sdk = Get-SdkVersion
    $pin = Get-CiPinnedSdk
    Write-Host ""
    Write-Host "Two possible causes -- tell them apart before acting:"
    Write-Host "  1) stale artifact, or a Debug build overwrote it   -> run tools/rebuild-dll.ps1"
    Write-Host "  2) built by a different compiler than CI's         -> the artifact is fine, it is just not the bytes CI would produce"
    Write-Host "     local dotnet = $sdk ; CI pins = $(if ($pin) { $pin } else { '(not readable)' })"
    if ($pin -and $sdk -eq $pin) {
        Write-Host "     -> versions match here, so cause 1 (stale) is the likely one"
    }
    elseif ($pin) {
        Write-Host "     -> versions differ, so cause 2 is the likely one (same source, 796,350/1,064,448 bytes differ)"
        Write-Host "        fix: make PATH resolve to the CI SDK first, then rebuild the artifact (handoff 10.38)"
    }
}

Push-Location $root
try {
    Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
    dotnet build "Source/PioneerFramework/PioneerFramework.csproj" --configuration $Configuration -v:minimal -p:OutputPath=$out -p:AppendTargetFrameworkToOutputPath=false
    if ($LASTEXITCODE -ne 0) { throw "build failed" }

    $committedHash = (Get-FileHash $committedDll).Hash
    $builtHash = (Get-FileHash "$out/PioneerFrameworkLite.dll").Hash
    if ($committedHash -ne $builtHash) {
        Write-Host "Committed DLL differs from $Configuration build."
        Write-Host "committed=$committedHash"
        Write-Host "built    =$builtHash"
        Show-MismatchDiagnosis
        exit 1
    }
    $pinText = if ($pin = Get-CiPinnedSdk) { $pin } else { "unknown" }
    Write-Host "Committed DLL matches $Configuration build: $committedHash (dotnet $(Get-SdkVersion), CI pins $pinText)"
}
finally {
    Pop-Location
}
