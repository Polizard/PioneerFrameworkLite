# PioneerFramework — 设计与计划

## 一、项目定位

### 1.1 一句话描述

**PioneerFramework（先遣框架）** 是为 RimWorld 打造的**太空探索与战斗基础框架**，为 mod 开发者提供完整的太空模拟能力，支撑「建造飞船 → 轨道航行 → 太空战斗 → 地表探索」的完整游戏循环。

### 1.2 核心价值

```
对 mod 作者而言：
  ├── 无需从零实现轨道物理
  ├── 提供完整的飞船装配系统（支持舱室↔Map双向同步）
  ├── 内置战斗系统和碰撞检测优化（零GC、战斗子Grid）
  └── 统一的存档和扩展接口

对玩家而言：
  ├── 在 RimWorld 中体验完整的太空层
  ├── 建造自定义飞船，规划轨道进行星际航行
  ├── 在太空中与敌舰交战，体验弹丸命中部件的战斗
  └── 探索行星地表，建立前哨站
```

### 1.3 设计原则

| 原则 | 说明 |
|------|------|
| **场景驱动** | 所有设计源于新边疆mod的实际使用场景，而非技术炫技 |
| **性能优先** | 大规模战斗和轨道计算必须保持流畅（< 5% CPU 占比） |
| **零 GC** | 高频操作（战斗、物理更新）必须零垃圾回收 |
| **可扩展** | 提供清晰的扩展接口，支持 mod 自定义武器、部件、AI |
| **存档兼容** | 支持 RimWorld 原生存档系统，保证版本迭代兼容 |

### 1.4 新边疆mod适配说明

框架的设计完全基于新边疆mod的需求模式，支撑以下核心玩法：

| 新边疆玩法 | 框架支撑模块 |
|-----------|-------------|
| 邓氏鱼级巡洋舰、玄武号要塞建造 | ShipSystem（飞船装配） |
| 赝相位发射器、轻激光武器 | CombatSystem（武器系统） |
| 太空战斗与接舷战 | CombatSystem（战斗循环） |
| 轨道机动与星际航行 | OrbitSystem（轨道物理） |

---

## 二、框架支撑的游戏场景

### 2.1 场景一：建造飞船

**新边疆mod需求**：玩家在 3D 视口中自由组装引擎、散热板、反应堆、舱室等部件，每个舱室部件永久对应一张 RW 2D 地图。

**框架支撑**：

```
ShipDesignerSystem
    ├── 3D 装配视口（Window + RenderTexture）
    ├── 部件定义系统（ShipPartDef XML）
    ├── 空间允许检测（OBB 重叠检测）
    ├── 框架质量计算（转动惯量思想）
    └── 舱室 ↔ Map 双向同步
```

**关键设计决策**：

1. **永不损坏的框架** — 飞船有一个"骨架"框架，部件挂在上面。部件可以被打烂，但框架不毁，船不会四分五裂。无需处理级联解体。

2. **二阶段建造流程** — 玩家先在 3D 视口中自由摆放部件（无惩罚），满意后按下"生成框架"按钮，系统根据当前布局锁定框架质量。生成后框架锁死，部件损坏不影响。

3. **紧凑度 → 框架质量** — 框架质量由部件布局实时计算：
   - 部件紧密堆在一起 → 跨度²小 → 框架质量小 → 推重比高 ✓
   - 部件散开放 → 每个部件都离质心远 → 框架质量大 → 推重比低 ✗

**新边疆mod具体部件类型**：

| 部件类型 | 示例 | 功能 |
|----------|------|------|
| 船体框架 | 邓氏鱼级船体 | 基础结构，承载其他部件 |
| 引擎模块 | 核融合引擎 | 提供推力，消耗燃料 |
| 武器模块 | 赝相位发射器、轻激光 | 战斗攻击 |
| 舱室模块 | 船员舱、指挥舱 | 对应 RW 2D Map |

### 2.2 场景二：轨道航行

