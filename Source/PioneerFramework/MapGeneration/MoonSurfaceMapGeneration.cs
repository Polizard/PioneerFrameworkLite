using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    [StaticConstructorOnStartup]
    public static class MoonSurfaceMapGeneration
    {
        private const string MoonBiomeBasalt = "PF_MoonBiomeBasalt";
        private const string MoonBiomeRegolith = "PF_MoonBiomeRegolith";
        private const string MoonBiomeWater = "PF_MoonBiomeWater";

        static MoonSurfaceMapGeneration()
        {
            var generateContentsIntoMap = AccessTools.Method(
                typeof(MapGenerator),
                "GenerateContentsIntoMap",
                [typeof(IEnumerable<GenStepWithParams>),
                    typeof(Map),
                    typeof(int),
                    typeof(bool)]);
            if (generateContentsIntoMap == null)
            {
                Log.Error("[PioneerFramework] Could not patch MapGenerator.GenerateContentsIntoMap for Moon maps.");
                return;
            }

            new Harmony("PioneerFramework.MoonSurfaceMapGeneration").Patch(
                generateContentsIntoMap,
                prefix: new HarmonyMethod(
                    typeof(MoonSurfaceMapGeneration),
                    nameof(GenerateContentsIntoMap_Prefix)));
        }

        internal static void GenerateContentsIntoMap_Prefix(
            ref IEnumerable<GenStepWithParams> genStepDefs,
            Map map)
        {
            string? biomeDefName = map?.Biome?.defName;
            if (genStepDefs == null ||
                !IsMoonSurfaceBiomeDefName(biomeDefName))
            {
                return;
            }

            genStepDefs = genStepDefs
                .Where(step => !ShouldSkipGenStep(biomeDefName, step))
                .ToList();
        }

        internal static bool IsMoonSurfaceBiomeDefName(string? biomeDefName)
        {
            return biomeDefName == MoonBiomeBasalt ||
                biomeDefName == MoonBiomeRegolith ||
                biomeDefName == MoonBiomeWater;
        }

        internal static bool ShouldSkipGenStepForBiomeDefName(
            string? biomeDefName,
            string genStepDefName)
        {
            if (!IsMoonSurfaceBiomeDefName(biomeDefName) ||
                string.IsNullOrEmpty(genStepDefName))
            {
                return false;
            }

            if (genStepDefName == "RockChunks" ||
                genStepDefName == "Snow" ||
                genStepDefName == "ScatterRuinsSimple" ||
                genStepDefName == "ScatterShrines" ||
                genStepDefName == "ScatterRoadDebris" ||
                genStepDefName == "ScatterCaveDebris" ||
                genStepDefName == "MechanoidRemains" ||
                genStepDefName == "VoidMonolith" ||
                genStepDefName.StartsWith("Ancient", StringComparison.Ordinal))
            {
                return true;
            }

            return IsFlatMoonSurfaceBiomeDefName(biomeDefName) &&
                (genStepDefName == "RocksFromGrid" ||
                genStepDefName == "RocksFromGrid_NoMinerals");
        }

        internal static bool ShouldReplaceMoonSurfaceTerrainForBiomeDefName(
            string? biomeDefName,
            string terrainDefName,
            bool isRockTerrain)
        {
            if (!IsMoonSurfaceBiomeDefName(biomeDefName))
            {
                return false;
            }

            return terrainDefName == "Gravel" ||
                (IsFlatMoonSurfaceBiomeDefName(biomeDefName) &&
                isRockTerrain);
        }

        private static bool ShouldSkipGenStep(
            string? biomeDefName,
            GenStepWithParams genStepWithParams)
        {
            GenStepDef genStepDef = genStepWithParams.def;
            if (genStepDef == null)
            {
                return false;
            }

            if (ShouldSkipGenStepForBiomeDefName(
                biomeDefName,
                genStepDef.defName))
            {
                return true;
            }

            GenStep genStep = genStepDef.genStep;
            if (genStep is GenStep_RockChunks ||
                genStep is GenStep_ScatterRuinsSimple ||
                genStep is GenStep_ScatterShrines)
            {
                return true;
            }

            return IsFlatMoonSurfaceBiomeDefName(biomeDefName) &&
                genStep is GenStep_RocksFromGrid;
        }

        internal static bool IsFlatMoonSurfaceBiomeDefName(string? biomeDefName)
        {
            return biomeDefName == MoonBiomeRegolith ||
                biomeDefName == MoonBiomeWater;
        }
    }

    public class GenStep_MoonSurfaceTerrain : GenStep
    {
        public override int SeedPart => 194892376;

        public override void Generate(Map map, GenStepParams parms)
        {
            string? biomeDefName = map?.Biome?.defName;
            if (!MoonSurfaceMapGeneration.IsMoonSurfaceBiomeDefName(
                biomeDefName))
            {
                return;
            }

            TerrainDef moonDirt = DefDatabase<TerrainDef>.GetNamedSilentFail(
                "PF_MoonDirt");
            if (moonDirt == null)
            {
                Log.ErrorOnce(
                    "[PioneerFramework] PF_MoonDirt is missing; Moon gravel terrain could not be replaced.",
                    194892377);
                return;
            }

            foreach (IntVec3 cell in map.AllCells)
            {
                TerrainDef terrain = map.terrainGrid.TerrainAt(cell);
                if (terrain != null &&
                    MoonSurfaceMapGeneration
                        .ShouldReplaceMoonSurfaceTerrainForBiomeDefName(
                            biomeDefName,
                            terrain.defName,
                            terrain.IsRock))
                {
                    map.terrainGrid.SetTerrain(cell, moonDirt);
                }
            }
        }
    }
}
