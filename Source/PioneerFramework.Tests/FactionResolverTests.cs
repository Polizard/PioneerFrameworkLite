using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// `FactionResolver` 里**能 headless 断言的**那部分（[V12] 阵营解析）。
    ///
    /// ## ⚠ 边界先说清楚（"没法探"是合法结论，但要显式登记）
    /// `FactionResolver` 的**主路径测不了**，两条硬边界（2026-09-23 实测）：
    /// ① **`Verse.Log.Warning` 在测试宿主里必抛 ECall**（`UnityEngine.StackTraceUtility.ExtractStackTrace`
    ///    是引擎外部调用 —— 与 `Quaternion.AngleAxis` 同族）⇒ **凡是要打日志的生产路径都驱动不了**
    ///    （`IsHostile` 的解析失败分支就落在这里）；
    /// ② **`Find.FactionManager` 自己抛 `NullReferenceException`**（无游戏时它内部解引用 `Current.Game`，
    ///    **不是**返回 null）⇒ `Find.FactionManager?.AllFactions` 这种 `?.` 写法在无游戏下**不成立**，
    ///    连 `fm == null` 的守卫都到不了（`HostileFactionIdForPlayer` / `Get` 的非负 id 路径都因此不可达）。
    ///
    /// ⇒ 三态语义（"未知即敌"）与敌对阵营选取的**真判定**归游戏内自检
    /// （`FrameworkSelfTest.RunFactionCase` 已经在断言 `IsHostile(敌对, 玩家)==true` /
    ///  `!IsHostile(玩家, 玩家)` / 解析失败计数不增长）；本文件只钉**不碰 `Find`/`Log` 的那一侧**。
    /// </summary>
    public class FactionResolverTests
    {
        // ── 限频判据（这条曾经是坏的：哨兵参与减法 ⇒ 整型溢出 ⇒ 警告永不打印）──────────
        [Fact]
        public void WarnPredicate_NeverWarnedSentinel_FiresImmediately()
        {
            // int.MinValue = "从未警告过"。**必须**显式判它：0 - int.MinValue 在 unchecked 下溢出回负数，
            // 判据会恒假 ⇒ 警告从第一次调用起就再也不打印（2026-09-23 修前就是这个状态）。
            Assert.True(FactionResolver.ShouldWarn(0, int.MinValue, 3600));
            Assert.True(FactionResolver.ShouldWarn(123456, int.MinValue, 3600));
            Assert.True(FactionResolver.ShouldWarn(int.MinValue, int.MinValue, 3600));
        }

        [Fact]
        public void WarnPredicate_RateLimitsWithinInterval()
        {
            const int gap = 3600;
            Assert.False(FactionResolver.ShouldWarn(5000, 5000, gap));       // 同一 tick
            Assert.False(FactionResolver.ShouldWarn(5000 + gap - 1, 5000, gap));
            Assert.True(FactionResolver.ShouldWarn(5000 + gap, 5000, gap));   // 边界含
            Assert.True(FactionResolver.ShouldWarn(5000 + gap * 10, 5000, gap));
        }

        // ── 负数 id = "没有阵营"（`HostileFactionIdForPlayer` 返回 -1 时下游就拿这个去查）────
        [Fact]
        public void NegativeId_ResolvesToNull_WithoutTouchingFind()
        {
            // 这条路径在 `Get` 的第一行就返回（不碰 Find、不打日志）⇒ 可 headless 断言。
            // ⚠ 非负 id 一律不可测（见类注释① ②）—— 所以**不要**在这里补 `Get(42) == null`：
            //   它会打到 `Find.FactionManager` 的 NRE 上，红的是环境不是被测物。
            Assert.Null(FactionResolver.Get(-1));
            Assert.Null(FactionResolver.Get(int.MinValue));
        }
    }
}
