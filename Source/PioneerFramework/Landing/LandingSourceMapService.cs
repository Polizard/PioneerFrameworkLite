using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace PioneerFramework.Landing
{
    public static class LandingSourceMapService
    {
        public static Map? FindMap(int id) => Current.Game == null ? null : Find.Maps.FirstOrDefault(m => m.uniqueID == id);

        public static void Describe(IEnumerable<SpaceEntity> entities, out List<LandingSourceMapData> maps, out List<int> mapless)
        {
            var byMap = new SortedDictionary<int, LandingSourceMapData>();
            mapless = new List<int>();
            foreach (var entity in entities.OrderBy(e => e.EntityId))
            {
                var ids = entity.Parts.Where(p => p.linkedMapId >= 0 && !p.isDestroyed).Select(p => p.linkedMapId).ToHashSet();
                if (entity.MapId >= 0) ids.Add(entity.MapId);
                if (ids.Count == 0)
                {
                    if (entity.HasMap || entity.IsMapArchived) throw new InvalidOperationException("Missing associated map identity: " + entity.EntityId);
                    mapless.Add(entity.EntityId);
                }
                foreach (int id in ids)
                {
                    if (!byMap.TryGetValue(id, out var item)) byMap[id] = item = new LandingSourceMapData { MapId = id };
                    item.EntityIds.Add(entity.EntityId);
                }
            }
            maps = byMap.Values.ToList();
        }

        public static bool TryCollect(SpaceGameComp sg, SpaceEntityGroup group, out List<LandingSourceMapData> maps,
            out List<int> maplessEntityIds, out LandingReport report)
        {
            maps = new List<LandingSourceMapData>(); maplessEntityIds = new List<int>();
            try
            {
                var entities = group.GetEntityIds().Select(id => sg.GetEntityById(id) ?? throw new InvalidOperationException("Missing entity " + id)).ToList();
                Describe(entities, out maps, out maplessEntityIds);
                foreach (var source in maps)
                {
                    Map? map = FindMap(source.MapId);
                    if (map == null) throw new InvalidOperationException("Source map requires restoration: " + source.MapId);
                    int owner = map.GetComponent<SpaceMapComp>()?.ownerEntityId ?? -1;
                    if (owner >= 0 && !source.EntityIds.Contains(owner)) throw new InvalidOperationException("Source map has another owner: " + source.MapId);
                }
                report = LandingReport.Ok(); return true;
            }
            catch (Exception e) { report = LandingReport.Reject("PFL_LandingSourceUnavailable", e.Message); return false; }
        }

        public static void BeginPrepare(SpaceGameComp sg, SpaceEntityGroup group, Action ready, Action<LandingReport> failed)
        {
            if (sg.ActiveArchiveOperation != null || sg.ActiveRestoreOperation != null)
            { failed(LandingReport.Reject("PFL_LandingSourceBusy")); return; }
            LongEventHandler.QueueLongEvent(() =>
            {
                try
                {
                    foreach (int id in group.GetEntityIds().OrderBy(x => x))
                    {
                        var entity = sg.GetEntityById(id) ?? throw new InvalidOperationException("Missing entity " + id);
                        if (!entity.IsMapArchived) continue;
                        if (!sg.BeginRestoreMap(entity)) throw new InvalidOperationException("Cannot restore map " + entity.MapId);
                        int batches = 0;
                        while (!sg.StepActiveMapRestore())
                            if (++batches > 100000) throw new InvalidOperationException("Source restoration did not finish.");
                    }
                    ready();
                }
                catch (Exception e) { failed(LandingReport.Reject("PFL_LandingSourceUnavailable", e.Message)); }
            }, "PFL_LandingPreparing", false, e => failed(LandingReport.Reject("PFL_LandingSourceUnavailable", e.Message)));
        }
    }
}
