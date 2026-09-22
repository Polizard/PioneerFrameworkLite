using System;
using MechJebLib.Primitives;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **交会阶梯的积木**（B3，2026-09-20）判据。上游成员名逐字对齐，实现去 KSP 化（同 P-1 的处置）。
    ///
    /// 判据一律是**定义式**（"做完之后必须等于目标"），不是"和谁比差不多"：
    ///   ① `DeltaVAndTimeToMatchPlanesAscending` ⇒ 打完两轨道的**相对倾角归零**、且**速度模长守恒**
    ///      （平面改变不改形状 ⇒ 模长必须逐位保）；
    ///   ② `DeltaVToInterceptAtTime` ⇒ 打完 v1、传播 dt 后**正好落在目标位置**（真交会，不是"接近"）；
    ///      带 `offsetDistance` ⇒ 落在**离目标 offset** 处（沿视线）；
    ///   ③ 回归：`DeltaVToMatchVelocities` 有且只有一份（本轮差点写出第二份，编译器 CS0111 拦下）
    ///      ⇒ 用"打完它相对速度归零"钉住那份的语义。
    /// </summary>
    public class RendezvousLadderBlocksTests
    {
        private const double Mu = 2.0e14;             // 与游戏一致
        private const double R0 = 7_000_000.0;

        /// <summary>赤道圆轨（升交点取 X 轴 ⇒ 与其相交的倾角轨道容易构造）。</summary>
        private static OrbitView Equatorial(double ut = 0.0, double incDeg = 0.0)
        {
            double vc = Math.Sqrt(Mu / R0);
            double inc = incDeg * Math.PI / 180.0;
            var r = new V3(R0, 0, 0);
            var v = new V3(0, vc * Math.Cos(inc), vc * Math.Sin(inc));
            return OrbitView.FromStateVectors(r, v, Mu, ut, new V3(0, 0, 1), R0 * 0.2);
        }

        /// <summary>① 匹配平面：打完相对倾角归零、速度模长守恒。</summary>
        [Fact]
        public void MatchPlanes_NullsRelativeInclinationAndPreservesSpeed()
        {
            var self = Equatorial(0.0, 0.0);
            var target = Equatorial(0.0, 2.0);            // 相对倾角 2°
            double inc0 = self.RelativeInclination(target) * 180.0 / Math.PI;

            V3 dv = OrbitalManeuverCalculator.DeltaVAndTimeToMatchPlanesAscending(self, target, 0.0, out double burnUT);
            Assert.True(!double.IsNaN(burnUT) && !double.IsInfinity(burnUT) && burnUT >= 0.0, $"交点时刻应有限非负，实测 {burnUT}");

            (V3 r, V3 v) = self.RightHandedStateVectorsAtUT(burnUT);
            var after = OrbitView.FromStateVectors(r, v + dv, Mu, burnUT, new V3(0, 0, 1), R0 * 0.2);
            double inc1 = after.RelativeInclination(target) * 180.0 / Math.PI;

            Assert.True(inc1 * 180.0 / Math.PI < 1e-9, $"匹配后相对倾角应归零：{inc0:F4}° → {inc1:E3}°");
            Assert.True(Math.Abs(after.eccentricity - self.eccentricity) < 1e-9, "平面改变不该动离心率");
            Assert.True(Math.Abs(after.semiMajorAxis - self.semiMajorAxis) / self.semiMajorAxis < 1e-12,
                "平面改变不该动半长轴");
        }

        /// <summary>② 拦截：打完 v1、传播 dt ⇒ 正好到达目标在 t0+dt 的位置（真交会）。</summary>
        [Fact]
        public void InterceptAtTime_ArrivesAtTargetPosition()
        {
            var self = Equatorial(0.0, 0.0);
            var target = Equatorial(0.0, 0.4);            // 略异面，逼 Lambert 真的解
            double t0 = 0.0, dt = 1800.0;                 // 30 分钟

            (V3 v1, V3 v2) = OrbitalManeuverCalculator.DeltaVToInterceptAtTime(self, t0, target, dt);
            Assert.True(v1.sqrMagnitude > 0.0 && v2.sqrMagnitude > 0.0, "两次点火都应有非零 Δv");

            (V3 ri, V3 vi) = self.RightHandedStateVectorsAtUT(t0);
            var transfer = OrbitView.FromStateVectors(ri, vi + v1, Mu, t0, new V3(0, 0, 1), R0 * 0.2);
            (V3 rf2, V3 vf2) = transfer.RightHandedStateVectorsAtUT(t0 + dt);
            (V3 rf, V3 vfT) = target.RightHandedStateVectorsAtUT(t0 + dt);

            Assert.True((rf2 - rf).magnitude < 1.0,
                $"应在 dt 时刻到达目标位置：偏差 {(rf2 - rf).magnitude:E3} m");
            // 第二次点火 = 匹配目标速度 ⇒ 打完相对速度归零
            Assert.True((vf2 + v2 - vfT).magnitude < 1e-6,
                $"v2 打完应匹配目标速度：残差 {(vf2 + v2 - vfT).magnitude:E3} m/s");
        }

        /// <summary>②b 带 `offsetDistance` ⇒ 落在离目标**视线后退**该距离处（我们按意图实现的那处偏离）。</summary>
        [Fact]
        public void InterceptAtTime_WithOffset_StopsShortOfTarget()
        {
            var self = Equatorial(0.0, 0.0);
            var target = Equatorial(0.0, 0.0);
            double t0 = 0.0, dt = 1800.0, offset = 5000.0;

            (V3 v1, V3 _) = OrbitalManeuverCalculator.DeltaVToInterceptAtTime(self, t0, target, dt, offset);
            (V3 ri, V3 vi) = self.RightHandedStateVectorsAtUT(t0);
            var transfer = OrbitView.FromStateVectors(ri, vi + v1, Mu, t0, new V3(0, 0, 1), R0 * 0.2);
            (V3 rf2, V3 _) = transfer.RightHandedStateVectorsAtUT(t0 + dt);
            (V3 rf, V3 _) = target.RightHandedStateVectorsAtUT(t0 + dt);

            double sep = (rf2 - rf).magnitude;
            Assert.True(Math.Abs(sep - offset) < 1e-3,
                $"应停在离目标 {offset:F0} m 处，实测 {sep:F3} m（**不是撞上去**）");
        }

        /// <summary>③ `DeltaVToMatchVelocities`：打完相对速度归零（全世界只有这一份实现）。</summary>
        [Fact]
        public void MatchVelocities_NullsRelativeVelocity()
        {
            var self = Equatorial(0.0, 0.0);
            var target = Equatorial(0.0, 1.0);
            const double ut = 600.0;

            V3 dv = OrbitalManeuverCalculator.DeltaVToMatchVelocities(self, ut, target);
            (V3 r, V3 v) = self.RightHandedStateVectorsAtUT(ut);
            V3 vRel = v + dv - target.RightHandedOrbitalVelocityAtUT(ut);
            Assert.True(vRel.magnitude < 1e-9, $"匹配后相对速度应为零，实测 {vRel.magnitude:E3} m/s");

            // 语义守卫：它**不改位置** ⇒ 只会在速度上动刀（别拿"改轨"的实现来顶替）
            V3 dvAtOther = OrbitalManeuverCalculator.DeltaVToMatchVelocities(self, ut + 1.0, target);
            Assert.True((dvAtOther - dv).magnitude > 1e-6, "不同 UT 的匹配速度 Δv 必须不同（否则实现是常数）");
            _ = r;
        }
    }
}
