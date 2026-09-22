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
    public class WorldGenStepMoonTilesTests
    {
        private const BindingFlags StaticFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static string DataPath(string relative) =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relative);

        private static string SourcePath(string relative) =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Source", "PioneerFramework", relative);

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
        public void MoonPlanetLayerDef_UsesSurfaceLayerAndTiles()
        {
            var layerDefs = new System.Xml.XmlDocument();
            layerDefs.Load(DataPath("Defs/PlanetLayerDefs/PlanetLayers_Celestial.xml"));

            var moon = layerDefs.SelectSingleNode(
                "/Defs/PlanetLayerDef[defName='Moon']");
            Assert.NotNull(moon);
            Assert.Equal("SurfaceLayer", moon.SelectSingleNode("layerType")?.InnerText);
            Assert.Equal("SurfaceTile", moon.SelectSingleNode("tileType")?.InnerText);
            var steps = moon.SelectNodes("worldGenSteps/li");
            var stepNames = steps.Cast<System.Xml.XmlNode>().Select(n => n.InnerText).ToList();
            Assert.Contains("PF_MoonTiles", stepNames);
        }

        [Fact]
        public void MoonBiomes_UseExpectedPlaceholderTexturesAndDirt()
        {
            var doc = new System.Xml.XmlDocument();
            doc.Load(DataPath("Defs/BiomesDefs/Biome_Planets.xml"));

            var expected = new[]
            {
                new { Def = "PF_MoonBiomeWater", Texture = "World/Biomes/IceSheetOcean" },
                new { Def = "PF_MoonBiomeBasalt", Texture = "World/Biomes/ExtremeDesert" },
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
        public void MoonTiles_StaticClassificationRules()
        {
            Assert.True(WorldGenStep_MoonTiles.IsWaterIceLatitude(75f));
            Assert.True(WorldGenStep_MoonTiles.IsWaterIceLatitude(-75f));
            Assert.False(WorldGenStep_MoonTiles.IsWaterIceLatitude(74.99f));
            Assert.False(WorldGenStep_MoonTiles.IsWaterIceLatitude(-74.99f));

            Assert.Equal(
                WorldGenStep_MoonTiles.MoonBiomeCategory.WaterIce,
                WorldGenStep_MoonTiles.SelectMoonBiomeCategory(75f, false));
            Assert.Equal(
                WorldGenStep_MoonTiles.MoonBiomeCategory.WaterIce,
                WorldGenStep_MoonTiles.SelectMoonBiomeCategory(-75f, true));
            Assert.Equal(
                WorldGenStep_MoonTiles.MoonBiomeCategory.Basalt,
                WorldGenStep_MoonTiles.SelectMoonBiomeCategory(0f, true));
            Assert.Equal(
                WorldGenStep_MoonTiles.MoonBiomeCategory.Regolith,
                WorldGenStep_MoonTiles.SelectMoonBiomeCategory(0f, false));

            Assert.Equal(Hilliness.SmallHills,
                WorldGenStep_MoonTiles.SelectHillinessForBiomeDefName("PF_MoonBiomeBasalt"));
            Assert.Equal(Hilliness.Flat,
                WorldGenStep_MoonTiles.SelectHillinessForBiomeDefName("PF_MoonBiomeRegolith"));
            Assert.Equal(Hilliness.Flat,
                WorldGenStep_MoonTiles.SelectHillinessForBiomeDefName("PF_MoonBiomeWater"));
            Assert.Equal(Hilliness.Flat,
                WorldGenStep_MoonTiles.SelectHillinessForBiomeDefName("Desert"));
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
        public void MoonSurfaceMapGenerationDefs_ArePresent()
        {
            var doc = new System.Xml.XmlDocument();
            doc.Load(DataPath("Defs/MapGeneration/MoonSurfaceMapGeneration.xml"));
            var step = doc.SelectSingleNode("/Defs/GenStepDef[defName='PF_MoonSurfaceTerrain']");
            Assert.NotNull(step);
            Assert.Equal("230", step.SelectSingleNode("order")?.InnerText);
            Assert.Equal("PioneerFramework.GenStep_MoonSurfaceTerrain",
                step.SelectSingleNode("genStep/@Class")?.InnerText);

            var patch = new System.Xml.XmlDocument();
            patch.Load(DataPath("Patches/MoonSurfaceMapGeneration.xml"));
            var add = patch.SelectSingleNode(
                "/Patch/Operation[@Class='PatchOperationAdd']");
            Assert.NotNull(add);
            Assert.Equal("/Defs/MapGeneratorDef[defName=\"Base_Player\"]/genSteps",
                add.SelectSingleNode("xpath")?.InnerText);
            var values = add.SelectNodes("value/li")
                .Cast<System.Xml.XmlNode>().Select(n => n.InnerText).ToList();
            Assert.Contains("PF_MoonSurfaceTerrain", values);
        }

        /// <summary>
        /// 判据：**编译产物**里 `GenerateTileFor` 确实构造了 `RimWorld.Planet.SurfaceTile`（而不是某个泛化的
        /// `Tile`）—— 与下面的源码文本检查互补：这条查的是 IL。
        ///
        /// ⚠ **2026-09-23 修仪器**（本库纪律：失败先怀疑仪器、先修仪器）：原写法"扫到第一个 `0x73` 就当 newobj"
        /// 是错的 —— **IL 是变长指令流**，操作数里也会出现 `0x73` 字节。新增类型使元数据 RID 位移之后，
        /// 假 newobj 解出一个不存在的 token（实测 `0x0a06000a`，RID 393226）⇒ `ResolveMethod` 直接抛，
        /// 报出来的是"仪器崩了"，而不是"被测物错了"。
        /// 改成：把所有 `0x73` 候选都试一遍、**跳过解不出来的**，断言"解析出的构造函数里有 SurfaceTile"
        /// ⇒ 判据**没有放宽**（仍然要求那个构造函数来自 SurfaceTile），只是不再被假字节误导。
        /// </summary>
        [Fact]
        public void MoonTiles_GenerateTileForConstructsSurfaceTile()
        {
            var method = typeof(WorldGenStep_MoonTiles).GetMethod(
                "GenerateTileFor",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.NotNull(method);
            byte[] il = method.GetMethodBody().GetILAsByteArray();
            byte newobj = (byte)System.Reflection.Emit.OpCodes.Newobj.Value;

            var constructedTypes = new System.Collections.Generic.List<string>();
            for (int i = 0; i <= il.Length - 5; i++)
            {
                if (il[i] != newobj)
                {
                    continue;
                }

                ConstructorInfo ctor;
                try
                {
                    ctor = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)) as ConstructorInfo;
                }
                catch (System.ArgumentException)
                {
                    continue;   // 假 newobj：token 在该模块里不存在 ⇒ 跳过（不是被测物的错）
                }
                if (ctor?.DeclaringType != null)
                {
                    constructedTypes.Add(ctor.DeclaringType.FullName);
                }
            }

            Assert.Contains("RimWorld.Planet.SurfaceTile", constructedTypes);
        }

        [Fact]
        public void MoonTilesSource_InitializesSurfaceTile()
        {
            string source = File.ReadAllText(SourcePath("WorldGenStep/WorldGenStep_MoonTiles.cs"));
            foreach (var required in new[]
            {
                "new SurfaceTile(tile)",
                "BiomeDef primaryBiome = SelectBiomeFor(tile);",
                "PrimaryBiome = primaryBiome",
                "tile.LayerDef.defName != \"Moon\"",
                "Find.WorldGrid.LongLatOf(tile).y",
                "IsWaterIceLatitude(latitude)",
                "SelectMoonBiomeCategory(latitude, Rand.Chance(0.5f))",
                "Rand.Chance(0.5f)",
                "PFDefs.PF_MoonBiomeWater",
                "PFDefs.PF_MoonBiomeBasalt",
                "PFDefs.PF_MoonBiomeRegolith",
                "hilliness = SelectHillinessForBiomeDefName(primaryBiome.defName)",
                "Hilliness.SmallHills",
                "temperature = 0f",
                "rainfall = 0f",
                "swampiness = 0f"
            })
            {
                Assert.True(source.Contains(required), $"PF_MoonTiles is missing required SurfaceTile initialization: {required}");
            }
        }

        [Fact]
        public void MoonSurfaceMapGeneration_ClassificationAndSkipRules()
        {
            Assert.True(MoonSurfaceMapGeneration.IsMoonSurfaceBiomeDefName("PF_MoonBiomeBasalt"));
            Assert.True(MoonSurfaceMapGeneration.IsMoonSurfaceBiomeDefName("PF_MoonBiomeRegolith"));
            Assert.True(MoonSurfaceMapGeneration.IsMoonSurfaceBiomeDefName("PF_MoonBiomeWater"));
            Assert.False(MoonSurfaceMapGeneration.IsMoonSurfaceBiomeDefName("PF_MoonBiome"));
            Assert.False(MoonSurfaceMapGeneration.IsMoonSurfaceBiomeDefName("Desert"));

            Assert.True(MoonSurfaceMapGeneration.ShouldSkipGenStepForBiomeDefName("PF_MoonBiomeBasalt", "RockChunks"));
            Assert.True(MoonSurfaceMapGeneration.ShouldSkipGenStepForBiomeDefName("PF_MoonBiomeWater", "RockChunks"));
            Assert.True(MoonSurfaceMapGeneration.ShouldSkipGenStepForBiomeDefName("PF_MoonBiomeBasalt", "Snow"));
            Assert.True(MoonSurfaceMapGeneration.ShouldSkipGenStepForBiomeDefName("PF_MoonBiomeRegolith", "Snow"));
            Assert.True(MoonSurfaceMapGeneration.ShouldSkipGenStepForBiomeDefName("PF_MoonBiomeWater", "Snow"));
            Assert.True(MoonSurfaceMapGeneration.ShouldSkipGenStepForBiomeDefName("PF_MoonBiomeRegolith", "ScatterRuinsSimple"));
            Assert.True(MoonSurfaceMapGeneration.ShouldSkipGenStepForBiomeDefName("PF_MoonBiomeBasalt", "ScatterShrines"));
            Assert.True(MoonSurfaceMapGeneration.ShouldSkipGenStepForBiomeDefName("PF_MoonBiomeWater", "AncientUtilityBuilding"));
            Assert.True(MoonSurfaceMapGeneration.ShouldSkipGenStepForBiomeDefName("PF_MoonBiomeRegolith", "MechanoidRemains"));
            Assert.True(MoonSurfaceMapGeneration.ShouldSkipGenStepForBiomeDefName("PF_MoonBiomeWater", "RocksFromGrid"));
            Assert.True(MoonSurfaceMapGeneration.ShouldSkipGenStepForBiomeDefName("PF_MoonBiomeRegolith", "RocksFromGrid_NoMinerals"));
            Assert.False(MoonSurfaceMapGeneration.ShouldSkipGenStepForBiomeDefName("PF_MoonBiomeBasalt", "RocksFromGrid"));
            Assert.False(MoonSurfaceMapGeneration.ShouldSkipGenStepForBiomeDefName("PF_MoonBiomeBasalt", "ScenParts"));
            Assert.False(MoonSurfaceMapGeneration.ShouldSkipGenStepForBiomeDefName("Desert", "RockChunks"));
            Assert.False(MoonSurfaceMapGeneration.ShouldSkipGenStepForBiomeDefName("Desert", "Snow"));
        }

        [Fact]
        public void MoonSurfaceMapGeneration_TerrainReplacementRules()
        {
            Assert.True(MoonSurfaceMapGeneration.ShouldReplaceMoonSurfaceTerrainForBiomeDefName("PF_MoonBiomeRegolith", "RoughGranite", true));
            Assert.True(MoonSurfaceMapGeneration.ShouldReplaceMoonSurfaceTerrainForBiomeDefName("PF_MoonBiomeWater", "RoughSlate", true));
            Assert.True(MoonSurfaceMapGeneration.ShouldReplaceMoonSurfaceTerrainForBiomeDefName("PF_MoonBiomeRegolith", "Gravel", false));
            Assert.False(MoonSurfaceMapGeneration.ShouldReplaceMoonSurfaceTerrainForBiomeDefName("PF_MoonBiomeWater", "Ice", false));
            Assert.False(MoonSurfaceMapGeneration.ShouldReplaceMoonSurfaceTerrainForBiomeDefName("PF_MoonBiomeRegolith", "PF_MoonDirt", false));
            Assert.True(MoonSurfaceMapGeneration.ShouldReplaceMoonSurfaceTerrainForBiomeDefName("PF_MoonBiomeBasalt", "Gravel", false));
            Assert.False(MoonSurfaceMapGeneration.ShouldReplaceMoonSurfaceTerrainForBiomeDefName("PF_MoonBiomeBasalt", "RoughVacstone", true));
            Assert.False(MoonSurfaceMapGeneration.ShouldReplaceMoonSurfaceTerrainForBiomeDefName("Desert", "RoughGranite", true));
        }

        [Fact]
        public void MoonSurfaceMapGeneration_HarmonyPrefixIsRegistered()
        {
            // Touch the type so the static constructor can install its Harmony patch.
            _ = MoonSurfaceMapGeneration.IsMoonSurfaceBiomeDefName("Desert");

            var generateContentsIntoMap = typeof(MapGenerator).GetMethods(StaticFlags)
                .Where(m => m.Name == "GenerateContentsIntoMap" && m.GetParameters().Length == 4)
                .FirstOrDefault();
            Assert.NotNull(generateContentsIntoMap);

            var info = Harmony.GetPatchInfo(generateContentsIntoMap);
            Assert.NotNull(info);
            Assert.Contains(
                info.Prefixes.Select(p => p.owner),
                owner => owner == "PioneerFramework.MoonSurfaceMapGeneration");
        }

        [Fact]
        public void MoonSurfaceSource_ContainsRequiredBehavior()
        {
            string source = File.ReadAllText(SourcePath("MapGeneration/MoonSurfaceMapGeneration.cs"));
            foreach (var required in new[]
            {
                "GenerateContentsIntoMap",
                "GenStep_RockChunks",
                "GenStep_RocksFromGrid",
                "ScatterRuinsSimple",
                "ScatterShrines",
                "ShouldReplaceMoonSurfaceTerrainForBiomeDefName",
                "!MoonSurfaceMapGeneration.IsMoonSurfaceBiomeDefName(",
                "\"Gravel\"",
                "terrain.IsRock",
                "PF_MoonDirt",
                "SetTerrain"
            })
            {
                Assert.True(source.Contains(required), $"Moon surface map generation is missing required behavior: {required}");
            }
        }
    }
}
