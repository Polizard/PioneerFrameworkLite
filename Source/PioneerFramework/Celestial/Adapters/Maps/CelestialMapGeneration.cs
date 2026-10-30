using System;
using System.Linq;
using Verse;

namespace PioneerFramework.Celestial
{
    public static class CelestialMapGeneration
    {
        public static bool TryGetProfile(Map map, out CelestialMapProfile profile)
        {
            profile = null!;
            if (map?.Tile.Layer is not CelestialSurfaceLayer layer) return false;
            var records = CelestialGenerationConfiguration.Decode(layer.Header.GenerationConfiguration);
            if (!records.Success) throw new InvalidOperationException("Invalid celestial map configuration.");
            var record = records.Value.SingleOrDefault(record => record.HandlerId == MapGenerationResolvedProperties.Handler);
            if (record == null) throw new InvalidOperationException("MapGenerationCapabilityMissing");
            var properties = MapGenerationResolvedProperties.Deserialize(record.Key, record.ConfigurationVersion, record.ConfigurationPayload);
            if (!properties.Success) throw new InvalidOperationException("Invalid celestial map profile.");
            profile = properties.Value.Profile;
            return true;
        }
        public static bool ShouldSkip(CelestialMapProfile profile, string biomeId, GenStepDef step)
        {
            if (step == null) return false;
            var rule = profile.BiomeRules.SingleOrDefault(rule => rule.BiomeId == biomeId);
            if (profile.SkipStepNames.Contains(step.defName, StringComparer.Ordinal) || profile.SkipStepPrefixes.Any(prefix => step.defName?.StartsWith(prefix, StringComparison.Ordinal) == true) ||
                rule?.SkipStepNames.Contains(step.defName, StringComparer.Ordinal) == true) return true;
            for (Type? type = step.genStep?.GetType(); type != null && typeof(GenStep).IsAssignableFrom(type); type = type.BaseType)
                if (type.FullName != null && (profile.SkipStepTypes.Contains(type.FullName, StringComparer.Ordinal) || rule?.SkipStepTypes.Contains(type.FullName, StringComparer.Ordinal) == true)) return true;
            return false;
        }
        public static void ReplaceTerrain(Map map, CelestialMapProfile profile)
        {
            if (profile.ReplacementTerrainId == null) return;
            var target = DefDatabase<TerrainDef>.GetNamed(profile.ReplacementTerrainId);
            string biome = map.Biome.defName;
            foreach (var cell in map.AllCells)
            {
                var terrain = map.terrainGrid.TerrainAt(cell);
                if (terrain != null && profile.ShouldReplace(biome, terrain.defName, terrain.IsRock)) map.terrainGrid.SetTerrain(cell, target);
            }
        }
    }
}
