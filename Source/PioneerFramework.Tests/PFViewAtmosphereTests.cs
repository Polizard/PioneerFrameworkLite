using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 大气表现（天空 α / 壳半径 / 日照因子）的**纯核心**钉子（2026-10-10 用户三问三答）。
    ///
    /// 这几条钉的是"数值口径"，不是"好不好看"：α 与阻力**共用**同一条 ρ(h)、缺席即零（月球没有蓝天空）、
    /// 夜侧不蓝、壳半径 = 1 + 厚度/2/半径。观感只能进游戏看（本文件不声称）。
    /// </summary>
    public class PFViewAtmosphereTests
    {
        // 地球：厚度 100 km · ρ₀ 1.2 · H 8 km
        const double Thickness = 100000.0;
        const double Rho0 = 1.2;
        const double ScaleH = 8000.0;

        [Fact]
        public void SkyAlpha_GroundIsFull_AndFollowsTheSameRhoAsDrag()
        {
            Assert.Equal(1f, PFViewAtmosphere.SkyAlpha(0.0, Thickness, Rho0, ScaleH, 1f));
            // 与 ρ/ρ₀ = exp(−h/H) 逐点一致（这条"同一出处"是刻意钉的：天空不许另画一条曲线）
            Assert.True(Mathf.Abs(PFViewAtmosphere.SkyAlpha(8000.0, Thickness, Rho0, ScaleH, 1f) - 0.3679f) < 1e-4f);
            Assert.True(Mathf.Abs(PFViewAtmosphere.SkyAlpha(24000.0, Thickness, Rho0, ScaleH, 1f) - 0.0498f) < 1e-4f);
            // 壳所在的高度（厚度一半 = 50 km）：天空基本黑掉（两条口径天然咬合）
            Assert.True(PFViewAtmosphere.SkyAlpha(50000.0, Thickness, Rho0, ScaleH, 1f) < 0.01f);
            // 厚度顶：彻底黑
            Assert.True(PFViewAtmosphere.SkyAlpha(100000.0, Thickness, Rho0, ScaleH, 1f) < 0.0001f);
        }

        [Fact]
        public void SkyAlpha_IsMonotone_AndClampedBelowGround()
        {
            float prev = 2f;
            for (int km = 0; km <= 120; km += 4)
            {
                float a = PFViewAtmosphere.SkyAlpha(km * 1000.0, Thickness, Rho0, ScaleH, 1f);
                Assert.True(a <= prev + 1e-6f, string.Format("α 应随高度单调不增：{0} km → {1}", km, a));
                Assert.True(a >= 0f && a <= 1f, "α 必须夹在 [0,1]");
                prev = a;
            }
            // 贴地/钻到地下都按 h = 0 处理（最浓），不许出现 >1 或负
            Assert.Equal(1f, PFViewAtmosphere.SkyAlpha(-5000.0, Thickness, Rho0, ScaleH, 1f));
        }

        [Fact]
        public void SkyAlpha_NightSideIsZero_AndSunlitScalesIt()
        {
            Assert.Equal(0f, PFViewAtmosphere.SkyAlpha(0.0, Thickness, Rho0, ScaleH, 0f));
            Assert.Equal(0f, PFViewAtmosphere.SkyAlpha(30000.0, Thickness, Rho0, ScaleH, 0f));
            // 日照因子是乘性的 ⇒ 半照 = 半浓
            Assert.True(Mathf.Abs(PFViewAtmosphere.SkyAlpha(8000.0, Thickness, Rho0, ScaleH, 0.5f) - 0.3679f * 0.5f) < 1e-4f);
        }

        [Fact]
        public void SkyAlpha_MissingAtmosphereIsZero()
        {
            // 月球：厚度 0 ⇒ 没有蓝天空（与 AtmosphereModel 的"缺席即零"同一条）
            Assert.Equal(0f, PFViewAtmosphere.SkyAlpha(0.0, 0.0, 1.2, 8000.0, 1f));
            // ρ₀/H 未定（[补2] 的惰性口径）同样不产生天空
            Assert.Equal(0f, PFViewAtmosphere.SkyAlpha(0.0, Thickness, 0.0, ScaleH, 1f));
            Assert.Equal(0f, PFViewAtmosphere.SkyAlpha(0.0, Thickness, Rho0, 0.0, 1f));
        }

        [Fact]
        public void ShellRadiusFactor_IsOnePlusHalfThicknessOverRadius()
        {
            // 地球：1 + 50 km / 5000 km = 1.01
            Assert.True(Mathf.Abs(PFViewAtmosphere.ShellRadiusFactor(5000000.0, 100000.0) - 1.01f) < 1e-6f);
            // 无大气 ⇒ 不发壳（比值 1，画出来与星球重合，等于不画）
            Assert.Equal(1f, PFViewAtmosphere.ShellRadiusFactor(5000000.0, 0.0));
            Assert.Equal(1f, PFViewAtmosphere.ShellRadiusFactor(0.0, 100000.0));
            // 单调：气越厚壳越外面
            float thin = PFViewAtmosphere.ShellRadiusFactor(5000000.0, 60000.0);
            float thick = PFViewAtmosphere.ShellRadiusFactor(5000000.0, 200000.0);
            Assert.True(thick > thin && thin > 1f);
        }

        [Fact]
        public void ShellHidden_WhenWatchedGroupIsBelowIt()
        {
            // 用户口径：观察的 Group 位于薄层下 ⇒ 不画壳（只留天空盒按高度变色）
            Assert.True(PFViewAtmosphere.ShellHiddenForGroupAltitude(0.0, Thickness), "贴地 ⇒ 藏");
            Assert.True(PFViewAtmosphere.ShellHiddenForGroupAltitude(49000.0, Thickness), "49 km < 壳(50 km) ⇒ 藏");
            Assert.False(PFViewAtmosphere.ShellHiddenForGroupAltitude(51000.0, Thickness), "51 km > 壳 ⇒ 画");
            Assert.False(PFViewAtmosphere.ShellHiddenForGroupAltitude(200000.0, Thickness), "远在太空 ⇒ 画");
            Assert.False(PFViewAtmosphere.ShellHiddenForGroupAltitude(0.0, 0.0), "没大气 ⇒ 没有壳可藏");
        }

        [Fact]
        public void DepthResolution_ExplainsTheShellFlicker_AndTheFix()
        {
            // 2026-10-10 用户报"外面那层壳一闪一闪" ⇒ 账在这里（用户给的取景下限 300 m = camDistance 30 单位）：
            //   δz = d²/(near·2²⁴)；行星在 5e5 单位（= 5000 km）处；壳离地 50 km = 5000 单位。
            const float planetDist = 5e5f;      // 单位（= 5000 km）
            const float shellOffset = 5000f;    // 单位（= 厚度/2 = 50 km）

            float deltaZOld = PFViewAtmosphere.DepthResolution(planetDist, 30f * 0.05f);   // 老：near = camDistance×0.05 = 1.5
            float deltaZNew = PFViewAtmosphere.DepthResolution(planetDist, 30f * 0.35f);   // 新：near = camDistance×0.35 = 10.5

            Assert.True(shellOffset / deltaZOld < 1f,
                string.Format("老系数下余量应当 < 1（壳与地表抢深度 = 闪）：余量 ×{0:F1}", shellOffset / deltaZOld));
            Assert.True(shellOffset / deltaZNew > 3f,
                string.Format("新系数下余量应当 > 3（够稳）：余量 ×{0:F1}", shellOffset / deltaZNew));
            // 数值量级：老 δz ≈ 10 万单位（100 km，比壳还高）→ 新 δz ≈ 1.4 万单位（14 km）
            Assert.True(deltaZOld > shellOffset * 1.5f && deltaZNew < shellOffset * 0.5f);
            // 看行星那档（near 被 Clamp 到 5000）应完全够用
            Assert.True(PFViewAtmosphere.DepthResolution(planetDist, 5000f) < 40f, "看行星时 δz 应只有几十米级（单位）");
        }

        [Fact]
        public void SunlitFactor_RampsAcrossTwilight_AndDiesOnTheNightSide()
        {
            Vector3 up = new Vector3(0f, 1f, 0f);
            Assert.Equal(1f, PFViewAtmosphere.SunlitFactor(up, up));                       // 正对太阳
            Assert.Equal(0f, PFViewAtmosphere.SunlitFactor(up, -up));                      // 背光
            Assert.Equal(0f, PFViewAtmosphere.SunlitFactor(up, Vector3.right));            // 恰好切于晨昏线
            float partial = PFViewAtmosphere.SunlitFactor(up, new Vector3(1f, PFViewAtmosphere.TwilightDot * 0.5f, 0f));
            Assert.True(partial > 0f && partial < 1f, "晨昏线附近必须给渐变（免得昼夜硬切换）：" + partial);
            Assert.True(partial < 0.6f, "渐变中点应约一半： " + partial);
            Assert.Equal(0f, PFViewAtmosphere.SunlitFactor(Vector3.zero, up));             // 退化输入不许给 NaN/正数
        }
    }
}
