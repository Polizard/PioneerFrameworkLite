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
