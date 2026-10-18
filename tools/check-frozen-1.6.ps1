# Freeze guard for the 1.6 line (2026-10-13 tree split).
#
# WHAT IS FROZEN (user ruling: "1.6 we do not touch any more; everything moves to 1.7"):
#   - 1.6/Assemblies/PioneerFrameworkLite.dll must keep the EXACT bytes recorded below;
#   - the 1.6 runtime assets shipped with it must stay present;
#   - the immutable last 1.6 commit anchors the authoritative source snapshot; the optional
#     `1.6-final` tag must resolve to it when available. CI fetches full history.
# WHY this script exists: "nobody touches 1.6" is a promise; this is its mechanical judge.
# If a legitimate change is ever needed, update the pinned hash IN THE SAME COMMIT and say why.
#
# ASCII-only on purpose (Windows PowerShell 5.1 reads BOM-less files as ANSI).
#
# Usage: powershell -ExecutionPolicy Bypass -File tools/check-frozen-1.6.ps1
# Exit code: 0 = intact / 1 = violated.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$script:fail = 0
function Fail([string]$m) { Write-Host "FAIL  $m" -ForegroundColor Red; $script:fail++ }
function Ok([string]$m)   { Write-Host "ok    $m" -ForegroundColor Green }

# Pinned 2026-10-13 (recorded before the tree split; verified unchanged after it).
$pinnedHash = "3C9B3812528C44450AD67A1E22839D2EAA75B2774B336BB64B84D0660B1F62F8"
$pinnedCommit = "507eddea84f190f017e22461fbdba48d2a2f72b2"
$pinnedBlob = "c2cf88844ea7fa73f7f8c569167a30d7c235a726"

$dll = Join-Path $root "1.6/Assemblies/PioneerFrameworkLite.dll"
if (-not (Test-Path -LiteralPath $dll)) {
    Fail "frozen artifact missing: 1.6/Assemblies/PioneerFrameworkLite.dll"
}
else {
    $h = (Get-FileHash -LiteralPath $dll).Hash
    if ($h -ne $pinnedHash) {
        Fail "frozen artifact CHANGED: 1.6/Assemblies/PioneerFrameworkLite.dll"
        Write-Host "      pinned = $pinnedHash" -ForegroundColor Yellow
        Write-Host "      actual = $h" -ForegroundColor Yellow
    }
    else {
        Ok "frozen DLL intact ($($h.Substring(0,16))...)"
    }
}

$assets = @(
    "1.6/AssetBundles/pfview_outline_win",
    "1.6/AssetBundles/pfview_outline_mac",
    "1.6/AssetBundles/pfview_outline_linux",
    "Shaders/PFView_OutlineMask.shader",
    "Shaders/PFView_OutlineDilate.shader",
    "Shaders/PFView_OutlineComposite.shader"
)
$missing = @()
foreach ($rel in $assets) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $rel) -PathType Leaf)) { $missing += $rel }
}
if ($missing.Count -gt 0) { Fail "frozen asset(s) missing: $($missing -join ', ')" }
else { Ok "frozen 1.6 assets present ($($assets.Count) files)" }

# A checkout can omit tags. Pin the source commit and its DLL blob as well as the file hash,
# rather than creating a tag locally or treating a missing tag as proof of changed assets.
$prev = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$anchorBlob = & git -C $root rev-parse "${pinnedCommit}:1.6/Assemblies/PioneerFrameworkLite.dll" 2>$null
$blobCode = $LASTEXITCODE
& git -C $root merge-base --is-ancestor $pinnedCommit HEAD 2>$null
$ancestorCode = $LASTEXITCODE
$tag = & git -C $root rev-parse --verify --quiet 'refs/tags/1.6-final^{commit}' 2>$null
$tagCode = $LASTEXITCODE
$ErrorActionPreference = $prev
if ($blobCode -ne 0 -or $ancestorCode -ne 0 -or "$anchorBlob".Trim() -ne $pinnedBlob) {
    Fail "frozen source anchor missing or changed (fetch full git history)"
}
elseif ($tagCode -eq 0 -and "$tag".Trim() -ne $pinnedCommit) {
    Fail "freeze anchor tag 1.6-final points to another commit"
}
else { Ok "frozen source commit $($pinnedCommit.Substring(0,12)) and DLL blob verified" }

if ($script:fail -gt 0) {
    Write-Host "FREEZE VIOLATED ($script:fail issue(s))" -ForegroundColor Red
    exit 1
}
Write-Host "Frozen 1.6 line intact." -ForegroundColor Green
exit 0
