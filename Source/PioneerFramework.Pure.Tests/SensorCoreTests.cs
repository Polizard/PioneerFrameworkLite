using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 探测判据与信号强度单测（SensorCore：SensorTech / SignalModel / DetectionCore）。
    /// 覆盖：标称基准、项的可加性、冷发射信号 profile、归一化判据的标度律、
    /// **烧穿是代数推论而非第二份算式**、压制与抗压制成对、不确定度传播、退化输入。
    ///
    /// 不能覆盖的部分归游戏内自检场景：接触视图的维护（[V3]）、辐射对称的端到端验收（S3）、
    /// 假目标的消耗链（S5）——那些需要 RimWorld 运行时。
    ///
    /// 反例纪律（沿用项目教训）：不写"就等于某个具体数值"这类把实现抄一遍的断言；
    /// 判据只用"实现正确则不可能"的硬逻辑（标度律、唯一翻转点、退化边界、两入口同值）。
    /// </summary>
    public class SensorCoreTests
    {
        // ── 标称基准：一艘静默基准船的 S 恰为 1.0 ──────────────────────────

        [Fact]
        public void Signal_ReferenceShipIsExactlyOne()
        {
            var x = new SignalInputs { rcsRadiusMeters = SignalModel.RRefMeters };
            Assert.Equal(1.0, SignalModel.Total(x), 12);
        }

        [Fact]
        public void Signal_TermsAreAdditive()
        {
            var bare = new SignalInputs { rcsRadiusMeters = SignalModel.RRefMeters };
            var withHeat = new SignalInputs
            {
                rcsRadiusMeters = SignalModel.RRefMeters,
                heatWatts = SignalModel.HeatRefWatts,
            };
            Assert.Equal(SignalModel.Total(bare) + 1.0, SignalModel.Total(withHeat), 12);
        }

        /// <summary>
        /// 冷发射的副产品（设计稿 §4.3）：巡航段导弹**只有尺寸基数**（远小于飞船），
        /// 终末点火后信号跃升——"巡航低信号、点火高信号"是算式推论，不是额外机制。
        /// 判据用**比值**而非绝对值：点火态的 S 至少是巡航态的 4 倍（真实倍数由 BurnMultiplier 与尺寸决定）。
        /// </summary>
        [Fact]
        public void Signal_CruiseMissileIsQuiet_TerminalBurnIsLoud()
        {
            var cruise = new SignalInputs { rcsRadiusMeters = 5.0 };
            var terminal = new SignalInputs { rcsRadiusMeters = 5.0, burning = true };

            double quiet = SignalModel.Total(cruise);
            double loud = SignalModel.Total(terminal);

            Assert.True(quiet < 0.05, "5m 半径的弹体相对基准船应远低于 1.0");
            Assert.True(loud > quiet * 4.0,
                string.Format("点火后信号应至少为巡航态的 4 倍（巡航 {0:F4} → 点火 {1:F4}）", quiet, loud));
        }

        // ── 有效探测距离的标度律（归一化判据的硬逻辑）────────────────────────

        [Fact]
        public void DetectRange_ScalesAsSquareRootOfSignal()
        {
            var tech = new SensorTech { sensitivityMeters = 1.0e6f };
            double r1 = DetectionCore.EffectiveDetectRange(1.0, tech, 0.0);
            double r4 = DetectionCore.EffectiveDetectRange(4.0, tech, 0.0);

            Assert.Equal(2.0 * r1, r4, 6);
        }

        [Fact]
        public void DetectRange_ScalesLinearlyWithSensitivity()
        {
            var weak = new SensorTech { sensitivityMeters = 3.0e5f };
            var strong = new SensorTech { sensitivityMeters = 9.0e5f };

            double rw = DetectionCore.EffectiveDetectRange(2.0, weak, 0.0);
            double rs = DetectionCore.EffectiveDetectRange(2.0, strong, 0.0);

            Assert.Equal(3.0 * rw, rs, 6);
        }

        /// <summary>
        /// **唯一算式护栏**：`DetectRangeWithoutJam`（纯距离律）与 `EffectiveDetectRange`（无干扰）
        /// 必须同值——它们是同一个数学对象。出现分叉即说明有人写了第二份算式（[C7] 不变式）。
        /// </summary>
        [Fact]
        public void DetectRange_WithoutJam_MatchesThePlainRangeLaw()
        {
            var tech = new SensorTech { sensitivityMeters = 7.5e5f, ecmResist = 12f };
            for (double s = 0.25; s <= 16.0; s *= 2.0)
            {
                double a = DetectionCore.EffectiveDetectRange(s, tech, 0.0);
                double b = DetectionCore.DetectRangeWithoutJam(s, tech.sensitivityMeters);
                Assert.Equal(b, a, 6);
            }
        }

        // ── 干扰：平方反比 + 烧穿 ────────────────────────────────────────

        [Fact]
        public void JamEffort_FallsOffWithSquareOfDistance()
        {
            double near = DetectionCore.JamEffort(1.0, DetectionCore.JamRefMeters);
            double far = DetectionCore.JamEffort(1.0, 2.0 * DetectionCore.JamRefMeters);

            Assert.Equal(1.0, near, 9);            // 参考距离上强度 1.0 ⇒ J_eff = 1
            Assert.Equal(0.25, far, 9);            // 距离翻倍 ⇒ 1/4
        }

        /// <summary>
        /// **烧穿**（[E4]）——本设计最值得钉住的一条，因为它"零成本"的说法只有在判据形式正确时才成立。
        /// 硬逻辑：干扰机固定 ⇒ `J_eff` 与目标距离无关 ⇒ 目标从远到近扫过时
        /// **`IsVisible` 必须恰好翻转一次**，且翻转点等于 `EffectiveDetectRange`。
        /// 若实现里 `J_eff` 误依赖目标距离（或把压制写成加在信号上），翻转次数会不等于 1。
        /// </summary>
        [Fact]
        public void BurnThrough_FlipsExactlyOnce_WhenTargetClosesUnderFixedJammer()
        {
            var tech = new SensorTech { sensitivityMeters = 1.0e6f };
            const double signal = 1.0;
            double jam = DetectionCore.JamEffort(4.0, 5.0e5); // 干扰机比目标更近，压制很重

            double predicted = DetectionCore.EffectiveDetectRange(signal, tech, jam);
            Assert.True(jam > 1.0, "前提：压制效力必须显著大于 0，否则测不到烧穿");
            Assert.True(predicted < DetectionCore.DetectRangeWithoutJam(signal, tech.sensitivityMeters),
                "前提：被压制时的有效距离必须小于无干扰距离");

            int flips = 0;
            double lastVisibleDist = double.MaxValue;
            bool prev = DetectionCore.IsVisible(signal, 3.0e6, tech, jam);
            Assert.False(prev, "起始点（3e6 m）应判不可见");

            for (double d = 3.0e6; d > 1.0e5; d -= 1.0e4)
            {
                bool now = DetectionCore.IsVisible(signal, d, tech, jam);
                if (now != prev)
                {
                    flips++;
                    lastVisibleDist = d;
                }
                prev = now;
            }

            Assert.Equal(1, flips);
            Assert.True(prev, "扫描结束时（1e5 m）应判可见——烧穿必须真的发生");
            Assert.True(Math.Abs(lastVisibleDist - predicted) < 1.0e4,
                string.Format("翻转点应等于有效探测距离（预测 {0:F0} m，实测 {1:F0} m）", predicted, lastVisibleDist));
        }

        [Fact]
        public void EcmResist_RestoresRangeMonotonically_AndSaturates()
        {
            var naked = new SensorTech { sensitivityMeters = 1.0e6f };
            double jam = DetectionCore.JamEffort(6.0, 4.0e5);

            double r0 = DetectionCore.EffectiveDetectRange(1.0, naked, jam);
            double prev = r0;
            for (float resist = 1f; resist <= 100f; resist *= 10f)
            {
                var armored = new SensorTech { sensitivityMeters = 1.0e6f, ecmResist = resist };
                double r = DetectionCore.EffectiveDetectRange(1.0, armored, jam);
                Assert.True(r > prev, string.Format("抗压制增大必须让距离回升（resist={0}）", resist));
                prev = r;
            }

            // 饱和是**渐近**的：任何有限 ecmResist 都还留着一个微小残差，
            // 所以只能断言相对差可忽略（绝对容差在 1e6 量级上没有意义——本测试初版就栽在这里）。
            var immune = new SensorTech { sensitivityMeters = 1.0e6f, ecmResist = 1.0e9f };
            double unjammed = DetectionCore.DetectRangeWithoutJam(1.0, immune.sensitivityMeters);
            double suppressed = DetectionCore.EffectiveDetectRange(1.0, immune, jam);
            Assert.True(Math.Abs(suppressed - unjammed) / unjammed < 1e-6,
                string.Format("极高抗压制应渐近退化到无干扰（无干扰 {0:F3} m，实测 {1:F3} m）", unjammed, suppressed));
        }

        // ── 不确定度传播 ────────────────────────────────────────────────

        [Fact]
        public void PositionSigma_GrowsWithRangeAndWithJam()
        {
            double baseSigma = DetectionCore.PositionSigmaMeters(1.0e6, 0.0, 0.01, 0.02, 1.0);
            double farSigma = DetectionCore.PositionSigmaMeters(2.0e6, 0.0, 0.01, 0.02, 1.0);
            double jammedSigma = DetectionCore.PositionSigmaMeters(1.0e6, 9.0, 0.01, 0.02, 1.0);

            Assert.Equal(2.0 * baseSigma, farSigma, 6);   // 越远越不准（内建）
            Assert.True(jammedSigma > baseSigma, "压制必须增大位置不确定度（[E3] 的作用对象）");
        }

        [Fact]
        public void PositionSigma_ZeroDistanceIsZero_NotNaN()
        {
            Assert.Equal(0.0, DetectionCore.PositionSigmaMeters(0.0, 5.0, 0.01, 0.02, 1.0));
        }

        // ── 退化输入：不得产生 NaN、不得抛 ────────────────────────────────

        [Theory]
        [InlineData(0.0, 1.0e6, 0.0)]      // 信号为 0：不可见
        [InlineData(-3.0, 1.0e6, 0.0)]     // 负信号：不可见（防守）
        [InlineData(1.0, 0.0, 0.0)]        // 零距离：可见（重合）
        [InlineData(1.0, -5.0, 0.0)]       // 负距离：不可见
        [InlineData(double.NaN, 1.0e6, 0.0)]
        [InlineData(1.0, double.NaN, 0.0)]
        public void Degenerate_Inputs_DoNotThrowAndDoNotClaimVisibility(double signal, double dist, double jam)
        {
            var tech = new SensorTech { sensitivityMeters = 1.0e6f };

            bool visible = DetectionCore.IsVisible(signal, dist, tech, jam);
            double range = DetectionCore.EffectiveDetectRange(signal, tech, jam);

            Assert.False(double.IsNaN(range), "有效距离不得为 NaN");
            Assert.True(range >= 0.0, "有效距离不得为负");
            if (!(signal > 0.0) || double.IsNaN(dist) || dist < 0.0)
                Assert.False(visible, "退化输入必须判不可见，不得凭默认值放行");
        }

        [Fact]
        public void ZeroSensitivity_SeesNothing()
        {
            var blind = new SensorTech { sensitivityMeters = 0f };
            Assert.False(DetectionCore.IsVisible(100.0, 1.0, blind, 0.0));
            Assert.Equal(0.0, DetectionCore.EffectiveDetectRange(100.0, blind, 0.0));
        }

        /// <summary>整体关系（不依赖具体常数）：灵敏度越高看得越远、干扰越强看得越近。</summary>
        [Fact]
        public void DetectRange_IsMonotonicInSensitivityAndJam()
        {
            var lo = new SensorTech { sensitivityMeters = 1.0e5f };
            var hi = new SensorTech { sensitivityMeters = 1.0e6f };
            double jam = DetectionCore.JamEffort(2.0, 8.0e5);

            Assert.True(DetectionCore.EffectiveDetectRange(1.0, hi, 0.0)
                      > DetectionCore.EffectiveDetectRange(1.0, lo, 0.0));
            Assert.True(DetectionCore.EffectiveDetectRange(1.0, hi, jam)
                      < DetectionCore.EffectiveDetectRange(1.0, hi, 0.0));
            Assert.True(DetectionCore.EffectiveDetectRange(1.0, hi, 2.0 * jam)
                      < DetectionCore.EffectiveDetectRange(1.0, hi, jam));
        }
    }
}
