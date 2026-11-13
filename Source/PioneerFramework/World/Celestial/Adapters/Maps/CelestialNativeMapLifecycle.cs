using System;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Celestial
{
    /// <summary>Only successful native removals retire identities. Unannounced missing objects retain their saved IDs.</summary>
    internal static class CelestialNativeMapLifecycle
    {
        private static bool installed;
        internal sealed record Retirement(Game Game, World World, CelestialRuntime Runtime, CelestialMapAddress Address,
            MapParent Parent, Map? Map, bool MovedFailedSite = false);
        internal static void Install()
        {
            if (installed) return;
            var harmony = new Harmony("PioneerFramework.Celestial.NativeMapLifecycle");
            harmony.Patch(CelestialNativeCompatibility.RequireMethod("Game.DeinitAndRemoveMap"),
                prefix: new HarmonyMethod(typeof(CelestialNativeMapLifecycle), nameof(CaptureMapRemoval)),
                postfix: new HarmonyMethod(typeof(CelestialNativeMapLifecycle), nameof(MapRemoved)));
            harmony.Patch(CelestialNativeCompatibility.RequireMethod("WorldObjectsHolder.Remove"),
                prefix: new HarmonyMethod(typeof(CelestialNativeMapLifecycle), nameof(CaptureParentRemoval)),
                postfix: new HarmonyMethod(typeof(CelestialNativeMapLifecycle), nameof(ParentRemoved)));
            CelestialNativeCompatibility.RequireInstalledHooks("Game.DeinitAndRemoveMap.prefix.CaptureMapRemoval", "Game.DeinitAndRemoveMap.postfix.MapRemoved",
                "WorldObjectsHolder.Remove.prefix.CaptureParentRemoval", "WorldObjectsHolder.Remove.postfix.ParentRemoved");
            installed = true;
        }
        private static Retirement? Capture(MapParent? parent, Map? map)
        {
            var game = Current.Game;
            if (game?.World == null || parent == null ||
                game.GetComponent<CelestialBodyManager>()?.TryGetCelestialRuntime(out var runtime) != true || runtime.IsRestoring || runtime.Maps == null) return null;
            if (runtime.Maps.FindFailedSite(parent, map) is { } failed)
            {
                bool moved = !game.World.grid.PlanetLayers.TryGetValue(failed.Address.LayerId, out var sourceLayer) ||
                    parent.Tile != new PlanetTile(failed.Address.TileId, sourceLayer);
                return new(game, game.World, runtime, failed.Address, parent, map, moved);
            }
            if (parent.Tile.Layer is not CelestialSurfaceLayer owned) return null;
            return new(game, game.World, runtime, new(owned.Header.OwnerId, owned.LayerID, parent.Tile.tileId, parent.ID), parent, map);
        }
        internal static void CaptureMapRemoval(Game __instance, Map map, out Retirement? __state)
            => __state = ReferenceEquals(Current.Game, __instance) && map != null && __instance.Maps.Contains(map) ? Capture(map.Parent, map) : null;
        internal static void CaptureParentRemoval(WorldObjectsHolder __instance, WorldObject o, out Retirement? __state)
            => __state = ReferenceEquals(Current.Game?.World?.worldObjects, __instance) && o is MapParent parent && __instance.MapParents.Contains(parent)
                ? Capture(parent, null) : null;
        private static bool IsCurrent(Retirement? state) => state != null && ReferenceEquals(Current.Game, state.Game) &&
            ReferenceEquals(state.Game.World, state.World) && state.Game.GetComponent<CelestialBodyManager>()?.TryGetCelestialRuntime(out var current) == true &&
            ReferenceEquals(state.Runtime, current) && !current.IsRestoring;
        internal static void MapRemoved(Game __instance, Retirement? __state)
        {
            if (!IsCurrent(__state) || !ReferenceEquals(__state!.Game, __instance) || __state.Map == null || __instance.Maps.Contains(__state.Map)) return;
            // Ordering dependency (P1-1): NativeLocalMapSession.Finish removes a failed session's partially generated
            // map before it records provenance, so RetireMap sees a site whose MapId is still null and early-returns.
            // Failure/Generation/operation history therefore survives; do not bind the failed record before removal.
            var retired = __state.Runtime.Maps!.RetireNativeMap(__state.Address, __state.Map.uniqueID, __state.MovedFailedSite);
            if (!retired.Success) { Log.Error("Celestial map retirement failed: " + retired.Errors[0].Code); return; }
            RetireEmptyParent(__state);
        }
        internal static void ParentRemoved(WorldObjectsHolder __instance, Retirement? __state)
        {
            if (!IsCurrent(__state) || !ReferenceEquals(__state!.World.worldObjects, __instance) || __instance.MapParents.Contains(__state.Parent)) return;
            RetireEmptyParent(__state);
        }
        private static void RetireEmptyParent(Retirement state)
        {
            if (state.World.worldObjects.MapParents.Contains(state.Parent)) return;
            foreach (var map in state.Game.Maps) if (ReferenceEquals(map.Parent, state.Parent)) return;
            var retired = state.Runtime.Maps!.RetireNativeParent(state.Address);
            if (!retired.Success) Log.Error("Celestial parent retirement failed: " + retired.Errors[0].Code);
        }
    }
}
