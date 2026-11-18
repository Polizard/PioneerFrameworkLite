# 先驱框架 Lite（PioneerFrameworkLite）开发文档

> 研究范围：`master` 分支 · 证据快照 `6508d2e`（2026-11-13）
> 文档性质：面向贡献者 / 接手者 / 扩展 Mod 作者的工程手册
> 所有结论均来自当前工作树与仓库内文档，未采信历史记忆

---

## 0. 先读这一段

这份文档回答四个问题：

1. **这是什么项目、现在做到哪一步**（§1–§2）
2. **代码怎么分层、关键机制怎么运作**（§3–§12）
3. **怎么构建、怎么验证、哪些代码不许动、怎么扩展**（§13–§16）
4. **接下来要做什么**（§17）

阅读顺序建议：新接手者先看 §2 的规模表和 §13 的「四条命令」，跑通闸门，再回头读架构。

**证据口径说明**：本文中的文件数、行数、哈希、提交号均为 2026-11-13 实测；测试用例数来自 DesignNotes 交接记录（未在本轮重跑），会明确标注。

---

## 1. 项目定位

### 1.1 一句话

**PioneerFrameworkLite 是 RimWorld 的「地月系太空模拟」底层框架**，作为内容 Mod《DMS：新边疆》的前置依赖，提供轨道力学、天体系统、飞船/空间站一体化物理、太空战斗与 3D 太空视图。

### 1.2 Lite 与完整版的关系

Lite 不是简化演示，而是完整版（`NewFrontierTeam.PioneerFramework`）的**稳定切片**：把已经验证的地月系玩法先交付出来，屏蔽掉多行星、多星系、多 Part 装配这些仍在演进的部分。

| 维度 | Lite（本项目） | 完整版 |
|---|---|---|
| 空间范围 | 地月系 + 太阳（四体） | 多行星 / 多星系 |
| 飞船形态 | 单 Part + 内部 Pocket Map | 多 Part 装配 |
| 存档 packageId | `NewFrontierTeam.PioneerFrameworkLite` | `NewFrontierTeam.PioneerFramework`（依据：`PFViewOutlineAssetBundleProvider.ModPackageId` 常量与打包资源路径；仓库内没有完整版 `About.xml`） |
| 存档格式 | 天体框架 v4（独立） | 不同分支，不可混用 |

`About/About.xml` 明确写了「独立存档前缀，请勿与 PioneerFramework 混用」——这是硬约束，不是建议。

### 1.3 玩家可见的玩法闭环

```
地面造火箭 → 总装 → 发射入轨 → 进入 Pocket Map 内部空间
   → 太空视图（PFView）规划机动 → 变轨 / 转移 / 交会
   → 空间站炮塔对射 → 被击中时内部地图对应位置爆炸
   → 再入 / 着陆 / 回收
```

这条链是项目的验收主线（M4），也是所有子系统存在的理由。

---

## 2. 现状快照

### 2.1 仓库状态

| 项 | 值 |
|---|---|
| 分支 | `master`（工作树干净，仅本文档为新增未跟踪文件） |
| HEAD | `6508d2e` — `fix: 完成 PFview 合并适配并重建 1.7 DLL`（2026-11-13） |
| 提交总数 | 115 |
| 交付 DLL | `1.7/Assemblies/PioneerFrameworkLite.dll`，3,813,888 B，SHA-256 `B4188736952C9C32EEF1A4247F4102B8B967D951D3E843420FA2F9C66EF3115B` |
| 1.6 冻结 DLL | SHA-256 `3C9B3812528C44450AD67A1E22839D2EAA75B2774B336BB64B84D0660B1F62F8`（`tools/check-frozen-1.6.ps1` 硬编码校验） |
| 游戏版本 | RimWorld `1.7.4977 rev1248`（见 `Libs/dependencies.json`） |

### 2.2 代码规模（实测文件数）

| 模块 | .cs 文件数 | 说明 |
|---|---:|---|
| `Source/PioneerFramework` 主工程 | **827** | 唯一交付 DLL 的来源 |
| ├ `World` | 257 | 天体 149 / 巨构 60 / 环世界 39 / 星历桥 5 / 视觉 4 |
| ├ `Gameplay` | 165 | 总装 32 / 着陆 29 / 发射 26 / 地图 23 / 太空 AI 21 / 建筑 16 / 战斗 16 / 星座 2 |
| ├ `PhysicsEngine` | 149 | 轨迹 45 / 部件 21 / 步进 20 / 模型 14 / 制导 11 / 推进 9 / 系统 9 / API 7 / 数学 5 / 气动 4 / 碰撞 3 / 派生 1 |
| ├ `Presentation` | 128 | PFView 111 / Orbital 8 / Visuals 3 / Windows 2 / UiKit 2 / WorldDrawLayer 2 |
| ├ `Debug` | 58 | 游戏内调试与验收场景 |
| ├ `ThirdParty` | 35 | MechJebLib 移植（16 个文件不编入交付 DLL，见 §3） |
| ├ `MapGeneration` | 20 | 本地地图生成 API |
| └ `Platform` | 15 | Mod 入口 / 设置 / DefOf / 内容注册 |
| `Source/PioneerFramework.Tests` | 261 | 游戏集成测试（引用 Verse） |
| `Source/PioneerFramework.Pure.Tests` | 115 | 纯数学测试（零 Verse） |
| `Source/MechJebLibTest` | 92 | 移植回归测试 |
| **测试代码合计** | **468** | 约占主工程的 57% |

### 2.3 内容与资产

| 资产 | 数量 | 位置 |
|---|---:|---|
| Def XML | 78 | `1.7/Defs/`（20 个 `Things` 文件最多） |
| Harmony XML Patch | 4 生效（另有 1 个 `.pending` 未启用） | `1.7/Patches/` |
| GLSL 着色器 | 24 | `1.7/Shaders/` |
| Unity `.shader` | 4 | `Shaders/`（根目录，打包用） |
| AssetBundle | 6 | `1.7/AssetBundles/pfview_{toon,outline}_{win,linux,mac}` |
| 设计/交接文档 | 67（54 顶层 + 13 在 `完整版归档…` / `已弃置` 子目录） | `Source/PioneerFramework/DesignNotes/` |
| 构建与校验脚本 | 19 个顶层 `.ps1`（另有 4 个在 `tools/PFViewShaderBundle/`）+ 13 个 `.py` | `tools/` |
| 扩展示例 | 3 个可编译工程 + 1 个纯文档样例 | `Examples/` |

### 2.4 测试口径（来自 DesignNotes，未重跑）

| 套件 | 用例数 | 来源 |
|---|---:|---|
| `PioneerFramework.Tests` | 1855 | `后端变更授权-20261111` 判据 |
| `PioneerFramework.Pure.Tests` | 1056 | 同上 |
| `MechJebLibTest` | 6271 | `交接-20261101` 全量闸门 |
| Python 工具测试 | 28 | `后端变更授权-20261111` |

⚠ 1855 / 1056 / 28 来自同一份 `后端变更授权-20261111`，MJ 的 6271 来自 `交接-20261101`，**不是同一次运行**。自己跑一遍 `tools/test-all.ps1` 才是当前真相。

---

## 3. 技术栈与版本矩阵

| 项 | 值 | 依据 |
|---|---|---|
| 目标框架 | `net10.0` | `PioneerFramework.csproj` |
| SDK | **10.0.402，`rollForward: disable`** | `global.json` |
| 语言 | `<LangVersion>latest</LangVersion>`（SDK 10 → C# 14），`Nullable=enable` | csproj |
| 输出名 | `PioneerFrameworkLite.dll` | csproj |
| 游戏引用 | `Libs/RimWorld/*.dll`，含 `Assembly-CSharp`、`UnityStubs`、**Waylith / Waylith.UnityCompat**、`Brutal.Core.{Numerics,Logging,Common,Strings}` | `dependencies.json` + `GameReferences.props` |
| Harmony | 3.0.0.0（Workshop 2009463077） | `About.xml` + `dependencies.json` |
| 轨道数学 | MechJebLib 移植：`Lambert`/`Gooding`、`TwoBody/Shepperd`、`Primitives`（部分）等。⚠ **`ODE/`、`Interpolants/`、`Farnocchia`、`Bisection`、`Newton`、`Vec`、`VecOps`、`ObjectPool` 共 16 个文件被 csproj 明确排除出交付 DLL**，只在测试工程里独立重编 | csproj + `ThirdParty/MechJebLib/README.md` |
| 测试框架 | xUnit 2 | `.Tests` 工程 |
| 多版本加载 | `LoadFolders.xml`：`v1.6 → 1.6/`，`v1.7 → 1.7/` | 根目录 |

**SDK 精确到什么程度**：`rollForward: disable` 意味着本机必须装 10.0.402，装 10.0.403 会直接拒绝。这是为了确定性构建——不同 SDK 的 Roslyn 可能产出不同字节。

---

## 4. 仓库地图

