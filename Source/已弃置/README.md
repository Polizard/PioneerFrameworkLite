# 已弃置（归档区）

> **本目录下的任何文件都不参与编译、不参与测试。** 这是**有意设计**，不是疏忽。

## 为什么是"归档"而不是"删除"

本项目有一条硬纪律：**绝不把测试场景改软、用例只增不减**（场景一经用于裁决就冻结）。
"已经不被任何生产代码调用的东西"与"它还有没有参考价值"是两件事——所以退役/残骸**一律搬到这里**，
而不是删掉：

- **留在盘上** ⇒ 随时可读、可对照、可整体取回（不必翻 git 历史）；
- **移出编译** ⇒ 不占用例数、不进门禁、不出现在"红灯"里（这正是"裁汰"要的效果）；
- **不写迁移代码** ⇒ 它已经是死的，不需要为它保留任何兼容层（见项目决策：开发中不考虑旧档兼容）。

## 机制（为什么放这里就自动不编译）

`Source/` 下**没有**任何 `.csproj` 用跨目录通配（各工程只递归收**自己目录**下的 `*.cs`，
> ⚠ **2026-09-26 更正：这句话是假的。** 两个测试工程都在用跨目录通配——
> `MechJebLibTest.csproj` 与 `PioneerFramework.Pure.Tests.csproj` 各自 `<Compile Include="..\PioneerFramework\ThirdParty\MechJebLib\**\*.cs" />`。
> 它只对 `Source/PioneerFramework/` 成立。⇒ **搬文件前要按这两个工程的通配实测一遍**，别只看这句话。
以及显式 `<Compile Include="..\PioneerFramework\Base\...">` 那批链接文件）。
⇒ **在 `Source/` 下直接建目录，就等于"留盘不编译"**，不需要改任何工程文件。

⚠ **两条边界**（别踩）：
1. **不要往这里搬"被显式链接进某工程的生产文件"**（例如 `Base/Orbital/*.cs` 那批）——搬走会让
   那个工程的 `<Compile Include>` 断链 ✗。搬生产文件要先连带改工程与调用点（属 S5 的手术类）。
2. **不要往这里搬"还有活调用者"的东西**——哪怕只被一处调用。判据是 `grep` **零生产调用者**。

## 恢复方法

直接把文件搬回原目录即可（原路径见下表）。若它当初是被显式 `<Compile Include>` 链接的，
要连那行一起恢复。

## 台账

