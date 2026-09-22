using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **`t_b` 进计划**（B2.2）的形状判据：`PlanNode.burnSeconds` 的默认与传递。
    /// 这一条看着琐碎，但它钉的是"计划自报燃烧时长"这个**新契约**：
    /// 默认 0 = 未声明（执行层退回自估），显式给出时才改变执行层的居中方式。
    /// </summary>
    public class PlanNodeBurnSecondsTests
    {
        [Fact]
        public void DefaultIsZero_MeaningUndeclared()
        {
            var n = new PlanNode(default, 100.0);
            Assert.Equal(0.0, n.burnSeconds, 9);
            Assert.Equal(PlanOwner.Guidance, n.owner);
        }

        [Fact]
        public void CanCarryThePlannedBurnDuration()
        {
            var n = new PlanNode(default, 100.0, PlanOwner.Manual, 92.5);
            Assert.Equal(92.5, n.burnSeconds, 9);
            Assert.Equal(PlanOwner.Manual, n.owner);
            Assert.Equal(100.0, n.ut, 9);
        }
    }
}
