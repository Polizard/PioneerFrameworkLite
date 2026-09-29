# ThirdParty/MechJebLib —— 逐字移植的 MechJebLib 数值底座

**本目录下的 `.cs` 文件不是我们的代码。** 它们是从 MechJeb2 逐字复制的上游文件，
用来替换我们自研的、靠手工护栏兜着的数值工具（求根 / 极小化 / 传播 / 近点角换算 / 轨道几何与事件）。

## 来源

| 项 | 值 |
|---|---|
| 上游仓库 | `https://github.com/MuMech/MechJeb2`（本地参考克隆 `D:\tmp\MechJeb2`） |
| 分支 / 提交 | `dev` @ `c5a6d8f` |
| 上游路径 | `MechJebLib/` |
| 原作者 | Lamont Granquist、Sebastien Gaggini 及 MechJeb 贡献者 |
| 许可证 | 见下节 |

## 许可证

每个文件的头部保留了上游的 SPDX 标识，逐字未改：

```
SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
```

这是一个**宽松许可的析取**（"OR" = 使用方可任选其一）。本项目选择 **MIT**，
理由：MIT 是标准 SPDX 许可（`LicenseRef-PD-hp` 是上游自定义引用标识，含义需自行解释），
且与本项目的 GPL-3.0 兼容。

> ⚠ **注意不要把它与 MechJeb2 仓库根的 `LICENSE.md` 混淆**：那份声明的是 **GPL-3.0**，
> 约束的是 `MechJeb2/`（KSP mod 本体，含 `OrbitExtensions.cs` 等宿主层代码）。
> `MechJebLib/` 是独立许可的纯数值库——`MechJebLib/` 下全部 90 个文件都带上面这一行 SPDX，无例外。
>
> 因此："移植 MechJebLib 的数值代码"的合规义务是**保留文件头署名 + 本文档**；
> 而"移植 `MechJeb2/OrbitExtensions.cs`"（宿主层）才落在 GPL-3.0 那一侧。

## 移植范围（P0）

只取**自包含、无 alglib 依赖、无 Unity/KSP 依赖**的那部分。共 **35 个文件**（2026-09-22 实地清点核实），
按上游目录结构原样摆放：

```
Primitives/     V3  M3  Q3  Dual  DualV3  Scale  Vec  VecOps
Utils/          Statics  Check  Logger  MechJebLibException  ObjectPool
Rootfinding/    Bisection  BrentRoot  Newton
Minimization/   BrentMin
TwoBody/        Shepperd  Farnocchia
Functions/      Angles  Astro  SingleImpulseHyperbolicBurn
Lambert/        Izzo
Maneuvers/      Simple
FunctionImpls/  RealDeltaVToChangeApoapsisPrograde
ODE/            AbstractIVP  AbstractRungeKutta  BS3  DP5  DP8  DenseNode  DenseOutput  Event  Tsit5
Interpolants/   IInterpolant
```

> ⚠ **计数与清单以 `find . -name '*.cs'` 为准**：本清单曾漏掉 `Lambert/Izzo.cs` 并因此长期写着"34 个文件"，
> 2026-09-22 核对时更正。新增移植物后请重新清点，不要沿用旧数。

**`ODE/` 是 2026-09-18 由用户裁定补入的**（原文：*"赶紧补上，别手搓了"*）。原因见下节"手写 RK4"那条——
不加它，上游 `TwoBodyTests`（`Shepperd`/`Farnocchia` 的验收）搬不进来，P0 核心就没有上游验收。

**`Maneuvers/Simple.cs` + `FunctionImpls/RealDeltaVToChangeApoapsisPrograde.cs`** 是同日为 **A4（1 个 Operation 端到端）**
补入的：`OperationCircularize` / `OperationInclination` / `OperationEllipticize` 只依赖它们
（**这三个不含 alglib，可移植**），而 `OrbitalManeuverCalculator` 的多数函数依赖 `ChangeOrbitalElement`
——**那个是 alglib 依赖的，属 P1**。上游测试 `ManeuversTests/Simple.cs` 一并搬入。

