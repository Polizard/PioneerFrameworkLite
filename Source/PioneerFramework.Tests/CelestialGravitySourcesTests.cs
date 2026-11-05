using System.Collections.Generic;
using System.Linq;
using PioneerFramework;
using PioneerFramework.Celestial;
using Verse;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests;

public class CelestialGravitySourcesTests
{
    private readonly ITestOutputHelper output;

    public CelestialGravitySourcesTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void PlanetFrameSourcesIncludeRootAndChildren()
    {
        using var scope = new CelestialShipBenchScope(1, 0);
        var mgr = Current.Game.GetComponent<CelestialBodyManager>();
        Assert.NotNull(mgr);
        var ids = mgr.GetAllBodyWorldPositions(0).Keys.OrderBy(i => i).ToList();
        Assert.Equal(3, ids.Count);
        int rootId = ids[0];
        var members = mgr.GetSystemMembers(rootId);
        Assert.Equal(3, members.Count);
        var sources = new List<GravitySource>();
        mgr.CollectSystemSources(rootId, 0, sources);
        output.WriteLine($"root={rootId} members={members.Count} sources={sources.Count}: " +
            string.Join(",", sources.Select(s => s.bodyId)));
        Assert.Equal(3, sources.Count);
    }

    [Fact]
    public void StarRoot_StarFrameOne_PlanetFrameTwo()
    {
        using var scope = new CelestialShipBenchScope(1, 0, starRoot: true);
        var mgr = Current.Game.GetComponent<CelestialBodyManager>();
        Assert.NotNull(mgr);
        var ids = mgr.GetAllBodyWorldPositions(0).Keys.OrderBy(i => i).ToList();
        Assert.Equal(3, ids.Count);

        var star = ids.Select(i => mgr.GetBodyById(i)!).First(b => b.bodyType == CelestialBodyType.Star);
        var starSources = new List<GravitySource>();
        mgr.CollectSystemSources(star.bodyId, 0, starSources);
        output.WriteLine($"star={star.bodyId} sources={starSources.Count}: " +
            string.Join(",", starSources.Select(s => s.bodyId)));
        Assert.Single(starSources);

        var planet = ids.Select(i => mgr.GetBodyById(i)!).First(b => b.bodyType == CelestialBodyType.Planet);
        var planetSources = new List<GravitySource>();
        mgr.CollectSystemSources(planet.bodyId, 0, planetSources);
        output.WriteLine($"planet={planet.bodyId} sources={planetSources.Count}: " +
            string.Join(",", planetSources.Select(s => s.bodyId)));
        Assert.Equal(2, planetSources.Count);
        Assert.Contains(planetSources, s => s.bodyId == star.bodyId);
    }

    [Fact]
    public void ChildPlanetFrame_IncludesParentPlanet()
    {
        using var scope = new CelestialShipBenchScope(1, 0);
        var mgr = Current.Game.GetComponent<CelestialBodyManager>();
        Assert.NotNull(mgr);
        var ids = mgr.GetAllBodyWorldPositions(0).Keys.OrderBy(i => i).ToList();
        int rootId = ids[0];
        int childId = ids[1];

        var sources = new List<GravitySource>();
        mgr.CollectSystemSources(childId, 0, sources);
        int childFrameRoot = mgr.GetReferenceFrameId(childId);
        output.WriteLine($"child={childId} frameRoot={childFrameRoot} sources={sources.Count}: " +
            string.Join(",", sources.Select(s => s.bodyId)));
        Assert.Equal(2, sources.Count);
        Assert.Equal(childFrameRoot, sources[0].bodyId);
        Assert.Contains(sources, s => s.bodyId == childId);
        Assert.Contains(sources, s => s.bodyId == rootId);
    }

    [Fact]
    public void PrefetchSlotIsKeyedByCentralBody_NotFrameRoot()
    {
        using var scope = new CelestialShipBenchScope(1, 0);
        var mgr = Current.Game.GetComponent<CelestialBodyManager>();
        Assert.NotNull(mgr);
        var ids = mgr.GetAllBodyWorldPositions(0).Keys.OrderBy(i => i).ToList();
        int rootId = ids[0];
        int childId = ids[1];

        mgr.PrefetchSystemSources(rootId, 0);
        mgr.PrefetchSystemSources(childId, 0);
        var rootTable = mgr.TryGetPrefetchedSources(rootId, 0);
        var childTable = mgr.TryGetPrefetchedSources(childId, 0);
        Assert.NotNull(rootTable);
        Assert.NotNull(childTable);
        output.WriteLine($"rootTable[0]={rootTable![0].bodyId} childTable[0]={childTable![0].bodyId}");
        Assert.Equal(rootId, rootTable[0].bodyId);
        Assert.Equal(mgr.GetReferenceFrameId(childId), childTable[0].bodyId);
        Assert.Contains(childTable, s => s.bodyId == childId);
    }
}
