using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>B4b：推力作用点/偏心力矩（按引擎质量流率分摊总推力）。</summary>
    public class EngineThrustTests
    {
        [Fact]
        public void Torque_SymmetricEngines_IsZero()
        {
            var points = new System.Collections.Generic.List<(DVector3 r, float flow)>
            {
                (new DVector3(2, 0, 0), 10f),
                (new DVector3(-2, 0, 0), 10f),
            };
            DVector3 t = EngineThrust.Torque(points, DVector3.forward, 100.0);
            Assert.Equal(0.0, t.magnitude, 9);
        }

        [Fact]
        public void Torque_AllocatesByMassFlow()
        {
            // r=±2，flow=1/3，总推力 100 ⇒ F=25/75；关于 +Z 推力：r×F 的 y 分量 = -50 + 150 = +100
            var points = new System.Collections.Generic.List<(DVector3 r, float flow)>
            {
                (new DVector3(2, 0, 0), 1f),
                (new DVector3(-2, 0, 0), 3f),
            };
            DVector3 t = EngineThrust.Torque(points, DVector3.forward, 100.0);
            Assert.Equal(100.0, t.y, 9);
            Assert.Equal(0.0, t.x, 9);
            Assert.Equal(0.0, t.z, 9);
        }

        [Fact]
        public void Torque_NoFlowOrZeroThrust_IsZero()
        {
            var points = new System.Collections.Generic.List<(DVector3 r, float flow)>
            {
                (new DVector3(2, 0, 0), 0f),
            };
            Assert.Equal(0.0, EngineThrust.Torque(points, DVector3.forward, 100.0).magnitude, 12);
            Assert.Equal(0.0, EngineThrust.Torque(points, DVector3.forward, 0.0).magnitude, 12);
        }
    }
}
