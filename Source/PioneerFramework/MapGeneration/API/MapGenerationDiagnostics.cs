using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.MapGeneration
{
    public sealed record MapGenerationDiagnostic(string Code, string? StepKey = null, string? Detail = null, bool IsWarning = false);

    public sealed class MapGenerationCompilationResult
    {
        public MapGenerationPlan? Plan { get; }
        public IReadOnlyList<MapGenerationDiagnostic> Diagnostics { get; }
        public bool Success => Plan != null;
        internal MapGenerationCompilationResult(MapGenerationPlan? plan, IEnumerable<MapGenerationDiagnostic> diagnostics)
        {
            Plan = plan;
            Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        }
    }

    public sealed class CompiledMapGenerationStep
    {
        public MapGenerationStepSpec Spec { get; }
        public GenerationModuleRegistration Registration { get; }
        internal CompiledMapGenerationStep(MapGenerationStepSpec spec, GenerationModuleRegistration registration)
        { Spec = spec; Registration = registration; }
    }

    public sealed class MapGenerationPlan
    {
        public LocalMapGenerationSpec Specification { get; }
        public MapGenerationPurpose Purpose { get; }
        public IReadOnlyList<CompiledMapGenerationStep> Steps { get; }
        public IReadOnlyList<MapGenerationPortContract> ExternalInputs { get; }
        public IReadOnlyList<MapGenerationDiagnostic> Diagnostics { get; }
        internal MapGenerationPlan(LocalMapGenerationSpec specification, MapGenerationPurpose purpose,
            IEnumerable<CompiledMapGenerationStep> steps, IEnumerable<MapGenerationPortContract> externalInputs,
            IEnumerable<MapGenerationDiagnostic> diagnostics)
        {
            Specification = specification;
            Purpose = purpose;
            Steps = Array.AsReadOnly(steps.ToArray());
            ExternalInputs = Array.AsReadOnly(externalInputs.ToArray());
            Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        }
    }
}
