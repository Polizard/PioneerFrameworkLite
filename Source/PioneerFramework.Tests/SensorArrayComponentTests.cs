using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Components;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **[V7] 传感器组件的聚合链**（2026-09-24）：`SensorArrayComponentData` →
    /// `SpacePart.Stats`（部件级累加）→ `SpaceEntity.AggregatedStats`（实体级求和）。
    ///
    /// 这条链是"给量纲"的全部内容，而它**不需要游戏**就能验（`SpacePart` / `SpaceEntity` 都是普通类）
    /// ⇒ 把它钉在这里，游戏内只剩"编队级派生量 = 实体级求和"那一半（`FrameworkSelfTest` 的 DetectTech 项）。
    ///
    /// ⚠ 变异陷阱（写这条时特意避开的）：`SpacePart.Stats` 对 `mass &lt;= 0` 有 `= 1f` 的兜底 ⇒
    /// 若只装传感器组件不装质量组件，`mass` 会被兜成 1kg。本用例**质量与传感器都装**，
    /// 另外单独断言"纯传感器部件的 sensorRange 仍进得来"（兜底不该吃掉传感器贡献）。
    /// </summary>
    public class SensorArrayComponentTests
    {
        private static SpacePart PartWith(params PartComponentData[] comps)
        {
            var part = new SpacePart
            {
                partId = 1,
                defName = "PF_ShipPart_Cube",
                positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(2, 2, 2),
                hp = 100f
            };
            foreach (var c in comps) part.AddComponent(c);
            return part;
        }

        private static SpaceEntity EntityWith(SpacePart part)
        {
            var e = new SpaceEntity(1, 0) { HasMap = false };
            e.SetRotation(UnityEngine.Quaternion.identity);
            e.SetParts(new List<SpacePart> { part });
            return e;
        }

        [Fact]
        public void Component_WritesAllThreeSlots()
        {
            var part = PartWith(new HullMassComponent { mass = 100f },
                new SensorArrayComponentData { passiveRangeMeters = 1_000_000f, ecmResist = 0.5f, eccmResist = 0.25f });

            var s = part.Stats;
            Assert.Equal(1_000_000f, s.sensorRange, 3);
            Assert.Equal(0.5f, s.ecmResist, 4);
            Assert.Equal(0.25f, s.eccmResist, 4);
        }

        [Fact]
        public void TwoArrays_SumAtEntityLevel()
        {
            // 一艘船装两台阵列 ⇒ 实体级聚合 = 两台之和（与其余字段同"求和"语义，见 RecalculateShipStats）
            var part = PartWith(new HullMassComponent { mass = 100f },
                new SensorArrayComponentData { passiveRangeMeters = 400_000f, ecmResist = 0.5f },
                new SensorArrayComponentData { passiveRangeMeters = 600_000f, ecmResist = 0.25f });

            var agg = EntityWith(part).AggregatedStats;
            Assert.Equal(1_000_000f, agg.sensorRange, 3);
            Assert.Equal(0.75f, agg.ecmResist, 4);
        }

        [Fact]
        public void SensorOnlyPart_StillContributesRange()
        {
            // 只装传感器（无质量组件）⇒ 质量被兜成 1kg，但**传感器的贡献不能被兜底吃掉**
            var part = PartWith(new SensorArrayComponentData { passiveRangeMeters = 250_000f });

            var agg = EntityWith(part).AggregatedStats;
            Assert.Equal(250_000f, agg.sensorRange, 3);
            Assert.True(agg.mass >= 1f, "质量兜底应仍生效（纯传感器部件不会变成 0 质量）");
        }

        [Fact]
        public void Registry_KnowsTheTypeId_OnceTheTypeIsInUse()
        {
            // ⚠ 语义要说准：`ComponentRegistry` 靠**静态构造器懒注册** —— 类型**第一次被实例化**时才注册
            // （`HullMassComponent` / `RoomComponentData` 同样的写法，也是计划稿 [V7] 示例的写法）。
            // 所以"还没用过就问 IsRegistered"本来就是假 —— 第一版用例就是这么写的（错的是断言，不是生产）。
            // 为什么懒注册在这里是安全的：① 读档走 RimWorld 自己的 Deep 序列化（按**类型名**解析并实例化
            // ⇒ 构造器必然跑过）；② `Clone()` 只在**活实例**上调用（类型此刻必然已注册）。
            // 本用例就按这个真实时序断言，不假装它是"启动即注册"。
            var used = new SensorArrayComponentData();
            Assert.True(ComponentRegistry.IsRegistered(used.TypeId),
                "实例化后仍查不到 ⇒ 读档/克隆会静默丢组件");
            Assert.Equal(nameof(SensorArrayComponentData), used.TypeId);
        }

        [Fact]
        public void Clone_RoundTripsThroughBinaryChannel()
        {
            // `PartComponentData.Clone` 默认走 Write/Read 二进制通道 ⇒ 三个字段都要在里面
            var src = new SensorArrayComponentData { passiveRangeMeters = 777_000f, ecmResist = 1.5f, eccmResist = 2.5f };
            var copy = (SensorArrayComponentData)src.Clone();

            Assert.Equal(777_000f, copy.passiveRangeMeters, 3);
            Assert.Equal(1.5f, copy.ecmResist, 4);
            Assert.Equal(2.5f, copy.eccmResist, 4);
        }
    }
}
