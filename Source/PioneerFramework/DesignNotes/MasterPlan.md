# PioneerFramework 统筹计划（MasterPlan）

> 创建日期：2026-08-01
>
> **这份文件是干什么的**
> 它是项目进度的唯一入口，记录每项工作的「进度」。当前设计思路暂时不在这里重复记录，因为既不方便原文档作者管理,又让这篇文档变得冗长.所以统一链接到 DesignNotes 下的各篇文档（见 §7 文档索引）。
>
> **状态标记**
> ⬜ 待办
> 🔵 进行中
> 🟡 待验证
> ✅ 已完成
> ⚪ 已废弃
>
> **优先级**
> P0 影响正确性，优先处理 ｜ P1 架构改进 ｜ P2 清理简化 ｜ P3 命名与文档
>
> **维护约定**
> 每完成一项任务，同步更新本文件的状态和对应的设计文档。

---

## 1. 当前焦点区

| 状态 | 优先级 | 任务 | 说明 |
|------|--------|------|------|
| 🟡 待验证 | P0 | 战斗系统待验证 4 项（详见 §4） | 需进游戏实测 |
| 🔵 进行中 | P1 | Phase 2 地图与空间站收尾（对接/同步/存档） | 见 §3.4 |

---

## 2. 阶段总览

| 阶段 | 内容 | 进度 |
|------|------|------|
| Phase 1 | 基础框架（轨道物理、空间网格、飞船装配、3D 视图） | 🔵 进行中（主体已完成，航天器发射系统未实装，见 §3.7） |
| Phase 2 | 地图与空间站（Pocket Map、空间站、月球生成、对接、存档） | 🔵 进行中（主体已完成，对接/同步/存档待办） |
| Phase 3 | 战斗系统（武器、弹丸、碰撞、爆炸、AI） | 🔵 主体完成，待验证 4 项；基础 AI 已实现，状态机未实现；导弹/无人机代码未实现 |
| Phase 4 | 地表探索（着陆、移动、前哨站） | ⬜ 未开始 |
| Phase 5 | 经济与外交（贸易、关系、任务） | ⬜ 未开始 |

来源：[PioneerFramework设计与计划.md](./PioneerFramework设计与计划.md)

---

## 3. 任务清单（按模块）

### 3.1 战斗系统

来源：[ToDoList.md](./ToDoList.md)、[太空战斗系统-设计思路.md](./太空战斗系统-设计思路.md)

- [✅ 已完成][P1] 物理时间基准统一：`GetDisplayTick` → `GetPhysicsTick` 系列，所有物理计算绑定 physicsTick
  - 代码：`../Base/Game/TimeScaleManager.cs`、`../Base/Game/SpaceGameComp.cs`
  - 验证：✅ 已日志验证（5008 tick → 83.47s）
- [🟡 待验证][P0] 细网格 key 统一为 frameId（绕卫星的船可被命中）
  - 代码：`../SpatialIndexing/CombatGrid.cs`（AddShip/RemoveShip/UpdateShipPosition）
  - 验证：游戏内战斗测试，出现 `[命中]` 日志（ToDoList T2）
- [🟡 待验证][P0] 物理层 double 化（开火链路 Vector3 → DVector3）
  - 代码：`../Combat/CombatManager.cs`、`../Combat/TargetingSystem.cs`、`../Combat/WeaponInstance.cs`
  - 验证：1e8m 级坐标下网格索引稳定（ToDoList T3）
- [🟡 待验证][P0] 武器引用化（`FireWeapon(groupId, WeaponInstance, ...)` 替代索引）
  - 代码：`../Combat/CombatManager.cs`、`../Base/Debug/Window_CombatTest.cs`
  - 验证：武器部件被摧毁后列表收缩不再索引错位（ToDoList T4）
- [✅ 已完成][P1] 多格注册（船注册到 AABB 覆盖格集合，位置变更差集增删）
  - 代码：`../SpatialIndexing/CombatGrid.cs`
- [✅ 已完成][P1] 速度分层碰撞模型（<50 m/s 撞击等效动量交换；≥50 m/s 真爆炸 R=8000+2·E^(1/3)）
  - 代码：`../Combat/CombatManager.cs`
- [✅ 已完成][P2] 碰撞/爆炸调试日志（`[ShipCollision]` / `[爆炸]` / `[爆炸损伤]`）
- [✅ 已完成][P1] 网格回收改百分比斩杀（DrainEmptyCells 双路径：快路径每 2 tick 20%、慢路径每 60 tick 全量）
  - 代码：`../SpatialIndexing/CombatGrid.cs`
- [🟡 待验证][P1] 碰撞爆炸参数实测调参（阈值 50 m/s、R/D0/冲量系数）
  - 来源：[ToDoList.md](./ToDoList.md)
  - 验证：游戏内实战触发，核对 `[爆炸]` 日志数值
