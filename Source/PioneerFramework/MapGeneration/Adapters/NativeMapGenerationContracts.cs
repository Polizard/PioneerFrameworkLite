using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace PioneerFramework.MapGeneration
{
    public sealed class NativeMapGenerationStep
    {
        public string Token { get; }
        public GenStepWithParams Step { get; }
        public NativeMapGenerationStep(string token, GenStepWithParams step)
        { ArgumentException.ThrowIfNullOrWhiteSpace(token); Token = token; Step = step; if (step.def?.genStep == null) throw new ArgumentException("Missing native step."); }
    }
    public interface INativeMapGenerationServices
    {
        Map Map { get; }
        MapSourceContext Source { get; }
        IReadOnlyList<NativeMapGenerationStep> NativeSteps { get; }
        InitialMapInputs? InitialOverrides { get; }
        void SetInitialOverrides(InitialMapInputs inputs);
        void GenerateNative(string token, ulong seed);
        void PostInitializeNative(string token, ulong seed);
        void ApplyTerrainReplacement(TerrainReplacementConfiguration configuration);
    }
    /// <summary>Metadata only. Constructors and this method must not mutate Map/world/Defs or execute callbacks.
    /// The host inspects these before final compilation and rejects selection of required site/quest content.</summary>
    public interface INativeMapPlanContributor
    {
        NativeMapPlanContribution Contribute(MapSourceContext source);
    }
    public sealed class NativeMapPlanContribution
    {
        public IReadOnlyList<NativeMapStepSelector> Remove { get; }
        public IReadOnlyList<NativeMapStepSelector> After { get; }
        /// <summary>Host adds edges from every retained native node in this stage to the contributing recipe node.</summary>
        public MapGenerationStage? AfterNativeStage { get; }
        /// <summary>Host adds edges from the contributing recipe node to every retained native node in this stage.</summary>
        public MapGenerationStage? BeforeNativeStage { get; }
        /// <summary>Immutable session inputs the host may preload before native initialization.</summary>
        public InitialMapInputs? InitialInputs { get; }
        /// <summary>Native workspace grids published by this module (Elevation/Fertility/Caves).
        /// Declaration adds ordering; the host verifies actual grids before every known consumer.</summary>
        public IReadOnlyList<string> ProvidesNativeInputs { get; }
        public NativeMapPlanContribution(IEnumerable<NativeMapStepSelector>? remove = null, IEnumerable<NativeMapStepSelector>? after = null,
            MapGenerationStage? afterNativeStage = null, MapGenerationStage? beforeNativeStage = null, InitialMapInputs? initialInputs = null,
            IEnumerable<string>? providesNativeInputs = null)
        {
            if (afterNativeStage.HasValue && !Enum.IsDefined(afterNativeStage.Value)) throw new ArgumentOutOfRangeException(nameof(afterNativeStage));
            if (beforeNativeStage.HasValue && !Enum.IsDefined(beforeNativeStage.Value)) throw new ArgumentOutOfRangeException(nameof(beforeNativeStage));
            Remove = Freeze(remove); After = Freeze(after); AfterNativeStage = afterNativeStage; BeforeNativeStage = beforeNativeStage; InitialInputs = initialInputs;
            var provided = (providesNativeInputs ?? Array.Empty<string>()).ToArray();
            if (provided.Any(name => name is not ("Elevation" or "Fertility" or "Caves")) || provided.Distinct(StringComparer.Ordinal).Count() != provided.Length)
                throw new ArgumentException("Invalid/duplicate native input declaration.");
            ProvidesNativeInputs = Array.AsReadOnly(provided);
        }
        private static IReadOnlyList<NativeMapStepSelector> Freeze(IEnumerable<NativeMapStepSelector>? values)
        {
            var copy = (values ?? Array.Empty<NativeMapStepSelector>()).ToArray();
            if (copy.Any(value => value == null)) throw new ArgumentException("Null native plan selector.");
            return Array.AsReadOnly(copy);
        }
    }
    public static class NativeMapStepSelection
    {
        public static bool Matches(NativeMapStepSelector selector, string biomeId, GenStepDef step)
        {
            if (selector.BiomeId != null && selector.BiomeId != biomeId) return false;
            if (selector.Names.Contains(step.defName, StringComparer.Ordinal) || selector.Prefixes.Any(prefix => step.defName?.StartsWith(prefix, StringComparison.Ordinal) == true)) return true;
            for (Type? type = step.genStep?.GetType(); type != null && typeof(GenStep).IsAssignableFrom(type); type = type.BaseType)
                if (type.FullName != null && selector.Types.Contains(type.FullName, StringComparer.Ordinal)) return true;
            return false;
        }
    }

    /// <summary>Exact-Map initial generation data. No layer caches or shared native definitions are changed.</summary>
    public sealed class NativeMapGenerationServices : INativeMapGenerationServices
    {
        public Map Map { get; }
        public MapSourceContext Source { get; }
        public IReadOnlyList<NativeMapGenerationStep> NativeSteps { get; }
        public InitialMapInputs? InitialOverrides { get; private set; }
        public NativeMapGenerationServices(Map map, MapSourceContext source, IEnumerable<NativeMapGenerationStep> nativeSteps)
        {
            Map = map ?? throw new ArgumentNullException(nameof(map)); Source = source ?? throw new ArgumentNullException(nameof(source));
            var copy = nativeSteps?.ToArray() ?? throw new ArgumentNullException(nameof(nativeSteps));
            if (copy.Any(step => step == null) || copy.Select(step => step.Token).Distinct(StringComparer.Ordinal).Count() != copy.Length)
                throw new ArgumentException("Duplicate/null native step token.");
            NativeSteps = Array.AsReadOnly(copy);
        }
        public void SetInitialOverrides(InitialMapInputs inputs)
        {
            ArgumentNullException.ThrowIfNull(inputs);
            if (InitialOverrides != null)
            {
                if (LocalMapBuiltinConfigurations.Encode(InitialOverrides).Span.SequenceEqual(LocalMapBuiltinConfigurations.Encode(inputs).Span)) return;
                throw new InvalidOperationException("Conflicting initial map inputs have already been supplied.");
            }
            InitialOverrides = inputs;
        }
        public void GenerateNative(string token, ulong seed) => InvokeNative(token, seed, false);
        public void PostInitializeNative(string token, ulong seed) => InvokeNative(token, seed, true);
        private void InvokeNative(string token, ulong seed, bool postInitialize)
        {
            var entry = NativeSteps.SingleOrDefault(step => step.Token == token) ?? throw new InvalidOperationException("Unknown native map step token: " + token);
            Rand.PushState(unchecked((int)(seed ^ (seed >> 32))));
            try
            {
                if (postInitialize) entry.Step.def.genStep.PostMapInitialized(Map, entry.Step.parms);
                else
                {
                    NativeLocalMapPlan.RequireNativeInputs(entry.Step.def.genStep);
                    entry.Step.def.genStep.Generate(Map, entry.Step.parms);
                }
            }
            finally { Rand.PopState(); }
        }
        public void ApplyTerrainReplacement(TerrainReplacementConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var target = DefDatabase<TerrainDef>.GetNamed(configuration.TargetTerrainId);
            foreach (var cell in Map.AllCells)
            {
                var terrain = Map.terrainGrid.TerrainAt(cell);
                if (terrain != null && configuration.ShouldReplace(Source.BiomeId, terrain.defName, terrain.IsRock)) Map.terrainGrid.SetTerrain(cell, target);
            }
        }
    }
}
