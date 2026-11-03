using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Celestial
{
    public static class CelestialMapGeneration
    {
        private sealed class FrozenProfiles
        {
            public CelestialMapProfile? Map { get; }
            public CelestialPlanetTerrainConfiguration? Terrain { get; }
            public ConcurrentDictionary<int, double> Geology { get; } = new();
            public FrozenProfiles(CelestialSurfaceLayer layer)
            {
                if (layer.Header.GenerationConfiguration.IsEmpty) return;
                var records = CelestialGenerationConfiguration.Decode(layer.Header.GenerationConfiguration);
                if (!records.Success) throw new InvalidOperationException("Invalid celestial generation configuration.");
                foreach (var record in records.Value)
                {
                    if (record.HandlerId == MapGenerationResolvedProperties.Handler)
                    {
                        if (Map != null) throw new InvalidOperationException("Ambiguous celestial map configuration.");
                        var decoded = MapGenerationResolvedProperties.Deserialize(record.Key, record.ConfigurationVersion, record.ConfigurationPayload);
                        if (!decoded.Success) throw new InvalidOperationException("Invalid celestial map profile.");
                        Map = decoded.Value.Profile;
                    }
                    if (record.HandlerId == BiomeGenerationResolvedProperties.Handler)
                    {
                        var decoded = BiomeGenerationResolvedProperties.Deserialize(record.Key, record.ConfigurationVersion, record.ConfigurationPayload);
                        if (!decoded.Success) throw new InvalidOperationException("Invalid celestial terrain profile.");
                        Terrain = decoded.Value.Profile.Terrain;
                    }
                }
            }
        }
        private static readonly ConditionalWeakTable<CelestialSurfaceLayer, FrozenProfiles> profiles = new();
        private static readonly CelestialPlanetTerrainConfiguration defaultField = new();
        public static bool TryGetTerrain(PlanetLayer native, out CelestialPlanetTerrainConfiguration terrain)
        {
            terrain = native is CelestialSurfaceLayer layer ? profiles.GetValue(layer, static value => new(value)).Terrain! : null!;
            return terrain != null;
        }
        public static bool TryGetDiversity(PlanetTile tile, out CelestialPlanetMapDiversity diversity)
        {
            diversity = null!;
            if (!CelestialPlanetEnvironmentHooks.TryGetLayer(tile, out var native) || native is not CelestialSurfaceLayer layer) return false;
            diversity = profiles.GetValue(layer, static value => new(value)).Map?.Diversity!;
            return diversity != null;
        }
        public static double GeologyFactor(PlanetTile tile, CelestialPlanetMapDiversity diversity)
        {
            var layer = (CelestialSurfaceLayer)tile.Layer;
            var frozen = profiles.GetValue(layer, static value => new(value));
            double value = frozen.Geology.GetOrAdd(tile.tileId, index => {
                var coords = layer.LongLatOf(index);
                return CelestialPlanetTerrain.Sample(frozen.Terrain ?? defaultField, coords.x, coords.y, frozen.Terrain?.FieldSeed ?? layer.Header.GenerationSeed).Geology;
            });
            return 1 + diversity.GeologyVariation * (2 * value - 1);
        }
        public static bool TryGetProfile(Map map, out CelestialMapProfile profile)
        {
            profile = null!;
            if (map?.Tile.Layer is not CelestialSurfaceLayer layer) return false;
            profile = profiles.GetValue(layer, static value => new(value)).Map ?? throw new InvalidOperationException("MapGenerationCapabilityMissing");
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
