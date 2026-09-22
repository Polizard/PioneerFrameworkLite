using System;
using MechJebLib.Functions;
using MechJebLib.Primitives;
using MechJebLib.TwoBody;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// `OperationStationary`（静止轨道）的验收。
    /// **判据就是"静止轨道"的定义本身**：终态必须**圆、赤道、周期 = 天体自转周期**，
    /// 且落点在**目标经线正上方**（最后一条是上游没有保证的——它差了整个转移期间的自转量，
    /// 见该 Operation 的类注释偏离 ②）。
    /// </summary>
    public class OperationStationaryTests
    {
        private static readonly double Mu = OrbitMath.GM;
        private const double R0 = 7.0e6;
        private const double BodyR = 5.0e6;
        private const double Omega = 2.0 * Math.PI / 60000.0; // 1 游戏天 = 60000 s
        private static readonly V3 Pole = new V3(0, 0, 1);

        private static OrbitView EquatorialRotating(double soi = double.NaN)
        {
            double vc = Math.Sqrt(Mu / R0);
            return OrbitView.FromStateVectors(new V3(R0, 0, 0), new V3(0, vc, 0), Mu, 0.0, Pole, BodyR, soi, Omega);
        }

        private static double Wrap360(double d)
        {
            double m = d % 360.0;
            return m < 0.0 ? m + 360.0 : m;
        }

        private static double AngDiff(double a, double b)
        {
            double d = (a - b) % 360.0;
            if (d > 180.0) d -= 360.0;
            if (d < -180.0) d += 360.0;
            return Math.Abs(d);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(77.0)]
        [InlineData(300.0)]
        public void Stationary_EndsCircularEquatorialSynchronous_OverRequestedLongitude(double wantLon)
        {
            var o = EquatorialRotating();
            var op = new OperationStationary { NewLongitude = { Val = wantLon } };
            var nodes = op.MakeNodes(o, 0.0, new FixedTargetView(o));

            Assert.NotNull(nodes);
            Assert.Equal(2, nodes!.Count);

            PlanNode n1 = nodes[0], n2 = nodes[1];
            Assert.True(n1.ut >= 0.0, $"第一次点火要在未来（ut1={n1.ut:E3}）");
            Assert.True(n2.ut > n1.ut, $"第二次点火要在第一次之后（ut1={n1.ut:E3} ut2={n2.ut:E3}）");

            (V3 r1, V3 v1) = o.RightHandedStateVectorsAtUT(n1.ut);
            (V3 r2, V3 v2) = Shepperd.Solve(Mu, n2.ut - n1.ut, r1, v1 + n1.dV);
            V3 v3 = v2 + n2.dV;

            // ① 圆
            Assert.True(Astro.EccFromStateVectors(Mu, r2, v3) < 1e-6,
                $"终态必须是圆：e={Astro.EccFromStateVectors(Mu, r2, v3):E3}");

            // ② 赤道，且与天体**共转**
            V3 h = V3.Cross(r2, v3);
            Assert.True(Math.Abs(V3.Dot(h.normalized, Pole)) > 1.0 - 1e-9,
                $"终态必须在赤道面内：|ĥ·极轴|={Math.Abs(V3.Dot(h.normalized, Pole)):E3}");

            // "同向"的判据**从天体自己的共转速度推**，不用我猜符号：
            // 本 mod 里顺行是 h ∥ −Y(游戏) ⇒ 适配层是 −z，与"游戏 +Y 极轴"直觉相反（踩过一次）。
            DVector3 gPosForRot = OrbitView.ToD(OrbitView.AdapterToGameWorld(r2));
            // ⚠ 共转速度是**切向**的，而 `h` 是**转轴** ⇒ 两者本来垂直（点乘 0）。
            // 转轴要从"位置 × 共转速度"推：`轴 ∝ r × v_共转`。
            V3 coRotVel = OrbitView.GameWorldToAdapter(OrbitView.ToV3(SurfaceFrame.SurfaceVelocity(gPosForRot, Omega)));
            V3 coRotAxis = V3.Cross(r2, coRotVel).normalized;
            Assert.True(V3.Dot(h.normalized, coRotAxis) > 0.99,
                $"必须与天体共转：ĥ·共转轴={V3.Dot(h.normalized, coRotAxis):F6}（<0 就是反向同步，不是静止）");

            // ③ 周期 = 天体自转周期
            var final = OrbitView.FromStateVectors(r2, v3, Mu, n2.ut, Pole, BodyR, double.NaN, Omega);
            double period = 2.0 * Math.PI / Omega;
            Assert.True(Math.Abs(final.period - period) < 1e-6 * period,
                $"周期必须是自转周期：{final.period:F6} vs {period:F6} s");

            // ④ 落点在目标经线正上方（**上游这条不成立**：它差一个转移期间的自转量）
            DVector3 g = OrbitView.ToD(OrbitView.AdapterToGameWorld(r2));
            double lon = Wrap360(SurfaceFrame.EciToLongLat(g, Omega, n2.ut).lonRad * 180.0 / Math.PI);
            Assert.True(AngDiff(lon, wantLon) < 1e-2,
                $"落点经度：{lon:F6}° vs 请求 {wantLon}°（差 {AngDiff(lon, wantLon):E3}°）");
        }

        /// <summary>没有自转数据 ⇒ 明说做不到（不去算一个 ω=0 的假同步半径）。</summary>
        [Fact]
        public void Stationary_WithoutRotationData_Throws()
        {
            double vc = Math.Sqrt(Mu / R0);
            var o = OrbitView.FromStateVectors(new V3(R0, 0, 0), new V3(0, vc, 0), Mu, 0.0, Pole, BodyR);

            var op = new OperationStationary();
            // ⚠ 守卫是 `MakeNodesImpl` 抛、`Operation.MakeNodes` **吞成 `ErrorMessage` 并返回 null**
            //（框架契约）⇒ 断言必须打在那一层，不能指望异常穿透到调用方。
            Assert.Null(op.MakeNodes(o, 0.0, new FixedTargetView(o)));
            Assert.Contains("自转数据", op.GetErrorMessage() ?? "");
        }

        /// <summary>同步半径超出影响范围 ⇒ 静止轨道不存在（上游同款守卫）。</summary>
        [Fact]
        public void Stationary_SynchronousRadiusBeyondSoi_Throws()
        {
            var o = EquatorialRotating(soi: 1.0e7); // 同步半径 ~2.63e7 > 1e7
            var op = new OperationStationary();
            Assert.Null(op.MakeNodes(o, 0.0, new FixedTargetView(o)));
            Assert.Contains("影响范围", op.GetErrorMessage() ?? "");
        }
    }
}
