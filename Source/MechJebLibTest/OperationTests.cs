using System.Collections.Generic;
using MechJebLib.Functions;
using MechJebLib.Primitives;
using MechJebLib.Utils;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **A4：一个 Operation 端到端**（《P-1适配层决策》§7 判据 A4）——也就是 **L2 oracle 的模板**。
    ///
    /// 判据的形态（照 P-1 原文）：`MakeNodesImpl(OrbitView, ut, ITargetView)` → `PlanNode`
    /// → **用返回的 Δv 传播 → 重新提取要素 → 断言等于请求值**。
    ///
    /// **为什么这个模板重要**：它对每个 Operation 都成立，且**与 MJ 的实现无关**——
    /// 所以它既能验证"我们移植对不对"，也能验证"上游那个算法本身对不对"。
    /// 18 个 Operation 各自套一份即可（P-1 §7 的"一个模板 × 18"）。
    ///
    /// 选 `OperationCircularize` 的理由：它只依赖 `Maneuvers.Simple`（**不含 alglib**）；
    /// `OperationApoapsis` 那一族走 `ChangeOrbitalElement`，属 P1。
    /// </summary>
    public class OperationTests
    {
        private static readonly double Mu = OrbitMath.GM;
        private const double R0 = 7_000_000.0;
        private static readonly V3 Pole = new V3(0, 0, 1); // 适配层参考系：+z
        private const double BodyR = 5_000_000.0;

        /// <summary>偏心 0.3 的椭圆（`r·v = 0` ⇒ `e = v²R/μ − 1`），近心点正好在 `ut = epoch` 处。</summary>
        private static OrbitView Eccentric(double epoch = 0.0)
        {
            double speed = System.Math.Sqrt(1.3 * Mu / R0);
            return OrbitView.FromStateVectors(new V3(R0, 0, 0), new V3(0, speed, 0), Mu, epoch, Pole, BodyR);
        }

        private static OrbitView CircularFrom(V3 r, V3 v, double epoch = 0.0)
        {
            V3 tangent = V3.Cross(Pole, r.normalized).normalized;
            return OrbitView.FromStateVectors(r, tangent * System.Math.Sqrt(Mu / r.magnitude), Mu, epoch, Pole, BodyR);
        }

        /// <summary>把一次节点的 Δv 施加到 `ut` 时刻的状态上，返回机动后的**瞬时**轨道（台架用）。</summary>
        private static OrbitView ApplyNode(OrbitView before, PlanNode node)
        {
            (V3 r, V3 v) = before.RightHandedStateVectorsAtUT(node.ut);
            return OrbitView.FromStateVectors(r, v + node.dV, Mu, node.ut, Pole, BodyR);
        }

        // ── oracle：请求 → Δv → 施加 → 重新提取要素 → 断言 ──────────────────────

        /// <summary>
        /// **L2 oracle 的完整形态**。两次分别请求"在近心点圆化"和"在远心点圆化"：
        /// 施加 Δv 后偏心率必须归零，且 `sma` 必须等于那一刻的半径（近心点 0.7R / 远心点 1.3R）——
        /// **这是闭式可算的**，不依赖任何数值参照。
        /// </summary>
        [Theory]
        [InlineData(TimeReference.PERIAPSIS)]
        [InlineData(TimeReference.APOAPSIS)]
        public void A4_CircularizeOperation_OracleCloses(TimeReference when)
        {
            var o = Eccentric();
            Assert.Equal(0.3, o.eccentricity, 9);
            Assert.Equal(0.0, o.trueAnomaly, 9); // 前置：ut = epoch 处正好在近心点

            var op = new OperationCircularize();
            op.TimeSelector._currentTimeRef = System.Array.IndexOf(
                new[] { TimeReference.APOAPSIS, TimeReference.PERIAPSIS, TimeReference.X_FROM_NOW, TimeReference.ALTITUDE }, when);
            Assert.Equal(when, op.TimeSelector.TimeReference); // 前置：选项表没写错

            System.Collections.Generic.List<PlanNode>? nodes = op.MakeNodes(o, 0.0, new FixedTargetView(o));
            Assert.NotNull(nodes);
            Assert.Single(nodes);
            // ⚠ 成功时 `ErrorMessage` 是**空串**不是 null：上游在 `MakeNodes` 开头就置 `""`（只在意外出错时写它）
            Assert.True(string.IsNullOrEmpty(op.GetErrorMessage()),
                $"不该有错误消息，实得『{op.GetErrorMessage()}』");

            PlanNode node = nodes![0];
            Assert.Equal(PlanOwner.Guidance, node.owner);

            // 节点时刻必须落在那一刻附近（近心点 = 0，远心点 = T/2）
            double expectedUt = when == TimeReference.PERIAPSIS ? 0.0 : o.period * 0.5;
            Assert.True(System.Math.Abs(node.ut - expectedUt) / o.period < 1e-6,
                $"{when} 节点时刻应 ≈ {expectedUt:F1}s，实得 {node.ut:F1}s");

            // ⭐ oracle 的核心：施加 Δv 后重提要素。
            // 期望值从轨道自己的 PeR/ApR 取（不要手算成 a 的分数——本轨的近心点是 R0 ⇒ a = R0/(1−e) = 1.4286·R0，
            // 远心点是 ApR = 1.857·R0，不是 1.3·R0。手算过一次就错过一次）。
            double expectedSma = when == TimeReference.PERIAPSIS ? o.PeR : o.ApR;
            var after = ApplyNode(o, node);
            Assert.True(after.eccentricity < 1e-12, $"圆化后偏心率应为 0，实得 {after.eccentricity:E3}");
            Assert.Equal(expectedSma, after.semiMajorAxis, 3);

            // 圆化是共面机动：倾角与轨道面法向不得改变
            Assert.Equal(o.inclination, after.inclination, 9);
        }

        /// <summary>
        /// "从现在起 N 秒"分支：把 `LeadTime` 设成 1/4 周期，节点必须落在那里；
        /// 且那一刻圆化后 `sma` = 那一刻的半径（不是近/远心点半径）。
        /// </summary>
        [Fact]
        public void A4_XFromNowBranch_UsesLeadTime()
        {
            var o = Eccentric();
            var op = new OperationCircularize();
            op.TimeSelector._currentTimeRef = System.Array.IndexOf(
                new[] { TimeReference.APOAPSIS, TimeReference.PERIAPSIS, TimeReference.X_FROM_NOW, TimeReference.ALTITUDE },
                TimeReference.X_FROM_NOW);
            op.TimeSelector.LeadTime.Val = o.period * 0.25;

            System.Collections.Generic.List<PlanNode>? nodes = op.MakeNodes(o, 0.0, new FixedTargetView(o));
            Assert.NotNull(nodes);

            PlanNode node = nodes![0];
            Assert.True(System.Math.Abs(node.ut - o.period * 0.25) < 1e-6, $"节点时刻应为 T/4，实得 {node.ut}");

            // 期望值**直接取自机动前的轨道**：那一刻的半径就是圆化后的 sma。
            // 比手算 Kepler 的 ν 再套 r = a(1-e²)/(1+e·cosν) 可靠 —— 手算那步我今天已经错过一次。
            double radiusAtBurn = o.RightHandedBCIPositionAtUT(node.ut).magnitude;
            var after = ApplyNode(o, node);
            Assert.True(after.eccentricity < 1e-12);
            Assert.Equal(radiusAtBurn, after.semiMajorAxis, 3);
        }

        /// <summary>
        /// 用**真实动力学**复核 oracle（不只信"重提要素"这一步）：
        /// 施加 Δv 之后传播半圈，半径必须**保持常数**（真圆轨的定义），
        /// 而机动前的椭圆会明显变化 —— 这条同时钉住了"Δv 的坐标系没搞反"。
        /// </summary>
        [Fact]
        public void A4_CircularizedOrbit_ActuallyStaysCircular_UnderPropagation()
        {
            var o = Eccentric();
            var op = new OperationCircularize();
            op.TimeSelector._currentTimeRef = 1; // PERIAPSIS

            PlanNode node = op.MakeNodes(o, 0.0, new FixedTargetView(o))![0];
            var after = ApplyNode(o, node);

            double r0 = after.RightHandedBCIPositionAtUT(node.ut).magnitude;
            for (int i = 1; i <= 8; i++)
            {
                double r = after.RightHandedBCIPositionAtUT(node.ut + after.period * i / 8.0).magnitude;
                Assert.True(System.Math.Abs(r - r0) / r0 < 1e-9,
                    $"圆化后走 {i}/8 圈半径应恒为 {r0:F1}m，实得 {r:F1}m");
            }

            // 对照：机动前的椭圆半径必然变化（否则上一条就是空断言）
            double rPreMin = double.MaxValue, rPreMax = 0.0;
            for (int i = 0; i <= 8; i++)
            {
                double r = o.RightHandedBCIPositionAtUT(o.period * i / 8.0).magnitude;
                rPreMin = System.Math.Min(rPreMin, r);
                rPreMax = System.Math.Max(rPreMax, r);
            }

            Assert.True((rPreMax - rPreMin) / rPreMax > 0.3, "前置失败：机动前的椭圆半径变化太小，这条对照没有意义");
        }

        // ── 框架契约：错误处理与目标接口 ──────────────────────────────────────────

        /// <summary>
        /// **框架的错误契约**（上游 `Operation.MakeNodes` 的行为，照搬）：
        /// 抛 `OperationException` ⇒ 不产生节点、消息进 `ErrorMessage`，且**不把异常泄漏给调用方**。
        /// 这里用"双曲轨道 + 请求远心点"触发——双曲没有远心点。
        /// </summary>
        [Fact]
        public void Operation_ErrorContract_OperationExceptionBecomesErrorMessageAndNoNodes()
        {
            double vEsc = System.Math.Sqrt(2.0 * Mu / R0);
            var hyp = OrbitView.FromStateVectors(new V3(R0, 0, 0), new V3(0, vEsc * 1.4, 0), Mu, 0.0, Pole, BodyR);
            Assert.True(hyp.eccentricity > 1.0);

            var op = new OperationCircularize();
            op.TimeSelector._currentTimeRef = 0; // APOAPSIS

            System.Collections.Generic.List<PlanNode>? nodes = op.MakeNodes(hyp, 0.0, new FixedTargetView(hyp));

            Assert.Null(nodes);
            Assert.False(string.IsNullOrEmpty(op.GetErrorMessage()));
        }

        /// <summary>
        /// **认知层的关键判据**（P-1 §4.2）：目标给不出轨道（接触过期 / 无解算中心天体）时，
        /// 规划器必须**把计划判为不可做**，而**不是回退到真值**。
        ///
        /// 这里用一个"永远给不出目标"的 `ITargetView` 驱动 `CLOSEST_APPROACH` 分支，
        /// 断言它走的是 `OperationException` 路径（而不是拿一个编出来的轨道硬算）。
        /// </summary>
        [Fact]
        public void Operation_StaleTarget_MakesPlanImpossible_NotTruthFallback()
        {
            var o = Eccentric();

            // `OperationCircularize` 的候选时间参考里没有 CLOSEST_APPROACH，所以单独构一个选择器来驱动那条分支。
            var selector = new TimeSelector(new[] { TimeReference.CLOSEST_APPROACH });
            selector._currentTimeRef = 0;

            Assert.Throws<OperationException>(() => selector.ComputeManeuverTime(o, 0.0, new StaleTargetView()));
        }

        /// <summary>一个永远给不出目标的 `ITargetView`——模拟"接触过期"。**台架用**。</summary>
        private sealed class StaleTargetView : ITargetView
        {
            public bool TryOrbit(double ut, out OrbitView orbit)
            {
                orbit = default;
                return false; // 调用方必须把它当成"计划不可做"
            }

            public double PositionSigma => 5000.0;
            public double DataAgeSeconds => 3600.0;
            public int IdentityGroupId => 7;
            public int IdentityDecoyId => -1;
            public double ApparentRadius => 0.0;
            public PlanConfidence Confidence => PlanConfidence.Unusable;
        }

        // ── P1：走 `ChangeOrbitalElement` 族的四个 Operation（E4 阻塞解除后） ──────

        /// <summary>
        /// 这四个共用同一个 oracle 形状：**设参数 → 出计划 → 施加 Δv → 重提要素 == 请求值**。
        /// 断言的是"要素真的被改到了"，而不是"Δv 长得像多少"。
        /// </summary>
        [Fact]
        public void P1_OperationApoapsis_MeetsRequestedApoapsis()
        {
            var o = Eccentric();
            var op = new OperationApoapsis();
            op.TimeSelector._currentTimeRef = 0; // APOAPSIS 时刻

            double targetRadius = R0 * 2.5;
            op.NewApA.Val = targetRadius - BodyR; // 参数是**高度**

            List<PlanNode>? nodes = op.MakeNodes(o, 0.0, new FixedTargetView(o));
            Assert.NotNull(nodes);
            var after = ApplyNode(o, nodes![0]);
            Assert.Equal(targetRadius, Astro.ApoapsisFromStateVectors(Mu, after.r, after.v), 0);
        }

        [Fact]
        public void P1_OperationPeriapsis_MeetsRequestedPeriapsis_AndGuardsNegativeHeight()
        {
            var o = Eccentric();
            var op = new OperationPeriapsis();
            op.TimeSelector._currentTimeRef = 0;

            double targetRadius = R0 * 0.8;
            op.NewPeA.Val = targetRadius - BodyR; // 高度 = 半径 − 天体半径（可为负：在地表以下）

            List<PlanNode>? nodes = op.MakeNodes(o, 0.0, new FixedTargetView(o));
            Assert.NotNull(nodes);
            var after = ApplyNode(o, nodes![0]);
            Assert.Equal(targetRadius, Astro.PeriapsisFromStateVectors(Mu, after.r, after.v), 0);

            // 负值守卫（上游只有这一个有）：高度低于 −天体半径 ⇒ 近心点在"负半径"处 ⇒ 不可做
            op.NewPeA.Val = -BodyR * 1.5;
            Assert.Null(op.MakeNodes(o, 0.0, new FixedTargetView(o)));
            Assert.False(string.IsNullOrEmpty(op.GetErrorMessage()));
        }

        [Fact]
        public void P1_OperationSemiMajor_MeetsRequestedSma_AndGuardsTooSmall()
        {
            var o = Eccentric();
            var op = new OperationSemiMajor();
            op.TimeSelector._currentTimeRef = 0;

            double target = Astro.SmaFromStateVectors(Mu, o.r, o.v) * 1.4;
            op.NewSma.Val = target;

            List<PlanNode>? nodes = op.MakeNodes(o, 0.0, new FixedTargetView(o));
            Assert.NotNull(nodes);
            var after = ApplyNode(o, nodes![0]);
            Assert.Equal(target, Astro.SmaFromStateVectors(Mu, after.r, after.v), 0);

            // 守卫：半长轴不能小于"烧点半径的一半"
            op.NewSma.Val = o.Radius(0.0) * 0.4;
            Assert.Null(op.MakeNodes(o, 0.0, new FixedTargetView(o)));
        }

        [Fact]
        public void P1_OperationEccentricity_MeetsRequestedEcc()
        {
            var o = Eccentric();
            var op = new OperationEccentricity();
            op.TimeSelector._currentTimeRef = 0;

            op.NewEcc.Val = 0.55;
            List<PlanNode>? nodes = op.MakeNodes(o, 0.0, new FixedTargetView(o));
            Assert.NotNull(nodes);
            var after = ApplyNode(o, nodes![0]);
            Assert.Equal(0.55, Astro.EccFromStateVectors(Mu, after.r, after.v), 7);
        }

        [Fact]
        public void P1_OperationEllipticize_MeetsRequestedPeAndAp()
        {
            var o = Eccentric();
            var op = new OperationEllipticize();
            op.TimeSelector._currentTimeRef = 1; // X_FROM_NOW（候选表是 APOAPSIS/X_FROM_NOW/ALTITUDE，没有 PERIAPSIS）
            op.TimeSelector.LeadTime.Val = 0;

            // ⚠ 烧点在 `R0` ⇒ 两条硬约束：**近心点必须 ≤ 烧点半径 ≤ 远心点**。
            //    （初稿我写 `wantPeR = 1.1 R0`，被 `OperationEllipticize` 的第一条守卫正确拒掉——
            //      那条守卫不是形式主义，它挡的是"不可能的几何"。）
            double wantPeR = R0 * 0.9;
            double wantApR = R0 * 2.2;
            op.NewPeA.Val = wantPeR - BodyR;
            op.NewApA.Val = wantApR - BodyR;

            List<PlanNode>? nodes = op.MakeNodes(o, 0.0, new FixedTargetView(o));
            Assert.NotNull(nodes);
            var after = ApplyNode(o, nodes![0]);
            Assert.Equal(wantPeR, Astro.PeriapsisFromStateVectors(Mu, after.r, after.v), 0);
            Assert.Equal(wantApR, Astro.ApoapsisFromStateVectors(Mu, after.r, after.v), 0);
        }

        /// <summary>
        /// ⭐ **`OperationInclination` 的两件事一起钉**：
        /// ① **度 → 弧度**（上游漏转；照搬会把 `30` 当成 `30 rad` ⇒ 垃圾）；
        /// ② **顺行基准换算**（本 mod 特有）：面向玩家的 `NewInc` 用**直觉口径**
        ///    （`0° = 赤道顺行 · 90° = 极轨 · 180° = 赤道逆行`），送进适配层的目标是 `180° − i`。
        ///
        /// 四个控制点都是**可以直接心算**的：
        /// | 输入 | 期望（适配层 `inclination`） | 几何含义 |
        /// |---|---|---|
        /// | 0°   | **180°** | 赤道**顺行**（`h ∥ −极轴`） |
        /// | 90°  | 90°      | 极轨 |
        /// | 30°  | **150°** | 顺行、倾斜 30° |
        /// | 180° | **0°**   | 赤道**逆行**（`h ∥ +极轴`） |
        ///
        /// ⚠ 若有人把 ② 的换算删掉（"跟上游对齐"），`0°` 那格会得到 `0°`＝**逆行** ⇒ 这条会红。
        /// </summary>
        [Theory]
        [InlineData(0.0, 180.0)]
        [InlineData(30.0, 150.0)]
        [InlineData(90.0, 90.0)]
        [InlineData(180.0, 0.0)]
        public void P1_OperationInclination_UsesIntuitiveDegrees_AndProgradeBasis(double inputDeg, double expectedAdapterDeg)
        {
            var o = Eccentric();
            var op = new OperationInclination();
            // 候选表是 EQ_* 四项 + X_FROM_NOW。测试轨道是**赤道**的（h 与赤道轴平行）
            // ⇒ EQ_* 的"赤道交点存在"判据为假、会抛 ⇒ 用 X_FROM_NOW（下标 4）。
            op.TimeSelector._currentTimeRef = 4;
            op.TimeSelector.LeadTime.Val = 0;
            op.NewInc.Val = inputDeg;

            List<PlanNode>? nodes = op.MakeNodes(o, 0.0, new FixedTargetView(o));
            Assert.NotNull(nodes);
            var after = ApplyNode(o, nodes![0]);

            double incDeg = Astro.IncFromStateVectors(after.r, after.v) * 180.0 / System.Math.PI;
            Assert.True(System.Math.Abs(incDeg - expectedAdapterDeg) < 1e-6,
                $"输入 {inputDeg}° 应得到适配层 {expectedAdapterDeg}°，实得 {incDeg:F6}°");

            // 顺行/逆行的**方向**也要对（不只是角度大小）：输入 < 90° ⇒ 顺行 ⇒ h 指向 −极轴
            double hDotPole = V3.Dot(after.OrbitNormal(), Pole);
            if (inputDeg < 90.0)
                Assert.True(hDotPole < 0.0, $"输入 {inputDeg}° 应为顺行（h ∥ −极轴），实得 h·极轴 = {hDotPole:F4}");
            else if (inputDeg > 90.0)
                Assert.True(hDotPole > 0.0, $"输入 {inputDeg}° 应为逆行（h ∥ +极轴），实得 h·极轴 = {hDotPole:F4}");
        }

        /// <summary>
        /// **对齐速度**：施加 Δv 后，两艘船在节点时刻的**相对速度必须归零**（判据就是它的定义）。
        /// </summary>
        [Fact]
        public void P1_OperationKillRelVel_ZerosRelativeVelocity()
        {
            var o = Eccentric();

            // 目标：另一个半径上的圆轨（同一个中心天体 ⇒ 同一个 mu）
            double rT = R0 * 1.6;
            var targetOrbit = OrbitView.FromStateVectors(new V3(rT, 0, 0),
                new V3(0, System.Math.Sqrt(Mu / rT), 0), Mu, 0.0, Pole, BodyR);
            var target = new FixedTargetView(targetOrbit, identityGroupId: 2);

            var op = new OperationKillRelVel();
            op.TimeSelector._currentTimeRef = 0; // CLOSEST_APPROACH

            List<PlanNode>? nodes = op.MakeNodes(o, 0.0, target);
            Assert.NotNull(nodes);
            PlanNode node = nodes![0];

            var after = ApplyNode(o, node);
            (V3 _, V3 vAfter) = after.RightHandedStateVectorsAtUT(node.ut);
            (V3 _, V3 vTarget) = targetOrbit.RightHandedStateVectorsAtUT(node.ut);

            Assert.True((vAfter - vTarget).magnitude / vTarget.magnitude < 1e-9,
                $"相对速度应归零：Δv_rel = {(vAfter - vTarget).magnitude:E3} m/s（目标 {vTarget.magnitude:F1} m/s）");
        }

        /// <summary>
        /// **共振轨道**：施加 Δv 后 `T_new / T_old` 必须**精确**等于请求的共振比 `f`。
        /// （闭式保证的：新 `sma = f^(2/3)·旧sma` ⇒ `T ∝ sma^(3/2)` ⇒ 比值恰为 `f`。）
        ///
        /// ⚠ **烧点必须是"被改那一端的对侧拱点"**（纯横推才保持另一个拱点）。
        /// 上游的候选时间参考里**默认第一个就是 `APOAPSIS`**，这不是巧合 —— 它是那个闭式的前提。
        /// （初稿我用 `X_FROM_NOW`+LeadTime=0 ⇒ 对这条轨道烧在**近心点** ⇒ 前提被破坏 ⇒ 实测比值 0.3152/1.0156。
        ///   **那是我的测试写错，不是上游的缺陷** —— 已撤回相应结论。）
        /// 我们的实现（直接约束 `sma`）**没有**这个前提，但测试仍按上游的用法烧在拱点上，以便与上游可比。
        /// </summary>
        [Theory]
        [InlineData(2, 3)]  // f = 2/3 < 1 ⇒ 缩短
        [InlineData(3, 2)]  // f = 3/2 > 1 ⇒ 拉长（**这一档上游闭式会漂**，见 ChangeOrbitalElementTests 的证据测试）
        [InlineData(2, 1)]  // f = 2
        public void P1_OperationResonantOrbit_AchievesExactPeriodRatio(int num, int den)
        {
            var o = Eccentric();
            double periodBefore = o.period;

            var op = new OperationResonantOrbit();
            op.TimeSelector._currentTimeRef = 0; // APOAPSIS（上游默认；见上面关于"前提"的说明）
            op.ResonanceNumerator.Val = num;
            op.ResonanceDenominator.Val = den;

            List<PlanNode>? nodes = op.MakeNodes(o, 0.0, new FixedTargetView(o));
            Assert.NotNull(nodes);

            var after = ApplyNode(o, nodes![0]);
            double f = (double)num / den;
            double ratio = after.period / periodBefore;

            Assert.True(System.Math.Abs(ratio - f) / f < 1e-8,
                $"T_new/T_old 应为 {f}，实得 {ratio:R}（T: {periodBefore:F1}s → {after.period:F1}s）");
        }

        /// <summary>
        /// ⭐ **改进点的钉住**：`f = 1/2` 在**远心点**烧是**做不到**的
        /// （要求 `2·sma_new = 1.8R₀ &lt; |r| = 1.857R₀`）。
        /// · **上游**在这种情况下 `x &lt; 0` ⇒ `return Vector3.zero` ⇒ **静默零脉冲**（点了一下什么都没发生）；
        /// · **我们**（直接约束 `sma`）⇒ `ChangeSMA` 的约束不满足 ⇒ **抛异常** ⇒ 走 `Operation` 的错误契约 ⇒
        ///   用户拿到一句"计划不可做"，而不是一个不动的船。
        ///
        /// 而同一个 `f=1/2` 改在**近心点**烧就可行（`|r| = R₀ &lt; 1.8R₀`）⇒ **我们能做**（周期比精确 0.5）。
        /// 上游那一档靠"改近心点 + 前提成立"也恰好能做，但它的**失败方式**依然是静默的。
        /// </summary>
        [Fact]
        public void P1_OperationResonantOrbit_InfeasibleBurnPoint_IsReportedNotSilentlyZeroed()
        {
            var o = Eccentric();

            // 远心点：不可行 ⇒ 必须报错（上游会静默给零脉冲）
            var opApo = new OperationResonantOrbit();
            opApo.TimeSelector._currentTimeRef = 0; // APOAPSIS
            opApo.ResonanceNumerator.Val = 1;
            opApo.ResonanceDenominator.Val = 2;
            Assert.Null(opApo.MakeNodes(o, 0.0, new FixedTargetView(o)));
            Assert.False(string.IsNullOrEmpty(opApo.GetErrorMessage()), "必须给出错误消息，而不是静默返回零脉冲");

            // 近心点：可行 ⇒ 精确命中 0.5
            var opPe = new OperationResonantOrbit();
            opPe.TimeSelector._currentTimeRef = 1; // PERIAPSIS
            opPe.ResonanceNumerator.Val = 1;
            opPe.ResonanceDenominator.Val = 2;
            List<PlanNode>? nodes = opPe.MakeNodes(o, 0.0, new FixedTargetView(o));
            Assert.NotNull(nodes);
            var after = ApplyNode(o, nodes![0]);
            Assert.True(System.Math.Abs(after.period / o.period - 0.5) < 1e-8,
                $"实得比值 {after.period / o.period:R}");
        }

        /// <summary>分母为 0 的共振比 ⇒ 计划不可做（而不是算出一个 NaN 节点）。</summary>
        [Fact]
        public void P1_OperationResonantOrbit_RejectsZeroDenominator()
        {
            var o = Eccentric();
            var op = new OperationResonantOrbit();
            op.TimeSelector._currentTimeRef = 2;
            op.TimeSelector.LeadTime.Val = 0;
            op.ResonanceDenominator.Val = 0;

            Assert.Null(op.MakeNodes(o, 0.0, new FixedTargetView(o)));
            Assert.False(string.IsNullOrEmpty(op.GetErrorMessage()));
        }

        // ── 参数与发现机制 ──────────────────────────────────────────────────────

        /// <summary>
        /// 参数对象（`EditableDoubleMult`）的算术与解析必须在**任意机器区域设置下**确定 ——
        /// 上游用环境文化解析，同一段文本在 zh-CN 与 en-US 上可能不同（小数点/千分位）。
        /// 我们固定 `InvariantCulture`。
        /// </summary>
        [Fact]
        public void EditableDoubleMult_ParsesCultureInvariantly_AndSupportsArithmetic()
        {
            var e = new EditableDoubleMult(200000, 1000);

            Assert.Equal(200000.0, (double)e, 6);   // implicit operator double
            Assert.Equal(200.0, e.Val / 1000, 9);   // 乘数口径：显示 200、存 200000
            Assert.Equal("200", e.Text);            // 显示值 = Val / multiplier

            e.Val = 350000;
            Assert.Equal("350", e.Text);

            e.Text = "425";
            Assert.True(e.Parsed);
            Assert.Equal(425000.0, e.Val, 6);

            // 直加：MakeNodesImpl 里写的就是 `NewApA + o.referenceBody.Radius`
            Assert.Equal(425000.0 + BodyR, e + BodyR, 6);

            e.Text = "not a number";
            Assert.False(e.Parsed);
            Assert.Equal(425000.0, e.Val, 6); // 解析失败时存值不变（上游语义）
        }

        /// <summary>
        /// **反射发现机制**（上游机制，照搬）：`OperationCircularize` 必须能被自动发现
        /// ——它 public、非抽象、有无参构造。这条是"加一个操作不用改注册表"的守卫。
        /// </summary>
        [Fact]
        public void Operation_IsDiscoveredByReflection()
        {
            Operation[] all = Operation.GetAvailableOperations();
            Assert.Contains(all, op => op is OperationCircularize);
            Assert.All(all, op => Assert.False(string.IsNullOrEmpty(op.GetName())));
        }
    }
}
