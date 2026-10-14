# Freeze guard for the 1.6 line (2026-10-13 tree split).
#
# WHAT IS FROZEN (user ruling: "1.6 we do not touch any more; everything moves to 1.7"):
#   - 1.6/Assemblies/PioneerFrameworkLite.dll must keep the EXACT bytes recorded below;
#   - the 1.6 runtime assets shipped with it must stay present;
#   - git tag `1.6-final` anchors the authoritative source snapshot (legacy/1.6/Source is a
#     local-only working copy for side-by-side reference; it is git-ignored on purpose).
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

# Freeze anchor tag (best effort -- needs a git repo; a missing tag is a failure, not a warning).
$prev = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$tag = & git -C $root tag -l "1.6-final" 2>$null
$ErrorActionPreference = $prev
if ("$tag".Trim() -eq "1.6-final") { Ok "freeze anchor tag 1.6-final exists" }
else { Fail "freeze anchor tag 1.6-final not found (create it on the last 1.6 commit)" }

if ($script:fail -gt 0) {
    Write-Host "FREEZE VIOLATED ($script:fail issue(s))" -ForegroundColor Red
    exit 1
}
Write-Host "Frozen 1.6 line intact." -ForegroundColor Green
exit 0
