# 先驱框架Lite（PioneerFramework Lite）TODO

> 创建日期：2026-08-10
> 定位：PFL 粒度任务清单，从 [Lite规划-20260808.md](./Lite规划-20260808.md) 里程碑 M3-M5 拆解而来。
>
> **状态标记**：⬜ 待办 ｜ 🔵 进行中 ｜ 🟡 待验证 ｜ ✅ 已完成 ｜ ⚪ 已废弃
>
> **优先级**：P0 影响正确性 ｜ P1 架构/功能 ｜ P2 清理简化 ｜ P3 文档

---

## 1. M3 游戏内验证（编译/测试已通过，待进游戏）

> M2 已完成源码裁剪 + 编译 0 错误 + 5 个回归测试脚本全部通过。M3 需进游戏实测轨道/月球/单 part 船/空间站/对射闭环。
> **Phase 归属**：M3 验证任务由 [PFL闭环规划 §6](./PFL闭环规划-20260810.md) **各 Phase 验收** 共同吸收（P1/P2/P3 验收步骤中的游戏内实测都包含 M3 条目）。

| # | 优先级 | 任务 | 验证方式 | Phase | 状态 |
|---|--------|------|----------|-------|------|
| M3-1 | P0 | 轨道物理链路游戏内验证 | 进游戏生成地月系，观察 N 体积分/圆轨保持/机动规划/Lambert/交会 | P1 验收 | ✅ 已完成 |
| M3-2 | P0 | 月球表面生成验证 | 进游戏触发月球地表生成，检查地形/生物群系 | P1 验收（着陆） | ⬜ 待办 |
| M3-3 | P0 | 单 part 船（空间站）形态验证 | CompDebugShipSpawner 部署空间站，进出 Pocket Map | P1 验收 | ✅ 已完成 |
| M3-4 | P0 | 空间站对射验证 | Window_CombatTest 触发对射，检查 [命中] 日志 + 单 part slab 命中 | P1 验收（F2/F3） | ✅ 已完成（2026-08-29 日志实证：六面出膛→物理弹→[命中Part!]→贯穿生成） |
| M3-5 | P1 | PFView 描边渲染验证 | MainTabWindow_PFView 打开，检查 ToonOutline 三平台 AssetBundle | P3-4 | ⬜ 待办 |

---

## 2. M4 首发功能目标（F1-F3 玩法闭环 + DMS:NF 接入点冻结）

> Lite 首发最小玩法闭环：**变轨（F1）→ 对射（F2）→ 受击反馈（F3）**。详见 [Lite规划-20260808.md §3.3](./Lite规划-20260808.md)。
> **扩展**：同步 DMS:NF 设计文档（2026-08-10）后，M4 追加「DMS:NF 接入点接口冻结」与「燃料/查询/再入/敌对注册最小实现」，详见 [PFL闭环规划 §6 P1/P2](./PFL闭环规划-20260810.md)。

### F1. dV 体系（空间站/单 part 卫星/导弹携带 dV，机动消耗变轨）

| # | 优先级 | 任务 | 说明 | Phase | 状态 |
|---|--------|------|------|-------|------|
| F1-1 | P0 | dV 补给路径实现 | `SpaceEntity` 有 `MaxDeltaV`/`RemainingDeltaV`/`TryConsumeDeltaV`/`RefillDeltaV`，但无补给/生成路径（测试硬编码 1e7）；需实现 Map 内补给（如推进剂罐建筑/调试按钮） | P1-4（调试级）→ P2-2（正式化） | ⬜ 待办 |
| F1-2 | P0 | 机动执行消耗 dV 链路验证 | `ConsumeGroupDeltaV` 已就绪（Lite规划声明）；需验证机动规划队列 + 缓释燃烧 + `RemainingDeltaV` 消耗闭环 | P1 验收 | ⬜ 待办 |
| F1-3 | P1 | 导弹/无人机 dV | `WeaponDef` missile*/drone* 字段保留，导弹为无 Map 纯物理 part（`linkedMapId=-1`），需确认导弹携带 dV 并消耗 | P2 | ✅ 已实测（2026-09-07 导弹闭环：领弹满罐注入 2000kg 工质 + 主网/RCS 双工质 burn 扣料、燃尽惯性弹 K5、命中时残余 ~1996kg——消耗链正确） |
| **F1-4** | **P1** | **燃料 ↔ dV 定量换算** | 实现 `ChemfuelToDeltaVMapper`：RW `Thing.Chemfuel` stackCount → 定量 dV → 调用 `RefillDeltaV`；换算系数做成可配置（初稿 1 格=2 单位仅供参考，消耗指标待 DMS:NF 定） | P2-6 | ⬜ 待办 |

### F2. 空间站开火（Map 炮塔 Building → 3D 射弹）

> ✅ 2026-08-29 全部完成（整体式炮塔改造）：单炮塔 360° 无射界，出膛点 = 炮口射线 ∩ 本船 AABB 六面（MapCoordMapper.RayExitAabb，与 F3 命中共用 slab 几何），物理弹在出膛碰撞点生成；Map 侧有出膛视觉弹（PFProjectile3D.visualOnly）。详见 [炮塔实现记录-20260819.md §5](./炮塔实现记录-20260819.md)。

