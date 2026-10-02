# Frame-crossing auditor for batch 6 stage S3 ("types carry the frame").
#
# WHY this exists: S3-3c turned the *host* frames into types (`SystemVec3` / `BodyVec3` in
# `Base/Core/FrameVec3.cs`). The compiler then enforces same-frame arithmetic inside the typed
# part -- but two things it CANNOT see:
#   1. hand-written frame crossings (e.g. `CurrentOrbit.position - GetCentralBodyOffset(t)`),
#      which are exactly how the historical mixing accidents happened;
#   2. carriers that are still untyped ("pending"), which drift silently back into the code.
# This tool is the mechanical judge for both. It is deliberately ALLOW-LIST based: every known
# legitimate occurrence carries a written reason, anything *new* is a failure (no silent drift).
#
# ASCII-ONLY ON PURPOSE: Windows PowerShell 5.1 reads BOM-less files as ANSI, so any non-ASCII
# literal here (including inside regexes) would be garbled -- same rule as tools/test-all.ps1.
#
# Usage: powershell -ExecutionPolicy Bypass -File tools/check-frame-crossing.ps1
# Exit code: 0 = no drift, 1 = drift found.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

$script:fail = 0
function Fail([string]$msg) { Write-Host "FAIL  $msg" -ForegroundColor Red; $script:fail++ }
function Ok([string]$msg)   { Write-Host "ok    $msg" -ForegroundColor Green }
function Note([string]$msg) { Write-Host "      $msg" -ForegroundColor Gray }

# ---------------------------------------------------------------------------
# Collect all mod sources (excluding the archived / dead-code folder).
# ---------------------------------------------------------------------------
$sources = Get-ChildItem -Path "Source\PioneerFramework" -Filter *.cs -Recurse -File |
    Where-Object { $_.FullName -notmatch "\\bin\\" -and $_.FullName -notmatch "\\obj\\" }

function Get-Hits([string]$pattern) {
    $rx = [regex]$pattern
    $hits = @()
    foreach ($f in $sources) {
        $lines = [System.IO.File]::ReadAllLines($f.FullName)
        for ($i = 0; $i -lt $lines.Length; $i++) {
            $t = $lines[$i].Trim()
            # comments are prose, not code -- skip them (the gate must judge code, not documentation)
            if ($t.StartsWith("//") -or $t.StartsWith("*") -or $t.StartsWith("/*")) { continue }
            if ($rx.IsMatch($lines[$i])) {
                $hits += [pscustomobject]@{
                    File = $f.Name
                    Path = $f.FullName.Substring($root.Length + 1)
                    Line = $i + 1
                    Text = $lines[$i].Trim()
                }
            }
        }
    }
    return $hits
}

# ---------------------------------------------------------------------------
# 1. Hand-written crossings of the authoritative system-frame state.
#    Pattern:  <group>.CurrentOrbit.position|velocity  followed by an arithmetic operator.
#    Allowed (with reason) -- keyed by "File|trimmedText" so line shifts do not break the list.
# ---------------------------------------------------------------------------
$allowCrossing = @{
}

$rxCross = "CurrentOrbit\.(position|velocity)\s*[-+*/]"
$hits = Get-Hits $rxCross
$unknown = @()
$usedKeys = @{}
foreach ($h in $hits) {
    $key = "$($h.File)|$($h.Text)"
    $usedKeys[$key] = $true
    if (-not $allowCrossing.ContainsKey($key)) { $unknown += $h }
}
$staleKeys = @($allowCrossing.Keys | Where-Object { -not $usedKeys.ContainsKey($_) })
if ($unknown.Count -eq 0) {
    Ok "no unknown hand-written crossings of CurrentOrbit.position/velocity ($($hits.Count) known, allow-listed)"
    foreach ($k in $allowCrossing.Keys | Sort-Object) { Note "allowed: $k" }
    # a stale entry means the code changed under it (usually: that site was migrated to the typed field)
    # -- it must be deleted from this list, otherwise the list slowly turns into fiction.
    foreach ($k in $staleKeys) {
        Fail "stale allow-list entry (no longer matches any code): $k"
        Note "  -> delete it from `$allowCrossing` (migrated or rewritten)"
    }
} else {
    foreach ($h in $unknown) { Fail "$($h.Path):$($h.Line)  $($h.Text)" }
    Note "a hand-written crossing means someone did the frame math by hand -- route it through"
    Note "FrameConvert / GetBodyPosition / GetBodyState instead, or add a reason to the allow list."
}