- [✅ 已完成][P1] 最小战斗闭环（弹丸 → 命中 → 部件损伤 → 结构失效）
  - 代码：`../Combat/`（CombatManager、WeaponDef、WeaponInstance、ProjectileInstance、TargetingSystem、SpacePart）
  - 来源：[太空战斗系统-设计思路.md](./太空战斗系统-设计思路.md)
- [✅ 已完成][P1] 战斗子 Grid（20km 细格，无边界 Dictionary + AABB 覆盖格多格注册 + TTL 回收，持久 GameComponent）
  - 代码：`../SpatialIndexing/CombatGrid.cs`
- [⬜ 待办][P1] 射线武器（瞬时命中、逐层穿透、平方反比衰减）
  - 来源：[太空战斗系统-设计思路.md](./太空战斗系统-设计思路.md)
  - 说明：`1.6/Defs/Combat/WeaponDefs.xml` 已定义 `PF_Weapon_Laser`，代码层未实现（2026-08-01 盘点）
- [⬜ 待办][P1] 导弹/无人机武器（复用 SpaceEntityGroup，终末阶段加速）
  - 来源：[太空战斗系统-设计思路.md](./太空战斗系统-设计思路.md)
  - 说明：`WeaponDefs.xml` 已定义 `PF_Weapon_MissileLauncher` / `PF_Weapon_DroneLauncher` / `PF_Weapon_DroneGun`，代码层未实现（2026-08-01 盘点）。2026-08-06 源码核查补充：`../Combat/CombatManager.cs` 的 `FireWeapon` 仅 `Ray` 走专门分支（`FireRayWeapon`），Missile/Drone 无制导/燃油/投放逻辑，全部回退通用射弹生成路径
- [🔵 进行中][P1] 战斗 AI 状态机（Idle/Defensive/Aggressive/Evasive + 目标评分）
  - 说明：基础目标评分 + 开火已实现（`../Combat/TargetingSystem.cs` 的 FindBestTarget/EvaluateTarget/EngageTarget），四状态机未实现
  - 来源：[太空战斗系统-设计思路.md](./太空战斗系统-设计思路.md)
- [⬜ 待办][P1] 碰撞检测升级为真正 OBB 旋转求交
  - 说明：2026-08-06 源码核查发现，`../Combat/CombatManager.cs` 的 `CheckSegmentGroupHit` 实为 AABB slab（调 `CombatGrid.RaySlabIntersection`）+ Part 级局部坐标查询（`entity.QueryRayHit`），非设计思路文档描述的 OBB 旋转求交；部件有旋转时精度下降
  - 代码：`../Combat/CombatManager.cs`、`../SpatialIndexing/CombatGrid.cs`
- [⬜ 待办][P2] 护盾系统、命中特效、弹丸拖尾、太空垃圾
  - 来源：[太空战斗系统-设计思路.md](./太空战斗系统-设计思路.md)
- [⬜ 待办][P2] 无边界战斗：三层模拟 tier（区域 LOD + tier 迁移规则）
  - 来源：[太空战斗系统-设计思路.md](./太空战斗系统-设计思路.md)「未来方向」
- [⬜ 待办][P2] 区域级时间膨胀（TiDi，TimeScaleManager 增加区域倍率）
  - 来源：[太空战斗系统-设计思路.md](./太空战斗系统-设计思路.md)「未来方向」

### 3.2 轨道物理

来源：[PioneerFramework设计与计划.md](./PioneerFramework设计与计划.md)、[架构审计-20260726.md](./架构审计-20260726.md)

- [✅ 已完成][P1] N 体引力积分（三阶泰勒）、圆轨保持、机动规划队列、Lambert 转移、交会规划
  - 代码：`../Base/Orbital/NBodyIntegrator.cs`、`OrbitMath.cs`、`ManeuverPlanner.cs`、`OrbitTransferPlanner.cs`、`RendezvousPlanner.cs`、`../Base/MechJebLib/Lambert/Gooding.cs`
- [⬜ 待办][P1] `GetWorldDeltaV` 增加 `mu` 参数（审计#6，源码核查发现未实际修复）
  - 代码：`../Base/Orbital/OrbitMath.cs`
- [⬜ 待办][P1] 轨道保持 dV 消耗优化
  - 来源：[PioneerFramework设计与计划.md](./PioneerFramework设计与计划.md) Phase 2.3
