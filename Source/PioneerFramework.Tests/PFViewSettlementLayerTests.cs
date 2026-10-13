using UnityEngine;
using RimWorld;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **PFView 定居点层（D-2）的纯核心钉子**（2026-10-12 用户裁定：「做：实时画一层（含夜灯）」）。
    ///
    /// 钉三件事：① 标记尺寸按**格**给的换算（与地表网格同一比例尺：1 世界单位 = 50 km）且退化输入不产生 0/NaN；
    /// ② 昼夜判据（背阳 = 夜面点灯；晨昏线上算昼——判据是**严格小于 0**）；③ 光点 alpha 轮廓（中心 1 / 边缘 0 / 单调不增）。
    /// 渲染那一半（矩阵、材质、朝向）只能在游戏里看，本文件不声称。
    /// </summary>
    public class PFViewSettlementLayerTests
    {
        [Fact]
        public void MarkerSize_ScalesLinearlyInTiles()
        {
            // 1 世界单位 = 50 km ⇒ 1 格 = 50,000 m
            Assert.Equal(50000f, PFViewSettlementLayer.MarkerSizeMeters(1f, 1f), 3);
            Assert.Equal(100000f, PFViewSettlementLayer.MarkerSizeMeters(1f, 2f), 3);   // 默认 2 格 = 100 km
            Assert.Equal(100000f, PFViewSettlementLayer.MarkerSizeMeters(2f, 1f), 3);   // 双倍格长 = 双倍尺寸
        }

        [Fact]
        public void MarkerSize_DegenerateInputs_AreClampedNotZeroOrNaN()
        {
            // 退化输入（层没数据 / 旋钮被填 0）：给一个**可见的最小量**，绝不返回 0/NaN（0 尺寸 = 画了看不见，本库栽过）
            float zeroTile = PFViewSettlementLayer.MarkerSizeMeters(0f);
            Assert.True(zeroTile > 0f && !float.IsNaN(zeroTile));
            float negative = PFViewSettlementLayer.MarkerSizeMeters(-5f, -1f);
            Assert.True(negative > 0f && !float.IsNaN(negative));
        }

        [Fact]
        public void MarkerSize_OnEarthCaliber_IsTwelveTilesAfterTheFourXEnlargement()
        {
            // 2026-10-12 两轮口径：先"弥漫到周围一圈格子"（3 格）⇒ 再看实测"覆盖范围直径放大 4 倍" ⇒ 12 格
            const float tileMeters = 50000f;                      // 1 世界单位 = 50 km
            Assert.Equal(12f * tileMeters, PFViewSettlementLayer.MarkerSizeMeters(1f), 3);
            // 地球档（半径 5,000 km）：12 格 = 600 km 直径 ⇒ 仍是直径的小零头（看得见、又不铺满球面）
            const float planetDiameterMeters = 2f * 5.0e6f;
            Assert.InRange(PFViewSettlementLayer.MarkerSizeMeters(1f) / planetDiameterMeters, 0.01f, 0.10f);
        }

        [Fact]
        public void IsNightSide_BackToSun_IsNight()
        {
            var normal = new Vector3(1f, 0f, 0f);
            Assert.True(PFViewSettlementLayer.IsNightSide(normal, new Vector3(-1f, 0f, 0f)));    // 背阳 ⇒ 夜
            Assert.False(PFViewSettlementLayer.IsNightSide(normal, new Vector3(1f, 0f, 0f)));    // 正对太阳 ⇒ 昼
            Assert.False(PFViewSettlementLayer.IsNightSide(normal, new Vector3(0f, 1f, 0f)));    // 晨昏线（垂直）⇒ 算昼
        }

        [Fact]
        public void TangentLift_ExceedsSphereSag_SoThePatchIsNeverBittenByTheGround()
        {
            // "贴地（切平面）但不被地表遮挡"（用户 2026-10-12）：抬升必须 > 球面在平片角点处的下沉 s²/(4R)。
            const float earthRadiusView = 5e5f;      // 地球档：R = 5e6 m × 0.1
            float quad = 12f * 50000f * 0.1f;        // 12 格（米）→ 视图单位
            float sag = PFViewSettlementLayer.TangentSag(quad, earthRadiusView);
            float lift = PFViewSettlementLayer.TangentLift(quad, earthRadiusView);
            Assert.True(lift > sag, "抬升必须大于球面下沉（否则平片被地表咬进去）");
            Assert.True(lift > earthRadiusView * 5e-4f, "抬升还必须带一份深度余量（不与球面共面打架）");
            Assert.True(sag < earthRadiusView * 0.01f, "下沉量本身必须是半径的小零头（否则该换球面片）");

            // 单调：片子越大、越要抬（同 R 下）
            Assert.True(PFViewSettlementLayer.TangentLift(quad * 2f, earthRadiusView) > lift);
            // 退化输入不炸
            Assert.True(PFViewSettlementLayer.TangentLift(0f, 0f) >= 0f);
        }

        [Fact]
        public void Hash01_IsDeterministic_AndInUnitRange()
        {
            // 确定性 = 贴图可复现（同一个存档每次生成同一张噪点图）；范围 [0,1)
            for (int i = 0; i < 64; i++)
            {
                float a = PFViewSettlementLayer.Hash01(i, i * 7 + 3, 20261012);
                float b = PFViewSettlementLayer.Hash01(i, i * 7 + 3, 20261012);
                Assert.Equal(a, b);
                Assert.InRange(a, 0f, 0.99999f);
            }
            // 不同格 ≠ 同值（哈希不是常量）
            Assert.NotEqual(PFViewSettlementLayer.Hash01(1, 2, 5), PFViewSettlementLayer.Hash01(3, 4, 5));
        }

        [Fact]
        public void CoverageFade_IsAFlatPlateauWithASharpRim()
        {
            // "弥漫"要的是**整块都亮**（平台），不是中间一坨（旧版 (1−r)² 的毛病）
            Assert.Equal(1f, PFViewSettlementLayer.CoverageFade(0f, 1f));
            Assert.Equal(1f, PFViewSettlementLayer.CoverageFade(0.7f, 1f));      // 平台
            Assert.Equal(0f, PFViewSettlementLayer.CoverageFade(1f, 1f));       // 到边归零
            Assert.Equal(0f, PFViewSettlementLayer.CoverageFade(1.2f, 1f));
            float prev = float.MaxValue;
            for (int i = 0; i <= 20; i++)
            {
                float a = PFViewSettlementLayer.CoverageFade(i / 20f, 1f);
                Assert.True(a <= prev + 1e-6f, "覆盖轮廓必须单调不增");
                prev = a;
            }
        }

        [Fact]
        public void Speck_IsSharp_NotABlur()
        {
            Assert.Equal(1f, PFViewSettlementLayer.SpeckAlpha(0f));
            Assert.Equal(1f, PFViewSettlementLayer.SpeckAlpha(0.85f));          // 内部实心
            Assert.True(PFViewSettlementLayer.SpeckAlpha(0.98f) < 0.25f);       // 边缘窄带内就收到接近 0
            Assert.Equal(0f, PFViewSettlementLayer.SpeckAlpha(1f));
            Assert.Equal(0f, PFViewSettlementLayer.SpeckAlpha(1.5f));
        }

        const int SampleN = 40;
        static int LitSamples(float coverage, float density, int seed = 7)
        {
            int lit = 0;
            for (int y = 0; y < SampleN; y++)
                for (int x = 0; x < SampleN; x++)
                {
                    float dx = -1f + 2f * x / (SampleN - 1f);
                    float dy = -1f + 2f * y / (SampleN - 1f);
                    if (PFViewSettlementLayer.SpeckleAlphaAt(dx, dy, seed, coverage, density) > 0.05f) lit++;
                }
            return lit;
        }

        [Fact]
        public void Speckle_IsBounded_Deterministic_AndZeroOutsideCoverage()
        {
            Assert.Equal(0f, PFViewSettlementLayer.SpeckleAlphaAt(1.2f, 0f, 7, 1f, 0.9f));   // 覆盖半径外 = 0
            Assert.Equal(0f, PFViewSettlementLayer.SpeckleAlphaAt(0f, -1.3f, 7, 1f, 0.9f));
            for (int i = -10; i <= 10; i++)
            {
                float dx = i / 10f;
                float a = PFViewSettlementLayer.SpeckleAlphaAt(dx, 0.1f, 7, 1f, 0.9f);
                Assert.InRange(a, 0f, 1f);
                Assert.Equal(a, PFViewSettlementLayer.SpeckleAlphaAt(dx, 0.1f, 7, 1f, 0.9f));   // 确定性
            }
        }

        // ── 派系科技差 = **覆盖面积 / 密度 / 亮度** 三轴（用户 2026-10-12 口径）──

        [Fact]
        public void Coverage_DrivesHowFarLightsSpread()
        {
            float covLow = PFViewSettlementLayer.CoverageFor(PFViewSettlementLayer.TechIntensity(TechLevel.Neolithic));
            float covHigh = PFViewSettlementLayer.CoverageFor(PFViewSettlementLayer.TechIntensity(TechLevel.Archotech));
            Assert.True(covHigh > covLow, "高科技的灯光覆盖面必须更大");
            Assert.True(LitSamples(covHigh, 0.8f) > LitSamples(covLow, 0.8f) * 1.3f,
                "高覆盖必须点亮**明显更多**的面积（低科技只在中心一小片）");
        }

        [Fact]
        public void Density_DrivesHowManyLights()
        {
            float denLow = PFViewSettlementLayer.DensityFor(PFViewSettlementLayer.TechIntensity(TechLevel.Neolithic));
            float denHigh = PFViewSettlementLayer.DensityFor(PFViewSettlementLayer.TechIntensity(TechLevel.Archotech));
            Assert.True(denHigh > denLow, "高科技的灯光密度必须更高");
            Assert.True(LitSamples(1f, denHigh) > LitSamples(1f, denLow) * 1.3f,
                "同覆盖下，高密度必须点亮明显更多的灯点");
        }

        [Fact]
        public void SpeckCells_AreCoarseEnoughToSurviveMinification()
        {
            // 旧版 8×8 在整球视角下每格只有 ~3 px ⇒ 糊成一个点（用户实测"还是只有一个圆点"）
            // ⇒ 细胞必须够粗（每格 ≥ ~5 px 才有结构）。这里把"粗"钉成一个可判定的界。
            Assert.InRange(PFViewSettlementLayer.SpeckCells, 4, 6);
        }

        [Fact]
        public void CoverageAndDensity_AreMonotoneInIntensity()
        {
            float prevC = -1f, prevD = -1f;
            var ordered = new[]
            {
                TechLevel.Animal, TechLevel.Neolithic, TechLevel.Medieval, TechLevel.Industrial,
                TechLevel.Spacer, TechLevel.Ultra, TechLevel.Archotech,
            };
            foreach (var tech in ordered)
            {
                float i = PFViewSettlementLayer.TechIntensity(tech);
                float c = PFViewSettlementLayer.CoverageFor(i);
                float d = PFViewSettlementLayer.DensityFor(i);
                Assert.InRange(c, 0.01f, 1f);
                Assert.InRange(d, 0.01f, 1f);
                Assert.True(c >= prevC && d >= prevD, $"科技档 {tech} 的覆盖/密度不得低于更低档");
                prevC = c; prevD = d;
            }
        }

        [Fact]
        public void TechIntensity_IsMonotoneNonDecreasing_AndClamped()
        {
            var ordered = new[]
            {
                TechLevel.Animal, TechLevel.Neolithic, TechLevel.Medieval, TechLevel.Industrial,
                TechLevel.Spacer, TechLevel.Ultra, TechLevel.Archotech,
            };
            float prev = -1f;
            foreach (var tech in ordered)
            {
                float v = PFViewSettlementLayer.TechIntensity(tech);
                Assert.InRange(v, 0f, 1f);
                Assert.True(v >= prev, $"科技档 {tech} 的灯光强度不得低于更低档（单调不减）");
                prev = v;
            }
            Assert.Equal(1f, PFViewSettlementLayer.TechIntensity(TechLevel.Archotech));
            Assert.InRange(PFViewSettlementLayer.TechIntensity(TechLevel.Undefined), 0.2f, 0.8f);   // 未定义给中间值
        }

        [Fact]
        public void LightAlpha_MapsIntensityIntoVisibleBand()
        {
            Assert.True(PFViewSettlementLayer.LightAlphaFor(0f) > 0f);                 // 再低的科技也看得见一点
            Assert.True(PFViewSettlementLayer.LightAlphaFor(1f) <= 1f);
            Assert.True(PFViewSettlementLayer.LightAlphaFor(1f) > PFViewSettlementLayer.LightAlphaFor(0f));
            Assert.Equal(PFViewSettlementLayer.LightAlphaFor(1f), PFViewSettlementLayer.LightAlphaFor(5f));  // 超界夹住
        }

        [Fact]
        public void GlowAlpha_CenterOne_EdgeZero_Monotonic()
        {
            Assert.Equal(1f, PFViewSettlementLayer.GlowAlphaAt(0f, 0f));
            Assert.Equal(0f, PFViewSettlementLayer.GlowAlphaAt(1f, 0f));
            Assert.Equal(0f, PFViewSettlementLayer.GlowAlphaAt(0f, -1f));
            Assert.Equal(0f, PFViewSettlementLayer.GlowAlphaAt(1.5f, 1.5f));   // 圆外一律 0

            float prev = float.MaxValue;
            for (int i = 0; i <= 20; i++)                                     // 沿半径单调不增（"一盏灯"的轮廓）
            {
                float a = PFViewSettlementLayer.GlowAlphaAt(i / 20f, 0f);
                Assert.True(a <= prev + 1e-6f, "alpha 沿半径必须单调不增");
                prev = a;
            }
        }
    }
}
