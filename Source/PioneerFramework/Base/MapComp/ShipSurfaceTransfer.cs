using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    public enum ShipSurfaceTransferStatus
    {
        Ready,
        Rejected,
        Completed,
        AlreadyCompleted,
        FailedRecovered,
        FailedUnrecovered
    }

    /// <summary>Caller-owned inputs. Maps must already exist; the ID must stay stable across saves and retries.</summary>
    public sealed class ShipSurfaceTransferRequest
    {
        public Map? SourceMap { get; }
        public Map? TargetMap { get; }
        public IEnumerable<IntVec3>? SourceCells { get; }
        public IntVec3 TargetOrigin { get; }
        public string? OperationId { get; }

        public ShipSurfaceTransferRequest(Map? sourceMap, Map? targetMap,
            IEnumerable<IntVec3>? sourceCells, IntVec3 targetOrigin, string? operationId)
        {
            SourceMap = sourceMap;
            TargetMap = targetMap;
            SourceCells = sourceCells;
            TargetOrigin = targetOrigin;
            OperationId = operationId;
        }
    }

    public sealed class ShipSurfaceTransferReport
    {
        public ShipSurfaceTransferStatus Status { get; }
        public string Message { get; }
        public int CellCount { get; }
        public int ThingCount { get; }

        // These systems have no snapshot/restore contract in this first version.
        private static readonly IReadOnlyList<string> categories = Array.AsReadOnly(["zones and storage settings", "gas", "room temperature", "power connections",
            "map and bridge bindings", "motes and projectiles",
            "effects triggered by third-party spawn callbacks"]);
        public IReadOnlyList<string> UncheckedCategories => categories;

        internal ShipSurfaceTransferReport(ShipSurfaceTransferStatus status, string message,
            int cellCount = 0, int thingCount = 0)
        {
            Status = status;
            Message = message;
            CellCount = cellCount;
            ThingCount = thingCount;
        }
    }

    /// <summary>
    /// Explicit, synchronous transfer of a selected ship region into an existing surface map.
    /// No launch or landing code calls this service. Call only on the RimWorld game thread.
    /// </summary>
    public static class ShipSurfaceTransfer
    {
        private sealed class CellSnapshot
        {
            internal IntVec3 Source;
            internal IntVec3 Target;
            internal TerrainDef SourceTerrain = null!;
            internal TerrainDef TargetTerrain = null!;
            internal RoofDef? SourceRoof;
            internal RoofDef? TargetRoof;
        }

        private sealed class ThingSnapshot
        {
            internal Thing Thing = null!;
            internal IntVec3 Source;
            internal IntVec3 Target;
            internal Rot4 Rotation;
            internal int HitPoints;
            internal bool Drafted;
        }

        private sealed class TransferPlan
        {
            internal Map SourceMap = null!;
            internal Map TargetMap = null!;
            internal ShipSurfaceTransferLedger Ledger = null!;
            internal string OperationId = null!;
            internal readonly List<CellSnapshot> Cells = new();
            internal readonly List<ThingSnapshot> Things = new();
        }

        /// <summary>Read-only preflight. TryTransfer repeats it because maps can change between calls.</summary>
        public static bool TryValidate(ShipSurfaceTransferRequest request, out ShipSurfaceTransferReport report)
        {
            return TryPrepare(request, out _, out report);
        }

        /// <summary>Transfers original Thing/Pawn instances and copies selected terrain and roofs.</summary>
        public static bool TryTransfer(ShipSurfaceTransferRequest request, out ShipSurfaceTransferReport report)
        {
            if (!TryPrepare(request, out TransferPlan plan, out report)) return false;
            if (!plan.Ledger.TryBegin(plan.OperationId))
            {
                report = new ShipSurfaceTransferReport(ShipSurfaceTransferStatus.Rejected,
                    "The transfer ID is already in progress or blocked.");
                return false;
            }

            var touched = new List<ThingSnapshot>();
            try
            {
                // Removing these roofs first avoids collapse when a supporting ship wall despawns.
                foreach (CellSnapshot cell in plan.Cells)
                    if (cell.SourceRoof != null) plan.SourceMap.roofGrid.SetRoof(cell.Source, null);

                foreach (CellSnapshot cell in plan.Cells)
                    plan.TargetMap.terrainGrid.SetTerrain(cell.Target, cell.SourceTerrain);

                // Terrain callbacks can change the destination after the read-only preflight.
                foreach (CellSnapshot cell in plan.Cells)
                    if (cell.Target.GetThingList(plan.TargetMap).Count != 0 ||
                        plan.TargetMap.roofGrid.RoofAt(cell.Target) != null)
                        throw new InvalidOperationException($"Target cell {cell.Target} changed during transfer.");

                foreach (ThingSnapshot entry in plan.Things)
                {
                    touched.Add(entry); // DeSpawn may fail after partially changing the map.
                    entry.Thing.DeSpawn(DestroyMode.Vanish);
                    Thing spawned = GenSpawn.Spawn(entry.Thing, entry.Target, plan.TargetMap,
                        entry.Rotation, WipeMode.Vanish, false);
                    if (!ReferenceEquals(spawned, entry.Thing) || !entry.Thing.Spawned ||
                        entry.Thing.Map != plan.TargetMap || entry.Thing.Position != entry.Target)
                        throw new InvalidOperationException("A Thing did not retain its identity or destination.");
                    RestoreThingProperties(entry);
                }

                foreach (CellSnapshot cell in plan.Cells)
                    if (cell.SourceRoof != null) plan.TargetMap.roofGrid.SetRoof(cell.Target, cell.SourceRoof);

                VerifyTransferred(plan);
                plan.Ledger.Complete(plan.OperationId);
                report = new ShipSurfaceTransferReport(ShipSurfaceTransferStatus.Completed,
                    "Selected terrain, roofs and persistent Thing/Pawn instances transferred.",
                    plan.Cells.Count, plan.Things.Count);
                return true;
            }
            catch (Exception error)
            {
                bool recovered = TryRollback(plan, touched, out string recoveryError);
                if (recovered) plan.Ledger.Cancel(plan.OperationId);
                else plan.Ledger.Block(plan.OperationId);
                report = new ShipSurfaceTransferReport(recovered
                        ? ShipSurfaceTransferStatus.FailedRecovered
                        : ShipSurfaceTransferStatus.FailedUnrecovered,
                    recovered ? $"Transfer failed and was restored: {error.Message}"
                        : $"Transfer failed; manual recovery is required: {error.Message}; {recoveryError}",
                    plan.Cells.Count, plan.Things.Count);
                return false;
            }
        }

        private static bool TryPrepare(ShipSurfaceTransferRequest request,
            out TransferPlan plan, out ShipSurfaceTransferReport report)
        {
            try { return PrepareCore(request, out plan, out report); }
            catch (Exception error)
            {
                plan = null!;
                report = new ShipSurfaceTransferReport(ShipSurfaceTransferStatus.Rejected,
                    "Preflight could not read the maps: " + error.Message);
                return false;
            }
        }

        private static bool PrepareCore(ShipSurfaceTransferRequest request,
            out TransferPlan plan, out ShipSurfaceTransferReport report)
        {
            plan = null!;
            if (request == null || request.SourceMap == null || request.TargetMap == null ||
                request.SourceMap == request.TargetMap || request.SourceCells == null ||
                string.IsNullOrWhiteSpace(request.OperationId) || Current.Game == null ||
                !Find.Maps.Contains(request.SourceMap) || !Find.Maps.Contains(request.TargetMap))
            {
                report = Reject("Provide two distinct loaded maps, selected cells and a stable operation ID.");
                return false;
            }

            string operationId = request.OperationId!;
            ShipSurfaceTransferLedger? ledger = Current.Game.GetComponent<ShipSurfaceTransferLedger>();
            if (ledger == null)
            {
                report = Reject("The transfer ledger is unavailable in this game.");
                return false;
            }
            if (ledger.IsCompleted(operationId))
            {
                report = new ShipSurfaceTransferReport(ShipSurfaceTransferStatus.AlreadyCompleted,
                    "This operation ID has already transferred successfully.");
                return false;
            }
            if (ledger.IsBlocked(operationId))
            {
                report = Reject("A previous attempt with this ID needs manual recovery.");
                return false;
            }

            Map source = request.SourceMap;
            Map target = request.TargetMap;
            if (!ShipSurfaceTransferLayout.TryCreate(request.SourceCells,
                new IntVec2(source.Size.x, source.Size.z), new IntVec2(target.Size.x, target.Size.z),
                request.TargetOrigin, out ShipSurfaceTransferLayout layout, out string layoutError))
            {
                report = Reject(layoutError);
                return false;
            }

            var result = new TransferPlan
            {
                SourceMap = source,
                TargetMap = target,
                Ledger = ledger,
                OperationId = operationId
            };
            var sourceThings = new HashSet<Thing>();
            foreach (IntVec3 sourceCell in layout.SourceCells)
            {
                IntVec3 targetCell = layout.MapCell(sourceCell);
                List<Thing> destinationThings = targetCell.GetThingList(target);
                if (destinationThings.Count != 0 || target.roofGrid.RoofAt(targetCell) != null)
                {
                    report = Reject($"Target cell {targetCell} contains a Thing or roof.");
                    return false;
                }

                result.Cells.Add(new CellSnapshot
                {
                    Source = sourceCell,
                    Target = targetCell,
                    SourceTerrain = source.terrainGrid.TerrainAt(sourceCell),
                    TargetTerrain = target.terrainGrid.TerrainAt(targetCell),
                    SourceRoof = source.roofGrid.RoofAt(sourceCell),
                    TargetRoof = target.roofGrid.RoofAt(targetCell)
                });

                foreach (Thing thing in sourceCell.GetThingList(source))
                {
                    if (thing.def.category == ThingCategory.Mote || thing.def.category == ThingCategory.Projectile)
                        continue; // Time-dependent visual/combat objects are outside this API's contract.
                    sourceThings.Add(thing);
                }
            }

            foreach (Thing thing in sourceThings)
            {
                if (!thing.Spawned || thing.Map != source || !layout.ContainsFootprint(thing.OccupiedRect()))
                {
                    report = Reject($"Thing {thing} extends outside the selected ship region.");
                    return false;
                }
                result.Things.Add(new ThingSnapshot
                {
                    Thing = thing,
                    Source = thing.Position,
                    Target = layout.MapCell(thing.Position),
                    Rotation = thing.Rotation,
                    HitPoints = thing.def.useHitPoints ? thing.HitPoints : 0,
                    Drafted = thing is Pawn pawn && pawn.drafter != null && pawn.drafter.Drafted
                });
            }

            // GenSpawn can merge two stackable instances in one cell. Keep identity guaranteed.
            if (result.Things.Where(t => t.Thing.def.stackLimit > 1)
                .GroupBy(t => (t.Target, t.Thing.def)).Any(g => g.Count() > 1))
            {
                report = Reject("Two stackable Things of the same type map to one cell.");
                return false;
            }

            result.Things.Sort((a, b) =>
            {
                int category = TransferOrder(a.Thing).CompareTo(TransferOrder(b.Thing));
                if (category != 0) return category;
                int z = a.Source.z.CompareTo(b.Source.z);
                if (z != 0) return z;
                int x = a.Source.x.CompareTo(b.Source.x);
                return x != 0 ? x : a.Thing.thingIDNumber.CompareTo(b.Thing.thingIDNumber);
            });

            plan = result;
            report = new ShipSurfaceTransferReport(ShipSurfaceTransferStatus.Ready,
                "The selected region can be transferred.", result.Cells.Count, result.Things.Count);
            return true;
        }

        private static int TransferOrder(Thing thing) => thing is Pawn ? 2
            : thing.def.category == ThingCategory.Building ? 0 : 1;

        private static ShipSurfaceTransferReport Reject(string message) =>
            new(ShipSurfaceTransferStatus.Rejected, message);

        private static void RestoreThingProperties(ThingSnapshot entry)
        {
            entry.Thing.Rotation = entry.Rotation;
            if (entry.Thing.def.useHitPoints) entry.Thing.HitPoints = entry.HitPoints;
            if (entry.Thing is Pawn pawn && pawn.drafter != null)
                pawn.drafter.Drafted = entry.Drafted;
        }

        private static void VerifyTransferred(TransferPlan plan)
        {
            foreach (ThingSnapshot entry in plan.Things)
                if (entry.Thing.Destroyed || !entry.Thing.Spawned || entry.Thing.Map != plan.TargetMap ||
                    entry.Thing.Position != entry.Target)
                    throw new InvalidOperationException($"Thing {entry.Thing} was not retained on the target map.");
            foreach (CellSnapshot cell in plan.Cells)
                if (plan.TargetMap.terrainGrid.TerrainAt(cell.Target) != cell.SourceTerrain ||
                    plan.TargetMap.roofGrid.RoofAt(cell.Target) != cell.SourceRoof)
                    throw new InvalidOperationException($"Terrain or roof differs at {cell.Target}.");
        }

        private static bool TryRollback(TransferPlan plan, List<ThingSnapshot> touched, out string error)
        {
            var errors = new List<string>();
            // Remove the new roofs before restoring buildings underneath them.
            foreach (CellSnapshot cell in plan.Cells)
                try { plan.TargetMap.roofGrid.SetRoof(cell.Target, null); }
                catch (Exception e) { errors.Add("target roof: " + e.Message); }

            for (int i = touched.Count - 1; i >= 0; i--)
            {
                ThingSnapshot entry = touched[i];
                try
                {
                    if (entry.Thing.Destroyed) throw new InvalidOperationException("Thing was destroyed.");
                    if (entry.Thing.Spawned && entry.Thing.Map == plan.TargetMap)
                        entry.Thing.DeSpawn(DestroyMode.Vanish);
                    if (!entry.Thing.Spawned)
                        GenSpawn.Spawn(entry.Thing, entry.Source, plan.SourceMap, entry.Rotation,
                            WipeMode.Vanish, false);
                    RestoreThingProperties(entry);
                    if (!entry.Thing.Spawned || entry.Thing.Map != plan.SourceMap ||
                        entry.Thing.Position != entry.Source)
                        throw new InvalidOperationException("Thing did not return to its source cell.");
                }
                catch (Exception e) { errors.Add($"Thing {entry.Thing}: {e.Message}"); }
            }

            foreach (CellSnapshot cell in plan.Cells)
            {
                try { plan.TargetMap.terrainGrid.SetTerrain(cell.Target, cell.TargetTerrain); }
                catch (Exception e) { errors.Add("target terrain: " + e.Message); }
                try { plan.TargetMap.roofGrid.SetRoof(cell.Target, cell.TargetRoof); }
                catch (Exception e) { errors.Add("target roof: " + e.Message); }
                try { plan.SourceMap.roofGrid.SetRoof(cell.Source, cell.SourceRoof); }
                catch (Exception e) { errors.Add("source roof: " + e.Message); }
            }

            foreach (ThingSnapshot entry in plan.Things)
                try
                {
                    if (entry.Thing.Destroyed || !entry.Thing.Spawned || entry.Thing.Map != plan.SourceMap ||
                        entry.Thing.Position != entry.Source)
                        errors.Add($"Thing {entry.Thing} is still missing from its source cell.");
                }
                catch (Exception e) { errors.Add("Thing recovery verification: " + e.Message); }
            foreach (CellSnapshot cell in plan.Cells)
            {
                try
                {
                    if (plan.TargetMap.terrainGrid.TerrainAt(cell.Target) != cell.TargetTerrain ||
                        plan.TargetMap.roofGrid.RoofAt(cell.Target) != cell.TargetRoof ||
                        cell.Target.GetThingList(plan.TargetMap).Count != 0 ||
                        plan.SourceMap.roofGrid.RoofAt(cell.Source) != cell.SourceRoof)
                        errors.Add($"Map cell {cell.Source}/{cell.Target} did not return to its original state.");
                }
                catch (Exception e) { errors.Add("Map recovery verification: " + e.Message); }
            }

            error = string.Join("; ", errors);
            return errors.Count == 0;
        }
    }
}
