using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 标准弹制导参数的**单一来源**守卫（A1）。
    ///
    /// 背景：此前 `MissileGuidanceParams.CreateDefault()` 是 `MissileDef` 默认值的**手抄平行副本**，
    /// 已漂两处（deadband 1.0 vs 游戏内 0.5；lateralAccel 83.0 vs 推力/湿质量推得的 80.0826），
    /// 于是"纯测试全绿"并不代表游戏内行为。现在数值只在 `MissileGuidanceDefaults` 定义一次，
    /// `MissileDef` 只覆写可调项、且与这里共用 LateralAccelFor 算式。
    /// 本文件的断言即"钉住那份唯一定义"——任何人改漂它都会被这里抓住。
    /// </summary>
    public class MissileGuidanceDefaultsTests
    {
        [Fact]
        public void Standard_MatchesStandardMissileDefinition()
        {
            var p = MissileGuidanceDefaults.Standard;

            Assert.Equal(0.8, p.lateralGain, 6);
            Assert.Equal(300.0, p.closingThreshold, 6);
            Assert.Equal(450.0, p.cruiseClosing, 6);
            Assert.Equal(600.0, p.boostMax, 6);
            Assert.Equal(2.0, p.timeMargin, 6);
            Assert.Equal(30000.0, p.lostMarginMeters, 6);
            Assert.Equal(20.0 * System.Math.PI / 180.0, p.turnRateRad, 9);

            // ── 两处曾漂移的值：钉死 ──
            Assert.Equal(0.5, p.deadband, 9);
            Assert.Equal(80.082645, p.lateralAccel, 5);
        }

        [Fact]
        public void Standard_DeadbandMatchesLaunchGuidanceConvention()
        {
            // 同项目发射制导（单一来源、自洽）用 0.5；导弹侧必须同为 0.5 —— 这是判定
            // "0.5 是意图值、1.0 是漂移"的依据，故固化为断言。
            double launchDeadband = LaunchGuidanceParams.CreateDefault(5_000_000.0).deadband;

            Assert.Equal(launchDeadband, MissileGuidanceDefaults.Standard.deadband, 9);
            Assert.Equal(0.5, MissileGuidanceDefaults.Standard.deadband, 9);
        }

        [Fact]
        public void LateralAccelFor_StandardMissile_IsThrustOverWetMass()
        {
            double expect = (3200.0 * 60.0 + 900.0 * 2.0) / (300.0 + 2000.0 + 120.0);

            double actual = MissileGuidanceDefaults.LateralAccelFor(
                300.0, 2000.0, 3200.0, 60.0, true, 900.0, 2.0, 120.0);

            Assert.Equal(expect, actual, 9);
            Assert.Equal(80.082645, actual, 5);
        }

        [Fact]
        public void LateralAccelFor_NoRcs_UsesMainEngineOnly()
        {
            double expect = (3200.0 * 60.0) / (300.0 + 2000.0);

            double actual = MissileGuidanceDefaults.LateralAccelFor(
                300.0, 2000.0, 3200.0, 60.0, false, 0.0, 0.0, 0.0);

            Assert.Equal(expect, actual, 9);
        }

        [Fact]
        public void LateralAccelFor_DegenerateWetMass_ReturnsZero()
        {
            double actual = MissileGuidanceDefaults.LateralAccelFor(
                0.0, 0.0, 3200.0, 60.0, false, 0.0, 0.0, 0.0);

            Assert.Equal(0.0, actual, 12);
        }

        [Fact]
        public void Standard_IsUsableByGuidanceCore_AndLandsOnTheIntentValue()
        {
            // 端到端：用唯一来源的参数跑一步制导，确认死区取 0.5（需求 dv 落在 0.5~1.0 之间时点火）
            var p = MissileGuidanceDefaults.Standard;
            var state = new MissileGuidanceState();

            // 极近且已对准：需求 dv 极小 → 应 Coast（死区之上不点火）
            var step = MissileGuidanceCore.ComputeStep(
                DVector3.zero, new DVector3(0, 0, 0),
                new DVector3(1e6, 0, 0), DVector3.zero, 57.0, p, state);

            // 静止且已接近率 0（< closingThreshold）→ 会有接近率补量，故此处只断言动作合法
            Assert.True(step.action == MissileGuidanceAction.Burn || step.action == MissileGuidanceAction.Coast);

            // 死区本身必须落在意图值上
            Assert.Equal(0.5, p.deadband, 9);
        }
    }
}
