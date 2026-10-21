using System.Collections.Generic;
using System.IO;
using PioneerFramework;
using PioneerFramework.Components;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **B1 运行时 part 集合 API**（2026-10-17）：Add/Remove/Move、partId 分配器、单一失效收口。
    /// 判据覆盖：id 唯一/单调、质量与包围盒重算、AABB 缓存显式失效（headless tick 恒 -1 也能验）、
    /// 挂图 part 的 KeepMap 拒绝、partSetVersion 变化、组件 OnDetached 回调。
    /// </summary>
    public class PartSetMutationTests
    {
        private sealed class DetachProbe : PartComponentData
        {
            public int Detached;
            public override void OnDetached() => Detached++;
            public override void ExposeData() { }
            public override void Write(BinaryWriter w) { }
            public override void Read(BinaryReader r) { }
        }

        private static SpacePart MakePart(int id, double x, double w, float mass)
        {
            var p = new SpacePart
            {
                partId = id,
                defName = "PF_ShipPart_Cube",
                positionShip = new DVector3(x, 0, 0),
                boundsSizeShip = new DVector3(w, 2, 2),
                hp = 100f,
            };
            p.AddComponent(new HullMassComponent { mass = mass });
            return p;
        }

        [Fact]
        public void SetParts_SyncsAllocator_AddPartGetsUniqueId()
        {
            var e = new SpaceEntity(1, 0);
            e.SetParts(new List<SpacePart> { MakePart(7, 0, 2, 100) });

            var added = MakePart(0, 5, 2, 50);
            Assert.True(e.AddPart(added));

            Assert.Equal(8, added.partId);
            Assert.Equal(2, e.Parts.Count);
            Assert.Equal(150f, e.TotalMass, 3);
        }

        [Fact]
        public void AddPart_UpdatesBounds_RemoveRestores()
        {
            var e = new SpaceEntity(1, 0);
            e.SetParts(new List<SpacePart> { MakePart(1, 0, 2, 100) });   // x ∈ [-1,1]
            Assert.Equal(2.0, e.BoundsSize.x, 6);

            Assert.True(e.AddPart(MakePart(0, 10, 4, 100)));              // x ∈ [8,12] ⇒ 并集 [-1,12]
            Assert.Equal(13.0, e.BoundsSize.x, 6);

            int id = e.Parts[1].partId;
            Assert.True(e.RemovePart(id));
            Assert.Equal(2.0, e.BoundsSize.x, 6);
            Assert.Equal(100f, e.TotalMass, 3);
        }

        [Fact]
        public void MovePart_InvalidatesAabbCache_EvenWithoutTickManager()
        {
            // headless 下 Find.TickManager 为 null ⇒ tick 恒 -1；只靠 tick 的缓存会返回旧值。
            var e = new SpaceEntity(1, 0);
            e.SetParts(new List<SpacePart> { MakePart(1, 0, 2, 100) });

            e.GetAabbSystem(out var min0, out var max0);
            Assert.Equal(-1.0, min0.x, 6);
            Assert.Equal(1.0, max0.x, 6);

            Assert.True(e.MovePart(1, new DVector3(10, 0, 0)));
            e.GetAabbSystem(out var min1, out var max1);
            Assert.Equal(9.0, min1.x, 6);
            Assert.Equal(11.0, max1.x, 6);
        }

        [Fact]
        public void RemoveMappedPart_KeepMap_IsRefused()
        {
            var e = new SpaceEntity(1, 0);
            var p = MakePart(1, 0, 2, 100);
            p.linkedMapId = 123;
            e.SetParts(new List<SpacePart> { p });

            Assert.False(e.RemovePart(1, PartRemovalPolicy.KeepMap));
            Assert.Single(e.Parts);
        }

        [Fact]
        public void DuplicatePartId_IsRejected()
        {
            var e = new SpaceEntity(1, 0);
            e.SetParts(new List<SpacePart> { MakePart(3, 0, 2, 100) });

            Assert.False(e.AddPart(MakePart(3, 5, 2, 100)));
            Assert.Single(e.Parts);
        }

        [Fact]
        public void PartSetVersion_BumpsOnAddRemoveMove()
        {
            var e = new SpaceEntity(1, 0);
            e.SetParts(new List<SpacePart> { MakePart(1, 0, 2, 100) });

            int v0 = e.partSetVersion;
            e.AddPart(MakePart(0, 5, 2, 50));
            Assert.True(e.partSetVersion > v0);

            int v1 = e.partSetVersion;
            e.MovePart(e.Parts[1].partId, new DVector3(7, 0, 0));
            Assert.True(e.partSetVersion > v1);

            int v2 = e.partSetVersion;
            e.RemovePart(e.Parts[1].partId);
            Assert.True(e.partSetVersion > v2);
        }

        [Fact]
        public void RemovePart_NotifiesComponents_OnDetached()
        {
            var e = new SpaceEntity(1, 0);
            var p = MakePart(1, 0, 2, 100);
            var probe = new DetachProbe();
            p.AddComponent(probe);
            e.SetParts(new List<SpacePart> { p });

            Assert.True(e.RemovePart(1));
            Assert.Equal(1, probe.Detached);
        }

        [Fact]
        public void NewEntity_RotationDefaultsToIdentity()
        {
            // B2 遗留：DQuaternion 默认是零四元数；SpaceEntity 必须显式初值 identity
            // （B3 验收用 new SpaceEntity + 零件姿态时，零四元数与零件旋转相乘会退化）。
            var e = new SpaceEntity(1, 0);
            Assert.Equal(DQuaternion.identity, e.Rotation);
        }
    }
}
