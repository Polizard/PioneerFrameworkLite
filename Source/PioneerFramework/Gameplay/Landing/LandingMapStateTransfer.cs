using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace PioneerFramework.Landing
{
    public sealed class LandingMapStateTransfer : IShipSurfaceTransferParticipant
    {
        private readonly Map source, target;
        private readonly ShipSurfaceTransferLayout layout;
        private readonly LandingVesselRecord record;
        private readonly Dictionary<Area, Area> areas = new Dictionary<Area, Area>();
        private readonly List<Area> createdAreas = new List<Area>();
        private readonly List<Zone> createdZones = new List<Zone>();
        private readonly Dictionary<Pawn, Area?> restrictions = new Dictionary<Pawn, Area?>();
        private readonly List<(Area area, IntVec3 cell, bool before)> modifiedAreas = new List<(Area, IntVec3, bool)>();
        private readonly List<(Area area, List<IntVec3> cells)> sourceAreas = new List<(Area, List<IntVec3>)>();
        private readonly List<(Zone zone, List<IntVec3> cells)> sourceZones = new List<(Zone, List<IntVec3>)>();
        private readonly List<Thing> things = new List<Thing>();
        private readonly Dictionary<Building_Bed, List<Pawn>> beds = new Dictionary<Building_Bed, List<Pawn>>();
        public LandingMapStateTransfer(Map source, Map target, ShipSurfaceTransferLayout layout, LandingVesselRecord record)
        { this.source = source; this.target = target; this.layout = layout; this.record = record; }
        public void Capture()
        {
            var selected = layout.SourceCells.ToHashSet();
            foreach (var area in source.areaManager.AllAreas)
            {
                var cells = area.ActiveCells.Where(selected.Contains).ToList();
                if (cells.Count > 0) sourceAreas.Add((area, cells));
            }
            foreach (var zone in source.zoneManager.AllZones)
            {
                var cells = zone.Cells.Where(selected.Contains).ToList();
                if (cells.Count == 0) continue;
                if (zone is not Zone_Stockpile && zone is not Zone_Growing) throw new InvalidOperationException("Unsupported ship zone: " + zone.GetType().FullName);
                sourceZones.Add((zone, cells));
            }
            foreach (var pawn in source.mapPawns.AllPawnsSpawned.Where(p => p.playerSettings != null))
                restrictions[pawn] = pawn.playerSettings.AreaRestrictionInPawnCurrentMap;
            things.AddRange(source.listerThings.AllThings.Where(t => t.def.category != ThingCategory.Mote && t.def.category != ThingCategory.Projectile));
            foreach (var area in restrictions.Values.OfType<Area_Allowed>().Distinct())
                if (!sourceAreas.Any(a => a.area == area)) sourceAreas.Add((area, new List<IntVec3>()));
            foreach (var bed in things.OfType<Building_Bed>()) beds[bed] = bed.OwnersForReading.ToList();
            record.ThingIds = things.Select(t => t.ThingID).ToList();
        }
        public void Apply()
        {
            foreach (var entry in sourceAreas)
            {
                Area destination;
                if (entry.area is Area_Allowed allowed)
                {
                    if (!target.areaManager.TryMakeNewAllowed(out var copy)) throw new InvalidOperationException("Target allowed-area limit reached.");
                    copy.SetLabel(allowed.Label); copy.SetColor(allowed.Color); createdAreas.Add(copy); destination = copy;
                }
                else destination = target.areaManager.AllAreas.FirstOrDefault(a => a.GetType() == entry.area.GetType())
                    ?? throw new InvalidOperationException("Missing standard target area.");
                areas[entry.area] = destination;
                foreach (var c in entry.cells)
                {
                    var to = layout.MapCell(c); modifiedAreas.Add((destination, to, destination[to])); destination[to] = true;
                }
            }
            foreach (var entry in sourceZones)
            {
                Zone copy;
                if (entry.zone is Zone_Stockpile stock)
                {
                    var created = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, target.zoneManager);
                    created.settings.CopyFrom(stock.settings); copy = created;
                }
                else
                {
                    var created = new Zone_Growing(target.zoneManager);
                    created.SetPlantDefToGrow(((Zone_Growing)entry.zone).GetPlantDefToGrow()); copy = created;
                }
                copy.RenamableLabel = entry.zone.label; createdZones.Add(copy); target.zoneManager.RegisterZone(copy);
                foreach (var cell in entry.cells) copy.AddCell(layout.MapCell(cell));
            }
            foreach (var entry in restrictions)
                entry.Key.playerSettings.AreaRestrictionInPawnCurrentMap = entry.Value != null && areas.TryGetValue(entry.Value, out var area) ? area : null;
            RestoreBeds();
            target.GetComponent<LandingMapRecords>().Record(record);
        }
        public void Verify()
        {
            foreach (var thing in things)
                if (!thing.Spawned || thing.Map != target) throw new InvalidOperationException("Ship content did not arrive: " + thing.ThingID);
            foreach (var pawn in restrictions.Keys)
                if (pawn.playerSettings.AreaRestrictionInPawnCurrentMap is Area area && area.Map != target)
                    throw new InvalidOperationException("Pawn area remained on source map.");
        }
        public void Rollback()
        {
            foreach (var entry in restrictions) entry.Key.playerSettings.AreaRestrictionInPawnCurrentMap = entry.Value;
            RestoreBeds();
            foreach (var zone in createdZones) target.zoneManager.DeregisterZone(zone);
            foreach (var entry in modifiedAreas) entry.area[entry.cell] = entry.before;
            foreach (var area in createdAreas) area.Delete();
            target.GetComponent<LandingMapRecords>()?.Records.Remove(record);
        }
        private void RestoreBeds()
        {
            foreach (var entry in beds)
                foreach (var pawn in entry.Value) entry.Key.GetComp<CompAssignableToPawn_Bed>().TryAssignPawn(pawn);
        }
    }
}
