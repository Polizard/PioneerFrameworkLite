using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.MapGeneration
{
    public enum MapGenerationState { Pending, Generated, Ready, Failed }
    public sealed record MapGenerationStepReport(string Key, MapGenerationStage Stage, string HandlerId,
        int ConfigurationVersion, int AlgorithmVersion, ulong Seed, bool IsOpaque, bool Completed,
        bool PostInitialized, TimeSpan Elapsed, long AllocatedBytes, string? Error = null, Exception? Exception = null);

    public sealed class MapGenerationReport
    {
        public MapGenerationState State { get; }
        public MapSourceContext Source { get; }
        public MapGenerationPurpose Purpose { get; }
        public IReadOnlyList<MapGenerationStepReport> Steps { get; }
        public IReadOnlyList<MapGenerationDiagnostic> Diagnostics { get; }
        internal MapGenerationReport(MapGenerationState state, MapSourceContext source, MapGenerationPurpose purpose,
            IEnumerable<MapGenerationStepReport> steps, IEnumerable<MapGenerationDiagnostic> diagnostics)
        {
            State = state; Source = source; Purpose = purpose;
            Steps = Array.AsReadOnly(steps.ToArray()); Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        }
    }
}