- [⬜ 待办][P1] 轨道保持飞船改圆锥弧段解析传播（Kepler 根数 / 万用变量）
  - 代码：`../Base/Orbital/`（新增解析传播器）；`../Base/Core/SpaceEntityGroup.cs`（轨道保持传播路径）
  - 说明：轨道保持（白名单 = 仅中心天体）= 纯二体 → 轨迹有闭式解，现状却每 tick 做三阶 Taylor 数值积分，是在数值积分一个解析可解的问题。改 (a,e,i,Ω,ω,M) 或万用变量后：5 根数恒定 + 平近点角线性推进，位置查询 O(1) 解析，长期巡航零积分成本。
  - 边界①：圆轨模式保持现状（本就是退化根数表示，已最优）——推广时必须用赤道根数/万用变量绕开 e→0 奇点，而圆轨恰是经典根数的奇点情形。
  - 边界②：射弹不适用——多体引力是设计意图（近天体弹道弯折的游戏性），射弹轨迹保持多体积分；射弹隧穿升级（conservative advancement）是与轨迹表示解耦的独立项。
  - 来源：2026-08-05 相空间讨论
- [⬜ 待办][P1] RendezvousPlanner 扫参范围覆盖检查（可能遗漏最优解，审计#8）
  - 代码：`../Base/Orbital/RendezvousPlanner.cs`
- [⬜ 待办][P2] 机动到期 UI 通知 / 倒数计时完善（审计#9，已有部分实现）
  - 代码：`../Base/Orbital/ManeuverQueueWindow.cs`
  - 说明：2026-08-06 源码核查：倒计时 UI 已实现（`ManeuverQueueWindow.cs:64-67` 显示 `T-{N} 秒`），但单位错误——`remainingTicks`（tick 数）直接当秒显示，TimeScaleManager 变速时（Combat=1/60x、Fast=8x）显示与真实时间不符；仍无到期通知
- [⬜ 待办][P3] N 体方法重命名 `RestrictedMultiBodyAccelJerk`（审计#13，实为限制性多体）
  - 代码：`../Base/Orbital/NBodyIntegrator.cs`

### 3.3 飞船装配

来源：[3D飞船系统-技术思路.md](./3D飞船系统-技术思路.md)、[PioneerFramework设计与计划.md](./PioneerFramework设计与计划.md)

- [✅ 已完成][P1] 3D 装配视口 + 部件定义系统 + 空间允许（OBB 重叠）+ 框架质量（紧凑度→质量）
  - 代码：`../Ships3D/`（Ship3D、ShipPartDef、ShipPartInstance、CompShipDesigner、Window_ShipDesigner3D）
- [🔵 进行中][P1] 多形状 Mesh 生成（枚举 7 形状，仅 3 种独立生成）
  - 代码：`../Ships3D/ShipPartMeshGenerator.cs`
  - 说明：2026-08-06 源码核查：`ShipPartShape` 枚举共 7 值（Cube/SphericalCorner/Weapon/Engine/PropellantTank/CargoHold/RoomModule）；`GetMesh` 仅 Cube/SphericalCorner/RoomModule 独立生成，Engine/PropellantTank/CargoHold 回退 Cube，Weapon 无 case 静默回退 Cube。设计文档（3D飞船系统-技术思路.md）描述的 Prism/Cylinder/Sphere 在枚举中不存在
- [⬜ 待办][P2] `ShipPartShape.Weapon` 补 case（当前 `GetMesh` switch 中无此 case，静默回退 Cube）
  - 代码：`../Ships3D/ShipPartMeshGenerator.cs`、`../Ships3D/ShipPartDef.cs`
- [⬜ 待办][P3] `ShipPartShape` 枚举与设计文档对齐
  - 说明：[3D飞船系统-技术思路.md](./3D飞船系统-技术思路.md) 写 Prism/Cylinder/Sphere，代码实为 Cube/SphericalCorner/Weapon/Engine/PropellantTank/CargoHold/RoomModule；需确认哪一套是最终方案并统一文档与代码
  - 代码：`../Ships3D/ShipPartDef.cs`
- [✅ 已完成][P1] 舱室部件 ↔ Map 双向同步（"扎小人"闭环）
  - 代码：`../Base/MapComp/SpaceMapGenerator.cs`、`../Ships3D/CompShipAssembler.cs`
- [✅ 已完成][P2] 部件模板设计器、飞船装配器（Window_PartTemplateDesigner / Window_ShipAssembler3D）
- [⬜ 待办][P1] 存档/读档完整验证（3D 飞船数据序列化）
  - 来源：[3D飞船系统-技术思路.md](./3D飞船系统-技术思路.md) Phase 4
- [⬜ 待办][P2] 部件 stats 接入（thrust/powerGen/heatDissipation 生效）
  - 来源：[PioneerFramework设计与计划.md](./PioneerFramework设计与计划.md) §8.2

### 3.4 地图与空间站（Phase 2）

来源：[PioneerFramework设计与计划.md](./PioneerFramework设计与计划.md) Phase 2

