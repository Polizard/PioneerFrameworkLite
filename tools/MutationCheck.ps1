# MutationCheck.ps1 -- 判据自证工具（"撤掉修复，测试必须红"）
#
# 用法（在仓库根或任意目录）：
#   powershell -ExecutionPolicy Bypass -File tools/MutationCheck.ps1
#   powershell -ExecutionPolicy Bypass -File tools/MutationCheck.ps1 -Manifest <path>
#   powershell -ExecutionPolicy Bypass -File tools/MutationCheck.ps1 -Only M22,M8     # 只跑受影响的子集
#
# 为什么有 -Only（2026-10-31）：全量 34 条约 4 分钟（实测 7.4 s/条 = 构建 ~4 s + 筛测试 ~3.4 s）。
# 日常改一个模块没必要全跑；按名字前缀挑相关的几条，~40–60 s 就够。**收口仍要全量跑一次**。
#
# 语义（2026-10-17 起，计划稿 §8-7 的"判据先自证"条）：
#   对清单里的每个条目：
#     1. 备份原文件字节（含 BOM 判定）+ 记 SHA256；
#     2. 施加变异（oldText 必须**恰好命中 1 次**，否则判清单漂移直接报 FAIL）；
#     3. 跑该条目的筛选测试，要求 `expectFail` 里**每一条**都出现在 [FAIL] 列表里；
#     4. 无论成败：用备份字节原样还原，并核 SHA256 一致；
#   全部条目符合预期 => exit 0；否则 exit 1（并在输出里给出诊断片段）。
#
# 纪律：
#   · 本工具**只做临时变异**——还原是 finally 语义，不会留下改动；
#   · 还原后若要提交/构建，请照常跑 `tools/test-all.ps1`（本工具不替代闸门）；
#   · 清单与源码漂移（oldText 命中 ≠ 1）时**先修清单**，不要放宽匹配。
param(
    [string]$Manifest = (Join-Path $PSScriptRoot "mutation-checks.json"),
    [string]$Only = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

$failures = 0
$entries = Get-Content -LiteralPath $Manifest -Raw -Encoding UTF8 | ConvertFrom-Json

if ($Only) {
    $pats = $Only -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    $entries = @($entries | Where-Object {
        $n = $_.name
        ($pats | Where-Object { $n -like "$_*" }).Count -gt 0
    })
    if ($entries.Count -eq 0) { throw "没有条目匹配 -Only $Only" }
}

Write-Host "=== MutationCheck: $($entries.Count) 个条目（清单 $Manifest）===" -ForegroundColor Cyan

foreach ($m in $entries) {
    Write-Host ""
    Write-Host "--- [$($m.name)] $($m.file)" -ForegroundColor Cyan
    if ($m.why) { Write-Host "    理由：$($m.why)" }

    $path = Join-Path $root $m.file
    $bytes = [IO.File]::ReadAllBytes($path)
    $sha0 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $offset = if ($hasBom) { 3 } else { 0 }
    $orig = [Text.Encoding]::UTF8.GetString($bytes, $offset, $bytes.Length - $offset)
    # 2026-10-21：平台口径归一。仓库存的是 LF，而 Windows checkout（core.autocrlf=true）会把文件写成
    # CRLF ⇒ 多行 oldText（清单里按 LF 书写）命中 0 次、**假红**（M15/M28/M30 实撞过）。
    # 匹配与施加统一按 LF；还原仍走上面的 `$bytes` 逐字节还原，不受影响。
    $origLf = $orig.Replace("`r`n", "`n")

    $hits = ([regex]::Matches($origLf, [regex]::Escape($m.oldText))).Count
    if ($hits -ne 1) {
        Write-Host "  FAIL: oldText 命中 $hits 次（要求恰好 1 次）——清单与源码已漂移，先修清单" -ForegroundColor Red
        $failures++
        continue
    }

    try {
        $mut = $origLf.Replace($m.oldText, $m.newText)
        $enc = New-Object System.Text.UTF8Encoding($hasBom)
        [IO.File]::WriteAllText($path, $mut, $enc)
        Write-Host "  已施加变异；跑筛选测试：$($m.testFilter)"

        # 结果以 **TRX** 为准（控制台格式随 locale/流合并而变：首版 [FAIL] 解析在中文环境下没抓到；
        # TRX 里的 testName 是稳定判据）。$ErrorActionPreference 临时降 Continue：dotnet 的失败行走 stderr。
        $resDir = Join-Path ([IO.Path]::GetTempPath()) ("pfl_mut_" + [guid]::NewGuid().ToString("N"))
        $eap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $out = ''
        # 2026-10-21：条目可带 `project` 指定测试工程。默认仍是 net10 的游戏集成测试工程；
        # 纯台架（阶梯/霍曼/移植面）住在 net48 的 `Source/MechJebLibTest`（MechJebLibTest.csproj），
        # 不指定就跑错工程 —— 现象是"没有测试匹配筛选器"，会被误读成"变异没被抓住"。
        $testProj = if ($m.project) { $m.project } else { "Source/PioneerFramework.Tests/PioneerFramework.Tests.csproj" }
        try {
            $out = & dotnet test $testProj -v:minimal --filter $m.testFilter --logger "trx;LogFileName=res.trx" --results-directory $resDir 2>&1 | Out-String
        }
        finally { $ErrorActionPreference = $eap }

        $red = @()
        $trx = Join-Path $resDir 'res.trx'
        if (Test-Path -LiteralPath $trx) {
            [xml]$trxXml = Get-Content -LiteralPath $trx -Raw
            $red = @($trxXml.TestRun.Results.UnitTestResult |
                Where-Object { $_.outcome -eq 'Failed' } |
                ForEach-Object { [string]$_.testName } | Sort-Object -Unique)
        } else {
            Write-Host "  TRX 未生成（构建失败？）——按无红处理，尾部见下" -ForegroundColor Yellow
        }
        if (Test-Path -LiteralPath $resDir) { Remove-Item -LiteralPath $resDir -Recurse -Force -ErrorAction SilentlyContinue }
        $red = @($red | Sort-Object -Unique)

        $ok = $true
        foreach ($want in $m.expectFail) {
            $hit = $false
            foreach ($r in $red) { if ($r.Contains($want)) { $hit = $true; break } }
            if (-not $hit) {
                Write-Host "  FAIL: 期望红但没有红：$want" -ForegroundColor Red
                $ok = $false
            }
        }
        if ($ok) {
            Write-Host "  PASS: 变异被 $($red.Count) 条测试抓到：$($red -join ' | ')" -ForegroundColor Green
        } else {
            Write-Host "  --- 测试输出尾部（诊断用）---" -ForegroundColor Yellow
            ($out -split '\r?\n' | Select-Object -Last 12) | ForEach-Object { Write-Host "    $_" }
            $failures++
        }
    }
    finally {
        [IO.File]::WriteAllBytes($path, $bytes)
        $sha1 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($sha1 -ne $sha0) {
            Write-Host "  FAIL: 还原后哈希不一致（$sha0 != $sha1）" -ForegroundColor Red
            $failures++
        } else {
            Write-Host "  已还原（SHA256 $($sha0.Substring(0,16))... 一致）"
        }
    }
}

Pop-Location
if ($failures -gt 0) {
    Write-Host ""
    Write-Host "MUTATION CHECK FAILED: $failures 项不符合预期" -ForegroundColor Red
    exit 1
}
Write-Host ""
Write-Host "MUTATION CHECK OK: $($entries.Count) 项全部符合预期（变异必红、还原逐字节同）" -ForegroundColor Green
exit 0
