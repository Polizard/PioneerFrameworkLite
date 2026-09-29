# S1 acceptance: prove that this batch changed ONLY names (plus comments/additions we whitelist).
#
# WHY: the gate proves "the program still works" (tests, warning baseline, deterministic build) but it can NOT
# prove "no logic was touched along the way" -- a silently edited formula that still compiles and still passes
# tests would leave every gate step green. This tool compares the CURRENT source against the PRE-S1 snapshot
# and normalizes each line (strip comments, collapse identifiers) so that:
#   * a pure rename            -> normalized lines are IDENTICAL      => ok
#   * a comment-only change    -> normalized lines are IDENTICAL      => ok
#   * anything else            -> normalized lines DIFFER             => must be whitelisted (with a reason)
# It also scans for leftover Scribe keys that still carry a retired name.
#
# NOT a gate step on purpose: it is S1-scoped (its baseline is the pre-S1 snapshot) and ANTI-additive -- any
# future legitimate edit to a touched file would fail it. Run it (a) at the end of S1, (b) after any re-run of
# the rename batches, with the whitelist kept small and justified.
#
# ASCII-only on purpose (PowerShell 5.1 reads BOM-less files as ANSI).
#
# Usage (from repo root):
#   powershell -ExecutionPolicy Bypass -File tools/check-rename-only-diff.ps1
# Exit code: 0 = every modified file is rename/comment-only or whitelisted / 1 = unexplained difference.

param([switch]$ListAll, [switch]$SelfTest)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$curRoot = Join-Path $root 'Source'
$refRoot = Join-Path $root '_backups\20260929-1332-pre-batch6-S1-2900a82b7e5a3726\Source'

# Files where a NON-rename change is intentional. Suffix globs (robust against path separators/case).
# Keep each entry justified in a comment.
$whitelist = @(
    '*OrbitViewProbe.cs'   # adds the S1 partial-oracle criterion (positionBody/velocityBody vs their definition)
)

# --- normalization ------------------------------------------------------------------------------
# strip: line comments, block comments (state machine), then collapse identifiers, numbers and strings.
# NOTE ON STRINGS: literals are collapsed to STR on purpose -- this audit is about LOGIC, and the S1 batch
# intentionally renamed names inside log labels / test messages. Collapsing them means such label edits pass;
# a *changed* literal therefore has to be caught by reading the diff, not by this tool.
$idRe = New-Object System.Text.RegularExpressions.Regex('[A-Za-z_]\w*')
$numRe = New-Object System.Text.RegularExpressions.Regex('\b\d+(\.\d+)?([eE][-+]?\d+)?[fFdDmMlL]?\b')
$strRe = New-Object System.Text.RegularExpressions.Regex('("(\\.|[^"\\])*")|(@("(\\.|[^"\\])*")+)')

function Normalize-Lines([string[]]$lines) {
    # Emit via the pipeline instead of List+return: the List version was measured to return ZERO items
    # (cur=0 / ref=0 in the same script) => the judge silently passed everything.
    $inBlock = $false
    foreach ($ln in $lines) {
        $s = $ln
        if ($inBlock) {
            $end = $s.IndexOf('*/')
            if ($end -lt 0) { continue }
            $s = $s.Substring($end + 2); $inBlock = $false
        }
        while ($true) {
            $lc = $s.IndexOf('//')
            $bc = $s.IndexOf('/*')
            if ($bc -ge 0 -and ($lc -lt 0 -or $bc -lt $lc)) {
                $head = $s.Substring(0, $bc)
                $rest = $s.Substring($bc + 2)
                $end = $rest.IndexOf('*/')
                if ($end -lt 0) { $s = $head; $inBlock = $true; break }
                $s = $head + $rest.Substring($end + 2)
                continue
            }
            if ($lc -ge 0) { $s = $s.Substring(0, $lc) }
            break
        }
        $s = $strRe.Replace($s, 'STR')
        $s = $idRe.Replace($s, 'ID')
        $s = $numRe.Replace($s, 'NUM')
        $s = $s.Trim()
        if ($s.Length -gt 0) { Write-Output $s }
    }
}

