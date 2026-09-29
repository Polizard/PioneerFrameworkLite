# S1 helper: enumerate OUR identifiers that contain world/unity/local (case-insensitive).
#
# WHY a tool instead of ad-hoc greps: my hand-written regexes failed THREE times in one session,
# each time silently (a) case-sensitive patterns missed lowercase spellings (`worldPos`),
# (b) prefix-requiring patterns missed keyword-initial names (`localStart`). Each hole produced a
# wrong "how much is left" number. This tool does the substring match properly and reports counts.
#
# ASCII-only on purpose (Windows PowerShell 5.1 reads BOM-less files as ANSI).
#
# Usage (from repo root):
#   powershell -ExecutionPolicy Bypass -File tools/list-frame-identifiers.ps1 [-Top 60]
# Output: identifier / occurrences / files, plus a summary line.
# Excluded: Source/<archived>/, any bin/ or obj/, and ThirdParty/ (upstream code keeps its names).

param([int]$Top = 60)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$archivedDir = -join ([char]0x5DF2, [char]0x5F03, [char]0x7F6E)   # retired-code directory name

$files = Get-ChildItem -Path (Join-Path $root 'Source') -Recurse -Filter *.cs |
    Where-Object {
        $_.FullName -notmatch '\\bin\\' -and
        $_.FullName -notmatch '\\obj\\' -and
        $_.FullName -notmatch '\\ThirdParty\\' -and
        $_.FullName -notlike ("*\{0}\*" -f $archivedDir)
    }

# Identifier = a run of word chars containing world/unity/local (any case), anywhere in it.
$idRe = New-Object System.Text.RegularExpressions.Regex('\b\w*(?i:world|unity|local)\w*\b')
$stats = @{}

foreach ($f in $files) {
    foreach ($line in [System.IO.File]::ReadAllLines($f.FullName, [System.Text.Encoding]::UTF8)) {
        foreach ($m in $idRe.Matches($line)) {
            $id = $m.Value
            if (-not $stats.ContainsKey($id)) { $stats[$id] = @{ n = 0; files = @{} } }
            $stats[$id].n++
            $stats[$id].files[$f.Name] = $true
        }
    }
}

$total = 0
$stats.GetEnumerator() | ForEach-Object { $total += $_.Value.n }
Write-Host ("Frame-marked identifiers (case-insensitive substring match): {0} unique / {1} occurrences in {2} .cs files" -f $stats.Count, $total, $files.Count)
Write-Host ""
Write-Host ("{0,5}  {1,4}  {2}" -f 'count', 'files', 'identifier')
$stats.GetEnumerator() |
    Sort-Object { -$_.Value.n } |
    Select-Object -First $Top |
    ForEach-Object {
        Write-Host ("{0,5}  {1,4}  {2}" -f $_.Value.n, $_.Value.files.Count, $_.Key)
    }
exit 0
