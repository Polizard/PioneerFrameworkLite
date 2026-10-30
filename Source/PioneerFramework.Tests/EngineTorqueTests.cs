using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Components;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>引擎偏心力矩（2026-10-26）：τ = Σ(pos×F) − COM×ΣF；"引擎全开"= 标称 ve·ṁ。</summary>
    public class EngineTorqueTests
    {
        private static SpacePart Engine(int id, double x, double y, double z, ThrustAxis axis, float ve = 3000f, float flow = 10f)
        {
            var p = new SpacePart
            {
                partId = id,
                defName = "engine",
                positionShip = new DVector3(x, y, z),
                boundsSizeShip = new DVector3(2, 2, 2),
            };
            p.AddComponent(new EngineComponentData
            {
                exhaustVelocity = ve,
                massFlowRate = flow,
                propellantDefName = "P",
                thrustAxis = axis,
            });
            return p;
        }

        [Fact]
        public void SingleOffsetEngine_ProducesTorque()
        {
            var e = new SpaceEntity(1, 0);
            e.SetParts(new List<SpacePart> { Engine(1, 0, 10, 0, ThrustAxis.Right) });

            var tau = e.GetEngineTorqueAboutCom(DVector3.zero, out double thrust);

            // 2026-10-27 口径：thrustAxis = 喷口方向，推力 = −轴 ⇒ F = −30,000 沿 X
            // τ = (0,10,0)×(−30000,0,0) = (0,0,+300000)
            Assert.Equal(30_000.0, thrust, 3);
            Assert.Equal(0.0, tau.x, 3);
            Assert.Equal(0.0, tau.y, 3);
            Assert.Equal(300_000.0, tau.z, 3);
        }

        [Fact]
        public void SymmetricPair_CancelsTorque()
        {
            var e = new SpaceEntity(2, 0);
            e.SetParts(new List<SpacePart>
            {
                Engine(1, 0, 10, 0, ThrustAxis.Right),
                Engine(2, 0, -10, 0, ThrustAxis.Right),
            });

            var tau = e.GetEngineTorqueAboutCom(DVector3.zero, out double thrust);

            Assert.Equal(60_000.0, thrust, 3);
            Assert.Equal(0.0, tau.magnitude, 3);
        }

        [Fact]
        public void ComOffset_ShiftsTorque()
        {
            var e = new SpaceEntity(3, 0);
            e.SetParts(new List<SpacePart> { Engine(1, 0, 0, 0, ThrustAxis.Right) });

            var tau = e.GetEngineTorqueAboutCom(new DVector3(0, 10, 0), out _);

            // 力过原点（−X），COM 在 +Y ⇒ τ = −COM×F = (0,0,−300000)
            Assert.Equal(-300_000.0, tau.z, 3);
        }

        [Fact]
        public void DestroyedEngine_IsExcluded()
        {
            var e = new SpaceEntity(4, 0);
            var engine = Engine(1, 0, 10, 0, ThrustAxis.Right);
            e.SetParts(new List<SpacePart> { engine });
            engine.ApplyDamage(9999f);

            var tau = e.GetEngineTorqueAboutCom(DVector3.zero, out double thrust);

            Assert.Equal(0.0, thrust, 9);
            Assert.Equal(0.0, tau.magnitude, 9);
        }
    }
}