```
PioneerFrameworkLite/
├── About/About.xml                  Mod 元数据（含 Harmony 依赖、独立 packageId）
├── global.json                      SDK 钉死 10.0.402
├── LoadFolders.xml                  1.6 / 1.7 分树加载
├── 1.6/                             冻结线：只服本地老存档，不再发行
│   ├── Assemblies/PioneerFrameworkLite.dll   ← 哈希钉死，改动必须同 commit 改钉
│   ├── AssetBundles/ Defs/ Patches/ Textures/
├── 1.7/                             主线（当前全部开发都在这里）
│   ├── Assemblies/PioneerFrameworkLite.dll   ← 唯一交付 DLL
│   ├── AssetBundles/               6 个 shader bundle
│   ├── Defs/                       78 个 XML，27 个子目录
│   ├── Patches/                    4 个生效 patch + 1 个 `.pending`
│   ├── Shaders/                    24 个 GLSL
│   ├── Textures/  Languages/
├── Shaders/                        4 个 Unity .shader（打包源）
├── Source/
│   ├── PioneerFramework/           ★ 主工程
│   ├── PioneerFramework.Tests/     游戏集成测试（引用 Verse）
│   ├── PioneerFramework.Pure.Tests/ 纯数学测试（零 Verse）
│   ├── MechJebLibTest/             移植回归测试
│   ├── TestSupport/                测试共享设施
│   ├── TopologyLab/                拓扑实验工具
│   ├── PioneerFramework.Bench/     基准
│   ├── 已弃置/                     已废弃代码（不参与构建，仅供检索）
│   ├── .editorconfig               代码风格
│   ├── Directory.Build.props
│   └── GameReferences.props        主工程与测试共享的引用配置
├── Examples/                       3 个可编译工程 + 1 个文档样例
├── Docs/LaunchTransitAPI.md        对外发射 API 文档（唯一一份）
├── tools/                          构建 / 闸门 / 基准 / 着色器打包
├── CLAUDE.md                       AI 助手入口（含禁改清单）
├── BACKEND-LOCK.md                 后端锁规则
├── CONTRIBUTING.md                 贡献流程
└── README.md
```

### 4.1 主工程目录

```
Source/PioneerFramework/
├── PhysicsEngine/          ★ 物理内核（壳内，外部只能走 Api/）
│   ├── Api/                7   壳外唯一入口
│   ├── Model/              14  实体/部件/体积/分级
│   ├── Step/               20  物理步宿主与子步
│   ├── Trajectory/         45  轨道机动、SOI、N 体、燃烧
│   ├── Parts/              21  部件功能化数据（引擎/罐/装甲/货舱…）
│   ├── Guidance/           11  发射/交会/悬停/导弹制导
│   ├── Propulsion/         9   推进合成、推力轴、分级
│   ├── Systems/            9   姿控/电力热/传感器/角动量
│   ├── Aero/  Collision/   4 / 3
│   ├── Math/               5   DVector3 / FrameVec3 / 惯量张量 / slab
│   └── Derived/            1
├── Gameplay/               玩法层（RimWorld 生命周期桥接）
│   ├── Buildings/          16  功能建筑 ThingComp
│   ├── Maps/               23  Pocket Map 与坐标映射
│   ├── Launch/             26  发射、过场、分级钩子、燃料换算
│   ├── Landing/            29  着陆、回收、再入
│   ├── GroundAssembly/     32  地面总装流程
│   ├── Combat/             16  战斗管理、射弹、命中、时间倍率
│   ├── SpaceAI/            21  太空 AI 与遭遇
│   └── Fleet/              2   星座组网
├── World/                  世界模拟
│   ├── Celestial/          149 天体框架（API 35 / Core 42 / Adapters 39 / Comps 13 / Definitions 15 / Runtime 5）
│   ├── Megastructures/     60  巨构框架
│   ├── RingWorld/          39  环世界实现
│   ├── Ephemeris/          5   既有星历桥
│   └── Visuals/            4   天空/恒星/行星层
├── Presentation/           128 表现层
│   ├── PFView/             111 3D 太空视图（主 Tab + 多个 partial + 子系统）
│   ├── Orbital/            8   轨道机动规划 UI
│   ├── WorldDrawLayer/     2   世界地图绘制层
│   └── Visuals/ 3 · Windows/ 2 · UiKit/ 2
├── MapGeneration/          20  本地地图生成 API
├── Platform/               15  Mod 入口、设置、DefOf、内容注册
├── Debug/                  58  调试页签 + 验收场景
└── ThirdParty/MechJebLib/  35
```

**注意几个空目录**：`Base/`、`Ships3D/`、`AutoTest/`、`UnityTest/` 在当前 master 中**没有 `.cs` 文件**——它们是历史重构（2026-11-01 类重组、Ships3D 移除）留下的壳。`BACKEND-LOCK.md` 与 `CONTRIBUTING.md` 中仍写着 `Base/**`、`Components/**`、`Combat/**` 这类旧路径，**已与实际树不符**；真正生效的是脚本里的路径（见 §14.1）。

---

## 5. 运行时入口与启动链

### 5.1 Mod 入口

`Platform/Entry/PioneerFrameworkLiteMod.cs`（类 `PioneerFrameworkLiteMod : Mod`）构造函数只有三步：

```csharp
public PioneerFrameworkLiteMod(ModContentPack content) : base(content)
{
    Settings = GetSettings<PioneerFrameworkLiteSettings>();
    Celestial.CelestialFrameworkStartup.Initialize();   // 能力 / 组件工厂 / codec / 运行时服务
    Components.ComponentRegistry.RegisterBuiltIns();    // 部件数据类型
    Components.PartRoleTable.Validate();                // 角色表自检：缺行 = 启动期报错
}
```

顺序是刻意的：**能力注册必须早于任何世界数据反序列化**，否则读档时未知组件会被判为无效；部件自检放在天体框架初始化之后，检查的是「全部注册都做完」的状态。

Harmony patch 不在此处注册——RimWorld 自动扫描 `[HarmonyPatch]`。

### 5.2 Mod 设置（`PioneerFrameworkLiteSettings`）

同文件内的 `partial class PioneerFrameworkLiteSettings : ModSettings` 暴露给玩家的开关：

| 设置 | 默认 | 说明 |
|---|---|---|
| `replaceWorldSky` | 开 | 用模组补画的太阳/星空**替换**原版：XML patch 在 def 解析期把原版 `Sun` / `Stars` 两层的 `worldDrawLayer` 换成 `PF_GlobalDrawLayer_Sun/Stars`（`Visible => !开关 && base.Visible`） |
| `spaceAiRandomEncounters` | 关 | 太空 AI 随机遭遇 |
| `kerbinMagicWheel` | 关 | 「坎星魔法动量轮」：开启后动量轮不累积角动量 |
| `showWorldRadarScanEffect` | 开 | 世界地图编队雷达扫描特效 |
| `pfviewFocusCameraMode` | `ThirdPersonHull` | 聚焦编队默认镜头（Free / 船体正向 / 速度正向），含旧键迁移 |
| `useFastStellarRenderer` / `stellarQuality` | 关 / `VisualFirst` | 恒星渲染器快速模式与画质档位（`PioneerFrameworkLiteSettings.Stellar.cs`） |

「替换星空」为什么必须成对：原版太阳/星空画在球心外 20 单位、平时靠行星壳挡；行星表面 shader 是单面剔除，镜头一进壳就整片露出。只补画不停用会看到两个太阳，只停用不补画则天上空白。

### 5.3 游戏组件（自动发现，无 XML）

RimWorld 会扫描程序集里所有非抽象 `GameComponent` 子类并自动实例化，**不需要 Def**（`1.7/Defs` 中没有任何 `GameComponent` 定义）。实测共 17 个：

| 组件 | 职责 |
|---|---|
| `SpaceGameComp` | **物理步唯一宿主**（主文件 + 8 个 partial） |
| `TimeScaleManager` | 时间倍率（太空/战斗分别管理） |
| `CelestialBodyManager` / `CelestialBodyLayerManager` | 天体实例与表层 |
| `CombatManager` | 战斗（partial 拆分） |
| `CombatGrid` | 3D 空间索引 |
| `SensorManager` | 传感器目标管理 |
| `SpaceAiManager` / `AutonomousVehicleManager` | 太空 AI 与无人载具 |
| `BoardingManager` | 跳帮 |
| `ConstellationGameComp` | 星座组网 |
| `LaunchTransitManager` / `RocketLaunchController` | 发射过场与流程 |
| `LandingTransitManager` | 着陆过场 |
| `ShipSurfaceTransferLedger` | 船体表面转移账本 |
| `SpaceDebrisTracker` | 太空碎片（`IExposable`） |
| `MegastructureManager` | 巨构运行时 |

补充：`GameComponentRef<T>` 不是 GameComponent，是组件查找缓存工具。

### 5.4 物理步宿主的内部拆分

`SpaceGameComp.cs` 是最重的一块，按 partial 分工（**实际调用顺序以 `GameComponentTick` 为准，不要凭 partial 名字猜**）：

| partial | 管什么 |
|---|---|
| `CelestialStep` | 天体运行时推进与帧偏移 |
| `FrameSwitch` | SOI / 帧切换 |
| `Burn` | 待施加冲量、燃烧执行 |
| `Guidance` | 制导接入 |
| `Hover` | 悬停 |
| `Staging` | 分级 |
| `Time` | 时间基与倍率协调 |
| `Persistence` | 存档读写 |

