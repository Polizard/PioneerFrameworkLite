using System.Collections.Generic;
using PioneerFramework.Components;
using PioneerFramework.GroundAssembly;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **B6：多舱室/多图——Part↔Map 双向指针与主图语义**（纯实体侧，不依赖 Map 实例）。
    /// </summary>
    public class MultiMapBindingTests
    {
        private static SpacePart Part(int id, int linkedMapId)
            => new()
            {
                partId = id,
                defName = "PF_ShipPart_Cube",
                positionShip = new DVector3(id * 10, 0, 0),
                boundsSizeShip = new DVector3(4, 4, 4),
                hp = 100f,
                linkedMapId = linkedMapId,
            };

        [Fact]
        public void GetMapIds_ReturnsAllLinkedMaps_InPartOrder()
        {
            var e = new SpaceEntity(1, 0);
            var a = Part(1, 11);
            var b = Part(2, 22);
            var c = Part(3, -1);
            e.SetParts(new List<SpacePart> { a, b, c });
            e.MapId = 11;
            e.HasMap = true;

            Assert.Equal(new[] { 11, 22 }, e.GetMapIds());
            Assert.Same(a, e.GetPartForMap(11));
            Assert.Same(b, e.GetPartForMap(22));
            Assert.Same(a, e.GetPrimaryMapPart());
            Assert.Null(e.GetPartForMap(99));
        }

        [Fact]
        public void GetMapIds_DeduplicatesSameMapOnMultipleParts()
        {
            // 防御：同一张图误挂两件时只报一次；Part→Map 反查取第一件。
            var e = new SpaceEntity(2, 0);
            var a = Part(1, 33);
            var b = Part(2, 33);
            e.SetParts(new List<SpacePart> { a, b });
            e.MapId = 33;

            Assert.Single(e.GetMapIds());
            Assert.Same(a, e.GetPartForMap(33));
        }

        [Fact]
        public void GetMapIds_LegacyFallback_UsesPrimaryMapId_WhenNoPartLinked()
        {
            var e = new SpaceEntity(3, 0);
            e.SetParts(new List<SpacePart> { Part(1, -1), Part(2, -1) });
            e.MapId = 7;

            Assert.Equal(new[] { 7 }, e.GetMapIds());
            Assert.Null(e.GetPartForMap(7));   // 旧档可能只有实体级 MapId，没有 Part→Map 指针
            Assert.Null(e.GetPrimaryMapPart());
        }

        [Fact]
        public void PrimaryMap_IsFirstLinkedPart_NotLast()
        {
            // 一 part 一图：后续图不覆盖主图；MapId 保持第一个挂图件。
            var e = new SpaceEntity(4, 0);
            var a = Part(1, 100);
            var b = Part(2, 200);
            a.linkedMapId = 100;
            b.linkedMapId = 200;
            e.SetParts(new List<SpacePart> { a, b });
            e.MapId = 100;

            Assert.Equal(100, e.MapId);
            Assert.Same(a, e.GetPrimaryMapPart());
            Assert.Equal(2, e.GetMapIds().Count);
        }

        [Fact]
        public void OnMapRemoved_ClearsPointer_AndAdvancesPrimary()
        {
            var e = new SpaceEntity(5, 0);
            var a = Part(1, 11);
            var b = Part(2, 22);
            e.SetParts(new List<SpacePart> { a, b });
            e.MapId = 11;
            e.HasMap = true;

            e.OnMapRemoved(11);
            Assert.Equal(-1, a.linkedMapId);
            Assert.Equal(22, e.MapId);
            Assert.True(e.HasMap);
            Assert.Equal(new[] { 22 }, e.GetMapIds());

            e.OnMapRemoved(22);
            Assert.Equal(-1, e.MapId);
            Assert.False(e.HasMap);
            Assert.Empty(e.GetMapIds());
        }


        [Fact]
        public void SealedPart_Adds100kgPlaceholder()
        {
            var open = new SpacePart { partId = 1, boundsSizeShip = new DVector3(2, 2, 2) };
            open.AddComponent(new HullMassComponent { mass = 500f });
            Assert.Equal(500f, open.Mass, 3);

            var sealedPart = new SpacePart { partId = 2, boundsSizeShip = new DVector3(2, 2, 2), isSealed = true };
            sealedPart.AddComponent(new HullMassComponent { mass = 500f });
            Assert.Equal(600f, sealedPart.Mass, 3);   // 500 结构 + 100 封闭占位

            var emptySealed = new SpacePart { partId = 3, boundsSizeShip = new DVector3(2, 2, 2), isSealed = true };
            Assert.Equal(SpacePart.SealedPlaceholderMassKg, emptySealed.Mass, 3);   // 空封闭件 = 100（不是 1kg 地板）
        }

        [Fact]
        public void Assembler_DefaultsSealed_ByPartCountHabit()
        {
            var single = DesignPartAssembler.BuildParts(
                new RocketDesignSpec { parts = new List<DesignPartSpec>() }, 10, 10, out SpacePart cabinSingle);
            Assert.False(cabinSingle.isSealed);   // 单 part：开放

            var multiSpec = new RocketDesignSpec
            {
                parts = new List<DesignPartSpec>
                {
                    new() { defName = "AnyThruster", category = "thruster", quantity = 1 }
                }
            };
            DesignPartAssembler.BuildParts(multiSpec, 10, 10, out SpacePart cabinMulti);
            Assert.True(cabinMulti.isSealed);   // 多 part：封闭（习惯默认，非机制）
        }

        [Fact]
        public void PartAwareMapLocalToShip_AddsPartPose_AndRoundTrips()
        {
            var part = new SpacePart
            {
                partId = 1,
                positionShip = new DVector3(10, 2, 4),
                rotationShip = DQuaternion.AngleAxis(System.Math.PI / 2.0, DVector3.up),
            };

            UnityEngine.Vector3 local = new(1f, 0f, 0f);
            UnityEngine.Vector3 ship = MapCoordMapper.MapLocalToShip(local, part);
            UnityEngine.Vector3 back = MapCoordMapper.ShipToMapLocal(ship, part);

            Assert.Equal(local.x, back.x, 5);
            Assert.Equal(local.y, back.y, 5);
            Assert.Equal(local.z, back.z, 5);
            // 地图中心（local=0）必须落在 part 位置
            UnityEngine.Vector3 center = MapCoordMapper.MapLocalToShip(UnityEngine.Vector3.zero, part);
            Assert.Equal(10f, center.x, 4);
            Assert.Equal(2f, center.y, 4);
            Assert.Equal(4f, center.z, 4);
        }


        [Fact]
        public void ArchivedMapList_AddRemoveAndPartLookup()
        {
            var e = new SpaceEntity(6, 0);
            e.AddArchivedMap(11, 1);
            e.AddArchivedMap(22, 2);
            Assert.Equal(2, e.archivedMapIds.Count);
            Assert.Equal(22, e.ArchivedMapId);          // 最近一张（单图兼容）
            Assert.Equal(11, e.GetArchivedMapForPart(1));
            Assert.Equal(22, e.GetArchivedMapForPart(2));
            Assert.Equal(-1, e.GetArchivedMapForPart(3));

            e.AddArchivedMap(11, 1);                    // 去重
            Assert.Equal(2, e.archivedMapIds.Count);

            Assert.True(e.RemoveArchivedMap(11));
            Assert.Single(e.archivedMapIds);
            Assert.Equal(-1, e.GetArchivedMapForPart(1));
            Assert.Equal(22, e.ArchivedMapId);
        }

        [Fact]
        public void PartAwareMapLocalToShip_NullPart_IsIdentity()
        {
            UnityEngine.Vector3 local = new(3f, -1f, 7f);
            Assert.Equal(local, MapCoordMapper.MapLocalToShip(local, null));
            Assert.Equal(local, MapCoordMapper.ShipToMapLocal(local, null));
        }
    }
}