- [✅ 已完成][P1] Pocket Map 生成与管理
  - 代码：`../Base/MapComp/SpaceMapGenerator.cs`、`../Base/MapComp/SpaceMapComp.cs`
- [✅ 已完成][P1] 空间站创建（WorldObject + MapParent）
  - 代码：`../Base/MapComp/SpaceStationController.cs`、`../Base/MapComp/SpaceStationMapParent.cs`
- [✅ 已完成][P1] 月球表面生成（WorldGenStep + MapGeneration + 世界绘制层）
  - 代码：`../WorldGenStep/WorldGenStep_MoonTiles.cs`、`../MapGeneration/MoonSurfaceMapGeneration.cs`、`../WorldDrawLayer/WorldDrawLayer_MoonUngeneratedPlanetParts.cs`
- [⬜ 待办][P1] 空间站对接系统（Phase 2.2）
- [⬜ 待办][P1] 天体相对坐标同步（Phase 2.4）
- [⬜ 待办][P2] 3D 视图性能优化（Phase 2.5）
- [⬜ 待办][P1] 存档兼容性修复（Phase 2.6）

### 3.5 基础架构（审计遗留，详见 §5）

来源：[架构审计-20260726.md](./架构审计-20260726.md)

- [✅ 已修复][P0] PFView 轨道绘制二次缩放（审计#1）
  - 代码：`../PFView/MainTabWindow_PFView.cs`
  - 说明：2026-08-07 复核确认已修——`DrawOrbitLinesGL` 现为 Unity→米(×MetersPerUnit)→显示(×MetersToView)，与天体渲染路径数值一致；残留复核点：`GetOrbitPathUnity` 返回值单位（名字为 Unity）
- [⚪ 已确认设计][P0] 两个 3D 视图缩放因子不统一（审计#2）
  - 说明：有意为之——轨道层 `SpaceConstants.MetersPerUnit=50000`（1 Unity=50km）与装配层 `Ship3D.UNITY_TO_METERS=10`（1 Unity=10m）为两套独立比例尺，作用域不同（2026-08-07 确认）；PFView 内部自洽（飞船位置米×0.1 + 部件偏移装配单位×1）
  - 代码：`../Base/Core/SpaceConstants.cs`、`../Ships3D/Ship3D.cs`
- [⬜ 待办][P0] Morton3D 越界加报错（审计#3，当前静默返回 0 产生幽灵碰撞）
  - 代码：`../SpatialIndexing/Morton3D.cs`
- [⬜ 待办][P1] 删除 DVector3 隐式转 Vector3，改显式 `ToVector3()`（审计#4）
  - 代码：`../Base/Core/DVector3.cs`
- [⬜ 待办][P1] 修复 CombatCache 战斗期间不更新（审计#5）
  - 代码：`../Base/Game/TimeScaleManager.cs`
- [⬜ 待办][P1] 清理 SpatialGrid3D.ApplyChanges 空方法残留（审计#7，GC 实质已实现）
  - 代码：`../SpatialIndexing/SpatialGrid3D.cs`
- [⬜ 待办][P2] 简化时间缩放命令队列为直接状态切换（审计#10）
  - 代码：`../Base/Game/TimeScaleManager.cs`
- [⬜ 待办][P2] 精简 ThreadSafety 过度武装（保留 FMA 检测与性能基准，审计#11）
  - 目录：`../ThreadSafety/`
- [⬜ 待办][P2] SpatialGrid3D / CombatGrid 重复模式提取（审计#12）
- [⬜ 待办][P3] 解决 ISpatialEntity 语义冲突（审计#14）
  - 代码：`../Base/Core/ISpatialEntity.cs`、`../Base/Core/SpaceEntity.cs`
- [⬜ 待办][P3] 删除战斗重力缓存或缓存位置（审计#15）
  - 代码：`../SpatialIndexing/CombatGrid.cs`
- [⬜ 待办][P3] 炮塔旋转标注"仅视觉效果"或接入实际开火方向（审计#16）
  - 代码：`../Combat/WeaponInstance.cs`

### 3.6 工程规范

- [✅ 已完成][P3] DesignNotes 迁移至 `Source/PioneerFramework/DesignNotes/`
- [✅ 已完成][P3] 删除根目录冗余 `Defs/MainButtonDefs/MainButtons.xml`（与 `1.6/` 下逐字节相同，版本目录副本已生效）
- [✅ 已完成][P3] 清理 `.gitignore` 重复/失效规则，并追加 `.trae/` 忽略
- [⬜ 待办][P1] Toon Shader 资产管线：`Shaders/PF_Toon.shader` 当前布置运行时不可达（未打包进 AssetBundle，`Shader.Find("PioneerFramework/Toon")` 恒失败，`ToonMat` 回退 Standard）
  - 代码：`../PFView/MainTabWindow_PFView.cs`、`../../../Shaders/PF_Toon.shader`
  - 任务需求：按 shader 文件头注释要求，构建 AssetBundle 放入 `1.6/AssetBundles/` 并补加载代码；需与 shader 负责人确认后再动
  - 来源：2026-08-01 脚手架审查（问题 2）