| 移入日期 | 文件（原路径） | 为什么退役 | 取回代价 |
|---|---|---|---|
| 2026-10-15 | `Source/PioneerFramework/GroundAssembly/Patches/BlueprintMakeSolidThingPatch.cs` | **反射清理 A-15**：蓝图→框架的规格传递 ⇒ 改**蓝图子类** `PF_Blueprint_Rocket`（4 个火箭 def 的 `<building><blueprintClass>` 已指过去；判据与旧补丁同） | 搬回 `GroundAssembly/Patches/` 即可；⚠ 搬回 = 与子类**双重传递**（幂等，无功能害处） |
| 2026-10-15 | `Source/PioneerFramework/GroundAssembly/Patches/FrameCompleteConstructionPatch.cs` | **反射清理 A-15**：框架完工 → 通知发射台 ⇒ 改**火箭自己的 comp** `CompRocketCompletionNotify`（`PostSpawnSetup`；守卫三连与旧补丁同窗口），4 个火箭 def 已挂 | 搬回即可；⚠ 搬回 = 与 comp **双重通知**（`NotifyRocketCompleted` 幂等，但应二选一） |
| 2026-10-15 | `Source/PioneerFramework/GroundAssembly/Patches/CompLaunchableGizmoPatch.cs` | **反射清理 A-5**：压制我方火箭的原版发射 Gizmo ⇒ 改**自有 comp 子类**（`Launch/CompLaunchable_PF.cs`：`CompProperties_Launchable_PF`/`CompLaunchable_PF`），4 个火箭 def 的 `Class=` 已换；判据 `IsManagedRocket`（= 带 `CompRocketPocketMap`）未变 | 搬回 `GroundAssembly/Patches/` 即可；⚠ 搬回 = 与自有 comp **双重压制**（幂等，无功能害处） |
| 2026-10-15 | `Source/PioneerFramework/GroundAssembly/Patches/PlaceWorker_NeedsFuelingPortPatches.cs` | **反射清理 A-6**：我方火箭/返回舱的放置校验与幽灵绘制 ⇒ 改**自有 `PF_PlaceWorker_NeedsFuelingPort`**（判据同一套 `LaunchPadUtility`），火箭 def 的 `placeWorkers` 已换 | 搬回即可；⚠ 搬回 = 与自有 worker **双重判定**（结论相同但两处维护） |
| 2026-10-15 | `Source/PioneerFramework/GroundAssembly/Patches/PlaceWorker_FuelingPortPatches.cs` | **反射清理 A-6**：总装发射台的燃料口幽灵（画在台中心）⇒ 改**自有 `PF_PlaceWorker_FuelingPort`**，发射台 def 的 `placeWorkers` 已换 | 同上 |
| 2026-10-15 | `Source/PioneerFramework/GroundAssembly/Patches/PlaceWorker_NotUnderRoof_DrawGhost_Patch.cs` | **反射清理 A-6（死补丁）**：守卫要求"带 `PlaceWorker_NotUnderRoof` 的 def **且** 是 returned booster"，而唯一的 returned booster（`PF_Building_Booster_Returned_Basic`）**没有 placeWorkers** ⇒ 作用集为空（2026-10-15 逐条核对）⇒ 无替代物、直接退役 | 若将来给返回舱 def 加 `PlaceWorker_NotUnderRoof` 并要"台心放置引导"，再取回（或把那 20 行绘制并进 `PF_PlaceWorkers.cs`） |
| 2026-10-15 | `Source/PioneerFramework/Launch/SkyfallerRocketTangentPatch.cs` | **反射清理 A-7**：切线朝向从 `Skyfaller.GetDrawPositionAndRotation` **基类 Postfix + `TargetMethod()` 反射**改为 **def 子类覆写**（`PF_Skyfaller_Leaving : FlyShipLeaving` / `PF_Skyfaller_Incoming : DropPodIncoming`，见 `Launch/PF_Skyfaller_RocketTangent.cs`）+ 5 处 def 的 `<thingClass>`。覆盖等价已核：该 comp 只在 def 里声明、无运行期添加；vanilla def 不带它 ⇒ 与旧补丁的 `comp == null` 早退同集 | 搬回 `Launch/` 即可；⚠ 搬回 = 与 def 子类**双重应用切线**（extraRotation 被重写两次，观感会错）⇒ 必须二选一 |
| 2026-10-15 | `Source/PioneerFramework/Base/MapComp/SpaceMapComp.ReliefPatches.cs` | **反射清理 A-8**：浮雕置脏从 **5 处 Harmony 补丁**（`TerrainGrid.SetTerrain` / `RemoveTopLayer` · `RoofGrid.SetRoof` · `Thing.SpawnSetup` / `DeSpawn`）改为 **`MapComponent_ReliefDirtyWatcher`**（自订阅 `MapEvents` 四事件；事件源与被补方法同点触发，两条 `*Unsafe` 直写路径原本就不经过被补方法 ⇒ **覆盖等价**，2026-10-15 逐条核对） | 搬回 `Base/MapComp/` 即可（目录通配，无需改 csproj）；⚠ 搬回 = 与 watcher **双重置脏**（幂等、无功能害处，但应二选一） |
| 2026-10-15 | `Source/PioneerFramework/GroundAssembly/MapComponentInject_LaunchBlastFX.cs` | **反射清理 A-1**：`Map.FinalizeInit` 补丁（手工注入 `MapComponent_LaunchBlastFX` + `MapComponent_RocketBoosterReturnScheduler`）**整删**——两个组件本就是 `MapComponent`（非 `CustomMapComponent`）子类，`Map.FillComponents` 自动实例化（新档 + 每次读档；已存在则跳过、缺失则补齐 ⇒ 老存档也自动获得） | 搬回 `GroundAssembly/` 即可；⚠ 搬回 = 与自动实例化**重复注入**（旧补丁带 Any 守卫，无害但冗余） |
| 2026-10-15 | `Source/PioneerFramework/Base/Debug/Window_SpaceBattleView3D.cs` | **用户裁定淘汰**（调试用"太空战 3D 视角窗"，不再需要）。25 处调用点全清：`DebugShipSpawner` 22 · `BoardingTestScenarios.FinishDeploy` · `Window_LaunchTest`（连带删 `OpenViewerFor` 方法）· `SpaceGroupVisualManager` 面板按钮。四个场景入口的 `openView` 参数改名 `announce`（此后只管部署消息；`SpawnEquatorHoverTest` 的那个已无用途、直接删除）。副产物：1.7 计划 P2-d 的"调试窗 13 处 `Standard`"与 P2 台账 #4 的"BlendMode 字面量"**随之作废** | 搬回 `Base/Debug/` 即可（主工程靠目录通配，无需改 csproj）；⚠ 调用侧已删——要恢复功能请从 git 历史取回调用点 |
| 2026-09-28 | `Source/PioneerFramework/Base/Orbital/RendezvousGuidanceLaw.cs` | **已作废的自制交会律**（用户 2026-09-22："那个东西不能用了"）。批5.B 撤掉它的玩家入口后，**生产侧零调用者**（原先唯一的活消费者是旧菜单的价目） | 搬回 `Base/Orbital/` + 恢复 `MechJebLibTest` / `Pure.Tests` 各一行 `<Compile Include>`。⚠ 别在没有新判决时接回玩家路径 |
| 2026-09-28 | `Source/PioneerFramework/Base/Orbital/CwRendezvousCore.cs` | CW（Hill）终端"温柔解" = 旧律的终端；旧律作废后**零生产调用者**（排队中 §2 早已登记）。替代 = MJ 靶执行器 | 同上 |
| 2026-09-28 | `Source/PioneerFramework.Pure.Tests/RendezvousGuidanceCoreTests.cs` | T1–T16 **全部由旧律驱动** ⇒ 律归档后无法编译。替代 = `MechJebLibTest/LadderEndToEndTests.cs`（**活路径**重跑同四条几何；T0 台架自检一并搬为 `G0`） | 搬回 `PioneerFramework.Pure.Tests/`（靠目录通配，无需改 csproj） |
| 2026-09-28 | `Source/PioneerFramework/Base/Core/DVector3Extensions.cs` | 它本是"DVector3 ↔ 移植面 V3"的**补齐件**（索引访问 / 安全单位化 / 北天极 / xaxis / zaxis）。批5 U2/U3 把移植面类型并进 `DVector3` 后，这 5 个成员**由类型本身直接提供** ⇒ 它成了第二份权威（J2），且实测**零调用者**（连 `using static` 都没有） | 无（搬回原路径 + 恢复 `MechJebLibTest` / `PioneerFramework.Pure.Tests` 各一行 `<Compile Include>`） |
| 2026-09-23 | `Source/PioneerFramework.Pure.Tests/NBodyTests.cs` | 2 例**完全被** `GravityFieldTests` 覆盖（后者更全）⇒ 冗余，非独有覆盖 | 无（搬回即可） |
| 2026-09-23 | `Source/MechJebLibTest/A4_StageBreakdown.cs` | 探针（只 `Assert.True(true)` + 写临时文件），零判据、零引用 | 无 |
| 2026-09-23 | `Source/PioneerFramework.Pure.Tests/TerminalCommitComparison.cs` | 测**已退休的终端三档政策**；唯一断言 `Assert.True(a.Reached)` 是仪器自证，B/C 档只打印不判定；结论随连续律退休作废 | 无（但见下"排队中"第 1 条：它曾充当 T5 的仪器基准） |

