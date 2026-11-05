using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 姿态请求解析（2026-10-27）：多个子系统只提交意图，优先级高者胜，低级请求不覆盖高级。
    /// </summary>
    public class AttitudeRequestTests
    {
        [Fact]
        public void HigherPriorityWins_LowerDoesNotOverride()
        {
            var g = new SpaceEntityGroup(1, 0);

            g.RequestAttitude(AttitudeAxis.Forward, new DVector3(0, 1, 0), 10);
            g.RequestAttitude(AttitudeAxis.Up, new DVector3(1, 0, 0), 50);

            Assert.Equal(50, g.attitudeRequest.priority);
            Assert.Equal(AttitudeAxis.Up, g.attitudeRequest.axis);
            Assert.Equal(1.0, g.attitudeRequest.targetSystem.x, 6);

            g.RequestAttitude(AttitudeAxis.MainThrust, new DVector3(0, 0, 1), 20);   // 低级，忽略
            Assert.Equal(AttitudeAxis.Up, g.attitudeRequest.axis);
            Assert.Equal(50, g.attitudeRequest.priority);
        }

        [Fact]
        public void Clear_ResetsToNoRequest()
        {
            var g = new SpaceEntityGroup(2, 0);
            g.RequestAttitude(AttitudeAxis.MainThrust, new DVector3(0, 0, 1), 30);
            g.ClearAttitudeRequest();

            Assert.Equal(int.MinValue, g.attitudeRequest.priority);
            Assert.Equal(0.0, g.attitudeRequest.targetSystem.sqrMagnitude, 6);
            Assert.Equal(default, g.attitudeRequest.axis);
        }
    }
}