> ⚠ **`ODE/` 的定位是"验收层的数值参照"，不是生产传播器。**
> 生产的传播权威是 **`NBodyIntegrator.ThirdOrderTaylorStep`**（我们的多体场三阶泰勒）。
> **不要把移植的积分器接进游戏的物理步或规划路径**——那会造成与泰勒并行的第二条传播链，
> 正是「一个概念两个权威」的事故形态。完整裁定见《MechJeb参考大修计划》§14.3 的裁定块。

**刻意不取**（各自的原因）：

| 不取 | 原因 |
|---|---|
| `Maneuvers/` **除 `Simple.cs` 外**全部、`PSG/` 全部、`Utils/AutoDiff.cs` | 依赖 vendored `alglib`（另有许可证）。属 P1 范围，且计划是用"网格扫描 + Brent"换掉 `minbleic` |
| `OrbitPool` / `PatchedConics` / `SOI_intercept` | 上游全库零调用者的死代码 |
| `Primitives/H1` `H3` `HBase` `Hn` | 只被 `FuelFlowSimulation` 与彼此引用，与本闭包无关 |
| `Utils/NullableAttributes.cs` | 是 net48/netstandard 的 BCL polyfill，在 net8 目标下会与 BCL 重复定义 |
| `FuelFlowSimulation/` `Control/` `HoverslamSimulation/` | 不在 P0 闭包内（宿主/着陆/上升相关） |
| `Functions/Interpolants.cs` | `ODE/DenseOutput.cs` 要的是 `Interpolants/IInterpolant.cs`（同名的另一个文件在 `Functions/` 命名空间下） |

> 闭包是**实测**出来的（沿 `using` 逐层追），不是估的。追的过程中曾漏掉
> `Functions/SingleImpulseHyperbolicBurn.cs`（被 `Astro.cs:838` 引用）——编译器抓到了。
> 若日后新增移植物，**照上面同样的方式把闭包重新跑一遍**，不要凭记忆。

## ⚠ 编译环境：只在 net48 下成立（且这是对的）

本目录的源码**按上游假设只 target net48**，本项目的**主工程也是 net48**，所以逐字可编译、0 警告。

**不要**把这些文件链接进 **net8** 工程（如 `PioneerFramework.Pure.Tests`）。原因是硬的：

`Functions/Angles.cs` 同时 `using static MechJebLib.Utils.Statics` 与 `using static System.Math`，
而 `Math.Asinh / Acosh / Atanh` 是 **.NET Core 2.1 才进 BCL** 的。
**上游 MechJebLib 正是靠 net48 没有这三个函数才写得通**；放到 net8 上会 CS0121 二义性。
这不是可以靠改配置绕开的东西——改了就叫偏离上游，那条"diff 为空"的不变式就没了。

**⇒ 移植面的测试宿主一律用 net48 工程**，共两个：

| 宿主 | 装什么 | 层次 |
|---|---|---|
| **`Source/MechJebLibTest/`** | **上游自己那套测试**（逐字移植）+ **我们自己的跨求解器互证台**（`MechJebLibPortTests.cs`）。net48、**零 RimWorld/Unity 依赖**（镜像上游 `MechJebLibTest → MechJebLib` 的形状）；程序集名与命名空间保持 `MechJebLibTest`。**共 5012 条用例** | **L1（白拿）+ L3（互证）** |

> 互证台需要**我们这一侧**的 `OrbitMath` 才能做"移植面 ↔ 我们"的互证，所以该工程额外用
> `<Compile Include>` 链接了 `DVector3` / `SpaceConstants` / `OrbitMath`（并定义 `PURE_MATH`）。
> （批5 U4 起 `DVector3Extensions` 已退役——它的成员并进了 `DVector3`，见 `Source/已弃置/README.md`。）
> **它仍然零 RimWorld/Unity 依赖**，只是不再是"纯上游镜像"——这是为了让 L1 与 L3 同处一个桶。