**新边疆mod需求**：飞船在太阳系中规划轨道、进行霍曼转移、进入圆轨保持、执行机动规划。

**框架支撑**：

```
OrbitSystem
    ├── N体引力积分（三阶泰勒）
    ├── 圆轨模式（自动轨道保持）
    ├── 机动规划队列（R/T/N 三向 dV）
    ├── 轨道转移计算（Lambert 求解器）
    └── dV 消耗系统（缓释燃烧）
```

**关键设计决策**：

1. **双精度向量** — 使用 DVector3 存储位置/速度，避免累积误差。

2. **并行物理计算** — 轨道动力学天生可并行，采用 Parallel.ForEach 计算所有群组的物理，再串行应用结果。

3. **轨道保持模式** — 选择性抵消非白名单天体的引力，消耗 dV，适用于圆轨保持和深空航行。

4. **安全检查** — 机动执行前自动检测安全性，阻止危险机动（如将撞击行星）。

### 2.3 场景三：太空战斗

**新边疆mod需求**：在 3D 太空战视图中，玩家操控飞船与敌舰交火，远程炮击摧毁对方部件，必要时展开接舷战进入 Map 内战斗。

**框架支撑**：

```
CombatSystem
    ├── 战斗管理器（CombatManager）
    ├── 武器系统（WeaponDef + WeaponInstance）
    ├── 弹丸飞行（欧拉积分 + 引力影响）
    ├── 碰撞检测（线段 OBB 求交 + 战斗子 Grid）
    ├── 部件损伤（hp 扣除 → 功能失效）
    └── 战斗 AI（目标选择 + 开火决策）
```

**关键设计决策**：

1. **弹丸命中 → 部件损伤 → 结构失效** — 完整的战斗闭环。

2. **射弹无敌我识别** — 命中任何 Group 的部件都会造成伤害，简化碰撞检测逻辑，增加战术深度（避免友军误伤）。

3. **战斗子 Grid** — 20 km 细格的无边界空间分区（持久 `CombatGrid`，见 `SpatialIndexing/CombatGrid.cs`），将 O(n×m) 降为 O(n×k)，支持大规模战斗。

4. **多层碰撞检测优化** — 包围球预判 → 方向过滤 → Group AABB → 部件 OBB，综合减少 90% 以上检测量。

5. **对象池** — 射弹使用固定大小对象池，零 GC 分配。
6. **无边界战斗** — 战斗是世界连续模拟的一部分，无战斗实例/生命周期；交战由敌对 Group 进入射程自然涌现。详见 [太空战斗系统-设计思路.md](./太空战斗系统-设计思路.md)「架构立场」章节。

**新边疆mod武器类型**：

| 武器类型 | 示例 | 特性 |
|----------|------|------|
| 射弹 | 通用激光 | 快速射击，精度高，单发伤害低 |
| 射线 | 重激光 | 穿透多层，伤害随距离衰减 |
| 导弹 | 制导导弹 | 追踪目标，终末阶段加速撞击 |
| 无人机 | 侦察/攻击无人机 | 自主飞行，可携带武器 |

### 2.4 场景四：地表探索

**新边疆mod需求**：飞船降落到行星表面，在地表移动、采集资源、建立前哨站，之后起飞返回轨道。

**框架支撑**：

```
SurfaceSystem
    ├── 地表实体管理（SurfaceEntity）
    ├── 地表移动算法（SurfaceMover）
    ├── 着陆/起飞逻辑
    └── 天地统一空间网格
```

**关键设计决策**：

1. **天地统一网格** — 轨道实体和地表实体共享同一套空间网格系统，支持跨域查询。

2. **行星网格分区** — 每个行星有独立的网格，使用中心天体参考系，避免浮点误差。

3. **碰撞检测** — 着陆时检测速度是否在安全范围内，超过阈值则坠毁。

---

## 三、核心模块设计

### 3.1 架构总览