| # | 优先级 | 任务 | 说明 | Phase | 状态 |
|---|--------|------|------|-------|------|
| F2-1 | P0 | CombatManager 新增「Map 炮塔开火」方法 | 复用 `FireWeapon` 的 groupId + targetPosition + spawnPosition 语义，传入炮塔在 3D 空间对应的 spawn 位置与目标，直接生成 `ProjectileInstance` | P1-5 | ✅ 已完成（`FireFromMapBuilding`） |
| F2-2 | P0 | Map 炮塔 Building 桥接 | 炮塔建筑（Building）调用上述方法；与已删 `CompNozzle` 同构（地图建筑↔太空实体桥接） | P1-5 | ✅ 已完成（`CompMapTurret` + `CompMapDesignator`：索敌/齐射/出膛点/精确炮击/标记） |
| F2-3 | P1 | 炮塔 Building Def 定义 | 需在 `1.6/Defs/` 下定义炮塔建筑 ThingDef（或复用现有 `WeaponDefs.xml`） | P1-5 | ✅ 已完成（`PF_Building_Turret_Cannon` 整体式 + `PF_Building_TargetDesignator`） |

### F3. 空间站被打中 → Map 内对应 part 位置生成爆炸

> ✅ 2026-08-29 基本版完成：命中 → Map 三维贯穿弹（PFProjectile3D）+ 落点建筑 Bomb 伤害 + 爆闪视觉。范围爆炸/pawn 伤害因太空 Pocket Map 上 `GenExplosion` NRE 后置（P3 二次伤害）。

| # | 优先级 | 任务 | 说明 | Phase | 状态 |
|---|--------|------|------|-------|------|
| F3-1 | P0 | 3D 命中点 → part 关联 Pocket Map 内坐标映射 | `CombatManager.CheckSegmentGroupHit` 命中后，将 3D 命中点（`hitEntryPoint`）映射到 `part.linkedMapId` 对应 Map 内坐标 | P1-3（复用 MapCoordMapper） | ✅ 已完成（`MapCoordMapper.ShipLocalToMapWorld`，日志 `[贯穿生成]`） |
| F3-2 | P0 | Map 内爆炸生成 | 在映射坐标处生成爆炸（视觉 + 伤害反馈），复用 RW 爆炸机制 | P1-6 | ✅ 已完成（贯穿弹 + 落点建筑 Bomb + FleckMaker 爆闪；范围爆炸后置） |
| F3-3 | P1 | 无 Map part 命中反馈 | 导弹等无 Map part（`linkedMapId=-1`）命中后无 Map 反馈，需确认仅 3D 损伤 | P1 验收 | 🟡 部分验证（2026-09-07 导弹命中链实测：对有 Map 空间站目标 slab HIT → part 入口点 → 3D AoE 全链；无 Map 纯物理船目标命中反馈仍未专项测试） |

### F4. DMS:NF 接入点接口冻结 + 最小实现（2026-08-10 同步 DMS:NF 文档新增）

> 为 DMS:NF（火箭型号 CV-1/CV-3 可用、CV-8/10/12 暂缓、11 款载荷、控制终端、敌对轨道事件）提前冻结 PFL 机制层接口。P1 冻结签名（空实现），P2/P3 正式化。

| # | 优先级 | 任务 | 说明 | Phase | 状态 |
|---|--------|------|------|-------|------|
| F4-1 | P1 | **火箭分级钩子接口冻结** | 定义 `IRocketStageHooks`：`StageSeparate`/`StageRecover`/`StageReturnHome`；发射/推进/销毁关键节点回调空实现 | P1-8 | ⬜ 待办 |
| F4-2 | P1 | **载荷承载/部署接口冻结** | 定义 `IPayloadCarrier`（承载列表/容量）+ `IPayloadDeployer`（`Deploy(payloadDef, targetOrbit)→SpaceEntity`）；调试返回占位实体 | P1-9 | ⬜ 待办 |
| F4-3 | P1 | **轨道实体只读查询 API 最小实现** | `OrbitalEntityCatalog`：枚举/ByDefName/ByFaction/ByType；返回快照对象（位置/速度/健康/剩余 dV） | P2-7 | ⬜ 待办 |
| F4-4 | P1 | **再入路径 API 最小实现** | `ReentryTrajectory.Calculate(entity, targetMapPos)` → 预计落点+时间；供 DMS:NF 弹道导弹/返回舱调用 | P2-8 | ⬜ 待办 |
| F4-5 | P1 | **敌对轨道实体注册 + 事件钩子（最小实现）** | `HostileOrbitalRegistry.Register(...)` + `IOrbitalIncidentEvent.Fire(incidentDef)` → 生成敌对 `SpaceEntity` 并加入战斗目标池 | P2-9 | ⬜ 待办 |
| F4-6 | P1 | **火箭分级/回收正式化实现** | `IRocketStageHooks` 回调时机 + 默认实现；对接 DMS:NF 各型号（CV-1/CV-3）分级/回收配置 | P3-6 | ⬜ 待办 |
| F4-7 | P1 | **载荷部署正式化实现** | `IPayloadCarrier`/`IPayloadDeployer` 部署创建卫星/模块实体并入轨；对接 DMS:NF 11 款载荷（7 民用+4 军用） | P3-7 | ⬜ 待办 |

---

## 3. M5 Defs 精简 + 发布打包

> **Phase 归属**：M5 打包任务对应 [PFL闭环规划 §6 P3](./PFL闭环规划-20260810.md) 收尾阶段。

