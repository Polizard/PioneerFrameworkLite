using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace PioneerFramework.Landing
{
    public static class LandingFootprintService
    {
        public static bool TryCollect(Map source, out LandingFootprint? footprint, out LandingReport report)
        {
            footprint = null;
            try
            {
                var cells = new HashSet<IntVec3>();
                foreach (var cell in source.AllCells)
                {
                    var terrain = source.terrainGrid.TerrainAt(cell);
                    if (terrain == null) throw new InvalidOperationException("Missing terrain at " + cell);
                    if (!SpaceTerrainUtility.IsSpaceFloor(terrain)) cells.Add(cell);
                }
                foreach (var thing in source.listerThings.AllThings.Where(t => t.def.category != ThingCategory.Mote && t.def.category != ThingCategory.Projectile))
                    if (thing.OccupiedRect().Cells.Any(c => !cells.Contains(c)))
                        throw new InvalidOperationException("Persistent ship content outside deck: " + thing.ThingID);
                if (source.AllCells.Any(c => source.roofGrid.RoofAt(c) != null && !cells.Contains(c)))
                    throw new InvalidOperationException("Roof outside ship deck.");
                footprint = new LandingFootprint(cells); report = LandingReport.Ok(); return true;
            }
            catch (Exception e) { report = LandingReport.Reject("PFL_LandingFootprintError", e.Message); return false; }
        }

        public static LandingReport Validate(Map source, Map target, LandingFootprint footprint, IntVec3 center, IReadOnlyCollection<IntVec3> reservedCells)
        {
            foreach (var cell in footprint.TargetCells(center))
            {
                if (!cell.InBounds(target) || cell.Fogged(target) || reservedCells.Contains(cell)) return LandingReport.Reject("PFL_LandingInvalidCell");
                var terrain = target.terrainGrid.TerrainAt(cell);
                if (terrain == null || terrain.passability == Traversability.Impassable || !terrain.affordances.Contains(TerrainAffordanceDefOf.Heavy))
                    return LandingReport.Reject("PFL_LandingInvalidTerrain");
            }
            var request = new ShipSurfaceTransferRequest(source, target, footprint.SourceCells, footprint.TargetOrigin(center), "landing-preview");
            return ShipSurfaceTransfer.TryValidate(request, out var result) ? LandingReport.Ok() : LandingReport.Reject("PFL_LandingPlacementBlocked", result.Message);
        }
    }
}