```
┌─────────────────────────────────────────────────────────────────────┐
│                        PioneerFramework                             │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  ┌──────────────────┐                                              │
│  │    SpaceGameComp │ ← 核心管理器，每 Tick 驱动所有子系统            │
│  │   (GameComponent)│                                              │
│  └────────┬─────────┘                                              │
│           │                                                         │
│    ┌──────┼──────┐    ┌──────────────┐    ┌──────────────────────┐  │
│    ▼      ▼      ▼    ▼              ▼    ▼                      ▼  │
│ ┌────────────────────┐ ┌───────────┐ ┌───────────────────────────┐│
│ │    轨道物理系统     │ │ 空间网格   │ │      战斗系统             ││
│ │  OrbitSystem       │ │ SpatialGrid│ │  CombatSystem            ││
│ ├────────────────────┤ ├───────────┤ ├───────────────────────────┤│
│ │ • NBodyIntegrator  │ │ • 主网格   │ │ • CombatManager          ││
│ │ • OrbitMath        │ │ • 行星网格 │ │ • WeaponDef/Instance     ││
│ │ • ManeuverPlanner  │ │ • 战斗子Grid││ │ • ProjectilePool         ││
│ │ • BurnSystem       │ │            │ │ • CollisionDetector      ││
│ │ • CircularOrbit    │ │            │ │ • BattleAI               ││
│ └────────────────────┘ └───────────┘ └───────────────────────────┘│
│                                                                   │
│    ┌──────────────────┐    ┌──────────────┐    ┌─────────────────┐│
│    ▼                  ▼    ▼              ▼    ▼                 ▼│
│ ┌────────────────────┐ ┌───────────┐ ┌───────────────────────────┐│
│ │    飞船装配系统     │ │ 地表系统   │ │      扩展接口             ││
│ │  ShipSystem        │ │ SurfaceSys│ │  ExtensionInterfaces    ││
│ ├────────────────────┤ ├───────────┤ ├───────────────────────────┤│
│ │ • ShipPartDef      │ │ • Surface │ │ • IWeaponTypeHandler     ││
│ │ • Ship3D           │ │   Entity  │ │ • ICollisionDetector     ││
│ │ • ShipDesigner     │ │ • Mover   │ │ • IBattleAI              ││
│ │ • PartMeshGen      │ │ • Landing │ │ • IShipPartExtension     ││
│ │ • RoomMapSync      │ │            │ │                           ││
│ └────────────────────┘ └───────────┘ └───────────────────────────┘│
│                                                                   │
└───────────────────────────────────────────────────────────────────┘
```

### 3.2 核心数据结构

#### 3.2.1 轨道实体

```
SpaceEntityGroup（群组）
    ├── GroupId / FactionId           // 标识
    ├── CurrentOrbit: OrbitState      // 当前轨道状态（位置+速度+朝向）
    ├── IsCircularOrbit / circular*   // 圆轨模式
    ├── HasActiveBurn / burnDirection / remainingBurnDV   // 主动燃烧
    ├── HasOrbitKeeping / orbitKeepingWhitelist           // 轨道保持
    ├── plannedManeuvers              // 机动规划队列
    └── CustomData                    // 运行时扩展数据

SpaceEntity（实体）
    ├── EntityId / FactionId / GroupId
    ├── Position / Velocity / Rotation
    ├── RemainingDeltaV               // 剩余 dV
    ├── MapId / HasMap                // 关联地图
    └── CanInteract / IsSelectable / IsMovable
```

#### 3.2.2 飞船装配

```
Ship3D（飞船装配数据）
    └── parts: List<ShipPartInstance>

ShipPartInstance（部件实例）
    ├── def: ShipPartDef
    ├── position: Vector3             // 相对飞船原点的位置
    ├── rotation: Quaternion          // 任意朝向
    └── hp: float                     // 耐久值

ShipPartDef（部件定义）
    ├── shapeType: ShipPartShape      // Cube | SphericalCorner | Prism | Cylinder | Sphere
    ├── bounds: Vector3               // 包围盒尺寸
    ├── mass: float                   // 质量
    ├── previewColor: Color           // 预览颜色
    ├── isRoomModule: bool            // 是否舱室
    ├── roomMapSize / roomMapDef      // 舱室地图配置
    └── stats: [thrust, powerGen, heatDissipation, ...]
```

