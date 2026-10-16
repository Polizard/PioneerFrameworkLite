using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.RingWorld
{
    public static class RingWorldService
    {
        public const string LayerDefName = "PF_RingWorld";
        [ThreadStatic] internal static RingParameters? PendingParameters;
        public static RingSurfaceLayer? FindById(IEnumerable<PlanetLayer> layers, string id) =>
            layers.OfType<RingSurfaceLayer>().FirstOrDefault(layer => layer.Parameters?.RingId == id);
        public static RingSurfaceLayer? FindById(string id) => Find.WorldGrid == null ? null : FindById(Find.WorldGrid.PlanetLayers.Values, id);
        public static void RequireMatching(RingSurfaceLayer layer, RingParameters parameters)
        {
            if (layer.Parameters != parameters) throw new InvalidOperationException("RingId already exists with different parameters.");
        }
        public static bool ValidateParent(RingParameters parameters, double parentRadius, out string reason)
        {
            reason = "";
            if (!double.IsFinite(parentRadius) || parentRadius <= 0 || parameters.RadiusMeters <= parentRadius)
            { reason = "Ring radius must exceed the parent body's radius."; return false; }
            return true;
        }
        public static bool TryCreate(RingParameters parameters, out RingSurfaceLayer? ring, out string reason)
        {
            ring = null; reason = "";
            if (parameters == null) {reason="Ring parameters are required.";return false;}
            if (Find.WorldGrid == null || Current.ProgramState != ProgramState.Playing || Scribe.mode != LoadSaveMode.Inactive)
            { reason = "A loaded game and inactive Scribe are required."; return false; }
            RingSurfaceLayer? existing = FindById(parameters.RingId);
            if (existing != null)
            {
                if (existing.Parameters != parameters) { reason = "RingId already exists with different parameters."; return false; }
                if (existing.Tiles.Count != existing.Topology.TileCount) {reason="Ring is still initializing.";return false;}
                ring = existing; return true;
            }
            var bodies = Current.Game.GetComponent<CelestialBodyManager>();
            var body = bodies?.GetBodyById(parameters.ParentBodyId);
            if (body == null || !body.hasSurface) { reason = "A parent body with a surface is required."; return false; }
            if (!ValidateParent(parameters, body.radius, out reason)) return false;
            if (!ModsConfig.OdysseyActive) { reason = "Odyssey is required for this ring world."; return false; }
            var def = DefDatabase<PlanetLayerDef>.GetNamedSilentFail(LayerDefName);
            if (def == null) { reason = "PF_RingWorld definition is missing."; return false; }
            var previousIds = new HashSet<int>(Find.WorldGrid.PlanetLayers.Keys);
            RingParameters? previousPending = PendingParameters;
            try
            {
                RingWorldPatches.Install();
                PendingParameters = parameters;
                ring = (RingSurfaceLayer)Find.WorldGrid.RegisterPlanetLayer(def,
                    radius: (float)(parameters.RadiusMeters * SpaceConstants.UnitsPerMeter), subdivisions: 0, viewAngle: 180);
                Rand.PushState();
                try {ring.RunWorldGeneration(parameters.Seed);}
                finally {Rand.PopState();}
                if (ring.Tiles.Count != ring.Topology.TileCount) throw new InvalidOperationException("Ring tile generation was incomplete.");
                Find.WorldPathGrid.RecalculateLayerPerceivedPathCosts(ring);
                Find.WorldReachability.ClearCache();
                ring.SetAllLayersDirty();
                return true;
            }
            catch (Exception error)
            {
                foreach (RingSurfaceLayer added in Find.WorldGrid.PlanetLayers.Values.OfType<RingSurfaceLayer>()
                    .Where(layer => !previousIds.Contains(layer.LayerID) && layer.Parameters?.RingId == parameters.RingId).ToList())
                    Find.WorldGrid.RemovePlanetLayer(added);
                ring = null; reason = error.Message;
                Log.Error($"[PFL RingWorld] Creation failed: {error}"); return false;
            }
            finally { PendingParameters = previousPending; }
        }
        public static bool TryRemove(RingSurfaceLayer ring, out string reason) =>
            RuntimePlanetLayerService.TryRemove(ring, false, out reason);
    }
}
