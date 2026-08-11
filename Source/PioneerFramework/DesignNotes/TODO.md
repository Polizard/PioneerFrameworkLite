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
| M3-1 | P0 | 轨道物理链路游戏内验证 | 进游戏生成地月系，观察 N 体积分/圆轨保持/机动规划/Lambert/交会 | P1 验收 | ⬜ 待办 |
| M3-2 | P0 | 月球表面生成验证 | 进游戏触发月球地表生成，检查地形/生物群系 | P1 验收（着陆） | ⬜ 待办 |
| M3-3 | P0 | 单 part 船（空间站）形态验证 | CompDebugShipSpawner 部署空间站，进出 Pocket Map | P1 验收 | ⬜ 待办 |
| M3-4 | P0 | 空间站对射验证 | Window_CombatTest 触发对射，检查 [命中] 日志 + 单 part slab 命中 | P1 验收（F2/F3） | ⬜ 待办 |
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
| F1-3 | P1 | 导弹/无人机 dV | `WeaponDef` missile*/drone* 字段保留，导弹为无 Map 纯物理 part（`linkedMapId=-1`），需确认导弹携带 dV 并消耗 | P2 | ⬜ 待办 |
| **F1-4** | **P1** | **燃料 ↔ dV 定量换算** | 实现 `ChemfuelToDeltaVMapper`：RW `Thing.Chemfuel` stackCount → 定量 dV → 调用 `RefillDeltaV`；换算系数做成可配置（初稿 1 格=2 单位仅供参考，消耗指标待 DMS:NF 定） | P2-6 | ⬜ 待办 |

### F2. 空间站开火（Map 炮塔 Building → 3D 射弹）

| # | 优先级 | 任务 | 说明 | Phase | 状态 |
|---|--------|------|------|-------|------|
| F2-1 | P0 | CombatManager 新增「Map 炮塔开火」方法 | 复用 `FireWeapon` 的 groupId + targetPosition + spawnPosition 语义，传入炮塔在 3D 空间对应的 spawn 位置与目标，直接生成 `ProjectileInstance` | P1-5 | ⬜ 待办 |
| F2-2 | P0 | Map 炮塔 Building 桥接 | 炮塔建筑（Building）调用上述方法；与已删 `CompNozzle` 同构（地图建筑↔太空实体桥接） | P1-5 | ⬜ 待办 |
| F2-3 | P1 | 炮塔 Building Def 定义 | 需在 `1.6/Defs/` 下定义炮塔建筑 ThingDef（或复用现有 `WeaponDefs.xml`） | P1-5 | ⬜ 待办 |

### F3. 空间站被打中 → Map 内对应 part 位置生成爆炸

| # | 优先级 | 任务 | 说明 | Phase | 状态 |
|---|--------|------|------|-------|------|
| F3-1 | P0 | 3D 命中点 → part 关联 Pocket Map 内坐标映射 | `CombatManager.CheckSegmentGroupHit` 命中后，将 3D 命中点（`hitEntryPoint`）映射到 `part.linkedMapId` 对应 Map 内坐标 | P1-3（复用 MapCoordMapper） | ⬜ 待办 |
| F3-2 | P0 | Map 内爆炸生成 | 在映射坐标处生成爆炸（视觉 + 伤害反馈），复用 RW 爆炸机制 | P1-6 | ⬜ 待办 |
| F3-3 | P1 | 无 Map part 命中反馈 | 导弹等无 Map part（`linkedMapId=-1`）命中后无 Map 反馈，需确认仅 3D 损伤 | P1 验收 | ⬜ 待办 |

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

## 5. 变更日志

| 日期 | 变更 |
|------|------|
| 2026-08-10 | 创建：从 [Lite规划-20260808.md](./Lite规划-20260808.md) M3-M5 拆解为粒度任务清单；登记 F1-F3 共 9 项 + M3 验证 5 项 + M5 打包 4 项 + 遗留 2 项，共 20 项 |
| 2026-08-10（同日同步 DMS:NF 文档） | **重大更新**：①所有任务表追加「Phase」列（映射 [PFL闭环规划 §6](./PFL闭环规划-20260810.md) P1/P2/P3 各子项）；②F1 新增 F1-4「化合燃料 ↔ dV 定量换算」；③新增 §F4「DMS:NF 接入点接口冻结 + 最小实现」7 项（F4-1~7）；④M5 新增 M5-5「DMS:NF 接入点文档（9 类）交付」；⑤M3/M5 标题与说明补充 Phase 归属引用；**合计从 20 项扩至 29 项**。 |
| 2026-08-11 | 按 DMS:NF 最新口径同步：火箭型号改为「CV-1/CV-3 可用、CV-8/10/12 暂缓」（F4 区块说明、F4-6）；F1-4 的「每格=2 单位→dV 定额」改为「系数可配、消耗指标待 DMS:NF 定」；M5-5 接入文档名「化合燃料映射」改「燃料映射」 |