### L1：搬了哪些、为什么少了那些

从 `MechJebLibTest` 搬来 **40 个测试文件**（含 2 个支撑文件 `AssertionExtensions` / `ApproximateEqualException`），
覆盖 `RootfindingTests` · `FunctionsTests`（含 4 个 `KeplerianFromStateVectors*`）· **`TwoBodyTests`（`Shepperd`/`Farnocchia`）** ·
`Utils/StaticsTests/TrigonometryTests` · `StaticTests` · `Primitives/{V3,M3,Q3}Tests` 全部。

**没搬的，以及原因**：

| 没搬 | 原因 |
|---|---|
| `ODETests/` | 上游直接测 `ODE` 积分器本身。**我们已有 `TwoBodyTests` 用 `DP5` 做参照**，覆盖了我们要的用途；且 `ODETests` 依赖 `Functions`/`Astro` 全量。**若要补，属独立决定** |
| `LambertTests/` · `ManeuversTests/`（**除 `Simple.cs`**——它已搬） · `PSGTests/` · `ControlTests/` · `HoverslamTests.cs` | `Lambert` **已移 `Izzo`**（见上表），但其上游测试尚未搬；其余模块未移植（`Maneuvers` 与 `PSG` 依赖 alglib） |
| `Primitives/HTests.cs` · `VecTests` · `VecOpsTests` · `Utils/ObjectPoolTests.cs` | 类型虽已随 `ODE/` 移植，但它们的测试尚未搬——**属可补的**（当前由 `TwoBodyTests` 间接覆盖） |
| `Utils/AutoDiffTests.cs` | 依赖 alglib |
| `TestInitialization.cs` | **它的行为已合并进本项目自己的 `TestCulture.cs`**（`ObjectPoolBase.UseGlobal = false`）。原因：一个程序集只能有一个 `[assembly: TestFramework]`，而这个钩子已被 `TestCulture` 占用。**行为与上游一致** |
| `Properties/AssemblyInfo.cs` | 纯程序集元数据（标题/GUID/ComVisible），与测试无关 |

### ⚠ 一条上游测试的环境假设（我们加了 `TestCulture.cs`）

上游 `StaticTests.ToSITest` 断言 `double.PositiveInfinity.ToSI() == "Infinity"`，
而 `Statics.ToSI` 对非有限值走 `d.ToString()`（**无格式 ⇒ 吃当前文化**）。
在 zh-CN 机器上 `PositiveInfinitySymbol` 是 `"∞"` ⇒ **上游这条断言在非英文机器上必然红**。
这是上游测试的环境假设，**不是移植缺陷**（移植文件 diff 为空）。
处置 = `Source/MechJebLibTest/TestCulture.cs`（**本项目的基础设施文件，不是移植文件**）把测试进程文化钉为
`InvariantCulture`，与 `tools/test-all.ps1` 强制 `DOTNET_CLI_UI_LANGUAGE=en` 同源。

### ✅ 已结清：手写 RK4 那段债

L3 互证台里曾有一段**我们自己写的 RK4** 数值传播。它当时存在**只因为 `ODE/` 还没移植**，
而本项目方法论明确反对自造求解器（§11.3：§23–§29 七连败的根因就是"每个求解器都现推、靠手工护栏兜着"）。

**2026-09-18 已结清**：用户裁定补 `ODE/` ⇒ 手写 RK4 **连同它的自检用例一起删除**，
互证台的数值第三方改为**上游的 `DP5`**（并且把状态无量纲化到 μ=1、`|r|`=O(1)，
以对齐上游测试的尺度 regime——`Hmin = EPS` 是绝对量，不这么做求解器会工作在完全不同的尺度上）。
**`TwoBodyTests` 也已搬入并全绿**，`Shepperd`/`Farnocchia` 的上游验收不再缺席。

