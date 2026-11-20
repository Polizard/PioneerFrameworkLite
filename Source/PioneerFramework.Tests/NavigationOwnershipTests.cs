using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 导航节点的归属与清理（2026-11-20，授权件 `后端变更授权-20261120-导航取消`）：
    /// `PlanOwner.Navigation` 只属于导航状态机 —— 阶梯失效不许删它、取消导航只删它、
    /// 玩家手动节点一条不动。
    /// </summary>
    public class NavigationOwnershipTests
    {
        private static SpaceEntityGroup.PlannedManeuver Node(PlanOwner owner, int tick) =>
            new() { active = true, executeTick = tick, owner = owner, ladderEpoch = 0 };

        [Fact]
        public void RemoveNavigationManeuvers_RemovesOnlyNavigationNodes()
        {
            var g = new SpaceEntityGroup(1, 0);
            g.plannedManeuvers.Add(Node(PlanOwner.Manual, 100));
            g.plannedManeuvers.Add(Node(PlanOwner.Navigation, 200));
            g.plannedManeuvers.Add(Node(PlanOwner.Guidance, 300));

            int removed = g.RemoveNavigationManeuvers();

            Assert.Equal(1, removed);
            Assert.Equal(2, g.plannedManeuvers.Count);
            Assert.Equal(PlanOwner.Manual, g.plannedManeuvers[0].owner);
            Assert.Equal(PlanOwner.Guidance, g.plannedManeuvers[1].owner);
        }

        [Fact]
        public void InvalidateStaleLadderNodes_KeepsNavigationNodes()
        {
            var g = new SpaceEntityGroup(2, 0);
            g.ladderPlanEpoch = 5;
            g.plannedManeuvers.Add(Node(PlanOwner.Guidance, 100));   // 旧代 ⇒ 应被清
            g.plannedManeuvers.Add(Node(PlanOwner.Navigation, 200)); // 导航的 ⇒ 应保留
            g.plannedManeuvers.Add(Node(PlanOwner.Manual, 300));     // 玩家的 ⇒ 应保留

            int removed = g.InvalidateStaleLadderNodes();

            Assert.Equal(1, removed);
            Assert.Equal(2, g.plannedManeuvers.Count);
            Assert.Equal(PlanOwner.Navigation, g.plannedManeuvers[0].owner);
            Assert.Equal(PlanOwner.Manual, g.plannedManeuvers[1].owner);
        }
    }
}
