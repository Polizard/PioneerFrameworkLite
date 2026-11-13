using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PioneerFramework.Celestial
{
    public sealed record CelestialRoomAir(double? PressurePascals, double? OxygenPascals, double ToxicityPerDay, double RadiationPerDay);
    public sealed record CelestialRoomVolume(int[] Cells, CelestialRoomAir Air);
    public sealed record CelestialRoomTopology(int[] Cells, bool OpenToOutside, double ConditioningPerSecond,
        bool SuppliesOxygen, bool FiltersToxins, bool RegulatesPressure,
        double? OxygenConditioningPerSecond = null, double? ToxinConditioningPerSecond = null, double? PressureConditioningPerSecond = null);

    /// <summary>Room-volume approximation. Cell membership only remaps split/merged native rooms; no per-cell chemistry.</summary>
    public sealed class CelestialRoomEnvironmentState
    {
        public const int Version = 1;
        public long LastGameTick { get; private set; } = -1;
        public IReadOnlyList<CelestialRoomVolume> Rooms { get; private set; } = Array.Empty<CelestialRoomVolume>();
        public string OwnerId { get; }
        public CelestialRoomEnvironmentState(string ownerId) { ArgumentException.ThrowIfNullOrWhiteSpace(ownerId); OwnerId = ownerId; }
        /// <summary>
        /// 时钟回拨（调试回拨/工具改 tick）时把已提交 tick 夹到当前值并保留房间状态。
        /// 2026-11-07 A-5：旧行为会永久 Block（太空暂停 + 拒绝保存且无自愈），改为可恢复诊断。
        /// </summary>
        public void Rebase(long gameTick)
        {
            if (gameTick < 0) throw new ArgumentOutOfRangeException(nameof(gameTick));
            if (LastGameTick > gameTick) LastGameTick = gameTick;
        }
        public void Advance(long gameTick, IReadOnlyList<CelestialRoomTopology> topology, CelestialRoomAir outdoor)
        {            if (gameTick < 0 || gameTick < LastGameTick) throw new ArgumentOutOfRangeException(nameof(gameTick));
            if (gameTick == LastGameTick) return;
            double seconds = LastGameTick < 0 ? 0 : (gameTick - LastGameTick) / 60d;
            var oldCells = new Dictionary<int, CelestialRoomAir>();
            foreach (var room in Rooms) foreach (int cell in room.Cells) oldCells.Add(cell, room.Air);
            var allCells = new HashSet<int>();
            var next = new List<CelestialRoomVolume>();
            foreach (var room in topology)
            {
                if (room.Cells.Length == 0 || room.Cells.Any(c => c < 0 || !allCells.Add(c)) ||
                    !double.IsFinite(room.ConditioningPerSecond) || room.ConditioningPerSecond < 0 ||
                    new[] { room.OxygenConditioningPerSecond, room.ToxinConditioningPerSecond, room.PressureConditioningPerSecond }.Any(v => v.HasValue && (!double.IsFinite(v.Value) || v < 0)))
                    throw new ArgumentException("Invalid room topology.");
                var inherited = room.Cells.Select(c => oldCells.GetValueOrDefault(c, outdoor)).ToArray();
                var air = new CelestialRoomAir(Mean(inherited.Select(a => a.PressurePascals)), Mean(inherited.Select(a => a.OxygenPascals)),
                    inherited.Average(a => a.ToxicityPerDay), inherited.Average(a => a.RadiationPerDay));
                // An open boundary exchanges in seconds. Inactive sealed rooms lose conditioning over two minutes,
                // representing finite life-support reserves rather than granting permanent safety from a roof.
                double exchange = 1 - Math.Exp(-seconds / (room.OpenToOutside ? 2 : 120));
                air = Mix(air, outdoor, exchange);
                if (!room.OpenToOutside && room.ConditioningPerSecond > 0)
                {
                    double pressure = 1 - Math.Exp(-seconds * (room.PressureConditioningPerSecond ?? room.ConditioningPerSecond));
                    double oxygen = 1 - Math.Exp(-seconds * (room.OxygenConditioningPerSecond ?? room.ConditioningPerSecond));
                    air = new(room.RegulatesPressure ? LerpKnown(air.PressurePascals, 101325, pressure) : air.PressurePascals,
                        room.SuppliesOxygen ? LerpKnown(air.OxygenPascals, 21200, oxygen) : air.OxygenPascals,
                        // Bounded game approximation: rated filtration removes toxicity-per-day units per second.
                        room.FiltersToxins ? Math.Max(0, air.ToxicityPerDay - seconds *
                            (room.ToxinConditioningPerSecond ?? room.ConditioningPerSecond)) : air.ToxicityPerDay,
                        air.RadiationPerDay);
                }
                next.Add(new(room.Cells.OrderBy(c => c).ToArray(), air));
            }
            Rooms = next.AsReadOnly(); LastGameTick = gameTick;
        }
        private static double? Mean(IEnumerable<double?> values)
        { var all = values.ToArray(); return all.Any(v => !v.HasValue) ? null : all.Average(v => v!.Value); }
        private static double? LerpKnown(double? a, double b, double t) => a.HasValue ? a + (b - a) * t : t >= .999 ? b : null;
        private static CelestialRoomAir Mix(CelestialRoomAir a, CelestialRoomAir b, double t) => new(
            b.PressurePascals.HasValue ? LerpKnown(a.PressurePascals, b.PressurePascals.Value, t) : null,
            b.OxygenPascals.HasValue ? LerpKnown(a.OxygenPascals, b.OxygenPascals.Value, t) : null,
            a.ToxicityPerDay + (b.ToxicityPerDay - a.ToxicityPerDay) * t,
            a.RadiationPerDay + (b.RadiationPerDay - a.RadiationPerDay) * t);
        public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(new Saved(Version, OwnerId, LastGameTick, Rooms));
        public static CelestialResult<CelestialRoomEnvironmentState> Deserialize(ReadOnlyMemory<byte> payload, string expectedOwner)
        {
            try
            {
                var data = CelestialResolvedJson.Read<Saved>(payload);
                if (data.Version != Version || data.OwnerId != expectedOwner || data.LastGameTick < -1 || data.Rooms == null)
                    throw new ArgumentException("Room state version/owner mismatch.");
                var cells = new HashSet<int>();
                foreach (var room in data.Rooms)
                {
                    if (room == null || room.Cells == null || room.Cells.Length == 0 || room.Air == null || room.Cells.Any(c => c < 0 || !cells.Add(c)) ||
                        new[] { room.Air.PressurePascals, room.Air.OxygenPascals, room.Air.ToxicityPerDay, room.Air.RadiationPerDay }.Any(v => v.HasValue && (!double.IsFinite(v.Value) || v < 0)))
                        throw new ArgumentException("Invalid saved room volume.");
                }
                return CelestialResult.Ok(new CelestialRoomEnvironmentState(data.OwnerId) { LastGameTick = data.LastGameTick, Rooms = data.Rooms });
            }
            catch (Exception e) when (e is JsonException or ArgumentException)
            { return CelestialResult.Reject<CelestialRoomEnvironmentState>(new CelestialError("RoomEnvironmentRestoreBlocked", Detail: e.Message)); }
        }
        private sealed record Saved(int Version, string OwnerId, long LastGameTick, IReadOnlyList<CelestialRoomVolume> Rooms);
    }
}
