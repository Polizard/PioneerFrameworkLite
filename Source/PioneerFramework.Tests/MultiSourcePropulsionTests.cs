using System.Collections.Generic;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **B6：多图/多源推进聚合**——所有图的工质都作数；网络按 Σ(ve·ṁ)/Σṁ 合并；
    /// 消耗沿各源分摊。纯测试（不依赖 Map 实例）。
    /// </summary>
    public class MultiSourcePropulsionTests
    {
        private sealed class FakeSource : IPropulsionSource
        {
            private readonly List<PropellantNetwork> nets = new();
            public double Mass;
            public double Capacity;

            public FakeSource(PropellantDef def, float ve, float flow, double mass, double capacity)
            {
                var net = new PropellantNetwork { propellant = def };
                net.RegisterEngine(ve, flow);
                nets.Add(net);
                Mass = mass;
                Capacity = capacity;
            }

            public IReadOnlyList<PropellantNetwork> Networks => nets;
            public double GetPropellantMass(PropellantDef def) => Mass;
            public double TryConsumePropellant(PropellantDef def, double kg)
            {
                double take = System.Math.Min(kg, Mass);
                Mass -= take;
                return take;
            }
            public double TotalPropellantMass => Mass;
            public double TotalPropellantCapacity => Capacity;
            public float EffectiveVe => nets.Count > 0 ? nets[0].Ve : 0f;
        }

        [Fact]
        public void Composite_MergesNetworksAndSumsPropellant()
        {
            var def = new PropellantDef { defName = "P" };
            var a = new FakeSource(def, 3000f, 10f, 100.0, 200.0);
            var b = new FakeSource(def, 3200f, 5f, 50.0, 80.0);
            var composite = new CompositePropulsionSource(new List<IPropulsionSource> { a, b }, 1);

            Assert.Single(composite.Networks);
            var net = composite.Networks[0];
            Assert.Equal(15f, net.MassFlow, 3);
            Assert.Equal((3000f * 10f + 3200f * 5f) / 15f, net.Ve, 2);   // Σ(ve·ṁ)/Σṁ = 3066.67
            Assert.Equal(150.0, composite.TotalPropellantMass, 6);
            Assert.Equal(280.0, composite.TotalPropellantCapacity, 6);
        }

        [Fact]
        public void Composite_ConsumesAcrossSources()
        {
            var def = new PropellantDef { defName = "Q" };
            var a = new FakeSource(def, 3000f, 10f, 30.0, 100.0);
            var b = new FakeSource(def, 3000f, 10f, 100.0, 100.0);
            var composite = new CompositePropulsionSource(new List<IPropulsionSource> { a, b }, 2);

            double consumed = composite.TryConsumePropellant(def, 90.0);
            Assert.Equal(90.0, consumed, 6);
            Assert.Equal(0.0, a.Mass, 6);      // 先吃 a 的 30
            Assert.Equal(40.0, b.Mass, 6);
            Assert.Equal(40.0, composite.GetPropellantMass(def), 6);
        }

        [Fact]
        public void NetworkMerge_AddsAxisBreakdown()
        {
            var def = new PropellantDef { defName = "R" };
            var a = new PropellantNetwork { propellant = def };
            a.RegisterEngine(3000f, 10f, 0);   // ThrustAxis.Back
            var b = new PropellantNetwork { propellant = def };
            b.RegisterEngine(3000f, 10f, 0);
            a.MergeFrom(b);

            Assert.Equal(20f, a.MassFlow, 3);
            Assert.Equal(20f, a.FlowByAxis(0), 3);
            Assert.Equal(60000f, a.ThrustByAxis(0), 2);
        }
    }
}
