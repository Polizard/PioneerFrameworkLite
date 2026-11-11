using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace PioneerFramework.MapGeneration
{
    /// <summary>Terminal provenance only; no native objects, intermediate grids, resume points, or XML resampling.</summary>
    public sealed record LocalMapGenerationRecord(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("source")] LocalMapSourceRecord Source,
        [property: JsonPropertyName("purpose")] MapGenerationPurpose Purpose,
        [property: JsonPropertyName("frozenRecipe")] string FrozenRecipe,
        [property: JsonPropertyName("recipeHash")] string RecipeHash,
        [property: JsonPropertyName("rootSeed")] ulong RootSeed,
        [property: JsonPropertyName("state")] MapGenerationState State,
        [property: JsonPropertyName("stage")] string Stage,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("steps")] IReadOnlyList<LocalMapStepRecord> Steps)
    {
        public static LocalMapGenerationRecord Capture(FrozenLocalMapContextData data, MapGenerationState state, string stage, string? error, MapGenerationReport? report)
            => new(1, LocalMapSourceRecord.Capture(data.Source), data.Purpose, Convert.ToBase64String(LocalMapGenerationCodec.Encode(data.Specification).Span),
                LocalMapGenerationCodec.SnapshotHash(data.Specification), MapGenerationSeeds.Root(data.Source), state, stage, error,
                Array.AsReadOnly((report?.Steps ?? Array.Empty<MapGenerationStepReport>()).Select(s => new LocalMapStepRecord(s.Key, s.Stage, s.HandlerId,
                    s.ConfigurationVersion, s.AlgorithmVersion, s.Seed, s.IsOpaque, s.Completed, s.PostInitialized, s.Error)).ToArray()));
        public void Validate(string sourceId, int tileId, string? failure)
        {
            var source = Source.Restore();
            var spec = LocalMapGenerationCodec.Decode(Convert.FromBase64String(FrozenRecipe));
            if (Version != 1 || source.SourceId != sourceId || source.TileId != tileId || !Enum.IsDefined(Purpose) ||
                State is not (MapGenerationState.Ready or MapGenerationState.Failed) || string.IsNullOrWhiteSpace(Stage) ||
                RootSeed != MapGenerationSeeds.Root(source) || RecipeHash != LocalMapGenerationCodec.SnapshotHash(spec) ||
                (State == MapGenerationState.Failed) != (failure != null) || State == MapGenerationState.Failed && string.IsNullOrWhiteSpace(Error) ||
                Steps == null || Steps.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() != Steps.Count ||
                Steps.Any(s => string.IsNullOrWhiteSpace(s.Key) || string.IsNullOrWhiteSpace(s.HandlerId) || !Enum.IsDefined(s.Stage) ||
                    s.ConfigurationVersion <= 0 || s.AlgorithmVersion <= 0 || s.PostInitialized && !s.Completed))
                throw new ArgumentException("Invalid local generation provenance.");
        }
    }
    public sealed record FrozenLocalMapContextData(MapSourceContext Source, LocalMapGenerationSpec Specification, MapGenerationPurpose Purpose);
    public sealed record LocalMapSourceRecord(
        [property: JsonPropertyName("sourceId")] string SourceId,
        [property: JsonPropertyName("generationSeed")] ulong GenerationSeed,
        [property: JsonPropertyName("tileId")] int TileId,
        [property: JsonPropertyName("sizeX")] int SizeX,
        [property: JsonPropertyName("sizeZ")] int SizeZ,
        [property: JsonPropertyName("biomeId")] string BiomeId,
        [property: JsonPropertyName("numericData")] IReadOnlyDictionary<string, double> NumericData,
        [property: JsonPropertyName("tags")] IReadOnlyDictionary<string, string> Tags)
    {
        public static LocalMapSourceRecord Capture(MapSourceContext source) => new(source.SourceId, source.GenerationSeed, source.TileId,
            source.SizeX, source.SizeZ, source.BiomeId, source.NumericData, source.Tags);
        public MapSourceContext Restore() => new(SourceId, GenerationSeed, TileId, SizeX, SizeZ, BiomeId, NumericData, Tags);
    }
    public sealed record LocalMapStepRecord(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("stage")] MapGenerationStage Stage,
        [property: JsonPropertyName("handlerId")] string HandlerId,
        [property: JsonPropertyName("configurationVersion")] int ConfigurationVersion,
        [property: JsonPropertyName("algorithmVersion")] int AlgorithmVersion,
        [property: JsonPropertyName("seed")] ulong Seed,
        [property: JsonPropertyName("isOpaque")] bool IsOpaque,
        [property: JsonPropertyName("completed")] bool Completed,
        [property: JsonPropertyName("postInitialized")] bool PostInitialized,
        [property: JsonPropertyName("error")] string? Error);
}
