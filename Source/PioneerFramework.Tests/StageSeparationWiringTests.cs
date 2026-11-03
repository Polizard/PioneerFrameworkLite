using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Components;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **抛级接线的三个地基**（2026-10-30）：批量移除（一次失效）、原点重定（位置平移 + 版本自增）、
    /// 质量口径（结构 + 罐内工质，损毁罐只剩结构）。纯核的守恒判据在
    /// `PioneerFramework.Pure.Tests/StageSeparationCoreTests.cs`，这里只钉接线不变量。
    /// </summary>
    public class StageSeparationWiringTests
    {
        private static SpacePart Part(int id, double z, float mass = 100f)
        {
            var p = new SpacePart
            {
                partId = id,
                defName = "PF_Test_Part",
                positionShip = new DVector3(0, 0, z),
                boundsSizeShip = new DVector3(2, 2, 4),
                hp = 100f,
            };
            p.AddComponent(new HullMassComponent { mass = mass });
            return p;
        }

        private static SpaceEntity EntityWith(params SpacePart[] parts)
        {
            var e = new SpaceEntity(1, 0);
            e.SetParts(new List<SpacePart>(parts));
            return e;
        }

        [Fact]
        public void RemoveParts_RemovesAllAndBumpsVersionOnce()
        {
            var a = Part(1, 0);
            var b = Part(2, -10);
            var c = Part(3, -20);
            var e = EntityWith(a, b, c);
            int version = e.partSetVersion;

            int removed = e.RemoveParts(new List<int> { 1, 3 });

            Assert.Equal(2, removed);
            Assert.Single(e.Parts);
            Assert.Equal(2, e.Parts[0].partId);
            Assert.True(e.partSetVersion > version);      // 一次收口（版本至少自增一次）
            Assert.True(e.BoundsSize.z > 0.0);            // 包围盒按剩余件重算
        }

        [Fact]
        public void RemoveParts_UnknownIdsAreIgnored()
        {
            var e = EntityWith(Part(1, 0));
            Assert.Equal(0, e.RemoveParts(new List<int> { 999 }));
            Assert.Single(e.Parts);
        }

        [Fact]
        public void RecenterParts_ShiftsPositionsAndBumpsVersion()
        {
            var e = EntityWith(Part(1, 0), Part(2, -10));
            int version = e.partSetVersion;

            e.RecenterParts(new DVector3(0, 0, -5));      // 原点移到两件中间

            Assert.Equal(5.0, e.Parts[0].positionShip.z, 9);
            Assert.Equal(-5.0, e.Parts[1].positionShip.z, 9);
            Assert.True(e.partSetVersion > version);      // 位置变了必须让惯量/包围盒缓存失效
            Assert.Equal(DVector3.zero, e.Position);      // 实体局部偏移不动（无图船恒 0）
        }

        [Fact]
        public void SeparationMass_UsesStructurePlusTankFuel_AndSkipsDestroyedFuel()
        {
            var live = Part(1, 0, 100f);
            live.AddComponent(new PropellantTankComponentData { capacityKg = 1000f, massKg = 50f });
            Assert.Equal(150.0, StageSeparation.PartMass(live), 9);

            var wreck = Part(2, -10, 100f);
            wreck.AddComponent(new PropellantTankComponentData { capacityKg = 1000f, massKg = 50f });
            wreck.isDestroyed = true;
            Assert.Equal(100.0, StageSeparation.PartMass(wreck), 9);   // 损毁罐：燃料漏光

            // 一次遍历分流：级号=1 的进 dropped，其余进 kept
            var e = EntityWith(live, wreck);
            e.Parts[1].stageId = 1;
            var kept = new SeparationMass();
            var dropped = new SeparationMass();
            StageSeparation.Accumulate(e, 1, ref kept, ref dropped);
            Assert.Equal(150.0, kept.mass, 9);
            Assert.Equal(100.0, dropped.mass, 9);
            // 质心：kept = z0 的 150kg；dropped = z−10 的 100kg
            Assert.Equal(0.0, kept.Center.z, 9);
            Assert.Equal(-10.0, dropped.Center.z, 9);
        }
    }
}
