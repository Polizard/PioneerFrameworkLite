# 链 A 备份（2026-11-21）· 备份记录

> 用途：在「发射链 A 退役、全面转向链 B」之前，把链 A 的全部可执行内容与它会牵连的共享文件留下完整副本。
> 生成时间：2026-11-21　源提交：`90d3819`（`Merge branch 'PFview优化'`）　Git 锚点：`tag backup/chain-a-20261121`

## 一、备份了什么

共 **28 个文件**，分三类。完整清单与 SHA-256 见同目录 [`MANIFEST.sha256`](MANIFEST.sha256)（格式：`原始路径|备份内路径|SHA256|字节`）。

### 1. `code/` — 链 A 专属代码（16 个，退役时删除）

| 文件 | 作用 |
|---|---|
| `GroundAssembly/CompAssemblyPad.cs` | 发射台状态机（Idle→Designing→Building→Completed）+ 发射方式记忆 |
| `GroundAssembly/Building_AssemblyPad.cs` | 发射台建筑类 |
| `GroundAssembly/Window_SpacecraftManagement.cs` | 6 Tab 管理窗口（总览/设计/施工/舱内/发射/轨迹） |
| `GroundAssembly/CompUmbilicalTower.cs` · `Building_RocketUmbilicalTower.cs` | 脐带塔 / 登船口（舱内线） |
| `GroundAssembly/CompReturnedBooster.cs` | 回收下来的第一级助推器 |
| `GroundAssembly/WorkGiver_RebuildRocket.cs` · `JobDriver_RebuildRocket.cs` | 回收后重建工作 |
| `GroundAssembly/RocketRebuildFrameUtility.cs` · `RocketRebuildResourceUtility.cs` | 重建框架与材料结算 |
| `GroundAssembly/Patches/FuelingPortUtilityPatches.cs` | 发射台燃料口补丁 |
| `GroundAssembly/PFLaunchDefOf.cs` | 重建 Job / Designation 的 DefOf |
| `Launch/Dialog_RocketLaunch.cs` | 统一发射对话框（**死代码**，`OpenLaunchDialog` 无调用者） |
| `Launch/RocketRecoveryDef.cs` | 火箭回收配置 Def |
| `Launch/RocketBoosterReturnUtility.cs` | 助推器返回生成 |
| `Launch/MapComponent_RocketBoosterReturnScheduler.cs` | 返回助推器的排队调度 |

### 2. `defs/` — 链 A 专属/受影响的 XML（7 个）

| 备份文件 | 原始路径 |
|---|---|
| `PF_CV_TestAssets.xml` | `1.7/Defs/Things/GroundAssembly/` — 小型总装发射台 + CV-1/CV-3 及其 Skyfaller/ActivePod |
| `PF_RocketUmbilicalTower.xml` | 同上 — 脐带塔 |
| `PF_Buildings_Basic.xml` | 同上 — **整文件副本**（退役时只删其中的 `PF_Building_Booster_Returned_Basic` 段） |
| `PF_RocketRecovery_Basic.xml` | 同上 — `RocketRecoveryDef` |
| `PF_Jobs.xml` | `1.7/Defs/JobDefs/` — `PF_RebuildRocket` |
| `PF_Designations.xml` | `1.7/Defs/Misc/Designations/` — `PF_DesignateRebuildRocket` |
| `PF_WorkGivers.xml` | `1.7/Defs/WorkGiverDefs/` — 重建 WorkGiver |

### 3. `shared/` — 会被改写的共享文件（5 个，「退役前」版本）

> 这五个文件**不会删**，但退役时会去掉其中对链 A 的引用。**只还原被删文件而不还原它们，会编译不过。**

`Launch/LaunchService.cs`、`Launch/RocketLaunchManager.cs`、`GroundAssembly/CompUnfinishedRocket.cs`、`GroundAssembly/PF_RocketConstructionFlow.cs`、`GroundAssembly/LaunchPadUtility.cs`

