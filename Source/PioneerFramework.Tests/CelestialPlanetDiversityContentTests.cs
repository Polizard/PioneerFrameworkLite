using System;
using System.Linq;
using System.Xml;
using HarmonyLib;
using PioneerFramework.Celestial;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialPlanetDiversityContentTests
{
    [Fact]
    public void SixNativeTemplatesOptInToTheirActualPhysicalEnvironmentalAndVisualConsumers()
    {
        using var content = new CelestialPlanetKindContentTests.Content();
        foreach (var body in content.Bodies.Values)
        {
            var resolved = body.ToTemplate().Value.Resolve(new(42, body.defName));
            Assert.True(resolved.Success, string.Join(",", resolved.Errors.Select(e => e.Code + ":" + e.Detail)));
            var values = resolved.Value.Select(c => c.Properties).ToArray();
            Assert.True(ExistingEphemerisParameters.Parse(Assert.Single(values.OfType<ICelestialPhysicsBindingProperties>()).Initialization).Value.UsesAuthoritativeRotation);
            if (body.defName is "PF_GasGiant" or "PF_IceGiant")
            {
                Assert.DoesNotContain(values, p => p is SurfaceResolvedProperties or MapGenerationResolvedProperties);
                Assert.Contains(values.OfType<VisualResolvedProperties>(), p => p.Profile.Appearance?.Style == CelestialSurfaceStyle.GasBands);
            }
            else
            {
                var environment = Assert.Single(values.OfType<ExistingEnvironmentResolvedProperties>()).Profile;
                Assert.NotNull(environment.Climate); Assert.NotNull(environment.Exposure);
                Assert.NotNull(Assert.Single(values.OfType<BiomeGenerationResolvedProperties>()).Profile.Terrain);
                Assert.NotNull(Assert.Single(values.OfType<MapGenerationResolvedProperties>()).Profile.Diversity);
            }
        }
    }
    [Fact]
    public void NativeReadableMotionConsumesRangesOverridesAndFreezesProviderPayload()
    {
        using var scope = new CelestialXmlTestScope();
        var xml = new XmlDocument();
        xml.LoadXml("<motion><orbitRadiusMeters>100000000</orbitRadiusMeters><orbitPeriodSeconds>60000</orbitPeriodSeconds><frameOriginReference>frameOrigin</frameOriginReference><rotation><periodSeconds>60000</periodSeconds></rotation><parameters><li><key>rotation.phaseRadians</key><value><min>0</min><max>2</max></value></li></parameters></motion>");
        var motion = DirectXmlToObject.ObjectFromXml<CelestialMotionXml>(xml.DocumentElement, false);
        Assert.Empty(motion.ConfigErrors());
        var method = typeof(CelestialMotionXml).GetMethod("Compile", new[] { typeof(CelestialGenerationContext) });
        Assert.NotNull(method);
        var context = new CelestialGenerationContext(42, "motion", "motion", new[] { new CelestialParameterOverride("motion", "motion.rotation.phaseRadians", .7) });
        var initialization = (PhysicsInitialization)method.Invoke(motion, new object[] { context });
        var parsed = ExistingEphemerisParameters.Parse(initialization);
        Assert.True(parsed.Success);
        Assert.Equal(.7, parsed.Value.RotationPhaseRadians);
        Assert.Equal(initialization.Payload.ToArray(), ((PhysicsInitialization)method.Invoke(motion, new object[] { context })).Payload.ToArray());
        var definition = new CelestialBodyDef { defName = "ReadableMotionProbe", comps = new() {
            new CompProperties_SphericalBody { key = "shape" }, new CompProperties_ExternalPhysicsBinding {
                key = "motion", providerId = ExistingCelestialEphemerisBridge.Id, gravitationalParameter = new() { value = 1e12 }, motion = motion } } };
        var template = definition.ToTemplate().Value;
        var frozen = Assert.Single(template.Resolve(new(42, "motion", parameterOverrides: new[] { new CelestialParameterOverride("motion", "motion.rotation.phaseRadians", .7) })).Value
            .Select(c => c.Properties).OfType<ICelestialPhysicsBindingProperties>());
        Assert.Equal(initialization.Payload.ToArray(), frozen.Initialization.Payload.ToArray());
        motion.rotation.phaseRadians = 2;
        Assert.Equal(.7, ExistingEphemerisParameters.Parse(frozen.Initialization).Value.RotationPhaseRadians);
    }

    [Theory]
    [InlineData("humidity", -1, .5)]
    [InlineData("windSpeed", 1, 1e100)]
    [InlineData("yearLengthTicks", 1e-300, 1)]
    public void WholeEffectiveClimateOverrideIntervalRejects(string key, double min, double max)
    {
        var climate = new CelestialClimateXml();
        Assert.Throws<ArgumentException>(() => climate.Resolve(new(42, "climate", "env", new[] { new CelestialParameterOverride("env", "climate." + key, Min: min, Max: max) })));
    }

    [Fact]
    public void ZeroResponseTinyFrequencyAndNativeWindStayFinite()
    {
        Assert.Equal(Math.Cos(.2), CelestialClimateModel.Harmonic(.2, double.PositiveInfinity, 0));
        Assert.True(double.IsFinite(CelestialClimateModel.Harmonic(.2, 1e200, 1e200)));
        Assert.Throws<ArgumentException>(() => new CelestialClimateXml { windSpeed = 1e100 }.Resolve());
    }

    [Fact]
    public void SealedPoweredFilterCanReachExactSafeAirAndLossOrBreachRestoresDanger()
    {
        var state = new CelestialRoomEnvironmentState("filter");
        var outside = new CelestialRoomAir(101325, 21200, 2, 0);
        var sealedRoom = new CelestialRoomTopology(new[] { 0 }, false, .5, true, true, true);
        state.Advance(0, new[] { sealedRoom }, outside);
        for (int tick = 60; tick <= 6000; tick += 60) state.Advance(tick, new[] { sealedRoom }, outside);
        Assert.Equal(0, Assert.Single(state.Rooms).Air.ToxicityPerDay);
        state.Advance(6060, new[] { sealedRoom with { ConditioningPerSecond = 0 } }, outside);
        Assert.True(Assert.Single(state.Rooms).Air.ToxicityPerDay > 0);
        state.Advance(6120, new[] { sealedRoom with { OpenToOutside = true } }, outside);
        Assert.True(Assert.Single(state.Rooms).Air.ToxicityPerDay > 0);
    }

    [Theory]
    [InlineData("PF_BarrenPlanet", "ThinAir")]
    [InlineData("PF_FrozenPlanet", "Atmospheric")]
    [InlineData("PF_TemperatePlanet", "Ocean")]
    [InlineData("PF_TemperatePlanet", "Arid")]
    [InlineData("PF_BarrenPlanet", "Synchronous")]
    public void DebugVariantsUseRealCompositionWithoutChangingDefinitionsAndRetainProviderRotation(string id, string variant)
    {
        using var harness = new PlanetKindDebugTests.Harness();
        using var native = new CelestialNativeWorldTestScope();
        native.AddDefs(new RimWorld.BiomeDef { defName = "Desert" }, new RimWorld.BiomeDef { defName = "IceSheet" });
        var definition = harness.Content.Bodies[id];
        var before = definition.ToTemplate().Value.Resolve(new(42, "unchanged")).Value.Select(c => c.Properties.Serialize().ToArray()).ToArray();
        var built = PlanetKindDebugScenario.BuildVariant(definition, variant, 42, "variant", PlanetKindDebugTests.Harness.Root,
            PlanetKindDebugTests.Harness.Root, 2e14, 5000000);
        Assert.True(built.Success, string.Join(",", built.Errors.Select(e => e.Code + ":" + e.Detail)));
        var item = Assert.Single(built.Value.Items);
        var properties = item.Components.Select(c => c.Properties).ToArray();
        var physics = Assert.Single(properties.OfType<ICelestialPhysicsBindingProperties>());
        Assert.True(ExistingEphemerisParameters.Parse(physics.Initialization).Value.UsesAuthoritativeRotation);
        if (variant is "ThinAir" or "Atmospheric")
        {
            Assert.Equal(2, Assert.Single(properties.OfType<AtmosphereResolvedProperties>()).Version);
            Assert.Empty(Assert.Single(properties.OfType<ExistingEnvironmentResolvedProperties>()).Profile.VacuumBiomeIds);
        }
        if (variant is "Ocean" or "Arid") Assert.Equal(variant == "Ocean" ? .8 : .02, Assert.Single(properties.OfType<BiomeGenerationResolvedProperties>()).Profile.Terrain.OceanCoverage);
        var operation = harness.Runtime.CreateBatch(new(new("variants", harness.Runtime.WorldSessionId, harness.Runtime.CaptureCatalog().Revision), built.Value));
        Assert.Equal(CelestialOperationStatus.Pending, operation.Status); harness.Advance();
        if (variant == "Synchronous")
        {
            Assert.Equal(PlanetKindDebugTests.Harness.Root, item.InitializationReferences["synchronousTarget"]);
            var bridge = (ExistingCelestialEphemerisBridge)harness.Runtime.Physics;
            var stamp = bridge.CommittedStamp;
            using var snapshot = bridge.Sample(new(bridge.SessionId, stamp.TopologyRevision, new[] { item.Id, PlanetKindDebugTests.Harness.Root })).Snapshot;
            Assert.True(snapshot.TryGetBodyState(item.Id, out var body)); Assert.True(snapshot.TryGetBodyState(PlanetKindDebugTests.Harness.Root, out var parent));
            Assert.True((DVector3.Rotate(new(0, 0, -1), body.Orientation) - (parent.Position - body.Position).normalized).magnitude < 1e-12);
        }
        var after = definition.ToTemplate().Value.Resolve(new(42, "unchanged")).Value.Select(c => c.Properties.Serialize().ToArray()).ToArray();
        Assert.Equal(before.Length, after.Length); for (int i = 0; i < before.Length; i++) Assert.Equal(before[i], after[i]);
    }

    [Fact]
    public void EffectiveDensityAndPressureIntervalsRejectMixedInvalidEndpointsForEverySeed()
    {
        using var content = new CelestialPlanetKindContentTests.Content();
        var body = content.Bodies["PF_TemperatePlanet"];
        var binding = body.comps.OfType<CompProperties_ExternalPhysicsBinding>().Single();
        binding.gravitationalParameter = null; binding.meanDensityKilogramsPerCubicMeter = new() { value = 5000 };
        var template = body.ToTemplate().Value;
        foreach (ulong seed in new[] { 1UL, 2UL, 42UL })
        {
            Assert.False(template.Resolve(new(seed, "range", parameterOverrides: new[] { new CelestialParameterOverride("motion", "meanDensityKilogramsPerCubicMeter", Min: -1, Max: 5000) })).Success);
            Assert.False(template.Resolve(new(seed, "range", parameterOverrides: new[] { new CelestialParameterOverride("air", "temperatureKelvin", Min: -1, Max: 300) })).Success);
            Assert.False(template.Resolve(new(seed, "range", parameterOverrides: new[] { new CelestialParameterOverride("air", "pressurePascals", Min: -1, Max: 101325) })).Success);
            Assert.False(template.Resolve(new(seed, "range", parameterOverrides: new[] { new CelestialParameterOverride("motion", "meanDensityKilogramsPerCubicMeter", Min: 1, Max: double.MaxValue) })).Success);
        }
    }

    [Fact]
    public void MotionRejectsWholeZeroAxisOrbitAndInactivePeriodIntervals()
    {
        var motion = new CelestialMotionXml { orbitRadiusMeters = 1, orbitPeriodSeconds = 1, rotation = new() { periodSeconds = 100 } };
        motion.parameters.Add(new() { key = "rotation.axisY", value = new() { min = -1, max = 1 } });
        Assert.NotEmpty(motion.ConfigErrors()); motion.parameters.Clear();
        motion.parameters.Add(new() { key = "orbitPeriodSeconds", value = new() { min = 0, max = 100 } });
        Assert.NotEmpty(motion.ConfigErrors()); motion.parameters.Clear();
        motion.rotation.periodSeconds = null; motion.rotation.synchronousTargetReference = "target";
        motion.parameters.Add(new() { key = "rotation.periodSeconds", value = new() { min = 1, max = 100 } });
        Assert.NotEmpty(motion.ConfigErrors());
    }

    [Theory]
    [InlineData(double.NaN, 0, 0, 0)]
    [InlineData(0, double.PositiveInfinity, 0, 0)]
    [InlineData(0, 0, double.NaN, 0)]
    [InlineData(0, 0, 0, double.PositiveInfinity)]
    public void ExposureOnlyQueriesRejectEveryNonFiniteCoordinate(double ticks, double lon, double lat, double altitude)
    {
        using var fixture = new CelestialPlanetNativeEnvironmentTests.Fixture(null, false, new CelestialExposureXml());
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Capability.SampleSurface(new(ticks, lon, lat, altitude)));
    }

    [Fact]
    public void DebugOrbitAdaptationPreservesV2AxisPhaseRetrogradeAndActualOrientation()
    {
        using var harness = new PlanetKindDebugTests.Harness();
        var definition = harness.Content.Bodies["PF_BarrenPlanet"];
        var motion = definition.comps.OfType<CompProperties_ExternalPhysicsBinding>().Single().motion;
        motion.rotation.axisX = .3; motion.rotation.axisY = 1; motion.rotation.phaseRadians = .4; motion.rotation.retrograde = true;
        var original = ExistingEphemerisParameters.Parse(motion.Compile()).Value;
        var built = PlanetKindDebugScenario.Build(new[] { definition }, 42, "axis-retro", PlanetKindDebugTests.Harness.Root,
            PlanetKindDebugTests.Harness.Root, 2e14, 5000000);
        Assert.True(built.Success); var item = Assert.Single(built.Value.Items);
        var physics = Assert.Single(item.Components.Select(c => c.Properties).OfType<ICelestialPhysicsBindingProperties>());
        var parsed = ExistingEphemerisParameters.Parse(physics.Initialization).Value;
        Assert.Equal(original.RotationPhaseRadians, parsed.RotationPhaseRadians); Assert.True(parsed.Retrograde);
        Assert.True((original.RotationAxis - parsed.RotationAxis).magnitude < 1e-15);
        harness.Runtime.CreateBatch(new(new("axis-create", harness.Runtime.WorldSessionId, harness.Runtime.CaptureCatalog().Revision), built.Value)); harness.Advance();
        var bridge = (ExistingCelestialEphemerisBridge)harness.Runtime.Physics;
        using var snapshot = bridge.Sample(new(bridge.SessionId, bridge.CommittedStamp.TopologyRevision, new[] { item.Id })).Snapshot;
        Assert.True(snapshot.TryGetBodyState(item.Id, out var state));
        Assert.True((DVector3.Rotate(DVector3.up, state.Orientation) - original.RotationAxis).magnitude < 1e-12);
        Assert.True(DVector3.Dot(state.AngularVelocity, original.RotationAxis) > 0);
    }

    [Fact]
    public void ExistingInspectorShowsFrozenInstanceAndLabelsCurrentDefinitionReferenceWithoutGenerating()
    {
        var translation = new Harmony("PFL.Tests.HeadlessInspectorTranslation");
        translation.Patch(AccessTools.Method(typeof(Translator), nameof(Translator.TryTranslate), new[] { typeof(string), typeof(TaggedString).MakeByRefType() }),
            prefix: new HarmonyMethod(typeof(CelestialPlanetDiversityContentTests), nameof(TranslateForHeadless)));
        try
        {
        using var harness = new PlanetKindDebugTests.Harness();
        using var native = new CelestialNativeWorldTestScope(); native.Game.tickManager = new TickManager();
        AccessTools.Field(typeof(TickManager), "gameStartAbsTick").SetValue(native.Game.tickManager, 60000);
        var definition = harness.Content.Bodies["PF_BarrenPlanet"];
        native.AddDefs(definition);
        var operation = PlanetKindDebugSession.For(harness.Runtime).Create(new[] { definition }); harness.Advance();
        var body = harness.Runtime.Catalog.CaptureInstances().Single(b => PlanetKindDebugScenario.IsTestBody(b.Description));
        var beforeCount = harness.Runtime.CaptureCatalog().Bodies.Count;
        var text = CelestialDebugInspection.Describe(harness.Runtime, body);
        Assert.Contains("density=", text); Assert.Contains("escape=", text); Assert.True(text.Contains("query: lon=0 lat=0 altitude=0m", StringComparison.Ordinal), text);
        definition.comps.OfType<CompProperties_SphericalBody>().Single().radiusMeters = new() { min = 1000, max = 2000 };
        var changed = CelestialDebugInspection.Describe(harness.Runtime, body);
        Assert.Equal(text.Split('\n')[0], changed.Split('\n')[0]);
        Assert.Equal(beforeCount, harness.Runtime.CaptureCatalog().Bodies.Count);
        Assert.Null(harness.Runtime.Surfaces);
        }
        finally { translation.UnpatchAll(translation.Id); }
    }
    private static bool TranslateForHeadless(string __0, ref TaggedString __1, ref bool __result)
    { __1 = __0; __result = true; return false; }
}
