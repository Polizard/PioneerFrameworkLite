using System;
using MechJebLib.Functions;
using MechJebLib.Maneuvers;
using MechJebLib.Primitives;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **改倾角的"两支"问题**（2026-09-19 由游戏内一键验收抓出）。
    ///
    /// `Astro.VelocityForInclination` 是"按新倾角重摆水平航向"，而航向有两个解（北向/南向）
    /// —— **同一个倾角、交点线差 180°**。上游的普通改倾角只走一支 ⇒ 碰巧取到远支时，
    /// 代价与节点都错。本文件把游戏里那条**同一条受控轨道**搬进纯测试：
    ///
    /// | 量 | 实测（游戏内） |
    /// |---|---|
    /// | 请求 | 直觉 45° ⇒ 适配层 135°（Δi = 105°） |
    /// | 旧（单支） | **6507 m/s**，方向 T=−6451 / N=+849 ⇒ 对应 Δ≈165° = 30+135（节点被翻） |
    /// | 闭式下界 | `2v·sin(105°/2)` = **5207 m/s** |
    /// </summary>
    public class InclinationBranchTests
    {
        private static readonly double Mu = OrbitMath.GM;
        private const double R0 = 7_000_000.0;
        private const double BodyR = 5_000_000.0;
        private static readonly V3 Pole = new V3(0, 1, 0); // 极轴 +Y（批4 P4 统一；旧值与适配层的 +z 一起作废）

        /// <summary>与游戏内验收铺的**同一条**轨道：倾角 30°、e=0.3（a = 1e7）。</summary>
        private static OrbitView Controlled()
        {
            double vc = Math.Sqrt(Mu / R0);
            double inc = Math.PI / 6;
            // 构造向量按 `(x, y, z)_新 = (x, z, −y)_旧` 换元（同一个物理轨道的像；倾角 30° 不变）
            return OrbitView.FromStateVectors(new V3(R0, 0, 0),
                new V3(0, Math.Sqrt(1.3) * vc * Math.Sin(inc), -Math.Sqrt(1.3) * vc * Math.Cos(inc)),
                Mu, 0.0, Pole, BodyR);
        }

        /// <summary>升交点经度（度，`[0,360)`）。本用例天体不自转 ⇒ 惯性/固连口径相同。</summary>
        private static double LanDeg(OrbitView o, double ut)
        {
            // 批6 S3-3a：交点位置走 `Try*`；本用例的轨道有交点 ⇒ 失败即用例前提被破坏，应当红
            Assert.True(o.TryAscendingNodePositionEquatorial(ut, out V3 an),
                "本用例的轨道必须有赤道升交点（否则 LAN 的判据无从谈起）");
            // 两系同轴（批4 P4）⇒ 不经转换（批6 S2：跨系只经 OrbitView 的进/出两个口）
            double deg = SurfaceFrame.EciToLongLat(an, 0.0, ut).lonRad * 180.0 / Math.PI;
            double m = deg % 360.0;
            return m < 0.0 ? m + 360.0 : m;
        }

        private static double AngDiff(double a, double b)
        {
            double d = (a - b) % 360.0;
            if (d > 180.0) d -= 360.0;
            if (d < -180.0) d += 360.0;
            return Math.Abs(d);
        }

        [Fact]
        public void ChangeInclination_TakesTheBranchThatKeepsTheNode()
        {
            var o = Controlled();
            double ut = o.NextApoapsisTime(0.0);
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(ut);

            // 前提自检：轨道是 30°，点火点在赤道上（否则下面那条闭式只是下界、不可达）
            double incNow = Astro.IncFromStateVectors(r, v);
            Assert.True(Math.Abs(incNow - Math.PI / 6) < 1e-9, $"前提：倾角 30°（实得 {incNow * 180 / Math.PI:F6}°）");
            Assert.True(Math.Abs(r.normalized.y) < 1e-9, $"前提：点火点在赤道上（|r̂·极轴|={Math.Abs(r.normalized.y):E3}）");

            double incWant = Math.PI * 135.0 / 180.0; // 直觉 45° ⇒ 适配层 180−45

            V3 dvNew = OrbitalManeuverCalculator.DeltaVToChangeInclination(o, ut, incWant);
            V3 dvOld = Simple.DeltaVToChangeInclination(r, v, incWant); // 上游的单支行为

            // ① 闭式：纯平面变更的最小代价 = 2v·sin(|Δi|/2)（支点在赤道上时可达）
            double expectMin = 2.0 * v.magnitude * Math.Sin(Math.Abs(incWant - incNow) / 2.0);
            Assert.True(Math.Abs(dvNew.magnitude - expectMin) < 1e-6 * expectMin,
                $"新解应等于闭式下界：{dvNew.magnitude:F3} vs {expectMin:F3} m/s");

            // ② 倾角要精确达到
            var after = OrbitView.FromStateVectors(r, v + dvNew, Mu, ut, Pole, BodyR);
            Assert.True(Math.Abs(after.inclination - incWant) < 1e-9,
                $"倾角：{after.inclination * 180 / Math.PI:F6}° vs 请求 {incWant * 180 / Math.PI:F6}°");

            // ③ **交点线必须不变**——这才是"改倾角"的语义（绕交点线倾斜）
            Assert.True(AngDiff(LanDeg(after, ut), LanDeg(o, ut)) < 1e-6,
                $"升交点经度必须保持：{LanDeg(o, ut):F6}° → {LanDeg(after, ut):F6}°");

            // ④ 反例钉住：旧单支行为给出的正是"绕极轴多转一圈"的解（更贵），留着引以为戒
            Assert.True(dvOld.magnitude > dvNew.magnitude * 1.2,
                $"旧单支行为应当明显更贵：旧 {dvOld.magnitude:F2} / 新 {dvNew.magnitude:F2} m/s" +
                $"（游戏内实测 6507 vs 闭式 5207）");
        }
    }
}