- [✅ 已完成][P3] 修正 `DesignNotes/PioneerFramework设计与计划.md` §8.2 过时状态：战斗系统/武器系统/碰撞检测已由「未实现」改为「已实现最小闭环/增量扩展」
  - 来源：2026-08-01 全文件盘点（2026-08-05 完成）
- [⬜ 待办][P3] 核对 `ShipPartMeshGenerator` 形状覆盖与 §3.3 状态一致（Prism/Cylinder/Sphere 未实现，见 §3.3）
  - 来源：2026-08-01 全文件盘点

### 3.7 基础框架（Phase 1 遗留）

- [⬜ 待办][P1] 航天器发射系统（从地表/轨道发射飞船进入太空）
  - 说明：仅存在于组内讨论，未实装、无任何相关代码；属于 Phase 1 基础框架的规划内容，盘点时发现遗漏（2026-08-01 全文件盘点）

---

## 4. 待验证区（代码完成 ≠ 验证完成）

| 任务 | 验证方式 | 阻塞 |
|------|----------|------|
| 绕卫星船命中（T2） | 游戏内战斗测试，`[命中]` 日志 | 需进游戏 |
| 1e8m 级坐标稳定性（T3） | 远距离坐标下网格索引稳定 | 需进游戏 |
| 武器引用化后列表收缩（T4） | 摧毁武器部件后开火不索引错位 | 需进游戏 |
| 碰撞爆炸参数实测调参 | 实战触发爆炸，核对 `[爆炸]` 日志 | 需进游戏 |

---

## 5. 架构审计遗留（16 项映射）

来源：[架构审计-20260726.md](./架构审计-20260726.md)

| # | 问题 | 优先级 | 状态 | 落点 |
|---|------|--------|------|------|
| 1 | PFView 轨道绘制二次缩放 | P0 | ✅ 已修复（2026-08-07 复核，见 §3.5） | §3.5 |
| 2 | 两个 3D 视图缩放因子不统一（5000 倍） | P0 | ⚪ 已确认设计（两套比例尺作用域不同，见 §3.5） | §3.5 |
| 3 | Morton3D 越界静默返回 0 | P0 | ⬜ 未修 | §3.5 |
| 4 | DVector3 隐式转 Vector3 精度泄漏 | P1 | ⬜ 未修 | §3.5 |
| 5 | CombatCache 战斗期间从不更新 | P1 | ⬜ 未修 | §3.5 |
| 6 | GetWorldDeltaV 硬编码 GM | P1 | ⬜ 未修（源码核查：签名无 mu 参数） | §3.2 |
| 7 | SpatialGrid3D.ApplyChanges 空方法 | P1 | 🔵 GC 已实现，空壳残留 | §3.5 |
| 8 | RendezvousPlanner 扫参范围可能遗漏最优解 | P1 | ⬜ 未修 | §3.2 |
| 9 | 机动执行硬编码倒计时、缺 UI 反馈 | P2 | 🟡 部分实现（倒计时有但单位错误：tick 直接当秒显示，变速时误导；无到期通知） | §3.2 |
| 10 | 时间缩放命令队列过度工程 | P2 | ⬜ 未修 | §3.5 |
| 11 | ThreadSafety 目录过度武装 | P2 | ⬜ 未修 | §3.5 |
| 12 | SpatialGrid3D/CombatGrid 重复模式 | P2 | ⬜ 未修 | §3.5 |
| 13 | "N 体摄动"实为限制性多体（命名误导） | P3 | ⬜ 未修 | §3.2 |
| 14 | ISpatialEntity 语义冲突 | P3 | ⬜ 未修 | §3.5 |
| 15 | 战斗重力缓存加速可忽略 | P3 | ⬜ 未修 | §3.5 |
| 16 | 炮塔旋转纯粹装饰 | P3 | ⬜ 未修 | §3.5 |

---

## 6. 未实现模块（未开始 ≠ 未完成）

| 模块 | 内容 | 对应 Phase |
|------|------|-----------|
| 地表探索 | 行星着陆/起飞、地表移动、资源采集、前哨站、天地统一网格 | Phase 4 |
| 经济与外交 | 贸易路线、势力关系、任务事件 | Phase 5 |
| 接舷战 | 进入敌舰舱室展开 2D Map 战斗 | Phase 3 延伸 |
| 残骸打捞 | 战败方飞船变为静态物体，对接打捞 | Phase 3 延伸 |
| 电子战 | 信号矩阵/覆盖网格（技术储备已有，不排期） | 未来 |
| 航天器发射 | 地表/轨道发射飞船进入太空（属 Phase 1 规划，盘点遗漏，见 §3.7） | Phase 1 |