> **留个记号给下一个想手搓求解器的人**：这一段存在了几天，期间它"看起来在工作"。
> 它的替代品是上游被 ~5000 条用例验过的积分器。**没有理由再自己写一个。**


## ⚠ 移植面改动规则（**2026-09-26 用户裁定后重写；原「逐字不变式＝一个字不许动」已作废**）

> 用户原话：**「必要时可以修改我们移植过来的 MJ 部分，使得移植的 MJ 和我们的 PFL 之间尽可能少转换。」**
> ⇒ 旧护栏"**diff 必须为空**"**不再是禁令**。它的真实价值是"**上游 6179 条用例是我们最强的验证资产**"，
> 所以新规则按"**改一下要付多少验证成本**"分三档：

| 档 | 允许的形态 | 上游用例 | 判据 |
|---|---|---|---|
| **T-A ✅ 鼓励** | **语句级重写，公式与求值序都不变**：提循环不变量 / 提局部变量 / 同一个子表达式只算一次 / 加 `AggressiveInlining` | **仍全绿** ⇒ 资产完好 | 逐位差分 + **移植用例数不减** |
| **T-B ⚠ 逐条裁定** | **加可选参数、默认值 = 上游行为**（例：把"极轴"参数化，默认 `+z`） | 仍绿，但**宿主走的那条路不被上游覆盖** | 需**自建**差分：宿主路径 vs 上游路径（默认参数）逐位比 |
| **T-C ❌ 不做** | **改公式 / 改约定 / 改默认容差 / 改迭代上限** | **红** ⇒ 资产作废 | 必须自建整套替代验证。**不值得** |

**⚠ `Math.Pow(x,2.0) → x*x` 不算 T-A**：`pow` 是否特判 `y=2.0` 不是 CLR/CRT 保证，
而目标运行时是 Unity/Mono 的 libm ⇒ 属**待实测**。**可证的只有 `Statics.Powi(x,3)`**（平方-乘序与 C# 同结合序）。

**已改清单（每次改完在此登记：文件 · 为什么 · 上游用例是否仍全绿）**：

### 批5「类型统一」（2026-09-28）—— 全部属 **T-A**（断言与求值序都没变），上游 **6207/6207 仍全绿**

| 改动 | 涉及文件 | 为什么 | 上游用例 |
|---|---|---|---|
| **6 个 NED 船体常量改名**：`up/down/left/right/forward/back` → `bodyUp/bodyDown/bodyLeft/bodyRight/bodyForward/bodyBack`（**值一个都没动**） | 原 `Primitives/V3.cs` + 9 个上游测试文件（`Q3Tests/*`、`M3Tests/*`、`V3Tests/*`）**共 139 处** | 类型统一（U2/U3）后这 6 个 NED 常量与宿主 `DVector3` 的**世界系**同名常量撞名（同名、值正交）⇒ 必须有一边改名；改的是"名字在说谎"的那一边 | 仍全绿 |
| **`Primitives/V3.cs` 删除**，`V3` 改为指向宿主的别名 `global using V3 = PioneerFramework.DVector3;` | 新增 `_PFL/GlobalAlias.cs`（**本目录里唯一的非上游文件**，`_PFL` 前缀 + 文件头声明）；`MechJebLibTest` 的 `LangVersion` 8 → 12（`global using` 要 C# ≥ 10） | 用户 2026-09-26 裁定「`V3` 与 `DVector3` 统一」；上游测试源码因此**一个字不用改** | 仍全绿 |
| **4 个分歧成员采用上游语义** | 合并后的 `Base/Core/DVector3.cs`：`Equals`（NaN 判相等）· `ToString`（`[G17]` 不变文化 + `IFormattable`）· `normalized`（NaN→zero）· `Normalize()`（NaN→zero + 赋值 `this`） | 让上游赢 ⇒ 那批断言（`EqualityHashingTests` / `StringRepresentationTests`）继续有效 | 仍全绿 |
| **两个恒等转换函数删除**（`OrbitView` 那一对 + `OrbitAdapter` 的两个转发） | 宿主侧 39 处调用点 + 两个测试工程 27 处 | 类型统一后它们退化为"原样搬运三个分量" ⇒ 零行为变化，纯减法（§12.4） | 不涉及（宿主/自研文件） |

