using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// **队列与引导的归属规则**（《MechJeb参考大修计划》§2.4 / B1.1）。
    ///
    /// ## 它要治什么
    /// 在此之前，两边靠**互斥 + 调用顺序**在撑：
    /// · `SpaceEntityGroup.QueueManeuver` 无条件 `ClearRendezvousGuidance()`（"玩家新排队 = 手动接管"）；
    /// · 引导侧反过来：队列非空就让位。
    /// 连起来的效果是**任何一次排队都会杀掉引导**，而 `RendezvousPlanner.ConfirmRendezvous` 靠
    /// "**先排队**（清掉引导，那时还没登记）**再登记**"这个**顺序**绕过去，点火时再用
    /// `PlannedManeuver.armRendezvousGuidance` 把引导"带回来"【计划稿原文】。
    /// **能 work，但脆弱**：只要有人先登记后排队，引导就被静默清掉。
    ///
    /// ⇒ 现在改成**显式归属**：每条机动带 `PlanOwner`（引导自己有 / 玩家手动），
    /// 规则只有两条，且都在这里（**唯一权威**，宿主只调用不做判断）：
    /// · 只有**手动**条目会夺走引导；
    /// · 引导只在队列里存在**手动**条目时让位。
    ///
    /// ⚠ 注意 `PlanOwner.Manual = 0` 是枚举零值 ⇒ `default(PlannedManeuver)` 视为**手动**
    /// （保守方向：不认识的一律当手动、引导让位，与旧行为一致，不会静默夺权）。
    /// </summary>
    public static class PlanOwnerRules
    {
        /// <summary>新入队的这一条会不会**夺走**引导（只有玩家手动排的才夺）。</summary>
        public static bool TakesOverGuidance(PlanOwner incoming) => incoming == PlanOwner.Manual;

        /// <summary>
        /// 引导遇到这一条时**是否必须让位**。逐条判（宿主可以无分配地扫队列）。
        /// </summary>
        public static bool GuidanceMustYieldTo(PlanOwner queued) => queued == PlanOwner.Manual;

        /// <summary>便利版：队列里**有没有**手动条目（宿主的扫描语义就写在这里，避免各写一份）。</summary>
        public static bool AnyManual(IReadOnlyList<PlanOwner> queuedOwners)
        {
            for (int i = 0; i < queuedOwners.Count; i++)
                if (GuidanceMustYieldTo(queuedOwners[i])) return true;
            return false;
        }
    }
}
