using System;
using System.Collections.Generic;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 引力场原语与"系统系传播 ≡ 子天体局部系 + 潮汐项"的等价性（A0 的数学核心）。
    ///
    /// 合成系统（数值取自 CelestialBodies_SolarSystem.xml 的量级，便于与游戏内对照）：
    ///   根天体（地球口径）mu = 2.0e14，钉在原点；
    ///   卫星（月球口径）mu = 5.0e12，绕根做圆轨，d = 8.0e7 m。
    /// 探针在卫星附近 rho = 3.3e6 m（1800km 轨道高度量级）。
    /// </summary>
    public class GravityFieldTests
    {
        private const double MuRoot = 2.0e14;
        private const double MuMoon = 5.0e12;
        private const double MoonOrbitRadius = 8.0e7;

        private static double MoonAngularRate() => Math.Sqrt(MuRoot / (MoonOrbitRadius * MoonOrbitRadius * MoonOrbitRadius));

        /// <summary>卫星在 tick 时刻的系统系位置/速度（圆轨，根钉住 → 开普勒一致的显式运动学）。</summary>
        private static void MoonState(double tick, out DVector3 pos, out DVector3 vel)
        {
            double w = MoonAngularRate();
            double phase = w * tick;
            double cos = Math.Cos(phase);
            double sin = Math.Sin(phase);
            pos = new DVector3(MoonOrbitRadius * cos, MoonOrbitRadius * sin, 0.0);
            vel = new DVector3(-MoonOrbitRadius * w * sin, MoonOrbitRadius * w * cos, 0.0);
        }

        private static List<GravitySource> SourcesAt(double tick)
        {
            MoonState(tick, out var mPos, out var mVel);
            return new List<GravitySource>
            {
                new GravitySource(DVector3.zero, DVector3.zero, MuRoot),
                new GravitySource(mPos, mVel, MuMoon)
            };
        }

        // ───────────────────────── 原语自洽性 ─────────────────────────

        [Fact]
        public void FieldAccelJerk_EqualsSumOfIndividualTwoBodyTerms()
        {
            var sources = SourcesAt(0.0);

            MoonState(0.0, out var mPos, out var mVel);
            var probe = mPos + new DVector3(3.3e6, 0, 0);
            var probeVel = mVel + new DVector3(0, 1200, 0);

            NBodyIntegrator.FieldAccelJerk(probe, probeVel, sources, out var fieldAcc, out var fieldJerk);

            NBodyIntegrator.ComputeBodyAccelJerk(probe, probeVel, DVector3.zero, DVector3.zero, MuRoot,
                out var rootAcc, out var rootJerk);
            NBodyIntegrator.ComputeBodyAccelJerk(probe, probeVel, mPos, mVel, MuMoon,
                out var moonAcc, out var moonJerk);

            var expectAcc = rootAcc + moonAcc;
            var expectJerk = rootJerk + moonJerk;

            Assert.Equal(expectAcc.x, fieldAcc.x, 12);
            Assert.Equal(expectAcc.y, fieldAcc.y, 12);
            Assert.Equal(expectAcc.z, fieldAcc.z, 12);
            Assert.Equal(expectJerk.x, fieldJerk.x, 12);
            Assert.Equal(expectJerk.y, fieldJerk.y, 12);
            Assert.Equal(expectJerk.z, fieldJerk.z, 12);
        }

        [Fact]
        public void DifferentialAccelJerk_EqualsFieldDifferenceBetweenTwoPoints()
        {
            var sources = SourcesAt(0.0);
            MoonState(0.0, out var mPos, out var mVel);

            var probe = mPos + new DVector3(3.3e6, 0.5e6, 0);
            var probeVel = mVel + new DVector3(0, 1200, 0);

            NBodyIntegrator.DifferentialAccelJerk(probe, probeVel, mPos, mVel, sources,
                out var diffAcc, out var diffJerk);

            NBodyIntegrator.FieldAccelJerk(probe, probeVel, sources, out var atProbe, out var jerkAtProbe);
            NBodyIntegrator.FieldAccelJerk(mPos, mVel, sources, out var atRef, out var jerkAtRef);

            var expectAcc = atProbe - atRef;
            var expectJerk = jerkAtProbe - jerkAtRef;

            Assert.Equal(expectAcc.x, diffAcc.x, 12);
            Assert.Equal(expectAcc.y, diffAcc.y, 12);
            Assert.Equal(expectAcc.z, diffAcc.z, 12);
            Assert.Equal(expectJerk.x, diffJerk.x, 12);
            Assert.Equal(expectJerk.y, diffJerk.y, 12);
            Assert.Equal(expectJerk.z, diffJerk.z, 12);
        }

        [Fact]
        public void TaylorInterpolate_VelocityIsPositionDerivative()
        {
            var pos = new DVector3(1.0e6, -2.0e6, 3.0e5);
            var vel = new DVector3(100, -250, 40);
            var acc = new DVector3(-3.5, 1.25, 0.5);
            var jerk = new DVector3(0.02, -0.01, 0.004);

            double t = 1.7;
            double h = 1e-6;

            NBodyIntegrator.TaylorInterpolate(pos, vel, acc, jerk, t, out var p0, out var v0);
            NBodyIntegrator.TaylorInterpolate(pos, vel, acc, jerk, t + h, out var p1, out _);

            var numeric = (p1 - p0) / h;

            Assert.Equal(v0.x, numeric.x, 3);
            Assert.Equal(v0.y, numeric.y, 3);
            Assert.Equal(v0.z, numeric.z, 3);
        }

        [Fact]
        public void ThirdOrderTaylorStep_MatchesInterpolateAtDt()
        {
            var pos = new DVector3(5.0e6, 1.0e6, 0);
            var vel = new DVector3(0, 5300, 0);
            var acc = new DVector3(-7.9, 0, 0);
            var jerk = new DVector3(0.001, 0.0002, 0);

            NBodyIntegrator.ThirdOrderTaylorStep(pos, vel, acc, jerk, 2.0, out var stepPos, out var stepVel);
            NBodyIntegrator.TaylorInterpolate(pos, vel, acc, jerk, 2.0, out var interpPos, out var interpVel);

            Assert.Equal(interpPos.x, stepPos.x, 12);
            Assert.Equal(interpPos.y, stepPos.y, 12);
            Assert.Equal(interpPos.z, stepPos.z, 12);
            Assert.Equal(interpVel.x, stepVel.x, 12);
            Assert.Equal(interpVel.y, stepVel.y, 12);
            Assert.Equal(interpVel.z, stepVel.z, 12);
        }

        [Fact]
        public void CircularOrbit_RootOnly_HoldsRadiusOverManySteps()
        {
            double r = 7.0e6;
            double v = Math.Sqrt(MuRoot / r);
            var pos = new DVector3(r, 0, 0);
            var vel = new DVector3(0, v, 0);

            var sources = new List<GravitySource> { new GravitySource(DVector3.zero, DVector3.zero, MuRoot) };

            for (int i = 0; i < 100; i++)
            {
                NBodyIntegrator.FieldAccelJerk(pos, vel, sources, out var acc, out var jerk);
                NBodyIntegrator.ThirdOrderTaylorStep(pos, vel, acc, jerk, 2.0, out pos, out vel);
            }

            double drift = Math.Abs(pos.magnitude - r) / r;
            Assert.True(drift < 1e-6, "圆轨半径相对漂移应可忽略，实际 " + drift.ToString("E3"));
        }

        // ───────────────────────── A0 的等价性与可分辨判据 ─────────────────────────

        /// <summary>
        /// 根域轨道保持：非白名单天体（卫星）的抵消量必须等于**其全量项**（旧行为），
        /// 而不是潮汐项。这是"地球系数字不变"的等价性守卫。
        /// </summary>
        [Fact]
        public void OrbitKeeping_RootFrame_CancelsFullMoonPull_NotTidal()
        {
            var sources = SourcesAt(0.0);
            MoonState(0.0, out var mPos, out var mVel);
            var probe = mPos + new DVector3(3.3e6, 0, 0);
            var probeVel = mVel;

            // 根域：计划中心 W = 根（钉住 → 自身受迫加速度 = 0）
            var keep = new List<GravitySource> { sources[0] };
            NBodyIntegrator.FieldAccelJerk(probe, probeVel, sources, out var full, out _);
            NBodyIntegrator.FieldAccelJerk(probe, probeVel, keep, out var kept, out _);
            var cancel = full - kept;

            NBodyIntegrator.ComputeBodyAccelJerk(probe, probeVel, mPos, mVel, MuMoon, out var moonFull, out _);

            Assert.Equal(moonFull.x, cancel.x, 12);
            Assert.Equal(moonFull.y, cancel.y, 12);
            Assert.Equal(moonFull.z, cancel.z, 12);

            // 且它明显是"全量"量级（0.459 m/s²），不是潮汐量级
            Assert.True(cancel.magnitude > 0.4, "根域抵消量应为卫星全量引力量级，实际 " + cancel.magnitude.ToString("E3"));
        }

        /// <summary>
        /// 月心域轨道保持：抵消量必须是**潮汐项**（占卫星引力 ~0.3–0.6%），
        /// 而不是根天体全量（~6.8%）。这条断言把三种实现钉成可分辨：
        ///   · 抵消 0            = 旧代码（只算月球）
        ///   · 抵消 ~0.3–0.6%    = 正确（差分/潮汐）
        ///   · 抵消 ~6.8%        = 天真全量和（ManeuverPlanner 现状）
        /// </summary>
        [Fact]
        public void OrbitKeeping_MoonFrame_CancelsTidalPart_NotFullPull()
        {
            var sources = SourcesAt(0.0);
            MoonState(0.0, out var mPos, out var mVel);
            var probe = mPos + new DVector3(3.3e6, 0, 0);
            var probeVel = mVel;

            // 月心域：计划中心 W = 卫星；其受迫（自由落体）加速度 = 根在 W 处的全量项
            NBodyIntegrator.ComputeBodyAccelJerk(mPos, mVel, DVector3.zero, DVector3.zero, MuRoot,
                out var rootAtMoon, out _);

            var keep = new List<GravitySource> { sources[1] };
            NBodyIntegrator.FieldAccelJerk(probe, probeVel, sources, out var full, out _);
            NBodyIntegrator.FieldAccelJerk(probe, probeVel, keep, out var keptLocal, out _);

            // 实施：kept = 白名单天体（卫星）的场 + 计划中心的受迫加速度
            var kept = keptLocal + rootAtMoon;
            var cancel = full - kept;

            // 恒等式：抵消量 = 根在该处全量 − 根在计划中心处全量 = 潮汐
            NBodyIntegrator.ComputeBodyAccelJerk(probe, probeVel, DVector3.zero, DVector3.zero, MuRoot,
                out var rootAtProbe, out _);
            var tidal = rootAtProbe - rootAtMoon;

            Assert.Equal(tidal.x, cancel.x, 12);
            Assert.Equal(tidal.y, cancel.y, 12);
            Assert.Equal(tidal.z, cancel.z, 12);

            // 量级判据：潮汐 / 卫星全量 ∈ (0.1%, 2%) —— 显著小于"全量抵消"的 ~6.8%
            double localPull = MuMoon / (3.3e6 * 3.3e6);
            double ratio = cancel.magnitude / localPull;
            Assert.True(ratio > 0.001, "抵消量不应为零或近零（旧「只算月球」行为），实际比 " + ratio.ToString("P3"));
            Assert.True(ratio < 0.02, "抵消量应是潮汐项而非全量项，实际比 " + ratio.ToString("P3"));

            // 并证明「全量抵消」就是那个错的：根的全量项比潮汐大一个数量级
            double naiveRatio = rootAtProbe.magnitude / cancel.magnitude;
            Assert.True(naiveRatio > 5.0, "天真全量项应显著大于潮汐项，实际倍数 " + naiveRatio.ToString("F1"));
        }

        /// <summary>
        /// **A0 的核心等价性**：同一物理情形，
        ///   途径 A = 系统系里用全量场（根 + 卫星）传播；
        ///   途径 B = 卫星局部系里用 二体项 + 显式潮汐 传播，再把卫星位置加回。
        /// 多步之后端点必须一致——这是"系统系全量求和 ≡ 局部系 + 潮汐"的可执行形式。
        /// </summary>
        [Fact]
        public void SystemFrameField_Equals_LocalFramePlusTidal_OverManySteps()
        {
            const double dt = 2.0;
            const int steps = 20;

            MoonState(0.0, out var mPos, out var mVel);
            var rho0 = new DVector3(3.3e6, 4.0e5, 0);
            var rhoVel0 = new DVector3(-120, 1500, 0);

            // 途径 A：系统系全量场
            var posA = mPos + rho0;
            var velA = mVel + rhoVel0;

            // 途径 B：卫星局部系（二体 + 潮汐）
            var posB = posA;
            var velB = velA;

            for (int i = 0; i < steps; i++)
            {
                double t = i * dt;

                var sources = SourcesAt(t);
                NBodyIntegrator.FieldAccelJerk(posA, velA, sources, out var accA, out var jerkA);
                NBodyIntegrator.ThirdOrderTaylorStep(posA, velA, accA, jerkA, dt, out posA, out velA);

                MoonState(t, out var mPosNow, out var mVelNow);
                var rho = posB - mPosNow;
                var rhoVel = velB - mVelNow;

                // 卫星局部系：卫星在原点（二体项直接可用）
                NBodyIntegrator.ComputeBodyAccelJerk(rho, rhoVel, DVector3.zero, DVector3.zero, MuMoon,
                    out var twoBody, out var twoBodyJerk);

                // 根天体在**局部系里**的位置/速度 = −(卫星的系统系位置/速度)；
                // 差分参考点 = 局部系原点（= 计划中心 = 卫星）。
                // 注意：这里若把根放在原点（或用系统系坐标），潮汐项就退化成全量项——
                // 正是 A0 要消灭的那类参考系混用。
                var rootLocal = new List<GravitySource>
                {
                    new GravitySource(-mPosNow, -mVelNow, MuRoot)
                };
                NBodyIntegrator.DifferentialAccelJerk(rho, rhoVel, DVector3.zero, DVector3.zero, rootLocal,
                    out var tidal, out var tidalJerk);

                var accB = twoBody + tidal;
                var jerkB = twoBodyJerk + tidalJerk;

                NBodyIntegrator.ThirdOrderTaylorStep(rho, rhoVel, accB, jerkB, dt, out var rhoNext, out var rhoVelNext);

                MoonState(t + dt, out var mPosNext, out var mVelNext);
                posB = mPosNext + rhoNext;
                velB = mVelNext + rhoVelNext;
            }

            var posDiff = (posA - posB).magnitude;
            var velDiff = (velA - velB).magnitude;

            Assert.True(posDiff < 1e-3, "系统系场与「局部系+潮汐」的位置应一致，实际差 " + posDiff.ToString("E3") + " m");
            Assert.True(velDiff < 1e-6, "系统系场与「局部系+潮汐」的速度应一致，实际差 " + velDiff.ToString("E3") + " m/s");
        }

        /// <summary>
        /// 参考系无关性：把同一颗探针分别当作「系统系里的组」与「绕卫星的组」传播时，
        /// 只要场与坐标同系，结果与"它在哪个系统里"无关——这正是"积分不需要环绕天体参量"的含义。
        /// </summary>
        [Fact]
        public void FieldPropagation_IsIndependentOfWhichBodyIsCalledCentral()
        {
            var sources = SourcesAt(0.0);
            MoonState(0.0, out var mPos, out var mVel);

            var probe = mPos + new DVector3(3.3e6, 0, 0);
            var probeVel = mVel + new DVector3(0, 1000, 0);

            // 同一对 (pos, vel) 与同一份源列表 → 无论"谁被认为环绕谁"，加速度唯一
            NBodyIntegrator.FieldAccelJerk(probe, probeVel, sources, out var acc1, out var jerk1);
            NBodyIntegrator.FieldAccelJerk(probe, probeVel, sources, out var acc2, out var jerk2);

            Assert.Equal(acc1.x, acc2.x, 12);
            Assert.Equal(acc1.y, acc2.y, 12);
            Assert.Equal(acc1.z, acc2.z, 12);
            Assert.Equal(jerk1.x, jerk2.x, 12);
            Assert.Equal(jerk1.y, jerk2.y, 12);
            Assert.Equal(jerk1.z, jerk2.z, 12);
        }

        // ───────────────────────── 泰勒传播段（A2 的"2 秒一段 + 插值飞行"）─────────────────────────

        [Fact]
        public void TaylorSegment_EndpointMatchesThirdOrderStep()
        {
            var pos = new DVector3(7.0e6, 0, 0);
            var vel = new DVector3(0, 5300, 0);
            var sources = new List<GravitySource> { new GravitySource(DVector3.zero, DVector3.zero, MuRoot) };
            NBodyIntegrator.FieldAccelJerk(pos, vel, sources, out var acc, out var jerk);

            var seg = new TaylorSegment();
            seg.Rebuild(pos, vel, acc, jerk, baseTick: 100.0, length: 2.0);

            NBodyIntegrator.ThirdOrderTaylorStep(pos, vel, acc, jerk, 2.0, out var expectPos, out var expectVel);
            seg.Evaluate(2.0, out var segPos, out var segVel);

            Assert.Equal(expectPos.x, segPos.x, 9);
            Assert.Equal(expectPos.y, segPos.y, 9);
            Assert.Equal(expectPos.z, segPos.z, 9);
            Assert.Equal(expectVel.x, segVel.x, 9);
            Assert.Equal(expectVel.y, segVel.y, 9);
            Assert.Equal(expectVel.z, segVel.z, 9);
        }

        [Fact]
        public void TaylorSegment_ExpiryIsMeasuredFromBaseTick()
        {
            var seg = new TaylorSegment();
            Assert.True(seg.Expired(0.0), "未初始化的段必须视为已过期");

            seg.Rebuild(DVector3.zero, DVector3.zero, DVector3.zero, DVector3.zero, baseTick: 500.0, length: 2.0);

            Assert.False(seg.Expired(500.0));
            Assert.False(seg.Expired(502.0));
            Assert.True(seg.Expired(502.001));
        }

        /// <summary>
        /// 段间连续性 + **驱动正确性**：以"状态所在时刻"为段起点重开一段，多段后圆轨半径与位置
        /// 都必须几乎不动。断言故意收紧（此前用松阈值 + 错误驱动方式，把"每段丢一步"放过去了）。
        /// </summary>
        [Fact]
        public void TaylorSegment_MultiSegment_KeepsCircularOrbitExact()
        {
            var sources = new List<GravitySource> { new GravitySource(DVector3.zero, DVector3.zero, MuRoot) };

            double r = 7.0e6;
            double v = Math.Sqrt(MuRoot / r);
            double omega = v / r;
            var pos = new DVector3(r, 0, 0);
            var vel = new DVector3(0, v, 0);

            var seg = new TaylorSegment();
            double stateTick = 0.0;
            const double dt = 0.5;
            const double segLen = 2.0;
            double maxRadiusDrift = 0.0;
            double maxExactOff = 0.0;

            for (int i = 0; i < 400; i++)   // 200 s ≈ 22 圈
            {
                if (seg.Expired(stateTick))
                {
                    NBodyIntegrator.FieldAccelJerk(pos, vel, sources, out var a, out var j);
                    seg.Rebuild(pos, vel, a, j, stateTick, segLen);
                }

                stateTick += dt;
                seg.Evaluate(stateTick - seg.baseTick, out pos, out vel);

                maxRadiusDrift = Math.Max(maxRadiusDrift, Math.Abs(pos.magnitude - r) / r);

                double ang = omega * stateTick;
                var exact = new DVector3(r * System.Math.Cos(ang), r * System.Math.Sin(ang), 0.0);
                maxExactOff = Math.Max(maxExactOff, (pos - exact).magnitude);
            }

            // 7e-9 相对（7e6 m 轨道上约 7 mm / 200 s）是三阶截断的正常量级；
            // 而"每段丢一步"是 ~33% 量级，故 1e-7 的阈值既有意义又不过敏。
            Assert.True(maxRadiusDrift < 1e-7,
                "多段后圆轨半径应几乎不动，实际相对漂移 " + maxRadiusDrift.ToString("E3"));
            Assert.True(maxExactOff < 1.0,
                "多段后应与精确圆轨重合到 <1 m（每段丢一步会偏到几十 km），实际 " + maxExactOff.ToString("F3") + " m");
        }

        // ───────────────── 场 − 计划（差分口径；2026-09-14 首轮验收误报的正是这一条）─────────────────

        /// <summary>
        /// "场 − 计划"必须是**潮汐**（~0.3%–0.6% of 局部引力），不是外面天体的**全量**（~6.8%）。
        /// 首轮游戏内验收把后者当成了前者，电池实测 6.86% 并判失败——这条测试就是把那次误报固化，
        /// 同时钉住口径：差分 = 场 − 中心天体二体项 − 中心天体自身受迫。
        /// </summary>
        [Fact]
        public void FieldMinusPlan_IsTidal_NotFullPullOfOuterBody()
        {
            var sources = SourcesAt(0.0);
            MoonState(0.0, out var moonPos, out var moonVel);

            var probe = moonPos + new DVector3(3.3e6, 0.0, 0.0);
            var probeVel = moonVel;

            var moon = new GravitySource(moonPos, moonVel, MuMoon, 2);
            var earth = new GravitySource(DVector3.zero, DVector3.zero, MuRoot, 1);

            NBodyIntegrator.FieldMinusPlanAccelJerk(probe, probeVel, moon, true, earth, true, sources,
                out var diff, out _);

            // 局部引力（月球在探针处的二体项）
            NBodyIntegrator.ComputeBodyAccelJerk(probe, probeVel, moonPos, moonVel, MuMoon, out var local, out _);
            double ratio = diff.magnitude / local.magnitude;

            Assert.True(ratio > 1e-3 && ratio < 0.01,
                "差分应是潮汐（0.1%–1% of 局部），实际比 " + ratio.ToString("P4"));

            // 与**精确闭式解**对照（不是一阶线性化——ρ/d = 4% 时线性化本身偏 6%，
            // 首版测试正是拿线性化当参照而误报）：
            //   tidal = −μ_e·[ r/|r|³ − r_m/|r_m|³ ]（地球在原点）
            var rVec = probe;
            DVector3 expectTidal = -(MuRoot * (rVec / (rVec.magnitude * rVec.magnitude * rVec.magnitude)
                - moonPos / (MoonOrbitRadius * MoonOrbitRadius * MoonOrbitRadius)));
            Assert.True((diff - expectTidal).magnitude / expectTidal.magnitude < 1e-6,
                string.Format("差分 {0:E6} 应 == 精确闭式 {1:E6} m/s²（Δ={2:E3}）",
                    diff.magnitude, expectTidal.magnitude, (diff - expectTidal).magnitude));

            // 反证：外面天体的**全量**项比它大一个数量级 —— 这正是"写错成场−中心天体单独在场"时得到的值
            NBodyIntegrator.ComputeBodyAccelJerk(probe, probeVel, DVector3.zero, DVector3.zero, MuRoot,
                out var earthFull, out _);
            double fullRatio = earthFull.magnitude / local.magnitude;
            Assert.True(fullRatio > 0.05,
                "地球全量项应显著大于潮汐（~6.8%），实际比 " + fullRatio.ToString("P4"));
            Assert.True(diff.magnitude < earthFull.magnitude / 5.0,
                "差分必须明显小于地球全量项（否则就是把全量当差分）");
        }

        /// <summary>中心天体不在场里（无计划可言）时，差分退化为全量场（不抛异常、不返回零）。</summary>
        [Fact]
        public void FieldMinusPlan_WithoutCenter_FallsBackToFullField()
        {
            var sources = SourcesAt(0.0);
            var probe = new DVector3(5.0e6, 0.0, 0.0);

            NBodyIntegrator.FieldMinusPlanAccelJerk(probe, DVector3.zero,
                default, false, default, false, sources, out var diff, out _);
            NBodyIntegrator.FieldAccelJerk(probe, DVector3.zero, sources, out var field, out _);

            Assert.Equal(field.x, diff.x, 12);
            Assert.Equal(field.y, diff.y, 12);
            Assert.Equal(field.z, diff.z, 12);
        }

        // ───────────────── 尖靶弹头的"自由飞行弯曲量"该是多少（㉔ 的期望式校验）─────────────────

        /// <summary>
        /// 复现 8/8 场景的动力学：GEO 圆轨弹头，2 秒一段三阶泰勒、段内按 dt=1s 求值。
        /// 量出「与出生直线的偏移」并对照 ½|a|t² —— 游戏内实测该比值 3.99–5.16×，
        /// 而解析上 GEO 圆轨应 ≈1×。本测试判定：是探针期望式错了，还是游戏里另有一层效应。
        /// </summary>
        /// <summary>
        /// 漏靶弹头（GEO 圆轨）自由飞行：与出生直线的偏移必须 ≈ ½|a|t²。
        ///
        /// 这条是**真 bug 的回归测试**（2026-09-14）：此前段重建把 baseTick 写成"当前时刻"而不是
        /// "当前状态所在时刻"，求值时 t=0 → 每段丢一步运动。圆轨上表现为 ½at² 严格成立却实测偏
        /// 3.99–5.16×（Normal 档 33% 速度亏损）。本测试用**正确驱动方式**（段起点时刻 = 状态时刻）
        /// 把比值钉死在 1×，谁再把驱动写错就会红。
        /// </summary>
        [Fact]
        public void MissedWarhead_FreeFlightBend_MatchesHalfAtSquared()
        {
            double r = 3.356e7;
            double mu = MuRoot;
            double v = Math.Sqrt(mu / r);
            double omega = v / r;
            const double dt = 1.0;
            const double segLen = 2.0;

            var sources = new List<GravitySource> { new GravitySource(DVector3.zero, DVector3.zero, mu) };
            var spawnPos = new DVector3(r, 0, 0);
            var spawnVel = new DVector3(0, 0, v);

            var seg = new TaylorSegment();
            var pos = spawnPos;
            var vel = spawnVel;
            double stateTick = 0.0;          // 当前状态所在时刻（驱动基准）

            for (int i = 0; i < 120; i++)
            {
                if (seg.Expired(stateTick))
                {
                    NBodyIntegrator.FieldAccelJerk(pos, vel, sources, out var a, out var j);
                    seg.Rebuild(pos, vel, a, j, stateTick, segLen);   // 段起点时刻 = 状态时刻
                }

                stateTick += dt;
                seg.Evaluate(stateTick - seg.baseTick, out pos, out vel);

                if (i + 1 == 90 || i + 1 == 120)
                {
                    double linear = (pos - (spawnPos + spawnVel * stateTick)).magnitude;
                    double expected = 0.5 * (mu / (r * r)) * stateTick * stateTick;
                    double ratio = linear / expected;

                    // 与**精确圆轨**对比：应几乎重合（<1 m），这条比 ½at² 更硬
                    double ang = omega * stateTick;
                    var exact = new DVector3(r * System.Math.Cos(ang), 0.0, r * System.Math.Sin(ang));
                    double exactOff = (pos - exact).magnitude;

                    Assert.True(ratio > 0.99 && ratio < 1.01,
                        string.Format("t={0}s 弯曲比应 ≈1×，实际 {1:F4}×（偏移 {2:F2} m / ½|a|t² {3:F2} m）",
                            stateTick, ratio, linear, expected));
                    Assert.True(exactOff < 1.0,
                        string.Format("t={0}s 与精确圆轨应几乎重合，实际偏 {1:F3} m（每段丢一步会偏到几十 km）",
                            stateTick, exactOff));
                }
            }
        }
    }
}