⇒ 净效果：**全库一个双精度三矢量类型**，且宿主 `DVector3` 从此被上游 6207 条用例覆盖（§12.4 的"白拿收益"）。
⚠ **文件计数口径**：`find . -name '*.cs'` 现在会多出 `_PFL/GlobalAlias.cs` 这一个**我们自己的**文件（它不属上游）。

**核验方式（改成"报出差异"而不是"必须为空"）**：

```bash
SRC=/path/to/MechJeb2/MechJebLib; DST=Source/PioneerFramework/ThirdParty/MechJebLib
for f in $(cd "$DST" && find . -name '*.cs' | sed 's|^\./||'); do diff -q "$SRC/$f" "$DST/$f" || echo "DIFF: $f"; done
```

### 上游外依赖：`JetBrains.Annotations` —— 不引入，靠 Unity 白拿

`Functions/Angles.cs` 有 6 处 `[UsedImplicitly]`（纯 ReSharper 提示、**零运行时语义**），
来自 `using JetBrains.Annotations;`。

- **主工程（net48 + Unity）**：`UsedImplicitlyAttribute` **已经存在于 RimWorld 自带的
  `UnityEngine.CoreModule`**（Unity 内置了这份 JetBrains 标注）⇒ `using` 直接解析到它，
  **不需要任何包**。实测：试图 `PackageReference` 引入 `JetBrains.Annotations` 会立刻
  **CS0433（类型同时存在于 Unity 与包中）**。
- 因此**不引入包**，文件也**不删属性** ⇒ 逐字不变式完整保留。
- ⚠ 这也是这些文件**不能进 net8 工程**的第二个原因：net8 侧没有 Unity，`using JetBrains.Annotations;`
  会因找不到命名空间而编译失败。

## 与我们的代码的边界

- 本目录**只有上游代码**。我们自己的适配层（`OrbitView` / `BodyView` / `ITargetView` / `PlanNode`）
  以及自己的算法**一律不放进这里**。
- 移植物的命名空间保持上游的 `MechJebLib.*`，类型名保持上游原名 ⇒
  "哪一份是上游、哪一份是我们的"在文件系统与命名空间两个层面都机械可判。
- **单一来源**：本目录引入的每个概念只允许一个权威。若我们已有的实现与之重合，
  按《MechJeb参考大修计划》§14.2 的 J2 **删除我们的那份**，不得"移植一份留一份"。

### ✅ 已消解：重复权威 `Base/Core/MJStatics.cs`（2026-09-22 记）

`PioneerFramework.MJStatics` 是上游 `MechJebLib.Utils.Statics` 的**手抄残缺版**，且已经漂移
（`Asinh/Atanh/Acosh` 是朴素一行式）。**2026-09-18 已删除该文件**，`Base/Orbital/Gooding.cs`
改为直接 `using static MechJebLib.Utils.Statics`（文件头留有这段沿革注释）。

原先"当前做不了"的理由是结构性的：`Gooding.cs` 被链进 **net8** 的 `Pure.Tests`，而移植的
`Statics` 无法在 net8 下编译。**该前提已消失** —— 同日用户裁定 `Pure.Tests` 由 net8.0 改为 **net48**，
移植面整份 `Compile Include`，net8/net48 分裂不复存在。
⇒ 本项**不再是待办**；若日后有人把 `Pure.Tests` 改回 net8，这条会立刻复活。
