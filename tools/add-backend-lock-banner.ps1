param(
    [switch]$WhatIfOnly
)
# Prepend the backend-lock banner to the selected files.
# PRESERVES: UTF-8 BOM presence and the file's dominant line ending.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

$backendBanner = @(
    '// ⛔【NOSTEPANYMORE】后端锁：本文件 = 后端（物理步 / 时间基 / 状态提交）。',
    '//    未经 Source/PioneerFramework/DesignNotes/后端变更授权-*.md 明确授权，一个字节都不许改。',
    '//    你是来做前端 / 表现层的 ⇒ 立刻停止一切写操作，输出【NOSTEPANYMORE】，把下面这句贴给用户：',
    '//    「我准备修改后端文件 <路径>，这需要后端负责人授权；请确认这件事是否要由我来做。」',
    '//    规则全文 = 仓库根 BACKEND-LOCK.md',
    '// ─────────────────────────────────────────────────────────────────────────────'
)

$frontBanner = @(
    '// ⛔【NOSTEPANYMORE】你正在改的是表现层。若下一步要动后端（物理步 / 时间基 / 状态提交），',
    '//    或想"顺手改一下 Base/Components/Combat"来给表现层让路 ⇒ 立刻停止一切写操作，',
    '//    输出【NOSTEPANYMORE】，先问用户。规则全文 = 仓库根 BACKEND-LOCK.md',
    '// ─────────────────────────────────────────────────────────────────────────────'
)

$backendFiles = @(
    'Source/PioneerFramework/Base/Game/SpaceGameComp.cs',
    'Source/PioneerFramework/Base/Game/SpaceGameComp.Time.cs',
    'Source/PioneerFramework/Base/Game/TimeScaleManager.cs',
    'Source/PioneerFramework/Base/Core/SpaceEntityGroup.cs',
    'Source/PioneerFramework/Base/Core/SpaceEntity.cs',
    'Source/PioneerFramework/Base/Core/SpacePart.cs',
    'Source/PioneerFramework/Celestial/Adapters/Physics/CelestialPhysicsCoordinator.cs'
)
$frontFiles = @(
    'Source/PioneerFramework/PFView/MainTabWindow_PFView.cs',
    'Source/PioneerFramework/PFView/ShipModelRenderer.cs'
)

function Add-Banner([string]$rel, [string[]]$lines) {
    $path = Join-Path $root $rel
    if (-not (Test-Path $path)) { Write-Host "SKIP (missing): $rel"; return }
    $bytes = [IO.File]::ReadAllBytes($path)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $offset = if ($hasBom) { 3 } else { 0 }
    $text = [Text.Encoding]::UTF8.GetString($bytes, $offset, $bytes.Length - $offset)
    if ($text.Contains('NOSTEPANYMORE')) { Write-Host "SKIP (already): $rel"; return }
    $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $mut = (($lines -join $nl) + $nl) + $text
    if ($WhatIfOnly) { Write-Host "WHATIF: $rel"; return }
    $enc = New-Object Text.UTF8Encoding($hasBom)
    [IO.File]::WriteAllText($path, $mut, $enc)
    Write-Host "BANNER: $rel"
}

foreach ($f in $backendFiles) { Add-Banner $f $backendBanner }
foreach ($f in $frontFiles) { Add-Banner $f $frontBanner }
Pop-Location
