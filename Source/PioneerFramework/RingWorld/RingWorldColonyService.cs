using System;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    public static class RingWorldColonyService
    {
        public static bool TryGetOrGenerateMap(RingSurfaceLayer ring, int tileId, IntVec3 size,
            out Map? map, out string reason)
        {
            map = null; reason = "";
            if (Current.ProgramState != ProgramState.Playing || Find.WorldGrid == null || Scribe.mode != LoadSaveMode.Inactive)
            { reason = "A loaded game and inactive Scribe are required."; return false; }
            if (ring == null || !Find.WorldGrid.PlanetLayers.TryGetValue(ring.LayerID, out PlanetLayer? current) || !ReferenceEquals(current, ring))
            { reason = "Ring is not registered."; return false; }
            if (!ring.Topology.Contains(tileId) || size.x < 50 || size.z < 50 || size.x > 500 || size.z > 500)
            { reason = "Invalid tile or map size (50..500 cells per side)."; return false; }
            PlanetTile tile = new PlanetTile(tileId, ring);
            MapParent parent = Find.WorldObjects.MapParentAt(tile);
            if (parent != null && (parent is not Settlement || parent.Faction != Faction.OfPlayer))
            { reason = "The tile already has another world object."; return false; }
            if (parent?.Map != null) { map = parent.Map; return true; }
            if (parent == null && !TileFinder.IsValidTileForNewSettlement(tile))
            { reason = "This tile is not valid for a new settlement."; return false; }
            Settlement? created = null;
            try
            {
                if (parent == null)
                {
                    created = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
                    created.Tile = tile; created.SetFaction(Faction.OfPlayer);
                    created.Name = "PF_RingColonyName".Translate(ring.Parameters.RingId, tileId);
                    Find.WorldObjects.Add(created); parent = created;
                }
                map = GetOrGenerateMapUtility.GetOrGenerateMap(tile, size, WorldObjectDefOf.Settlement);
                if (map == null) throw new InvalidOperationException("Map generation returned no map.");
                return true;
            }
            catch (Exception error)
            {
                if (created != null && !created.HasMap && created.Spawned) created.Destroy();
                reason = error.Message; Log.Error($"[PFL RingWorld] Colony generation failed: {error}"); return false;
            }
        }

        public static void View(RingSurfaceLayer ring, int tileId = -1)
        {
            if (!Find.WorldGrid.PlanetLayers.TryGetValue(ring.LayerID, out PlanetLayer? current) || !ReferenceEquals(current, ring))
                throw new InvalidOperationException("Ring is not registered.");
            PlanetLayer.Selected = ring; CameraJumper.TryShowWorld();
            if (tileId >= 0) { ring.Topology.Check(tileId); Find.WorldCameraDriver.JumpTo(new PlanetTile(tileId, ring)); }
            else Find.WorldCameraDriver.JumpTo(Vector3.forward);
            float altitude = tileId >= 0 ? 180 : 600;
            Find.WorldCameraDriver.altitude = altitude;
            HarmonyLib.AccessTools.Field(typeof(WorldCameraDriver), "desiredAltitude").SetValue(Find.WorldCameraDriver, altitude);
        }
    }
}