#### 3.2.3 战斗系统

```
WeaponDef（武器定义）
    ├── weaponType: Ray | Projectile | Missile | Drone
    ├── damagePerShot / armorPenetration
    ├── fireRate / maxRange / minRange
    ├── accuracyRadius / accuracyFalloff
    ├── cooldownTicks / overheatThreshold
    ├── mountSize / mountShape / turret
    └── projectileSpeed / missileFuel / ...（类型专属）

WeaponInstance（武器实例）
    ├── def: WeaponDef
    ├── mountPartIndex: int
    ├── cooldownRemaining / overheatCounter / isOverheated
    ├── turretRotation / targetRotation
    ├── isFiring / ammo
    └── ownerGroupId: int

ProjectileInstance（弹丸实例）
    ├── sourceGroupId / weaponDef
    ├── position / velocity
    ├── remainingLife / damage / armorPenetration
    ├── affectedByGravity / currentGravitySources
    ├── flybyDistance                 // 最近飞越距离（太空垃圾判定）
    └── cellIndex                     // 战斗子 Grid 缓存

ShipCombatState（飞船战斗状态）
    ├── weapons: List<WeaponInstance>
    ├── shields: List<ShieldInstance>（预留）
    ├── combatMode: Idle / Defensive / Aggressive / Evasive
    ├── currentTargetGroupId / targetAcquiredTick
    ├── isWeaponReady / threatLevel
    └── ownerGroup: SpaceEntityGroup
```

### 3.3 核心流程

#### 3.3.1 轨道物理更新流程

```
每 2 Tick（SpaceGameComp.GameComponentTick）:
    ├── 阶段 1：并行计算所有群组的物理（只读共享状态）
    │       Parallel.ForEach(groups.Values, group => {
    │           ComputeGroupPhysics(group) → GroupPhysicsResult
    │       })
    │
    ├── 阶段 2：串行应用结果（修改共享状态）
    │       foreach (result in physicsResults) {
    │           应用主动燃烧结果 → 更新轨道 → 应用轨道保持 dV 扣除
    │       }
    │
    ├── 阶段 3：行星碰撞检测（串行）
    │       foreach (groupId in groupsToCheck) {
    │           CheckPlanetCollision(group)
    │       }
    │
    └── 阶段 4：执行到期机动（串行）
            foreach (group in groups.Values) {
                if (nextManeuver.IsDue) {
                    安全检查 → StartBurn → PopExecutedManeuver
                }
            }
```

#### 3.3.2 战斗更新流程

```
每 Tick（CombatManager.Update）:
    ├── 阶段 1：AI 决策
    │       foreach (group in combatGroups) {
    │           if (AI 需要开火) {
    │               SelectTarget(group) → FireWeapon(group, target)
    │           }
    │       }
    │
    ├── 阶段 2：武器冷却计时
    │       foreach (weapon in allWeapons) {
    │           if (weapon.cooldownRemaining > 0) weapon.cooldownRemaining--
    │           if (weapon.isOverheated) 过热倒计时...
    │       }
    │
    ├── 阶段 3：弹丸飞行与碰撞检测
    │       foreach (projectile in activeProjectiles) {
    │           // 欧拉积分更新位置
    │           if (affectedByGravity) velocity += acceleration × dt
    │           prevPos = position; position += velocity × dt
    │
    │           // 碰撞检测（战斗子 Grid + 多层过滤）
    │           foreach (candidate in battleGrid.GetNearby(projectile)) {
    │               if (SegmentHitsOBB(prevPos, position, candidate)) {
    │                   ApplyDamage(part) → Destroy(projectile) → Break
    │               }
    │           }
    │       }
    │
    └── 阶段 4：视图同步
            Window_SpaceBattleView3D.UpdateScene()
```

#### 3.3.3 碰撞检测流程（多层过滤）