# ---------------------------------------------------------------------------
# 2. Offset-accessor arithmetic outside the single conversion authority.
#    The central-body offset is a SystemVec3 now; mixing it with raw math is the A4 "second
#    implementation" smell. Legitimate uses pass it to FrameConvert / take `.v`.
# ---------------------------------------------------------------------------
$rxOffset = "(GetCentralBodyOffset|GetBodyOffsetSystem|GetBodyVelocityOffsetSystem)\([^)]*\)\s*[-+]"
$hits = Get-Hits $rxOffset
if ($hits.Count -eq 0) {
    Ok "no arithmetic applied directly to a *BodyOffset* accessor result"
} else {
    foreach ($h in $hits) { Fail "$($h.Path):$($h.Line)  $($h.Text)" }
}

# ---------------------------------------------------------------------------
# 2b. RATCHET: untyped reads of the authoritative system-frame state.
#     `OrbitState.positionSystem` / `.velocitySystem` are the typed carriers; the plain
#     `position` / `velocity` members are a DECLARED transition bridge (see the comment in
#     OrbitState) that must shrink to zero and then be deleted. This count may only go DOWN:
#     a new untyped read is a failure, migrating reads is the (visible) progress.
#     NOTE the `\b`: it does NOT match `positionSystem` (word continues), so the number is
#     exactly "reads that still see the untyped bridge".
# ---------------------------------------------------------------------------
$untypedBaseline = 0   # 2026-09-30: 109 (bridge) -> 105 -> 78 -> 52 -> **0** (migration complete, bridge deleted).
#                        With the bridge gone this can only stay 0: any hit means someone re-introduced
#                        an untyped view of the authoritative state (it would not even compile on OrbitState,
#                        so a hit means a NEW carrier appeared -- register it or type it).
$rxUntyped = "(CurrentOrbit|\.orbit)\.(position|velocity)\b"
# ^ NOT `\.(CurrentOrbit|...)`: inside SpaceEntityGroup the field is written bare (`CurrentOrbit.position`),
#   with no leading dot -- the first version of this regex required one and therefore silently missed all
#   13 in-file accesses (found by the compiler when the transition bridge was deleted, not by this tool).
#   Lesson (same family as D5's "empty output cannot tell 0 from a broken filter"): the instrument needs a
#   second, independent judge -- here the compiler.
$untypedHits = Get-Hits $rxUntyped
$untypedNow = $untypedHits.Count
if ($untypedNow -gt $untypedBaseline) {
    Fail "untyped reads of the system-frame state went UP: $untypedNow > baseline $untypedBaseline"
    foreach ($h in $untypedHits) { Note "  $($h.Path):$($h.Line)  $($h.Text)" }
    Note "new code must use positionSystem / velocitySystem (typed); see OrbitState's bridge note."
} elseif ($untypedNow -lt $untypedBaseline) {
    Ok "untyped system-state reads: $untypedNow (baseline $untypedBaseline) -- ratchet moved DOWN"
    Note "update `\$untypedBaseline` in this script to $untypedNow (and keep going: 0 means the bridge must go)"
} else {
    Ok "untyped system-state reads: $untypedNow (== baseline; nothing new, nothing migrated)"
}

# ---------------------------------------------------------------------------
# 2c. ABSOLUTE frame (`GetBodyPositionAbs`) is DISPLAY-ONLY by contract.
#     `CelestialBodyManager` documents it: "绝对系（递归到恒星）… **只用于渲染与跨系展示**;
#     拿它去减系统系坐标 = 凭空叠加一个行星公转偏移，量级上直接毁掉结果."
#     DECIDED 2026-09-30 (S3 tail): NOT typed -- every live consumer is rendering/view code
#     (PFView, SpaceBattleView3D, ViewFrame), its transitions go into the VIEW frame (UnityEngine
#     float, out of scope of these structs), and the frame vocabulary has no entry for it.
#     This check keeps the decision honest instead of leaving a gray zone: the accessor may only be
#     referenced from the files below. If physics/guidance ever starts using it, THIS goes red.
# ---------------------------------------------------------------------------
$absFrameAllowed = @(
    "CelestialBodyManager.cs",        # the definition + its recursion
    "MainTabWindow_PFView.cs",        # PFView renderer
    "MainTabWindow_PFView.Render.cs", # PFView renderer (2026-10-02 V-0: split into partials -- same
    "MainTabWindow_PFView.Scene.cs",  #   display-only decision, same call sites, new file names)
    "MainTabWindow_PFView.GUI.cs",    #   (ibid)
    "Window_SpaceBattleView3D.cs",    # 3D battle view
    "ViewFrame.cs",                   # view-frame helper (abs - abs)
    "GetSurfaceState.cs"              # tile->abs ECI helper (currently only used by a zero-caller API)
)
$hits = Get-Hits "GetBodyPositionAbs"
$absBad = @($hits | Where-Object { $absFrameAllowed -notcontains $_.File })
if ($absBad.Count -eq 0) {
    Ok "absolute frame (GetBodyPositionAbs) stays display-only ($($hits.Count) refs in $($absFrameAllowed.Count) allowed files)"
} else {
    foreach ($h in $absBad) { Fail "$($h.Path):$($h.Line)  $($h.Text)" }
    Note "the absolute frame is display-only; bringing it into physics/guidance needs a deliberate"
    Note "conversion (and probably a frame type) -- see CelestialBodyManager's frame doc block."
}

