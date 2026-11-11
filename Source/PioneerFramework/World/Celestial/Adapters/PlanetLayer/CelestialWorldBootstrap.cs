using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Celestial
{
    public static class CelestialWorldBootstrap
    {
        public const string DefaultSystem = "PF_DefaultCelestialSystem";
        public static CelestialWorldBootstrapScope? Current => CelestialWorldGenSessionRegistry.Current;
        public static CelestialResult<CelestialWorldBootstrapScope> Begin(string systemDefName, ulong seed)
        {
            var definition = DefDatabase<CelestialSystemDef>.GetNamedSilentFail(systemDefName);
            if (definition == null) return Reject("SystemDefinitionMissing", systemDefName);
            var template = definition.ToTemplate();
            if (!template.Success) return CelestialResult.Reject<CelestialWorldBootstrapScope>(template.Errors.ToArray());
            return Begin(template.Value, seed, request => {
                var ids = request.Manifest.Items.SelectMany(item => item.Components).Select(component => component.Properties)
                    .OfType<ICelestialPhysicsBindingProperties>().Select(properties => properties.Initialization.ProviderId).Distinct(StringComparer.Ordinal).ToArray();
                return ids.Length == 1 ? CelestialPhysicsProviderRegistry.Global.Connect(ids[0], request) :
                    CelestialResult.Reject<CelestialPhysicsConnection>(new CelestialError(ids.Length == 0 ? "PhysicsProviderMissing" : "MultiplePhysicsProviders"));
            });
        }
        internal static CelestialResult<CelestialWorldBootstrapScope> Begin(CelestialSystemTemplate system, ulong seed,
            Func<CelestialPhysicsWorldRequest, CelestialResult<CelestialPhysicsConnection>> connect, ICelestialMapBackend? mapBackend = null)
        {
            var game = Verse.Current.Game; var world = Verse.Current.CreatingWorld ?? game?.World;
            if (game == null || world?.grid == null) return Reject("NativeWorldUnavailable");
            if (Current != null) return Reject("BootstrapAlreadyActive");
            var compatible = CelestialNativeCompatibility.ValidateNativeContracts();
            if (!compatible.Success) return CelestialResult.Reject<CelestialWorldBootstrapScope>(compatible.Errors.ToArray());
            CelestialWorldBootstrapHooks.InstallConstruction();
            CelestialNativeCompatibility.RequireInstalledHooks("WorldGrid.RegisterPlanetLayer.prefix.BeginConstruction", "WorldGrid.RegisterPlanetLayer.finalizer.EndConstruction",
                "WorldGrid.RegisterPlanetLayer.Settings.prefix.BeginRootConstruction", "WorldGrid.RegisterPlanetLayer.Settings.finalizer.EndConstruction");
            var expanded = CelestialSystemGenerator.Expand(system, seed);
            if (!expanded.Success) return CelestialResult.Reject<CelestialWorldBootstrapScope>(expanded.Errors.ToArray());
            var selectedRoot = CelestialNativeCompatibility.ResolveRootDefinition(Find.Scenario);
            if (!selectedRoot.Success) return CelestialResult.Reject<CelestialWorldBootstrapScope>(selectedRoot.Errors.ToArray());
            var rootDefinition = selectedRoot.Value;
            if (rootDefinition == null || !typeof(CelestialSurfaceLayer).IsAssignableFrom(rootDefinition.layerType)) return Reject("RootLayerAdapterMissing");
            var genericDefinition = DefDatabase<PlanetLayerDef>.GetNamedSilentFail("PF_CelestialSurface");
            if (genericDefinition == null) return Reject("GenericSurfaceDefinitionMissing");
            var worldRequest = new CelestialPhysicsWorldRequest(Guid.NewGuid(), seed, expanded.Value);
            CelestialResult<CelestialPhysicsConnection> connection;
            try { connection = connect(worldRequest); }
            catch (Exception exception) { return Reject("ProviderInitializationFailed", exception.Message); }
            if (!connection.Success) return CelestialResult.Reject<CelestialWorldBootstrapScope>(connection.Errors.ToArray());
            if (connection.Value.InitialStamp.WorldSessionId != worldRequest.WorldSessionId || connection.Value.InitialStamp.PhysicalSeconds != 0 || connection.Value.InitialStamp.TopologyRevision != 0)
                return Reject("ProviderContractViolation");
            CelestialRuntime? runtime = null;
            CelestialBodyManager? manager = null;
            try
            {
                runtime = new CelestialRuntime(connection.Value.Bridge, connection.Value.InitialStamp, new NativeResources(), seed, name => {
                    var definition = DefDatabase<CelestialBodyDef>.GetNamedSilentFail(name);
                    return definition?.ToTemplate() ?? CelestialResult.Reject<CelestialBodyTemplate>(new CelestialError("DefinitionMissing", Detail: name)); });
                var surfaces = new CelestialSurfaceService(runtime, genericDefinition);
                _ = new CelestialMapService(runtime, surfaces, mapBackend);
                var operation = runtime.CreateBatch(new CreateBatchRequest(new("bootstrap/" + system.DefinitionId, runtime.WorldSessionId, 0), expanded.Value));
                if (operation.Status == CelestialOperationStatus.Rejected) return CelestialResult.Reject<CelestialWorldBootstrapScope>(operation.Errors.ToArray());
                manager = Verse.Current.Game?.GetComponent<CelestialBodyManager>();
                if (manager == null) throw new InvalidOperationException("CelestialBodyManager missing.");
                var attached = manager.AttachCelestialRuntime(runtime);
                if (!attached.Success) throw new InvalidOperationException(attached.Errors[0].Code);
                var clock = Verse.Current.Game?.GetComponent<TimeScaleManager>();
                if (clock != null)
                {
                    var anchored = clock.InitializeCelestialWorldClock(runtime.CurrentStamp.PhysicalSeconds);
                    if (!anchored.Success) throw new InvalidOperationException(anchored.Errors[0].Code);
                }
                var scope = new CelestialWorldBootstrapScope(expanded.Value, runtime, surfaces, rootDefinition, manager, operation.OperationId, game, world);
                CelestialWorldGenSessionRegistry.Register(scope);
                return CelestialResult.Ok(scope);
            }
            catch (Exception exception)
            {
                if (runtime != null) manager?.DetachCelestialRuntime(runtime);
                runtime?.AbandonBootstrap();
                return Reject("BootstrapInitializationFailed", exception.Message);
            }
        }
        internal static void End(CelestialWorldBootstrapScope scope)
        {
            CelestialWorldGenSessionRegistry.Remove(scope);
        }
        internal static float RadiusFor(PlanetLayerDef definition, float fallback)
            => CelestialSurfaceCreationScope.CurrentHeader?.NativeRadius ?? fallback;
        internal static int SubdivisionsFor(PlanetLayerDef definition, int fallback)
            => CelestialSurfaceCreationScope.CurrentHeader?.Subdivisions ?? fallback;
        internal static CelestialSurfaceHeader HeaderFor(PlanetLayerDef definition, int layerId)
        {
            if (CelestialSurfaceCreationScope.CurrentHeader != null) return CelestialSurfaceCreationScope.Bind(layerId);
            throw new InvalidOperationException("Missing celestial surface construction header.");
        }
        private static CelestialResult<CelestialWorldBootstrapScope> Reject(string code, string detail = "")
            => CelestialResult.Reject<CelestialWorldBootstrapScope>(new CelestialError(code, Detail: detail));
        private sealed class NativeResources : ICelestialRemovalResources
        {
            public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId instanceId) => Array.Empty<CelestialError>();
            public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId instanceId) => CelestialResult.Ok(true);
        }
    }

    public sealed class CelestialWorldBootstrapScope : IDisposable
    {
        private readonly CelestialSurfaceService surfaces;
        private readonly PlanetLayerDef rootDefinition;
        private readonly CelestialBodyManager manager;
        private readonly string operationId;
        private CelestialSurfaceLayer? root;
        private int? constructedRootId;
        private bool completed;
        private bool disposed;
        internal Game OwnerGame { get; }
        internal World OwnerWorld { get; }
        internal WorldGrid Grid { get; }
        internal readonly List<PlanetLayer> RegisteredRoots = new();
        internal bool ManagedByNativeHooks { get; set; }
        internal bool IsCompleted => completed;
        public CelestialSpawnManifest Manifest { get; }
        public CelestialRuntime Runtime { get; }
        internal CelestialSurfaceHeader RootHeader { get; }
        internal CelestialSurfaceHeader? ConstructedRootHeader { get; private set; }
        public int RootBindingCount { get; private set; }
        internal CelestialWorldBootstrapScope(CelestialSpawnManifest manifest, CelestialRuntime runtime, CelestialSurfaceService surfaces,
            PlanetLayerDef rootDefinition, CelestialBodyManager manager, string operationId, Game game, World world)
        {
            Manifest = manifest;
            Runtime = runtime;
            this.surfaces = surfaces;
            this.rootDefinition = rootDefinition;
            this.manager = manager;
            this.operationId = operationId;
            OwnerGame = game; OwnerWorld = world; Grid = world.grid;
            var id = manifest.RootSurfaceId!.Value;
            runtime.Catalog.TryGetInstance(id, out var instance);
            var properties = runtime.Creation.GetProperties(id).OfType<SurfaceResolvedProperties>().Single();
            RootHeader = CelestialSurfaceHeader.Create(instance, properties);
        }
        internal bool MatchesRoot(PlanetLayerDef definition) => !constructedRootId.HasValue && ReferenceEquals(rootDefinition, definition);
        internal CelestialSurfaceHeader ConstructRootHeader(int layerId)
        {
            if (constructedRootId.HasValue) throw new InvalidOperationException("Root owner is already reserved.");
            constructedRootId = layerId;
            ConstructedRootHeader = RootHeader.WithLayerId(layerId);
            return ConstructedRootHeader;
        }
        public CelestialResult<bool> BindRoot(SurfaceLayer layer)
        {
            if (disposed) return Reject("BootstrapDisposed");
            if (layer is not CelestialSurfaceLayer surface || surface.Header.OwnerId != Manifest.RootSurfaceId ||
                surface.LayerID != constructedRootId || !ReferenceEquals(surface.Def, rootDefinition)) return Reject("RootIdentityMismatch");
            if (root != null && !ReferenceEquals(root, surface)) return Reject("RootAlreadyBound");
            if (root == null) { root = surface; RootBindingCount++; }
            return surfaces.RestoreHeader(surface.Header);
        }
        public CelestialResult<bool> Complete()
        {
            if (disposed) return Reject("BootstrapDisposed");
            if (completed) return CelestialResult.Ok(true);
            if (root == null)
            {
                if (Find.WorldGrid.Surface == null) return Reject("RootNotBound");
                var bound = BindRoot(Find.WorldGrid.Surface);
                if (!bound.Success) return bound;
            }
            if (!root!.IsRootSurface || root.Header.State != CelestialSurfaceState.Ready || root.Tiles.Count != root.TilesCount) return Reject("RootGenerationIncomplete");
            var advanced = Runtime.Advance(new PhysicsIntervalRequest(Runtime.WorldSessionId, Runtime.CurrentStamp.TopologyRevision, Runtime.CurrentStamp.PhysicalSeconds, Runtime.CurrentStamp.PhysicalSeconds));
            if (!advanced.Success) return advanced;
            var receipt = Runtime.GetOperation(operationId);
            if (receipt.Status != CelestialOperationStatus.Applied)
                return receipt.Errors.Count > 0 ? CelestialResult.Reject<bool>(receipt.Errors.ToArray()) : Reject("BootstrapRegistrationPending");
            foreach (var item in Manifest.Items.Where(item => item.Id != Manifest.RootSurfaceId && item.Components.Select(component => component.Properties)
                .OfType<SurfaceResolvedProperties>().Any(properties => properties.GenerationMode == CelestialSurfaceGenerationMode.Creation)))
            {
                var surface = surfaces.Find(item.Id);
                if (!surface.Success)
                {
                    var generation = Runtime.GetOperation("surface/creation/" + item.Id.Value + "/surface");
                    return generation.Errors.Count > 0 ? CelestialResult.Reject<bool>(generation.Errors.ToArray()) :
                        CelestialResult.Reject<bool>(new CelestialError("CreationSurfaceIncomplete", item.Id));
                }
            }
            completed = true;
            return CelestialResult.Ok(true);
        }
        public void Dispose()
        {
            if (disposed) return;
            if (!completed)
            {
                manager.DetachCelestialRuntime(Runtime);
                Runtime.AbandonBootstrap();
            }
            CelestialWorldBootstrap.End(this);
            disposed = true;
        }
        private static CelestialResult<bool> Reject(string code) => CelestialResult.Reject<bool>(new CelestialError(code));
    }
}
