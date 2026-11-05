using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using PioneerFramework.Celestial;
using UnityEngine;
using Verse;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests;

/// <summary>
/// 2026-11-04/05 事故回归：源表**位置基准是帧根**（与物理步读的 positionSystem 同系），
/// 成员 = 帧根 + 中心天体 + 中心的子 + 中心的整条祖先链（含父恒星）——所有 Group 都受恒星引力。
/// 月球域差分里的太阳均匀项由 ComputeFieldMinusPlan 递归抵消父链消掉，
/// 所以月球表含太阳、但差分仍必须是潮汐（≈0.29%），且月球 3300 km 局部引力 ≈ μ/r² ≈ 0.45。
/// </summary>
public class PlanetaryFrameSourceTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly ITestOutputHelper output;

    public PlanetaryFrameSourceTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void EarthAndMoonTablesBothContainTheStar()
    {
        var (mgr, earth, moon, sun) = LoadSolarSystem();

        var earthSources = new List<GravitySource>();
        var moonSources = new List<GravitySource>();
        mgr.CollectSystemSources(earth, 0, earthSources);
        mgr.CollectSystemSources(moon, 0, moonSources);
        output.WriteLine("earth=[" + string.Join(",", earthSources.Select(s => s.bodyId)) + "]");
        output.WriteLine("moon=[" + string.Join(",", moonSources.Select(s => s.bodyId)) + "]");

        Assert.Equal(2, earthSources.Count);   // 地球 + 月球；恒星在希尔盒内被非惯性抵消
        Assert.Equal(earth, earthSources[0].bodyId);
        Assert.Contains(earthSources, s => s.bodyId == moon);
        Assert.DoesNotContain(earthSources, s => s.bodyId == sun);

        Assert.Equal(2, moonSources.Count);
        Assert.Equal(earth, moonSources[0].bodyId);
        Assert.Contains(moonSources, s => s.bodyId == moon);
        Assert.DoesNotContain(moonSources, s => s.bodyId == sun);
    }

    [Fact]
    public void PrefetchIsPerCentralBody_ButBothTablesKeepTheStar()
    {
        var (mgr, earth, moon, sun) = LoadSolarSystem();

        mgr.PrefetchSystemSources(earth, 0);
        Assert.Null(mgr.TryGetPrefetchedSources(moon, 0)); // 槽按中心天体分，不按帧根

        var earthShared = mgr.TryGetPrefetchedSources(earth, 0);
        Assert.NotNull(earthShared);
        Assert.Equal(earth, earthShared![0].bodyId);

        mgr.PrefetchSystemSources(moon, 0);
        var moonShared = mgr.TryGetPrefetchedSources(moon, 0);
        Assert.NotNull(moonShared);
        Assert.Equal(earth, moonShared![0].bodyId); // 位置基准仍是地球
        Assert.DoesNotContain(moonShared, s => s.bodyId == sun); // 希尔盒内不计恒星
        Assert.NotSame(earthShared, moonShared);
        output.WriteLine($"earthTable[0]={earthShared[0].bodyId} moonTable[0]={moonShared[0].bodyId} moonCount={moonShared.Count}");
    }

    [Fact]
    public void MoonLocalGravityMatchesMuOverR2_AndDifferentialIsTide()
    {
        var (mgr, _, moon, _) = LoadSolarSystem();

        var all = new List<GravitySource>();
        mgr.CollectSystemSources(moon, 0, all);
        var moonDef = mgr.GetBodyById(moon)!;
        var moonPos = mgr.GetBodyPositionParent(moon, 0);
        var moonVel = mgr.GetBodyVelocityParent(moon, 0);
        var offset = new DVector3(3.3e6, 4.0e5, 0.0);
        var probe = moonPos + offset;           // 地球系（与 positionSystem 同系）
        var probeVel = moonVel + new DVector3(-120.0, 1500.0, 0.0);

        var moonSource = default(GravitySource);
        for (int i = 0; i < all.Count; i++)
            if (all[i].bodyId == moon) moonSource = all[i];
        var moonOnly = new List<GravitySource> { moonSource };
        NBodyIntegrator.FieldAccelJerk(probe, probeVel, moonOnly, out var local, out _);
        mgr.ComputeFieldMinusPlan(probe, probeVel, moon, all, out var tidal, out _);

        double expectedLocal = moonDef.gravitationalParameter / offset.magnitude / offset.magnitude;
        double ratio = tidal.magnitude / local.magnitude;
        output.WriteLine($"local={local.magnitude:F4} expected={expectedLocal:F4} ratio={ratio:P4} tidal={tidal.magnitude:E3}");
        Assert.InRange(local.magnitude, 0.44, 0.47);   // 月球 3300 km：μ/r² ≈ 0.45；基准错位时会掉到 ~0.001
        Assert.InRange(ratio, 0.001, 0.006);           // 潮汐带；太阳项未递归抵消时会到 ~13.7%
    }

    [Fact]
    public void SoiOffsetPrefetchMatchesParentChain()
    {
        var (mgr, earth, moon, sun) = LoadSolarSystem();
        mgr.PrefetchSoiOffsets(sun, 0);
        Assert.True(mgr.TryGetSoiOffset(sun, earth, 0, out var earthOff));
        Assert.True(mgr.TryGetSoiOffset(sun, moon, 0, out var moonOff));

        DVector3 earthRelSun = mgr.GetBodyPositionParent(earth, 0);
        DVector3 moonRelSun = mgr.GetBodyPositionParent(moon, 0) + earthRelSun;
        output.WriteLine($"earthOff={earthOff.Position.magnitude:E3} moonOff={moonOff.Position.magnitude:E3}");
        Assert.True((earthOff.Position + earthRelSun).magnitude < 1e-6);
        Assert.True((moonOff.Position + moonRelSun).magnitude < 1e-6);
    }

    private static (CelestialBodyManager mgr, int earth, int moon, int sun) LoadSolarSystem()
    {
        var root = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Defs", "CelestialBodies", "CelestialBodies_SolarSystem.xml")).Root;
        var system = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Defs", "CelestialFramework", "CelestialSystems.xml"))
            .Root.Elements().Single().Element("entries").Elements("li").ToDictionary(entry => (string)entry.Element("key"));
        string EntryPath(XElement entry) => entry.Element("parentEntry") is { } parent ? EntryPath(system[parent.Value]) + "/" + entry.Element("key").Value + "[0]"
            : entry.Element("key").Value + "[0]";
        var entries = system.Values.OrderBy(EntryPath, StringComparer.Ordinal).ToArray();
        var xmlBodies = root.Elements().ToDictionary(element => element.Element("defName").Value);
        var byName = entries.Select((entry, index) => {
            var e = xmlBodies[entry.Element("bodyDef").Value];
            var comps = e.Element("comps").Elements("li").ToArray();
            var geometry = comps.Single(comp => comp.Attribute("Class").Value.EndsWith("CompProperties_SphericalBody", StringComparison.Ordinal));
            var physics = comps.Single(comp => comp.Attribute("Class").Value.EndsWith("CompProperties_ExternalPhysicsBinding", StringComparison.Ordinal));
            var decoded = ExistingEphemerisParameters.Parse(new(physics.Element("providerId").Value, physics.Element("modeId").Value,
                (int?)physics.Element("initializationVersion") ?? 1, Convert.FromBase64String(physics.Element("initializationPayload").Value)));
            Assert.True(decoded.Success);
            var parameters = decoded.Value;
            return new CelestialBodyInfo {
                defName = e.Element("defName").Value, bodyId = index, bodyType = Enum.Parse<CelestialBodyType>(e.Element("category").Value),
                radius = (double)geometry.Element("radiusMeters").Element("value"),
                gravitationalParameter = (double)physics.Element("gravitationalParameter").Element("value"),
                orbitRadius = parameters.OrbitRadius, orbitPeriod = parameters.OrbitPeriod, initialPhase = parameters.InitialPhase,
                orbitNormal = (Vector3)parameters.OrbitNormal,
                parentBodyDefName = entry.Element("parentEntry") is { } parent ? system[parent.Value].Element("bodyDef").Value : null };
        }).ToDictionary(body => body.defName);
        foreach (var body in byName.Values)
            typeof(CelestialBodyInfo).GetProperty(nameof(CelestialBodyInfo.ParentBodyId)).SetValue(body,
                body.parentBodyDefName != null ? byName[body.parentBodyDefName].bodyId : -1);
        var bodies = byName.Values.ToDictionary(b => b.bodyId);
        var manager = new CelestialBodyManager(null);
        typeof(CelestialBodyManager).GetField("initialized", Flags).SetValue(manager, true);
        typeof(CelestialBodyManager).GetField("bodiesById", Flags).SetValue(manager, bodies);
        return (manager,
            byName["Earth"].bodyId,
            byName["Moon"].bodyId,
            byName["Sun"].bodyId);
    }
}
