# PioneerFramework 战斗系统改造 ToDoList

> 日期：2026-07-31
> 规则：物理层全程 double；所有物理计算绑定 physicsTick；改完用日志验证。

---

## 本轮任务（物理时间基准统一 + 网格/精度/武器修复）—— ✅ 已完成

### T1. physicsTick 改名收尾 + 物理计算绑定

- [x] TimeScaleManager：`GetDisplayTick()` / `GetDisplayTickSafe()` / `GetDisplayTick(int)` → `GetPhysicsTick()` 系列
- [x] 残留字段改名：`ExecuteDisplayTick` → `ExecutePhysicsTick`、`lastManeuverCheckDisplayTick` → `lastManeuverCheckPhysicsTick`（SpaceGameComp）
- [x] 所有调用点同步更新（ManeuverPlanner / OrbitTransferPlanner / RendezvousPlanner / SpaceGroupVisualManager / CombatManager / CombatGrid / SpaceEntityGroup / 调试窗口）
- [x] CalendarAndSunPatch 注释修正：历法显示层（displayTicksAbs），与物理 tick 语义分离
- [x] 物理计算 TicksGame → physicsTick 审查替换（CombatManager / CombatGrid / 调试窗口）
- [x] 验证：编译通过；游戏日志显示 Combat 模式 5008 tick → 物理时间 83.47（=5008/60）✓ 缩放正确

### T2. 细网格 key 统一为 frameId（绕卫星船可被命中）

- [x] CombatGrid.AddShip / RemoveShip / UpdateShipPosition：`GetOrCreateSystem(ship.CelestialBodyId)` → `GetFrameId(ship)`（根行星/恒星）
- [ ] 验证：绕卫星的船被射弹命中的 `[命中]` 日志出现（需战斗测试）

### T3. double 化（物理层零 float）

- [x] CombatGrid.WorldToFineCell：双精度
- [x] 开火链路 Vector3 → DVector3（FireWeapon / TargetingSystem / Window_CombatTest）
- [x] GetCentralBodyOffset 增加 double 重载
- [ ] 验证：1e8m 级坐标下网格索引稳定

### T4. 武器引用化（修复索引脆弱）

- [x] `FireWeapon(int groupId, WeaponInstance weapon, ...)` + `GetGroupWeapon` 辅助
- [x] TargetingSystem 直接传引用；Window_CombatTest 同步
- [ ] 验证：武器部件被摧毁后列表收缩不再索引错位

---

## 碰撞 / 爆炸系统（本轮 T5-T7）—— ✅ 已完成

### T5. 多格注册（跨格普遍化，任何船都适用）

- [x] CombatGrid：船注册到 AABB 覆盖格集合（普通船 ≤8 格，母舰几十格），位置变更差集增删
- [x] `GetGroupWorldBounds`（groupPos + Σ entity AABB，DVector3）+ `CoverCells`
- [x] 碰撞完备性：`EnumerateShipPairs` 同格两两去重（重叠必共享覆盖格）
- [x] DebugPrintStats 多格注册去重统计；SyncAllShipsToGrid 船销毁时 RemoveShip 防泄漏

### T6. 速度分层碰撞模型

- [x] 检测：球包围快筛 + AABB 重叠（覆盖格共享即候选）
- [x] vRel < 50 m/s（`CollisionExplosionSpeedThreshold` 可配置）：**撞击等效**——动量交换（e=0）+ 位置分离（最短分离轴），无损伤
- [x] vRel ≥ 50 m/s：**真爆炸**——R = 8000 + 2·E^(1/3)，范围损伤 D = 0.005·√E·(1-dist/R)，径向冲量 Δv = 500·√E·falloff/mass
- [x] 任何速度/位置修正 → `ExitCircularOrbit()` 强制退轨
- [x] 爆炸视觉残留（ExplosionInstance 仿 LaserBeamInstance）+ PFView 绘制扩散闪光球
- [x] 质量：ShipPartDef.mass + 货舱容量（CargoHold），`SpaceEntity.TotalMass` 缓存（MarkPartDestroyed/SetParts 置脏）

### T7. 调试日志

- [x] `[ShipCollision]` 撞击等效 / 分离中重叠（含接近速度、质量、冲量）
- [x] `[爆炸]` 位置 / 半径 / 能量 / vRel / 损伤Part数 / 冲量船数
- [x] `[爆炸损伤]` Part HP≤0 被摧毁
- [x] 编译验证：0 错误

