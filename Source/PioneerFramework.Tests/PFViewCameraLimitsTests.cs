using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 取景限制的**纯核心**钉子（2026-10-10 用户口径：看向 Group 时"平移 ≤ 2km · 拉远 ≤ 3km · 靠近 ≥ 300m"）。
    ///
    /// 这几条把**用户给的三个数**变成可机械复查的判据（含"默认距离必须落在区间内"这条自洽性），
    /// 而不是只写在注释里靠人记。观感/手感只能进游戏试（本文件不声称）。
    /// </summary>
    public class PFViewCameraLimitsTests
    {
        const float M2V = 0.1f;      // PFView 的 MetersToView（1 m = 0.1 view 单位）

        [Fact]
        public void OrbitDistance_GroupFraming_IsClampedTo300m_3km()
        {
            // 太近 → 顶到 300 m
            Assert.Equal(PFViewCameraLimits.GroupOrbitMinMeters * M2V,
                PFViewCameraLimits.ClampOrbitDistance(1f, M2V, true));
            // 太远 → 顶到 3 km
            Assert.Equal(PFViewCameraLimits.GroupOrbitMaxMeters * M2V,
                PFViewCameraLimits.ClampOrbitDistance(100000f, M2V, true));
            // 区间内 → 原样
            Assert.Equal(150f, PFViewCameraLimits.ClampOrbitDistance(150f, M2V, true));
            // 数值对得上：300 m = 30 单位、3 km = 300 单位
            Assert.Equal(30f, PFViewCameraLimits.GroupOrbitMinMeters * M2V, 3);
            Assert.Equal(300f, PFViewCameraLimits.GroupOrbitMaxMeters * M2V, 3);
        }

        [Fact]
        public void OrbitDistance_PlanetFraming_IsUntouched()
        {
            // 看行星那套是几十万~上亿单位 ⇒ 绝不能被这几条编队限制夹住
            Assert.Equal(500000f, PFViewCameraLimits.ClampOrbitDistance(500000f, M2V, false));
            Assert.Equal(1e8f, PFViewCameraLimits.ClampOrbitDistance(1e8f, M2V, false));
            Assert.Equal(0.5f, PFViewCameraLimits.ClampOrbitDistance(0.5f, M2V, false));
        }

        [Fact]
        public void PanOffset_GroupFraming_CapsAt2km_AndKeepsDirection()
        {
            // 沿 (1,0,1)/√2 方向推 10 km ⇒ 应缩到 2 km（= 200 单位），且方向不变
            Vector3 pushed = new Vector3(500f, 0f, 500f);           // |·| ≈ 707 单位 = 7.07 km
            Vector3 clamped = PFViewCameraLimits.ClampPanOffset(pushed, M2V, true);

            Assert.Equal(PFViewCameraLimits.GroupPanMaxMeters * M2V, clamped.magnitude, 3);
            Assert.True(Vector3.Dot(clamped.normalized, pushed.normalized) > 0.99999f, "夹取必须保留方向（不许把焦点拽回原点）");
        }

        [Fact]
        public void PanOffset_InsideLimit_AndDegenerate_AreUntouched()
        {
            var inside = new Vector3(50f, 20f, 0f);                 // |·| ≈ 53.9 单位 = 539 m < 2 km
            Assert.Equal(inside, PFViewCameraLimits.ClampPanOffset(inside, M2V, true));
            // 退化输入不许产生 NaN
            Vector3 degenerate = PFViewCameraLimits.ClampPanOffset(Vector3.zero, M2V, true);
            Assert.Equal(Vector3.zero, degenerate);
            // 看行星时平移不受这条限制（行星取景的平移另有一套尺度）
            var far = new Vector3(100000f, 0f, 0f);
            Assert.Equal(far, PFViewCameraLimits.ClampPanOffset(far, M2V, false));
        }

        [Fact]
        public void DefaultGroupDistance_LiesInsideTheLimits()
        {
            // 自洽性：默认取景距离必须落在 [靠近下限, 拉远上限] 内 —— 谁改一个常量忘了另一个，这条就红
            float init = PFViewCameraLimits.GroupInitOrbitMeters;
            Assert.True(init >= PFViewCameraLimits.GroupOrbitMinMeters,
                string.Format("默认距离 {0} m 比靠近下限 {1} m 还近", init, PFViewCameraLimits.GroupOrbitMinMeters));
            Assert.True(init <= PFViewCameraLimits.GroupOrbitMaxMeters,
                string.Format("默认距离 {0} m 超过拉远上限 {1} m", init, PFViewCameraLimits.GroupOrbitMaxMeters));
        }
    }
}
