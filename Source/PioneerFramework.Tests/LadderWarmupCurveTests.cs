using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **阶梯规划预热曲线**（2026-10-21，net10 口径）。
    ///
    /// 游戏内 MakePlan 实测 6.9–9.2 ms，而 net48 台架同几何 0.3–0.5 ms。头号嫌疑 = 分层编译
    /// （`MakePlan` 每组每 30–120 s 才调一次 ⇒ 可能常驻 tier0）。本用例在**真实运行时**（net10）
    /// 直接跑 `RendezvousLadder.Warmup`：**首调 5–9 ms、末调 0.3–0.5 ms ⇒ 假设成立**；
    /// 若首调就 <1 ms，则游戏里的 9 ms 另有原因（真实状态/几何），别把预热当结论。
    ///
    /// 只打读数；唯一断言是"预热不抛、返回曲线"。
    /// </summary>
    public class LadderWarmupCurveTests
    {
        private readonly ITestOutputHelper _out;
        public LadderWarmupCurveTests(ITestOutputHelper o) => _out = o;

        [Fact]
        public void Warmup_ReportsCurve_AndDoesNotThrow()
        {
            // 地球档 mu/半径（与游戏 `GetBodyGM(1)/GetBodySafeRadius(1)` 同量级）
            string curve = RendezvousLadder.Warmup(2.0e14, 5.0e6, 200);
            _out.WriteLine("[阶梯·预热·台架] " + curve);
            Assert.False(string.IsNullOrEmpty(curve), "预热返回空串（内部异常被兜住了）");
            Assert.Contains("Hohmann", curve);
            Assert.Contains("Phasing", curve);
        }
    }
}
