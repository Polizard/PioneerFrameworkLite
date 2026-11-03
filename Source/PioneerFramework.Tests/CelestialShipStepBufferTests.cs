using System.Reflection;
using System;
using System.Linq;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialShipStepBufferTests
{
    [Fact]
    public void EveryInstanceFieldHasAnExplicitInputCopyOrBufferPolicy()
    {
        foreach (var type in new[] { typeof(SpaceGameComp), typeof(SpaceEntityGroup), typeof(SpaceEntity), typeof(SpacePart), typeof(PropulsionData), typeof(SpaceMapComp) })
        {
            var policy = (string)type.GetField("CelestialInputFieldSchema", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue();
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Select(field => field.Name).Except(new[] { "celestialStepBuffers", "celestialBuffers", "celestialSource" }, StringComparer.Ordinal).Order().ToArray();
            Assert.Equal(policy!.Split('|').Order().ToArray(), fields);
        }
    }
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
        var field = typeof(SpaceGameComp).GetField("celestialStepBuffers", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int tick = 0; tick < expected.Length; tick++)
        { field.SetValue(fresh.Space, null); fresh.Advance(); Assert.Equal(expected[tick], Capture(fresh)); }
    }
    [Fact]
    public void AddedAndRemovedGroupsRefreshTheSamePrivateGraph()
    {
        using var host = new CelestialShipBenchScope(0, 0);
        host.Advance();
        var field = typeof(SpaceGameComp).GetField("celestialStepBuffers", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var buffers = field.GetValue(host.Space)!;
        var prepared = (SpaceGameComp)buffers.GetType().GetProperty("Prepared", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(buffers)!;
        var added = new SpaceEntityGroup(999, 2) { IsActive = false, CelestialBodyId = 1,
            CurrentOrbit = new() { positionSystem = (SystemVec3)new DVector3(1e7, 0, 0), rotation = DQuaternion.identity } };
        host.Space.RegisterGroup(added); host.Advance();
        Assert.NotNull(prepared.GetGroupById(999));
        Assert.NotSame(added, prepared.GetGroupById(999));
        host.Space.DestroyGroup(999, "buffer topology regression"); host.Advance();
        Assert.Null(prepared.GetGroupById(999));
    }
    [Fact]
    public void SteadyTopologyReusesThePrivateGraphWithoutReplacingLiveShips()
    {
        using var host = new CelestialShipBenchScope(32, 4);
        var live = host.Space.GetGroupById(100);
        var field = typeof(SpaceGameComp).GetField("celestialStepBuffers", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        host.Advance();
        var buffers = field!.GetValue(host.Space);
        Assert.NotNull(buffers);
        var prepared = buffers!.GetType().GetProperty("Prepared", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(buffers) as SpaceGameComp;
        var privateShip = prepared!.GetGroupById(100);
        var privateEntity = prepared.GetEntityById(1000);
        for (int i = 0; i < 4; i++) host.Advance();
        Assert.Same(buffers, field.GetValue(host.Space));
        Assert.Same(privateShip, prepared.GetGroupById(100));
        Assert.Same(privateEntity, prepared.GetEntityById(1000));
        Assert.Same(live, host.Space.GetGroupById(100));
        Assert.NotSame(live, privateShip);
    }
}
