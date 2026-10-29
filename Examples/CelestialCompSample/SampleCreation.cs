using System;
using System.Collections.Generic;
using PioneerFramework.Celestial;

namespace PFL.CelestialCompSample;

/// <summary>A data-only dynamic creation example. The caller owns the epoch and the explicit parent/light selection.</summary>
public static class SampleCreation
{
    public static CelestialResult<string> SubmitComponentAction(ICelestialCompContext context, long actionEpoch, string operationId, string registeredCommandKind)
        => context.SubmitNewAction(actionEpoch, new(registeredCommandKind, operationId, context.InstanceId, context.WorldSessionId, context.DescriptorRevision));
    public static CelestialOperation CreateProbe(string operationId, long epoch, CelestialInstanceId parent, CelestialInstanceId light, double? radiusMeters = null)
    {
        var services = CelestialWorldServices.TryGetCurrent() ?? throw new InvalidOperationException("Celestial services unavailable.");
        var query = services.Query;
        var scope = new CelestialOperationScope(operationId, query.WorldSessionId, query.CaptureCatalog().Revision, epoch);
        return services.Bodies.Create(new(scope, "PF_CounterSampleFixed", "probe", ParentId: parent,
            InitializationReferences: new Dictionary<string, CelestialInstanceId> { ["frameOrigin"] = parent, ["primaryLight"] = light },
            ParameterOverrides: radiusMeters.HasValue ? new[] { new CelestialParameterOverride("shape", "radiusMeters", Value: radiusMeters.Value) } : null));
    }
}
