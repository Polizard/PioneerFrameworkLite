# S1 A2 judge: "one frame, one name" (plan sections 11.1 A2 / 11.3 S1).
#
# WHY NOT grep: this host's bash/console encoding for CJK is unreliable -- measured `grep -c`
# returning 0 for a string that is actually present. So "grep to zero" is NOT decidable here.
# This script reads the files explicitly as UTF-8 and compares .NET strings => decidable.
#
# NOTE: this file is intentionally ASCII-only (same reason as tools/test-all.ps1):
# Windows PowerShell 5.1 reads BOM-less files as the ANSI codepage, which mangles CJK literals.
# The banned names are therefore built from code points.
#
# Usage (from repo root):
#   powershell -ExecutionPolicy Bypass -File tools/check-frame-names.ps1
# Exit code: 0 = clean (judge passes) / 1 = banned frame names still present (file:line listed).
#
# Banned = the 5 alternative names for the SAME frame (system frame) that section 11.1 A2 calls out.
# The single legal name is **system frame** (section 11.2 vocabulary).
# Excluded: Source/<archived>/ (history, kept as-is) and any bin/ or obj/ (build output).

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function U([int[]]$cp) { -join ($cp | ForEach-Object { [char]$_ }) }

$banned = @(
    (U @(0x7FA4, 0x7EC4, 0x7CFB)),                          # "group frame"
    (U @(0x7FA4, 0x7EC4, 0x5750, 0x6807, 0x7CFB)),          # "group coordinate frame"
    (U @(0x884C, 0x661F, 0x53C2, 0x8003, 0x7CFB)),          # "planet reference frame"
    (U @(0x6839, 0x7CFB)),                                  # "root frame"
    (U @(0x5730, 0x5FC3, 0x7CFB))                           # "geocentric frame"
)
$archivedDir = U @(0x5DF2, 0x5F03, 0x7F6E)                  # the retired-code directory name

$files = Get-ChildItem -Path (Join-Path $root 'Source') -Recurse -Filter *.cs |
    Where-Object {
        $_.FullName -notmatch '\\bin\\' -and
        $_.FullName -notmatch '\\obj\\' -and
        $_.FullName -notlike ("*\{0}\*" -f $archivedDir)
    }

$hits = 0
foreach ($f in $files) {
    $lines = [System.IO.File]::ReadAllLines($f.FullName, [System.Text.Encoding]::UTF8)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        for ($b = 0; $b -lt $banned.Count; $b++) {
            if ($lines[$i].Contains($banned[$b])) {
                $hits++
                $rel = $f.FullName.Substring($root.Length + 1)
                Write-Host ("{0}({1}): banned frame name index {2}" -f $rel, ($i + 1), $b)
            }
        }
    }
}

if ($hits -eq 0) {
    Write-Host ("A2 PASS: banned frame names appear 0 times in {0} .cs files." -f $files.Count)
    exit 0
} else {
    Write-Host ("A2 FAIL: {0} occurrence(s) of banned frame names (legal name = system frame)." -f $hits)
    exit 1
}
