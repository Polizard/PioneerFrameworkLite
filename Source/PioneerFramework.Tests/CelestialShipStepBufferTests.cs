using System;
using System.Linq;
using System.Reflection;
using PioneerFramework.Components;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialShipStepBufferTests
{
    [Fact]
    public void ManualFuelAndCombatCacheCopiesAlsoGuardTheirFullFieldInventory()
    {
        void Check(Type type, string fields) => Assert.Equal(fields.Split('|').Order().ToArray(),
            type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Select(f => f.Name).Order().ToArray());
        // 2026-10-31（合并）：十三批给推进剂罐加了 RCS 供给三字段（rcsSupply/rcsEfficiency/feedEnabled）。
        Check(typeof(PioneerFramework.Components.PropellantTankComponentData), "propellantDefName|capacityKg|massKg|leaked|rcsSupply|rcsEfficiency|feedEnabled");
        Check(typeof(SpaceEntityGroup.PhysicsStateCache), "baseTick|startPos|startVel|acc|jerk|dt|offset");
    }
    [Theory]
    [InlineData(TimeScaleLevel.Normal)]
    [InlineData(TimeScaleLevel.Fast)]
    public void ReusedAndFreshGraphsProduceBitwiseIdenticalBurnsAcrossManyCommits(TimeScaleLevel level)
    {
        long[][] expected = new long[100][];
        long[] Capture(CelestialShipBenchScope host) => Enumerable.Range(0, 32).SelectMany(index => {
            var group = host.Space.GetGroupById(100 + index);
            var state = group.CurrentOrbit;
            return new[] { state.positionSystem.v.x, state.positionSystem.v.y, state.positionSystem.v.z,
                state.velocitySystem.v.x, state.velocitySystem.v.y, state.velocitySystem.v.z,
                state.rotation.x, state.rotation.y, state.rotation.z, state.rotation.w,
                host.Space.GetEntityById(1000 + index).TotalPropellantMass }.Select(BitConverter.DoubleToInt64Bits);
        }).ToArray();
        using (var reused = new CelestialShipBenchScope(32, 4))
        {
            reused.SetSpeed(level);
            for (int tick = 0; tick < expected.Length; tick++) { reused.Advance(); expected[tick] = Capture(reused); }
        }
        using var fresh = new CelestialShipBenchScope(32, 4);
        fresh.SetSpeed(level);
        for (int tick = 0; tick < expected.Length; tick++)
        { fresh.Advance(); Assert.Equal(expected[tick], Capture(fresh)); }
    }
    [Fact]
    public void ReusedAndFreshGraphsWithPartsProduceBitwiseIdenticalBurns()
    {
        const int ships = 16, partsPerShip = 20, commits = 30;
        long[][] expected = new long[commits][];
        long[] Capture(CelestialShipBenchScope host) => Enumerable.Range(0, ships).SelectMany(index => {
            var state = host.Space.GetGroupById(100 + index).CurrentOrbit;
            return new[] { state.positionSystem.v.x, state.positionSystem.v.y, state.positionSystem.v.z,
                state.velocitySystem.v.x, state.velocitySystem.v.y, state.velocitySystem.v.z,
                state.rotation.x, state.rotation.y, state.rotation.z, state.rotation.w,
                host.Space.GetEntityById(1000 + index).TotalPropellantMass }.Select(BitConverter.DoubleToInt64Bits);
        }).ToArray();
        using (var reused = new CelestialShipBenchScope(ships, 4, partsPerShip))
            for (int tick = 0; tick < expected.Length; tick++) { reused.Advance(); expected[tick] = Capture(reused); }
        using var fresh = new CelestialShipBenchScope(ships, 4, partsPerShip);
        for (int tick = 0; tick < expected.Length; tick++)
        { fresh.Advance(); Assert.Equal(expected[tick], Capture(fresh)); }
    }
}
