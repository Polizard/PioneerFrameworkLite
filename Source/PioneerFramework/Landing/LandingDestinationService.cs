using System;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Landing
{
    public static class LandingDestinationService
    {
        public static IntVec2 RequiredMapSize(IntVec2 size) => new IntVec2(Math.Max(250, size.x + 20), Math.Max(250, size.z + 20));
        public static bool IsAllowed(bool friendly, bool quest, bool supported, bool landable) => friendly && !quest && supported && landable;
        public static T ResolveExisting<T>(Func<T?> find, Func<T> create) where T : class => find() ?? create();
        public static Map? FindExisting(PlanetTile tile) => Current.Game == null ? null : Find.Maps.FirstOrDefault(m => m.Tile == tile && m.Parent is not SpacecraftMapParent);

        public static LandingReport Check(PlanetTile target, int bodyId, IntVec2 requiredSize)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var layer = mgr == null ? null : Launch.GetSurfaceState.GetLayerForBody(mgr, bodyId);
            if (!target.Valid || layer == null || target.Layer != layer || target.tileId >= layer.TilesCount)
                return LandingReport.Reject("PFL_LandingWrongLayer");
            if ((requiredSize.x > 400 || requiredSize.z > 400) && FindExisting(target) == null) return LandingReport.Reject("PFL_LandingTooLarge");
            if (target.Tile.PrimaryBiome == null || target.Tile.PrimaryBiome.impassable || target.Tile.hilliness == Hilliness.Impassable)
                return LandingReport.Reject("PFL_LandingUnsupportedDestination");
            foreach (var obj in Find.WorldObjects.ObjectsAt(target))
            {
                if (obj is not MapParent parent) continue;
                bool supported = parent is LandedShipMapParent || parent is Settlement || parent.GetType() == typeof(MapParent);
                if (!IsAllowed(parent.Faction == null || parent.Faction == Faction.OfPlayer, !parent.questTags.NullOrEmpty(), supported, true))
                    return LandingReport.Reject("PFL_LandingUnsupportedDestination");
                if (!parent.HasMap && (parent is not LandedShipMapParent own || own.EverGenerated))
                    return LandingReport.Reject("PFL_LandingDestinationUnavailable");
                if (parent.GetType() == typeof(MapParent)) return LandingReport.Reject("PFL_LandingUnsupportedDestination");
            }
            return LandingReport.Ok();
        }

        public static void BeginResolve(LandingTaskData task, Action<Map> ready, Action<LandingReport> failed)
        {
            LongEventHandler.QueueLongEvent(() =>
            {
                try
                {
                    var largest = new IntVec2(0, 0);
                    foreach (var source in task.SourceMaps)
                    {
                        if (source.SourceCells.Count == 0) continue;
                        largest.x = Math.Max(largest.x, source.SourceCells.Max(c => c.x) - source.SourceCells.Min(c => c.x) + 1);
                        largest.z = Math.Max(largest.z, source.SourceCells.Max(c => c.z) - source.SourceCells.Min(c => c.z) + 1);
                    }
                    var size = RequiredMapSize(largest);
                    var check = Check(task.TargetTile, task.TargetBodyId, size);
                    if (!check.Success) { failed(check); return; }
                    Map map = ResolveExisting(() => FindExisting(task.TargetTile), () => LandingDestinationRuntime.Generate(task, size));
                    task.DestinationMapId = map.uniqueID; task.DestinationParentId = map.Parent.ID;
                    ready(map);
                }
                catch (Exception e) { failed(LandingReport.Reject("PFL_LandingDestinationError", e.Message)); }
            }, "PFL_LandingPreparing", false, e => failed(LandingReport.Reject("PFL_LandingDestinationError", e.Message)));
        }
    }
}
