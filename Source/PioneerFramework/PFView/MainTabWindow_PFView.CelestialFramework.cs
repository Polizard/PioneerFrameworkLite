using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView
    {
        private CelestialRuntime? celestialRenderRuntime;
        private CelestialViewAdapter? celestialRenderAdapter;
        private PhysicsSampleStamp? celestialRenderStamp;
        private long celestialRenderCatalog = -1;
        private long celestialRenderSurfaces = -1;
        private double celestialRenderTime = double.NaN;
        private PhysicsBridgeStatus celestialRenderStatus;
        private readonly Dictionary<int, CelestialViewSnapshot> celestialRenderSnapshots = new();
        private readonly Dictionary<int, CelestialViewCacheKey> celestialRenderKeys = new();
        private readonly Dictionary<int, CelestialVisualDescriptorCache.Entry> celestialRenderConfigurations = new();
        private readonly Dictionary<CelestialInstanceId, int> celestialRenderHandles = new();
        private readonly Dictionary<int, CelestialBodyInfo> celestialRenderProjections = new();
        private readonly List<CelestialBodyInfo> celestialRenderBodies = new();

        private IReadOnlyList<CelestialBodyInfo> GetCelestialRenderBodies(CelestialBodyManager manager, double tick)
        {
            if (!manager.TryGetCelestialRuntime(out var runtime))
            {
                if (celestialRenderRuntime != null)
                {
                    DestroySurfaceResources(); DestroyCloudResources(); DestroyStellarVisualResources(); DestroyAtmosphereShellMaterials();
                    celestialRenderRuntime = null; celestialRenderAdapter = null; celestialRenderStamp = null;
                    celestialRenderSnapshots.Clear(); celestialRenderKeys.Clear(); celestialRenderProjections.Clear(); celestialRenderBodies.Clear();
                    celestialRenderConfigurations.Clear(); celestialRenderHandles.Clear();
                }
                return manager.HasCelestialState ? Array.Empty<CelestialBodyInfo>() : manager.GetAllBodies();
            }
            bool changedWorld = !ReferenceEquals(celestialRenderRuntime, runtime);
            if (changedWorld)
            {
                DestroySurfaceResources(); DestroyCloudResources(); DestroyStellarVisualResources(); DestroyAtmosphereShellMaterials();
                celestialRenderRuntime = runtime; celestialRenderAdapter = new(runtime); celestialRenderStamp = null;
                celestialRenderKeys.Clear(); celestialRenderProjections.Clear();
                celestialRenderConfigurations.Clear(); celestialRenderHandles.Clear();
            }
            var stamp = runtime.CurrentStamp;
            long surfaces = runtime.Surfaces?.ResourceRevision ?? 0;
            if (celestialRenderStamp == stamp && celestialRenderTime == tick && celestialRenderCatalog == runtime.Catalog.Revision &&
                celestialRenderSurfaces == surfaces && celestialRenderStatus == runtime.Physics.Status && celestialRenderAdapter!.IsConfigurationCurrent()) return celestialRenderBodies;
            bool changedCatalog = changedWorld || celestialRenderCatalog != runtime.Catalog.Revision;
            celestialRenderStamp = stamp; celestialRenderTime = tick; celestialRenderCatalog = runtime.Catalog.Revision;
            celestialRenderSurfaces = surfaces; celestialRenderStatus = runtime.Physics.Status;
            celestialRenderSnapshots.Clear(); celestialRenderBodies.Clear();
            bool changedConfiguration = false;
            var catalog = runtime.CaptureCatalog();
            if (changedCatalog)
            {
                celestialRenderHandles.Clear();
                foreach (var description in catalog.Bodies) celestialRenderHandles.Add(description.Id, description.Handle);
                var handles = catalog.Bodies.Select(description => description.Handle).ToHashSet();
                foreach (int retired in celestialRenderConfigurations.Keys.Where(handle => !handles.Contains(handle)).ToArray())
                {
                    celestialRenderKeys.Remove(retired); celestialRenderProjections.Remove(retired); celestialRenderConfigurations.Remove(retired);
                    PFViewSurfaceBake.ForgetMemory(retired); changedConfiguration = true;
                }
            }
            var presentation = celestialRenderAdapter!.CapturePresentationBatch(catalog.Bodies.Select(body => body.Id), stamp, tick);
            for (int index = 0; index < catalog.Bodies.Count; index++)
            {
                var description = catalog.Bodies[index];
                var captured = presentation[index];
                if (!captured.Success) continue;
                var snapshot = captured.Value;
                celestialRenderSnapshots.Add(description.Handle, snapshot);
                if (!celestialRenderAdapter.TryGetConfiguration(snapshot, out var configuration)) continue;
                bool changedEntry = !celestialRenderConfigurations.TryGetValue(description.Handle, out var previous) || !ReferenceEquals(previous, configuration);
                celestialRenderConfigurations[description.Handle] = configuration;
                changedConfiguration |= changedEntry && previous != null;
                if (snapshot.Visuals.Count == 0) continue;
                if (!celestialRenderKeys.TryGetValue(description.Handle, out var key) || key != snapshot.CacheKey || changedEntry)
                {
                    changedConfiguration |= key != null;
                    celestialRenderKeys[description.Handle] = snapshot.CacheKey;
                    celestialRenderProjections[description.Handle] = CelestialRenderProjection.Create(snapshot, configuration.Atmosphere, configuration.DayLengthTicks);
                }
                celestialRenderBodies.Add(celestialRenderProjections[description.Handle]);
            }
            if (changedConfiguration)
            { DestroySurfaceResources(); DestroyCloudResources(); DestroyStellarVisualResources(); DestroyAtmosphereShellMaterials(); }
            return celestialRenderBodies;
        }
        private CelestialBodyInfo? GetCelestialRenderBody(CelestialBodyManager manager, int handle, double tick)
        {
            var bodies = GetCelestialRenderBodies(manager, tick);
            return celestialRenderRuntime == null ? manager.GetBodyById(handle) : bodies.FirstOrDefault(body => body.bodyId == handle);
        }
        private DVector3 GetCelestialRenderPosition(CelestialBodyManager manager, int handle, double tick) => celestialRenderSnapshots.TryGetValue(handle, out var snapshot) &&
            celestialRenderRuntime != null ? snapshot.Motion.Position : manager.GetBodyPositionAbs(handle, tick);
        private Quaternion GetCelestialRenderRotation(int handle) => celestialRenderSnapshots.TryGetValue(handle, out var snapshot) && celestialRenderRuntime != null ?
            (Quaternion)snapshot.Motion.Orientation : PFViewBodyRotation.FixedToEciRotation(handle);
        private PlanetLayer? GetCelestialRenderLayer(CelestialBodyManager manager, int handle)
        {
            if (celestialRenderRuntime == null) return Launch.GetSurfaceState.GetLayerForBody(manager, handle);
            return celestialRenderSnapshots.TryGetValue(handle, out var snapshot) && snapshot.CacheKey.LayerId.HasValue &&
                Find.WorldGrid.PlanetLayers.TryGetValue(snapshot.CacheKey.LayerId.Value, out var layer) ? layer : null;
        }
        private CelestialVisualDescription? GetCelestialRenderVisual(int handle, CelestialVisualKind kind) => celestialRenderSnapshots.TryGetValue(handle, out var snapshot) ?
            snapshot.Visuals.SingleOrDefault(visual => visual.Kind == kind) : null;
        private DVector3 GetCelestialRenderLight(CelestialBodyManager manager, int handle, double tick)
        {
            if (celestialRenderRuntime == null)
            { var star = manager.GetStar(); return star == null ? DVector3.zero : manager.GetBodyPositionAbs(star.bodyId, tick); }
            var runtime = celestialRenderRuntime;
            bool Resolve(int owner, out DVector3 position)
            {
                if (celestialRenderSnapshots.ContainsKey(owner) && celestialRenderConfigurations.TryGetValue(owner, out var configuration) &&
                    configuration.PrimaryLightId is { } light && celestialRenderHandles.TryGetValue(light, out int lightHandle) &&
                    celestialRenderSnapshots.TryGetValue(lightHandle, out var snapshot)) { position = snapshot.Motion.Position; return true; }
                position = default; return false;
            }
            if (Resolve(handle, out var direct)) return direct;
            if (runtime.Creation.RootSurfaceId is { } rootId && celestialRenderHandles.TryGetValue(rootId, out int rootHandle) && Resolve(rootHandle, out var root)) return root;
            return DVector3.zero;
        }
    }
}
