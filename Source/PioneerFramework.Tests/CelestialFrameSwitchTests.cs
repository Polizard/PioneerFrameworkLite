using System.Linq;
using PioneerFramework;
using PioneerFramework.Celestial;
using Verse;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests;

public class CelestialFrameSwitchTests
{
    private readonly ITestOutputHelper output;

    public CelestialFrameSwitchTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void SwitchGroupFrame_ChangesOnlyReference_AndResetsRender()
    {
        using var scope = new CelestialShipBenchScope(1, 0);
        var mgr = Current.Game.GetComponent<CelestialBodyManager>();
        Assert.NotNull(mgr);
        var ids = mgr.GetAllBodyWorldPositions(0).Keys.OrderBy(i => i).ToList();
        Assert.Equal(3, ids.Count);

        Assert.True(PhysicsApi.TryGetGroup(100, out var group));
        int target = ids.First(i => i != group.CelestialBodyId);
        var pos = group.CurrentOrbit.positionSystem.v;
        var vel = group.CurrentOrbit.velocitySystem.v;

        scope.Space.SwitchGroupFrame(group.GroupId, target);

        output.WriteLine($"group={group.GroupId} -> {target}");
        Assert.Equal(target, group.CelestialBodyId);
        Assert.Equal(pos, group.CurrentOrbit.positionSystem.v);
        Assert.Equal(vel, group.CurrentOrbit.velocitySystem.v);
        Assert.Equal(pos, group.physicsRenderPreviousPosition);
        Assert.True(group.OrbitDirty);
    }

    [Fact]
    public void PrecisionManeuver_CancelsStar_FreeFlightKeepsIt()
    {
        using var scope = new CelestialShipBenchScope(2, 0, starRoot: true);
        Assert.True(PhysicsApi.TryGetGroup(100, out var precision));
        Assert.True(PhysicsApi.TryGetGroup(101, out var free));
        precision.EndBurn();
        free.EndBurn();
        precision.plannedManeuvers.Add(default);

        var v0p = precision.CurrentOrbit.velocitySystem.v;
        var v0f = free.CurrentOrbit.velocitySystem.v;
        scope.Advance();

        var dp = precision.CurrentOrbit.velocitySystem.v - v0p;
        var df = free.CurrentOrbit.velocitySystem.v - v0f;
        var diff = (df - dp).magnitude;
        output.WriteLine($"precision={dp.magnitude:E3} free={df.magnitude:E3} diff={diff:E3}");
        Assert.True(diff > 1e-9, "free flight should feel the star");
    }

    [Fact]
    public void IgnoredGravityBody_OptsOutOfStarPull_WhileDefaultGroupKeepsIt()
    {
        using var scope = new CelestialShipBenchScope(2, 0, starRoot: true);
        var mgr = Current.Game.GetComponent<CelestialBodyManager>();
        Assert.NotNull(mgr);
        int starId = mgr.GetAllBodyWorldPositions(0).Keys
            .Select(i => mgr.GetBodyById(i)!).First(b => b.bodyType == CelestialBodyType.Star).bodyId;

        Assert.True(PhysicsApi.TryGetGroup(100, out var keepStar));
        Assert.True(PhysicsApi.TryGetGroup(101, out var ignoreStar));
        keepStar.EndBurn();
        ignoreStar.EndBurn();
        ignoreStar.ignoredGravityBodyIds = new System.Collections.Generic.List<int> { starId };

        var v0k = keepStar.CurrentOrbit.velocitySystem.v;
        var v0i = ignoreStar.CurrentOrbit.velocitySystem.v;
        scope.Advance();

        var dk = keepStar.CurrentOrbit.velocitySystem.v - v0k;
        var di = ignoreStar.CurrentOrbit.velocitySystem.v - v0i;
        var diff = (dk - di).magnitude;
        output.WriteLine($"keepStar={dk.magnitude:E3} ignoreStar={di.magnitude:E3} diff={diff:E3} starId={starId}");
        Assert.True(diff > 1e-9, "default group should feel the star while opt-out group ignores it");
    }
}