`MapArchiveService` 以静态服务形式接收宿主参数（显式传参，不持有状态），这样天体侧的预演链路不会在宿主身上残留共享可变状态。

### 5.5 UI 入口

- `MainTabWindow_PFView` —— 主太空视图（Def `PF_View`，即 `1.7/Defs/MainButtonDefs`）
- `PF_Megastructures` —— 巨构主界面（Def `PF_Megastructures`）
- `Window_Steering` / `Window_MissileControl` / `Window_PFViewBindings` —— 驾驶与武器绑定
- `Window_RocketAssembler` / `Window_LandingPlacement` / `ManeuverQueueWindow` / `Window_Rendezvous` —— 总装、着陆点、机动队列、交会
- `DebugTabMenu_PFL` —— 游戏内验收入口（58 个 Debug 文件）

---

## 6. 架构分层与依赖规则

### 6.1 六层结构

```
Presentation  ──┐
                ├── 只允许向下调用，绝不反向
Gameplay      ──┤
                │
World         ──┤
                │
PhysicsEngine ──┘   ← 内核
Platform            ← 只做注册与装配
Debug               ← 可依赖任意层
```

### 6.2 最硬的一条：壳外只能走 `PhysicsEngine/Api/`

`交接-20261101-类重组现场.md` 明确定义：

> **壳外唯一入口** = `PhysicsEngine/Api/`：`SpacecraftSnapshot` / `PhysicsApi` / `PhysicsCommands` / `PhysicsQueries` / 载荷契约两个。
> 壳外代码**不许**直接引用 `SpaceEntityGroup` / `SpaceEntity` / `SpacePart` / `SpaceGameComp` / `ManeuverPlan` / `OrbitState` / `ThrustAxis` / `RadarAim` / `SpaceMapComp` 等。

这条规则由 `tools/check-architecture.ps1 -Fail` 机器执行，**ratchet 模式**：基线文件 `tools/architecture-baseline.json` 记录每个文件当前的违规计数，新增即红；基线归零后文件可删。

⚠ 2026-11-13 实测：master 上该闸门**是红的**——`baseline: 0 / actual: 14 文件 49 处引用`（PFView 合并把 cockpit、武器、引擎尾焰、建筑视觉等直接读引擎类型的代码带回表现层）。这是当前唯一红的静态闸门，处理方式二选一：收敛到 `PhysicsApi` / `PhysicsCommands` / `PhysicsQueries`，或运行 `-Update` 重写基线（等于承认存量）。

### 6.3 概念状态收敛（2026-11-01 类重组成果）

`SpaceEntityGroup` 上原本散落的 32 个字段被收成 4 个值类型：

| 概念类型 | 原散字段数 | 作用 |
|---|---:|---|
| `PowerThermalState` | 15 | 电力 / 热 / 电池 / 散热 |
| `PropulsionSelection` | 2 | 主网 / RCS 选择（输入单向下发，不回写） |
| `AttitudeRequest` | 3 | 姿态请求 |
| `SensorSuite` | 12 | 传感器套件与雷达瞄准 |

好处：状态是值语义，复制与比较天然可控（这也是 2026-11-02「船退出天体事务」能安全删掉整条 2PC 的前提）。`IsStealth` 仍是组级字段（19 个文件直接读，收敛收益小）。

### 6.4 提交模型：天体先提交，船后跑（2026-11-02 起）

**旧的「船参与两阶段提交」已被删除**，不要按老交接件理解当前实现：

```
1. AdvanceCelestialPhysicsInterval
     provider + clock 先提交该区间的天体状态（Coordinator.Prepare → Publish）
2. UpdateAllMovements(tsm, step)
     船在「已提交区间」上直接跑实时图（dt / 子步 / start / end 显式传入）
```

- 已删除：`ShipParticipant` / `ShipPreparation` / `CopyForCelestialStep` / `CelestialShipInputCopy` 等 2PC 结构
- 后果：动态天体命令**不再能中途 abort 船**；船步失败不会回滚天体
- 失败语义：船步异常 → `celestialShipStepFaulted = true`、`CelestialPhysicsErrors = [ShipStepFault]`、`Log.Error`、`throw`，随后 `AdvancePhysics` 早退（停推进，不带半更新状态继续跑）
- `PublicationConflict` 只影响天体区间：船还没跑，失败就 break，下一 tick 重试

台架证据（2026-11-02）：copy/步 = 0.00 µs、commit/步 = 0.00 µs，step 549 µs（较前 −34 µs），状态哈希与改前**逐位相同**。

`MapArchiveService` 仍是静态服务、显式传宿主，但它服务的是**天体侧**的预演/档案，不是船的 2PC。

---

## 7. 物理内核 `PhysicsEngine`

### 7.1 坐标系与帧

| 概念 | 定义 |
|---|---|
| 系统系（System Frame） | 以帧根天体为原点的惯性系；`DVector3` 双精度 |
| 帧根 | 恒星或行星。地月系中 Earth 与 Moon 有各自的帧 |
| SOI | 影响球，判定用**轴对齐立方体** `max(|x|,|y|,|z|) ≤ R`（比球体便宜且避免边界抖动） |
| BOI | 有界轨道注入区；区内**恒星祖先不入引力源表**（帧系非惯性 / 公转抵消，2026-11-05 裁定）；`GravitySource.isUniform` 的常向量机制保留备用 |
| 帧切换 | `FrameTransitionCore` 用最近公共祖先帧（LCA）平移位置/速度 |

`FrameVec3` = 帧标识 + 向量；`FrameTransitionCore` 保证跨帧引用不会静默混用。`tools/check-frame-crossing.ps1` 做静态检查，`check-frame-names.ps1` 管命名。

### 7.2 轨迹与机动（45 个文件）

| 子领域 | 代表文件 |
|---|---|
| N 体积分 | `NBodyIntegrator`、`BodySampleCache` |
| 解析转移 | `HohmannCore`、`TwoImpulseTransfer`、`AnalyticTransferCore`、`TransferGeometry` |
| Lambert | `Gooding`、`OrbitTransferPlanner` |
| 单根数机动 | `Operation*` 一族（Apoapsis / Periapsis / Circularize / Inclination / Lan / Longitude / SemiMajor / Stationary / ResonantOrbit / Eccentricity / Ellipticize / KillRelVel） |
| 燃烧执行 | `BurnCore`、`BurnStep`、`BurnCalculator`、`BurnArc`、`BurnArcCompensation`、`BurnRefineNewton`、`TrimBurn` |
| 规划门面 | `ManeuverPlan`、`PlanNode`、`PlanIntent`、`OrbitPlanner`、`ManeuverPlanner` |
| 相位与自动分级 | `PhaseWindow`、`AutoStageCore` |
| SOI | `SoiCheck`、`FrameTransitionCore` |
| 地表系 | `SurfaceFrame`（自转轴固定 +Y，无倾角；`FixedToEci` / `SurfaceVelocity` / `GroundSpeed`，纯层可单测） |

### 7.3 模型（14 个文件）

- `SpaceEntityGroup` —— 一艘船/一座站。partial 拆为 `CelestialPlanning` / `CelestialStep` / `Engine` / `Inertia`
- `SpaceEntity` —— 单 Part 化后的实体，绑定 `linkedMapId`
- `SpacePart` —— 物理体积（AABB）与质量
- `ProjectileInstance` —— 物理射弹
- `StagePartition` / `StageSeparation` / `StageTypes` —— 分级
- `GroupBirth` —— 出生协议（新群组如何进入世界）
- `PartGeometry` / `ISpatialEntity`

### 7.4 部件功能化数据（21 个文件）

每个建筑/部件不再是散落的 `Comp` 字段，而是注册到 `ComponentRegistry` 的数据类型：

`EngineComponentData`、`PropellantTankComponentData`、`RadiatorComponentData`、`ReactorComponentData`、`BatteryComponentData`、`SensorArrayComponentData`、`JammerComponentData`、`WeaponMountComponentData`、`PayloadBayComponentData`、`CargoComponentData`、`RoomComponentData`、`IntakeComponentData`、`ControlCoreComponentData`、`HullMassComponent`、`PartInertiaComponent`、`PartRole` / `PartRoleTable`、`ArmorMath`。

`PartPropulsionBuilder` 把部件数据聚合成 `GroupPropulsionAggregator` → `CompositePropulsionSource` / `StandardPropulsion`，最终得到 `ThrustAxis` 与可用推力。

### 7.5 推进与工质

- `PropulsionSelection`：主网 / RCS 两套选择，输入单向下发
- `FuelPlan`：燃烧计划与工质分配
- `EngineThrust`：单引擎推力曲线
- `StageSeparationCore`：分级时的质量/惯量/工质重分配
- dV：`SpaceEntity.RemainingDeltaV` 由工质网络按齐奥尔科夫斯基公式**实时推导**，不落盘

### 7.6 制导（11 个文件）

