using System.Collections.Generic;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 原地更新燃烧指令（2026-10-27）：不重开状态机、不丢点火质量，只换方向/剩余 Δv/命令加速度。
    /// </summary>
    public class BurnCommandTests
    {
        [Fact]
        public void SetBurnCommand_UpdatesInPlace_WithoutRestart()
        {
            var g = new SpaceEntityGroup(1, 0);
            g.StartBurn(new DVector3(100, 0, 0));
            g.burnStartMass = 1234.0;
            g.appliedBurnDV = 42.0;
            Assert.True(g.HasActiveBurn);

            g.SetBurnCommand(new DVector3(0, 30, 0), 15.0);

            Assert.True(g.HasActiveBurn);                       // 没有重开
            Assert.Equal(1234.0, g.burnStartMass, 6);           // 点火质量保留
            Assert.Equal(0.0, g.appliedBurnDV, 6);              // 本步交付从零计
            Assert.Equal(30.0, g.remainingBurnDV, 6);
            Assert.Equal(0.0, g.burnDirectionSystem.x, 6);      // 方向换成 +Y
            Assert.Equal(1.0, g.burnDirectionSystem.y, 6);
            Assert.Equal(15.0, g.burnCommandAccel, 6);
        }

        [Fact]
        public void SetBurnCommand_WhenNotBurning_StartsOnce()
        {
            var g = new SpaceEntityGroup(2, 0);
            Assert.False(g.HasActiveBurn);

            g.SetBurnCommand(new DVector3(10, 0, 0), 5.0);

            Assert.True(g.HasActiveBurn);
            Assert.Equal(10.0, g.remainingBurnDV, 6);
            Assert.Equal(5.0, g.burnCommandAccel, 6);
        }

        [Fact]
        public void EndBurn_RestoresPlayerSelection()
        {
            var g = new SpaceEntityGroup(3, 0);
            var hyd = new PropellantDef { defName = "PF_Propellant_Hydrazine" };
            var rcsOnly = new HashSet<PropellantDef> { hyd };

            g.SetBurnNetworkOverride(rcsOnly, true);
            Assert.True(g.burnUseRcs);
            Assert.Same(rcsOnly, g.burnEnabledNetworks);

            g.EndBurn();

            Assert.False(g.burnUseRcs);              // 玩家默认 = 全部（主网）
            Assert.Null(g.burnEnabledNetworks);      // 机动覆盖不残留
        }

        [Fact]
        public void StartBurn_DoesNotClearRcsMode()
        {
            var g = new SpaceEntityGroup(4, 0);
            g.burnUseRcs = true;

            g.StartBurn(new DVector3(50, 0, 0));

            Assert.True(g.burnUseRcs);               // 2026-10-27：玩家选 RCS 后点火不能被清掉
            Assert.True(g.HasActiveBurn);
        }
    }
}
