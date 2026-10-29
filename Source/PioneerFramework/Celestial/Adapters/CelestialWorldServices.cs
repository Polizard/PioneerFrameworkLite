using System;
using Verse;

namespace PioneerFramework.Celestial
{
    /// <summary>World-scoped business entry point. Retaining this object never switches it to a new game.</summary>
    public sealed class CelestialWorldServices
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CelestialRuntime, CelestialWorldServices> instances = new();
        public ICelestialQueryService Query { get; }
        public ICelestialBodyService Bodies { get; }
        internal CelestialWorldServices(CelestialRuntime runtime) { Query = runtime; Bodies = new BodyService(runtime); }
        public static CelestialWorldServices? TryGetCurrent()
            => Current.Game?.GetComponent<CelestialBodyManager>()?.TryGetCelestialRuntime(out var runtime) == true ? instances.GetValue(runtime, value => new(value)) : null;
        private sealed class BodyService(CelestialRuntime runtime) : ICelestialBodyService
        {
            public CelestialOperation Create(CelestialCreateRequest request) => runtime.CreateData(new(request.Scope,
                new[] { new CelestialCreateItem(request.GenerationKey, request.DefinitionId, request.Seed, request.ParentId, request.InitializationReferences, request.ParameterOverrides) }));
            public CelestialOperation CreateBatch(CelestialCreateBatchRequest request) => runtime.CreateData(request);
            public CelestialOperation GetOperation(string id) => runtime.GetOperation(id);
            public CelestialResult<long> OpenEpoch(Guid session) => runtime.OpenEpoch(session);
            public CelestialResult<bool> CloseEpoch(Guid session, long epoch) => runtime.CloseEpoch(session, epoch);
            public CelestialResult<bool> DiscardFailedCreation(Guid session, CelestialInstanceId id) => runtime.DiscardFailedCreation(session, id);
            public CelestialResult<CelestialBodySnapshot> Query(CelestialInstanceId id) => runtime.Query(id);
            public CelestialRemovalCheck CanRemove(CelestialInstanceId id) => runtime.CanRemove(id);
            public CelestialOperation Remove(RemoveBodyRequest request) => runtime.Remove(request);
            public CelestialResult<string> RegisterReference(CelestialReference reference) => runtime.RegisterReference(reference);
            public CelestialResult<bool> ReleaseReference(string id) => runtime.ReleaseReference(id);
            public CelestialSurfaceOperation RequestSurface(CelestialSurfaceRequest request) => runtime.RequestSurface(request);
            public CelestialMapOperation RequestMap(CelestialMapRequest request) => runtime.RequestMap(request);
            public CelestialResult<bool> EnqueueEvent(Guid session, CelestialInstanceId id, CelestialEvent message, string? compKey = null)
                => runtime.EnqueueEvent(session, id, message, compKey);
            public CelestialResult<string> Submit(CelestialCommand command) => runtime.Submit(command);
        }
    }
}
