# MechJebLibTest —— 上游测试套 + 我们的互证台

**本目录下有 39 个文件不是我们的代码**，是从 MechJeb2 的 `MechJebLibTest/` 逐字复制的。
它们验收的是 `../PioneerFramework/ThirdParty/MechJebLib/` 下的移植面本身。

**另有 1 个文件是本项目的**：`MechJebLibPortTests.cs`（跨求解器互证台，L3 层）
—— 它测的是"移植物 ↔ 我们的 `OrbitMath` ↔ 上游 `DP5`"三方一致，性质上是**我们**的测试，
所以它不带上游 SPDX 头、也不受逐字不变式约束。

来源、许可证、逐字不变式、搬了哪些/为什么少那些、以及 `TestCulture.cs` 这个例外，**全部记在**
[`../PioneerFramework/ThirdParty/MechJebLib/README.md`](../PioneerFramework/ThirdParty/MechJebLib/README.md)。

三条要点：

1. **net48，零 RimWorld/Unity 依赖** —— 这不是省事，是必需：移植物只能在 net48 编译。
2. **移植面改写规则**（2026-09-26 用户裁定后重写，「逐字不变式＝一个字不许动」已作废）：
   见 `../PioneerFramework/ThirdParty/MechJebLib/README.md` 的 **T-A / T-B / T-C 三档 + 已改清单**。
   批5 U1'（2026-09-28）按 T-A 在 **9 个测试文件**里做过一次**纯改名**
   （6 个 NED 常量加 `body` 前缀；**值未动、断言未动**），原因见该 README 的已改清单。
3. 本工程**直接编译**移植面源码（不 `ProjectReference` 主工程），并额外链接我们四个纯算术源文件
   （`DVector3` / `SpaceConstants` / `OrbitMath`）供互证台使用 ⇒ 主工程零改动。
   （`DVector3Extensions` 批5 U4 已退役，成员并入 `DVector3`。）
   同一份源码被多个工程编译，**权威仍只有一个**（源文件唯一）。

**规模**：上游 38 个测试文件 + 2 个支撑文件 + 我们的互证台 = **5012 条用例**。
