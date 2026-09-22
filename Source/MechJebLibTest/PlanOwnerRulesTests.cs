using System.Collections.Generic;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **队列与引导的归属规则**（§2.4 / B1.1）的判据。
    ///
    /// 旧行为是"**任何**排队都清引导"（两边互斥），靠"先排队后登记"的调用顺序 + `armRendezvousGuidance`
    /// 把引导带回来 ✗ 脆弱。现在只有**手动**条目夺权、引导只在有手动条目时让位。
    /// </summary>
    public class PlanOwnerRulesTests
    {
        [Fact]
        public void ManualTakesOverGuidance_GuidanceDoesNot()
        {
            Assert.True(PlanOwnerRules.TakesOverGuidance(PlanOwner.Manual));
            Assert.False(PlanOwnerRules.TakesOverGuidance(PlanOwner.Guidance),
                "引导自己派出的机动不该把自己的引导清掉（这正是旧行为最荒谬的一处）");
        }

        /// <summary>
        /// ⭐ **反回归**：队列里只有引导自己的条目时，引导**不许**让位。
        /// 旧规则是"队列非空 ⇒ 让位" ⇒ 这条在旧规则下会红。
        /// </summary>
        [Fact]
        public void GuidanceDoesNotYieldToItsOwnEntries()
        {
            var onlyGuidance = new List<PlanOwner> { PlanOwner.Guidance, PlanOwner.Guidance };
            Assert.False(PlanOwnerRules.AnyManual(onlyGuidance),
                "队列非空就让位是旧规则；引导不该被自己挤走");

            var mixed = new List<PlanOwner> { PlanOwner.Guidance, PlanOwner.Manual };
            Assert.True(PlanOwnerRules.AnyManual(mixed), "有手动条目 ⇒ 引导让位");

            var empty = new List<PlanOwner>();
            Assert.False(PlanOwnerRules.AnyManual(empty), "空队列 ⇒ 不让位（引导可以干活）");
        }

        /// <summary>
        /// **枚举零值必须是 `Manual`**：`default(PlannedManeuver)`、旧存档里没写 owner 的条目，
        /// 都会被当作"手动"⇒ 引导让位 ⇒ 与旧行为一致（保守方向：不认识的不许静默夺权）。
        /// 谁要是调整枚举顺序，这条会红。
        /// </summary>
        [Fact]
        public void EnumZeroValue_IsManual_SoUnknownCountsAsManual()
        {
            Assert.Equal(PlanOwner.Manual, default);
            Assert.True(PlanOwnerRules.GuidanceMustYieldTo(default),
                "零值必须让引导让位（保守），否则旧存档的条目会静默变成'引导自己的'");
        }

        [Fact]
        public void GuidanceMustYieldTo_IsTheSingleRuleBothSidesUse()
        {
            Assert.True(PlanOwnerRules.GuidanceMustYieldTo(PlanOwner.Manual));
            Assert.False(PlanOwnerRules.GuidanceMustYieldTo(PlanOwner.Guidance));
        }
    }
}
