# One-click verification for PioneerFrameworkLite (mirrors .github/workflows/ci.yml locally).
#
# Steps, in this order:
#   1. Build (Debug, full rebuild)                  -> 0 errors, unique warning count reported
#   2. Pure math tests (net48)                      -> Source/PioneerFramework.Pure.Tests
#   3. Game integration tests (net48)               -> Source/PioneerFramework.Tests
#   3b. Ported MechJebLib tests (net48)             -> Source/MechJebLibTest (third-party 移植面)
#   4. Def/XML validation                           -> python tools/validate_config.py
#   5. Committed runtime assets present
#   6. Deterministic Release build                  -> two builds, identical DLL bytes
#   7. Rebuild committed DLL (Release)              -> restores 1.6/Assemblies/PioneerFrameworkLite.dll
#   8. Verify committed DLL matches Release build
#
# WHY the DLL steps are LAST (and WARN-marked when skipped): the DLL is a committed artifact that
# CI hash-checks, so the script leaves the tree with a *Release* DLL built from the current source.
# D2(2026-09-14): the old reason -- "OutputPath points at 1.6/Assemblies for BOTH configurations,
# so any Debug build (incl. dotnet test) silently overwrites the committed DLL" -- no longer
# applies: PioneerFramework.csproj now sends Debug output to bin/, so a Debug build cannot touch
# the committed artifact at all. The ordering is kept so the script still ends in a verified state.
# Use -NoDllRebuild to verify WITHOUT touching the DLL (then the hash check reports repo as-is).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/test-all.ps1
#   powershell -ExecutionPolicy Bypass -File tools/test-all.ps1 -SkipConfig -SkipDeterminism
#   powershell -ExecutionPolicy Bypass -File tools/test-all.ps1 -NoDllRebuild
#   powershell -ExecutionPolicy Bypass -File tools/test-all.ps1 -WarningDiff   # + diff warning classes vs HEAD
#
# Exit code: 0 = all steps passed, 1 = at least one step failed.
#
# NOTE 1: this file is intentionally ASCII-only -- Windows PowerShell 5.1 reads BOM-less files
#         as ANSI, which would garble any non-ASCII literal (including inside regexes).
# NOTE 2: in-game acceptance probes (PerfProbe Gizmo) and the combat scenario regressions are
#         manual and NOT covered here -- see the performance TODO doc.

param(
    [switch]$SkipConfig,
    [switch]$SkipDeterminism,
    [switch]$NoDllRebuild,
    [switch]$WarningDiff
)

$ErrorActionPreference = "Stop"
# Force English CLI output: keeps parsing locale-independent and produces the same numbers as CI
# (which runs on English runners). Also set the console encoding since dotnet emits UTF-8.
$env:DOTNET_CLI_UI_LANGUAGE = "en"
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

$script:results = New-Object System.Collections.ArrayList

# D4(2026-09-14): a step that COULD NOT RUN (missing tool / skipped by switch) must not be
# reported as PASS -- that is a silent false green. Warn() records it as its own state:
# the summary lists it explicitly, the exit code stays 0 (it is "unchecked", not "failed"),
# and the caller can see exactly which steps were not verified.
function Warn {
    param([string]$Name, [string]$Why)
    Write-Host ""
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    Write-Host "WARN  $Name  ->  $Why" -ForegroundColor Yellow
    [void]$script:results.Add([pscustomobject]@{ Name = $Name; Ok = $true; Warn = $true; Detail = "UNCHECKED: $Why"; Seconds = 0.0 })
}

function Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host ""
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $detail = & $Body
        $sw.Stop()
        if (-not $detail) { $detail = "ok" }
        # collapse whitespace + truncate: nested scripts leak their build output into the pipeline
        $detail = ("$detail" -replace '\s+', ' ').Trim()
        if ($detail.Length -gt 160) { $detail = $detail.Substring(0, 160) + " ..." }
        Write-Host "PASS  $Name  ($([math]::Round($sw.Elapsed.TotalSeconds, 1))s)  $detail" -ForegroundColor Green
        [void]$script:results.Add([pscustomobject]@{ Name = $Name; Ok = $true; Warn = $false; Detail = "$detail"; Seconds = $sw.Elapsed.TotalSeconds })
    }
    catch {
        $sw.Stop()
        Write-Host "FAIL  $Name  ->  $($_.Exception.Message)" -ForegroundColor Red
        [void]$script:results.Add([pscustomobject]@{ Name = $Name; Ok = $false; Warn = $false; Detail = "$($_.Exception.Message)"; Seconds = $sw.Elapsed.TotalSeconds })
    }
}

