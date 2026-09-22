using MechJebLib.Primitives;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **天地映射的纯数学**（`Base/Orbital/SurfaceFrame.cs`）。
    ///
    /// 这批函数原先在 `Launch/GetSurfaceState.cs`（游戏侧）—— 但它们是**纯三角**，
    /// 而 `LAN`/`Longitude`/`Stationary` 三个 Operation（纯层）都要用 ⇒ 搬到纯层、原处转发。
    /// ⇒ **它们现在可以单测了**：本节把原本只能在游戏里跑的"极轴口径"判据（探针 A）也变成单测。
    /// </summary>
    public class SurfaceFrameTests
    {
        private const double R = 5_000_000.0;
        private const double Omega = 2.0 * System.Math.PI / 60000.0; // 1 游戏天 = 60000 s

        /// <summary>`FixedToEci` 与 `EciToFixed` 必须互逆（逐位级别）。</summary>
        [Fact]
        public void FixedAndEci_AreInverse()
        {
            var p = new DVector3(1.5e6, -2.5e6, 3.5e6);
            foreach (double tick in new[] { 0.0, 1234.5, 60000.0, -777.0 })
            {
                DVector3 back = SurfaceFrame.EciToFixed(SurfaceFrame.FixedToEci(p, Omega, tick), Omega, tick);
                Assert.Equal(p.x, back.x, 6);
                Assert.Equal(p.y, back.y, 6);
                Assert.Equal(p.z, back.z, 6);
            }
        }

        /// <summary>
        /// 经纬度 → ECI → 经纬度 的**往返**必须闭合（含自转相位：`SurfaceState` 把 λ₀ 变成 ECI 经度、
        /// `EciToLongLat` 再转回固连系 ⇒ 必须回到 λ₀）。
        /// </summary>
        [Theory]
        [InlineData(0.0, 0.0)]
        [InlineData(0.5, 1.1)]
        [InlineData(-0.7, -2.4)]
        [InlineData(1.2, 3.0)]
        public void LongLat_RoundTripsThroughSurfaceState(double lat, double lon0)
        {
            foreach (double tick in new[] { 0.0, 5000.0, 43210.0 })
            {
                (DVector3 pos, DVector3 _) = SurfaceFrame.SurfaceState(R, lat, lon0, Omega, tick);
                (double latBack, double lonBack) = SurfaceFrame.EciToLongLat(pos, Omega, tick);

                Assert.True(System.Math.Abs(latBack - lat) < 1e-12, $"纬度往返不闭合：{lat} → {latBack}");
                double dLon = System.Math.Abs(System.Math.Atan2(System.Math.Sin(lonBack - lon0), System.Math.Cos(lonBack - lon0)));
                Assert.True(dLon < 1e-12, $"经度往返不闭合：{lon0} → {lonBack}");
            }
        }

        /// <summary>
        /// ⭐ **极轴口径的判据（探针 A 的纯版）**：极轴上的点纬度必须是 **±90°**、赤道面内的必须是 **0°**。
        /// 这是**本 mod 自己的约定**（`GetSurfaceState` 文件头：自转轴 = +y），
        /// 错一个轴或一个符号就会红 —— 而且**不需要开游戏**。
        /// </summary>
        [Fact]
        public void LongLat_PoleAndEquatorConventions()
        {
            double deg = 180.0 / System.Math.PI;

            // +Y 是北极
            Assert.Equal(+90.0, SurfaceFrame.EciToLongLat(new DVector3(0, R, 0), Omega, 0.0).latRad * deg, 6);
            Assert.Equal(-90.0, SurfaceFrame.EciToLongLat(new DVector3(0, -R, 0), Omega, 0.0).latRad * deg, 6);

            // 赤道面（xz）里的点纬度是 0
            Assert.Equal(0.0, SurfaceFrame.EciToLongLat(new DVector3(R, 0, 0), Omega, 0.0).latRad * deg, 6);
            Assert.Equal(0.0, SurfaceFrame.EciToLongLat(new DVector3(0, 0, R), Omega, 0.0).latRad * deg, 6);
            Assert.Equal(0.0, SurfaceFrame.EciToLongLat(new DVector3(0, 0, -R), Omega, 0.0).latRad * deg, 6);
        }

        /// <summary>
        /// `SurfaceVelocity` 的两个**可心算**性质：
        /// ① 它必须**垂直于极轴**（自转速度没有南北分量）；
        /// ② 模长 = `ω·R·cosφ`（赤道上最大、极点为零）。
        /// 第 ② 条不是同义反复——它同时钉住了 `p` 的构造与 `v` 的构造是一致的。
        /// </summary>
        [Fact]
        public void SurfaceVelocity_IsHorizontal_AndHasCorrectMagnitude()
        {
            foreach (double lat in new[] { 0.0, 0.5, 1.2 })
            {
                (DVector3 pos, DVector3 vel) = SurfaceFrame.SurfaceState(R, lat, 0.3, Omega, 0.0);

                Assert.Equal(0.0, vel.y, 6); // 极轴 = +y ⇒ 自转速度无南北分量
                Assert.Equal(Omega * R * System.Math.Cos(lat), vel.magnitude, 6);

                // 与"由 p 直接算的自转切向"一致
                DVector3 direct = SurfaceFrame.SurfaceVelocity(pos, Omega);
                Assert.Equal(vel.x, direct.x, 6);
                Assert.Equal(vel.z, direct.z, 6);
            }
        }

        /// <summary>
        /// ⭐ **顺行方向的推论**（本 mod 特有，见 `SurfaceFrame` 的类注释）：
        /// 在 `p = +x̂` 处**东向**速度是 `+ẑ`，于是 `h = p×v ∥ −Ŷ`
        /// ⇒ **顺行赤道轨的 `inclination` 是 180°**。这条把"约定"与"元素口径"钉在一起。
        /// </summary>
        [Fact]
        public void EastwardSurfaceVelocity_ImpliesProgradeHasHAlongMinusPole()
        {
            var p = new DVector3(R, 0, 0);
            DVector3 vEast = SurfaceFrame.SurfaceVelocity(p, Omega);
            Assert.True(vEast.z > 0.0, $"在 +x̂ 处东向应是 +ẑ，实得 {vEast}");

            // h = p × v 必须指向 −Ŷ（= −极轴）
            var h = new MechJebLib.Primitives.V3(
                p.y * vEast.z - p.z * vEast.y,
                p.z * vEast.x - p.x * vEast.z,
                p.x * vEast.y - p.y * vEast.x);
            Assert.Equal(-1.0, h.normalized.y, 9);
        }

        /// <summary>
        /// ⭐ **接地速度的口径**（2026-09-22，撞击事件判据的唯一速度口径）：<see cref="SurfaceFrame.GroundSpeed"/>
        /// 必须扣掉地表共转。
        ///
        /// 判据用**可心算的构造**：把船放在某条经线上、速度取"该点地表共转速度 + 纯径向下降 d"
        /// ⇒ 接地速度必须**恰好等于 d**，与纬度无关（`d = 0` 那行 = 停在地面上 ⇒ 0）。
        /// 这不是同义反复：它同时钉住"下降是径向的"与"共转按同一点算"。
        /// </summary>
        [Theory]
        [InlineData(0.0, 0.0)]      // 赤道：共转最快（523.6 m/s）；d=0 = 停在地面
        [InlineData(0.0, 150.0)]    // 赤道：垂直下降 150
        [InlineData(0.6, 150.0)]    // 中纬度
        [InlineData(1.4, 60.0)]     // 近极：共转几乎为 0 ⇒ 扣不扣都一样
        [InlineData(-1.0, 60.0)]    // 南半球（符号）
        public void GroundSpeed_CoRotatingDescent_EqualsDescentRate(double lat, double descent)
        {
            (DVector3 pos, DVector3 velSurface) = SurfaceFrame.SurfaceState(R, lat, 0.9, Omega, 12345.0);
            DVector3 vel = velSurface - pos.normalized * descent;   // 相对地表：纯垂直下降

            Assert.Equal(descent, SurfaceFrame.GroundSpeed(pos, vel, Omega), 6);
        }

        /// <summary>
        /// ⭐⭐ **陷阱本身**（这条测试存在的理由）：地球赤道地表共转 = ω·R ≈ **523.6 m/s**。
        /// 同一个"跟着地面一起垂直下降 60 m/s"的状态，**用惯性系速度**读出来是 ~527 m/s
        /// ⇒ 拿它跟 `MaxSafeLandingSpeedMps`（100）比，**任何**着陆都判坠毁。
        /// 两条断言必须一起看：前一条 = 正确口径，后两条 = 曾经的口径（以及它为什么不可能对）。
        /// </summary>
        [Fact]
        public void GroundSpeed_Equator_InertialReadingIsTheTrap()
        {
            const double descent = 60.0;
            (DVector3 pos, DVector3 velSurface) = SurfaceFrame.SurfaceState(R, 0.0, 0.9, Omega, 12345.0);
            DVector3 vel = velSurface - pos.normalized * descent;

            Assert.Equal(descent, SurfaceFrame.GroundSpeed(pos, vel, Omega), 6);

            // 惯性口径 = 共转 523.6 与下降 60 的合成（近似正交）⇒ 模长 ≈ 527
            Assert.True(vel.magnitude > 500.0,
                $"赤道惯性系速度应当 ≈527（共转 523.6 + 下降 {descent}），实得 {vel.magnitude:F1}");
            Assert.True(vel.magnitude > 5.0 * SpaceConstants.MaxSafeLandingSpeedMps,
                "旧口径下这个状态会被判**坠毁** —— 这正是阈值必须配『相对地表』口径的原因");
        }
    }
}