```
碰撞检测（对每枚射弹）:
    ├── 第一层：战斗子 Grid 粗筛（整数比较，约 5-20 个候选 Group）
    │
    ├── 第二层：距离过滤（O(1)，约 50-70% 跳过）
    │       if (distance > maxRange) → 跳过
    │
    ├── 第三层：方向过滤（约 20-30% 跳过）
    │       if (dot < 0) → 跳过（射弹背向目标）
    │
    ├── 第四层：Group 级 AABB 检测（约 20-30% 跳过）
    │       if (!SegmentHitsAABB) → 跳过
    │
    └── 第五层：部件级 OBB 检测（精确命中判定）
            foreach (part in ship.partsSortedByRadius) {
                if (SegmentHitsOBB) → ApplyDamage → 返回
            }
```

---

## 四、开发计划

### 4.1 里程碑规划

```
Phase 1: 基础框架搭建（已完成）
  ├── 轨道物理系统（N体积分、圆轨模式、机动规划）
  ├── 空间网格系统（天地统一、行星网格）
  ├── 飞船装配系统（部件定义、二进制序列化）
  └── 3D 视图系统（战斗视图、飞船设计器）

Phase 2: 空间站与地图系统（开发中）
  ├── Pocket Map 生成与管理
  ├── 空间站创建与对接
  ├── 轨道保持 dV 消耗优化
  ├── 天体相对坐标同步
  └── 存档兼容性修复

Phase 3: 战斗系统（规划中）
  ├── 武器定义与实例化（支持四种武器类型）
  ├── 弹丸飞行与碰撞检测（欧拉积分 + OBB求交）
  ├── 战斗子 Grid（10 km 粒度）
  ├── 部件损伤与特效（命中→摧毁→特效）
  ├── 战斗 AI 决策器（目标选择、开火决策）
  └── 性能优化（对象池、时间分片、多层过滤）

Phase 4: 地表探索系统（规划中）
  ├── 行星着陆与起飞
  ├── 地表移动与资源采集
  └── 前哨站建设

Phase 5: 经济与外交系统（规划中）
  ├── 太空贸易路线
  ├── 势力关系与冲突
  └── 任务与事件系统
```

### 4.2 Phase 2 详细计划

| 任务 | 描述 | 优先级 | 预估工时 | 状态 |
|------|------|--------|----------|------|
| 2.1 | Pocket Map 生成优化 | 高 | 8h | 进行中 |
| 2.2 | 空间站对接系统 | 高 | 12h | 待开始 |
| 2.3 | 轨道保持 dV 消耗优化 | 中 | 8h | 待开始 |
| 2.4 | 天体相对坐标同步 | 高 | 6h | 待开始 |
| 2.5 | 3D 视图性能优化 | 中 | 10h | 待开始 |
| 2.6 | 存档兼容性修复 | 高 | 6h | 待开始 |

### 4.3 Phase 3 详细计划

| 任务 | 描述 | 优先级 | 预估工时 | 状态 |
|------|------|--------|----------|------|
| 3.1 | WeaponDef XML 定义（四种武器类型） | 高 | 8h | 待开始 |
| 3.2 | 武器实例化与冷却系统 | 高 | 10h | 待开始 |
| 3.3 | 弹丸飞行与欧拉积分（含引力影响） | 高 | 12h | 待开始 |
| 3.4 | 战斗子 Grid（10 km 粒度空间分区） | 高 | 16h | 待开始 |
| 3.5 | 碰撞检测（线段 OBB 求交 + 多层过滤） | 高 | 12h | 待开始 |
| 3.6 | 部件损伤系统（命中→摧毁→Map同步） | 高 | 8h | 待开始 |
| 3.7 | 战斗 AI 目标选择（评分排序） | 中 | 10h | 待开始 |
| 3.8 | 命中特效与视觉反馈 | 中 | 8h | 待开始 |
| 3.9 | 对象池与零 GC（射弹池 4096 枚） | 高 | 6h | 待开始 |

