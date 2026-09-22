using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **预对准可行性**的判据（2026-09-19 真飞验收的最后一环）。
    ///
    /// 锚点就是那次实测：**排队离节点 60 s、燃烧 92 s** ⇒ 可用对准时间只有
    /// `60 − 46 = 14 s` ⇒ 5°/s 最多转 70°，而需要 177° ⇒ 带 107° 夹角点火。
    /// </summary>
    public class AlignFeasibilityTests
    {
        [Fact]
        public void TheLiveRunCase_IsInfeasible_AndSaysWhy()
        {
            // 实测参数：窗口 60 s、燃烧 92 s、需要转 177°、限速 5°/s
            bool ok = AlignFeasibility.IsFeasible(177.4, 5.0, 60.0, 92.0, out string why);

            Assert.False(ok, "物理上不可能对准的情形必须判不可行");
            Assert.Contains("预对准时间不足", why);
            // 可用 14 s × 5°/s = 70° ⇒ 点火时约剩 107°（与实测一致）
            Assert.True(why.Contains("107") || why.Contains("106"), "理由里应给出「点火时约剩多少度」：" + why);
        }

        [Fact]
        public void UsableTimeIsWindowMinusHalfBurn()
        {
            Assert.Equal(14.0, AlignFeasibility.UsableAlignSeconds(60.0, 92.0), 6);
            Assert.Equal(-10.0, AlignFeasibility.UsableAlignSeconds(50.0, 120.0), 6); // 窗口还没开就该点火
        }

        [Fact]
        public void EnoughTimeIsFeasible()
        {
            // 300 s 窗口、92 s 燃烧 ⇒ 可用 254 s ⇒ 转 177° 只要 35 s ✓
            Assert.True(AlignFeasibility.IsFeasible(177.4, 5.0, 300.0, 92.0, out string why), why);
            Assert.Equal("", why);
        }

        [Fact]
        public void ZeroRateIsNeverFeasible_UnlessNoRotationNeeded()
        {
            Assert.False(AlignFeasibility.IsFeasible(10.0, 0.0, 1000.0, 0.0, out _), "限速为 0 转不动");
            Assert.True(AlignFeasibility.IsFeasible(0.0, 5.0, 10.0, 92.0, out _), "本来就不需要转 ⇒ 可行");
        }
    }
}
