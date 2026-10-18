using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **取消目标 ≠ 保留自转 / 断开引导要撤回节点**（2026-10-17 用户实测两缺陷的判据）：
    /// ① 自动对准目标被清 ⇒ ω 归零；手动自由自转（无自动目标）不受影响；
    /// ② 断开交会引导 ⇒ 撤回该代阶梯节点，但**保留手动条目**；`invalidateLadderNodes:false` 时不动。
    /// </summary>
    public class ManeuverCancelSemanticsTests
    {
        private static SpaceEntityGroup NewGroup() => new(1, 0);

        private static SpaceEntityGroup.PlannedManeuver Node(PlanOwner owner) => new()
        {
            active = true,
            executeTick = 100,
            owner = owner,
            ladderEpoch = 0,
        };

        [Fact]
        public void ClearManeuvers_StopsAutopilotSpin_ButLeavesFreeSpin()
        {
            var g = NewGroup();
            g.pendingAlignDir = new DVector3(0, 0, 1);
            g.angularVelocity = new DVector3(0.5, 0, 0);

            g.ClearManeuvers();
            Assert.Equal(0.0, g.angularVelocity.magnitude, 12);
            Assert.Equal(0.0, g.pendingAlignDir.sqrMagnitude, 12);

            // 无自动对准目标时的自由自转不受 ClearManeuvers 影响（手动键松开仍保留）
            g.angularVelocity = new DVector3(0.5, 0, 0);
            g.pendingAlignDir = DVector3.zero;
            g.ClearManeuvers();
            Assert.Equal(0.5, g.angularVelocity.x, 12);
        }

        [Fact]
        public void CancelFromZero_StopsAutopilotSpin_FromLater_KeepsHead()
        {
            var g = NewGroup();
            g.pendingAlignDir = new DVector3(0, 0, 1);
            g.angularVelocity = new DVector3(0.5, 0, 0);
            g.plannedManeuvers.Add(Node(PlanOwner.Guidance));

            g.CancelManeuversFrom(0);
            Assert.Equal(0.0, g.angularVelocity.magnitude, 12);
            Assert.Empty(g.plannedManeuvers);

            var h = NewGroup();
            h.pendingAlignDir = new DVector3(0, 0, 1);
            h.angularVelocity = new DVector3(0.5, 0, 0);
            h.plannedManeuvers.Add(Node(PlanOwner.Guidance));
            h.plannedManeuvers.Add(Node(PlanOwner.Guidance));

            h.CancelManeuversFrom(1);
            Assert.Single(h.plannedManeuvers);
            Assert.Equal(0.5, h.angularVelocity.x, 12);   // 保留队首 ⇒ 预对准继续，不消旋
        }

        [Fact]
        public void ClearRendezvousGuidance_InvalidatesGuidanceNodes_KeepsManual()
        {
            var g = NewGroup();
            g.RegisterRendezvousGuidance(2, 1000.0, prograde: true, useNetworks: null);
            g.plannedManeuvers.Add(Node(PlanOwner.Guidance));
            g.plannedManeuvers.Add(Node(PlanOwner.Manual));

            g.ClearRendezvousGuidance();

            Assert.False(g.HasRendezvousGuidance);
            Assert.Single(g.plannedManeuvers);
            Assert.Equal(PlanOwner.Manual, g.plannedManeuvers[0].owner);
        }

        [Fact]
        public void ClearRendezvousGuidance_CanPreserveLadderNodes()
        {
            var g = NewGroup();
            g.RegisterRendezvousGuidance(2, 1000.0, prograde: true, useNetworks: null);
            g.plannedManeuvers.Add(Node(PlanOwner.Guidance));

            g.ClearRendezvousGuidance(invalidateLadderNodes: false);

            Assert.False(g.HasRendezvousGuidance);
            Assert.Single(g.plannedManeuvers);   // 保留（CancelManeuversFrom(index>0) 的语义）
        }
    }
}