`LaunchGuidanceCore`（上升段）、`LaunchTargeting`（目标入轨点）、`RendezvousCore` / `RendezvousGuidanceCore` / `RendezvousLadder` / `TerminalRendezvousLaw` / `RendezvousGoal`（交会）、`HoverCore`（悬停）、`MissileGuidanceCore`（比例导引 + 追丢判定）、`LandingQuote`（着陆预算）、`AlignFeasibility`（姿态可达性）。

### 7.7 其他

- `Aero/`（4）：大气再入与气动阻力
- `Collision/`（3）：slab 射线求交、3D 网格
- `Systems/`（9）：`AttitudeRequest`、`TorqueFreeRotation`、`WheelDesaturationCore`、`PowerThermalCore`、`PowerThermalState`、`SensorCore`、`SensorManager`、`SensorSuite`、`ContactCore`
- `Math/`（5）：`DVector3`、`FrameVec3`、`InertiaTensor`、`SlabMath`、`SpaceConstants`

---

## 8. 天体框架 `World/Celestial`

> 权威文档：[Celestial/API/README.md](file:///d:/Steam/steamapps/common/RimWorld/Mods/PioneerFrameworkLite/Source/PioneerFramework/World/Celestial/API/README.md)（契约状态：2026-11-05 本地地图生成版）

### 8.1 结构

```
Celestial/
├── API/           35   对外契约（服务接口 / 描述对象 / 扩展点 / 文档）
├── Core/          42   纯核：运动、烘焙、几何、查询、hash
├── Adapters/      39   RimWorld 桥接（含 Physics/ 后端锁区）
├── Comps/         13   组件实现（球形、环境、生物群系、地图、地表、视觉…）
├── Definitions/   15   Def 解析与校验
└── Runtime/        5   运行时装配与推进
```

### 8.2 版本与兼容

| 项 | 当前值 |
|---|---|
| 框架存档格式 | **v4**（v1/v2/v3 明确拒绝，必须新建世界） |
| 默认解析提供者检查点载荷 | **v2** |
| 运动协议 | `PhysicsContractVersion(1,0)` |
| hash schema | 创建/命令/物理/移除 = 2/1/1/1，与存档版本独立演进 |
| 编码 | 固定二进制字段顺序 + 确定数值格式；损坏载荷原样保留诊断 |
| 内建球面细分 | 只允许 0～10（资源保护） |

**不做自动迁移**是有意选择：宁可让旧档开不了，也不让新格式被历史特判污染。

### 8.3 运动模型

`1.7/Defs/CelestialBodies/CelestialBodies_SolarSystem.xml` 已从「v1 烘焙载荷」迁移为可读 `<motion>` 声明（2026-11-11）：

- 太阳系四体全部走 `existing.circular.v2`
- v2 的星历桥自己产出权威自转：轴、相位、逆行、同步目标
- 月球是真潮汐锁定：`<orbitPeriodSeconds>317900</orbitPeriodSeconds>`，`<synchronousTargetReference>frameOrigin</synchronousTargetReference>`

⚠ 存档 schema 里默认 provider 仍是 `existing.circular.v1`——v2 是「授权给具体天体」的新格式，两者并存。

由此 `Gameplay/Launch/GetSurfaceState.Pose.cs` 去掉了「排除既有星历桥」的条件——旧逻辑假设桥只给标量日长，没有姿态；v2 之后这个前提消失。

**倾斜自转轴禁区**：`RotationOmega` 仍是标量口径，且无运行时兜底。author 倾斜轴天体时不能用它当速率模长，必须走 `CelestialMotionState.Orientation` / `AngularVelocity`。

### 8.4 动态创建、代次与重试

运行时创建天体走「代次（Epoch）+ 操作回执」：

```csharp
var opened = services.Bodies.OpenEpoch(query.WorldSessionId);
// 提交操作 → 轮询回执 → 终态后使用 InstanceIds
services.Bodies.CloseEpoch(session, epoch);
```

| 限制 | 值 |
|---|---:|
| 同时开放代次 | 8（含 epoch 0） |
| 每代次回执总数 | 4096（pending + 终态合计） |
| 每代次 pending | 256 |

关键语义：

- `OperationExpired` = 旧代次永久过期，**不自动重放**
- 代次用尽时先清 pending 再关已完成代次，框架不会静默淘汰
- 回调阶段禁止创建/移除/关代次/保存/再推进，只允许命令与事件排队
- `Prepare` 期间查询读到旧已提交视图；提交后回调读到新视图

### 8.5 组件与生命周期

组件钩子（以 API README 表格为准）：

| 钩子 | 语义 |
|---|---|
| `OnInitialized` | 只用于新建世界的业务状态初始化 |
| `OnReady` | 新建时物理注册确认后运行；**恢复不重放** |
| `OnAttached` | 读档恢复先解码状态，再绑定上下文与资源（`AttachReason = Restored`） |
| `OnRestored` | 原生对象、物理检查点与上下文都绑好后通知一次 |
| `Advance` | 已提交区间，按显式 `Scheduled` 间隔调度 |
| `Handle` | 明确事件；`EventsOnly` 不进时间调度队列 |
| `CanRemove` / `OnRelease` | 检查阻碍并释放独占资源；Release 最多执行一次，抛错也解除上下文 |

故障策略 `CelestialCompFailurePolicy` 默认 `BlockBody`：关键组件失败阻塞整颗天体，避免出半残天体；可选 `DisableOptional`。`CanRemove` 在两阶段校验中返回 `HostWorkPending` 阻碍，真正 Remove 时重新校验。

Provider 诊断走 `PhysicsBridgeStatus { Unavailable, Ready, Restoring, Faulted }` 与结构化 `Errors`：`Unavailable` / `Restoring` 阻止新推进但保留描述供诊断，`Faulted` 或一致性错误直接阻止推进。

### 8.6 本地地图生成

`Celestial` 侧（`Comps` 中的 MapGeneration 组件）+ `MapGeneration/`（20 文件）共同构成：

- 配方（Recipe）注册与用途派生种子（Usage profile 决定地图多样性）
- 冻结配方 SHA-256（字节级可复现）
- 阶段化生成，支持失败回收
- 关联 Def：`1.7/Defs/MapGeneration/`（3 个 XML）+ `PlanetLayerDefs/`（3 个）

文档：[MapGeneration/API/README.md](file:///d:/Steam/steamapps/common/RimWorld/Mods/PioneerFrameworkLite/Source/PioneerFramework/MapGeneration/API/README.md)

---

## 9. 玩法层 `Gameplay`

### 9.1 建筑组件（16 文件）

| Comp | 作用 |
|---|---|
| `CompThruster` | 引擎：`propellantDefName` / `exhaustVelocity`（默认 9000）/ `massFlowRate`（默认 5）/ `thrustAxis` / `isVectoredRcs` / `machCurve`（吸气曲线） |
| `CompPropellantTank` | 储罐，增加工质容量 |
| `CompSteeringConsole` | 驾驶台（配合 `Window_Steering`） |
| `CompShuttleDock` | 穿梭机停靠 |
| `CompMapTurret` | Map 炮塔 → 3D 物理弹开火 |
| `CompMapDesignator` | 目标标记 / 精确炮击 |
| `CompMissileLauncher` | 导弹发射（配合 `Window_MissileControl`） |
| `CompSensorArray` / `CompActiveRadar` / `CompJammer` / `CompDecoyEmitter` | 电子战四件套 |
| `CompIntakeAir` | 大气吸气（吸气式引擎） |
| `ShipBuildingUI` / `Window_PFViewBindings` | 建筑侧 UI 注册 |

### 9.2 Pocket Map 与坐标映射（23 文件）

地面建筑与太空实体通过 Pocket Map（内部地图）双向绑定：

- `SpacecraftMapParent` / `SpacecraftMapRegistry` / `SpacecraftBridge` —— 船 ↔ 地图
- `SpaceStationMapParent` —— 空间站地图
- `SpaceMapComp`（+`.Mass` / `.Relief`）—— 地图侧物理摘要（质量、工质网络、地形起伏）
- `MapCoordMapper` —— **3D 船体局部坐标 ↔ Map 世界坐标**：`MapCellToShip` / `MapLocalToShip` / `ShipToMapLocal` / `ShipToMapCell` / `ShipToMap` / `ShipToSystem` / `SystemToShip`，外加 `RayExitAabb`（炮口出膛点）
- `MapArchiveManager` + `MapArchiveFormat` / `BuildingRecord` / `CompRecord` / `TerrainRecord` —— 地图归档（随船移动保存/恢复）
- `ShipSurfaceTransfer*` —— 船体表面转移
- `GenStep_RocketCabinLayout` / `GenStep_RoomShell` —— 舱室生成

### 9.3 发射（26 文件）

```
RocketLaunchManager（流程编排）
  ├─ RocketLaunchConditions   条件校验
  ├─ Dialog_RocketLaunch      玩家选项（原版 / PFL 入轨）
  ├─ CompLaunchable_PF        发射能力
  ├─ LaunchTransitService     过场服务（TryStart / TryFindByMapId / TryCancel）
  ├─ LaunchTransitManager     过场生命周期
  ├─ LaunchTransitWorldObject 世界地图上的过场对象
  ├─ LaunchGuidanceCore       上升段制导（引擎侧）
  ├─ GetSurfaceState(+.Pose)  发射场位置与自转速度
  ├─ IRocketStageHooks        分级/回收钩子（接口已定义）
  └─ ChemfuelToDeltaVMapper   燃料 → dV 换算（TODO F1-4）
```

对外 API 文档：[Docs/LaunchTransitAPI.md](file:///d:/Steam/steamapps/common/RimWorld/Mods/PioneerFrameworkLite/Docs/LaunchTransitAPI.md)

### 9.4 着陆（29 文件）

主链：`LandingTransitService` / `LandingTransitManager` / `LandingTransitWorldObject`（过场）+ `LandingWorkflow`（流程编排）+ `LandingQuote`（预算，物理侧）+ `LandingFuelTransaction`（燃料结算）+ `SurfaceTouchdownService`（接地）。

外围：`LandingDestinationService` / `LandingDestinationRuntime`（落点）、`LandingFootprintService` + `LandingFootprint`（着陆场足迹）、`LandingMapStateTransfer` / `LandingMapTransferService` / `LandingSourceMapService`（地图状态搬运）、`LandingRecoveryService` + `LandingRecoveryData` + `RecoveredSpacecraft`（回收）、`LandedGroupWorldObject` / `LandedShipMapParent`（落地后的世界对象）、`LandingMapProtection`（地图保护）、`CaravanArrivalAction_RecoverLandedGroup`（商队抵达回收）。着陆点由 `Window_LandingPlacement` + `LandingPlacementTargeter` 选择。

### 9.5 总装（32 文件）

地面组装流程：设计校验 → 蓝图施工（复用原版 Blueprint/施工）→ 完工火箭 → 发射条件 → 发射。包含 `CompRocketAssembler`、`CompUnfinishedRocket`、`Window_RocketAssembler` 等。

### 9.6 战斗（16 文件）

```
CombatManager（partial 拆分：核心 / Shots / ImpactEvents / Time）
   ├─ FireFromMapBuilding(groupId, targetPos, spawnPos)   Map 炮塔 → 3D 弹
   ├─ FireWeapon(...)                                     太空实体开火
   ├─ CheckSegmentGroupHit(...)                           slab 命中判定
   └─ ImpactEvents                                        命中/爆炸/冲量

ProjectileInstance  物理弹（3D 空间飞行）
WeaponDef/WeaponInstance  武器定义与实例
```

命中链：出膛点 = 炮口射线 ∩ 本船 AABB 六面（`RayExitAabb`）→ `ProjectileInstance` → slab 求交 → 命中唯一 Part → `MapCoordMapper.ShipToMap` → Map 内贯穿弹 + 落点 Bomb 伤害 + 爆闪。

已知边界：范围爆炸与 pawn 伤害在太空 Pocket Map 上因 `GenExplosion` NRE 被后置（TODO F3 二次伤害）。

### 9.7 太空 AI 与遭遇（21 文件）

`SpaceAI` 负责敌对实体决策与遭遇生成；关联 Def：`1.7/Defs/SpaceEncounterDefs/`（2 个）。性能台架：`tools/SpaceAiBenchmark/`。

### 9.8 星座（2 文件）

`Gameplay/Fleet` 只有 2 个文件——星座组网目前是**轻量实现**，不是完整编队系统。真正的编队逻辑在 full 版。

---

## 10. 表现层 `Presentation`

### 10.1 PFView（111 文件）

自研 3D 太空视图，不是 RimWorld 原生渲染管线。

```
MainTabWindow_PFView（主 Window）
├── 场景与相机：Scene / Camera / Render / Piloting
├── 天体与天空：Starfield / StellarVisuals / Atmosphere / Clouds / Terrain / CelestialFramework
├── 实体与战斗：Fleet / BuildingVisuals / WeaponSalvo / ProjectileVisuals / EnginePlume
├── 巨构：Megastructures
└── 子系统
    ├── EnginePlume/          喷焰
    ├── ShipBuildingVisuals/  ★ 公开注册 API（Mod 可扩）
    ├── ShipSkin/             船体蒙皮
    └── ToonOutline/          描边
```

### 10.2 着色器与 AssetBundle

RimWorld **不会加载散装 `.shader`**，`Shader.Find` 恒失败（这是引擎事实，不是本项目缺陷）。因此流程是：

```
Shaders/*.shader                     ← Unity 格式源（4 个：PF_Toon + PFView_OutlineMask / _Dilate / _Composite）
  ↓ tools/PFViewShaderBundle/Build-PFViewToonBundles.ps1
  ↓ tools/PFViewShaderBundle/Build-PFViewOutlineBundles.ps1
1.7/AssetBundles/pfview_{toon,outline}_{win,linux,mac}
  ↓ 运行时双通道取包
PFViewToonShader / PFViewFixedOutlineShaderProvider  →  取不到回退 Standard
```

24 个 GLSL 文件在 `1.7/Shaders/`，是 shader 的源码形态（含 `PF_Toon`、`PF_PlanetBands`、`PF_PlanetClouds`、`PF_ProjectileGlow`、`PF_Ring*`、`PF_ShipSkin`、`PF_StellarVolume*`、`PF_TerrainLit`、`PFLit` 等）。

⚠ 打包机用的是中国版编辑器 `2022.3.35f1c1_dcf06934cc2b`（本机唯一可用版本，工具链已放行同 patch）。**若游戏内加载失败，第一嫌疑就是编辑器版本**，其次才是 bundle 名。校验脚本：`tools/PFViewShaderBundle/Assert-PFViewOutlineBundleBuild.ps1` 与 `Assert-PFViewOutlineBundleTooling.ps1`。

### 10.3 ShipBuildingVisuals 扩展 API

Mod 作者可注册自定义建筑视觉：

```csharp
// 状态提供者：驱动装饰性动画（渲染时钟，可暂停）
ShipBuildingVisualRegistry.Default.RegisterStateProvider("Example.DeckBeaconSweep.v1", new DeckBeaconSweep());
// Mesh 提供者：自定义几何
ShipBuildingVisualRegistry.Default.RegisterMeshProvider("Example.MyMesh.v1", new MyMesh());
```

XML 侧走 `ShipBuildingVisualDef`（`nodes` 描述 mesh / 位置 / 缩放 / 颜色 / `bindYaw`）+ `ShipBuildingVisualExtension`（挂到 ThingDef 的 `modExtensions`）。样例（纯文档，无工程）：[Examples/ShipBuildingVisualSample](file:///d:/Steam/steamapps/common/RimWorld/Mods/PioneerFrameworkLite/Examples/ShipBuildingVisualSample)

### 10.4 Orbital UI

轨道机动规划界面：机动队列、Lambert 转移、交会窗口。`Presentation/Orbital/` + `ManeuverQueueWindow` + `Window_Rendezvous`。

---

## 11. 巨构框架与环世界

### 11.1 可插拔 Backend

```
World/Megastructures/（60）
├── API/            1   IMegastructureService
├── Core/          28   框架模型 / 地表 / 资源 / 能力运行时（含 Backends 3、Surfaces 9、Motion 2、Environment 1）
├── Definitions/    1   MegastructureDef
├── Adapters/      21   RimWorld 桥接（Presentation 7 / Surfaces 9 / 根 5）
├── Runtime/        7   MegastructureManager + BackendRequestFactory + 4 个 Structure*Service + SurfaceBindingMigration
└── UI/             2   MainTabWindow_Megastructures / MegastructureDeferredGuard

World/RingWorld/（39）
├── Core/          11   环几何 / 拓扑 / 旋转 / 气候 / 路径搜索
├── Adapters/       5   含 RingWorldNativeBackend / RingBackendProvider
└── 根目录         23   服务 / 世界对象 / 5 个 WorldDrawLayer / 镜头 / 存档数据 / WorldGenStep
```

设计意图：巨构是「世界级结构 + 可插拔 Backend + 能力（Capability）」。环世界只是第一个 Backend；`Examples/MegastructureExtensionSample` 演示如何写自定义 Capability。

Def：`1.7/Defs/MegastructureDefs/`（2 个）。

---

## 12. 内容层（XML）

### 12.1 Defs 分布（78 个 XML，27 个子目录）

| 目录 | 数量 | 内容 |
|---|---:|---|
| `Things/` | 20 | 建筑、部件、回收船等 ThingDef |
| `CelestialFramework/` | 6 | 框架配置 |
| `Misc/` | 6 | Mod 设置、世界生成入口等 |
| `WorldObjectDefs/` | 6 | 过场对象、空间站标记 |
| `ShaderDefs/` | 5 | shader 引用 |
| `Combat/` | 3 | 武器 / 弹 |
| `MapGeneration/` | 3 | 地图配方 |
| `PlanetLayerDefs/` | 3 | 行星表层 |
| `CelestialBodies/` | 2 | 太阳系四体 |
| `MegastructureDefs/` | 2 | 巨构 |
| `ShipParts/` | 2 | 船部件 |
| `SpaceEncounterDefs/` | 2 | 遭遇 |
| `MainButtonDefs/` | 2 | PFView / 巨构主按钮 |
| `WorldDrawLayerDefs/` | 2 | 世界绘制层 |
| `BiomesDefs/` | 2 | 生物群系 |
| `Armor` `HediffDefs` `JobDefs` `WorkGiverDefs` `TerrainDefs` `WeatherDefs` `WorldGenStepDefs` `ThingDefs` `Propellants` `Rooms` `PFView` `Debug` | 各 1 | 零散定义 |

### 12.2 Patch（4 生效 + 1 待启用）

`1.7/Patches/`：

| 文件 | 作用 |
|---|---|
| `CelestialEnvironment.xml` | 天体环境对接 |
| `GlobalWorldDrawLayers_SkySwap.xml` | 把原版 `Sun` / `Stars` 两层的绘制类换成 `PF_GlobalDrawLayer_*`（配合「替换星空」设置） |
| `PlanetLayerDefs_CelestialSurface.xml` | 行星表层 |
| `PFViewShipBuildingVisuals.xml` | 建筑视觉绑定 |
| `PFViewProjectileVisualBindings.xml.pending` | 射弹视觉绑定，**未启用**（扩展名 `.pending` 不会被加载） |

### 12.3 校验工具

| 脚本 | 作用 |
|---|---|
| `tools/validate_config.py` | Defs 全面校验（字段、引用、承载） |
| `tools/check-def-fields.ps1` | Def 字段完整性 |
| `tools/celestial_schema.py` / `celestial_payload.py` / `celestial_localization.py` | 天体 Def 专项校验 |
| `tools/validate_celestial_examples.py` | 扩展示例 XML 结构校验（不编译） |

---

## 13. 构建、验证与交付

### 13.1 环境准备

```powershell
# 1) 精确 SDK（10.0.402）
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile "$env:TEMP\dotnet-install.ps1"
& "$env:TEMP\dotnet-install.ps1" -Version 10.0.402 -InstallDir "C:\Program Files\dotnet"

# 2) 同步本机游戏依赖（生成/校验 Libs/dependencies.json 里的哈希）
powershell -ExecutionPolicy Bypass -File tools/update-game-dependencies.ps1
powershell -ExecutionPolicy Bypass -File tools/check-game-dependencies.ps1

# 3) 可选：让脚本优先用仓库内的便携 SDK（.zcode/dotnet10 存在时才生效，否则回落到系统 SDK）
. ./tools/Use-ProjectDotnet.ps1
```

### 13.2 输出路径规则（重要）

| 配置 | 输出 | 是否入库 |
|---|---|---|
| Release | `1.7/Assemblies/PioneerFrameworkLite.dll` | ✅ 交付件 |
| Debug | `Source/PioneerFramework/bin/Debug/` | ❌ gitignore |
| Release + `EnableDiagnostics` | 必须显式 `-p:OutputPath=<临时目录>` | csproj 里写了 `Error`，**拒绝写进 1.7** |

也就是说：`dotnet test` / `dotnet build -c Debug` 在结构上不可能覆盖交付 DLL。

需要带 PDB 进游戏调试时：

```powershell
dotnet build Source/PioneerFramework/PioneerFramework.csproj -c Debug -p:OutputPath=..\..\1.7\Assemblies\
```

### 13.3 四条命令（30 秒上手）

```powershell
git log -1 --oneline; git status --short
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-architecture.ps1 -Fail
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-frame-crossing.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-backend-lock.ps1 -Fail
```

期望 `exit 0`。⚠ 实测例外：`check-architecture.ps1` 在 master `6508d2e` 上是红的（14 文件 / 49 处，见 §6.2）；其余三条为绿。然后跑全量：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-all.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/MutationCheck.ps1   # 约 4 分钟
```

### 13.4 交付件重建（唯一正确方式）

```powershell
powershell -ExecutionPolicy Bypass -File tools/rebuild-dll.ps1      # 重建并写 1.7/Assemblies
powershell -ExecutionPolicy Bypass -File tools/check-committed-dll.ps1
powershell -ExecutionPolicy Bypass -File tools/verify-deterministic.ps1
powershell -ExecutionPolicy Bypass -File tools/Deploy-LiteToGame.ps1 # 部署到游戏目录
```

**改了任何 `.cs` 都必须重建 DLL 并一起提交**，否则 CI 的「已提交 DLL == Release 构建」会红。

### 13.5 CI 闸门（`.github/workflows/ci.yml`，windows-latest / 30 分钟超时）

按顺序执行：

| 步骤 | 命令 | 作用 |
|---|---|---|
| 后端锁（仅 PR） | `check-backend-lock.ps1 -Base <base.sha> -Fail` | 未经授权改后端直接红 |
| .NET | `setup-dotnet` 10.0.402 + 缓存 | 与 `global.json` 对齐 |
| Python | 3.12 | Def/工具校验 |
| 游戏引用 | `check-game-dependencies.ps1` | 哈希比对 `Libs` |
| Debug 构建 | `dotnet build -c Debug -p:OutputPath=$RUNNER_TEMP/pfl_debug` | 编译闸门 |
| Release 构建 | `dotnet build -c Release -p:OutputPath=$RUNNER_TEMP/pfl_release` | 交付件母本 |
| 确定性 | `verify-deterministic.ps1` | 双构建同字节 |
| 交付一致性 | `check-committed-dll.ps1` | 仓库里的 DLL == Release 产物 |
| 游戏集成测试 | `dotnet test PioneerFramework.Tests` | Verse 相关 |
| 纯数学测试 | `dotnet test PioneerFramework.Pure.Tests` | 零 Verse |
| MechJebLib | `dotnet test MechJebLibTest` | 移植回归 |
| Def/DefOf/本地化 | `python tools/validate_config.py` | XML 全量 |
| Python 回归 | `python -m unittest discover -s tools/tests` | 工具自身 |
| 1.6 冻结 | `check-frozen-1.6.ps1` | 哈希钉死 |

注意 CI 里 Debug/Release 都输出到 `RUNNER_TEMP`，**不会**写 `1.7/Assemblies`；交付件比对是「已提交文件 vs Release 产物」。另外 CI 不做资源存在性清单校验——`1.6` 由哈希脚本管，`1.7` 由 DLL 一致性间接管。

### 13.6 工具清单（19 个顶层 `.ps1` + 4 个 `PFViewShaderBundle` 脚本 + 13 个 `.py`）

| 脚本 | 用途 |
|---|---|
| `test-all.ps1` | 全量闸门（镜像 CI） |
| `rebuild-dll.ps1` / `check-committed-dll.ps1` / `verify-deterministic.ps1` | 交付件 |
| `check-architecture.ps1` | 分层依赖 |
| `check-frame-crossing.ps1` / `check-frame-names.ps1` / `list-frame-identifiers.ps1` | 帧系 |
| `check-backend-lock.ps1` / `add-backend-lock-banner.ps1` | 后端锁 |
| `check-frozen-1.6.ps1` | 1.6 冻结哈希 |
| `check-def-fields.ps1` / `validate_config.py` | Def 校验 |
| `check-game-dependencies.ps1` / `update-game-dependencies.ps1` | 游戏依赖 |
| `check-rename-only-diff.ps1` | 纯改名 diff 检测 |
| `MutationCheck.ps1` | 变异测试 |
| `measure-bake-calibers.ps1` | 烘焙标定 |
| `Deploy-LiteToGame.ps1` / `Use-ProjectDotnet.ps1` | 部署 / SDK 切换 |

---

## 14. 代码治理

### 14.1 后端锁（`【NOSTEPANYMORE】`）

**规则来源**：`BACKEND-LOCK.md` + `CLAUDE.md`。
**机器执行**：`tools/check-backend-lock.ps1`（CI 以 `-Fail` 运行）。

当前脚本里真正受保护的路径（以脚本为准，不要以旧文档为准）：

```
^Source/PioneerFramework/PhysicsEngine/
^Source/PioneerFramework/Gameplay/Combat/
^Source/PioneerFramework/Gameplay/Launch/
^Source/PioneerFramework/Gameplay/Landing/
^Source/PioneerFramework/Gameplay/GroundAssembly/
^Source/PioneerFramework/World/Celestial/Adapters/Physics/
^1\.7/Assemblies/
```

**AI 助手 / 任何人**准备改这些文件时的协议：

1. 立即停止写操作
2. 输出标记 `【NOSTEPANYMORE】`
3. 列出：要改的路径、为什么、风险
4. 等用户明确授权
5. 授权后落一份**授权件**（`DesignNotes/后端变更授权-YYYYMMDD-<主题>.md`），写明授权人、原话、范围、判据、回退

示例：[后端变更授权-20261111-曲面姿态与自转.md](file:///d:/Steam/steamapps/common/RimWorld/Mods/PioneerFrameworkLite/Source/PioneerFramework/DesignNotes/后端变更授权-20261111-曲面姿态与自转.md)

### 14.2 确定性构建

Release 双构建必须同字节。任何以下操作都会破坏它，需要谨慎：

- 引入 `Dictionary` 迭代顺序依赖输出
- 使用 `DateTime.Now` / `Guid.NewGuid()` 参与载荷
- 改变编译顺序（例如纯文件移动——`交接-20261031` 记录过「纯移动**会**改字节」）
- 升级 SDK 或 Roslyn

### 14.3 交接驱动开发

每批完成后在 `DesignNotes/` 落交接件，结构固定：

1. 本批改了什么
2. 判据（闸门结果 / 关键读数 / 交付哈希）
3. 回退路径（`_backups/<时间戳>-<主题>-<哈希>/`）
4. 未做 / 下一步

**当前现场权威**：`交接-20261101-类重组现场.md`（类重组批 1–5d 完成，批 5e 待续）。它会取代更早的交接件；读历史交接件时先确认有没有被新件取代。

### 14.4 PR 流程

1. 从最新 `master` 开功能分支
2. 小步提交，一次一件事
3. 改 C# → `rebuild-dll.ps1` → 一并提交 DLL
4. 推送 → PR → CI 全绿 → review → 合并

提交信息风格（观察自历史）：`fix:` / `feat(scope):` / `docs:` / `merge:` + 中文说明。

---

## 15. 测试体系

### 15.1 三个工程

| 工程 | 运行时 | 依赖 Verse | 覆盖 |
|---|---|---|---|
| `PioneerFramework.Pure.Tests` | net10.0 | ❌ | 轨道力学、N 体、Lambert、交会、碰撞、制导、SOI、巨构数学、PFView 数学 |
| `PioneerFramework.Tests` | net10.0 | ✅ | 天体框架全生命周期、物理桥接、战斗、地图、渲染适配、内容 Def |
| `MechJebLibTest` | **net48** | ❌ | ODE 求解器、Primitives、Lambert、二体闭式解回归 |

`MechJebLibTest` 留在 net48 是**刻意例外**（csproj 注释有完整理由）：上游 6248 条期望按 net48/Mono 的浮点转字符串口径校准，net10 上会有 7 条 ulp/格式类红；而生产运行时（Waylith CLR）模仿 Mono 行为，net48 口径反而最忠实。`LangVersion=12`，并显式引 `Microsoft.NETFramework.ReferenceAssemblies`。

拆分原则：**能在无 Verse 环境验证的，一律放 Pure**。这样内核可以脱离游戏迭代。

### 15.2 游戏内验收

`Debug/DebugTabMenu_PFL.cs` 注册「PFL 测试」页签，提供一键场景：

| 验收器 | 覆盖 |
|---|---|
| `FrameworkSelfTest` | 轨道物理全链、编队/实体量纲核对 |
| `GravityFieldSelfTest` / `RefactorMechanismsSelfTest` | 引力场、重构后机制 |
| `BurnABAcceptanceRunner` / `BurnPhaseAcceptanceRunner` / `LiveBurnAcceptanceRunner` | 推进 / 燃烧 A-B / 燃烧相位 |
| `DualBodyLaunchAcceptanceRunner` / `ThreeStageLaunchAcceptanceRunner` / `MoonLaunchAcceptanceRunner` | 发射（双体 / 三级 / 月球） |
| `LandingAcceptanceRunner` / `SurfaceTouchdownAcceptanceRunner` | 着陆与接地 |
| `BoardingTestScenarios` | 跳帮 |
| `SwarmScaleAcceptanceRunner` | 导弹群规模 |
| `EwSystemAcceptanceRunner` / `SensorCognitionAcceptanceRunner` / `SensorProjectionAcceptanceRunner` | 电子战与传感器 |
| `AtmosphereAcceptanceRunner` | 大气 / 悬浮 / 档位 |
| `AdapterAcceptanceRunner` / `MultipartAcceptanceRunner` / `PlanEditorAcceptanceRunner` | 适配层 / 多 Part / 规划器 |
| `PerfAcceptanceRunner` | 「一键验收」与性能 |
| `ConstellationSelfTest` | 星座 API 全链 |
| `PlanetKindDebugScenario` | 天体框架 |

### 15.3 没有自动化的部分

以下只能人工验收，不要把绿色 CI 当全覆盖：

- 真实渲染 / GPU 成本
- 交战手感与平衡
- 读档体验
- PFView 3D 视图完整性
- 倾斜自转轴内容级反例（目前只有合成探针）

### 15.4 性能台架

`tools/CelestialBenchmark/`、`tools/MegastructureBenchmark/`、`tools/SpaceAiBenchmark/`。基准工程 `Source/PioneerFramework.Bench/`。

---

## 16. 扩展点（给 Mod 作者）

### 16.1 三份可编译样例 + 一份文档样例

| 样例 | 形式 | 演示 |
|---|---|---|
| `Examples/CelestialCompSample` | 可编译工程（7 个 .cs） | 自定义天体组件（Comp + 工厂 + codec） |
| `Examples/ArtificialSurfaceSample` | 可编译工程（5 个 .cs） | 人造地表 Geometry Backend |
| `Examples/MegastructureExtensionSample` | 可编译工程（3 个 .cs） | 巨构自定义 Capability |
| `Examples/ShipBuildingVisualSample` | 仅 README | 建筑自定义 3D 视觉（XML Def + 注册片段） |

`validate_celestial_examples.py` 在 CI 中校验的只是**XML 结构**（例题/字段），不编译这三个工程。

### 16.2 天体组件扩展步骤

1. 在 XML 里定义天体（支持固定值或范围采样）
2. 写 `CelestialBodyComp` 子类 + 组件工厂 + codec
3. 在首次世界初始化前 `CelestialCapabilityRegistry.Global.Register(...)`
4. Patch 挂接生成配置
5. 用 `Examples/CelestialCompSample` 的目录结构对照

### 16.3 其他对外接口

| 接口 | 位置 |
|---|---|
| 发射过场 | `LaunchTransitService.TryStart / TryFindByMapId / TryCancel` |
| 分级回收 | `IRocketStageHooks`（`StageSeparate` / `StageRecover` / `StageReturnHome`） |
| 载荷承载 | `IPayloadCarrier` / `IPayloadDeployer`（`PhysicsEngine/Api`） |
| 物理只读快照 | `SpacecraftSnapshot` / `PhysicsQueries` / `PhysicsCommands` |
| 建筑视觉 | `ShipBuildingVisualRegistry.Default.RegisterStateProvider` / `RegisterMeshProvider` |
| 本地地图 | `MapGeneration/API/README.md` |

---

## 17. 路线图与未来目标

> 以下条目来自 `DesignNotes/TODO.md`（基线为 2026-08 之后的滚动台账）与 2026-11 的交接/授权件。状态标记沿用原文：✅ 完成 / 🟡 部分 / 🔵 进行中 / ⬜ 待办。

### 17.1 里程碑

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M1 | 裁剪策略确认（单 Part + 战斗保留） | ✅ |
| M2 | 建仓 + 快照 + 源码裁剪 | ✅ |
| M3 | 编译通过 + 游戏内验证 | 大部分 ✅（M3-2 月球地表生成、M3-5 描边渲染 ⬜） |
| M4 | 首发玩法闭环（F1 变轨 / F2 对射 / F3 受击反馈） | F2/F3 ✅，F1 部分 |
| M5 | Defs 精简 + 发布打包 | 🔵 进行中 |

### 17.2 M4 剩余项（玩法闭环）

| 编号 | 任务 | 状态 |
|---|---|---|
| F1-1 | dV 补给路径（Map 内补给/推进剂罐） | ⬜ |
| F1-2 | 机动执行消耗 dV 链路验证（规划队列 + 缓释燃烧 + RemainingDeltaV） | ⬜ |
| F1-3 | 导弹/无人机携带 dV 并消耗 | ✅ 已实测 |
| F1-4 | 燃料 ↔ dV 定量换算（`ChemfuelToDeltaVMapper`） | ⬜ |
| F2-1~3 | Map 炮塔 → 3D 射弹 | ✅ |
| F3-1~2 | 3D 命中点 → Map 内爆炸 | ✅ |
| F3-3 | 无 Map part 命中反馈 | 🟡 部分 |

### 17.3 F4：DMS:NF 接入点（全部 ⬜）

为内容 Mod《DMS：新边疆》（CV-1/CV-3 火箭、11 款载荷、控制终端、敌对轨道事件）冻结机制层接口。⚠ 下表除 F4-1 / F4-2 外，**类型尚未在源码中存在**，是 TODO 里的规划名（`OrbitalEntityCatalog` / `ReentryTrajectory` / `HostileOrbitalRegistry` / `IOrbitalIncidentEvent` 全库搜索无命中）：

| 编号 | 接口 | 说明 |
|---|---|---|
| F4-1 | `IRocketStageHooks` | 分级钩子冻结（接口已在 `Gameplay/Launch`，需正式化） |
| F4-2 | `IPayloadCarrier` / `IPayloadDeployer` | 载荷承载与部署（`PhysicsEngine/Api` 已有契约） |
| F4-3 | `OrbitalEntityCatalog` | 轨道实体只读查询（枚举/按 Def/按派系/按类型） |
| F4-4 | `ReentryTrajectory.Calculate` | 弹道导弹/返回舱落点与时间 |
| F4-5 | `HostileOrbitalRegistry` + `IOrbitalIncidentEvent` | 敌对实体注册与事件钩子 |
| F4-6 | 分级/回收正式化 | 对接 CV-1/CV-3 型号配置 |
| F4-7 | 载荷部署正式化 | 对接 11 款载荷入轨 |

### 17.4 M5：发布准备

| 编号 | 任务 | 状态 |
|---|---|---|
| M5-1 | Defs 精简复核（无 full 版残留） | ⬜ |
| M5-2 | Toon Shader 资产管线 | ✅ 三个平台包已入库 |
| M5-3 | 发布包结构 | ⬜ |
| M5-4 | 存档前缀独立性确认 | ⬜ |
| M5-5 | DMS:NF 接入文档 9 类 | ⬜ |

### 17.5 近期进行中的技术方向

| 方向 | 现状 | 依据 |
|---|---|---|
| 曲面姿态与自转 | 太阳系四体迁 `existing.circular.v2`，月球真潮汐锁定；倾斜轴只有合成探针 | `后端变更授权-20261111` |
| 行星地图生成大修 | 2026-11-06 批次已合入；`PlanetLayerDefs` / `MapGeneration` Def 支撑 | 提交 `033d71b` / `bb283ba` |
| PFView 合并与性能 | 2026-11-13 完成合并适配并重建 DLL；已停用 PFView 变速 | 提交 `6508d2e` / `bee087b` |
| 类重组（批 5e） | 批 1–5d 完成；5e 的 §4.1–§4.3b 已落地并验收，§4.4 待收尾。注：2026-11-13 PFView 合并后 `Presentation → PhysicsEngine` 直接引用回升到 49 处，架构闸门重新变红（§6.2 / §18） | `交接-20261101-类重组现场.md` |
| 大气层 | L1 已收官，完整实现仍在待办 | `交接-20261008-大气层L1收官与控制台UI.md` |
| 跨星系扩展 | Lite 明确不做，归 full 版 | `Lite规划-20260808` |

### 17.6 明确不做（Lite 边界）

- 出地月系
- 多 Part 装配（Ships3D 目录已空）
- 8x 时间流速验证（留给 full）
- 存档 v1/v2/v3 自动迁移

---

## 18. 已知限制与技术债

| 项 | 说明 | 影响 |
|---|---|---|
| 架构闸门当前为红 | `check-architecture.ps1 -Fail` 实测 `baseline 0 / actual 14 文件 49 处`；`tools/architecture-baseline.json` 不存在（基线为空） | PFView 合并引入的直接引用；要么收敛到 `PhysicsEngine/Api`，要么 `-Update` 落基线 |
| 文档路径陈旧 | `BACKEND-LOCK.md` / `CONTRIBUTING.md` 仍写 `Base/**`、`Components/**`、`Combat/**`，实际树已重组到 `PhysicsEngine/` 与 `Gameplay/` | 新人不看脚本会找错地方 |
| `Docs/CelestialFrameworkAPI.md` 缺失 | Celestial README 声明「由本文件复制生成」，但 `Docs/` 只有 `LaunchTransitAPI.md` | 发布前需补（属 M5-5） |
| 空壳目录 | `Base/`、`Ships3D/`、`AutoTest/`、`UnityTest/` 无源码 | 易误导 |
| TODO 引用失效 | `DesignNotes/TODO.md` 仍列 `Window_CombatTest`，源码中已不存在（仅 TODO 提及） | 按 TODO 找文件会扑空 |
| 倾斜自转轴无内容级验证 | 只有合成探针，无真实 authored 天体 | author 倾斜轴天体前需游戏内冒烟 |
| `RotationOmega` 标量口径 | 无运行时兜底 | 倾斜轴天体不得用它当速率模长 |
| 范围爆炸后置 | 太空 Pocket Map 上 `GenExplosion` NRE | F3 只有落点 Bomb + 爆闪 |
| 描边包「复用通道」失效 | `PFViewOutlineAssetBundleProvider.ModPackageId` 硬编码 `NewFrontierTeam.PioneerFramework`，而 Lite 的 `About.xml` 是 `...FrameworkLite`。`TryGetLoadedModShaderSet` 因此永不命中，只能走文件回退（描边功能仍可用） | 轻微冗余；改包名 / 合并全量版时需同步该常量 |
| 结构质量写回依赖 `HullMassComponent` | `SpaceMapComp.ApplyStructureMassToOwner` 把 `StructureMassKg` 写进挂图 part 的 `HullMassComponent`；若该 part 没有此组件则直接 `continue`（不改质量） | 自定义 part 忘了加该组件 ⇒ 建筑质量静默不计入 |
| TODO 台账时间基线偏旧 | 主体为 2026-08 规划，部分条目状态未随 9–11 月进展更新 | 引用前对照最新交接件 |
| 测试数量口径分散 | 1855 / 1056 / 6271 / 28 来自不同日期 | 不要横向相加当同次运行 |

---

## 19. 上手路径

### 19.1 第一天

1. 装 SDK 10.0.402（注意 `rollForward: disable`）
2. `tools/update-game-dependencies.ps1` 同步依赖
3. 跑 §13.3 的四条命令，确认全绿
4. 跑 `tools/test-all.ps1`，记录当前用例数（这是你自己的基线）
5. 读 `交接-20261101-类重组现场.md` §0–§2（当前现场权威）

### 19.2 第一周

1. 读本文 §6（分层）与 §7（物理内核）
2. 进游戏打开 PFView，对照 §10 看渲染子系统
3. 用 Debug 页签跑 `FrameworkSelfTest`，看轨道闭环
4. 读 `World/Celestial/API/README.md`（天体框架最完整的对外文档）
5. 找一个 TODO 的 ⬜ 项，从非后端区（UI / 工具 / 文档 / Def）切入

### 19.3 常见坑

| 坑 | 后果 | 解法 |
|---|---|---|
| 用错 SDK | `global.json` 直接拒绝构建 | 装 10.0.402，或 `Use-ProjectDotnet.ps1` |
| 改 C# 不重建 DLL | CI 「已提交 DLL」红 | `rebuild-dll.ps1` |
| Release 带诊断写交付路径 | csproj 抛 `Error` | `-p:OutputPath=D:\tmp\...` |
| 壳外直接 `using` 内核类型 | 架构闸门红（ratchet；当前已有 49 处存量） | 走 `PhysicsEngine/Api`；新增一处也会红 |
| 纯文件移动 | 字节变化导致确定性红 | 预期行为，需重建 DLL |
| 动后端没授权件 | 后端锁红 | 先问用户，再落授权件 |
| 散装 `.shader` 直接扔进 Mod | `Shader.Find` 恒失败 | 必须打 AssetBundle |
| 期待旧存档兼容 | v4 拒绝 v1–v3 | 新建世界 |

---

## 20. 索引：要找什么去哪

| 需求 | 位置 |
|---|---|
| Mod 元数据 / 依赖 | `About/About.xml` |
| 版本加载规则 | `LoadFolders.xml` |
| SDK 钉版 | `global.json` |
| 游戏引用与哈希 | `Source/PioneerFramework/Libs/dependencies.json` |
| 物理内核入口 | `Source/PioneerFramework/PhysicsEngine/Api/` |
| 物理步宿主 | `PhysicsEngine/Step/SpaceGameComp.cs` + 8 个 partial |
| 轨道机动 | `PhysicsEngine/Trajectory/`（45 文件） |
| 天体框架文档 | `World/Celestial/API/README.md` |
| 地图生成文档 | `MapGeneration/API/README.md` |
| 发射 API | `Docs/LaunchTransitAPI.md` |
| PFView | `Presentation/PFView/`（111 文件） |
| 建筑视觉扩展 | `Presentation/PFView/ShipBuildingVisuals/` |
| 战斗 | `Gameplay/Combat/` |
| Pocket Map | `Gameplay/Maps/` |
| 巨构 | `World/Megastructures/` + `World/RingWorld/` |
| 扩展示例 | `Examples/`（3 个工程 + 1 个文档） |
| 设计文档 / 交接 / 授权 | `Source/PioneerFramework/DesignNotes/`（67 份，含归档子目录） |
| 当前待办 | `DesignNotes/TODO.md` |
| 构建脚本 | `tools/`（19 个顶层 PS + 4 个 shader 打包 PS + 13 个 PY） |
| CI | `.github/workflows/ci.yml` |
| AI 助手规则 | `CLAUDE.md` + `BACKEND-LOCK.md` |
| 后端锁实现 | `tools/check-backend-lock.ps1` |
| 1.6 冻结哈希 | `tools/check-frozen-1.6.ps1` |

---

## 21. 八条工程铁律

> 用户设定的协作准则，本项目所有改动都以此为准：

1. 以瞎猜接口为耻，以认真查询为荣
2. 以模糊执行为耻，以寻求确认为荣
3. 以臆想业务为耻，以人类确认为荣
4. 以创造接口为耻，以复用现有为荣
5. 以跳过验证为耻，以主动测试为荣
6. 以破坏架构为耻，以遵循规范为荣
7. 以假装理解为耻，以诚实无知为荣
8. 以盲目修改为耻，以谨慎重构为荣

---

*本文基于 master `6508d2e` 实测编写。若与 `DesignNotes` 中更晚的交接件冲突，以交接件为准；若与脚本行为冲突，以脚本为准。*
