using System;
using System.Linq;
using HarmonyLib;
using PioneerFramework.Celestial;
using RimWorld.Planet;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialProductionBootstrapTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeEntryHooksCreateDefaultSystemWithProductionProviderBeforeRootConstruction(bool createSurfaces)
    {
        using var fixture = new CelestialWorldBootstrapTests.Fixture();
        var body = new CelestialBodyDef { defName = "PF_ProductionProbe", label = "production", category = "Moon", comps = new() {
            new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { value = 1500000 } },
            new CompProperties_Surface { key = "surface", subdivisions = new() { value = 1 },
                generationMode = createSurfaces ? CelestialSurfaceGenerationMode.Creation : CelestialSurfaceGenerationMode.OnDemand },
            new CompProperties_BiomeGeneration { key = "biomes", profile = fixture.Profile },
            new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = ExistingCelestialEphemerisBridge.Id, modeId = "existing.circular.v1",
                initializationPayload = Convert.ToBase64String(CelestialExistingEphemerisTests.Registration(CelestialInstanceId.Parse("unused"), 0).Initialization.Payload.Span) } } };
        fixture.Native.AddDefs(body);
        fixture.Native.AddDefs(new CelestialSystemDef { defName = CelestialWorldBootstrap.DefaultSystem, rootSurfaceEntry = "root", entries = new() {
            new() { key = "root", bodyDef = body }, new() { key = "optional", bodyDef = body, count = new() { value = 2 } } } });
        CelestialFrameworkStartup.InitializeProviders();
        var harmony = new Harmony("PFL.Tests.ProductionCelestialBootstrap");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(WorldGrid), nameof(WorldGrid.GenerateFreshWorld)),
                prefix: new HarmonyMethod(typeof(CelestialWorldBootstrapHooks), nameof(CelestialWorldBootstrapHooks.BeginFreshWorld)));
            harmony.Patch(AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GeneratePlanetLayer)),
                postfix: new HarmonyMethod(typeof(CelestialWorldBootstrapHooks), nameof(CelestialWorldBootstrapHooks.PlanetLayerGenerated)));
            fixture.Native.Grid.GenerateFreshWorld();
            Assert.NotNull(CelestialWorldBootstrap.Current);
            var root = Assert.IsType<CelestialSurfaceLayer>(fixture.Native.Grid.Surface);
            Assert.Equal("PF_DefaultCelestialSystem:root[0]", root.Header.OwnerId.Value);
            Assert.Equal(30, root.Radius);
            WorldGenerator.GeneratePlanetLayer(root, fixture.Native.World.info.seedString, 1);
            Assert.Null(CelestialWorldBootstrap.Current);
            var manager = fixture.Native.Game.GetComponent<CelestialBodyManager>();
            Assert.True(manager.TryGetCelestialRuntime(out var runtime));
            Assert.IsType<ExistingCelestialEphemerisBridge>(runtime.Physics);
            Assert.Equal(3, runtime.CaptureCatalog().Bodies.Count);
            Assert.All(runtime.CaptureCatalog().Bodies, description => Assert.Equal(CelestialLifecycle.Ready, runtime.Query(description.Id).Value.Lifecycle));
            Assert.Equal(createSurfaces ? 3 : 1, fixture.Native.Grid.PlanetLayers.Count);
            Assert.All(fixture.Native.Grid.PlanetLayers.Values, layer => {
                var surface = Assert.IsType<CelestialSurfaceLayer>(layer);
                Assert.Equal(CelestialSurfaceState.Ready, surface.Header.State);
                Assert.Equal(surface.TilesCount, surface.Tiles.Count);
            });
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            CelestialWorldBootstrapHooks.FinalizeWorld(new InvalidOperationException("test cleanup"));
        }
    }
}
