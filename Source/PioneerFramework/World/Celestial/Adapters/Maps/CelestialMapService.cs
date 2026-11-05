using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Celestial
{
    public interface ICelestialMapBackend
    {
        MapParent CreateParent(CelestialMapAddress address);
        Map Generate(MapParent parent, int sizeX, int sizeZ, string? generatorDefName);
        void RemoveEmptyParent(MapParent parent);
        CelestialResult<bool> CheckAccess(MapParent parent, CelestialMapAction action) => CelestialResult.Ok(true);
    }
    /// <summary>保存联合身份；每次访问重新核对原生对象，缺失时保留记录供诊断和恢复。</summary>
    public sealed class CelestialMapService : ICelestialRemovalResources
    {
        private readonly CelestialRuntime runtime;
        private readonly CelestialSurfaceService surfaces;
        private readonly World world;
        private readonly ICelestialMapBackend backend;
        private readonly CelestialMapSiteIndex sites = new();
        public CelestialMapService(CelestialRuntime runtime, CelestialSurfaceService surfaces, ICelestialMapBackend? backend = null)
        {
            this.runtime = runtime; this.surfaces = surfaces; world = Find.World;
            this.backend = backend ?? new NativeBackend();
            var result = runtime.AttachMaps(this);
            if (!result.Success) throw new InvalidOperationException(result.Errors[0].Code);
        }
        public CelestialSurfaceOperation RequestSurface(CelestialSurfaceRequest request) => runtime.RequestSurface(request);
        public IReadOnlyList<CelestialMapSiteRecord> CaptureSites() => runtime.WithPublication(sites.Capture);
        internal void CompactEpoch(long epoch) => sites.CompactEpoch(epoch);
        internal CelestialResult<bool> BindNativeMapsForSave() => runtime.WithPublication(() => {
            if (!ReferenceEquals(Find.World, world)) return CelestialResult.Reject<bool>(new CelestialError("WorldChanged"));
            foreach (var parent in world.worldObjects.MapParents.ToArray())
            {
                if (parent.Tile.Layer is not CelestialSurfaceLayer owned) continue;
                var address = new CelestialMapAddress(owned.Header.OwnerId, owned.LayerID, parent.Tile.tileId, parent.ID);
                var surface = surfaces.Find(address.InstanceId);
                if (!surface.Success || surface.Value.LayerId != address.LayerId)
                    return CelestialResult.Reject<bool>(new CelestialError("SurfaceIdentityMismatch", address.InstanceId));
                var map = parent.Map;
                // Keep a saved missing map's original ID. A native loss must never become permission to generate a replacement.
                if (map == null && sites.TryGet(address, out var known))
                {
                    if (known.ParentRetired || known.MapParentId != parent.ID) return CelestialResult.Reject<bool>(new CelestialError("MapIdentityMismatch", address.InstanceId));
                    continue;
                }
                var bound = map == null ? sites.Bind(new(address, parent.ID, null)) : BindExistingCore(address, parent, map);
                if (!bound.Success) return bound;
            }
            return CelestialResult.Ok(true);
        });
        public CelestialResult<bool> RestoreSite(CelestialMapSiteRecord record) => runtime.WithPublication(() => sites.Bind(record));
        public CelestialMapOperation RequestMap(CelestialMapRequest request)
        {
            var result = runtime.WithPublication(() => RequestCore(request));
            if (result.Operation.Status != CelestialOperationStatus.Pending || runtime.IsPublicationHeld || runtime.IsExecutingCallbacks || !runtime.HostWork.IsHostThread) return result;
            runtime.ProcessHostWork();
            return runtime.WithPublication(() => RequestCore(request));
        }
        private CelestialMapOperation RequestCore(CelestialMapRequest request)
        {
            var address = request.Address;
            CelestialMapOperation Reject(string code) => new(new CelestialOperation(request.Context.OperationId, CelestialOperationStatus.Rejected,
                errors: new[] { new CelestialError(code, address.InstanceId) }));
            if (request.Context.WorldSessionId != runtime.WorldSessionId) return Reject("StaleSession");
            if (!ReferenceEquals(Find.World, world)) return Reject("WorldChanged");
            if (!Enum.IsDefined(request.Action)) return Reject("InvalidMapAction");
            if (request.SizeX < 50 || request.SizeX > 500 || request.SizeZ < 50 || request.SizeZ > 500) return Reject("InvalidMapSize");
            var ticket = runtime.Operations.Begin(request.Context,
                FormattableString.Invariant($"PFL.Map.v1:{address.InstanceId.Value.Length}:{address.InstanceId.Value}:{address.LayerId}:{address.TileId}:{address.MapParentId}:{request.Action}:{request.SizeX}:{request.SizeZ}"),
                runtime.Catalog.Revision, new[] { address.InstanceId });
            if (!ticket.Success) return new(new CelestialOperation(request.Context.OperationId, CelestialOperationStatus.Rejected, errors: ticket.Errors));
            CelestialMapOperation Fail(string code, string? detail = null)
            {
                var operation = new CelestialOperation(request.Context.OperationId, CelestialOperationStatus.Rejected, new[] { address.InstanceId },
                    new[] { new CelestialError(code, address.InstanceId, Detail: detail ?? "") });
                if (ticket.Value.IsNew) runtime.WithPublication(() => { runtime.Operations.Update(operation); return true; });
                return new(operation);
            }
            if (!ticket.Value.IsNew && ticket.Value.Operation.Status != CelestialOperationStatus.Applied) return new(ticket.Value.Operation);
            var body = runtime.Query(address.InstanceId);
            if (!body.Success || body.Value.Lifecycle != CelestialLifecycle.Ready) return Fail("BodyNotReady");
            var found = surfaces.Find(address.InstanceId);
            if (!found.Success || found.Value.LayerId != address.LayerId || !world.grid.PlanetLayers.TryGetValue(address.LayerId, out var layer) ||
                address.TileId < 0 || address.TileId >= layer.TilesCount) return Fail("SurfaceIdentityMismatch");
            var tile = new PlanetTile(address.TileId, layer);
            ICelestialMapGenerationCapability? requiredCapability = null;
            var parent = world.worldObjects.MapParentAt(tile);
            sites.TryGet(address, out var known);
            if (known?.RetiredOperationIds.Contains(request.Context.OperationId, StringComparer.Ordinal) == true) return Fail("MapIdentityMismatch");
            if (known != null && !known.ParentRetired && (parent == null || parent.ID != known.MapParentId || known.MapId.HasValue && parent.Map?.uniqueID != known.MapId))
                return Fail("MapIdentityMismatch");
            if (known is { ParentRetired: false, Failure: not null }) return Fail("MapGenerationIncomplete", known.Failure);
            if (address.MapParentId.HasValue && (parent == null || parent.ID != address.MapParentId)) return Fail("MapIdentityMismatch");
            if (parent != null)
            {
                var access = backend.CheckAccess(parent, request.Action);
                if (!access.Success || !access.Value) return Fail(access.Errors.Count > 0 ? access.Errors[0].Code : "MapAccessDenied");
                if (parent.Map != null) return Apply(parent, parent.Map);
                var bind = sites.Bind(new(address, parent.ID, null));
                if (!bind.Success) return Fail("MapIdentityMismatch");
            }
            if (!ticket.Value.IsNew) return Fail("MapIdentityMismatch");
            if (request.Action == CelestialMapAction.Inspect) return Fail("MapNotCreated");
            var capabilities = runtime.GetCapabilities<ICelestialMapGenerationCapability>(address.InstanceId);
            if (capabilities.Count != 1) return Fail("MapGenerationCapabilityMissing");
            requiredCapability = capabilities[0];
            CelestialMapProfile profile;
            try { profile = requiredCapability.GetProfile() ?? throw new InvalidOperationException("Missing map profile."); }
            catch (Exception exception)
            {
                if (requiredCapability is CelestialBodyComp component && runtime.Catalog.TryGetInstance(address.InstanceId, out var owner))
                    runtime.Creation.HandleComponentFault(owner, component, "ComponentCapabilityFailed", exception);
                return Fail("MapGenerationCapabilityFailed", exception.Message);
            }
            bool CapabilityIsEffective() => runtime.GetCapabilities<ICelestialMapGenerationCapability>(address.InstanceId).Any(capability => ReferenceEquals(capability, requiredCapability));
            bool SurfaceIsCurrent() => surfaces.Find(address.InstanceId) is { Success: true } current && current.Value.LayerId == address.LayerId &&
                world.grid.PlanetLayers.TryGetValue(address.LayerId, out var currentLayer) && ReferenceEquals(currentLayer, layer);
            return new(runtime.HostWork.Enqueue(request.Context.OperationId, address.InstanceId, body.Value.Description.DescriptorRevision, () => Generate().Operation));
            CelestialMapOperation Generate()
            {
            if (!runtime.WithPublication(SurfaceIsCurrent)) return Fail("SurfaceIdentityMismatch");
            if (!runtime.WithPublication(CapabilityIsEffective)) return Fail("MapGenerationCapabilityMissing");
            // Other queued requests may have completed at this address since this job was prepared.
            var currentParent = world.worldObjects.MapParentAt(tile);
            if (currentParent != null && (currentParent.Map != null || !ReferenceEquals(currentParent, parent)))
            {
                var reused = runtime.WithPublication(() => {
                    if (!ReferenceEquals(currentParent, parent) && (parent != null || known is { ParentRetired: false }) ||
                        address.MapParentId.HasValue && address.MapParentId != currentParent.ID)
                        return Fail("MapIdentityMismatch");
                    if (!sites.TryGet(address, out var currentSite) || currentSite.ParentRetired || currentSite.MapParentId != currentParent.ID)
                        return Fail("MapIdentityMismatch");
                    if (currentSite.Failure != null) return Fail("MapGenerationIncomplete", currentSite.Failure);
                    var access = backend.CheckAccess(currentParent, request.Action);
                    if (!access.Success || !access.Value) return Fail(access.Errors.Count > 0 ? access.Errors[0].Code : "MapAccessDenied");
                    if (currentParent.Map == null || currentSite.MapId != currentParent.Map.uniqueID) return Fail("MapIdentityMismatch");
                    return Apply(currentParent, currentParent.Map);
                });
                return reused;
            }
            MapParent? created = null;
            Rand.PushState();
            try
            {
                if (parent == null) parent = created = backend.CreateParent(address);
                if (parent == null || parent.Tile != tile || !world.worldObjects.MapParents.Contains(parent)) throw new InvalidOperationException("Map parent was not registered at the requested address.");
                var map = backend.Generate(parent, request.SizeX, request.SizeZ, profile.MapGeneratorId);
                if (map == null || !ReferenceEquals(map.Parent, parent) || !ReferenceEquals(parent.Map, map) || !Current.Game.Maps.Contains(map))
                    throw new InvalidOperationException("Map generator returned a different identity.");
                return runtime.WithPublication(() => Apply(parent, map));
            }
            catch (Exception exception)
            {
                if (parent?.Map != null) sites.Bind(new(address, parent.ID, parent.Map.uniqueID, exception.Message));
                if (created != null && !created.HasMap && world.worldObjects.MapParents.Contains(created))
                {
                    try { backend.RemoveEmptyParent(created); }
                    catch (Exception cleanup) { sites.Bind(new(address, created.ID, null)); return Fail("MapCleanupFailed", cleanup.Message); }
                }
                return Fail("MapGenerationFailed", exception.Message);
            }
            finally { Rand.PopState(); }

            }

            CelestialMapOperation Apply(MapParent actualParent, Map map)
            {
                if (!runtime.IsHostJobCurrent(request.Context.WorldSessionId, address.InstanceId, body.Value.Description.DescriptorRevision)) return Fail("HostWorkIdentityChanged");
                if (requiredCapability != null && !SurfaceIsCurrent())
                { sites.Bind(new(address, actualParent.ID, map.uniqueID, "SurfaceIdentityMismatch")); return Fail("SurfaceIdentityMismatch"); }
                if (requiredCapability != null && !CapabilityIsEffective())
                { sites.Bind(new(address, actualParent.ID, map.uniqueID, "MapGenerationCapabilityMissing")); return Fail("MapGenerationCapabilityMissing"); }
                var bound = BindExistingCore(address, actualParent, map);
                if (!bound.Success) return Fail("MapIdentityMismatch");
                var recorded = sites.RecordOperation(address, request.Context.OperationId);
                if (!recorded.Success) return Fail("MapIdentityMismatch");
                var operation = new CelestialOperation(request.Context.OperationId, CelestialOperationStatus.Applied, new[] { address.InstanceId });
                if (ticket.Value.IsNew) runtime.WithPublication(() => { runtime.Operations.Update(operation); return true; });
                return new(operation, new(address with { MapParentId = actualParent.ID }, actualParent.ID, map.uniqueID));
            }
        }
        public bool TryGetNativeMap(int mapId, out Map map)
        {
            Map? result = runtime.WithPublication(() => {
                if (!ReferenceEquals(Find.World, world)) return null;
                var site = sites.Capture().SingleOrDefault(site => !site.ParentRetired && site.MapId == mapId);
                if (site == null || !world.grid.PlanetLayers.TryGetValue(site.Address.LayerId, out var layer)) return null;
                var parent = world.worldObjects.MapParentAt(new PlanetTile(site.Address.TileId, layer));
                return parent?.ID == site.MapParentId && parent.Map?.uniqueID == mapId ? parent.Map : null;
            });
            map = result!; return result != null;
        }
        public CelestialResult<bool> BindExisting(CelestialMapAddress address, MapParent parent, Map map)
            => runtime.WithPublication(() => BindExistingCore(address, parent, map));
        private CelestialResult<bool> BindExistingCore(CelestialMapAddress address, MapParent parent, Map map)
        {
            var surface = surfaces.Find(address.InstanceId);
            if (!ReferenceEquals(Find.World, world) || !surface.Success || surface.Value.LayerId != address.LayerId || parent.Tile.Layer?.LayerID != address.LayerId ||
                parent.Tile.tileId != address.TileId || !world.worldObjects.MapParents.Contains(parent) ||
                !ReferenceEquals(world.worldObjects.MapParentAt(parent.Tile), parent) || !ReferenceEquals(map.Parent, parent) ||
                !ReferenceEquals(parent.Map, map) || !Current.Game.Maps.Contains(map))
                return CelestialResult.Reject<bool>(new CelestialError("MapIdentityMismatch", address.InstanceId));
            return sites.Bind(new(address, parent.ID, map.uniqueID));
        }
        public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id) => runtime.WithPublication(() => {
            var errors = new List<CelestialError>();
            foreach (var site in sites.Capture().Where(site => !site.ParentRetired && site.Address.InstanceId == id))
            {
                var parent = world.worldObjects.MapParents.SingleOrDefault(parent => parent.ID == site.MapParentId);
                bool exact = ReferenceEquals(Find.World, world) && parent != null && parent.Tile.Layer?.LayerID == site.Address.LayerId &&
                    parent.Tile.tileId == site.Address.TileId && (!site.MapId.HasValue || parent.Map?.uniqueID == site.MapId);
                errors.Add(new CelestialError(!exact ? "MapIdentityMismatch" : site.MapId.HasValue ? "MapExists" : "WorldObjectExists", id));
            }
            return (IReadOnlyList<CelestialError>)errors;
        });
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id) => sites.Capture().Any(site => !site.ParentRetired && site.Address.InstanceId == id) ?
            CelestialResult.Reject<bool>(new CelestialError("MapIdentityMismatch", id)) : CelestialResult.Ok(true);
        internal CelestialResult<bool> RetireNativeMap(CelestialMapAddress address, int mapId)
            => runtime.WithPublication(() => sites.RetireMap(address, mapId));
        internal CelestialResult<bool> RetireNativeParent(CelestialMapAddress address)
            => runtime.WithPublication(() => sites.RetireParent(address));
        private sealed class NativeBackend : ICelestialMapBackend
        {
            public CelestialResult<bool> CheckAccess(MapParent parent, CelestialMapAction action) => action == CelestialMapAction.Inspect ||
                parent is Settlement && parent.Faction == Faction.OfPlayer ||
                action == CelestialMapAction.Land && parent is Landing.LandedShipMapParent site && parent.Faction == Faction.OfPlayer && (site.HasMap || !site.EverGenerated) ? CelestialResult.Ok(true) :
                CelestialResult.Reject<bool>(new CelestialError("MapAccessDenied"));
            public MapParent CreateParent(CelestialMapAddress address)
            {
                if (Current.ProgramState != ProgramState.Playing || Scribe.mode != LoadSaveMode.Inactive) throw new InvalidOperationException("Map generation requires an active game.");
                var tile = new PlanetTile(address.TileId, Find.WorldGrid.PlanetLayers[address.LayerId]);
                if (!TileFinder.IsValidTileForNewSettlement(tile)) throw new InvalidOperationException("Invalid settlement tile.");
                var parent = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
                parent.Tile = tile; parent.SetFaction(Faction.OfPlayer);
                try { Find.WorldObjects.Add(parent); }
                catch { if (parent.Spawned && !parent.HasMap) parent.Destroy(); throw; }
                return parent;
            }
            public Map Generate(MapParent parent, int sizeX, int sizeZ, string? generatorDefName)
            {
                if (Current.ProgramState != ProgramState.Playing || Scribe.mode != LoadSaveMode.Inactive) throw new InvalidOperationException("Map generation requires an active game.");
                var generator = generatorDefName == null ? parent.MapGeneratorDef : DefDatabase<MapGeneratorDef>.GetNamed(generatorDefName);
                return MapGenerator.GenerateMap(new(sizeX, 1, sizeZ), parent, generator, parent.ExtraGenStepDefs);
            }
            public void RemoveEmptyParent(MapParent parent) { if (!parent.HasMap && parent.Spawned) parent.Destroy(); }
        }
    }
}