**不包含**：贴图 / AssetBundle（链 A 复用了 `BuildingBase` 与小型发射台贴图，无独立美术）；测试代码（经核对，测试工程对链 A 符号 **0 引用**）。

## 二、存放在哪

| 形态 | 位置 | 说明 |
|---|---|---|
| 分支副本（本目录） | `Source/已弃置/ChainA-20261121/` | 28 个文件 + 本记录 + 清单；不参与编译（不在任何 `.csproj` 里） |
| Git 锚点 | `tag backup/chain-a-20261121` → `90d3819` | 与分支副本等价，且逐字节可验证 |
| 原始提交 | `90d3819` | 退役改动之前的最后一个提交 |

建议把 tag 一并推送（否则只在本地）：
```powershell
git push origin backup/chain-a-20261121
```

## 三、怎么还原

### 方案 1（推荐）：按 tag 还原，逐字节一致

```powershell
cd "D:\Steam\steamapps\common\RimWorld\Mods\PioneerFrameworkLite"

# 按清单逐条 checkout（原始路径已写在清单第一列）
Get-Content "Source\已弃置\ChainA-20261121\MANIFEST.sha256" |
  Where-Object { $_ -and -not $_.StartsWith('#') } |
  ForEach-Object { ($_ -split '\|')[0] } |
  ForEach-Object { git checkout backup/chain-a-20261121 -- $_ }

# 还原后必须重建交付 DLL，否则游戏里跑的还是退役版
powershell -ExecutionPolicy Bypass -File tools/rebuild-dll.ps1
```

注意：这会把 `shared/` 的 5 个文件也一并回到退役前状态（正确，且必要）。

### 方案 2：按本目录副本还原（tag 丢失时的兜底）

```powershell
$dst = "Source\已弃置\ChainA-20261121"
Get-Content "$dst\MANIFEST.sha256" |
  Where-Object { $_ -and -not $_.StartsWith('#') } |
  ForEach-Object {
    $p = $_ -split '\|'
    Copy-Item (Join-Path $dst $p[1]) $p[0] -Force
  }
powershell -ExecutionPolicy Bypass -File tools/rebuild-dll.ps1
```

### 方案 3：只取回单个文件

`code\` / `shared\` 下的相对路径 = `Source/PioneerFramework/` + 该路径；
`defs\` 下的文件按上表「原始路径」列放回即可。

## 四、校验

```powershell
$dst = "Source\已弃置\ChainA-20261121"
Get-Content "$dst\MANIFEST.sha256" |
  Where-Object { $_ -and -not $_.StartsWith('#') } |
  ForEach-Object {
    $p = $_ -split '\|'
    $actual = (Get-FileHash (Join-Path $dst $p[1]) -Algorithm SHA256).Hash
    if ($actual -ne $p[2]) { "MISMATCH: " + $p[1] }
  }
# 无输出 = 28 个文件全部一致
```

还原后另可跑闸门：`python tools/validate_config.py` 与 `tools/test-all.ps1`。

## 五、已知代价与注意

1. **`defs/PF_Buildings_Basic.xml` 是整文件副本**：退役时只删 `PF_Building_Booster_Returned_Basic` 一段。若该文件在退役之外还有别的改动，方案 1/2 会整文件覆盖 —— 那种情况请手工合并，或只取回那一段。
2. **存档不兼容**：退役期间新建的存档里没有发射台数据。还原 def 与代码后，旧发射台不会凭空出现在这些存档里；只有退役前的存档才认得它。
3. **`PF_CV_TestAssets.xml` 删掉后，老档里已建的 CV-1/CV-3 会变成缺失 def**（按用户裁定「现阶段不需要考虑」）。
4. **还原 ≠ 恢复回收线以外的能力**：链 A 的舱内地图 / 载荷 / 双模式发射能力只存在于这些文件里，删掉后链 B 侧**没有**对应实现 —— 还原才拿得回来。
5. 备份目录本身**不进构建**；`tools/validate_config.py` 只扫 `1.7/Defs` 与 `NFexp/1.7/Defs`，不会误读这里的 XML。