| # | 优先级 | 任务 | 说明 | Phase | 状态 |
|---|--------|------|------|-------|------|
| M5-1 | P1 | Defs 精简复核 | M2 已删 ShipPartDefs/装配 ThingDef/Nozzle/Mars；复核无残留 full 版 Def | P3-5（发布包） | ⬜ 待办 |
| M5-2 | P1 | Toon Shader 资产管线确认 | `PF_Toon.shader` 是否打入 AssetBundle？归档 MasterPlan 标记 `Shader.Find` 恒失败，需核查 PFL 是否修复 | P3-4 | ⬜ 待办 |
| M5-3 | P2 | 发布包结构 | 按 [Lite规划 §6.1](./Lite规划-20260808.md) 目录结构打包（About/1.6/Assemblies/Source/Defs） | P3-5 | ⬜ 待办 |
| M5-4 | P2 | 存档前缀独立性确认 | `About.xml` 已声明独立 `packageId`，需确认存档不与 master 冲突 | P3-3（存档验证） | ⬜ 待办 |
| M5-5 | P1 | **DMS:NF 接入点文档（9 类）交付** | 输出「发射/着陆/资源组件/炮塔建筑 Def/对接转移/火箭分级钩子/载荷接口/燃料映射/轨道实体查询/敌对事件钩子」共 9 类接入文档，作为发布包的 DesignNotes 附录 | P3-1 | ⬜ 待办 |

---

## 4. 遗留项（从 full 审计中筛 Lite 仍适用项）

> 来源：[完整版归档/架构审计-20260726.md](./完整版归档_我们这是lite所以一般不用看/架构审计-20260726.md)。仅登记 Lite 范围内仍适用的项，其余 full 审计项不迁移（Lite 是独立线，范围已收敛）。

| # | 优先级 | 任务 | 来源 | 状态 |
|---|--------|------|------|------|
| L-1 | P2 | ManeuverQueueWindow 倒计时单位修正 | 归档审计#9：`remainingTicks` 直接当秒显示，变速时误导 | ⬜ 待办 |
| L-2 | P3 | PFView 轨道绘制缩放复核 | 归档审计#1：full 已修，PFL 需确认同步 | ⬜ 待办 |

---

## 5. 发射台总装（Design v1 拆解）

> 来源：《航天器地面建设与发射流程设计v1.md》（仓库根目录）。
> 目标：把“发射台就是总装台”的流程跑通：设计 → 框架施工 → 口袋地图装修 → 发射入轨。
> 状态标记沿用本文件顶部约定；下面这些是从设计稿拆出来的目标，做完一项就更新一项状态。

| # | 优先级 | 任务 | 说明/验收 | 状态 |
|---|--------|------|-----------|------|
| LA-1 | P0 | 发射台多格可建造区域 | 原版只允许中心 1 格；改为多格，可建造区域由发射台等级/大小决定；修复燃料口定位后中心 3×3 可建 | ✅ 已完成 |
| LA-2 | P0 | 发射台状态机 | 空闲/设计中/建造中/已完工；状态与关联建筑写入存档 | 🔵 进行中 |
| LA-3 | P0 | 装配界面 | 航天器管理界面已落地，设计页支持基础编辑：框架尺寸/Part/载荷/舱室类型；完整可视化编辑器待后续 | 🔵 进行中 |
| LA-4 | P0 | 设计校验规则 | 框架正方形且在发射台范围内；至少 1 推进器 + 1 储罐；载荷不超安装位；未确认不能施工 | ⬜ 待办 |
| LA-5 | P0 | 未完工火箭框架生成 | 发射台中心生成大型 Building，挂 `CompUnfinishedRocket`，持有 spec；已改为原版 Blueprint→Frame 流程并注入 `CompRocketBlueprint` | ✅ 已完成 |
| LA-6 | P0 | 原版建造菜单施工 | 框架一次性施工到 100%，施工过程代表 Part 建造；已改为原版 Blueprint，pawn 先搬运建材再施工 | ✅ 已完成 |
| LA-7 | P0 | 施工完成切换已完工火箭 | 同一 Building 切换为已完工火箭，进入下一阶段；已接调试“标记完工”+ `FrameCompleteConstructionPatch` 正常完工回调，通知发射台进入 Completed | ✅ 已完成 |
| LA-8 | P1 | 框架拆除/取消设计 | 取消设计已接入管理界面并限制在未完工阶段；框架拆除与材料返还待后续 | 🔵 进行中 |
| LA-9 | P1 | 航天器地图生成 | 已改为 `SpacecraftMapParent` 平级地图 + `SpacecraftMapRegistry`；当前 50×50 基础舱室布局，完整船体轮廓待后续 | 🔵 进行中 |
| LA-10 | P1 | 航天器地图与火箭关联 | 已实现：`SpacecraftMapParent` + `SpacecraftBridge` + 脐带塔；原版 `Dialog_EnterPortal` / `JobDriver_EnterPortal` / `PortalContainerProxy` 登船与物品转移；发射后地图随航天器进入 InSpace；待做：完整船体布局与持久乘员舱 | 🔵 进行中 |
| LA-11 | P1 | 对接口预留 | 为 Pawn↔Pawn、Pawn↔非Pawn、非Pawn↔非Pawn 三种对接预留结构 | ⬜ 待办 |
| LA-12 | P0 | 发射条件校验 | 至少 1 有人控制台；房间封闭；燃料足够；气体系统可选；`RocketLaunchConditions` 校验器已落地，正式控制台 Def 待 DMS:NF 接入 | 🔵 进行中 |
| LA-13 | P1 | 燃料加注 | 玩家把燃料搬到发射台，写入 Part 储罐参数；保留 RimPipe 扩展点 | ⬜ 待办 |
| LA-14 | P0 | 发射入轨 | 调试级已打通：主地图火箭消失、发射台保留，生成/关联 `SpaceEntity` + 单 `SpacePart`；本轮已补：发射条件校验（`RocketLaunchConditions`）、Part 虚拟推进注入、正式入轨机动（`BeginCircularize` 两段式圆轨化+工质消耗）、读档后推进源重连 | ✅ 已完成 |
| LA-15 | P1 | Part 数据建模 | Part 不是地面/地图实体，数据保存在 `RocketDesignSpec`；施工代表建造 | ✅ 已完成 |
| LA-16 | P2 | 检修口 | 口袋地图内可建造；可指定检修 Part 类型；维护 Job 抽象恢复耐久/消耗材料时间 | ⬜ 待办 |
| LA-17 | P2 | 载荷配置与入轨 | 装配界面配置类型+数量+安装位置；首发只保存并随火箭入轨 | ⬜ 待办 |
| LA-18 | P0 | 存档 | 保存 `RocketDesignSpec`、发射台状态、口袋地图、检修口设置；已实现：`RocketDesignSpec` 与虚拟推进参数由 `SpaceMapComp` 持久化并在读档后重建；待做：发射台状态/检修口完整存档验证 | 🔵 进行中 |
| LA-19 | P3 | 未来扩展点 | 拖运平台、自定义口袋地图长宽、多 Part/多格、对接、载荷部署、燃料/RimPipe、Part 损耗/故障、火箭回收/退役 | ⬜ 待办 |
| LA-20 | P1 | 人员/建筑在航天器地图中的装载 | 已接入原版 `Dialog_EnterPortal` 选择人员/物品 + `PortalContainerProxy` 物品投放；航天器地图可返回地面；待做：预置舱段建筑、建筑运输、完整登船调度 | 🔵 进行中 |
| LA-21 | P0 | 统一发射方式管理 | `RocketLaunchManager` + `RocketLaunchMode` + `Dialog_RocketLaunch`；火箭/发射台统一入口；PFL 火箭隐藏原版发射 Gizmo；支持原版 TransportPod 与 PFL 入轨两种方式；记住上次选择；原版/PFL 条件分开校验；已有“强制发射 PFL (调试)”Gizmo。待做：游戏内验证、原版发射后的地图处置（依赖设计界面） | 🔵 进行中 |

