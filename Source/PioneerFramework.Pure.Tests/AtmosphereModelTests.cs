using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **[A1] 密度提供者的纯测试**（计划稿 §2.1 判据：单调 / 边界连续 / ρ(100 km)/ρ₀ ≈ 0 / ρ≡0 分支）。
    ///
    /// ⚠ 用例参数（ρ₀/H）是**合成值**——[补2]（2026-10-06）裁定"数值不按地球取值、延后裁定"，
    /// 所以本文件测的是**模型**（64 点表 + 线性插值 + 惰性分支），不是任何一颗具体天体的数据。
    /// </summary>
    public class AtmosphereModelTests
    {
        private const double Rho0 = 1.2;            // 合成 ρ₀ (kg/m³)
        private const double ScaleHeight = 8500.0;  // 合成标尺高度 H (m)
        private const double Top = 100000.0;        // 与地球 Def 的 atmosphereThickness 同口径 (m)

        private static AtmosphereProfile Profile() => AtmosphereModel.Build(Rho0, ScaleHeight, Top);

        // ── ρ≡0 分支（惰性）──

        [Theory]
        [InlineData(0.0, 8500.0, 100000.0)]   // ρ₀ 未定（[补2] 的当前状态）
        [InlineData(1.2, 0.0, 100000.0)]      // H 未定
        [InlineData(1.2, 8500.0, 0.0)]        // 无大气（太阳/月球的 atmosphereThickness = 0）
        [InlineData(-1.0, -1.0, -1.0)]        // 全负（防御）
        public void Build_NonPositiveParams_IsInert(double rho0, double scaleHeight, double top)
        {
            var profile = AtmosphereModel.Build(rho0, scaleHeight, top);

            Assert.False(profile.IsActive);
            Assert.Equal(0.0, profile.Density(0.0));
            Assert.Equal(0.0, profile.Density(50_000.0));
            Assert.Equal(0.0, profile.Density(500_000.0));
        }

        [Fact]
        public void Build_ValidParams_IsActive()
        {
            Assert.True(Profile().IsActive);
        }

        [Fact]
        public void AnyAtmosphereActive_IsTrue_AfterBuildingAnActiveProfile()
        {
            // [A2-2] 热路径的"缺席即零"闸门：全库只有这一个 bool 表达"有没有活大气"
            // （值未定的现状 ⇒ false ⇒ 物理步一次静态读就跳过整个气动分支）。这里钉"建出活剖面后必为 true"。
            AtmosphereModel.Build(Rho0, ScaleHeight, Top);
            Assert.True(AtmosphereModel.AnyAtmosphereActive);
        }

        // ── [A6] 两条判据（语义不同，别合并）──

        [Fact]
        public void IsAerodynamicallySignificant_Thresholds()
        {
            // "气动不可忽略" ⟺ h < k·H（与 ρ/ρ₀ > e^−k 等价）——供**引力掩码闩锁的恢复**
            var p = Profile();
            const double k = 9.5;                 // 与 CelestialBodyDef 默认值一致
            double hK = ScaleHeight * k;          // 80.75 km（地球口径下 < 大气顶 100 km）

            Assert.True(p.IsAerodynamicallySignificant(0.0, k));
            Assert.True(p.IsAerodynamicallySignificant(hK - 1.0, k));
            Assert.False(p.IsAerodynamicallySignificant(hK, k));
            Assert.False(p.IsAerodynamicallySignificant(hK + 1.0, k));
            Assert.False(p.IsAerodynamicallySignificant(Top + 1.0, k));
        }

        [Fact]
        public void IsAerodynamicallySignificant_TopWinsOverLongScaleHeights()
        {
            // k 大到 k·H（170 km）> 大气顶：顶之上仍必须判"可忽略"（ρ 被硬截断成 0），顶之下判"不可忽略"
            var p = Profile();
            Assert.False(p.IsAerodynamicallySignificant(Top + 1.0, 20.0));
            Assert.True(p.IsAerodynamicallySignificant(Top - 1.0, 20.0));
        }

        [Fact]
        public void IsAerodynamicallySignificant_InertProfile_AlwaysFalse()
        {
            // [补2] 数值未定 ⇒ 整条规则休眠（恢复判据因此永远不会触发）
            var inert = AtmosphereModel.Build(0.0, ScaleHeight, Top);
            Assert.False(inert.IsAerodynamicallySignificant(0.0, 9.5));
        }

        [Fact]
        public void IsInsideAtmosphere_ShellBoundary_AndInert()
        {
            // 解析圆轨准入判据（[A6] 互斥）：壳内（ρ>0）即拒——与上面那条**不是同一条**
            var p = Profile();
            Assert.True(p.IsInsideAtmosphere(0.0));
            Assert.True(p.IsInsideAtmosphere(Top - 1.0));
            Assert.False(p.IsInsideAtmosphere(Top));
            Assert.False(p.IsInsideAtmosphere(Top + 1.0));
            Assert.False(AtmosphereModel.Build(0.0, ScaleHeight, Top).IsInsideAtmosphere(0.0));
        }

        // ── 边界 ──

        [Fact]
        public void Density_AtOrBelowSurface_IsRho0()
        {
            var p = Profile();
            Assert.Equal(Rho0, p.Density(0.0));      // 逐位：表第 0 点 = ρ₀·exp(0) = ρ₀
            Assert.Equal(Rho0, p.Density(-100.0));   // 地下钳位（不该出现，但答案必须是这个）
        }

        [Fact]
        public void Density_AboveTop_IsZero()
        {
            var p = Profile();
            Assert.Equal(0.0, p.Density(Top));            // 硬截断：恰在顶上也是 0
            Assert.Equal(0.0, p.Density(Top + 1.0));
            Assert.Equal(0.0, p.Density(3.0 * Top));
        }

        [Fact]
        public void Density_TopValue_IsNegligibleFractionOfRho0()
        {
            // 判据"ρ(100 km)/ρ₀ ≈ 0"：顶下 1 m 处的密度 ≲ 1e-5·ρ₀（H=8500 ⇒ ≈7.8e-6）
            var p = Profile();
            Assert.True(p.Density(Top - 1.0) / Rho0 <= 1e-5,
                $"ρ(top−1 m)/ρ₀ = {p.Density(Top - 1.0) / Rho0:E3}（应 ≲ 1e-5）");
        }

        // ── 单调 / 连续 / 精度 ──

        [Fact]
        public void Density_IsStrictlyMonotoneDecreasing_InAtmosphere()
        {
            var p = Profile();
            const int n = 2000;
            double prev = p.Density(0.0);
            for (int i = 1; i <= n; i++)
            {
                double h = Top * i / n;
                double cur = p.Density(h);
                Assert.True(cur < prev, $"h={h:F0} m 处不再严格下降：ρ={cur:E6} ≥ 前一点 {prev:E6}");
                prev = cur;
            }
        }

        [Fact]
        public void Density_AtSampleKnots_EqualsTableSamples()
        {
            // 64 个采样点上插值必须**恰好**落在采样值上（表值 = ρ₀·exp(−h/H)，同一算式 ⇒ 逐位一致）。
            // 注意 i = N−1（h = top）走硬截断返 0，不参与本比对。
            var p = Profile();
            double step = Top / (AtmosphereModel.TableSize - 1);
            for (int i = 0; i <= AtmosphereModel.TableSize - 2; i++)
            {
                double h = i * step;
                double expected = Rho0 * Math.Exp(-h / ScaleHeight);
                double got = p.Density(h);
                Assert.True(Math.Abs(got - expected) <= 1e-12 * expected,
                    $"采样点 i={i}（h={h:F0} m）：表值 {got:E15} vs 解析 {expected:E15}");
            }
        }

        [Fact]
        public void Density_AcrossKnots_IsContinuous()
        {
            // 边界连续：跨每个采样点（i=1..N−2）取 ±1 m，差必须 ≤ 斜率上界 2·δ·ρ₀/H（≈2.8e-4）。
            var p = Profile();
            double step = Top / (AtmosphereModel.TableSize - 1);
            double bound = 2.0 * 1.0 * (Rho0 / ScaleHeight) * 1.05;
            for (int i = 1; i <= AtmosphereModel.TableSize - 2; i++)
            {
                double h = i * step;
                double diff = Math.Abs(p.Density(h + 1.0) - p.Density(h - 1.0));
                Assert.True(diff <= bound, $"跨采样点 i={i}（h={h:F0} m）出现台阶：Δρ={diff:E3} > {bound:E3}");
            }
        }

        [Fact]
        public void Density_TracksExponential_WithinLinearInterpolationBound()
        {
            // 线性插值相对误差界 ≈ (Δh)²/(8H²) ≈ 0.44%（H=8500、top=100 km）⇒ 判据留到 ≤1%。
            var p = Profile();
            const int n = 997;   // 质数采样，避开采样点本身
            for (int i = 1; i < n; i++)
            {
                double h = Top * i / n;
                double exact = Rho0 * Math.Exp(-h / ScaleHeight);
                double rel = Math.Abs(p.Density(h) - exact) / exact;
                Assert.True(rel <= 0.01, $"h={h:F0} m：插值相对误差 {rel:P3} > 1%");
            }
        }

        // ── 数据无关性 ──

        [Fact]
        public void Density_ScalesLinearlyWithRho0()
        {
            var a = AtmosphereModel.Build(Rho0, ScaleHeight, Top);
            var b = AtmosphereModel.Build(10.0 * Rho0, ScaleHeight, Top);
            for (int i = 0; i <= 20; i++)
            {
                double h = Top * i / 20.0;
                if (h >= Top) continue;
                Assert.True(Math.Abs(b.Density(h) - 10.0 * a.Density(h)) <= 1e-9 * b.Density(h),
                    $"h={h:F0} m：ρ₀ ×10 未带来 ρ ×10");
            }
        }

        [Fact]
        public void Build_DifferentTop_TableSpansThatTop()
        {
            // 表必须铺满 Def 给的 top（不是写死 100 km）：60 km 顶的小天体同样单调 + 顶上为 0。
            var p = AtmosphereModel.Build(0.8, 6000.0, 60000.0);
            Assert.True(p.IsActive);
            Assert.True(p.Density(59999.0) > 0.0);
            Assert.Equal(0.0, p.Density(60000.0));
            Assert.Equal(0.0, p.Density(60001.0));
            double prev = p.Density(0.0);
            for (int i = 1; i <= 1000; i++)
            {
                double cur = p.Density(60000.0 * i / 1000);
                Assert.True(cur < prev);
                prev = cur;
            }
        }
    }
}
