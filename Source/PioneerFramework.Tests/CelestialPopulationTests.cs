using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using PioneerFramework.Celestial;
using RimWorld.Planet;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialPopulationTests
{
    [Fact]
    public void NewWorldExpansionKeepsTheAuthoredIdsAndIsIndependentOfDefOrdering()
    {
        using var xml = new CelestialXmlTestScope();
        using var recipes = new ShippedLocalMapRecipeDefs();
        var fixture = new Fixture();
        var authored = CelestialSystemGenerator.Expand(fixture.System, 384).Value;
        var first = CelestialWorldBootstrap.ExpandNewWorld(fixture.System, 384, fixture.Policy).Value;
        fixture.Policy.bodyRules.Reverse();
        fixture.Policy.distanceBands.Reverse();
        var reordered = new CelestialSystemTemplate(fixture.System.DefinitionId, fixture.System.RootSurfaceEntry, fixture.System.Entries.Reverse());
        var second = CelestialWorldBootstrap.ExpandNewWorld(reordered, 384, fixture.Policy).Value;
        Assert.Equal(authored.RootSurfaceId, first.Manifest.RootSurfaceId);
        Assert.All(authored.Items, item => Assert.Contains(first.Manifest.Items, other => other.Id == item.Id && FrozenPayload(other) == FrozenPayload(item)));
        Assert.Equal(first.Manifest.Items.Select(item => (item.Id, FrozenPayload(item))), second.Manifest.Items.Select(item => (item.Id, FrozenPayload(item))));
        var differentSeed = CelestialWorldBootstrap.ExpandNewWorld(reordered, 385, fixture.Policy).Value;
        Assert.Contains(first.Manifest.Items.Where(item => item.ParentId != null), item =>
            differentSeed.Manifest.Items.Any(other => other.Id == item.Id && FrozenPayload(other) != FrozenPayload(item)));
        Assert.InRange(first.Diagnostics.RequestedPlanets, 4, 8);
        Assert.InRange(first.Diagnostics.RequestedDwarfs, 1, 4);
        Assert.InRange(first.Diagnostics.AcceptedPlanets, 4, 8);
        Assert.InRange(first.Diagnostics.AcceptedDwarfs, 1, 4);
        Assert.True(first.Diagnostics.AcceptedMoons > 0);
        Assert.Equal(3, CelestialWorldBootstrap.ExpandNewWorld(fixture.System, 384, null).Value.Manifest.Items.Count);

        static string FrozenPayload(CelestialSpawnItem item) => string.Join("|", item.Components.Select(comp => Convert.ToBase64String(comp.Properties.Serialize().Span)));
    }

    [Fact]
    public void ConstrainedSlotsReportExhaustedCandidatesAndKeepAuthoredMembers()
    {
        using var xml = new CelestialXmlTestScope();
        using var recipes = new ShippedLocalMapRecipeDefs();
        var fixture = new Fixture();
        var authored = CelestialSystemGenerator.Expand(fixture.System, 384).Value;
        var home = authored.Items.Single(item => item.Id == authored.RootSurfaceId.Value);
        var star = authored.Items.Single(item => item.Id == home.ParentId.Value);
        double stellarRadius = star.Components.Select(comp => comp.Properties).OfType<ICelestialGeometryProperties>().Single().RadiusMeters;
        double homeOrbitRadius = ExistingEphemerisParameters.Parse(Binding(home).Initialization).Value.OrbitRadius;

        // Keep every candidate periapsis within the stellar radius, even if the shipped orbital scale changes.
        double bandWidth = stellarRadius / homeOrbitRadius / (2 * fixture.Policy.distanceBands.Count);
        for (int i = 0; i < fixture.Policy.distanceBands.Count; i++)
        {
            var band = fixture.Policy.distanceBands[i];
            band.minimum = (i + 0.5) * bandWidth;
            band.maximum = (i + 1) * bandWidth;
        }
        foreach (var rule in fixture.Policy.bodyRules)
        {
            rule.minimumDistance = 0;
            rule.maximumDistance = 1;
        }
        var result = CelestialWorldBootstrap.ExpandNewWorld(fixture.System, 384, fixture.Policy).Value;
        Assert.Equal(3, result.Manifest.Items.Count);
        Assert.Equal(0, result.Diagnostics.AcceptedPlanets + result.Diagnostics.AcceptedDwarfs + result.Diagnostics.AcceptedMoons);
        Assert.Equal(32 * (result.Diagnostics.RequestedPlanets + result.Diagnostics.RequestedDwarfs), result.Diagnostics.Attempts);
        Assert.Equal(result.Diagnostics.Attempts, result.Diagnostics.Rejections["StellarClearance"]);
        Assert.Equal(result.Diagnostics.RequestedPlanets + result.Diagnostics.RequestedDwarfs, result.Diagnostics.SkippedSlots.Count);
        Assert.All(result.Diagnostics.SkippedSlots.Values, reason => Assert.Equal("StellarClearance", reason));
    }

    [Fact]
    public void GeneratedSiblingOrbitsRespectRocheHillSpacingAndSatelliteMassBudget()
    {
        using var xml = new CelestialXmlTestScope();
        using var recipes = new ShippedLocalMapRecipeDefs();
        var fixture = new Fixture();
        var result = CelestialWorldBootstrap.ExpandNewWorld(fixture.System, 384, fixture.Policy).Value;
        var bodies = result.Manifest.Items.ToDictionary(item => item.Id, item => new {
            Item = item,
            Radius = item.Components.Select(comp => comp.Properties).OfType<ICelestialGeometryProperties>().Single().RadiusMeters,
            Gm = Binding(item).GravitationalParameter.Value,
            Orbit = ExistingEphemerisParameters.Parse(Binding(item).Initialization).Value
        });
        var star = bodies[bodies[result.Manifest.RootSurfaceId.Value].Item.ParentId.Value];
        var satellites = bodies.Values.Where(body => body.Item.Template.Tags.Contains("ProceduralPopulation") && body.Item.Template.Tags.Contains("Moon"))
            .GroupBy(body => body.Item.ParentId.Value).ToArray();
        Assert.NotEmpty(satellites);
        Assert.Contains(satellites, system => system.Count() >= 2);
        foreach (var system in satellites)
        {
            var planet = bodies[system.Key];
            double hill = planet.Orbit.PeriapsisRadius * Math.Pow(planet.Gm / (3 * star.Gm), 1.0 / 3);
            Assert.True(system.Sum(moon => moon.Gm) < fixture.Policy.satelliteMassFraction * planet.Gm);
            var sorted = system.OrderBy(moon => moon.Orbit.OrbitRadius).ToArray();
            for (int i = 0; i < sorted.Length; i++)
            {
                var moon = sorted[i];
                double densityRatio = planet.Gm / moon.Gm * Math.Pow(moon.Radius / planet.Radius, 3);
                double roche = 2.44 * planet.Radius * Math.Pow(densityRatio, 1.0 / 3);
                Assert.True(moon.Orbit.PeriapsisRadius - moon.Radius > roche);
                Assert.True(moon.Orbit.ApoapsisRadius + moon.Radius < fixture.Policy.hillFraction * hill);
                if (i == 0) continue;
                var inner = sorted[i - 1];
                double mutualHill = (moon.Orbit.OrbitRadius + inner.Orbit.OrbitRadius) / 2 * Math.Pow((moon.Gm + inner.Gm) / (3 * planet.Gm), 1.0 / 3);
                Assert.True(moon.Orbit.PeriapsisRadius - moon.Radius - (inner.Orbit.ApoapsisRadius + inner.Radius) > 3.5 * mutualHill);
            }
        }

        var orbitingStar = bodies.Values.Where(body => body.Item.ParentId == star.Item.Id && body.Item.Template.Tags.Contains("ProceduralPopulation"))
            .OrderBy(body => body.Orbit.OrbitRadius).ToArray();
        for (int i = 1; i < orbitingStar.Length; i++)
        {
            var inner = orbitingStar[i - 1];
            var outer = orbitingStar[i];
            Assert.True(outer.Orbit.PeriapsisRadius - outer.Radius - (inner.Orbit.ApoapsisRadius + inner.Radius) >
                fixture.Policy.minimumOrbitSeparation * inner.Orbit.OrbitRadius);
        }
    }

    [Fact]
    public void GeneratedOrbitsLeaveTheAuthoredEarthMoonEnvelopeClearAndKeepSurfacesLazy()
    {
        using var xml = new CelestialXmlTestScope();
        using var recipes = new ShippedLocalMapRecipeDefs();
        var fixture = new Fixture();
        var result = CelestialWorldBootstrap.ExpandNewWorld(fixture.System, 384, fixture.Policy).Value;
        var items = result.Manifest.Items.ToDictionary(item => item.Id);
        var root = items[result.Manifest.RootSurfaceId.Value];
        var star = items[root.ParentId.Value];
        double moonReach = 80000000 + 1500000;
        var generated = result.Manifest.Items.Where(item => item.GenerationPath.Contains("$population", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(generated);
        foreach (var item in generated)
        {
            var binding = Binding(item);
            var orbit = ExistingEphemerisParameters.Parse(binding.Initialization).Value;
            double radius = item.Components.Select(c => c.Properties).OfType<ICelestialGeometryProperties>().Single().RadiusMeters;
            double gm = binding.GravitationalParameter.Value;
            Assert.True(gm > 0 && radius > 0 && orbit.Eccentricity > 0);
            Assert.Equal(star.Id, item.InitializationReferences["primaryLight"]);
            var parent = items[item.ParentId.Value];
            Assert.Equal(parent.Id == star.Id ? item.Id : parent.Id, item.InitializationReferences["frameOrigin"]);
            double expected = 2 * Math.PI * Math.Sqrt(Math.Pow(orbit.OrbitRadius, 3) / (gm + Binding(parent).GravitationalParameter.Value));
            Assert.Equal(expected, orbit.OrbitPeriod, 8);
            if (parent.Id == star.Id)
                Assert.True(orbit.ApoapsisRadius + radius < 120000000 - moonReach || orbit.PeriapsisRadius - radius > 120000000 + moonReach);
            foreach (var surface in item.Components.Select(c => c.Properties).OfType<SurfaceResolvedProperties>())
                Assert.Equal(CelestialSurfaceGenerationMode.OnDemand, surface.GenerationMode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V4CheckpointRestoresTheExactDirectoryAndFrozenComponents(bool generatePopulation)
    {
        using var native = new CelestialWorldBootstrapTests.Fixture();
        using var recipes = new ShippedLocalMapRecipeDefs();
        var fixture = new Fixture(native.Profile);
        CelestialSpawnManifest providerManifest = null;
        var started = CelestialWorldBootstrap.Begin(fixture.System, 384, request => {
            providerManifest = request.Manifest;
            return ExistingCelestialEphemerisBridge.Connect(request);
        }, populationPolicy: generatePopulation ? fixture.Policy : null);
        Assert.True(started.Success, string.Join(",", started.Errors));
        using var scope = started.Value;
        Assert.Same(providerManifest, scope.Manifest);
        Assert.Equal(generatePopulation, scope.Manifest.Items.Count > 3);
        native.Native.Grid.GenerateFreshWorld();
        WorldGenerator.GeneratePlanetLayer(native.Native.Grid.Surface, "population-v4", 1);
        var completed = scope.Complete();
        Assert.True(completed.Success, string.Join(",", completed.Errors));
        Assert.Equal(2, native.Native.Grid.PlanetLayers.Count);
        var captured = new CelestialSaveAdapter(scope.Runtime).CaptureCheckpoint();
        Assert.True(captured.Success, string.Join(",", captured.Errors));
        var encoded = CelestialSaveCodec.Encode(captured.Value);
        Assert.True(encoded.Success, string.Join(",", encoded.Errors));
        var saved = CelestialSaveCodec.Decode(encoded.Value).Value;

        fixture.Policy.bodyRules.Clear();
        var bridge = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var restore = new CelestialRestoreCoordinator(bridge.SessionId, bridge, CelestialBuiltinCompRegistry.Create(),
            _ => throw new InvalidOperationException("A v4 restore must use its frozen definitions."));
        Assert.True(restore.RestoreIdentities(saved).Success);
        Assert.True(restore.BindNativeObjects().Success);
        var result = restore.CompleteRestore();
        Assert.True(result.Success, string.Join(",", result.Errors));
        Assert.True(restore.CompleteRestore().Success);
        Assert.Equal(saved.CommittedBodies.OrderBy(id => id.Value, StringComparer.Ordinal),
            restore.Runtime.CaptureCatalog().Bodies.Select(body => body.Id).OrderBy(id => id.Value, StringComparer.Ordinal));
        Assert.Equal(2, native.Native.Grid.PlanetLayers.Count);
        var restored = new CelestialSaveAdapter(restore.Runtime).CaptureCheckpoint().Value;
        Assert.Equal(saved.Bodies.SelectMany(body => body.Components).Select(FrozenComponent),
            restored.Bodies.SelectMany(body => body.Components).Select(FrozenComponent));

        static string FrozenComponent(CelestialCompSaveRecord comp) => Convert.ToBase64String(comp.ConfigurationPayload.Span);
    }

    private static ICelestialPhysicsBindingProperties Binding(CelestialSpawnItem item)
        => item.Components.Select(component => component.Properties).OfType<ICelestialPhysicsBindingProperties>().Single();

    private sealed class Fixture
    {
        internal CelestialSystemTemplate System { get; }
        internal CelestialPopulationPolicyDef Policy { get; }
        internal Fixture(CelestialBiomeProfileDef nativeProfile = null)
        {
            using var content = new CelestialPlanetKindContentTests.Content();
            var native = new XmlDocument();
            native.Load(CelestialDefaultContentTests.DataPath("CelestialBodies/CelestialBodies_SolarSystem.xml"));
            var bodies = new Dictionary<string, CelestialBodyTemplate>();
            foreach (XmlNode node in native.DocumentElement.SelectNodes("PioneerFramework.CelestialBodyDef"))
            {
                var body = Verse.DirectXmlToObject.ObjectFromXml<CelestialBodyDef>(node, false);
                body.comps.RemoveAll(comp => comp is CelestialVisualCompProperties or CompProperties_MapGeneration or CompProperties_BiomeGeneration or CompProperties_ExistingEnvironment);
                if (nativeProfile != null && body.defName != "Sun")
                {
                    body.comps.OfType<CompProperties_Surface>().Single().subdivisions = new() { value = 0 };
                    body.comps.Add(new CompProperties_BiomeGeneration { key = "biomes", profile = nativeProfile });
                }
                bodies.Add(body.defName, body.ToTemplate().Value);
            }
            var count = IntParameterSpec.Fixed(1);
            System = new("PF_DefaultCelestialSystem", "root", new[] {
                new CelestialSystemEntry("primary", bodies["Sun"], count, initializationReferences: Frame("primary", CelestialSpawnReferenceScope.Self)),
                new CelestialSystemEntry("root", bodies["Earth"], count, "primary", Frame("root", CelestialSpawnReferenceScope.Self)),
                new CelestialSystemEntry("satellite", bodies["Moon"], count, "root", Frame("root", CelestialSpawnReferenceScope.Parent))
            });
            var xml = new XmlDocument();
            xml.Load(CelestialDefaultContentTests.DataPath("CelestialFramework/CelestialPopulation.xml"));
            var policy = xml.DocumentElement.FirstChild;
            Policy = Verse.DirectXmlToObject.ObjectFromXml<CelestialPopulationPolicyDef>(policy, false);
            var rules = policy.SelectNodes("bodyRules/li").Cast<XmlNode>().ToArray();
            for (int i = 0; i < rules.Length; i++) Policy.bodyRules[i].body = content.Bodies[rules[i]["body"].InnerText];
        }
        private static Dictionary<string, CelestialSpawnReference> Frame(string entry, CelestialSpawnReferenceScope scope)
            => new() { ["frameOrigin"] = new(entry, null, scope) };
    }
}
