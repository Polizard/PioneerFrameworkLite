using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PioneerFramework.Celestial
{
    internal sealed record CelestialCommittedReadState(PhysicsSampleStamp Stamp, CelestialCatalogSnapshot Catalog,
        long FaultRevision, IReadOnlyDictionary<CelestialInstanceId, CelestialBodySnapshot> Bodies,
        IReadOnlyDictionary<CelestialInstanceId, CelestialBodyComp[]> Capabilities,
        IReadOnlyDictionary<CelestialInstanceId, IReadOnlyDictionary<string, CelestialInstanceId>> InitializationReferences,
        IReadOnlyList<CelestialEpochUsage> EpochUsage)
    {
        internal static CelestialCommittedReadState Capture(CelestialRuntime runtime) => new(runtime.CurrentStamp, runtime.CaptureCatalog(),
            runtime.Creation.FaultRevision, new ReadOnlyDictionary<CelestialInstanceId, CelestialBodySnapshot>(runtime.Catalog.CaptureInstances()
                .ToDictionary(instance => instance.Id, instance => runtime.Creation.Query(instance.Id).Value)),
            runtime.Catalog.CaptureInstances().ToDictionary(instance => instance.Id, instance => runtime.GetLiveCapabilities<CelestialBodyComp>(instance.Id).ToArray()),
            runtime.Catalog.CaptureInstances().ToDictionary(instance => instance.Id,
                instance => (IReadOnlyDictionary<string, CelestialInstanceId>)new ReadOnlyDictionary<string, CelestialInstanceId>(
                    runtime.Creation.GetInitializationReferences(instance.Id).ToDictionary(pair => pair.Key, pair => pair.Value, System.StringComparer.Ordinal))), runtime.Operations.Epochs.CaptureUsage());
        internal IReadOnlyList<T> GetCapabilities<T>(CelestialInstanceId id) where T : class => Capabilities.TryGetValue(id, out var components) ?
            System.Array.AsReadOnly(components.OfType<T>().ToArray()) : System.Array.Empty<T>();
        internal CelestialResult<CelestialBodySnapshot> Query(CelestialInstanceId id) => Bodies.TryGetValue(id, out var body) ?
            CelestialResult.Ok(body) : CelestialResult.Reject<CelestialBodySnapshot>(new CelestialError("BodyMissing", id));
    }
}
