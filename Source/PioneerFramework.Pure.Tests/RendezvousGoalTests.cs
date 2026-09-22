using System;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **交会目的**（`RendezvousGoal`）的判据，重点钉两件事：
    ///
    /// ① **有符号相位的方向性**（`SignedPhaseDeg`）——这是最容易被写成"无符号"而静默出错的地方：
    ///    "目标在我前方 5°"与"在我后方 5°"必须**反号**。写成无符号的话，
    ///    判据会把两件相反的事判成同一件，而且**不会报错**（只是船往反方向收）。
    /// ② **相位判据的三条同中**：同轨（Δa）+ 相对速度 + 相位差，缺一条都不算。
    ///
    /// 纯几何、无引擎（`RendezvousGoal` 是纯描述量，链进本工程）。
    /// </summary>
    public class RendezvousGoalTests
    {
        private const double Mu = 3.986e14;
        private static readonly double R = SpaceConstants.PlanetRadiusMeters + 500_000.0;

        /// <summary>在半径 <paramref name="r" />、沿轨偏移 <paramref name="phaseDeg" /> 处放一个点。
        /// 轨道面 = XY 平面（法向 +Z），顺行 = 逆时针。</summary>
        private static (DVector3 r, DVector3 v) Sample(double r, double phaseDeg)
        {
            double th = phaseDeg * Math.PI / 180.0;
            DVector3 pos = new DVector3(r * Math.Cos(th), r * Math.Sin(th), 0.0);
            double vc = Math.Sqrt(Mu / r);
            DVector3 vel = new DVector3(-vc * Math.Sin(th), vc * Math.Cos(th), 0.0);
            return (pos, vel);
        }

        /// <summary>① **符号**：目标在顺行前方 ⇒ 正；在后方 ⇒ 负；同一个角度的两侧必须反号。</summary>
        [Fact]
        public void SignedPhase_IsAheadPositive_BehindNegative()
        {
            var (r0, v0) = Sample(R, 0.0);

            foreach (double ahead in new[] { 0.5, 5.0, 45.0, 90.0, 170.0 })
            {
                var (rt, _) = Sample(R, ahead);
                double ph = RendezvousGoal.SignedPhaseDeg(r0, v0, rt);
                Assert.True(Math.Abs(ph - ahead) < 1e-9,
                    $"前方 {ahead}° 应读出 +{ahead}°，实测 {ph:F6}°");

                var (rb, _) = Sample(R, -ahead);
                double pb = RendezvousGoal.SignedPhaseDeg(r0, v0, rb);
                Assert.True(Math.Abs(pb + ahead) < 1e-9,
                    $"后方 {ahead}° 应读出 -{ahead}°，实测 {pb:F6}°（**若与前方同号 ⇒ 方向性丢了**）");
            }
        }

        /// <summary>①（续）**短弧优先**：179° 与 181° 必须给出 −179° 而不是 +181°（量程 −180..+180）。</summary>
        [Fact]
        public void SignedPhase_WrapsToShortArc()
        {
            var (r0, v0) = Sample(R, 0.0);
            var (r179, _) = Sample(R, 179.0);
            var (r181, _) = Sample(R, 181.0);
            Assert.True(Math.Abs(RendezvousGoal.SignedPhaseDeg(r0, v0, r179) - 179.0) < 1e-6);
            Assert.True(Math.Abs(RendezvousGoal.SignedPhaseDeg(r0, v0, r181) + 179.0) < 1e-6,
                "181° 应折到 −179°（短弧）");
        }

        /// <summary>② **相位判据三条同中**：Δa / 相对速度 / 相位差，缺一条都不算达成。</summary>
        [Fact]
        public void PhaseSynced_RequiresAllThree()
        {
            var goal = RendezvousGoal.PhaseSynced(phaseDeg: 30.0, phaseTolDeg: 2.0,
                aTol: 1000.0, speedTol: 20.0);

            // 全中
            Assert.True(goal.IsMet(500.0, 5.0, 30.5), "Δa 够、速度够、相位够 ⇒ 应达成");
            // ① 同轨不满足
            Assert.False(goal.IsMet(5000.0, 5.0, 30.5), "Δa 超差 ⇒ 不达成");
            // ② 相对速度不满足
            Assert.False(goal.IsMet(500.0, 99.0, 30.5), "相对速度超差 ⇒ 不达成");
            // ③ 相位差不满足
            Assert.False(goal.IsMet(500.0, 5.0, 40.0), "相位差超差 ⇒ 不达成");
            // ⚠ 缺相位参（两参重载）⇒ **保守不判达成**，不许用"缺参"冒充已到
            Assert.False(goal.IsMet(500.0, 5.0), "缺相位参 ⇒ 必须保守返回 false");
        }

        /// <summary>②（续）**相位差取短弧**：期望 350° 与实测 −10° 是同一件事（差 0），不是差 360°。</summary>
        [Fact]
        public void PhaseSynced_PhaseErrorWraps()
        {
            var goal = RendezvousGoal.PhaseSynced(phaseDeg: 350.0, phaseTolDeg: 2.0);
            Assert.True(Math.Abs(goal.PhaseErrorDeg(-10.0)) < 1e-9,
                "期望 350°、实测 −10° ⇒ 误差应为 0（短弧），实测 " + goal.PhaseErrorDeg(-10.0));
            Assert.True(goal.IsMet(0.0, 0.0, -10.0), "同一件事 ⇒ 应达成");
            Assert.False(goal.IsMet(0.0, 0.0, 0.0), "相差 10° ⇒ 超 2° 容差 ⇒ 不达成");
        }

        /// <summary>③ 既有目的**不受影响**（回归钉）：两参重载的行为与改动前逐位相同。</summary>
        [Fact]
        public void ExistingKinds_Unchanged()
        {
            Assert.True(RendezvousGoal.Precise(1000, 10).IsMet(900, 9));
            Assert.False(RendezvousGoal.Precise(1000, 10).IsMet(1001, 9));
            Assert.False(RendezvousGoal.Precise(1000, 10).IsMet(900, 11));

            Assert.True(RendezvousGoal.Combat().IsMet(4999, 49));
            Assert.False(RendezvousGoal.Combat().IsMet(5001, 49));

            Assert.True(RendezvousGoal.Loose(10000).IsMet(9000, 9999), "Loose 只判距离");
            Assert.False(RendezvousGoal.Loose(10000).IsMet(10001, 0));
        }
    }
}
