using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// Moon surface content and map-generation contracts. The legacy PF_MoonTiles world-gen step was
    /// retired (its latitude bands are now expressed by the Parameterized PF_MoonBiomes profile), so
    /// this class only pins the content/defs and the retained native-map seam.
    /// </summary>
    public class MoonSurfaceContentTests
    {
        private const BindingFlags StaticFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static string DataPath(string relative) =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relative);

        private static string GetConstructedTileTypeName(string worldGenStepTypeName)
        {
            var type = typeof(WorldGenStep).Assembly.GetType(worldGenStepTypeName, true);
            var method = type.GetMethod(
                "GenerateTileFor",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.NotNull(method);
            byte[] il = method.GetMethodBody().GetILAsByteArray();
            byte newobj = (byte)System.Reflection.Emit.OpCodes.Newobj.Value;

            for (int i = 0; i <= il.Length - 5; i++)
            {
                if (il[i] != newobj)
                {
                    continue;
                }

                int token = BitConverter.ToInt32(il, i + 1);
                ConstructorInfo ctor = method.Module.ResolveMethod(token) as ConstructorInfo;
                return ctor.DeclaringType.FullName;
            }

            throw new Xunit.Sdk.XunitException(
                $"{worldGenStepTypeName}.GenerateTileFor does not construct a tile.");
        }

        [Fact]
        public void MoonPlanetLayerDef_UsesCelestialSurfaceLayerAndTiles()
        {
            var layerDefs = new System.Xml.XmlDocument();
            layerDefs.Load(DataPath("Defs/PlanetLayerDefs/PlanetLayers_Celestial.xml"));

            var moon = layerDefs.SelectSingleNode(
                "/Defs/PlanetLayerDef[defName='Moon']");
            Assert.NotNull(moon);
            var layerType = typeof(PF_CelestialSurfaceLayer).Assembly.GetType(moon.SelectSingleNode("layerType")?.InnerText, true);
            Assert.Equal(typeof(Celestial.CelestialSurfaceLayer), layerType);
            Assert.True(typeof(SurfaceLayer).IsAssignableFrom(layerType));
            Assert.Equal("SurfaceTile", moon.SelectSingleNode("tileType")?.InnerText);
            var steps = moon.SelectNodes("worldGenSteps/li");
            var stepNames = steps.Cast<System.Xml.XmlNode>().Select(n => n.InnerText).ToList();
            Assert.Equal(new[] { "PF_CelestialTiles" }, stepNames);
        }

        [Fact]
        public void MoonBiomes_UseExpectedPlaceholderTexturesAndDirt()
        {
            var doc = new System.Xml.XmlDocument();
            doc.Load(DataPath("Defs/BiomesDefs/Biome_Planets.xml"));

            var expected = new[]
            {
                new { Def = "PF_MoonBiomeWater", Texture = "World/Biomes/IceSheetOcean" },
                // 2026-11-09：玄武岩改用深灰贴图（与荒芜平原区分，荒芜行星高地可见）。
                new { Def = "PF_MoonBiomeBasalt", Texture = "World/Biomes/PF_BasaltDark" },
                new { Def = "PF_MoonBiomeRegolith", Texture = "World/Biomes/Desert" }
            };

            foreach (var entry in expected)
            {
                var biome = doc.SelectSingleNode($"/Defs/BiomeDef[defName='{entry.Def}']");
                Assert.NotNull(biome);
                Assert.Equal(entry.Texture, biome.SelectSingleNode("texture")?.InnerText);
            }

            foreach (var defName in new[] { "PF_MoonBiomeWater", "PF_MoonBiomeBasalt", "PF_MoonBiomeRegolith" })
            {
                var biome = doc.SelectSingleNode($"/Defs/BiomeDef[defName='{defName}']");
                var terrains = biome.SelectNodes("terrainsByFertility/li/terrain")
                    .Cast<System.Xml.XmlNode>().Select(n => n.InnerText).ToList();
                Assert.Equal(new[] { "PF_MoonDirt" }, terrains);
            }

            var waterIce = doc.SelectSingleNode("/Defs/BiomeDef[defName='PF_MoonBiomeWater']");
            var waterIcePatchTerrains = waterIce.SelectNodes("terrainPatchMakers/li/thresholds/li/terrain")
                .Cast<System.Xml.XmlNode>().Select(n => n.InnerText).ToList();
            Assert.Contains("Ice", waterIcePatchTerrains);

            var basalt = doc.SelectSingleNode("/Defs/BiomeDef[defName='PF_MoonBiomeBasalt']");
            var basaltRockTypes = basalt.SelectNodes("forceRockTypes/li")
                .Cast<System.Xml.XmlNode>().Select(n => n.InnerText).ToList();
            Assert.Contains("Vacstone", basaltRockTypes);

            var moonLayer = new System.Xml.XmlDocument();
            moonLayer.Load(DataPath("Defs/PlanetLayerDefs/PlanetLayers_Celestial.xml"));
            var moon = moonLayer.SelectSingleNode("/Defs/PlanetLayerDef[defName='Moon']");
            Assert.Equal("PF_MoonBiomeWater", moon.SelectSingleNode("backgroundBiome")?.InnerText);
        }

        [Fact]
        public void MoonDirtTerrainDef_Exists()
        {
            var doc = new System.Xml.XmlDocument();
            doc.Load(DataPath("Defs/TerrainDefs/Terrain_Planets.xml"));
            Assert.NotNull(doc.SelectSingleNode("/Defs/TerrainDef[defName='PF_MoonDirt']"));
        }

        [Fact]
        public void VanillaWorldGenStep_AssumptionsRemainValid()
        {
            Assert.Equal("RimWorld.Planet.Tile",
                GetConstructedTileTypeName("RimWorld.Planet.WorldGenStep_Tiles"));
            Assert.Equal("RimWorld.Planet.SurfaceTile",
                GetConstructedTileTypeName("RimWorld.Planet.WorldGenStep_Terrain"));
        }

        [Fact]
        public void NativeLocalMapGeneration_HarmonyPrefixIsRegistered()
        {
            // The native local-map host installs the scoped Harmony boundary.
            MapGeneration.NativeLocalMapHooks.Install();

            var generateContentsIntoMap = typeof(MapGenerator).GetMethods(StaticFlags)
                .Where(m => m.Name == "GenerateContentsIntoMap" && m.GetParameters().Length == 4)
                .FirstOrDefault();
            Assert.NotNull(generateContentsIntoMap);

            var info = Harmony.GetPatchInfo(generateContentsIntoMap);
            Assert.NotNull(info);
            Assert.Contains(
                info.Prefixes.Select(p => p.owner),
                owner => owner == "PioneerFramework.LocalMapGeneration");
        }

    }
}
