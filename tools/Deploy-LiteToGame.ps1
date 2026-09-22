# 部署脚本：把 Lite 裁剪后的运行时资产同步到游戏 Mods 目录。
# 用法：在 PowerShell 中运行本脚本（路径含空格，建议用管理员权限执行一次以确保写入 Program Files 下的 Mods）。
#   .\Tools\Deploy-LiteToGame.ps1
# 说明：
#   - 同步 1.6/Defs、1.6/Textures、1.6/Patches、1.6/AssetBundles、About、Shaders（先清后复制，无冗余残留）
#   - 1.6/Assemblies 只部署 PioneerFrameworkLite.dll（若已构建）；旧 dll/pdb 一律清理
#   - 请先 build（msbuild /p:Configuration=Debug）再运行本脚本
param(
    [string]$DeployDir = "C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\PioneerFrameworkLite"
)

$ErrorActionPreference = 'Stop'
$srcRoot = Split-Path -Parent $PSScriptRoot

# ── 硬门（2026-09-19 加，用户批准）：**闸门红就别部署** ──────────────────────────────
# 起因：那天我在"Debug 全量重建失败"之后照样跑了本脚本 ⇒ 把**上一版** DLL 部署进游戏，
# 而"构建失败时不要部署"是我自己写进台账的规矩（B30）—— 靠人记没用，改成本脚本自己拦。
# 两道检查：
#   ① 主工程能过 Release 构建（红 ⇒ 直接退出，不部署任何东西）；
#   ② 交付物 `1.6/Assemblies/PioneerFrameworkLite.dll` **不比最新源文件旧**
#      （否则就是把旧 DLL 送进游戏 —— 那天真正发生的事）。
Write-Host "=== 部署前闸门 ===" -ForegroundColor Cyan

$proj = Join-Path $srcRoot 'Source\PioneerFramework\PioneerFramework.csproj'
$buildLog = & dotnet build $proj -c Release --nologo 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "闸门红：Release 构建失败 ⇒ **拒绝部署**（先修构建）" -ForegroundColor Red
    $buildLog | Select-Object -Last 12 | ForEach-Object { Write-Host "  $_" }
    exit 1
}

$dllPath = Join-Path $srcRoot '1.6\Assemblies\PioneerFrameworkLite.dll'
if (-not (Test-Path -LiteralPath $dllPath)) {
    Write-Host "闸门红：交付物 DLL 不存在 ⇒ 拒绝部署" -ForegroundColor Red
    exit 1
}
$dllTime = (Get-Item -LiteralPath $dllPath).LastWriteTimeUtc
# ⚠ 必须用 `-Filter` 而**不是** `-Include *.cs`：PowerShell 5.1 里 `-Include` 与 `-Recurse` 同用
#   会**把过滤丢掉**（实测它返回了 `Source\PioneerFramework\DesignNotes` 这个**目录**，
#   于是"最新源文件"变成了一份 `.md` 交班文档的时间戳 ⇒ 闸门随机变红、拦住合法部署）✗✗。
#   2026-09-21 修（`tools/` 下只此一处用 `-Include`，已核对）。
#   `-Filter` 由文件系统提供者执行、快得多，且不会退化。
$newestSrc = Get-ChildItem -LiteralPath (Join-Path $srcRoot 'Source') -Recurse -File -Filter *.cs |
             Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($newestSrc -and $newestSrc.LastWriteTimeUtc -gt $dllTime.AddSeconds(2)) {
    Write-Host ("闸门红：交付物 DLL 比最新源文件旧（DLL {0:u} vs {1} {2:u}）⇒ 这是'部署旧 DLL'的情形，拒绝" `
        -f $dllTime, $newestSrc.Name, $newestSrc.LastWriteTimeUtc) -ForegroundColor Red
    Write-Host "  先跑 tools/test-all.ps1（它会重建 Release 交付物）" -ForegroundColor Yellow
    exit 1
}
Write-Host "闸门绿：Release 构建通过、交付物不比源文件旧" -ForegroundColor Green

if (-not (Test-Path -LiteralPath $DeployDir)) {
    Write-Host "部署目录不存在，已创建: $DeployDir"
    New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null
}

# 资产目录：先清后复制（防止 Lite 已删除的文件残留在部署副本）
foreach ($d in @("1.6\Defs", "1.6\Textures", "1.6\Patches", "1.6\AssetBundles")) {
    if (Test-Path -LiteralPath "$DeployDir\$d") { Remove-Item -LiteralPath "$DeployDir\$d" -Recurse -Force }
    Copy-Item -LiteralPath "$srcRoot\$d" -Destination "$DeployDir\1.6\" -Recurse -Force
    Write-Host "  同步 $d"
}
foreach ($d in @("About", "Shaders")) {
    if (Test-Path -LiteralPath "$DeployDir\$d") { Remove-Item -LiteralPath "$DeployDir\$d" -Recurse -Force }
    Copy-Item -LiteralPath "$srcRoot\$d" -Destination "$DeployDir\" -Recurse -Force
    Write-Host "  同步 $d"
}

# Assemblies：只保留 PioneerFrameworkLite.dll（未构建时为空目录，游戏不会加载空 mod 代码）
if (Test-Path -LiteralPath "$DeployDir\1.6\Assemblies") { Remove-Item -LiteralPath "$DeployDir\1.6\Assemblies" -Recurse -Force }
New-Item -ItemType Directory -Force -Path "$DeployDir\1.6\Assemblies" | Out-Null
$liteDll = Join-Path $srcRoot '1.6\Assemblies\PioneerFrameworkLite.dll'
if (Test-Path -LiteralPath $liteDll) {
    Copy-Item -LiteralPath $liteDll -Destination "$DeployDir\1.6\Assemblies\"
    Write-Host "  部署 PioneerFrameworkLite.dll"
} else {
    Write-Host "  警告：PioneerFrameworkLite.dll 未构建，先执行 msbuild /p:Configuration=Debug 再运行本脚本"
}

Write-Host "部署同步完成: $DeployDir"