# Runs a native command and captures stdout+stderr as text.
# WHY the preference dance: Windows PowerShell 5.1 with $ErrorActionPreference='Stop' turns ANY
# native stderr write into a terminating NativeCommandError -- and tools like git write normal
# progress output to stderr, so a perfectly successful `git worktree add` would throw here.
function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $code = 0
    try {
        $output = & $Exe @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $prev
    }
    return [pscustomobject]@{ ExitCode = $code; Output = @($output) }
}

function Invoke-Dotnet {
    param([string[]]$Arguments)
    $r = Invoke-Native 'dotnet' $Arguments
    if ($r.ExitCode -ne 0) {
        $tail = ($r.Output | Select-Object -Last 25) -join "`n"
        throw "dotnet $($Arguments -join ' ') failed (exit $($r.ExitCode))`n$tail"
    }
    return @($r.Output)
}

# Reads MSBuild's own total from the last "N Warning(s)" summary line (English forced above),
# so the number matches what the build prints and what CI reports.
function Get-CsWarningCount {
    param([string[]]$BuildOutput)
    $text = $BuildOutput -join "`n"
    $hits = [regex]::Matches($text, '(\d+)\s+Warning\(s\)')
    if ($hits.Count -gt 0) { return [int]$hits[$hits.Count - 1].Groups[1].Value }
    # D4(2026-09-14): -1 = could not parse (NOT "zero warnings"). The old version returned 0
    # silently, disguising "regex did not match / build output format changed" as "no warnings".
    return -1
}

# VSTest prints one summary line ending with "... <assembly>.dll (net8.0)" (locale-independent).
function Get-TestSummary {
    param([string[]]$TestOutput)
    for ($i = $TestOutput.Count - 1; $i -ge 0; $i--) {
        if ("$($TestOutput[$i])" -match '\.dll \(net') { return "$($TestOutput[$i])".Trim() }
    }
    return "see log"
}

# Distinct warning KINDS (CSxxxx), used by -WarningDiff. Deliberately file-name-agnostic:
# a renamed source file would otherwise look like a brand-new warning class (real false positive
# hit on 2026-09-10 when CompDebugShipSpawner.cs became DebugShipSpawner.cs).
# Sensitivity to "more warnings of an existing kind" comes from comparing the totals as well.
function Get-WarningCodes {
    param([string[]]$BuildOutput)
    $set = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($line in $BuildOutput) {
        $m = [regex]::Match($line, '\(\d+,\d+\): warning (CS\d+)')
        if ($m.Success) { [void]$set.Add($m.Groups[1].Value) }
    }
    return @($set | Sort-Object)
}

# -- 1. Debug build -----------------------------------------------------------
Step "Build (Debug, full rebuild)" {
    $out = Invoke-Dotnet @("build", "Source/PioneerFramework/PioneerFramework.csproj", "-c", "Debug", "-t:Rebuild", "-v:minimal")
    return "0 errors, $(Get-CsWarningCount $out) unique warnings"
}

# -- 2. Pure math tests -------------------------------------------------------
Step "Pure math tests (net48)" {
    $out = Invoke-Dotnet @("test", "Source/PioneerFramework.Pure.Tests/PioneerFramework.Pure.Tests.csproj", "-v:minimal")
    return (Get-TestSummary $out)
}

# -- 3. Game integration tests ------------------------------------------------
Step "Game integration tests (net48)" {
    $out = Invoke-Dotnet @("test", "Source/PioneerFramework.Tests/PioneerFramework.Tests.csproj", "-v:minimal")
    return (Get-TestSummary $out)
}

# -- 3b. 第三方移植物：上游 MechJebLib 测试套 ---------------------------------
# net48、零 RimWorld/Unity 依赖；它验收的是 ThirdParty/MechJebLib/ 下的移植面本身，
# 而不是我们的代码，所以单独一个桶 —— 红了就能一眼看出是哪一侧的问题。
Step "Ported MechJebLib tests (net48)" {
    $out = Invoke-Dotnet @("test", "Source/MechJebLibTest/MechJebLibTest.csproj", "-v:minimal")
    return (Get-TestSummary $out)
}

