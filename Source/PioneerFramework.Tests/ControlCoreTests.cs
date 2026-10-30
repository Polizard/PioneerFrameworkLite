using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Components;
using PioneerFramework.GroundAssembly;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **控制核心的判据**（2026-10-26；用户口径 = KSP 命令舱/探测器核心）。
    ///
    /// 三条语义：
    /// · **装过才要求**：没装过控制核心的舰（EVA/导弹/无人机/调试船）完全不受影响；
    /// · **装过就粘住**：一旦装过，失去核心 ⇒ **永久失控**（不因残骸被移除而复活）；
    /// · **失控 = 不能点火/操舵**：`StartBurn` 拒绝并置读数位；操舵台直接关闭。
    /// </summary>
    public class ControlCoreTests
    {
        private static SpacePart Part(int id, string def, params PartComponentData[] comps)
        {
            var p = new SpacePart { partId = id, defName = def, boundsSizeShip = new DVector3(2, 2, 2) };
            p.AddComponent(new HullMassComponent { mass = 100f });
            if (comps != null)
                for (int i = 0; i < comps.Length; i++) p.AddComponent(comps[i]);
            return p;
        }

        [Fact]
        public void Component_IsRegistered_WithControlRole()
        {
            ComponentRegistry.RegisterBuiltIns();
            Assert.True(ComponentRegistry.IsRegistered("ControlCoreComponentData"));
            Assert.Equal(PartRole.Control, new ControlCoreComponentData().Role);
        }

        [Fact]
        public void Entity_WithCore_IsControllable()
        {
            var e = new SpaceEntity(1, 0);
            e.SetParts(new List<SpacePart>
            {
                Part(1, "core", new ControlCoreComponentData()),
                Part(2, "hull"),
            });

            Assert.True(e.RequiresControlCore);
            Assert.True(e.HasIntactControlCore);
            Assert.True(e.IsControllable);
        }

        [Fact]
        public void Entity_WithoutCore_IsExempt()
        {
            var e = new SpaceEntity(2, 0);
            e.SetParts(new List<SpacePart> { Part(1, "hull") });

            Assert.False(e.RequiresControlCore);
            Assert.False(e.HasIntactControlCore);
            Assert.True(e.IsControllable);   // 没装过 ⇒ 不受影响（老船零变化）
        }

        [Fact]
        public void DestroyedCore_LeavesEntityPermanentlyUncontrollable()
        {
            var e = new SpaceEntity(3, 0);
            var core = Part(1, "core", new ControlCoreComponentData());
            e.SetParts(new List<SpacePart> { core, Part(2, "hull") });
            Assert.True(e.IsControllable);

            core.ApplyDamage(9999f);
            e.MarkPartDestroyed(core.partId);

            Assert.True(e.RequiresControlCore);      // 粘住
            Assert.False(e.HasIntactControlCore);
            Assert.False(e.IsControllable);

            // 把残骸拆掉也不会"复活"（粘性状态）
            e.RemovePart(core.partId);
            Assert.True(e.RequiresControlCore);
            Assert.False(e.IsControllable);
        }

        [Fact]
        public void AddingCoreAtRuntime_MakesEntityControllable()
        {
            var e = new SpaceEntity(4, 0);
            e.SetParts(new List<SpacePart> { Part(1, "hull") });
            Assert.True(e.IsControllable);

            Assert.True(e.AddPart(Part(2, "core", new ControlCoreComponentData())));

            Assert.True(e.RequiresControlCore);
            Assert.True(e.HasIntactControlCore);
        }

        [Fact]
        public void Group_WithLostCore_RefusesIgnition()
        {
            var g = new SpaceEntityGroup(1, 0)
            {
                RequiresControlCore = true,
                HasIntactControlCore = false,
            };

            g.StartBurn(new DVector3(100, 0, 0));

            Assert.False(g.HasActiveBurn);
            Assert.True(g.BurnIgnitionBlockedNoControl);
        }

        [Fact]
        public void Group_WithCore_CanIgnite()
        {
            var g = new SpaceEntityGroup(1, 0)
            {
                RequiresControlCore = true,
                HasIntactControlCore = true,
            };

            g.StartBurn(new DVector3(100, 0, 0));

            Assert.True(g.HasActiveBurn);
            Assert.False(g.BurnIgnitionBlockedNoControl);
        }

        [Fact]
        public void Group_WithoutCore_CanStillIgnite()
        {
            var g = new SpaceEntityGroup(2, 0);   // 没装过核心 ⇒ 豁免

            g.StartBurn(new DVector3(100, 0, 0));

            Assert.True(g.HasActiveBurn);
            Assert.False(g.BurnIgnitionBlockedNoControl);
        }

        [Fact]
        public void Assembler_ControlCategory_AddsControlCore()
        {
            var def = new ShipPartDef
            {
                defName = "Test_ControlCore",
                label = "测试控制核心",
                category = "control",
                massKg = 300f,
                presets = new List<PartComponentPreset>(),
            };
            var spec = new RocketDesignSpec
            {
                parts = new List<DesignPartSpec>
                {
                    new() { defName = "Test_ControlCore", category = "control", quantity = 1 },
                },
            };

            var parts = DesignPartAssembler.BuildParts(spec, 10, 10, out _, _ => def);

            Assert.Equal(2, parts.Count);
            Assert.NotNull(parts[1].GetComponent<ControlCoreComponentData>());

            var e = new SpaceEntity(9, 0);
            e.SetParts(parts);
            Assert.True(e.RequiresControlCore);
            Assert.True(e.IsControllable);
        }
    }
}
