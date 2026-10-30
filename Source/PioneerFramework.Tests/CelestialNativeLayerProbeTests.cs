using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    public class CelestialNativeLayerProbeTests
    {
        [Fact]
        public void SharedDefinitionCreatesDistinctLayers()
        {
            var definition = new PlanetLayerDef
            {
                defName = "PF_CelestialNativeProbe",
                layerType = typeof(SurfaceLayer),
                tileType = typeof(SurfaceTile),
                worldDrawLayers = new List<System.Type>()
            };
            using var grid = new WorldGrid();

            PlanetLayer first = grid.RegisterPlanetLayer(definition, radius: 30f, subdivisions: 0);
            PlanetLayer second = grid.RegisterPlanetLayer(definition, radius: 42f, subdivisions: 0);

            Assert.NotEqual(first.LayerID, second.LayerID);
            Assert.Same(definition, first.Def);
            Assert.Same(first.Def, second.Def);
            Assert.Equal(2, grid.PlanetLayers.Count);
            Assert.True(first.TilesCount > 0);
            Assert.Equal(first.TilesCount, second.TilesCount);
            Assert.Equal(30f, first.Radius);
            Assert.Equal(42f, second.Radius);
        }

        [Fact]
        public void HeaderRestoredBeforeInitialize()
        {
            using var prefs = new ProbePreferences();
            var biome = new BiomeDef { defName = "PF_CelestialProbeBiome", shortHash = 64001 };
            using var biomeDefs = new ProbeDefinitions<BiomeDef>(biome);
            var definition = new PlanetLayerDef
            {
                defName = "PF_CelestialHeaderProbe",
                layerType = typeof(HeaderProbeLayer),
                tileType = typeof(SurfaceTile),
                worldDrawLayers = new List<Type> { typeof(HeaderObserverLayer) }
            };
            using var layerDefs = new ProbeDefinitions<PlanetLayerDef>(definition);
            using var grid = new WorldGrid();
            var original = (HeaderProbeLayer)grid.RegisterPlanetLayer(definition, radius: 30f, subdivisions: 0);
            original.OwnerId = "system/moon[1]";
            for (int i = 0; i < original.TilesCount; i++)
                original.Tiles.Add(new SurfaceTile(new PlanetTile(i, original)) { PrimaryBiome = biome });
            HeaderProbeLayer restored = null;
            string path = Path.Combine(Path.GetTempPath(), "PFL-celestial-header-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                Scribe.saver.InitSaving(path, "celestial-probe");
                Scribe_Deep.Look(ref original, "layer");
                Scribe.saver.FinalizeSaving();
                HeaderObserverLayer.ObservedOwner = null;
                Scribe.loader.InitLoading(path);
                Scribe_Deep.Look(ref restored, "layer");
                Scribe.loader.FinalizeLoading();

                Assert.Equal("system/moon[1]", HeaderObserverLayer.ObservedOwner);
                Assert.Equal("system/moon[1]", restored.OwnerId);
                Assert.Equal(original.LayerID, restored.LayerID);
                Assert.Equal(original.TilesCount, restored.TilesCount);
                Assert.Equal(original.TilesCount, restored.Tiles.Count);
            }
            finally
            {
                Scribe.ForceStop();
                restored?.Dispose();
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void ManifestContextCanBeInstalledBeforeRootAndTileGeneration()
        {
            using var prefs = new ProbePreferences();
            using var savePath = new ProbeSavePath();
            Game previousGame = Current.Game;
            World previousWorld = Current.CreatingWorld;
            using var grid = new WorldGrid();
            var rootDefinition = new PlanetLayerDef
            {
                defName = "PF_CelestialRootProbe",
                layerType = typeof(RootProbeLayer),
                tileType = typeof(SurfaceTile),
                worldDrawLayers = new List<Type>()
            };
            var stepDefinition = new WorldGenStepDef
            {
                defName = "PF_CelestialRootProbeTiles",
                worldGenStep = new RootProbeStep()
            };
            AccessTools.Field(typeof(PlanetLayerDef), "worldGenSteps").SetValue(rootDefinition,
                new List<WorldGenStepDef> { stepDefinition });
            var rootPart = new ScenPart_PlanetLayer
            {
                layer = rootDefinition,
                tag = "Surface",
                settingsDef = new PlanetLayerSettingsDef
                {
                    settings = new PlanetLayerSettings { radius = 30f, subdivisions = 0, useSurfaceViewAngle = true }
                }
            };
            var scenario = new Scenario();
            AccessTools.Field(typeof(Scenario), "surfaceLayer").SetValue(scenario, rootPart);
            var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
            game.components = new List<GameComponent>();
            game.Scenario = scenario;
            var world = new World { grid = grid };
            world.info.planetCoverage = 1f;
            var patch = new Harmony("PFL.Tests.CelestialBootstrapProbe");
            BootstrapContext = null;
            RootProbeLayer.ContextAtConstruction = null;
            RootProbeStep.ContextAtGeneration = null;
            Rand.PushState();
            try
            {
                Current.Game = game;
                Current.CreatingWorld = world;
                patch.Patch(AccessTools.Method(typeof(WorldGrid), nameof(WorldGrid.GenerateFreshWorld)),
                    prefix: new HarmonyMethod(typeof(CelestialNativeLayerProbeTests), nameof(InstallBootstrapContext)));

                grid.GenerateFreshWorld();
                WorldGenerator.GeneratePlanetLayer(grid.Surface, "celestial-probe", 31);

                Assert.True(grid.Surface.IsRootSurface);
                Assert.Equal("manifest-before-root", RootProbeLayer.ContextAtConstruction);
                Assert.Equal("manifest-before-root", RootProbeStep.ContextAtGeneration);
                Assert.Single(grid.PlanetLayers);
            }
            finally
            {
                patch.UnpatchAll(patch.Id);
                BootstrapContext = null;
                Current.CreatingWorld = previousWorld;
                Current.Game = previousGame;
                Rand.PopState();
            }
        }

        private static string BootstrapContext;

        private static void InstallBootstrapContext() => BootstrapContext = "manifest-before-root";

        public sealed class RootProbeStep : WorldGenStep
        {
            public static string ContextAtGeneration;
            public override int SeedPart => 31;
            public override void GenerateFresh(string seed, PlanetLayer layer) => ContextAtGeneration = BootstrapContext;
        }

        public sealed class RootProbeLayer : SurfaceLayer
        {
            public static string ContextAtConstruction;
            public RootProbeLayer(int layerId, PlanetLayerDef def, float radius, Vector3 origin,
                Vector3 viewCenter, float viewAngle, int subdivisions, float extraCameraAltitude,
                float backgroundWorldCameraOffset, float backgroundWorldCameraParallaxDistance)
                : base(layerId, def, radius, origin, viewCenter, viewAngle, subdivisions, extraCameraAltitude,
                    backgroundWorldCameraOffset, backgroundWorldCameraParallaxDistance)
            {
                ContextAtConstruction = BootstrapContext;
            }
        }

        public sealed class HeaderProbeLayer : SurfaceLayer, IExposable
        {
            public string OwnerId;
            public static HeaderProbeLayer LoadingLayer;
            public HeaderProbeLayer() { }
            public HeaderProbeLayer(int layerId, PlanetLayerDef def, float radius, Vector3 origin,
                Vector3 viewCenter, float viewAngle, int subdivisions, float extraCameraAltitude,
                float backgroundWorldCameraOffset, float backgroundWorldCameraParallaxDistance)
                : base(layerId, def, radius, origin, viewCenter, viewAngle, subdivisions, extraCameraAltitude,
                    backgroundWorldCameraOffset, backgroundWorldCameraParallaxDistance) { }

            void IExposable.ExposeData()
            {
                Scribe_Values.Look(ref OwnerId, "celestialOwner");
                HeaderProbeLayer previous = LoadingLayer;
                LoadingLayer = this;
                try { base.ExposeData(); }
                finally { LoadingLayer = previous; }
            }
        }

        public sealed class HeaderObserverLayer : WorldDrawLayer
        {
            public static string ObservedOwner;
            public HeaderObserverLayer()
            {
                if (Scribe.mode == LoadSaveMode.LoadingVars)
                    ObservedOwner = HeaderProbeLayer.LoadingLayer?.OwnerId;
            }
        }

        private sealed class ProbePreferences : IDisposable
        {
            private readonly System.Reflection.FieldInfo field = AccessTools.Field(typeof(Prefs), "data");
            private readonly object previous;
            public ProbePreferences()
            {
                previous = field.GetValue(null);
                if (previous == null) field.SetValue(null, RuntimeHelpers.GetUninitializedObject(field.FieldType));
            }
            public void Dispose() => field.SetValue(null, previous);
        }

        internal sealed class ProbeSavePath : IDisposable
        {
            private readonly List<(System.Reflection.FieldInfo Field, object Previous)> previous = new();
            public ProbeSavePath()
            {
                string path = Path.Combine(Path.GetTempPath(), "PFL-celestial-native-probe-config");
                foreach (var setting in new[]
                {
                    ("saveDataPath", path),
                    ("officialModsFolderPath", Path.Combine(path, "Data")),
                    ("modsFolderPath", Path.Combine(path, "Mods"))
                })
                {
                    var field = AccessTools.Field(typeof(GenFilePaths), setting.Item1);
                    previous.Add((field, field.GetValue(null)));
                    Directory.CreateDirectory(setting.Item2);
                    field.SetValue(null, setting.Item2);
                }
                string coreAbout = Path.Combine(path, "Data", "Core", "About");
                Directory.CreateDirectory(coreAbout);
                File.WriteAllText(Path.Combine(coreAbout, "About.xml"),
                    "<ModMetaData><packageId>Ludeon.RimWorld</packageId><author>Ludeon Studios</author></ModMetaData>");
            }
            public void Dispose()
            {
                foreach (var entry in previous) entry.Field.SetValue(null, entry.Previous);
            }
        }

        private sealed class ProbeDefinitions<T> : IDisposable where T : Def, new()
        {
            private readonly T[] previous = DefDatabase<T>.AllDefsListForReading.ToArray();
            public ProbeDefinitions(T definition) => DefDatabase<T>.Add(definition);
            public void Dispose()
            {
                DefDatabase<T>.Clear();
                foreach (T definition in previous) DefDatabase<T>.Add(definition);
            }
        }
    }
}