---

## 6. 性能优化（2026-08-31 全面性能审查）

> 来源：对轨道物理 / 空间索引 / 战斗 / 跳帮 / 制导全链的性能审查，重点核查「算法是否性能最优化」。
> 结论：无正确性缺陷，但存在若干热路径冗余（无条件日志、O(n²) 索敌、重复向量正交基计算），不影响功能，仅在大量实体时放大 GC/CPU 开销。

| # | 优先级 | 任务 | 说明 | 状态 |
|---|--------|------|------|------|
| PERF-1 | P1 | 战斗日志门控 | `CombatManager.cs` 共 37 处 `Log.Message`，大量无条件输出（`[射弹脱靶]`、`[命中!]`、`[ShipCollision]`、`[爆炸]`、`[Part摧毁]` 等），生产运行时逐条拼字符串 → GC + I/O。另有 `TargetingSystem.cs` `[锁定]` 日志（60tick 限频但未挂调试开关）。修复：统一挂 `DebugDetailedCombat`/`DebugDetailedCollision` 门控，非调试不拼字符串 | ✅ 已完成 |
| PERF-2 | P1 | `CombatGrid.WalkSegmentCells` 空格提前中断 | DDA 走到无船的格（不在 cells 字典）就中断，可能漏掉空格之后的船。修复：cell 边界由坐标直接推导（cell 索引 × cellSize），不再依赖 cells 字典查格 | ✅ 已完成 |
| PERF-3 | P1 | `CelestialBodyManager.GetOrbitFrame` 重复正交基 | `GetOrbitFrame` 每次调用都 `normalized + cross ×2` 重算 refX/refY，N 体积分热路径多处调用。修复：在 `CelestialBodyDef` 懒加载缓存规范化轨道法线 + refX/refY（`EnsureOrbitFrame`/`GetOrbitFrame`），`CelestialBodyManager` 5 处调用改走缓存 | ✅ 已完成 |
| PERF-4 | P2 | `TargetingSystem.FindBestTarget` O(n²) 索敌 | 每 tick 对 N 个 group 双重遍历；N 大时 O(n²)。修复：用 `SpaceGameComp.GetGroupsInRange`（粗网格 `SpatialGrid3D.GetNearby`）按 CellSizeMeters 预过滤候选，候选数从 N 降到近邻 k | ✅ 已完成 |
| PERF-5 | P2 | `Parallel.ForEach` 小规模回退顺序 | `SpaceGameComp.UpdateAllMovements` 对 `groups.Values` 用 Parallel.ForEach + ConcurrentBag；group 数少时线程调度 + ConcurrentBag 分配 > 顺序开销。修复：`PARALLEL_PHYSICS_MIN_GROUPS = 32` 阈值以下走顺序循环 + 预分配数组 | ✅ 已完成 |

---

## 7. 已知问题（已处理）

| 问题 | 日志 | 状态 |
|------|------|------|
| `Could not resolve cross-reference: No Verse.ThingDef named Steel100 found to give to Verse.ThingDefCountClass (1x null)` | 游戏启动加载 Def 阶段出现 | ✅ 已查明并修复：`PF_RocketRecovery_Basic.xml` 的 `rebuildCost` 误用 `<li><thingDef>Steel</thingDef><count>100</count></li>`，RimWorld 的 `ThingDefCountClass` 会把 `<li>` 的 InnerText 拼成 `Steel100` 当作 defName 解析；已改为正确的 `<Steel>100</Steel>` |
| `Drop pod left the map, but its group ID is -1` | 测试火箭发射后离港动画结束时报错 | ✅ 已修复：PFL 的离港 `FlyShipLeaving` 只是视觉演出，未走原版 `CompLaunchable.TryLaunch` 初始化 groupID/目的地；现在生成离港 Skyfaller 时放入 `activeTransporterDef` 并设置 `createWorldObject=false`，不再尝试创建真实运输舱世界物体 |
| `Type CompThruster probably needs a StaticConstructorOnStartup attribute, because it has a field flameMaterial of type Material` | 启动期静态资源检查警告 | ✅ 已修复：`CompThruster` 添加 `[StaticConstructorOnStartup]`，`flameMaterial` 改为 `static readonly` 并在静态构造函数中创建 |