# ---------------------------------------------------------------------------
# 3. Carrier registry: every frame-carrying carrier must be either TYPED or PENDING-with-reason.
#    TYPED entries are asserted (old untyped form must be gone); PENDING entries are printed.
# ---------------------------------------------------------------------------
$typedMustMatch = @(
    @{ Name = "ProjectileInstance.position";      Rx = "public\s+SystemVec3\s+position;" },
    @{ Name = "ProjectileInstance.velocity";      Rx = "public\s+SystemVec3\s+velocity;" },
    @{ Name = "ProjectileInstance.prevPosition";  Rx = "public\s+SystemVec3\s+prevPosition;" },
    @{ Name = "ProjectileInstance.spawnPosition"; Rx = "public\s+SystemVec3\s+spawnPosition;" },
    @{ Name = "ProjectileInstance.spawnVelocity"; Rx = "public\s+SystemVec3\s+spawnVelocity;" },
    @{ Name = "SpaceEntityGroup.GetCentralBodyOffset"; Rx = "public\s+SystemVec3\s+GetCentralBodyOffset\(" },
    @{ Name = "SpaceEntityGroup.GetCentralBodyVelocity"; Rx = "public\s+SystemVec3\s+GetCentralBodyVelocity\(" },
    @{ Name = "SpaceGameComp.GetBodyOffsetSystem"; Rx = "internal\s+SystemVec3\s+GetBodyOffsetSystem\(" },
    @{ Name = "SpaceGameComp.GetBodyVelocityOffsetSystem"; Rx = "private\s+SystemVec3\s+GetBodyVelocityOffsetSystem\(" },
    @{ Name = "OrbitState.positionSystem";  Rx = "public\s+SystemVec3\s+positionSystem;" },
    @{ Name = "OrbitState.velocitySystem";  Rx = "public\s+SystemVec3\s+velocitySystem;" },
    @{ Name = "OrbitState has NO untyped bridge (deleted 2026-09-30)"; Rx = "public\s+SystemVec3\s+positionSystem;" }
)
$typedMissing = @()
foreach ($t in $typedMustMatch) {
    $found = $false
    foreach ($f in $sources) {
        $txt = [System.IO.File]::ReadAllText($f.FullName)
        if ([regex]::IsMatch($txt, $t.Rx)) { $found = $true; break }
    }
    if (-not $found) { $typedMissing += $t.Name }
}
if ($typedMissing.Count -eq 0) {
    Ok "carrier registry: all $($typedMustMatch.Count) typed carriers still declare their frame type"
} else {
    foreach ($m in $typedMissing) { Fail "typed carrier lost its frame type: $m" }
}

$pending = @(
    "OrbitView.r / .v / .equatorialAxis         (DECIDED 2026-09-30: NOT typed - 173 refs feed the verbatim"
    "                                            MechJebLib API (V3 params); the S2 two-port boundary covers it)",
    "ISpatialEntity.Position                    (A3 specimen: SpaceEntityGroup=system, SpaceEntity=ship-local;"
    "                                            needs an interface split, not a type change)",
    "positionBody / velocityBody (SpaceEntityGroup)  (DECIDED 2026-09-30: NOT typed -- lagging-copy problem is"
    "                                            GONE (they are computed now, same tick as CurrentOrbit); the"
    "                                            typed entry points for new code are GetBodyPosition/Velocity/"
    "                                            GetBodyState -> BodyVec3; the ~130 legacy readers are display/"
    "                                            diagnostic, migrating them is optional)"
)
Write-Host ""
Note "PENDING carriers (registered, not failures):"
foreach ($p in $pending) { Note "  - $p" }

Pop-Location
if ($script:fail -gt 0) {
    Write-Host ""
    Write-Host "FRAME-CROSSING CHECK FAILED ($($script:fail) finding(s))" -ForegroundColor Red
    exit 1
}
Write-Host ""
Write-Host "frame-crossing check passed (no drift; registry printed above)" -ForegroundColor Green
exit 0
