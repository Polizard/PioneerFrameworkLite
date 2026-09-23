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
| 2026-09-23 | `Source/PioneerFramework.Pure.Tests/NBodyTests.cs` | 2 例**完全被** `GravityFieldTests` 覆盖（后者更全）⇒ 冗余，非独有覆盖 | 无（搬回即可） |
| 2026-09-23 | `Source/MechJebLibTest/A4_StageBreakdown.cs` | 探针（只 `Assert.True(true)` + 写临时文件），零判据、零引用 | 无 |
| 2026-09-23 | `Source/PioneerFramework.Pure.Tests/TerminalCommitComparison.cs` | 测**已退休的终端三档政策**；唯一断言 `Assert.True(a.Reached)` 是仪器自证，B/C 档只打印不判定；结论随连续律退休作废 | 无（但见下"排队中"第 1 条：它曾充当 T5 的仪器基准） |

## 排队中（**不是"忘了"，是"先有替代再搬"**）

1. **`RendezvousGuidanceCoreTests` 的 T1/T2/T7/T8/T9**（整文件内的**用例级**退役，需要手术而非搬文件）：
   测的是 `RendezvousGuidanceLaw.Compose`（游戏侧**零调用者**）与 `MaxClosablePhase`/`PhaseRateFromElements`
   等零调用者函数；其中 **T8/T9 连断言都没有**（纯 `WriteLine`）。
   ⚠ **必须先有新覆盖**：T11/T12/T13/T14/T16 覆盖的是**仍在玩家路径上的需求**（一圈交会 / LEO→GEO /
   GTO 异轨 / 死区不憋死）⇒ 它们的替代 = **阶梯端到端台架**（四条几何有界时间到 300 m / 2 m/s）。
   台架不到位就不搬——否则就是"删了测试却没替代"。
2. **两族零生产调用者的生产代码**：`Base/Orbital/CwRendezvousCore.cs`（+ 引用它的用例）、
   `Base/Orbital/BurnArcCompensation.cs` + `BurnArcCompensationTests.cs`。
   ⚠ 它们**被显式链接进测试工程** ⇒ 搬动要连带改 csproj 与调用点，且 `CwRendezvousCore` 是否留作
   将来"终端温柔解"属**设计选择**（待裁定）。
3. **文件内的残骸**（手术类，与上面同批做）：`LadderVsLawComparison` 里零调用者的私有 `RunLaw/RunLadder`
   与 `A4_PlanLevel_CostTable`（`Assert.True(true)`）；`BurnArcTests.cs` 里的 `BurnLagHypothesisTests`
   （负结果已被 `BurnArcExecutorBench` 的三档覆盖）；两个**死旋钮** `TerminalUseExactLambert`（从未被
   任何用例设置）与 `MainOnly`（只经一个从未置真的静态字段）。
4. **同题第二份**：`MechJebLibTest/BurnRegimeTests.cs`（与 `ManeuverRegimeTests` 同一条 Θ 判据；
   `BurnRegime` 的唯一调用者是 `Window_PlanEditor` 的 UI 显示 ⇒ 搬它等于接受"UI 读数无人守"，待裁定）。