---

## 五、扩展接口

### 5.1 自定义武器类型

```csharp
public interface IWeaponTypeHandler
{
    void Fire(WeaponInstance weapon, SpaceEntityGroup attacker, SpaceEntityGroup target);
    void Update(WeaponInstance weapon);
    void DrawVisual(WeaponInstance weapon);
}
```

### 5.2 自定义碰撞检测

```csharp
public interface ICollisionDetector
{
    bool Detect(ProjectileInstance projectile, ShipPartInstance part);
    float GetHitDistance(ProjectileInstance projectile, ShipPartInstance part);
}
```

### 5.3 自定义战斗 AI

```csharp
public interface IBattleAI
{
    int SelectTarget(SpaceEntityGroup attacker, List<int> enemies);
    bool ShouldFire(SpaceEntityGroup attacker, int targetId);
    CombatMode GetCombatMode(SpaceEntityGroup group);
}
```

### 5.4 自定义部件扩展

```csharp
public interface IShipPartExtension
{
    void OnPartPlaced(ShipPartInstance part);
    void OnPartDamaged(ShipPartInstance part, float damage);
    void OnPartDestroyed(ShipPartInstance part);
    void Tick(ShipPartInstance part);
}
```

---

## 六、性能规范

### 6.1 核心性能目标

| 场景 | CPU 占比 | 目标 |
|------|----------|------|
| 小规模战斗（2 艘船，10 枚射弹） | < 0.1% | ✓ 可实现 |
| 中等规模（4 艘船，50 枚射弹） | < 1.2% | ✓ 可实现 |
| 大规模（4 艘船，200 枚射弹） | < 5% | ✓ 可实现 |
| 射弹雨（4 艘船，500 枚射弹） | < 3% | ✓ 通过战斗子 Grid 实现 |

### 6.2 优化策略优先级

```
推荐实现顺序（性价比从高到低）:

1. Early Out（距离过滤 + AABB 先判）
   → 实现简单，立即减少 90% 的无效检测

2. 战斗子 Grid（10 km 粒度）
   → 将 O(n×m) 降至 O(n×k)，30x 提升

3. 对象池
   → 零 GC 分配，实现简单

4. 轨道动力学多线程（Parallel.ForEach）
   → 4x 提升，收益 375 μs

5. 时间分片
   → 大规模战斗分摊到多个 tick

6. LOD 碰撞检测
   → 根据距离动态调整精度

7. GPU 加速（暂不考虑）
   → 串行依赖限制，实现成本太高
```

### 6.3 内存规范

| 数据结构 | 内存占用 | 说明 |
|----------|----------|------|
| 战斗子 Grid（200km³） | ~64 KB | 8000 格 × 8 字节 |
| 射弹对象池（4096 枚） | ~16 KB | 固定数组，零 GC |
| 飞船部件（30 个） | ~10 KB | 连续内存，缓存友好 |

---

## 七、版本路线图

```
v0.1.0 (已发布)
  ├── 基础轨道物理系统
  ├── 空间网格系统
  ├── 飞船装配系统
  └── 3D 视图系统

v0.2.0 (预计 2026 Q3)
  ├── 空间站创建与管理
  ├── 轨道保持模式
  ├── 天体相对坐标同步
  ├── 存档兼容性
  └── 3D 视图优化

v0.3.0 (预计 2026 Q4)
  ├── 战斗系统 MVP
  ├── 武器定义与实例化（四种类型）
  ├── 弹丸飞行与碰撞检测（OBB求交）
  ├── 战斗子 Grid（10 km）
  └── 部件损伤系统

v0.4.0 (预计 2027 Q1)
  ├── 战斗 AI 决策器
  ├── 对象池与零 GC
  ├── 性能监控与自适应优化
  └── 接舷战系统

v0.5.0 (预计 2027 Q2)
  ├── 地表探索系统
  ├── 行星着陆与起飞
  └── 前哨站建设

v1.0.0 (预计 2027 Q3)
  ├── 经济与外交系统
  ├── 完整任务系统
  └── 正式版本发布
```

