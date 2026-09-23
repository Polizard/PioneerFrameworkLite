using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// `LandingAcceptanceRunner.SpotOf` —— 从 `[Collision]` 日志行里抠**落点经纬度**那一段的纯字符串仪器。
    ///
    /// 为什么要单独测它：这条判据（"两个落点的经纬度不同"）在**游戏外没法跑**，
    /// 而它依赖的"从日志行里切开一段"恰恰是最容易悄悄坏掉的零件（切错位置就会恒为 "?" ⇒ 判据红成假红，
    /// 或者切出常量 ⇒ 判据恒真）。所以把这块**纯逻辑**单拎出来钉住：喂真实格式的行，断言抠出的正是经纬度。
    ///
    /// ⚠ 锚点用日志里的 **ASCII** 部分（`" at ("` / `)`），不用中文 —— 中文文案会改，`[Collision]` 行的骨架不会。
    /// 格式的真实来源（两处，都在这两个分支里）：
    /// `SpaceGameComp.CheckPlanetCollision` 的
    /// `"[Collision] Group {0} LANDED on {1} at ({2}) — ground-relative touchdown speed {3:F1} m/s."` 与 CRASHED 同构。
    /// </summary>
    public class LandingSpotParsingTests
    {
        [Fact]
        public void LandedLine_ExtractsSpot()
        {
            const string line =
                "[Collision] Group 42 LANDED on Earth at (北纬 0.0° 东经 0.0°) — ground-relative touchdown speed 60.1 m/s.";
            Assert.Equal("北纬 0.0° 东经 0.0°", LandingAcceptanceRunner.SpotOf(line));
        }

        [Fact]
        public void CrashedLine_ExtractsSpot()
        {
            const string line =
                "[Collision] Group 43 CRASHED into Earth at (北纬 30.0° 东经 120.0°) — ground-relative impact speed 305.0 m/s (max safe 100 m/s). Entities destroyed.";
            Assert.Equal("北纬 30.0° 东经 120.0°", LandingAcceptanceRunner.SpotOf(line));
        }

        [Fact]
        public void TwoDifferentSpots_AreDifferentStrings()
        {
            // 判据本体的最小复现：两条**同格式**的行，抠出来的经纬度必须不同（这就是那条判据要看的东西）
            const string a = "[Collision] Group 1 LANDED on Earth at (北纬 0.0° 东经 0.0°) — x";
            const string b = "[Collision] Group 2 CRASHED into Earth at (北纬 30.0° 东经 120.0°) — x";
            Assert.NotEqual(LandingAcceptanceRunner.SpotOf(a), LandingAcceptanceRunner.SpotOf(b));
        }

        [Fact]
        public void MissingAnchor_YieldsQuestionMark_NotEmpty()
        {
            // 抠不出必须是 "?"（判据会红）—— 不能是 ""（空串会让"两条不同"之类的比较静默成立）
            Assert.Equal("?", LandingAcceptanceRunner.SpotOf("[Collision] 格式变了没有锚点"));
            Assert.Equal("?", LandingAcceptanceRunner.SpotOf("at (缺右括号"));
            Assert.Equal("?", LandingAcceptanceRunner.SpotOf(null));
        }
    }
}