---

## 7. 文档索引（设计知识库，防遗弃）

| 文档 | 定位 | 关联模块 |
|------|------|----------|
| [ToDoList.md](./ToDoList.md) | 战斗系统改造专项执行清单（物理时间基准统一 + 碰撞/爆炸） | 战斗系统 |
| [PioneerFramework设计与计划.md](./PioneerFramework设计与计划.md) | 项目总设计：定位、核心模块、Phase 规划、扩展接口、性能规范 | 全部 |
| [架构审计-20260726.md](./架构审计-20260726.md) | 2026-07-26 全项目架构审计，16 项问题与修复优先级 | 基础架构 |
| [太空战斗系统-设计思路.md](./太空战斗系统-设计思路.md) | 战斗系统详细设计：四类武器、碰撞五层过滤、性能优化 | 战斗系统 |
| [3D飞船系统-技术思路.md](./3D飞船系统-技术思路.md) | 飞船装配系统技术思路：3D 视口、框架质量、舱室↔Map 同步 | 飞船装配 |

---

## 8. 变更日志

| 日期 | 变更 |
|------|------|
| 2026-08-01 | 创建本文件；DesignNotes 迁移至 `Source/PioneerFramework/DesignNotes/`；盘点 5 份文档 + 源码，修正设计与计划.md 中已过时的战斗状态；建立审计 16 项状态映射 |
| 2026-08-01 | 脚手架审查（布置 + 构建配置）：删除根目录冗余 `Defs/`、清理 `.gitignore` 重复规则、追加 `.trae/` 忽略；登记 Toon Shader 资产管线待办（P1，需与 shader 负责人确认） |
| 2026-08-01 | 全文件盘点：登记文档与代码不一致问题 —— 设计与计划.md §8.2 战斗状态过时（§3.6 待办）；ShipPartMeshGenerator 仅实现 Cube/SphericalCorner（§3.3 状态降级为进行中）；四类武器 Def 已定义但代码仅 Projectile 生效（§3.1 补充说明） |
| 2026-08-01 | 补记 Phase 1 遗漏项：航天器发射系统（未实装、无代码），新增 §3.7 待办，Phase 1 状态降级为进行中 |
| 2026-08-05 | 架构讨论定调"无边界战斗"：战斗是世界连续模拟的一部分，无战斗实例/生命周期（排除 CoDE 式领域展开）。同步更新 太空战斗系统-设计思路.md（补「架构立场」章节 + 战斗子 Grid 对齐 CombatGrid.cs 实际实现 + 「未来方向」三层模拟 tier / 区域 TiDi / 兴趣管理 / 确定性）与 PioneerFramework设计与计划.md §2.3 / §8.2 过时状态；新增 §3.1 三层模拟 tier / 区域 TiDi 待办 |
| 2026-08-06 | 源码核查同步：审计#6 降级（GetWorldDeltaV 未实际修复，§3.2/§5 状态改 ⬜）；战斗 AI 升级为进行中（基础版已实现，§3.1/§2）；审计#9 补充倒计时单位 bug 说明（§3.2/§5）；新增 4 项待办——OBB 真实求交（§3.1）、ShipPartShape.Weapon 补 case（§3.3）、枚举与文档对齐（§3.3）、ManeuverQueueWindow 单位修正（§3.2）；修正 ShipPartMesh 形状描述（枚举 7 值，非 Prism/Cylinder/Sphere）；新增 §9 集中核查快照（模块完成度 + 4 项偏差详细记录 + 审计 16 项逐项核查表 + 文件清单） |

## 9. 2026-08-06 源码核查记录

> 本节为 2026-08-06 源码核查的完整快照，作为可追溯审计记录。§3 各模块条目是待办执行视图，本节是状态核查视图，两者互补。

### 9.1 核查范围与方法

- **范围**：遍历 `Source/PioneerFramework/DesignNotes/` 全部 5 份文档 + 关键源码文件（Combat/Orbital/Ships3D/SpatialIndexing/PFView/Game 模块）
- **方法**：逐项核对 2026-07-26 架构审计 16 项 + MasterPlan §3 各模块标注 vs 实际源码
- **原则**：只读核查，未修改源码/Defs/csproj，未进游戏实测；遵循「以瞎猜接口为耻，以认真查询为荣」
- **产出**：4 项文档-代码偏差 + 审计 16 项逐项核查结果 + 模块完成度速览 + 文件清单