---

## 八、稳定接口与即将变更

### 8.1 稳定接口（不会变更）

| 接口 | 稳定性 | 说明 |
|------|--------|------|
| SpaceGameComp | 稳定 | 核心管理器，接口保持不变 |
| SpaceEntityGroup | 稳定 | 轨道群组，基础字段不变 |
| SpaceEntity | 稳定 | 轨道实体，基础字段不变 |
| OrbitState | 稳定 | 轨道状态，结构不变 |
| SpatialGrid3D | 稳定 | 空间网格，查询接口不变 |
| Ship3D / ShipPartInstance | 稳定 | 飞船装配数据，序列化格式不变 |
| CelestialBodyManager | 稳定 | 天体管理，接口不变 |

### 8.2 即将变更（按新边疆模式调整）

| 模块 | 当前状态 | 变更方向 | 影响 |
|------|----------|----------|------|
| ShipPartDef | 基础形状定义 | 扩展为完整部件定义（支持多形状、stats） | mod 需更新 XML |
| 战斗系统 | 已实现最小闭环（弹丸→命中→部件损伤→结构失效） | 补全四类武器（射线/导弹/无人机）与战斗 AI | 增量扩展 |
| 武器系统 | 已实现（WeaponDef + WeaponInstance + 射弹） | 补全 Ray/Missile/Drone 三类 | 增量扩展 |
| 碰撞检测 | 已实现（线段 OBB 求交 + 多层过滤 + CombatGrid） | 维持并优化 | 稳定 |
| 飞船设计器 | 基础版本 | 扩展为完整装配系统（二阶段建造） | UI 变更 |
| 3D 视图 | 基础版本 | 扩展为战斗视图（弹丸轨迹、命中特效） | UI 变更 |

### 8.3 新边疆模式适配说明

框架将按照新边疆 mod 的需求模式进行调整，但核心接口保持稳定：

1. **飞船装配** — 支持多形状部件（Cube/SphericalCorner/Prism/Cylinder/Sphere）、自由朝向、紧凑度计算
2. **战斗系统** — 支持四种武器类型（Ray/Projectile/Missile/Drone）、射弹无敌我识别、战斗子 Grid
3. **舱室同步** — 支持舱室部件 ↔ Map 双向同步（"扎小人"机制）
4. **性能优化** — 支持战斗子 Grid、对象池、多层过滤、时间分片

---

## 九、新边疆mod具体适配项

### 9.1 飞船类型适配

| 飞船类型 | 框架支撑 | 说明 |
|----------|----------|------|
| 邓氏鱼级巡洋舰 | Ship3D + ShipPartDef | 大型飞船，多部件装配 |
| 玄武号要塞 | Ship3D + 空间站系统 | 超大型结构，支持对接 |

### 9.2 武器类型适配

| 武器名称 | 类型 | 框架支撑 | 特性 |
|----------|------|----------|------|
| 赝相位发射器 | Projectile | WeaponDef + ProjectileInstance | 射弹武器，受引力影响 |
| 轻激光 | Ray | WeaponDef + 射线检测 | 射线武器，穿透多层 |
| 通用激光 | Projectile | WeaponDef + ProjectileInstance | 快速射击，精度高 |
| 近战武器 | 特殊处理 | IShipPartExtension | 接舷战时激活 |

### 9.3 特殊机制适配

| 机制 | 框架支撑 | 说明 |
|------|----------|------|
| 接舷战 | 舱室↔Map同步 | 进入敌舰舱室展开 2D Map 战斗 |
| 飞船残骸打捞 | SpaceEntityGroup | 战败方飞船变为静态物体，可对接打捞 |
| 多 Map 并行 | SpaceMapGenerator | 舱室数量多时需降频 Tick 机制 |

---

**文档版本**: v0.3  
**创建日期**: 2026-06-26  
**适用框架**: PioneerFramework v0.2+  
**参考 mod**: 新边疆（New Frontier）