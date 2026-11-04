using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// Harmony host for native map generation. The single source of truth for which native map steps
    /// to skip (and which terrain to replace) is the owning celestial map profile (e.g. PF_MoonMaps);
    /// layers without a map profile are left untouched.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class MoonSurfaceMapGeneration
    {
        static MoonSurfaceMapGeneration()
        {
            var generateContentsIntoMap = Celestial.CelestialNativeCompatibility.RequireMethod("MapGenerator.GenerateContentsIntoMap");

            new Harmony("PioneerFramework.MoonSurfaceMapGeneration").Patch(
                generateContentsIntoMap,
                prefix: new HarmonyMethod(
                    typeof(MoonSurfaceMapGeneration),
                    nameof(GenerateContentsIntoMap_Prefix)));
            Celestial.CelestialNativeCompatibility.RequireInstalledHooks("MapGenerator.GenerateContentsIntoMap.prefix.GenerateContentsIntoMap_Prefix");
        }

        internal static void GenerateContentsIntoMap_Prefix(
            ref IEnumerable<GenStepWithParams> genStepDefs,
            Map map)
        {
            if (genStepDefs == null || !Celestial.CelestialMapGeneration.TryGetProfile(map, out var profile)) return;
            genStepDefs = genStepDefs
                .Where(step => !Celestial.CelestialMapGeneration.ShouldSkip(profile, map.Biome.defName, step.def))
                .ToList();
        }
    }

    /// <summary>Terrain replacement for native maps; data comes from the celestial map profile only.</summary>
    public class GenStep_MoonSurfaceTerrain : GenStep
    {
        public override int SeedPart => 194892376;

        public override void Generate(Map map, GenStepParams parms)
        {
            if (!Celestial.CelestialMapGeneration.TryGetProfile(map, out var profile)) return;
            Celestial.CelestialMapGeneration.ReplaceTerrain(map, profile);
        }
    }
}