### 9.2 模块完成度速览

| 模块 | 状态 | 关键证据 |
|------|------|----------|
| 轨道物理 | ✅ 主体完成 | `NBodyIntegrator`（限制性 N+1 体）、`ManeuverPlanner`、`OrbitTransferPlanner`、`RendezvousPlanner`、Gooding Lambert 求解器全部实装 |
| 飞船装配（3D） | 🔵 进行中 | 3D 视口 + OBB 空间允许 + 框架质量 + 舱室↔Map 双向同步已完成；Mesh 生成仅 3 种独立网格（Cube/SphericalCorner/RoomModule） |
| 战斗系统 | 🔵 主体完成，4 项待验证 | 射弹 + 激光 + 多格注册 + 速度分层碰撞 + 爆炸 + 基础 AI 实装；导弹/无人机仅 Def 已定义；碰撞实为 AABB slab |
| 地图与空间站 | 🔵 进行中 | Pocket Map、空间站、月球表面生成已完成；对接/同步/存档待办 |
| 地表探索 | ⬜ 未开始 | 无代码 |
| 经济与外交 | ⬜ 未开始 | 无代码 |
| 航天器发射 | ⬜ 未开始 | 无代码（Phase 1 遗漏项） |

### 9.3 文档-代码偏差（本次新发现，MasterPlan 此前未登记）

#### 偏差 A：碰撞检测实为 AABB slab，非真正 OBB 旋转求交

- **证据**：`Source/PioneerFramework/Combat/CombatManager.cs:258-348` 的 `CheckSegmentGroupHit` 调用 `CombatGrid.RaySlabIntersection`（AABB slab 检测，位于 `SpatialIndexing/CombatGrid.cs:582`）；Part 级碰撞用 `entity.QueryRayHit(localOrigin, localDir, ...)` 在局部坐标查询（L303），无带旋转的 OBB 线段求交
- **影响**：[太空战斗系统-设计思路.md](./太空战斗系统-设计思路.md) 描述的"OBB 线段求交"未真正实现；部件有旋转时碰撞精度下降
- **处置**：§3.1 新增待办「碰撞检测升级为真正 OBB 旋转求交」（P1）

#### 偏差 B：ManeuverQueueWindow 倒计时单位错误

- **证据**：`Source/PioneerFramework/Base/Orbital/ManeuverQueueWindow.cs:64-67` 把 `remainingTicks`（tick 数）直接格式化为 `T-{N} 秒`
- **影响**：TimeScaleManager 启用变速时（Combat=1/60x、Fast=8x），tick 数与真实秒数不对应，显示会误导玩家
- **处置**：§3.2 审计#9 补充说明 + §5 审计表 #9 状态改为 🟡 部分实现

#### 偏差 C：ShipPartShape 枚举与文档描述不符

- **证据**：`Source/PioneerFramework/Ships3D/ShipPartDef.cs:37-46` 枚举为 `Cube, SphericalCorner, Weapon, Engine, PropellantTank, CargoHold, RoomModule`（7 个）；[3D飞船系统-技术思路.md](./3D飞船系统-技术思路.md) 描述的 Prism/Cylinder/Sphere 在枚举中不存在；`ShipPartMeshGenerator.cs:17-30` 的 `GetMesh` switch 中 `Weapon` 无 case，静默回退 Cube
- **影响**：文档与代码形状集对不上；`Weapon` 形状静默回退 Cube，无任何警告
- **处置**：§3.3 修正 ShipPartMesh 描述 + 新增 2 条待办（Weapon 补 case P2、枚举与文档对齐 P3）

#### 偏差 D：战斗 AI 实际已实现（基础版）

- **证据**：`Source/PioneerFramework/Combat/TargetingSystem.cs` 实现 `FindBestTarget`（L37，按距离评分）、`EvaluateTarget`（L64）、`EngageTarget`（L82，遍历武器开火）；逻辑简单，在 `SpaceConstants.CellSizeMeters`（1,000 km）范围内选最高分目标
- **影响**：MasterPlan §2 阶段总览原写"Phase 3 AI 未实现"，实际已有基础 AI
- **处置**：§2 Phase 3 改为「基础 AI 已实现，状态机未实现」；§3.1 战斗 AI 状态机项改为 🔵 进行中

### 9.4 审计 16 项逐项核查结果

