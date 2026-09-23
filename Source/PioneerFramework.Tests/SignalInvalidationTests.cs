using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **派生信号（[V8]）失效点的结构性测试**（P1 四字段里最容易静默过期的一块）。
    ///
    /// ## 为什么这条必须存在
    /// `SignalStrength` 是"带失效钩子的缓存值"——**读到的值对不对，完全取决于每个写入点有没有置脏**。
    /// 漏一个钩子不会报错、不会崩，只会让信号**停在旧值上**（而信号决定"谁先看见谁"，是 W1 枢纽的量）。
    /// 这类"静默过期"是本库反复踩过的坑（`HasActiveBurn` 是 public 字段 ⇒ 钩子只能挂两处；
    /// `AVM:824` 的派生点；[C4] 新发现的 `SpacePart.InvalidateStats` 不上行通知）。
    ///
    /// ## 为什么能 headless 断言（分辨率说清楚）
    /// 无 `Current.Game` ⇒ `RecomputeSignal` 里"遍历实体的部件"那段被跳过 ⇒
    /// `SignalStrength` 恰好退化为 `SignalModel.Total(burning, activePing)`（其余项输入 0）。
    /// 于是读数**精确可预期**（静默 0 / 开机 10 / 点火 20，见 `SignalModel` 的常量），
    /// 断言用**相等**而不仅是单调 ⇒ 顺带钉住"缓存不会被重复累加"。
    ///
    /// ## 覆盖到哪、没覆盖到哪（不许用沉默冒充覆盖）
    /// · ✅ 覆盖三个群组侧驱动点：`ActivePingEnabled` setter · `StartBurn` · `EndBurn`（+ 公开 `InvalidateSignal`）；
    /// · ⚠ **未覆盖**：第四个驱动点 `SpaceEntity.InvalidateStats` —— 它经 `Current.Game.GetComponent<SpaceGameComp>()`
    ///   找群组，headless 必然是空操作（**观测不到**，不是"通过"）⇒ 归游戏内自检（见交接文件 B⑥/S3）。
    /// </summary>
    public class SignalInvalidationTests
    {
        [Fact]
        public void ActivePingToggle_InvalidatesCache_ExactlyByDeclaredMultiplier()
        {
            var g = new SpaceEntityGroup();
            double passive = g.SignalStrength;            // 第一次读 ⇒ 建缓存

            g.ActivePingEnabled = true;

            Assert.Equal(SignalModel.ActivePingMultiplier, g.SignalStrength, 9);
            Assert.True(g.SignalStrength > passive, "setter 没置脏 ⇒ 派生信号静默过期");
        }

        [Fact]
        public void ActivePingToggle_SameValueTwice_DoesNotDrift()
        {
            var g = new SpaceEntityGroup();
            g.ActivePingEnabled = true;
            double once = g.SignalStrength;

            g.ActivePingEnabled = true;                   // setter 的 early-return 分支

            Assert.Equal(once, g.SignalStrength, 12);
        }

        [Fact]
        public void ActivePingToggle_BackToPassive_ReturnsToBaseExactly()
        {
            var g = new SpaceEntityGroup();
            double passive = g.SignalStrength;

            g.ActivePingEnabled = true;
            g.ActivePingEnabled = false;

            Assert.Equal(passive, g.SignalStrength, 12);   // 关掉必须精确回到基准（不是"接近"）
        }

        [Fact]
        public void StartBurn_InvalidatesCache()
        {
            var g = new SpaceEntityGroup();
            double passive = g.SignalStrength;

            g.StartBurn(new DVector3(10.0, 0.0, 0.0));

            Assert.True(g.HasActiveBurn);
            Assert.Equal(SignalModel.BurnMultiplier, g.SignalStrength, 9);
            Assert.True(g.SignalStrength > passive, "StartBurn 没置脏 ⇒ 冷发射信号 profile 会漏报");
        }

        [Fact]
        public void EndBurn_InvalidatesCache()
        {
            var g = new SpaceEntityGroup();
            double passive = g.SignalStrength;
            g.StartBurn(new DVector3(10.0, 0.0, 0.0));
            double burning = g.SignalStrength;

            g.EndBurn();

            Assert.False(g.HasActiveBurn);
            Assert.Equal(passive, g.SignalStrength, 12);
            Assert.True(burning > g.SignalStrength);
        }

        [Fact]
        public void BurnCycles_DoNotAccumulate()
        {
            // 缓存若被"就地累加"而不是"整算重来"，这个回路会一路涨上去
            var g = new SpaceEntityGroup();
            double passive = g.SignalStrength;

            for (int i = 0; i < 3; i++)
            {
                g.StartBurn(new DVector3(10.0, 0.0, 0.0));
                Assert.Equal(SignalModel.BurnMultiplier, g.SignalStrength, 9);
                g.EndBurn();
                Assert.Equal(passive, g.SignalStrength, 12);
            }
        }

        [Fact]
        public void BothFlagsTogether_SumTheTerms()
        {
            var g = new SpaceEntityGroup();
            g.StartBurn(new DVector3(1.0, 0.0, 0.0));
            g.ActivePingEnabled = true;

            Assert.Equal(SignalModel.BurnMultiplier + SignalModel.ActivePingMultiplier, g.SignalStrength, 9);
        }

        [Fact]
        public void PublicInvalidateSignal_ForcesRecompute_AndIsIdempotent()
        {
            // 第四个驱动点（SpaceEntity.InvalidateStats）就是调它，但那条链 headless 观测不到 ⇒
            // 这里只钉"公开入口确实能把陈旧缓存打掉、且重复调用无害"，不冒充覆盖了那条链。
            var g = new SpaceEntityGroup();
            g.StartBurn(new DVector3(1.0, 0.0, 0.0));
            Assert.Equal(SignalModel.BurnMultiplier, g.SignalStrength, 9);

            g.HasActiveBurn = false;          // 绕过 EndBurn 直接改字段 ⇒ 缓存里还是"点火"那个值
            g.InvalidateSignal();
            g.InvalidateSignal();

            Assert.Equal(0.0, g.SignalStrength, 12);
        }

        [Fact]
        public void DirectFieldWrite_BypassesHook_KnownHazard()
        {
            // ⚠ **这是"已知局限"的可执行形式，不是期望✅**：
            // `HasActiveBurn` 是 public 字段（不是属性）⇒ 谁绕过 `StartBurn` 直接赋值，
            // 派生信号就停在旧值上。本条把它写成断言的目的：
            // **将来若有人把 `HasActiveBurn` 改成属性（顺手修掉这个坑），这条会红** ⇒
            // 那就说明文档该改了（`SpaceEntityGroup.cs` 的属性注释与 `StartBurn` 的失效点注释），
            // 而不是把断言改成"通过"。
            var g = new SpaceEntityGroup();
            double passive = g.SignalStrength;

            g.HasActiveBurn = true;                       // 绕过 StartBurn

            Assert.Equal(passive, g.SignalStrength, 12);   // 仍是旧值 = 钩子确实没触发
        }
    }
}
