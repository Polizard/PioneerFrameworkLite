using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Components;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>动量轮的 Entity 级状态（2026-10-26）：角动量向量 + 随质量重算的容量。</summary>
    public class WheelStateTests
    {
        private static SpacePart Hull(float mass)
        {
            var p = new SpacePart { partId = 1, defName = "hull", boundsSizeShip = new DVector3(2, 2, 2) };
            p.AddComponent(new HullMassComponent { mass = mass });
            return p;
        }

        [Fact]
        public void NewEntity_GetsCapacityFromMass()
        {
            var e = new SpaceEntity(1, 0);
            e.SetParts(new List<SpacePart> { Hull(1000f) });

            Assert.Equal(500_000.0, e.wheelCapacity, 3);   // 500 × 1000 kg
            Assert.Equal(0.0, e.wheelMomentum.magnitude, 9);
        }

        [Fact]
        public void ExistingCapacity_IsNotOverwritten()
        {
            var e = new SpaceEntity(2, 0);
            e.wheelCapacity = 12_345.0;
            e.SetParts(new List<SpacePart> { Hull(1000f) });

            Assert.Equal(12_345.0, e.wheelCapacity, 3);
        }
    }
}