# -- 4. Def / XML validation --------------------------------------------------
$skipConfig = $SkipConfig
$py = $null
if (-not $skipConfig) {
    $py = Get-Command python -ErrorAction SilentlyContinue
    if (-not $py) { $py = Get-Command py -ErrorAction SilentlyContinue }
}
if ($skipConfig) {
    Warn "Validate Defs/XML (tools/validate_config.py)" "UNCHECKED: -SkipConfig"
}
elseif (-not $py) {
    Warn "Validate Defs/XML (tools/validate_config.py)" "UNCHECKED: no local python (CI covers this step)"
}
else {
Step "Validate Defs/XML (tools/validate_config.py)" {
    $r = Invoke-Native $py.Source @("tools/validate_config.py")
    if ($r.ExitCode -ne 0) {
        throw "validate_config.py failed (exit $($r.ExitCode))`n$(($r.Output | Select-Object -Last 15) -join "`n")"
    }
    return "ok"
}
}

# -- 5. Committed runtime assets ----------------------------------------------
Step "Committed runtime assets present" {
    $required = @(
        "1.6/Assemblies/PioneerFrameworkLite.dll",
        "1.6/AssetBundles/pfview_outline_win",
        "1.6/AssetBundles/pfview_outline_mac",
        "1.6/AssetBundles/pfview_outline_linux",
        "Shaders/PFView_OutlineMask.shader",
        "Shaders/PFView_OutlineDilate.shader",
        "Shaders/PFView_OutlineComposite.shader"
    )
    $missing = @()
    foreach ($path in $required) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $missing += $path }
    }
    if ($missing.Count -gt 0) { throw "missing: $($missing -join ', ')" }
    return "$($required.Count) files"
}

# -- 6. Deterministic Release build -------------------------------------------
if ($SkipDeterminism) {
    Warn "Deterministic Release build (two builds, identical bytes)" "UNCHECKED: -SkipDeterminism"
}
else {
Step "Deterministic Release build (two builds, identical bytes)" {
    $log = & (Join-Path $PSScriptRoot "verify-deterministic.ps1") -Configuration Release 2>&1
    if ($LASTEXITCODE -ne 0) { throw "verify-deterministic.ps1 failed`n$(($log | Select-Object -Last 8) -join "`n")" }
    return "two Release builds produced identical bytes"
}
}

# -- 7. Rebuild committed DLL (Release) ---------------------------------------
if ($NoDllRebuild) {
    Warn "Rebuild committed DLL (Release)" "UNCHECKED: -NoDllRebuild (the hash check below then reflects the repo as-is)"
}
else {
Step "Rebuild committed DLL (Release)" {
    $log = & (Join-Path $PSScriptRoot "rebuild-dll.ps1") -Configuration Release 2>&1
    if ($LASTEXITCODE -ne 0) { throw "rebuild-dll.ps1 failed`n$(($log | Select-Object -Last 8) -join "`n")" }
    return "1.6/Assemblies/PioneerFrameworkLite.dll refreshed"
}
}

# -- 8. Verify committed DLL matches a fresh Release build --------------------
Step "Committed DLL matches Release build" {
    # *>&1 (not 2>&1): the check script reports the hashes via Write-Host, which lands on the
    # information stream -- capturing only stderr/stdout would lose them.
    $out = & (Join-Path $PSScriptRoot "check-committed-dll.ps1") -Configuration Release *>&1
    $text = @($out) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        # Compact failure detail: just the two hashes, not the whole build log.
        $committed = [regex]::Match($text, 'committed=([0-9A-F]{64})')
        $built = [regex]::Match($text, 'built\s*=\s*([0-9A-F]{64})')
        if ($committed.Success -and $built.Success) {
            throw "DLL mismatch: committed=$($committed.Groups[1].Value.Substring(0, 16))... built=$($built.Groups[1].Value.Substring(0, 16))... (a Debug build overwrote the committed DLL -> rerun without -NoDllRebuild)"
        }
        throw "DLL mismatch: the file in 1.6/Assemblies does not match a fresh Release build (a Debug build overwrote it? rerun without -NoDllRebuild)"
    }
    $m = [regex]::Match($text, '([0-9A-F]{64})')
    if ($m.Success) { return "hash $($m.Groups[1].Value.Substring(0, 16))..." }
    return "ok"
}

# -- 9. Optional: warning diff vs HEAD (catches newly introduced warnings) ----
if ($WarningDiff) {
    Step "Warning diff vs HEAD" {
        $worktree = Join-Path $env:TEMP "pfl_warn_head"
        try {
            # Idempotent start: drop any leftover registration from an interrupted earlier run.
            Invoke-Native 'git' @("worktree", "unlock", $worktree) | Out-Null
            Invoke-Native 'git' @("worktree", "remove", $worktree, "--force") | Out-Null
            Invoke-Native 'git' @("worktree", "prune") | Out-Null
            Remove-Item -Recurse -Force $worktree -ErrorAction SilentlyContinue

            $add = Invoke-Native 'git' @("worktree", "add", "-f", $worktree, "HEAD")
            if ($add.ExitCode -ne 0) {
                throw "git worktree add failed (exit $($add.ExitCode)): $(($add.Output | Select-Object -Last 5) -join ' ')"
            }

            # HEAD build needs the tracked Libs/*.dll (they ARE tracked -- a bare worktree can build).
            $headOut = Invoke-Dotnet @("build", "$worktree/Source/PioneerFramework/PioneerFramework.csproj", "-c", "Debug", "-t:Rebuild", "-v:minimal")
            $nowOut = Invoke-Dotnet @("build", "Source/PioneerFramework/PioneerFramework.csproj", "-c", "Debug", "-t:Rebuild", "-v:minimal")

            $headCodes = Get-WarningCodes $headOut
            $nowCodes = Get-WarningCodes $nowOut
            $headTotal = Get-CsWarningCount $headOut
            $nowTotal = Get-CsWarningCount $nowOut
            $added = @(Compare-Object -ReferenceObject $headCodes -DifferenceObject $nowCodes | Where-Object { $_.SideIndicator -eq '=>' })

            if ($added.Count -gt 0) {
                throw "new warning KINDS vs HEAD: $($added.InputObject -join ' | ')"
            }
            if ($nowTotal -gt $headTotal) {
                throw "warning count grew vs HEAD: $headTotal -> $nowTotal (more warnings of existing kinds)"
            }
            return "$headTotal -> $nowTotal warnings, $($headCodes.Count) kinds, none added"
        }
        finally {
            # Cleanup must never mask the real failure above -> no throw, preference relaxed.
            $prev = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            Invoke-Native 'git' @("worktree", "unlock", $worktree) | Out-Null
            Invoke-Native 'git' @("worktree", "remove", $worktree, "--force") | Out-Null
            Invoke-Native 'git' @("worktree", "prune") | Out-Null
            Remove-Item -Recurse -Force $worktree -ErrorAction SilentlyContinue
            $ErrorActionPreference = $prev
        }
    }
}

# -- Summary ------------------------------------------------------------------
Write-Host ""
Write-Host "================ SUMMARY ================" -ForegroundColor Cyan
foreach ($r in $script:results) {
    $mark = if ($r.Ok) { "[PASS]" } else { "[FAIL]" }
    $color = if ($r.Ok) { "Green" } else { "Red" }
    $secs = "{0,6:N1}s" -f $r.Seconds
    Write-Host "$mark $($r.Name.PadRight(52))$secs  $($r.Detail)" -ForegroundColor $color
}

$failed = @($script:results | Where-Object { -not $_.Ok })
$warned = @($script:results | Where-Object { $_.Ok -and $_.Warn })
Write-Host ""
if ($warned.Count -gt 0) {
    Write-Host "$($warned.Count) step(s) NOT CHECKED (see WARN above):" -ForegroundColor Yellow
    foreach ($w in $warned) { Write-Host "  - $($w.Name)" -ForegroundColor Yellow }
    Write-Host ""
}
if ($failed.Count -eq 0) {
    Write-Host "ALL GREEN ($($script:results.Count) steps$([string]::Format(', {0} unchecked', $warned.Count)))" -ForegroundColor Green
    Write-Host "Reminder: in-game acceptance needs the game running -- dev mode -> Debug menu -> [PFL Test] tab" -ForegroundColor DarkGray
    Write-Host "          -> [One-click acceptance] (auto-deploys 5 scenarios, prints a verdict block)." -ForegroundColor DarkGray
    Pop-Location
    exit 0
}
else {
    Write-Host "$($failed.Count) of $($script:results.Count) step(s) FAILED" -ForegroundColor Red
    Pop-Location
    exit 1
}