---

## 待办（后续轮）

> 注：T2/T3/T4 三项 🟡 待验证状态未变，需进游戏实测。2026-08-06 核查未进游戏，状态保持。

- [x] **网格回收改百分比斩杀**（已完成）：DrainEmptyCells 双路径——快路径每 2 tick 按队列 20% 斩杀 TTL 到期空格；慢路径每 60 tick 全量扫描删除所有到期空格并重建队列（防队首卡死/重复入队膨胀）。Fast 模式膨胀应被兜底
- [ ] 碰撞爆炸参数实测调参（阈值 50 m/s、R/D0/冲量系数）
- [ ] 绕卫星船命中验证测试
- [ ] 碰撞检测升级为真正 OBB 旋转求交（2026-08-06 源码核查：`CombatManager.CheckSegmentGroupHit` 实为 AABB slab + 局部坐标查询 `entity.QueryRayHit`，非设计思路文档描述的 OBB 旋转求交；部件有旋转时精度下降）
- [ ] 导弹/无人机武器代码层实现（2026-08-06 源码核查：`WeaponDefs.xml` 已定义 `PF_Weapon_MissileLauncher`/`PF_Weapon_DroneLauncher`/`PF_Weapon_DroneGun`，但 `CombatManager.FireWeapon` 仅 `Ray` 走专门分支 `FireRayWeapon`，Missile/Drone 无制导/燃油/投放逻辑，全部回退通用射弹生成路径）
- [ ] ManeuverQueueWindow 倒计时单位修正（2026-08-06 源码核查：`remainingTicks` 直接格式化为"T-{N} 秒"，TimeScaleManager 变速时显示与真实时间不符）

---

## CPU 优化候选（2026-08-06 追加）

> 背景：`性能优化-脑洞与经验合集.md` §3 量化——Fast 档下三角函数每 2 tick ~170-200µs，是最大单一热点。以下三项为本轮讨论确定的方向。

### O1. 射弹弹道多项式（采样 + 泰勒插值）—— 优先

现状射弹是显式欧拉 1 阶（`ProjectileInstance.UpdatePosition`），2s 步长近地误差 ~cm/步累积。

- [ ] 方案：发射/重采样时用 `MultiBodyAccelJerk` 固化 acc+jerk，8 物理秒区间内位置 = 泰勒 3 阶多项式 O(1) 求值（复用 `PhysicsStateCache` 模式）
- [ ] 精度：8s 区间近地误差 ~1.65mm（3 阶余项 (1/24)·ω⁴·r·Δt⁴，ω⁴·r = 9.65e-6），比现状欧拉高 2 个数量级
- [ ] `ProjectileInstance` 加多项式字段（x0/v0/a0/j0/t0/validUntil），`UpdatePosition` 改"采样 + 求值"
- [ ] 重采样时机：8s 到期 / 碰撞扰动 / 爆炸波及 / `systemBodyId` 变化 / `affectedByGravity` 变化
- [ ] `prevPosition` 语义保留（碰撞线段检测 = 上次 + 本次求值位置）
- [ ] 与星历材料表互补：弹幕战射弹引力计算从 ~16µs/2tick 归零
- [ ] 验证：弹道精度测试（对比旧欧拉，10km 射程落点偏差 < 1m）

### O2. 时间维度惰性宇宙（冻结无观察者的轨道）

没有观察者（舰船 / 射弹 / UI 聚焦区）的组不积分，只记"最后活跃 tick"；被查询时从冻结状态恢复。

- [ ] 冻结条件：组与所有活跃消费者距离超阈值
- [ ] 圆轨组恢复 = 解析回放（`GetCircularPositionAtTick`）；N 体积分组恢复 = 轨道参数快速前推
- [ ] 收益：几百组时省 ~80% 积分量；风险：冻结/恢复边界、存档交互

### O3. 能量漂移测量（验证现状积分器长期行为）

Taylor 3 阶非辛、能量不守恒，游戏内挂机多天轨道会缓慢漂移。先量化再决定是否动。

- [ ] 在 DeterminismTest 框架加能量守恒断言：10 万 tick 后轨道能量偏差率
- [ ] 近地 + 高轨两组样本；战役时长内可接受则不动