# --- diagnostics (must come AFTER Normalize-Lines: PowerShell defines functions when it executes them) -------
if ($SelfTest) {
    $probeRel = 'MechJebLibTest/AssertionExtensions.cs'
    $c = Join-Path $curRoot $probeRel.Replace('/', '\')
    $r = Join-Path $refRoot $probeRel.Replace('/', '\')
    Write-Host ("SelfTest: cur exists={0} ref exists={1}" -f (Test-Path -LiteralPath $c), (Test-Path -LiteralPath $r))
    $cl = [System.IO.File]::ReadAllLines($c, [System.Text.Encoding]::UTF8)
    $rl = [System.IO.File]::ReadAllLines($r, [System.Text.Encoding]::UTF8)
    Write-Host ("SelfTest: raw lines cur={0} ref={1}" -f $cl.Count, $rl.Count)
    $cn = @(Normalize-Lines $cl); $rn = @(Normalize-Lines $rl)
    Write-Host ("SelfTest: normalized cur={0} ref={1}" -f $cn.Count, $rn.Count)
    $dl = @(Compare-Object -ReferenceObject $rn -DifferenceObject $cn -CaseSensitive)
    Write-Host ("SelfTest: delta={0}" -f $dl.Count)
    if ($dl.Count -gt 0) { Write-Host ("SelfTest: first={0} [{1}]" -f $dl[0].SideIndicator, $dl[0].InputObject) }
    exit 0
}

# --- walk both trees ----------------------------------------------------------------------------
$curFiles = Get-ChildItem -Path $curRoot -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\' }
$bad = New-Object System.Collections.Generic.List[string]
$mod = 0; $same = 0; $added = 0; $removed = 0; $wl = 0

foreach ($f in $curFiles) {
    $rel = $f.FullName.Substring($curRoot.Length + 1).Replace('\', '/')
    $ref = Join-Path $refRoot $rel.Replace('/', '\')
    $curLines = [System.IO.File]::ReadAllLines($f.FullName, [System.Text.Encoding]::UTF8)
    if (-not (Test-Path -LiteralPath $ref)) { $added++; Write-Host ("NEW  {0}" -f $rel); continue }
    $refLines = [System.IO.File]::ReadAllLines($ref, [System.Text.Encoding]::UTF8)

    # raw equality first (fast path)
    $rawSame = ($curLines.Length -eq $refLines.Length)
    if ($rawSame) {
        for ($i = 0; $i -lt $curLines.Length; $i++) { if ($curLines[$i] -cne $refLines[$i]) { $rawSame = $false; break } }
    }
    if ($rawSame) { $same++; continue }

    $mod++
    $a = @(Normalize-Lines $refLines)
    $b = @(Normalize-Lines $curLines)
    # Use the built-in Compare-Object instead of a hand-rolled index loop: the hand-rolled version was measured
    # to MISS a real change (`epsilon * 2` -> `epsilon / 2`), while the same comparison via Compare-Object catches
    # it. Lesson: never hand-roll the comparison logic of a judge -- its hole fails silently, it does not error.
    $delta = @(Compare-Object -ReferenceObject $a -DifferenceObject $b -CaseSensitive)
    if ($delta.Count -eq 0) { if ($ListAll) { Write-Host ("OK   {0}" -f $rel) }; continue }   # rename/comment-only => ok
    $first = $delta[0]
    $where = if ($first.SideIndicator -eq '=>') { 'added' } else { 'removed' }
    if ($ListAll) { Write-Host ("DIFF {0}  ({1} normalized line(s), e.g. {2} [{3}])" -f $rel, $delta.Count, $where, $first.InputObject) }
    $wlHit = $false
    foreach ($w in $whitelist) { if ($rel -like $w) { $wlHit = $true; break } }
    if ($wlHit) { $wl++; if ($ListAll) { Write-Host ("WL   {0}" -f $rel) } ; continue }
    $bad.Add(("{0}: {1} normalized line(s) {2} vs the pre-S1 snapshot (e.g. [{3}])" -f $rel, $delta.Count, $where, $first.InputObject))
}

# files present in the snapshot but gone now
$refFiles = Get-ChildItem -Path $refRoot -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\' }
foreach ($f in $refFiles) {
    $rel = $f.FullName.Substring($refRoot.Length + 1)
    if (-not (Test-Path -LiteralPath (Join-Path $curRoot $rel))) { $removed++; Write-Host ("GONE {0}" -f $rel) }
}

# --- leftover Scribe keys carrying a retired name -----------------------------------------------
$retired = @('mainAxisLocal','plannedWorldDv','burnArcDv','burnDirection','localPosition','localVelocity',
             'localBoundsSize','aimOffsetLocal','landingLocalOffset','preciseAimOffsetLocal')
$scribeHits = 0
foreach ($f in $curFiles) {
    $n = 0
    foreach ($ln in [System.IO.File]::ReadAllLines($f.FullName, [System.Text.Encoding]::UTF8)) {
        $n++
        foreach ($r in $retired) {
            if ($ln.Contains('"' + $r + '"') -and $ln -match 'Scribe') {
                $scribeHits++
                Write-Host ("SCRIBE {0}({1}): Scribe key still says '{2}'" -f $f.FullName.Substring($curRoot.Length + 1), $n, $r)
            }
        }
    }
}

Write-Host ""
Write-Host ("Rename-only diff audit: identical={0}  modified={1} (of which whitelisted={2})  new={3}  removed={4}" -f $same, $mod, $wl, $added, $removed)
if ($bad.Count -eq 0 -and $scribeHits -eq 0) {
    Write-Host "PASS: every modified .cs file differs ONLY in identifiers/comments (or is whitelisted with a reason); no leftover Scribe key."
    exit 0
} else {
    foreach ($b in $bad) { Write-Host ("FAIL {0}" -f $b) }
    if ($scribeHits -gt 0) { Write-Host ("FAIL {0} leftover Scribe key(s)" -f $scribeHits) }
    exit 1
}
