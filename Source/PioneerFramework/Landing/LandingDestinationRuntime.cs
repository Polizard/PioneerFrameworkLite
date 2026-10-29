using System;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using PioneerFramework.Celestial;

namespace PioneerFramework.Landing
{
    public static class LandingDestinationRuntime
    {
        public static Map Generate(LandingTaskData task, IntVec2 size)
        {
            CelestialRuntime? runtime = null;
            CelestialSurfaceLayer? surface = task.TargetTile.Layer as CelestialSurfaceLayer;
            if (surface != null)
            {
                var manager = Current.Game?.GetComponent<CelestialBodyManager>();
                if (manager?.TryGetCelestialRuntime(out runtime!) != true)
                    throw new InvalidOperationException("CelestialRuntimeUnavailable");
                var target = runtime.Query(surface.Header.OwnerId);
                if (!target.Success || target.Value.Description.Handle != task.TargetBodyId || target.Value.Lifecycle != CelestialLifecycle.Ready)
                    throw new InvalidOperationException("SurfaceIdentityMismatch");
            }
            var parent = Find.WorldObjects.ObjectsAt(task.TargetTile).OfType<MapParent>().FirstOrDefault();
            if (parent == null)
            {
                parent = (MapParent)WorldObjectMaker.MakeWorldObject(DefDatabase<WorldObjectDef>.GetNamed("PF_LandedShipSite"));
                parent.Tile = task.TargetTile; parent.SetFaction(Faction.OfPlayer);
                Find.WorldObjects.Add(parent);
            }
            task.DestinationParentId = parent.ID;
            if (runtime != null)
            {
                var result = runtime.RequestMap(new(new("landing:" + task.TaskId + ":destination", runtime.WorldSessionId, runtime.CaptureCatalog().Revision),
                    new(surface!.Header.OwnerId, surface.LayerID, task.TargetTile.tileId, parent.ID), CelestialMapAction.Land, size.x, size.z));
                if (result.Operation.Status != CelestialOperationStatus.Applied || result.Handle == null ||
                    runtime.Maps?.TryGetNativeMap(result.Handle.MapId, out var map) != true)
                    throw new InvalidOperationException("CelestialMapUnavailable: " + string.Join(",", result.Operation.Errors.Select(error => error.Code)));
                return map;
            }
            if (parent.HasMap) return parent.Map;
            if (parent is not LandedShipMapParent site || site.EverGenerated) throw new InvalidOperationException("Existing map cannot be safely regenerated.");
            return GetOrGenerateMapUtility.GetOrGenerateMap(task.TargetTile, new IntVec3(size.x, 1, size.z), parent.def);
        }
    }
}
