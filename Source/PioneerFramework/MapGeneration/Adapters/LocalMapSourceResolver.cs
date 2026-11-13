using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PioneerFramework.Celestial;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.MapGeneration
{
    public sealed class FrozenLocalMapContext
    {
        public MapSourceContext Source { get; }
        public LocalMapGenerationSpec Specification { get; }
        public FrozenLocalMapContext(MapSourceContext source, LocalMapGenerationSpec specification)
        { Source = source ?? throw new ArgumentNullException(nameof(source)); Specification = specification ?? throw new ArgumentNullException(nameof(specification)); }
    }
    /// <summary>Special map generators require explicit opt-in; all decisions are read before content execution.</summary>
    public static class LocalMapSourceResolver
    {
        private static readonly Dictionary<string, bool> generatorPolicies = new(StringComparer.Ordinal);
        public static void RegisterGenerator(string generatorId, bool participate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(generatorId);
            if (LocalMapGenerationModules.Registry.IsFrozen) throw new InvalidOperationException("Local generation registration is frozen.");
            if (!generatorPolicies.TryAdd(generatorId, participate)) throw new ArgumentException("Duplicate generator policy.");
        }
        public static bool IsTarget(Map map, MapGeneratorDef generator)
            => map != null && IsTarget(map.Tile, generator, map.IsPocketMap);
        public static bool IsTarget(PlanetTile tile, MapGeneratorDef generator, bool isPocketMap)
        {
            if (OutsideSurface(tile, generator, isPocketMap)) return false;
            if (generatorPolicies.TryGetValue(generator.defName, out bool participate)) return participate;
            return generator.defName is "Base_Player" or "Base_Faction" or "Encounter" or "Empty" ||
                generator.genSteps?.Any(step => step.genStep is GenStep_ElevationFertility) == true && generator.genSteps.Any(step => step.genStep is GenStep_Terrain);
        }
        public static CelestialResult<FrozenLocalMapContext> Resolve(MapParent parent, IntVec3 size, MapGeneratorDef generator, bool isPocketMap,
            ICelestialCapabilityResolver? capabilities = null)
            => parent == null ? CelestialResult.Reject<FrozenLocalMapContext>(new CelestialError("LocalMapSourceOutOfScope")) :
                Resolve(parent.Tile, size.x, size.z, generator, isPocketMap, capabilities);
        public static CelestialResult<FrozenLocalMapContext> Resolve(Map map, MapGeneratorDef generator, ICelestialCapabilityResolver? capabilities = null)
            => map == null ? CelestialResult.Reject<FrozenLocalMapContext>(new CelestialError("LocalMapSourceOutOfScope")) :
                Resolve(map.Tile, map.Size.x, map.Size.z, generator, map.IsPocketMap, capabilities);
        private static bool OutsideSurface(PlanetTile tile, MapGeneratorDef generator, bool isPocketMap)
            => generator == null || isPocketMap || generator.isUnderground || generator.renderWorld || generator.pocketMapProperties != null || !TryGetOwnedLayer(tile, out _);
        private static bool TryGetOwnedLayer(PlanetTile tile, out CelestialSurfaceLayer? layer)
        {
            layer = null;
            if (!tile.Valid || Current.Game?.World?.grid == null) return false;
            try { layer = tile.Layer as CelestialSurfaceLayer; }
            catch (KeyNotFoundException) { return false; }
            return layer != null && tile.tileId < layer.TilesCount;
        }
        public static CelestialResult<FrozenLocalMapContext> Resolve(PlanetTile tile, int sizeX, int sizeZ, MapGeneratorDef generator, bool isPocketMap,
            ICelestialCapabilityResolver? capabilities = null)
        {
            CelestialResult<FrozenLocalMapContext> Reject(string code, string? detail = null) => CelestialResult.Reject<FrozenLocalMapContext>(new CelestialError(code, Detail: detail));
            if (OutsideSurface(tile, generator, isPocketMap)) return Reject("LocalMapSourceOutOfScope");
            if (generatorPolicies.TryGetValue(generator.defName, out bool explicitPolicy) && !explicitPolicy) return Reject("LocalMapGeneratorOutOfScope");
            bool standardTarget = IsTarget(tile, generator, isPocketMap);
            var layer = (CelestialSurfaceLayer)tile.Layer;
            if (layer.Header == null || layer.Header.State != CelestialSurfaceState.Ready || layer.Tiles?.Count != layer.TilesCount)
                return Reject(standardTarget ? "LocalMapSurfaceNotReady" : "LocalMapGeneratorOutOfScope");
            capabilities ??= CelestialWorldServices.TryGetCurrent()?.Query;
            if (capabilities == null) return Reject(standardTarget ? "LocalMapRuntimeMissing" : "LocalMapGeneratorOutOfScope");
            var effective = capabilities.GetCapabilities<ICelestialMapGenerationCapability>(layer.Header.OwnerId);
            if (effective.Count != 1) return Reject(standardTarget ? "MapGenerationCapabilityMissing" : "LocalMapGeneratorOutOfScope");
            var capability = effective[0];
            try
            {
                LocalMapGenerationSpec spec = capability is CelestialBodyComp component && component.ResolvedProperties is ICelestialLocalMapGenerationProperties properties
                    ? properties.Specification : capability.GetSpecification();
                if (spec == null) return Reject("LocalMapSpecificationMissing");
                bool optedOut = generatorPolicies.TryGetValue(generator.defName, out bool policy) && !policy;
                if (optedOut || !IsTarget(tile, generator, isPocketMap) && spec.NativeGeneratorId != generator.defName) return Reject("LocalMapGeneratorOutOfScope");
                var registry = LocalMapGenerationModules.Freeze();
                LocalMapGenerationModules.ValidateSpecification(spec, registry);
                CelestialGenerationOrigin? origin = null;
                if (capabilities is CelestialRuntime runtime && runtime.Catalog.TryGetInstance(layer.Header.OwnerId, out var body)) origin = body.GenerationOrigin;
                var terrain = capabilities.GetCapabilities<CelestialBodyComp>(layer.Header.OwnerId).Select(comp => comp.ResolvedProperties)
                    .OfType<BiomeGenerationResolvedProperties>().SingleOrDefault()?.Profile.Terrain;
                return CelestialResult.Ok(new FrozenLocalMapContext(CaptureSource(tile, sizeX, sizeZ, origin, terrain), spec));
            }
            catch (Exception exception) { return Reject("LocalMapSpecificationInvalid", exception.ToString()); }
        }
        public static MapSourceContext CaptureSource(Map map, CelestialGenerationOrigin? origin = null, CelestialPlanetTerrainConfiguration? terrain = null)
        {
            ArgumentNullException.ThrowIfNull(map);
            return CaptureSource(map.Tile, map.Size.x, map.Size.z, origin, terrain);
        }
        public static MapSourceContext CaptureSource(PlanetTile address, int sizeX, int sizeZ, CelestialGenerationOrigin? origin = null,
            CelestialPlanetTerrainConfiguration? terrain = null)
        {
            if (!TryGetOwnedLayer(address, out var owned)) throw new ArgumentException("Map has no owned planetary source.");
            var layer = owned!;
            var tile = layer[address.tileId];
            var coords = layer.LongLatOf(address.tileId);
            double geology = CelestialPlanetTerrain.Sample(terrain ?? new(), coords.x, coords.y, terrain?.FieldSeed ?? layer.Header.GenerationSeed).Geology;
            var numeric = new Dictionary<string, double>(StringComparer.Ordinal) {
                ["temperature"] = tile.temperature, ["elevation"] = tile.elevation, ["rainfall"] = tile.rainfall,
                ["hilliness"] = (int)tile.hilliness, ["swampiness"] = tile.swampiness, ["pollution"] = tile.pollution,
                ["longitude"] = coords.x, ["latitude"] = coords.y, ["geology"] = geology,
                ["coastDirection"] = Find.World.CoastDirectionAt(address).AsInt,
                ["riverCount"] = (tile as SurfaceTile)?.Rivers?.Count ?? 0 };
            var tags = new Dictionary<string, string>(StringComparer.Ordinal);
            if (origin != null)
            {
                tags.Add("generationKey", origin.GenerationKey); tags.Add("worldSeed", origin.WorldSeed.ToString(CultureInfo.InvariantCulture));
                tags.Add("generationAlgorithmVersion", origin.AlgorithmVersion.ToString(CultureInfo.InvariantCulture));
            }
            if (tile is SurfaceTile surface && surface.Rivers != null)
                tags.Add("rivers", string.Join(",", surface.Rivers.Select(river => river.river.defName).OrderBy(id => id, StringComparer.Ordinal)));
            return new MapSourceContext(layer.Header.OwnerId.Value, unchecked((ulong)layer.Header.GenerationSeed), address.tileId,
                sizeX, sizeZ, tile.PrimaryBiome.defName, numeric, tags);
        }
    }
}