## 排队中（**不是"忘了"，是"先有替代再搬"**）

1. ✅ **已完成（2026-09-28 批5.B）**：`RendezvousGuidanceCoreTests` **整文件**（T0–T16）已归档。
   路径 = 先补齐本规则的**前置**（把四条几何端到端台架建到活路径上），再搬：
   `MechJebLibTest/LadderEndToEndTests.cs`（一圈交会 / LEO→GEO / GTO 异轨 / 死区不憋死**四条全绿**，
   口径 300 m / 2 m/s；含从 T0 搬过去的台架自检 `G0`）+ `LadderPlanCostTests.cs`（规划墙钟）。
   ⚠ **一处登记**：`LadderEndToEndTests.G1`（同轨 15°）实测 **5.41 个周期**，而"**一圈交会**"这条
   用户硬要求**当前阶梯没达成** ⇒ 只断言"**有界**"（≤12 周期）并把读数打出来；阶梯自己的
   `PlanPhasingOrbit` 注释也把它登记在"**总修缮**"清单里（与"先照 MJ、创新放最后"一致）。
   ⇒ **不许拿旧律的 1.68 周期当基准**（旧律是废物：它快在相位、贵在别处——LEO→GEO 3.27× 螺旋价且超预算、
   末端极限环、反复买冲量）。
2. 🔶 **部分完成（2026-09-28 批5.B）**：`CwRendezvousCore.cs`（+ 引用它的用例）**已归档** ✅
   （用户裁定"旧律完全不要"⇒ 排队时留的"是否留作终端温柔解"这个设计选择已由该裁定解决）。
   **仍欠**：`Base/Orbital/BurnArcCompensation.cs` + `BurnArcCompensationTests.cs`（同样零生产调用者、
   同样被显式链接进测试工程 ⇒ 搬动要连带改 csproj）。
3. 🔶 **残骸部分完成（2026-09-28 批5.B）**：`LadderVsLawComparison` 已**瘦身成 `LadderPlanCostTests`**
   （零调用者的 `RunLaw`/`RunLadder`、自带的私有 `LadderPilot`、以及 `A4_PlanLevel_CostTable` 探针全删）。
   **仍欠**：`BurnArcTests.cs` 里的 `BurnLagHypothesisTests`（负结果已被 `BurnArcExecutorBench` 的三档覆盖）；
   两个**死旋钮** `TerminalUseExactLambert`（从未被任何用例设置）与 `MainOnly`（只经一个从未置真的静态字段）。
4. **同题第二份**：`MechJebLibTest/BurnRegimeTests.cs`（与 `ManeuverRegimeTests` 同一条 Θ 判据；
   `BurnRegime` 的唯一调用者是 `Window_PlanEditor` 的 UI 显示 ⇒ 搬它等于接受"UI 读数无人守"，待裁定）。
