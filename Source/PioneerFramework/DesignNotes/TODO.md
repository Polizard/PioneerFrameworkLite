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
| M5-2 | P1 | Toon Shader 资产管线确认 | **✅ 2026-10-02 全链完成**：定性 = RimWorld 不加载散装 `Shaders/`（必须 AssetBundle；`Shader.Find` 恒失败属实、非 PFL 缺陷）。落地三件——① 打包工具链 `tools/PFViewShaderBundle/Build-PFViewToonBundles.ps1`（独立于 outline 那套；`-PrepareOnly` 可离线验）② 运行时加载器 `PFView/PFViewToonShader.cs`（双通道取包，取不到回退 Standard）③ **三个平台包已生成并入库/部署**：`pfview_toon_{win,linux,mac}`（win `7425675C…` / linux `e87B1531…` / mac `100B5921…`）。⚠ 打包用的是中国版编辑器 `2022.3.35f1c1_dcf06934cc2b`（本机只有这个；工具链已放行同 patch）——**若游戏内加载失败，第一嫌疑就是它** | P3-4 | ✅ 完成 |
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
| 2026-09-14 | ⚠️→✅ **㉔ 探针抓到并修掉一个真竞争（本轮唯一的生产 bug，且是新引入的）**：第一版局部缓存写成 `Dictionary<int,(double tick, DVector3 v)>` —— 值类型元组 32 字节、字段逐个拷贝，**写入不原子**，而群组物理在 `Parallel.For` 里跑 ⇒ 读者会拿到"新 tick + 旧位置分量"的**撕裂值**。游戏内实测 3 次（月球，t=509.033/525.100/526.800，Δ=5.3/15.8/31.5 m），全部由 ㉔ 的影子比对报出（判定 PASS 是**在它们之前**打印的，而 `TotalMismatches` 是硬指标 —— 晚打印就会 FAIL）。**断定是撕裂的依据**：命中要求 tick 逐位相等，而该算式是 (bodyId,tick) 的纯函数 ⇒ "命中却值不同"只可能来自写入方式。**离线复现**：同负载压旧写法 = 656 万命中/1585 次撕裂，新写法 = 1.687 亿命中/0。**修法**：抽出纯类型 `Base/Orbital/BodySampleCache.cs` —— ①值体是不可变引用对象、槽位只做一次引用赋值（原子）②槽位是数组下标而非字典键（并发期零字典写入）。四处缓存（世界/局部 × 位置/速度）全换掉，位置与速度合成一份采样。同批修掉 `CelestialBodyDef.EnsureOrbitFrame` 的"先置标志后算值"竞争（PERF-3 遗留：并发首用会拿到 (0,0,0) 基向量）。新增纯测试 `BodySampleCacheTests`（3 例，直接压这个生产类型）。`tools/test-all.ps1` ALL GREEN 8/8（纯测试 98/98、集成 80/80、0 错误、警告 561 = 基线、已提交 DLL 随脚本重建）。**游戏内已确认（同日重跑，8/8 PASS）：㉔ 影子 182796 次比对、不一致 0**（修前同流程 3 次撕裂），命中/未命中 8.0:1 → **13.9:1**（值体合并成一份采样的结构签名）。顺带把 ㉖ 的"最慢一步"补上**第几步 + 真实 tick**（峰值 6.9/7.1 ms 与 10.8 ms 在两轮间不复现 ⇒ 需要定位而不是猜）。DLL → `40B2AD61…`。 |
| 2026-09-14 | ✅ **性能挂账第 24/25 条落地**（都是"能看见"的能力，不是新判据）：㉔**局部系天体位置/速度加 `(bodyId,tick)` 缓存** —— A0 之后群组引力场每步、每父链天体都查一次 `GetBodyLocalPosition/Velocity`，而这对 API 此前**没有缓存**，同一 tick 内同一颗月球被每个群组各重算一遍（21 组 × 2 天体 × 60 步/秒 ≈ 2500 次三角/秒）；顺手把世界系递归里内联的**同一份相位算式**并进 `...Core`（算式副本 4 → 1，与 A1 同一条单一来源原则）。新探针 ㉔ 判据：未命中 ≈ 子天体数×步数（缓存前是 ×组数），影子在命中时用无缓存算式复算逐位比对（容差 0 → 不一致只可能是缓存陈旧）。㉖**每物理步墙钟探针** —— 此前日志里**全是计数**，"性能没变差"只能靠代码结构推断、给不出前后对比；现在一步一计时（平均/最慢 µs/步 + 引力场段 `CollectSystemSources+FieldAccelJerk` 单列占比，只在物理步调用点计时以免被战斗插值重建污染口径）。读数为观测项，**不进 PASS/FAIL**。`tools/test-all.ps1` ALL GREEN 8/8（纯测试 95/95、集成 80/80、已提交 DLL 随脚本重建 → `DEE19494…`）；游戏内读数待跑（㉔ 命中/未命中 + ㉖ 墙钟值）。 |
| 2026-09-14 | ✅ **E9 删除孤儿 WeaponDef（用户裁定）**：`1.6/Defs/Combat/WeaponDefs.xml` 删去 `PF_Weapon_Cannon` 与 `PF_Weapon_Default`——前者是**唯一**声明 `affectedByGravity=true` 的武器却零引用（建筑 `PF_Building_Turret_Cannon` 实际挂的是 `PF_Weapon_ProjectileCannon`），留着会让 grep `affectedByGravity` 的人误以为有活的引力射弹在跑；后者零引用且其"部件未指定武器时的默认武器"描述已无对应代码。删前全库（.cs/.xml/.ps1/.py + 测试工程）确认除自身 defName 外零引用、且代码无指向它的兜底字符串。武器 Def **8 → 6**，`validate_config.py` 通过（78 defs）；`tools/test-all.ps1` ALL GREEN 8/8 |
| 2026-09-14 | ✅ **批次 E 文档与陈旧注释清理**：①**E1/E3 换了修法**——原条目是"把星座文档的 24 例改 22 例""把 AVM 注释的 33 用例改实际值"，但这两个数字**各已漂两轮**（星座 24→22→25、导弹 33→16→20），**记数必然漂**；改为**不再记数**（只描述覆盖内容），从根上消掉该类漂移。②**E5** `DebugShipSpawner` 注释称"旧建筑 `PF_ShipDeployer` 保留一个 Gizmo 作兼容入口"与代码/XML 均不符（该 Def 已无 Comp、无 Gizmo）→ 更正。③**E6** 验收器阶段表与 8 个 `Spawn*` 调用逐条核对一致。④**E7** `OnPhysicsStepCompleted` 注释"组件 tick 顺序 = AVM → SpaceGameComp → CombatManager"与类型全名字母序不符（C 在 S 前）→ 改为陈述事实并注明旧说法为何错。⑤**E8** `MissileFlightSim` 的 `target + DVector3.zero * dt` 恒零死代码清理。⑥**E11** 两处调试场景用 `DateTime.Now.Millisecond` 造初始相位（`DebugShipSpawner`、`BoardingTestScenarios`）→ 改由 physicsTick 推导，**场景几何可复现**。⑦**E12** `CombatManager` 导弹分支闭合括号缩进错位（20 空格应为 16）→ 修正。E2/E4 已随批次 A 解决。**E9 孤儿 WeaponDef（`PF_Weapon_Cannon`/`PF_Weapon_Default`）需用户裁定**（零引用，但项目 XML 卫生约定禁止在 Defs 里写"未使用"注释，故不能靠标注了事）。`tools/test-all.ps1` ALL GREEN 8/8（纯测试 95/95、集成 80/80） |
| 2026-09-14 | ✅ **批次 D 产物与工作流（4 项）**：①**D2（最高价值）** `PioneerFramework.csproj` 的 `OutputPath` **按配置分离**（Release → `1.6/Assemblies/`；Debug → 项目内 `bin/`，被 .gitignore 忽略）。旧写法两配置共用，**任何 Debug 构建（含 `dotnet test`）都会覆盖已提交的 Release DLL**，只能靠"脚本把重建放最后"+ 三处文档警告兜（本次实测一次普通 Debug 构建即改掉已提交 DLL 哈希）→ 现结构上不可能：Debug 构建前后已提交 DLL **逐字节相同**。三处陈旧警告（`test-all.ps1` WHY 块 / `CONTRIBUTING.md` / `性能优化待办`）已改写并给出游戏内 Debug 构建的显式 opt-in 命令。②**D3** CI 原先**内联重写**了 `verify-deterministic.ps1` 与 `check-committed-dll.ps1` 的逻辑（两份并行实现会漂移，CI 缺警告计数与 `-WarningDiff`）→ 改为 CI 直接调同一套脚本。③**D4** `test-all.ps1` 两处温和假绿：新增 **WARN 三态**（无法校验的步骤不再记 PASS，汇总单列 `N unchecked`，退出码不变）+ 警告计数取不到返回 **-1** 而非静默 0；并复查该脚本恢复 **ASCII-only**（其顶部 NOTE 明确要求——PS 5.1 按 ANSI 读无 BOM 文件，我一度写入中文文案）。④**D5 结论=不改并写清理由**：`Base/Debug/` 刻意不加 `#if DEBUG`——验收工具必须在**发布 DLL** 里可用（游戏内回归就是跑发布 DLL 的 dev-mode 菜单），且全是运行时门控；该守的边界是另一侧：**玩家可达的正式 UI 不放此目录**。⑤**D1** `Window_MissileControl` / `Window_Steering` 由 `Base/Debug/` 迁至 `Base/Buildings/`（与开它们的 Comp 同目录；同惯例 `Window_VolleyCountSlider` 就在 `CompMissileLauncher.cs` 内），文件头写明原因。**`tools/test-all.ps1` ALL GREEN 8/8、0 unchecked**（纯测试 95/95、集成 80/80、确定性一致、已提交 DLL == Release 构建哈希） |
| 2026-09-14 | ✅ **批次 C 测试补强（纯测试 89→95）+ 修掉 `minSurfaceDist` 哨兵 NOTE**：①**C1 负测试**（原缺——可达测试只断言「包线内必命中」，一个「永远命中」的错误实现也能过）：**改用燃料硬约束**（`fuelMassOverride` 新增于仿真辅助），因为审查建议的「`l > 0.8·(r/c)·lateralAccel` 即不可达」**不可实现**——实测 `r=1500 c=100 l=1600` 被该判据判为包线外却真实命中（minSD=−57）：目标静止时导弹减速修横差会把 tGo 拉长，那条判据只是可达测试里的**保守跳过过滤器**，不是可达性上界。负测试必须建在物理硬约束上。②**C2** 对抗性对照：同一几何把 `turnRateRad` 取极大（转向耗时≈0）后需求应显著更高，断言的是「转向项**被计入**」而非「需求被压低」（原只断上界 <200，0 或负值也能过）。③**C3** 退化几何三例（`pos==aimPoint` 早退不产 NaN、`targetRadius=0`、贴面后再远离历史保持），并**顺带修掉审查的 `MissileGuidanceCore` 哨兵 NOTE**：`minSurfaceDist <= 0` 当「未初始化」会把**有效**历史值（贴面 =0、侵入 <0）误判为未初始化 → 下一步被更大的值覆盖 → 追丢判据被抬高、提前/漏 Abort；改为显式 `minSurfaceInitialized` 标志（瞬态字段）。**这是行为变更**，被既有测试 `Abort_GeometryLost_WhenClosestApproachPassed` 抓住（它直接写 `minSurfaceDist = 500` 依赖旧哨兵的隐含语义）→ 已按其真实语义补 `minSurfaceInitialized = true` 播种。④**C4** 星座：`Remove→Add` 往返一致性、大 N=100 索引（双向计数与条目回收）、`AnchorWorldPosition` 的 `radius=0` 退化。编译 0 错误、警告 564、纯测试 **95/95** |
| 2026-09-14 | ✅ **批次 B 四项止血（不改架构）**：①**B1** `CheckSegmentGroupHit` 的拦截分支原本在 `foreach (group.GetEntityIds())` 内调 `RemoveMissileEntity`（改底层 HashSet），安全性只靠「紧接着 `return true`」——改成「记录命中 → `break` → 循环外摘除/引爆」，**零分配**（这是每弹每组的命中热路径，不能为快照分配 List）；`break` 位置即原 `return true` 位置，故语义等价、「先摘除后引爆」顺序与跳过的调试日志都保持不变。②**B2** `SpaceGroupVisualManager` 的 7 个 Harmony 补丁（含 2 个 transpiler）包 try/catch——补丁失败原本会让静态构造抛异常，真实后果是**其后补丁静默缺失 + 类型被毒化**（之后任何静态访问抛 `TypeInitializationException`，而 `LaunchService` 会调 `SetLaunchSiteTile` → 发射流程连带炸）；注：**并非**「整个 mod 加载失败」，框架 `StaticConstructorOnStartupUtility.CallAll` 对每个类型独立 try/catch 只 Log.Error。③**B3** 缺 Def 的静默兜底改报错：`CompMissileLauncher` 缺武器 Def 时不再回退 `100000f`（= WeaponDef 的 C# 默认值，恰好掩盖「能用但从不命中」）而是 `Log.Error` + 明确不参与交战；`ExplodeAtWorldPoint` 的 `1000/500/1e9` 兜底**保留**（爆炸必须发生）但改为先 `Log.Error` 报出 Def 名。④**B4** `Window_MissileControl` 的 `cm` 由表达式体属性（**每次访问都重新 `GetComponent` 线性扫组件表**，每帧每行读 1–2 次）改为每帧开头刷新一次的字段——该窗口**玩家可达**（可建造建筑 Gizmo，非 dev-only），故不是「仅开发者的开销」。编译 0 错误、警告 564（比基线少 1）、纯测试 89/89 |
| 2026-09-14 | ⭐ **引力场去掉"环绕天体"参量（A0）+ 制导参数单一来源（A1）**：`NBodyIntegrator` 原为"中心天体 + 其直接子天体"口径（`MultiBodyAccelJerk(primaryBody, whitelist)`），改为**统一在系统系（根行星/恒星为中心、根钉住）对**该行星系全部后代天体**做全量求和，签名不含 primaryBody**（`FieldAccelJerk(pos, vel, sources, out acc, out jerk)`）。关键推导：系统系全量求和 ≡ "子天体局部系 + 显式潮汐项"（月球开普勒运动是给定的，代入 ρ=r−r_m 自动产生间接项），故月球周围那 0.56% 的地球潮汐缺失**不需要任何特殊代码**就补上了。**收缩**：删 `ComputeGroupPhysics` 的 `bodyType != Planet` 两段式分支、删 `ManeuverPlanner` 的手工父天体**全量**摄动（那是把均匀项重复计入，月球系 ~224km 量级错）。`NBodyIntegrator.cs` 现**零条件编译、零 RimWorld 依赖**（原 `#if !PURE_MATH` 因直接吃 `CelestialBodyDef` 而豁免），多体引力**首次可进纯测试**。新增 `CelestialBodyManager.GetSystemMembers/CollectSystemSources`（沿父链累加位置，嵌套卫星亦正确）。A1：`MissileGuidanceParams.CreateDefault()` 原是 `MissileDef` 默认值的**手抄平行副本**，已漂两处（`deadband` 1.0 vs 游戏 0.5、`lateralAccel` 硬编码 83.0 vs 由推力/湿质量推得 80.0826）→ 数值只在 `MissileGuidanceDefaults` 定义一次，`MissileDef` 只覆写可调项且 `lateralAccel` 两侧共用同一算式。纯测试 67→88（含"系统系传播 ≡ 局部系+潮汐"多步等价、"差分=精确闭式潮汐"、"根域影子恒等"） |
| 2026-09-14 | 🔴→✅ **弹头弹道改"2 物理秒一段三阶泰勒 + 段内插值"（A2），并修掉随之暴露的真 bug**：原 `CombatGrid.ComputeProjectileGravity` 拿**行星系相对坐标**去减 `GetBodyWorldPosition` 的**绝对坐标**（差一个行星公转偏移，地球 1.2e8 m）→ 太阳项被算成 800 m/s²（应 1.4）、地球项 0.0139（应 8），净效果 = 被一个 GM=2e16（真实地球 100 倍）的中心引力拽着；因弹头在命中点出生、通常一步内即结算而未暴露。改法：弹头走**与飞船同一套引力场**，**每 2 物理秒**建一段三阶泰勒（`TaylorSegment`，`PhysicsStateCache` 的插值多项式与 `ThirdOrderTaylorStep` 合并为唯一算式 `TaylorInterpolate`）并在段内插值飞行（引力求值频率降到 1/120）；`systemBodyId`（= 参考帧 = 系统标识）直接用于取场。**真 bug（由新增的定量探针 ㉔ 抓到）**：段重建时 `baseTick` 写了 `physicsTickNow`，而段起点状态是**上一步**的 → 段内求值 t=0、**每段丢一步运动**（Combat 档 0.83%、Normal 档 **33%**；`|pos|`/`|vel|`/`|a|` 全都"看着正常"，只有"实际偏移 vs ½\|a\|t²"才抓得到）。修法 = 重建时传 `proj.prevUpdatePhysicsTick`（状态所在时刻）。回归守卫入纯测试：弯曲比钉 0.99–1.01、多段后与精确圆轨差 <1 m |
| 2026-09-14 | ✅ **id 分配改持久单调计数器（A5a）+ 实体注册表泄漏修复（A5b）**：①分配原为"扫 max+1"这种**派生状态**——销毁当前最大 id 的组后新组会**复用**该 id（实测一轮验收 1–21 次），会让弹头的 `sourceGroupId` 友军免疫、AVM 的 `targetGroupId` 追踪认错组，也让按 id 记忆的探针误判"同 id 不同组"。改为 `SpaceGameComp.nextGroupId/nextEntityId` + `ConstellationGameComp.nextConstellationId`（均 `Scribe_Values`；≤0 时一次性自愈播种为 max+1），并**删掉三份 `GetNextGroupId/GetNextEntityId` 副本**（`DebugShipSpawner`/`LaunchService`/`MainTabWindow_PFView`，54 行），全项目一条 id 规则。②导弹离群只 `group.RemoveEntity`、**从不注销**，而 `DestroyGroup` 只遍历组内**剩余**成员 → 被移出的实体永久滞留 `entities`（随档序列化，`GetEntityById(陈旧 id)` 取回僵尸）。修在产生端（`AVM.RemoveMissileEntity` 补 `UnregisterEntity`；解离路径是"搬组"非死亡，未动）。**新增探针 ㉕**：每 600 tick 断言 `entities` == 各存活组 entityIds 并集，多出即泄漏 → 硬指标（**独立限频**：验收期间日志汇总静默，不能挂汇总钩子）。游戏内实测：**㉓ id 复用 0 次**（修前 1–21）、**㉕ 泄漏 0**（本轮 6 次导弹离群为触发源，修前即 6 个僵尸实体） |
| 2026-09-14 | ✅ **口径单一来源三项（A3/A4/A6）+ 游戏内验收基建**：A3 齐射滑杆的间距显示原写死 `min(2000, 10000/(n−1))`——它**正是被废弃的旧几何（10km 队长上限）**，即"间距压到爆炸半径内、后弹被兄弟炸飞"那个 bug 的参数，UI 显示的是**修复前的数字**；改调 `MissileVolleyMath.ComputeSpacing` 且参数由武器 Def 解析的 `MissileDef` 提供（n=8 现显示 2000m，旧为 1429m）。A4 齐射上限收敛为 `CombatManager.MaxVolleyCount`（`public const`，4 处引用 + 陈旧"1..32"文案更正）。A6 坐标系约定收敛为**唯一出处**（`CelestialBodyManager.GetBodyWorldPosition` 文档块：三系各自来源/用途 + "跨系必须显式换算" + 两次事故），三处散落警告改交叉引用。**验收基建**：新增「引力自检电池」33 项即时断言（数据路径/场数学/轨道保持/A1 参数，纯 API 只读）、一键验收扩到 **8 阶段**（新增 6/8 月球系引力场、7/8 弹头建段、8/8 漏靶弹头 + 新场景 `SpawnMissedWarheadTest` 让弹头活满 120s）、**自检进度窗口** `Window_PFLSelfTest`（总进度条/逐阶段状态/引力读数/失败清单）与**测试项勾选**（可只跑引力相关），探针新增 ㉑–㉕（根域影子/子天体域差分/弹头引力/自由飞行弯曲/id 复用/注册表一致性）。**游戏内总判定 PASS：硬指标全 0、覆盖度 8/8（含长期卡住的 4/8 无 Map 碰撞，阶段间清场后首次通过）、值判据全过**；读数：月球域差分 0.2890%（理论切向 0.285%）、弹头 \|a\|=μ/r² 的 1.00×、漏靶弯曲 1.00×（20.0m/20.0m） |
| 2026-09-10 | 🔴→✅ **锚点坐标系错误（自检漏网 → 独立口径校验补上）**：游戏内日志 `[星座] 创建锚点 G1 … pos=(116920,3284,-3453)km` 暴露——地球锚点被放到距原点 11.7 万 km 处。根因 = `AnchorWorldPosition` 用了 `CelestialBodyManager.GetBodyWorldPosition`（**递归到恒星**的绝对位置，Earth `orbitRadius`=120,000 km），而 PFL 的**群组坐标系是根行星中心**（碰撞 `GetBodyWorldOffset`、圆轨解析 `GetCentralBodyOffset`、N 体积分器 `GetBodyLocalPosition` 三处一致口径）。**修法** = 改用 `group.GetCentralBodyOffset`（行星 → 零 / 卫星 → 绕行星偏移）；地球锚点位置由 `120,000km 处` 纠正为 `= R_body`，且不再随地球公转时变。**游戏内复验通过**：`创建锚点 G1 … pos=(-1699,-281,-4694)km |pos|=5000km`（= 地球半径），自检增至 **32 通过 / 0 失败**（新增的两条独立口径校验：`距中心天体 == R_body`、`根行星锚点 |pos| == R_body`）。**教训**：初版自检的"锚点落在天体表面"检查与实现同错（两边都调 `GetBodyWorldPosition`）→ 帧错误被判 PASS；现改为用 `GetCentralBodyOffset` **独立反推**，并加"根行星锚点 `|pos| == R_body`"这条与天体绝对位置无关的硬校验（设计要求见设计稿 §0.2 I3/I6）。~~顺带：`CombatGrid.ComputeGravityAccel` 也混用了两套口径，但该方法当前无任何调用点（死代码），暂不构成影响，仅登记~~ **【2026-09-14 更正】**：此处记录有误——真实方法名是 `CombatGrid.ComputeProjectileGravity`，且 `CombatManager.cs:358` 就是**活调用点**（弹头引力求值）；该参考系混用已于 A2 一并根治并删除该方法 |
| 2026-09-10 | ✅ **粗网格空格回收修复（既有缺陷，非星座专属）**：`SpatialGrid3D.DrainEmptyCells()` 长期**没有任何调用点**（`CombatManager` 每 tick 调的是另一个同名 `CombatGrid.DrainEmptyCells`）→ TTL 600 tick 的回收机制是死代码，空格与其 `emptyQueue` 条目永不回收。**修法** = `SpaceGameComp.GameComponentTick` 里紧邻现有 `pg.ApplyChanges()` 循环补 `pg.DrainEmptyCells()`。**游戏内实证**：自检造/销一枚圆轨探针后 `网格数=1 粗网格格数=1 实体=0 空队=1`（tick 480~960），到 **tick 1080（恰为 TTL 600 到期）回落为 `粗网格格数=0 空队=0`** 并稳定至 tick 2760 无反复。影响面核查：粗网格唯一读取方是 `GetGroupsInRange`（索敌预筛）+ 调试统计（定向能粗筛走 `CombatGrid` 另一套索引）；`SpatialGrid3D.Update` 唯一调用方 `SyncGroupToCoarseGrid` 与位置缓存成对维护，不会把已回收格当旧格 |
| 2026-09-10 | **星座组网系统首版落地（设计稿 §11 清单清零）+ 游戏内自检 31/31 通过**：按《星座组网系统设计-20260828.md》实现——①新组件 `ConstellationGameComp`（零 Tick / 纯事件驱动 / 纯 API，CRUD + 成员准入 + 多归属反查 + 读档惰性重建）；②`SpaceGameComp.DestroyGroup` 第 2.7 步新增 `event Action<int> GroupDestroyed`（**唯一侵入点**，早于实体注销 fire 故订阅者可读 group 身份字段）；③`SpaceEntityGroup` 新增 `IsSurfaceAnchor` / `anchorWorldObjectId`；④地表据点 = **合成锚点 group**（`IsActive=false`/`IsMovable=false`/`IsCollidable=false`，`GetGridForGroup` 短路使其不进任何空间索引，绘制循环显式过滤——I1 决策见设计 §0.2）；⑤抽纯核心 `ConstellationCore` + `ConstellationIndex` 链入纯测试（准入真值表 / 结果码优先级 / 锚点位置 / 索引增删对称，**纯测试 43→67 全绿**）；⑥新增游戏内自检 `ConstellationSelfTest`（调试页签「90 场景 · 星座」，**自造探针 group 不依赖场景**、跑完自清理）+「详细星座日志」复选框；⑦`[Grid]` 注册日志补"粗网格=已入/锚点不入/无参考系"实况后缀（原日志对锚点报 registered 与「空间索引隔离」结论矛盾）。游戏内自检 **31 通过 / 0 失败**（含锚点落点=天体表面 5000km、锚点不入粗网格、双收尸链）。编译 0 错误、警告 562 无新增。**遗留（与星座无关，见下方 PERF 区）：粗网格空格无回收** |
| 2026-09-10 | 🔧 **粗网格空格回收修复（既有缺陷，非星座专属）**：`SpatialGrid3D.DrainEmptyCells()` 长期**没有任何调用点**（`CombatManager` 每 tick 调的是另一个同名 `CombatGrid.DrainEmptyCells`）→ TTL 600 tick 的回收机制是死代码，空格与其 `emptyQueue` 条目永不回收，历史上被访问过的每个 1000km 格常驻内存。**游戏内实证**（星座自检顺带暴露）：空闲场景 `[网格状态]` 从 `网格数=0` 变为恒 `网格数=1 粗网格格数=1 实体=0 空队=1`（tick 240/360/480 无回落）。**修法** = `SpaceGameComp.GameComponentTick` 里紧邻现有 `pg.ApplyChanges()` 循环补 `pg.DrainEmptyCells()`（每 tick 摊销 `emptyQueue.Count/20`，空队零迭代）。**影响面核查**：粗网格唯一读取方是 `GetGroupsInRange`（索敌预筛）与 `[网格状态]` 调试统计——定向能粗筛走的是 `CombatGrid` 另一套索引；`SpatialGrid3D.Update` 唯一调用方 `SyncGroupToCoarseGrid` 与 `groupLastCoarsePos` 缓存同步维护（Add/Remove 成对），故不会把已回收格当旧格传入 `Update`；被回收格必然"当前无实体"，查询语义不变。**验收预期**：造/销一枚圆轨探针后约 600 tick（TTL），`[网格状态]` 的 `粗网格格数` 与 `空队` 应回到 0（`网格数` 仍是 1——那是按参考系常驻的网格实例，数量受天体数约束，非泄漏） |
| 2026-09-10 | **警告分类梳理：565 条里挑出 1 个真 bug + 4 处边角，警告 565→560**。565 条中 563 条是可空引用警告（项目开 `<Nullable>enable` 而代码按 RimWorld 老写法：字段由 XML/Scribe 填、`GetComponent` 可空），属噪声；**只挑 CS8602（真·可能解引用，11 条）逐条看**，结果：①**真 bug** `AutonomousVehicleManager` 读档分支 `if (vehicles == null) vehicles.Clear();`——对 null 调方法，旧存档无 `vehicles` 节点时读档必 NRE，改为 `= new List<VehicleRecord>()`；②`SpaceGameComp.cs:310` 与 `Window_Steering.cs:45` 两处 `Current.Game.` 漏 `?.`（全库约定是 `Current.Game?.`）；③`Window_SpaceBattleView3D.cs:802` 创建太阳标记后不复查直接解引用（缺资源即 NRE）；④删死字段 `CompMissileLauncher.lastSyncedTargetGroupId`（只赋值从不读）。余 7 处为守卫存在但流分析看不见的误报（`rec = avm?.GetRecord(...)` 判空蕴含、`hit ⇒ hitGroup 已赋值`、`GenStep.Generate(Map)` 引擎保证非空、`activeCount` 间接判空等），保持原样 |
| 2026-09-10 | **测试入口规范化：挂建筑的 ThingComp → 游戏自带 Debug 菜单**：原 `CompDebugShipSpawner : ThingComp`（需先建 PF 调试台建筑才能开面板）整体迁移——①场景库转**静态类** `DebugShipSpawner`（`CompTick`→`Tick()` 由 `SpaceGameComp.GameComponentTick` 驱动，承接场景自动下令 + 碰撞距离探针；18 处 `parent.Faction` → `Faction.OfPlayer`；各场景本就自建口袋地图，**不依赖建筑**）；②新增 **`DebugTabMenu_PFL`（「PFL 测试」页签）**+ `DebugTabMenuDef`（Def `PF_DebugTab`，`menuClass` 扩展点，**无需 Harmony 补丁**）——含一键验收/中止/探针开关/全部单场景入口/调试日志复选框（写法照官方 `DebugTabMenu_Settings`：`Action` 节点 + `settingsField` 渲染成勾选框、切换动作自备故可联动多字段）；③删除 `Window_DebugHub`（内容被页签 1:1 取代）；④`PF_ShipDeployer` 建筑 Def 保留但**去掉 Comp**（存档兼容，已弃用）。命令行 9 步全绿、警告 570→565（改名免疫比对：种类零新增） |
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
