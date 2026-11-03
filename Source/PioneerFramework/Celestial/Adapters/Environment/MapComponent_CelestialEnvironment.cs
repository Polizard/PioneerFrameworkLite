using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace PioneerFramework.Celestial
{
    /// <summary>Native room topology and game ticks own supplement updates. No indoor temperature or native vacuum is stored here.</summary>
    public sealed class MapComponent_CelestialEnvironment(Map map) : MapComponent(map)
    {
        private CelestialRoomEnvironmentState? state;
        private string? savedPayload;
        private string? savedOwner;
        private readonly Dictionary<int, CelestialRoomAir> airByCell = new();
        private readonly Dictionary<int, CelestialRoomVolume> volumeByCell = new();
        private int lastProcessedTick = -1;
        public string? BlockedReason { get; private set; }
        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving && state != null && BlockedReason == null)
            { savedPayload = Encoding.UTF8.GetString(state.Serialize()); savedOwner = state.OwnerId; }
            Scribe_Values.Look(ref savedPayload, "celestialRoomEnvironment", null);
            Scribe_Values.Look(ref savedOwner, "celestialRoomOwner", null);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) { state = null; airByCell.Clear(); lastProcessedTick = -1; }
        }
        public bool EnsureRestored(bool retry = false)
        {
            if (BlockedReason != null && !retry) return false;
            if (state != null)
            {
                if (BlockedReason == null) return true;
                try
                {
                    if (!HasEnvironmentOwner(state.OwnerId)) return Block("RoomEnvironmentDependencyUnavailable");
                    if (state.LastGameTick > Find.TickManager.TicksGame) return Block("RoomEnvironmentClockReversed");
                    // Validate repaired read dependencies only. Keep both the committed state and exposure interval claim.
                    _ = CaptureTopology(map);
                    foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                        if (!pawn.Dead) _ = CelestialPawnEnvironment.Protection(pawn);
                    Reindex(); BlockedReason = null; return true;
                }
                catch (Exception exception) { return Block("RoomEnvironmentUpdateFailed: " + exception.Message); }
            }
            if (savedPayload == null) return BlockedReason == null;
            try
            {
                if (!HasEnvironmentOwner(savedOwner))
                    return Block("RoomEnvironmentDependencyUnavailable");
                var restored = CelestialRoomEnvironmentState.Deserialize(Encoding.UTF8.GetBytes(savedPayload), savedOwner!);
                if (!restored.Success) return Block(restored.Errors[0].Code);
                state = restored.Value; Reindex(); BlockedReason = null; return true;
            }
            catch (Exception exception) { return Block("RoomEnvironmentRestoreFailed: " + exception.Message); }
        }
        private bool HasEnvironmentOwner(string? owner) =>
            CelestialPlanetEnvironmentHooks.TryGetLayer(map.Tile, out var nativeLayer) && nativeLayer is CelestialSurfaceLayer layer && owner == layer.Header.OwnerId.Value &&
            CelestialPlanetEnvironmentHooks.TrySample(map.Tile, out var sample) && sample.Exposure != null;
        private bool Block(string reason)
        {
            BlockedReason = reason;
            Current.Game?.GetComponent<CelestialBodyManager>()?.NotifyRoomEnvironmentRestoreFailure(reason, map);
            return false;
        }
        public override void MapComponentTick()
        {
            int tick = Find.TickManager.TicksGame;
            if (tick % 60 != 0 || tick == lastProcessedTick) return;
            if (!EnsureRestored()) return;
            if (!CelestialPlanetEnvironmentHooks.TrySample(map.Tile, out var sample) || sample.Exposure == null) return;
            if (map.Tile.Layer is not CelestialSurfaceLayer layer) return;
            state ??= new(layer.Header.OwnerId.Value);
            if (state.LastGameTick > tick) { Block("RoomEnvironmentClockReversed"); return; }
            var outdoors = new CelestialRoomAir(sample.PressurePascals, sample.OxygenPartialPressurePascals, sample.ToxicityPerDay, sample.RadiationPerDay);
            try
            {
                long previousTick = state.LastGameTick;
                state.Advance(tick, CaptureTopology(map), outdoors);
                Reindex();
                // Claim the interval before applying damage; reentrant reads cannot repeat its exposure.
                int elapsed = previousTick < 0 ? 0 : (int)Math.Min(60, tick - previousTick);
                lastProcessedTick = tick;
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn.Dead) continue;
                    var local = Effective(sample, pawn.Position);
                    bool native = ModsConfig.OdysseyActive && map.Biome.inVacuum;
                    CelestialPawnEnvironment.Apply(pawn, CelestialExposureModel.Dose(local, CelestialPawnEnvironment.Protection(pawn), elapsed, native));
                }
            }
            catch (Exception exception)
            { Block("RoomEnvironmentUpdateFailed: " + exception.Message); }
        }
        private void Reindex()
        {
            airByCell.Clear();
            volumeByCell.Clear();
            if (state == null) return;
            foreach (var volume in state.Rooms) foreach (int cell in volume.Cells)
            { airByCell.Add(cell, volume.Air); volumeByCell.Add(cell, volume); }
        }
        public CelestialSurfaceEnvironmentSample Effective(CelestialSurfaceEnvironmentSample outside, IntVec3 cell)
        {
            if (!cell.InBounds(map)) return outside;
            var result = outside;
            int index = map.cellIndices.CellToIndex(cell);
            if (airByCell.TryGetValue(index, out var air) && cell.GetRoom(map) is { IsDoorway: false } room)
            {
                var group = ConnectedRooms(map, room, new(), out bool open);
                var members = group.SelectMany(r => r.Cells).Select(map.cellIndices.CellToIndex).ToHashSet();
                // Never advertise a stale protected volume after a breach, split or merge, even within one update interval.
                if (!open && volumeByCell.TryGetValue(index, out var volume) && members.SetEquals(volume.Cells))
                    result = outside with { PressurePascals = air.PressurePascals, OxygenPartialPressurePascals = air.OxygenPascals,
                        ToxicityPerDay = air.ToxicityPerDay, RadiationPerDay = air.RadiationPerDay };
            }
            if (ModsConfig.OdysseyActive && map.Biome.inVacuum)
            {
                double fraction = 1 - Math.Clamp(cell.GetVacuum(map), 0, 1);
                result = result with { PressurePascals = 101325 * fraction, OxygenPartialPressurePascals = 21200 * fraction };
            }
            return result;
        }
        public bool IsDangerous(Pawn pawn, IntVec3 cell)
        {
            if (!EnsureRestored()) return true;
            if (!CelestialPlanetEnvironmentHooks.TrySample(map.Tile, out var sample) || sample.Exposure == null) return false;
            bool native = ModsConfig.OdysseyActive && map.Biome.inVacuum;
            // Native vacuum owns both immunity and low-pressure injury. Other exposure channels remain independent.
            return CelestialExposureModel.Dose(Effective(sample, cell), CelestialPawnEnvironment.Protection(pawn), 60000, native).Dangerous ||
                native && pawn.HarmedByVacuum && cell.GetVacuum(map) >= VacuumUtility.MinVacuumForDamage;
        }
        public static IReadOnlyList<CelestialRoomTopology> CaptureTopology(Map map)
        {
            var rooms = map.regionGrid.AllRooms.Where(r => !r.IsDoorway).ToArray();
            var visited = new HashSet<Room>(); var result = new List<CelestialRoomTopology>();
            var devices = map.listerThings.AllThings.OfType<ThingWithComps>().Select(t => t.GetComp<CompCelestialLifeSupport>()).Where(c => c?.Operational == true).ToArray();
            foreach (var first in rooms)
            {
                if (visited.Contains(first)) continue;
                var group = ConnectedRooms(map, first, visited, out bool open);
                int[] cells = group.SelectMany(r => r.Cells).Select(map.cellIndices.CellToIndex).Distinct().ToArray();
                if (cells.Length == 0) continue;
                var localDevices = devices.Where(d => group.Contains(d!.parent.GetRoom())).ToArray();
                double rate = localDevices.Sum(d => d!.Settings.conditioningPerSecondPerHundredCells) * 100 / cells.Length;
                double Rate(Func<CompProperties_CelestialLifeSupport, bool> predicate) => localDevices.Where(d => predicate(d!.Settings))
                    .Sum(d => d!.Settings.conditioningPerSecondPerHundredCells) * 100 / cells.Length;
                result.Add(new(cells, open, rate, localDevices.Any(d => d!.Settings.suppliesOxygen),
                    localDevices.Any(d => d!.Settings.filtersToxins), localDevices.Any(d => d!.Settings.regulatesPressure),
                    Rate(p => p.suppliesOxygen), Rate(p => p.filtersToxins), Rate(p => p.regulatesPressure)));
            }
            return result;
        }
        private static HashSet<Room> ConnectedRooms(Map map, Room first, HashSet<Room> visited, out bool open)
        {
            visited.Add(first);
            var group = new HashSet<Room> { first }; var queue = new Queue<Room>(); queue.Enqueue(first);
            open = false;
            while (queue.Count != 0)
            {
                var room = queue.Dequeue(); open |= room.TouchesMapEdge || room.OpenRoofCount > 0;
                foreach (var border in room.BorderCells)
                {
                    if (!border.InBounds(map)) { open = true; continue; }
                    if (border.GetEdifice(map) is not Building_Door door || !door.Open) continue;
                    foreach (var offset in GenAdj.CardinalDirections)
                    {
                        var adjacent = border + offset;
                        if (!adjacent.InBounds(map)) { open = true; continue; }
                        var other = adjacent.GetRoom(map);
                        if (other == null) { open = true; continue; }
                        if (!other.IsDoorway && visited.Add(other)) { group.Add(other); queue.Enqueue(other); }
                    }
                }
            }
            return group;
        }
    }
    public sealed class Alert_CelestialEnvironment : Alert
    {
        public Alert_CelestialEnvironment() { defaultLabel = "PF_CelestialEnvironmentAlert".Translate(); defaultPriority = AlertPriority.High; }
        public override TaggedString GetExplanation() => "PF_CelestialEnvironmentAlertDesc".Translate();
        public override AlertReport GetReport() => AlertReport.CulpritsAre(PawnsFinder.AllMaps_FreeColonistsSpawned.Where(p =>
            p.Map.GetComponent<MapComponent_CelestialEnvironment>()?.IsDangerous(p, p.Position) == true).ToList());
    }
}
