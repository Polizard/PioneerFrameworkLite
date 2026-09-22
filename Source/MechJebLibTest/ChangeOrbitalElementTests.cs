using System;
using MechJebLib.Functions;
using MechJebLib.Maneuvers;
using MechJebLib.Primitives;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **P1：`ChangeOrbitalElement`（alglib 的替换品）的验收。**
    ///
    /// 这是 P1 的第一道坎：上游那个文件用 vendored `alglib` 的 `minnlc`（增广拉格朗日 → SQP）解一个
    /// **2 变量 + 1 等式约束**的问题。本实现把它化归为**两层一维问题**
    /// （内层 `BrentRoot` 解"沿某方向需要多大 Δv"，外层 `BrentMin` 最小化那个大小）
    /// —— 于是用的是**已被上游 5000 条用例验过的**两个一维求解器，不再引入任何自造求解器。
    ///
    /// **判据分三层：**
    /// ① **oracle**：把求出的 Δv 带回去 → 重新提取要素 → 必须等于请求值（这就是 L2 的形状）；
    /// ② **最优性上界**：`Simple` 里的顺行解是**可行解** ⇒ 最小范数解必须 **≤** 它。
    ///    这条很便宜而且是真判据：它抓到"求根收敛到了曲线上的一个点、但不是离原点最近的那个"；
    /// ③ **平凡解**：请求当前值 ⇒ Δv ≈ 0（最容易被写错的一格）。
    /// </summary>
    public class ChangeOrbitalElementTests
    {
        private static readonly double Mu = OrbitMath.GM;
        private const double R0 = 7_000_000.0;

        /// <summary>偏心 0.3 的椭圆：`r·v = 0` ⇒ `e = v²R/μ − 1 = 0.3`，近心点在 +x。</summary>
        private static void Eccentric(out V3 r, out V3 v)
        {
            r = new V3(R0, 0, 0);
            v = new V3(0, System.Math.Sqrt(1.3 * Mu / R0), 0);
        }

        private static void AssertRel(double actual, double expected, double relTol, string what) =>
            Assert.True(System.Math.Abs(actual - expected) / System.Math.Abs(expected) <= relTol,
                $"{what}: 实得 {actual:R}，期望 {expected:R}（相对误差 {System.Math.Abs(actual - expected) / System.Math.Abs(expected):E2}）");

        // ── ① oracle + ② 最优性上界 ──────────────────────────────────────────────

        /// <summary>提高远心点：要素必须真的到请求值，且 Δv 不得超过 `Simple` 的顺行解。</summary>
        [Theory]
        [InlineData(2.0)]
        [InlineData(2.5)]
        [InlineData(4.0)]
        public void ChangeApoapsis_MeetsTarget_AndBeatsProgradeBound(double factor)
        {
            Eccentric(out V3 r, out V3 v);
            double target = R0 * factor;

            V3 dv = ChangeOrbitalElement.ChangeApoapsis(Mu, r, v, target, optguard: true);

            // ① oracle：带回去重提要素
            double apAfter = Astro.ApoapsisFromStateVectors(Mu, r, v + dv);
            AssertRel(apAfter, target, 1e-8, "远心点");

            // ② 最优性上界：顺行解可行 ⇒ min-norm ≤ it
            V3 prograde = Simple.DeltaVToChangeApoapsisPrograde(Mu, r, v, target);
            Assert.True(dv.magnitude <= prograde.magnitude * (1.0 + 1e-6),
                $"最小范数解 |Δv|={dv.magnitude:F4} 不该大于顺行可行解 {prograde.magnitude:F4}");
        }

        /// <summary>降低近心点：同上（用 `Simple.DeltaVToEllipticize` 保持当前远心点做可行解上界）。</summary>
        [Theory]
        [InlineData(0.9)]
        [InlineData(0.7)]
        [InlineData(0.5)]
        public void ChangePeriapsis_MeetsTarget_AndBeatsEllipticizeBound(double factor)
        {
            Eccentric(out V3 r, out V3 v);
            double target = R0 * factor;
            double apNow = Astro.ApoapsisFromStateVectors(Mu, r, v);

            V3 dv = ChangeOrbitalElement.ChangePeriapsis(Mu, r, v, target, optguard: true);

            double peAfter = Astro.PeriapsisFromStateVectors(Mu, r, v + dv);
            AssertRel(peAfter, target, 1e-8, "近心点");

            V3 feasible = Simple.DeltaVToEllipticize(Mu, r, v, target, apNow);
            Assert.True(dv.magnitude <= feasible.magnitude * (1.0 + 1e-6),
                $"最小范数解 |Δv|={dv.magnitude:F4} 不该大于 Ellipticize 可行解 {feasible.magnitude:F4}");
        }

        /// <summary>改半长轴与改偏心率：只要求 oracle（没有现成的可行解做上界）。</summary>
        [Theory]
        [InlineData(1.2)]
        [InlineData(1.6)]
        [InlineData(2.2)]
        public void ChangeSMA_MeetsTarget(double factor)
        {
            Eccentric(out V3 r, out V3 v);
            double aNow = Astro.SmaFromStateVectors(Mu, r, v);
            double target = aNow * factor;

            V3 dv = ChangeOrbitalElement.ChangeSMA(Mu, r, v, target, optguard: true);

            AssertRel(Astro.SmaFromStateVectors(Mu, r, v + dv), target, 1e-8, "半长轴");
        }

        [Theory]
        [InlineData(0.05)]
        [InlineData(0.15)]
        [InlineData(0.55)]
        public void ChangeECC_MeetsTarget(double target)
        {
            Eccentric(out V3 r, out V3 v);

            V3 dv = ChangeOrbitalElement.ChangeECC(Mu, r, v, target, optguard: true);

            AssertRel(Astro.EccFromStateVectors(Mu, r, v + dv), target, 1e-7, "偏心率");
        }

        // ── ③ 平凡解与分派 ──────────────────────────────────────────────────────

        /// <summary>请求"当前已有的值" ⇒ Δv ≈ 0。最容易被写错的一格（曲线在 r=0 处就满足约束）。</summary>
        [Fact]
        public void RequestingTheCurrentValue_GivesZeroDeltaV()
        {
            Eccentric(out V3 r, out V3 v);
            double apNow = Astro.ApoapsisFromStateVectors(Mu, r, v);
            double aNow = Astro.SmaFromStateVectors(Mu, r, v);
            double eNow = Astro.EccFromStateVectors(Mu, r, v);

            Assert.True(ChangeOrbitalElement.ChangeApoapsis(Mu, r, v, apNow).magnitude < 1e-6, "远心点");
            Assert.True(ChangeOrbitalElement.ChangeSMA(Mu, r, v, aNow).magnitude < 1e-6, "半长轴");
            Assert.True(ChangeOrbitalElement.ChangeECC(Mu, r, v, eNow).magnitude < 1e-6, "偏心率");
        }

        /// <summary>
        /// ⚠ **钉住一个上游的浮点边界**（2026-09-18 实测）：`ChangePeriapsis` 的守卫是
        /// `peR > r.magnitude`（**严格大于**），而 `Astro.PeriapsisFromStateVectors` 在"正好位于近心点"时
        /// 算出的 `PeR` 会比 `|r|` **大一丝**（`7000000.000000001` vs `7000000`）⇒
        /// **"把近心点设成当前半径"会被守卫拒掉**。
        ///
        /// 这是**上游守卫逐字照搬**带来的行为（我们没有改它——改它就成了偏离上游）。登记在此，
        /// 让将来遇到"明明没改却报 Bad periapsis"的人有一个现成答案：
        /// **在近心点处请求当前 PeR 要改用 `ChangeApsis`（它按 `≤ |r|` 分派），或者把目标稍微放进去一点。**
        /// </summary>
        [Fact]
        public void ChangePeriapsis_AtCurrentPeriapsis_IsRejectedByUpstreamGuard()
        {
            Eccentric(out V3 r, out V3 v);
            double peNow = Astro.PeriapsisFromStateVectors(Mu, r, v);

            Assert.True(peNow > r.magnitude, $"前置：这个浮点边界正是问题所在（PeR={peNow:R} vs |r|={r.magnitude:R}）");
            Assert.Throws<ArgumentException>(() => ChangeOrbitalElement.ChangePeriapsis(Mu, r, v, peNow));

            // 而**恰好取 `|r|`** 就是允许的（守卫是严格大于）⇒ 正常返回，且 Δv ≈ 0。
            // ⚠ 别用 `peNow * 0.9999` 之类"稍微放进去"的值：那意味着把近心点降 ~700m，
            //   需要 ~0.19 m/s 而不是 0（初稿我这么写过，被自己的断言打回来了）。
            V3 dv = ChangeOrbitalElement.ChangePeriapsis(Mu, r, v, r.magnitude);
            Assert.True(dv.magnitude < 1e-6, $"目标 = |r| 时 Δv 应≈0，实得 {dv.magnitude:E3}");
            AssertRel(Astro.PeriapsisFromStateVectors(Mu, r, v + dv), r.magnitude, 1e-8, "近心点 = |r|");
        }

        /// <summary>`ChangeApsis` 按"目标半径与当前半径的大小关系"分派到近心点/远心点。</summary>
        [Fact]
        public void ChangeApsis_DispatchesToPeriOrApoapsis()
        {
            Eccentric(out V3 r, out V3 v);
            double rNow = r.magnitude;

            // 目标 < 当前半径 ⇒ 近心点
            V3 dvLow = ChangeOrbitalElement.ChangeApsis(Mu, r, v, rNow * 0.8);
            AssertRel(Astro.PeriapsisFromStateVectors(Mu, r, v + dvLow), rNow * 0.8, 1e-8, "分派→近心点");

            // 目标 > 当前半径 ⇒ 远心点
            V3 dvHigh = ChangeOrbitalElement.ChangeApsis(Mu, r, v, rNow * 3.0);
            AssertRel(Astro.ApoapsisFromStateVectors(Mu, r, v + dvHigh), rNow * 3.0, 1e-8, "分派→远心点");
        }

        /// <summary>共面性：`dvz` 恒为 0（上游的变量集就是 2 维）⇒ 轨道面不得改变。</summary>
        [Fact]
        public void ManeuverStaysInPlane()
        {
            Eccentric(out V3 r, out V3 v);
            V3 hBefore = V3.Cross(r, v).normalized;

            V3 dv = ChangeOrbitalElement.ChangeApoapsis(Mu, r, v, R0 * 3.0);
            V3 hAfter = V3.Cross(r, v + dv).normalized;

            Assert.True((hAfter - hBefore).magnitude < 1e-9,
                $"轨道面法向不该变：{hBefore} → {hAfter}");
        }

        /// <summary>
        /// 不可达的目标 ⇒ 明确抛（而不是返回一个静默的错答案）。
        ///
        /// ⚠ **这里的目标不是随手挑的**：本实现把无量纲 Δv 的搜索上限钉在 `DV_CAP = 2.83`（= `2·v_esc`）。
        /// `e` 与 `a` 在**单次脉冲**下并没有硬上界（加大 Δv 就能继续推），所以"不可达"是**由那个上限定义的**。
        /// `e = 20` 需要 `v &gt; √(21·μ/r) ≈ 4.58·v_circ` ⇒ 超出上限 ⇒ 必抛。
        /// （初稿我写的是 `e = 5.0`，而它**是可达的**——`√(6μ/r) ≈ 2.45·v_circ` 还在上限内。实现没写错，是我的前提错了。）
        /// </summary>
        [Fact]
        public void TargetBeyondDeltaVCap_Throws()
        {
            Eccentric(out V3 r, out V3 v);
            Assert.Throws<ArgumentException>(() => ChangeOrbitalElement.ChangeECC(Mu, r, v, 20.0));
        }

        /// <summary>
        /// ⭐ **实测证据**：`ChangeApoapsis` 返回的**最小范数**解，一般**不是**"在远心点纯横推"那个解。
        ///
        /// 这条是 `DeltaVToResonantOrbit` **偏离上游做法**的全部理由（见那里的说明）：
        /// 上游的共振轨道闭式建立在"改一个拱点时另一个不变"上，而那只在
        /// "最小范数解恰好是纯横推"时成立。这里把它量出来钉住：
        /// · 目标 `ApR × 1.016`（小改动）⇒ 我的解**更省**（370 vs 656 m/s）但**含大径向分量**
        ///   ⇒ 它顺带把 `PeR` 从 1.0000 改到了 1.0943R₀ ⇒ 前提失效；
        /// · 目标 `ApR × 1.442`（f=2 那一档）⇒ 我的解**恰好就是纯横推**（径向 = 0）⇒ 前提成立 ⇒ 上游组合精确。
        /// ⇒ **上游不是错，而是"漂不漂取决于 f"**；我们的做法（直接约束 `sma`）没有这个前提。
        /// </summary>
        [Fact]
        public void ChangeApoapsis_MinNormSolutionIsNotGenerallyTheTransverseOne()
        {
            Eccentric(out V3 r0, out V3 v0);
            var o = OrbitView.FromStateVectors(r0, v0, Mu, 0.0);
            double ut = o.NextApoapsisTime(0.0);
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(ut);
            double peBefore = Astro.PeriapsisFromStateVectors(Mu, r, v);

            V3 radialDir = r.normalized;

            // 小改动 ⇒ 最小范数解与纯横推**不同**，且更省；代价是它改了另一个拱点
            {
                double target = o.ApR * 1.016;
                V3 mine = ChangeOrbitalElement.ChangeApoapsis(Mu, r, v, target);
                V3 transverse = Simple.DeltaVToChangeApoapsisPrograde(Mu, r, v, target);

                Assert.True(mine.magnitude < transverse.magnitude * 0.99,
                    $"小改动时最小范数解应明显更省：{mine.magnitude:F2} vs 纯横推 {transverse.magnitude:F2}");
                Assert.True(System.Math.Abs(V3.Dot(mine, radialDir)) > 100.0,
                    $"小改动时最小范数解应含大径向分量，实得 {V3.Dot(mine, radialDir):F2}");

                double peAfter = Astro.PeriapsisFromStateVectors(Mu, r, v + mine);
                Assert.True(System.Math.Abs(peAfter - peBefore) / peBefore > 0.05,
                    $"⇒ 它顺带改了另一个拱点（PeR {peBefore / R0:F4} → {peAfter / R0:F4} R0）——这正是上游共振闭式的前提失效处");
            }

            // 大改动 ⇒ 最小范数解**恰好**是纯横推 ⇒ 前提成立（上游组合在这一档精确）
            {
                double target = o.ApR * 1.442;
                V3 mine = ChangeOrbitalElement.ChangeApoapsis(Mu, r, v, target);
                V3 transverse = Simple.DeltaVToChangeApoapsisPrograde(Mu, r, v, target);

                Assert.True(System.Math.Abs(V3.Dot(mine, radialDir)) < 1e-6,
                    $"大改动时最小范数解应恰好是纯横推，实得径向 {V3.Dot(mine, radialDir):E2}");
                Assert.Equal(transverse.magnitude, mine.magnitude, 6);
            }
        }

        /// <summary>入参守卫与上游逐字一致（`ChangeApoapsis` 拒绝小于当前半径的目标）。</summary>
        [Fact]
        public void ArgumentGuards_MatchUpstream()
        {
            Eccentric(out V3 r, out V3 v);
            Assert.Throws<ArgumentException>(() => ChangeOrbitalElement.ChangeApoapsis(Mu, r, v, R0 * 0.5));
            Assert.Throws<ArgumentException>(() => ChangeOrbitalElement.ChangePeriapsis(Mu, r, v, R0 * 1.5));
            Assert.Throws<ArgumentException>(() => ChangeOrbitalElement.ChangeECC(Mu, r, v, -1.0));
            Assert.Throws<ArgumentException>(() => ChangeOrbitalElement.ChangeSMA(Mu, r, v, double.NaN));
        }
    }
}