## 8. 变更日志

| 日期 | 变更 |
|------|------|
| 2026-09-09 | **拆站伤害语义定稿 + Map 爆炸半径 5 格（用户拍板）**：①拆站 = 弹头进图拆建筑即细化伤害（Map 无血条、站体 part 维持不可摧毁；"关键建筑被毁 → 站毁灭"链后续登记）；②新字段 `MissileDef.mapBlastRadiusCells`=5（格）经 ProjectileInstance 透传至 SpawnMapWarheadBlast，与 3D AoE 1500m 分口径（成员间距/冲量豁免等不变量全在 3D 口径，不受影响）。纯测试 43/43，DLL 00:41。详见《导弹系统设计-20260830.md》§0 十六轮 |
| 2026-09-09 | **"被打即脱队，残组照飞"语义补全（A+B+C）**：①拦截路径换序（先摘除后引爆）；②自家爆炸豁免自家组（`excludeGroupId` 贯穿 ApplyExplosionDamage/Impulse/ExplodeAtWorldPoint/拦截/自毁/成员弹头 `sourceGroupId`）——队列中间成员被打不再以群质心冲量踢飞残组；③AoE 击毁当场脱队（ApplyExplosionDamage 收集-执行模式；`TriggerExplosion` 延后销毁）+ 冲量空组守卫（防除零）。编译 0 错误，DLL 00:13。⚠️ 待游戏内验证。详见 §0 十五轮 |
| 2026-09-08 | **多实体目标锁定改随机+缓存（用户定稿）**：零偏置打合并弹群从"每步选最近"（O(n) 距离比较）改为随机锁定一枚、交战期缓存（`aimEntityId` 瞬态）——每步只剩 O(1) 查询；锁定实体离群/换目标自动重选。一次性 [导弹瞄准] 日志。DLL 23:46。详见 §0 十四轮 |
| 2026-09-08 | **合并期被撞场景实测 + 零偏置瞄质心缺口修复**：探针弹收敛到合并弹群质心（= 队列空当）两次穿过编队零接触（逐实体解析放行正确）；再捕获后撞自家发射站（K1 正确），爆炸波及 E5 → [弹群] 离群剩 3 枚（实体清理链 ✓）。修复 = TickAttack 零偏置 + 多实体目标 → 瞄准最近实体（导弹打弹群不再瞄空当）。前沿余量警告首次触发（0.3km）。DLL 23:41。⚠️ 待复跑验证命中编队成员。详见 §0 十三轮 |
| 2026-09-08 | **弹群齐射场景验收关闭（第四轮，7/8）**：队首 10.91km 接管直接命中（余量修复生效）；错峰点火生效——零 `[爆炸损伤]`（无一枚被兄弟爆炸击杀）；7 枚全链命中。唯一损失 E5 = 正常接近至 1.4km 时恰逢 E4 弹头引爆，损伤不死（120<200HP）但吃满爆炸冲量被踢飞（closing +305→−5535）→ 再捕获无望 → [导弹中止] 干净回 Standby。**用户裁定：物理正确、丢 1-2 发可接受，场景关闭**。登记物理注记：AoE 冲量作用半径（1500m）远超致死带（1km），穿过爆炸区被踢飞是正确物理。详见 §0 十二轮 |
| 2026-09-08 | **弹群场景第三轮实测 + 错峰点火**：slab 修复验证通过（正常解离/错峰接管/每车日志）；6/8 命中。暴露**收敛聚堆误杀**——各弹收敛 closing 不同（310 vs 476，450 冲刺仅在轴对准时可达）→ E6 净追近 166m/s 把 2km 队列间隔坍缩，E5 差 0.1s 命中时被 E6 弹头 AoE 杀死（dist=52m）。修复 = 解离错峰点火（`holdFireUntilTick = now + depth/cruise`，时隙前只修横向；到达间隔由构造保证 ≥ 间距/巡航）+ 队首余量不足警告（<5km）+ 场景靶距 15.2→25.3km。纯测试 43/43，DLL 23:26。⚠️ 待第四轮复跑 8/8。详见 §0 十一轮 |
| 2026-09-08 | **弹群场景第二轮实测 → slab 几何两 bug 修复**：现象 = 无解离、8 枚同 tick 全转化（接触点 = 各弹自身位置，间距 2010m）。根因：①`RaySlabIntersection` 对**退化零长段**恒真（三轴 slab 全跳过），而 `ResolveEntityContacts` 在组刚创建首拍（`hasPrev=false`）传入零长段 → 每枚都被判接触；②方向分量为 0 时跳过该轴 slab 而非板内检查 → "平行且板外"误判命中（单测抓出）。修复：抽出纯核心 `SlabMath`（退化段=点-盒包含、平行轴=板内检查，CombatGrid 委托）+ 首拍按速度回推段起点（`UPDATE_INTERVAL_TICKS` 改 internal 复用）+ `SlabMathTests` 6 用例。纯测试 42/42，DLL 已重建。⚠️ 待复跑验证 8/8。详见《导弹系统设计-20260830.md》§0 十轮 |
| 2026-09-08 | **弹群场景游戏内实测（第一轮）+ 队首擦过根因修复**：7/8 命中，全链（间距依据行/参数快照/解离/8×撞击→弹头→Map 爆炸）实证通过。唯一失败 V1（队首）0.3km 擦过——根因 = **解离当步制导读解离前组原点**（队首偏移 +7km）→ 首条指令方向与弹体姿态恰好对准 → 主网满推力点火（该步 120kg / 158m·s⁻¹）→ 接近率冲到 310 而 400m 横偏未修 → 到靶剩 2s 修不完。修复：①解离后重取几何；②接近率上限计入转向时间（新增 `turnRateRad`）；③jitter 0.2→0.05·间距；④制导日志限频改每车独立。纯测试 36/36，DLL 已重建。⚠️ 待复跑验证 8/8。详见《导弹系统设计-20260830.md》§0 九轮 |
| 2026-09-08 | **导弹制导改良 + 纯核心抽取 + 弹群冲量根因修正（八轮）**：①抽纯核心 `MissileGuidanceCore`（Base/Game/，零 Verse 依赖，链入纯测试）——AVM.TickAttack 硬编码常量（0.8/300/450/600/+3km）全部 Def 化（`guidanceLateralGain`/`guidanceClosingThreshold`/`guidanceTimeMargin`/`guidanceLostMarginMeters`；`terminalSpeed` 重定义为“终末目标接近率”800→450）。②控制律：接近率补量加“横差修正时间”门控（横差大时需求压到近 0、dv 退化为纯横向 → 主推全力修横差）；追丢判定改**最近点 + 30km**基准（旧“接管点+3km”对近距滑脱不敏感）；远离 = 掉头再捕获；独立“横差门”经仿真证伪弃用。③**弹群冲量根因**：迷你弹群仿真复现——旧间距 1.43km < 爆炸半径 1.5km 时后弹被前弹爆炸侧推 **320m/s**、命中 5-6/8；间距 2km（`arraySideLimitMeters` 10→16km + 间距下限 `blastRadius×1.2`）→ **零冲量 8/8 命中**。④燃尽惯性弹寿命清理（K5 缺口）：`bodyLifetimeSeconds` 600s 到期 `[导弹清理]` 移除。⑤纯测试 34/34（`MissileGuidanceCoreTests` + `MissileFlightSim`/`VolleyFlightSim`：转向率限幅/主推 1.5° 门槛/RCS 兜底/**扫掠段-球命中判定**/可达包线 385 组合/再捕获/间距不变量）。⑥新增 DebugHub「长距制导(60km)」场景（`SpawnMissileLongRangeTest`，N=4 → 60km 靶）。⑦弹群场景靶距 11.2→15.2km（队首仍 ~1.2km）。编译 0 错误（Release）。⚠️ 待游戏内验收：长距 60km + 回归 1.5km/弹群/合并期被撞。详见《导弹系统设计-20260830.md》§0/§14 |
| 2026-09-08（会话交接快照） | **导弹系统里程碑**：弹群思路 A（深度队列 N≤8、每枚满罐、`GroupPropulsionAggregator` N 源聚合、终末解离）+ 碰撞双路径统一（唯一 `ResolveEntityContacts` 路径）已完成，游戏内日志已实证（解离带油/5 命中/无自伤）。**下个会话第一步 = 游戏内回归**：①撞击测试(1.5km) N=1（命中日志新格式 `[导弹撞击] …E.. 单枚转化` + `[弹群] 离群`）；②DebugHub「弹群齐射→解离(8)」；通过后决策制导改良（§14 已登记）。全部改动未提交 git（61 文件）。详见《导弹系统设计-20260830.md》§0/§14 |
| 2026-09-08 | **碰撞双路径统一 + 制导改良待办登记**：①统一（弹群机制日志验证通过后执行）——pair 导弹分支删单实体老路径（`if multi…else` 分叉），所有导弹对一律走 `ResolveEntityContacts` 逐实体解析（单实体 = 1×1 退化，几何等价）；`HandleMissileContact` 整组转化整段删除；拦截统一"引爆命中单枚 + 离群 + 组空销毁"；`ExplodeMissileAt`（自毁）恒逐枚爆心。编译 0 错误。⚠️ 待游戏内回归 N=1 + 弹群各一次。②登记制导改良待办（弹群场景暴露，见导弹设计文档 §0/§14）：接近率只加不减在速度已超目标时仍补 450 → 飞越后 LOS 翻转超 20°/s 带宽滑脱；追丢阈值对近距滑脱不敏感；前排爆炸扰动后排。改良项 = 接近率上限感知相对速度 / 近距再捕获 / 60km+ 靶验证解散距离-命中率关系 |
| 2026-09-08 | **弹群阵列改深度队列（方形同排互杀修正）+ 测试场景**：场景设计暴露方形阵列同深度横排成员被兄弟弹头 AoE 击杀（到达差离散物理步 33ms、分离 < 致死带 1km）→ 用户定稿**深度队列**（同深度 1 枚、间距 = min(2km, 10km/(N−1))，N=8 → 1.43km；横向仅 ±0.2 间距装饰微偏；**齐射上限 8**：滑杆/EffectiveVolleyCount/MaxVolleyCount 同步）。新增 DebugHub 场景：`SpawnVolleyImpactTest`（N=8 齐射→解离→8×错峰撞击，~11km 靶）与 `SpawnVolleyMergedHitTest`（弹群 Following 合并期被敌弹撞入 → 逐实体解析单枚转化离群、兄弟继续）；`ResolveEntityContacts` 补 [弹群接触] 空当放行门控日志；`CompMissileLauncher.SetVolleyCount` 公开（场景用）。编译 0 错误（Release）。⚠️ 待游戏内验证（预期日志链见场景方法注释） |
| 2026-09-08 | **弹群治本（思路 A）**：撤销前一轮"油箱实体制"实现（领弹死亡 = 单点故障，其移交/平分记账是给"group = 单飞行体"推进假设打的补丁，属治标）→ **每枚自带满罐 + 物理 N 源聚合**：新类 `GroupPropulsionAggregator : IPropulsionSource`（按 (工质,主/RCS) 合并组内全部实体引擎、扣料沿实体分摊、存量/容量 Σ）；`GetGroupPropulsionSource` 无 Map 多推进实体分支返回聚合器（单实体/火箭/Map 热路径零变化）；`GetMainThrustWorldAxis` 去 `as SpaceEntity` 强转改扫实体（弹群同构等价）；`ComputeGroupPhysics` 零改动。记账全删：`RemoveMissileEntity` 退化为 remove + 计数（燃料/质量/推力随实体自动收缩）、`DissociateVolley` 纯搬实体无分账。配套：发射器「齐射数量」滑杆（0 = 武器 Def）+ volleyCount 存档透传；`CreateMissileVolley` 与 `CreateMissileGroup` 共用 `BuildMissileEntity`（去重）；终末接管即解离（克隆记录同 volleyId/偏置）；pair/拦截逐实体化（`ResolveEntityContacts` 空当不误转化、单枚独立转化离群；拦截单枚爆心 = 实体世界位；群自毁逐枚爆心）；面板 ×N + 单枚燃料口径。编译 0 错误（Release）、纯测试 21/21。⚠️ 待游戏内验证 |
| 2026-09-07 | **弹头 Map 爆炸视觉修正（实测反馈："散一地"）**：`SpawnMapBlastFlecks` 弃用"半径内全图随机撒点"（观感 = 散一地），改为**同心冲击波结构**——中心主爆闪（规模随半径增长封顶）+ 中心火球密集闪光 + 4 层确定性角度抽样冲击波环（中心密外沿疏、环间错相位）+ 近场火光亮与烟柱；巨型 AoE 可见环收敛到 ~10 格核心。编译 0 错误（Release，DLL 已更新） |
| 2026-09-07 | **导弹发射器 UI 调整**：①发射档位独立于索敌模式——新增「自动发射 / 手动发射」Command_Toggle（默认自动；手动档 = 不自动打，靠「手动开火」单发/面板/全体命令）；原「模式」Gizmo 改名「索敌」消除歧义；autoFire 存档。②新增 Gizmo「命令全体导弹」：对本发射器释放的全部在册导弹统一撞击当前索敌目标 / 全体待命 / 全体自毁（AVM 新方法 `CommandAll`，倒序遍历防自毁注销改列表）；建筑 Def 描述同步。编译 0 错误（Release，DLL 已更新） |
| 2026-09-07 | **导弹收尾 A：精确瞄准（弹着偏置）+ 弹头 Map 范围爆炸**（编码轮，未游戏内验证）：①`VehicleRecord.aimOffsetLocal`（目标局部米 y=0，序列化）经 `FireFromMapBuilding`→`FireWeapon`→`FireMissileWeapon`→`RegisterMissileLeader` 可选参数透传；`IssueCommand` 带参写入/同目标保留/换目标清零、`SyncFollowerTargets` 换跟随目标清零；`TickAttack` 瞄准点 = 目标锚点 + `DVector3.Rotate(offset, e.Rotation)`（随目标姿态保持贴格）——发射器「设置精确打击点」Gizmo 镜像定位机精确炮击（跳图 + Targeter 选格 + 标记 Mote + 存档补全）。②`CombatManager.SpawnMapWarheadBlast`：弹头命中带 Map 目标 → 以命中映射格为心在目标地图上做范围爆炸（半径 blastRadius 米线性衰减、建筑/Pawn Bomb 伤害、纯 Fleck 视觉，GenExplosion NRE 规避）；**爆炸条件 = 命中处地板不是"太空"**（奥德赛 DLC TerrainDef `Space`：defName/HasTag 判定；中心太空整体跳过、半径内太空格跳过）。③控制面板目标列表标注「[有图]」。编译 0 错误（Release） |
| 2026-08-31 | **性能优化全部落地（§6 PERF-1~5 ✅）**：PERF-1 战斗日志门控（CombatManager 全量挂 `DebugDetailedCombat`/`DebugDetailedCollision`，补齐遗漏的 `[爆炸]` + `TargetingSystem` `[锁定]`）；PERF-2 `CombatGrid.WalkSegmentCells` 空格改坐标推导边界不中断；PERF-3 `CelestialBodyDef` 懒缓存轨道正交基（`EnsureOrbitFrame`/`GetOrbitFrame`）；PERF-4 `TargetingSystem.FindBestTarget` 改走 `GetGroupsInRange`（粗网格预过滤，O(n²)→O(n×k)）；PERF-5 `UpdateAllMovements` `PARALLEL_PHYSICS_MIN_GROUPS=32` 阈值内顺序循环。编译 0 错误、测试 92/92 通过 |
| 2026-08-31 | **全面性能审查 → 登记性能优化待办（§6 PERF-1~5）**：对轨道物理/空间索引/战斗/跳帮/制导全链性能审查，无正确性缺陷，登记 5 项热路径优化——①战斗日志门控（`CombatManager.cs` 37 处 `Log.Message` 大量无条件 + `TargetingSystem` `[锁定]`）；②`CombatGrid.WalkSegmentCells` 空格提前中断；③`CelestialBodyManager.GetOrbitFrame` 重复正交基；④`TargetingSystem.FindBestTarget` O(n²) 索敌；⑤`SpaceGameComp.UpdateAllMovements` Parallel.ForEach 小规模回退顺序 |
| 2026-08-10 | 创建：从 [Lite规划-20260808.md](./Lite规划-20260808.md) M3-M5 拆解为粒度任务清单；登记 F1-F3 共 9 项 + M3 验证 5 项 + M5 打包 4 项 + 遗留 2 项，共 20 项 |
| 2026-08-29 | **炮塔系统收尾（M4-F2/F3 完成）**：①整体式炮塔改造——上下分体→单炮塔、360° 无射界、出膛点=炮口射线∩本船 AABB 六面（与 F3 命中几何对称）；②**F2-1/2/3 全部标 ✅**、**F3-1 ✅**、**F3-2 基本版 ✅**（贯穿弹+落点伤害；范围爆炸后置 P3）；③修复射弹生成后立即误判命中 bug（`prevUpdatePhysicsTick` 初始化 + `segmentLength<0.5m` 防御，详见实现记录踩坑 #13）；④新增精确炮击（敌舰地图选格瞄准偏移 + 红色标记）；⑤全链详细调试日志（`DebugDetailedCombat` + 射弹探针）；⑥新增测试场景：全方位 6 方位对射 + 正下方精确炮击；⑦**M3-1/3/4 游戏内验证标 ✅**；⑧炮塔渲染修复（底座+炮头分离、AltIncVect 抬高）；⑨**性能优化**：定位机炮塔朝向 4 tick 限频（对齐 SOS2）+ `CheckShipCollisions` 组级碰撞缓存（位置/半径/当前盒/上一步盒每组每 tick 只算一次，消除每对最多 6 次 `GetWorldAabb` 旋转计算） |
| 2026-08-10（同日同步 DMS:NF 文档） | **重大更新**：①所有任务表追加「Phase」列（映射 [PFL闭环规划 §6](./PFL闭环规划-20260810.md) P1/P2/P3 各子项）；②F1 新增 F1-4「化合燃料 ↔ dV 定量换算」；③新增 §F4「DMS:NF 接入点接口冻结 + 最小实现」7 项（F4-1~7）；④M5 新增 M5-5「DMS:NF 接入点文档（9 类）交付」；⑤M3/M5 标题与说明补充 Phase 归属引用；**合计从 20 项扩至 29 项**。 |
| 2026-08-11 | 按 DMS:NF 最新口径同步：火箭型号改为「CV-1/CV-3 可用、CV-8/10/12 暂缓」（F4 区块说明、F4-6）；F1-4 的「每格=2 单位→dV 定额」改为「系数可配、消耗指标待 DMS:NF 定」；M5-5 接入文档名「化合燃料映射」改「燃料映射」 |
| 2026-08-19 | 把《航天器地面建设与发射流程设计v1.md》拆成 19 项目标，新增“发射台总装（Design v1 拆解）”章节（LA-1~LA-19） |
| 2026-08-20 | 修复火箭框架 Frame 缺少 `entityDefToBuild` 导致的循环 NRE；火箭框架移出建造菜单；LA-1 发射台可建造区域从中心 1 格改为发射台占地区域；按 DMS:NF 补齐 `PF_Buildings_Basic` 美术 Def 的 `drawSize`/`size`；删除重复的 `PF_Building_AssemblyPad_Basic`（保留 `PF_Building_Launchpad_Small`）；迁移 DMS:NF 离港 Skyfaller 轨迹并让 PFL 发射时生成离港动画 |
| 2026-08-20 | 小型总装发射台区分可回收/不可回收火箭，各自提供设计 Gizmo 链路；回收第一级增加“直接重建完成 (调试)”按钮 |
| 2026-08-20 | 小型发射台可建造区域限制为中心 3×3；同一发射台仅允许一座火箭/运输仓，修复多运输仓同台发射崩溃；`Steel100` 交叉引用初步未在 PFL/DMS:NF 源码中找到字面量（后查明为 `rebuildCost` XML 列表格式错误） |
| 2026-08-20 | 查明并修复 `Steel100`（`rebuildCost` 的 `ThingDefCountClass` 列表格式错误）与 `Drop pod left the map`（离港动画未初始化 transport 字段）；调试用的 0 造价已按原值恢复，`rebuildCost` 用正确格式恢复为 `100 Steel` |
| 2026-08-20 | 修复 `CompThruster` 启动期静态资源警告：添加 `[StaticConstructorOnStartup]`，`flameMaterial` 改为 `static readonly` 并在静态构造函数中创建；重新编译 `PioneerFrameworkLite.dll` |
| 2026-08-21 | 发射台总装-口袋地图装载：火箭完工/重建时生成并关联舱内地图；新增 `CompRocketPocketMap`（火箭 Gizmo 进入、调试装载/卸载殖民者）；发射台 Gizmo 可进入舱内，舱内新增“返回地面”；发射时复用已有地图，舱内人员/建筑随火箭入轨；发射失败保留已装修地图，虚拟推进重试不重复注册；LA-10 转为进行中，新增 LA-20 |
| 2026-08-21 | 统一发射方式管理开始实现：新增 `RocketLaunchManager`/`RocketLaunchMode`/`RocketLaunchContext`/`Dialog_RocketLaunch`；火箭与发射台统一“发射…”入口；Harmony 隐藏 PFL 火箭原版发射 Gizmo；`CompAssemblyPad` 持久化发射方式；原版条件/PFL 条件分开校验；新增火箭“强制发射 PFL (调试)”Gizmo；新增 LA-21 |
| 2026-08-23 | 航天器管理界面与航天器地图架构：新增 `SpacecraftMapParent`/`SpacecraftMapRegistry`/`SpacecraftBridge`/`DockingPort`；脐带塔 + 原版 `Dialog_EnterPortal`/`PortalContainerProxy` 登船与物品转移；普通运输仓无地图 PFL 入轨；取消设计限制；`isPocketMap` 引擎技术要求注释；TODO 状态同步 |
