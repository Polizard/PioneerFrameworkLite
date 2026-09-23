using System;
using PioneerFramework.Launch;   // GetSurfaceState.DefaultDayLengthTicks / RotationOmega
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 渲染换系口 `ViewFrame` 的数学钉桩（2026-09-23 三处渲染缺陷的唯一收敛点）。
    ///
    /// ## 为什么能 headless 断言
    /// 无 `Current.Game` 时 `BodySystemPosition` 返零、`RotationOmega` 落回 `DefaultDayLengthTicks`
    /// ⇒ 视图系退化为"根行星中心 + 默认自转"，**正是本类要钉的那层数学**。
    /// Unity 侧只碰**受管实现**（`Quaternion.AngleAxis` / 四元数乘向量）——`Quaternion.Euler` 一族带
    /// ECall，headless 会炸（`BoardingMathTests.cs:44` 已登记过这个坑）。
    ///
    /// ## 钉的四件事（每条都对应一次真实事故）
    /// 1. **t=0 恒等**（含 y≠0 的点，防"顺手转个 90°"）；
    /// 2. **钉在地表的东西不飞走**：固连点经 `FixedToEci`（物理侧）→ `ToViewUnity`（渲染侧）**必须回到原地**
    ///    —— 两处旋转方向写反任何一个都会让地表物体反向自转（`GetSurfaceState.cs:14` 的冻结约定）；
    /// 3. **方向必须相反**：t = 1/4 周期时 `+x̂ → −ẑ`（手算、不调被测物）⇒ 两边**同时**写反的巧合不可能通过；
    /// 4. **球半径缩放**：Unity 内置球网格**直径 1** ⇒ 缩放 = 2r·UnitsPerMeter（地球 200 / 月球 60）。
    ///    2026-09-23 用户实测"地球半径不对"就是这里少乘 2（画成了半径 50 而非 100）。
    /// </summary>
    public class ViewFrameTests
    {
        /// <summary>默认行星半径（米）——`SpaceConstants.PlanetRadiusMeters` 的运行值。</summary>
        const double R = 5_000_000.0;

        /// <summary>自转角速度（rad/s）——与 `GetSurfaceState.RotationOmega(null, ·)` 同源。</summary>
        static readonly double Omega = 2.0 * Math.PI / GetSurfaceState.DefaultDayLengthTicks;

        /// <summary>自转周期（秒）——不写死 60000，跟着唯一常量走。</summary>
        static readonly double Period = GetSurfaceState.DefaultDayLengthTicks;

        const float Tol = 0.01f;   // Unity 单位（1 单位 = 50 km）；星体尺度 100 ⇒ 相对 1e-4

        static void AssertNear(Vector3 expected, Vector3 actual)
        {
            Assert.True(Vector3.Distance(expected, actual) <= Tol,
                string.Format("期望 ({0}, {1}, {2})，实得 ({3}, {4}, {5})，差 {6}",
                    expected.x, expected.y, expected.z,
                    actual.x, actual.y, actual.z,
                    Vector3.Distance(expected, actual)));
        }

        /// <summary>任意固连方向（不引经纬度约定 ⇒ 断言不依赖被测链路之外的任何东西）。</summary>
        static DVector3 FixedRadiusVector()
        {
            var dir = new DVector3(0.4, 0.5, -0.3);
            return dir / dir.magnitude * R;
        }

        // ── ① t=0 恒等 ────────────────────────────────────────────────────────────
        [Fact]
        public void AtTickZero_ViewIsIdentity()
        {
            var pts = new[]
            {
                new Vector3(100f, 0f, 0f),
                new Vector3(0f, 40f, 0f),
                new Vector3(-37f, 12f, 84f),
            };
            foreach (var p in pts)
                AssertNear(p, ViewFrame.ToViewUnityFromUnity(p, 0.0));
        }

        // ── ② 固连点往返（物理侧 FixedToEci → 渲染侧 ToViewUnity）───────────────
        [Theory]
        [InlineData(0.0)]
        [InlineData(1.0)]
        [InlineData(15000.0)]
        [InlineData(30000.0)]
        [InlineData(45000.0)]
        [InlineData(59999.5)]
        [InlineData(123456.75)]     // 跨多个周期 + 非整 tick（double 时刻不得被 int 化）
        [InlineData(-45000.0)]      // 负 tick（读档回放口径）
        public void GroundFixedPoint_RoundTripsBackToItself(double tick)
        {
            var pFixedMeters = FixedRadiusVector();
            var eci = SurfaceFrame.FixedToEci(pFixedMeters, Omega, tick);

            var view = ViewFrame.ToViewUnity(eci, tick);
            var expected = (Vector3)(pFixedMeters * SpaceConstants.UnitsPerMeter);

            AssertNear(expected, view);
        }

        // ── ③ 方向相反（手算锚点；防"两边一起写反"）──────────────────────────────
        [Fact]
        public void AtQuarterPeriod_FixedXAxis_MapsToMinusZ()
        {
            // ω·t = π/2 ⇒ 固连 (R,0,0) 在视图系里应落在 −ẑ 方向（Ry 正向把 +x̂ 转到 −ẑ）
            var view = ViewFrame.ToViewUnity(new DVector3(R, 0.0, 0.0), Period / 4.0);

            AssertNear(new Vector3(0f, 0f, -100f), view);
            Assert.True(view.z < 0f, "自转方向写反了（读成 " + view.z + "）");
        }

        [Fact]
        public void AtQuarterPeriod_FixedXAxis_IsNotPlusZ()
        {
            // 与上一条配对的**反向**断言：万一将来有人把 ∓ 一起改反，这条会把矛盾钉出来
            var view = ViewFrame.ToViewUnity(new DVector3(R, 0.0, 0.0), Period / 4.0);
            Assert.True(Vector3.Distance(view, new Vector3(0f, 0f, 100f)) > 1f,
                "固连 +x̂ 在 1/4 周期处被转到了 +ẑ（方向写反）");
        }

        // ── ④ 算式只有一份（J2 回归闸）────────────────────────────────────────────
        [Theory]
        [InlineData(0.0)]
        [InlineData(7777.0)]
        public void MeterEntry_And_UnityEntry_UseTheSameTransform(double tick)
        {
            var p = new DVector3(1.2e6, 3.0e5, -8.4e6);

            var viaMeters = ViewFrame.ToViewUnity(p, tick);
            var viaUnity = ViewFrame.ToViewUnityFromUnity((Vector3)(p * SpaceConstants.UnitsPerMeter), tick);

            AssertNear(viaUnity, viaMeters);
        }

        // ── ⑤ 球网格直径 1 ⇒ 缩放 = 2r·UnitsPerMeter ─────────────────────────────
        [Fact]
        public void RadiusToSphereScale_NullMesh_DoublesTheRadius()
        {
            // 地球：半径 100 Unity 单位 ⇒ 缩放 200（少了这个 2 就是用户看到的"地球半径不对"）
            Assert.Equal(2.0 * SpaceConstants.PlanetRadiusUnity,
                ViewFrame.RadiusToSphereScale((float)SpaceConstants.PlanetRadiusMeters, null), 3);

            // 月球：半径 1.5e6 m = 30 Unity 单位 ⇒ 缩放 60（曾画成 15）
            Assert.Equal(60f, ViewFrame.RadiusToSphereScale(1_500_000f, null), 3);
        }

        [Theory]
        [InlineData(1.0)]
        [InlineData(1_500_000.0)]
        [InlineData(5_000_000.0)]
        public void RadiusToSphereScale_IsLinearInRadius(double radiusMeters)
        {
            float one = ViewFrame.RadiusToSphereScale((float)radiusMeters, null);
            float two = ViewFrame.RadiusToSphereScale((float)(radiusMeters * 2.0), null);
            Assert.Equal(one * 2f, two, 3);
            Assert.True(one > 0f, "缩放必须为正");
        }

        // ── ⑥ 无游戏时的兜底（不伪装成"看对了天体"）──────────────────────────────
        [Fact]
        public void WithoutGame_FallsBackToRootPlanet()
        {
            Assert.Equal(1, ViewFrame.RootPlanetId(null));
            Assert.Equal(1, ViewFrame.ViewedBodyId);          // 取不到 layer ⇒ 根行星，不抛
            Assert.Equal(0.0, ViewFrame.ViewedBodyRadiusUnity - (float)SpaceConstants.PlanetRadiusUnity, 3);
        }
    }
}
