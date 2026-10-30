using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Components;
using PioneerFramework.GroundAssembly;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 上面级 RCS（2026-10-27，A 路线）：独立工质（肼）+ 打 `rcsSupply` 标签的小罐
    /// ⇒ `PartPropulsionBuilder` 拆出**两条网络**（化学主网 / 肼 RCS 网），主发动机烧不到 RCS 的油。
    /// </summary>
    public class RcsPropulsionTests
    {
        private static readonly PropellantDef Chem = new() { defName = "PF_Propellant_Chemical" };
        private static readonly PropellantDef Hyd = new() { defName = "PF_Propellant_Hydrazine" };
        private static PropellantDef? Lookup(string name)
            => name == Chem.defName ? Chem : name == Hyd.defName ? Hyd : null;

        private static SpacePart Engine(int id, string prop, float ve, float flow, bool rcs = false)
        {
            var p = new SpacePart { partId = id, defName = "e", boundsSizeShip = new DVector3(2, 2, 2) };
            p.AddComponent(new EngineComponentData
            { exhaustVelocity = ve, massFlowRate = flow, propellantDefName = prop, isVectoredRcs = rcs });
            return p;
        }

        private static SpacePart Tank(int id, string prop, double cap, double mass, bool rcsSupply = false)
        {
            var p = new SpacePart { partId = id, defName = "t", boundsSizeShip = new DVector3(2, 2, 2) };
            p.AddComponent(new PropellantTankComponentData
            { propellantDefName = prop, capacityKg = cap, massKg = mass, rcsSupply = rcsSupply });
            return p;
        }

        [Fact]
        public void Builder_SplitsMainAndRcsNetworks()
        {
            var e = new SpaceEntity(1, 0);
            e.SetParts(new List<SpacePart>
            {
                Engine(1, Chem.defName, 3000f, 10f), Tank(2, Chem.defName, 1000.0, 800.0),
                Engine(3, Hyd.defName, 1500f, 2f, rcs: true), Tank(4, Hyd.defName, 200.0, 200.0, rcsSupply: true),
            });

            Assert.True(PartPropulsionBuilder.BuildForMapless(e, Lookup));
            Assert.Equal(2, e.propulsion.Count);

            PropulsionData main = default, rcs = default;
            foreach (var p in e.propulsion)
            {
                if (p.propellant == Chem) main = p;
                else if (p.propellant == Hyd) rcs = p;
            }

            Assert.Equal(3000f, main.ve, 3);
            Assert.Equal(10f, main.massFlow, 3);
            Assert.Equal(800f, main.mass, 3);
            Assert.False(main.isVectoredRcs);

            Assert.Equal(1500f, rcs.ve, 3);
            Assert.Equal(2f, rcs.massFlow, 3);
            Assert.Equal(200f, rcs.mass, 3);
            Assert.True(rcs.isVectoredRcs);   // 独立网络，不参与主推轴聚合
        }

        [Fact]
        public void MainPropellantMass_DoesNotIncludeRcsPool()
        {
            var e = new SpaceEntity(2, 0);
            e.SetParts(new List<SpacePart>
            {
                Engine(1, Chem.defName, 3000f, 10f), Tank(2, Chem.defName, 1000.0, 800.0),
                Engine(3, Hyd.defName, 1500f, 2f, rcs: true), Tank(4, Hyd.defName, 200.0, 200.0, rcsSupply: true),
            });
            PartPropulsionBuilder.BuildForMapless(e, Lookup);

            Assert.Equal(800.0, e.GetPropellantMass(Chem), 3);
            Assert.Equal(200.0, e.GetPropellantMass(Hyd), 3);
        }

        [Fact]
        public void Assembler_RcsCategories_AddComponents()        {
            var specs = new List<DesignPartSpec>
            {
                new() { defName = "Rcs", category = "rcs" },
                new() { defName = "RcsTank", category = "rcs-tank" },
            };
            var defs = new Dictionary<string, ShipPartDef>
            {
                ["Rcs"] = new() { defName = "Rcs", category = "rcs", massKg = 50f, presets = new List<PartComponentPreset>() },
                ["RcsTank"] = new() { defName = "RcsTank", category = "rcs-tank", massKg = 60f, tankCapacityKg = 200f, tankInitialKg = 200f, presets = new List<PartComponentPreset>() },
            };

            var parts = DesignPartAssembler.BuildParts(new RocketDesignSpec { parts = specs }, 1, 1, out _,
                name => defs.TryGetValue(name, out var d) ? d : null, includeUnknownDefs: false, includeCabin: false);

            var eng = parts[0].GetComponent<EngineComponentData>();
            Assert.NotNull(eng);
            Assert.True(eng!.isVectoredRcs);
            Assert.Equal("PF_Propellant_Hydrazine", eng.propellantDefName);

            var tank = parts[1].GetComponent<PropellantTankComponentData>();
            Assert.NotNull(tank);
            Assert.True(tank!.rcsSupply);
            Assert.Equal(200.0, tank.massKg, 3);
        }

        [Fact]
        public void NetworkMask_ReflectsSelection()
        {
            var e = new SpaceEntity(3, 0);
            e.SetParts(new List<SpacePart>
            {
                Engine(1, Chem.defName, 3000f, 10f), Tank(2, Chem.defName, 1000.0, 800.0),
                Engine(3, Hyd.defName, 1500f, 2f, rcs: true), Tank(4, Hyd.defName, 200.0, 200.0, rcsSupply: true),
            });
            PartPropulsionBuilder.BuildForMapless(e, Lookup);

            var g = new SpaceEntityGroup(1, 0);
            Assert.All(g.GetNetworkMask(e), Assert.True);   // 默认全启用

            g.SetBurnNetworkOverride(new HashSet<PropellantDef> { Hyd }, true);
            bool[] mask = g.GetNetworkMask(e);
            Assert.Equal(e.propulsion.Count, mask.Length);
            for (int i = 0; i < e.propulsion.Count; i++)
            {
                if (e.propulsion[i].propellant == Hyd) Assert.True(mask[i]);
                else Assert.False(mask[i]);
            }
            Assert.True(g.burnUseRcs);
        }
    }
}
