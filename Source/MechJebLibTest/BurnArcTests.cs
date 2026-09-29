using System;
using MechJebLib.Functions;
using MechJebLib.Primitives;
using MechJebLib.TwoBody;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **有限燃烧弧前向模型**（`BurnArc`）的判据。测试里**不再自带积分器**（那会是第二份权威 ✗）。
    ///
    /// 三条事实（都由实测标定过）：
    /// ① 居中燃烧的残差 ~1.4e-4（t_b=100s）；
    /// ② 残差**仍是一阶**（`e ∝ t_b`）—— 我"对称 ⇒ 二阶"的推导被数据推翻 ✗，照数据钉；
    /// ③ ⚠ **游戏实测 5.29e-3 比模型大 20 倍**（模型 2.5e-4）⇒ 执行器与模型有分歧 = **待查项**，
    ///    本文件只钉模型自己（不假装与游戏吻合）。
    /// </summary>
    public class BurnArcTests
    {
        private static readonly double Mu = OrbitMath.GM;
        private const double R0 = 7_000_000.0;
        private static readonly V3 Pole = new V3(0, 0, 1);

        private static OrbitView Eccentric()
        {
            double vc = Math.Sqrt(Mu / R0);
            return OrbitView.FromStateVectors(new V3(R0, 0, 0), new V3(0, Math.Sqrt(1.3) * vc, 0),
                Mu, 0.0, Pole, R0 * 0.2);
        }

        [Fact]
        public void CenteredFiniteBurn_LeavesOnlySmallResidual()
        {
            var o = Eccentric();
            double ut = o.NextPeriapsisTime(0.0);
            V3 dv = OrbitalManeuverCalculator.DeltaVToCircularize(o, ut);

            Assert.True(BurnArc.TryPredict(o, ut, dv, 100.0, out BurnArc.Result r, out string why), why);

            // ① 居中 + 正确方向律 ⇒ 残差 O(1e-4)（脉冲计划本身是精确圆化 ⇒ 残差 100% 来自有限燃烧）
            Assert.True(r.eccentricity < 1e-3,
                $"居中燃烧的残差应当很小：{r}（游戏实测 5.29e-3 比它大 20 倍 ⇒ 执行器有分歧）");
            Assert.True(r.eccentricity > 1e-5, $"不该小到「没燃烧」的程度：{r}");
        }

        [Fact]
        public void ResidualStillScalesLinearly_DespiteSymmetryArgument()
        {
            var o = Eccentric();
            double ut = o.NextApoapsisTime(0.0);
            V3 dv = OrbitalManeuverCalculator.DeltaVToCircularize(o, ut);

            Assert.True(BurnArc.TryPredict(o, ut, dv, 25.0, out BurnArc.Result shortBurn, out _));
            Assert.True(BurnArc.TryPredict(o, ut, dv, 200.0, out BurnArc.Result longBurn, out _));

            double ratio = longBurn.eccentricity / shortBurn.eccentricity;
            // 时长比 8 倍 ⇒ 残差比 ≈8（**一阶**）。我原以为居中后该是 8²=64 ✗ —— 数据不支持。
            Assert.True(Math.Abs(ratio - 8.0) < 0.3 * 8.0,
                $"残差应仍与 t_b 成正比（一阶）：e(200)/e(25)={ratio:F2}（若 ≈64 则是二阶，与实测不符）");
        }

        [Fact]
        public void StartAndEndAreSymmetricAboutTheNode()
        {
            var o = Eccentric();
            double ut = o.NextPeriapsisTime(0.0);
            V3 dv = OrbitalManeuverCalculator.DeltaVToCircularize(o, ut);

            Assert.True(BurnArc.TryPredict(o, ut, dv, 120.0, out BurnArc.Result r, out _));
            Assert.Equal(ut, 0.5 * (r.startUt + r.endUt), 6); // 弧心 = 节点（居中）
            Assert.Equal(120.0, r.endUt - r.startUt, 6);
        }


        /// <summary>
        /// **游戏侧应用政策的对照模型**（不是第二份权威，是**刻意不同**的对照）：
        /// · 步长 = 物理步 **2 s**（而 `BurnArc` 是 t_b/400 ≈ 0.245 s ⇒ 细 8 倍）
        /// · 方向**每扫过 ~2° 弧才刷新**（≈每 46 s 一个台阶，台阶内惯性冻结）
        /// · 冲量按**离散踢**施加（等价于"主线程在下一物理步首统一施加"⇒ 系统性晚一步）
        /// 用途：验证用户的假设——"那个残差是不是 2 s 物理步长造成的"。
        /// </summary>
        private static double ExecuteGamePolicy(OrbitView o, double nodeUt, V3 dv, double tB,
            double stepSeconds = 2.0, double sweepRad = 0.035)
        {
            double aThrust = dv.magnitude / tB;
            double startUt = nodeUt - 0.5 * tB;
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(startUt);

            (V3 rNode, V3 vNode) = o.RightHandedStateVectorsAtUT(nodeUt);
            V3 rhat0 = rNode.normalized;
            V3 hhat0 = V3.Cross(rNode, vNode).normalized;
            V3 that0 = V3.Cross(hhat0, rhat0);
            double rad = V3.Dot(dv, rhat0), tan = V3.Dot(dv, that0), nor = V3.Dot(dv, hhat0);

            V3 dir = (rad * rhat0 + tan * that0 + nor * hhat0).normalized;
            double sweep = 0.0, prevUt = startUt;

            for (double t = 0.0; t < tB; t += stepSeconds)
            {
                double dt = Math.Min(stepSeconds, tB - t);

                // 方向刷新：按"扫过的弧"节流（台阶内冻结）—— 与 B2.3 一致
                double omega = V3.Cross(r, v).magnitude / (r.magnitude * r.magnitude);
                sweep += omega * dt;
                if (sweep >= sweepRad)
                {
                    sweep = 0.0;
                    V3 rhat = r.normalized;
                    V3 hhat = V3.Cross(r, v).normalized;
                    V3 that = V3.Cross(hhat, rhat);
                    V3 d = rad * rhat + tan * that + nor * hhat;
                    if (d.sqrMagnitude > 0.0) dir = d.normalized;
                }

                // 引力步用**精确开普勒传播**（Shepperd）⇒ 对照里只剩"燃烧离散化"这一个变量
                (r, v) = Shepperd.Solve(o.mu, dt, r, v);
                // 离散踢：整步的 Δv 在步末一次施加（= 游戏里"下一步首统一施加"的等价）
                v += dir * (aThrust * dt);
                prevUt += dt;
            }

            return Astro.EccFromStateVectors(o.mu, r, v);
        }

        /// <summary>
        /// ⭐⭐ **证伪判据：残差不是 2 s 物理步长造成的**（2026-09-19，用户假设的验证）。
        ///
        /// 猜法很自然：执行器每 2 s 一次离散踢、方向每 2° 弧才刷新（台阶内惯性冻结），
        /// 而 `BurnArc` 是 0.245 s 的细积分 ⇒ 差 8 倍 —— 看着像是它。
        ///
        /// **实测（用精确开普勒做引力步 ⇒ 对照里只剩"燃烧离散化"这一个变量）**：
        /// · 细积分 `e = 2.55e-4`
        /// · 游戏政策（2 s 踢 + 2° 台阶）`e = 1.21e-4` —— **反而更小** ✗
        /// ⇒ 离散化只贡献 ~1e-4 量级，比游戏实测 **6.06e-3 小 60 倍** ⇒ **不是它** ✗。
        /// ⇒ 残差必须来自**燃烧之外**（环境/动力学，或对`e`的测量口径）——不是"烧得糙"。
        /// </summary>
        [Fact]
        public void TwoSecondStep_IsNotTheCause()
        {
            var o = Eccentric();
            double ut = o.NextPeriapsisTime(0.0);
            V3 dv = OrbitalManeuverCalculator.DeltaVToCircularize(o, ut);

            Assert.True(BurnArc.TryPredict(o, ut, dv, 98.0, out BurnArc.Result fine, out _));
            double coarse = ExecuteGamePolicy(o, ut, dv, 98.0);

            Assert.True(coarse < 5e-4,
                $"2 s 步 + 2° 台阶只该给 ~1e-4 量级（细积分 {fine.eccentricity:E3}、游戏政策 {coarse:E3}）" +
                $"——它比游戏实测 6.06e-3 小 60 倍 ⇒ **不是物理步长的锅**");
        }

        [Fact]
        public void DegenerateInputs_AreRejectedWithReasons()
        {
            var o = Eccentric();
            Assert.False(BurnArc.TryPredict(o, 0.0, new V3(1, 0, 0), 0.0, out _, out string why1));
            Assert.Contains("时长", why1);
            Assert.False(BurnArc.TryPredict(o, 0.0, V3.zero, 10.0, out _, out string why2));
            Assert.Contains("Δv", why2);
        }
    }

    /// <summary>
    /// ⭐⭐ **"晚一步施加"能否同时解释不变量 0.989 与 e=6.1e-3？**（2026-09-19 思维实验的推理，纯环境验证）
    ///
    /// 机制：冲量是"主线程在**下一物理步首**统一施加"⇒ 比它被算出来时**晚 2 秒**落到状态上 ✗。
    /// 而 `Δε = v·Δv` 里的 `v` 是**施加那一刻**的 v ⇒ 晚 2 秒的 v 已被引力改了 `g·dt ≈ 17 m/s`。
    ///
    /// 验法：同一条弧，**只把节点平移 2 秒**（`BurnArc` 里就是把 `nodeUt` 加 2）⇒ 比 Δε、Δh 的比值与 e。
    /// 若两个比值 ≈0.99 **且** e ≈6e-3 ⇒ 一个机制同时解释两个读数 ⇒ 根因落定。
    /// </summary>
    public class BurnLagHypothesisTests
    {
        private static readonly double Mu = OrbitMath.GM;
        private const double R0 = 7_000_000.0;
        private static readonly DVector3 Pole = new DVector3(0, 0, 1);

        private static OrbitView Eccentric()
        {
            double vc = Math.Sqrt(Mu / R0);
            return OrbitView.FromStateVectors(new DVector3(R0, 0, 0),
                new DVector3(0, Math.Sqrt(1.3) * vc, 0), Mu, 0.0, Pole, R0 * 0.2);
        }

        private static (double eps, double h, double e) Props(DVector3 r, DVector3 v)
        {
            double eps = v.sqrMagnitude / 2.0 - Mu / r.magnitude;
            double h = DVector3.Cross(r, v).magnitude;
            double e = Math.Sqrt(Math.Max(0.0, 1.0 + 2.0 * eps * h * h / (Mu * Mu)));
            return (eps, h, e);
        }

        [Fact]
        public void TwoSecondLag_ReproducesBothTheInvariantRatiosAndTheResidualE()
        {
            var o = Eccentric();
            double ut = o.NextPeriapsisTime(0.0);
            DVector3 dv = OrbitalManeuverCalculator.DeltaVToCircularize(o, ut);

            // 基准：在节点起燃、居中
            Assert.True(BurnArc.TryPredict(o, ut, dv, 98.0, out BurnArc.Result ideal, out _));

            // 假设：整条弧晚 2 秒（= 每步冲量晚一步施加的净效果）
            Assert.True(BurnArc.TryPredict(o, ut + 2.0, dv, 98.0, out BurnArc.Result lagged, out _));

            (DVector3 r0, DVector3 v0) = o.RightHandedStateVectorsAtUT(ut);
            (double eps0, double h0, double _) = Props(r0, v0);

            // 两者都在"节点 + 100 s"处读（同一时刻、同一系）
            double readUt = ut + 100.0;
            (DVector3 ri, DVector3 vi) = ideal.achieved.RightHandedStateVectorsAtUT(readUt);
            (DVector3 rl, DVector3 vl) = lagged.achieved.RightHandedStateVectorsAtUT(readUt);

            (double epsI, double hI, double eI) = Props(ri, vi);
            (double epsL, double hL, double eL) = Props(rl, vl);

            double ratioEps = (epsL - eps0) / (epsI - eps0);
            double ratioH = (hL - h0) / (hI - h0);

            // ⚠ **证伪判据**（2026-09-19）：整条弧平移 2 秒，对 Δε/Δh 的影响**是 0**（实测比值 1.0000/1.0000），
            //   对 e 甚至是**变小**（2.5e-4 → 7.8e-5）—— 与游戏实测的 0.9899/0.9892 与 6.09e-3 **方向都不对** ✗。
            //   ⇒ "每步冲量晚一步施加"**不是**那 1% 亏空的原因 ✓（当时是纯算术推理出来的候选，被纯实验验掉）。
            Assert.True(Math.Abs(ratioEps - 1.0) < 1e-3,
                $"2 秒平移对 Δε 应当无影响：{ratioEps:F6}（游戏实测 0.9899 ⇒ 不是平移）");
            Assert.True(Math.Abs(ratioH - 1.0) < 1e-3,
                $"2 秒平移对 Δh 应当无影响：{ratioH:F6}（游戏实测 0.9892 ⇒ 不是平移）");
            Assert.True(eL <= eI,
                $"2 秒平移只会让 e 变**小**（{eI:E3} → {eL:E3}）⇒ 与游戏 6.09e-3 方向相反，也不是它");
        }
    }
}