| # | 问题 | 优先级 | 核查状态 | 核查依据（文件:行号） |
|---|------|--------|----------|----------------------|
| 1 | PFView 轨道绘制二次缩放 | P0 | ⬜ 未修 | `PFView/MainTabWindow_PFView.cs:994-1001` 仍 `* MetersPerUnit` 后再 `* MetersToView` |
| 2 | 两视图缩放因子差 5000 倍 | P0 | ⬜ 未修 | `MetersToView=0.1`（PFView L25） vs `UnitsPerMeter=1/50000`（`Base/Core/SpaceConstants.cs:8`） |
| 3 | Morton3D 越界静默返回 0 | P0 | ⬜ 未修 | `SpatialIndexing/Morton3D.cs:18-23` 无 Log.Warning |
| 4 | DVector3 隐式转 Vector3 | P1 | ⬜ 未修 | `Base/Core/DVector3.cs:81` 仍 `implicit operator Vector3` |
| 5 | CombatCache 战斗期间不更新 | P1 | ⬜ 未修 | `Base/Game/TimeScaleManager.cs:283-336` 仅初始化时计算一次；`GameComponentTick:202-214` 仅更新 combatSubTick 计数器 |
| 6 | GetWorldDeltaV 不接受 mu | P1 | ⬜ 未修 | `Base/Orbital/OrbitMath.cs:571-579` 签名无 mu 参数 |
| 7 | SpatialGrid3D.ApplyChanges 空方法 | P1 | ⬜ 未修 | `SpatialIndexing/SpatialGrid3D.cs:171` 仍空体（注：`DrainEmptyCells` L132-151 已实现，但 ApplyChanges 入口仍是 stub） |
| 8 | RendezvousPlanner 扫参范围 | P1 | ⬜ 未修 | `Base/Orbital/RendezvousPlanner.cs:274-276` 步长 0.1/0.05，无局部细化 |
| 9 | 机动到期 UI 反馈 | P2 | 🟡 部分实现 | `Base/Orbital/ManeuverQueueWindow.cs:64-67` 有倒计时但 tick 当秒显示；无到期通知 |
| 10 | 时间缩放命令队列过度工程 | P2 | ⬜ 未修 | `Base/Game/TimeScaleManager.cs` 仍 `Queue<TimeScaleCommand>` + 约 200 行基础设施 |
| 11 | ThreadSafety 过度武装 | P2 | ⬜ 未修 | `ThreadSafety/` 目录下 9 个文件，`RaceConditionDetector` 仍存在 |
| 12 | SpatialGrid3D/CombatGrid 重复 | P2 | ⬜ 未修 | 两类结构相似无共享抽象 |
| 13 | NBody 命名误导 | P3 | ⬜ 未修 | `Base/Orbital/NBodyIntegrator.cs:75` 仍 `MultiBodyAccelJerk`（实为限制性 N+1 体） |
| 14 | ISpatialEntity 语义冲突 | P3 | ⬜ 未修 | SpaceEntity 与 SpaceEntityGroup 都实现 ISpatialEntity 但语义不同 |
| 15 | 战斗重力缓存加速可忽略 | P3 | ⬜ 未修 | `SpatialIndexing/CombatGrid.cs:249-275` 仍缓存重力源列表 |
| 16 | 炮塔旋转纯装饰 | P3 | ⬜ 未修 | `WeaponInstance` 有 `UpdateTurretRotation` 但 `FireWeapon` 忽略 |

**汇总**：16 项中 15 项未修复，1 项部分修复（#9）。代码与 2026-07-26 审计文档记录的状态基本一致，审计指出的所有架构问题在当前源码中仍然存在。

### 9.5 文件清单速览

| 类别 | 路径 | 说明 |
|------|------|------|
| 主跟踪文档 | `Source/PioneerFramework/DesignNotes/MasterPlan.md` | 统筹主文档（2026-08-01 创建） |
| 战斗专项清单 | `Source/PioneerFramework/DesignNotes/ToDoList.md` | 战斗系统改造执行清单 |
| 架构审计 | `Source/PioneerFramework/DesignNotes/架构审计-20260726.md` | 16 项问题快照 |
| 项目总设计 | `Source/PioneerFramework/DesignNotes/PioneerFramework设计与计划.md` | 定位/模块/Phase 规划（§8.2 战斗状态过时） |
| 战斗设计 | `Source/PioneerFramework/DesignNotes/太空战斗系统-设计思路.md` | 四类武器/碰撞五层/性能优化 |
| 飞船设计 | `Source/PioneerFramework/DesignNotes/3D飞船系统-技术思路.md` | 3D 装配/框架质量/扎小人 |
| 源码根 | `Source/PioneerFramework/` | Base/Combat/Ships3D/SpatialIndexing/PFView/MapGeneration/WorldGenStep 等模块 |
| 运行时资产 | `1.6/` | Defs/AssetBundles/Textures/Patches |
| Shader 工具 | `Shaders/` + `Tools/PFViewShaderBundle/` | Toon/Outline shader（Toon 未打入 AssetBundle，运行时不可达） |
>>>>>>> 0fe8dfff914de5317379bade80f5be93ec038947
