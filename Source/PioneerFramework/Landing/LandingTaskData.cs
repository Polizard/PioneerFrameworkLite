using System;
using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Landing
{
    public enum LandingTransitStatus { InFlight, PreparingDestination, AwaitingPlacement, Transferring, Completing, Completed, FailedRecoverable, Blocked }
    public enum LandingMapStatus { Pending, Transferred, Cleaned }

    public sealed class LandingReport
    {
        public bool Success { get; }
        public string ReasonKey { get; }
        public object[] Arguments { get; }
        public LandingReport(bool success, string reasonKey = "", params object[] arguments)
        { Success = success; ReasonKey = reasonKey; Arguments = arguments; }
        public string Text => string.IsNullOrEmpty(ReasonKey) ? "" : ReasonKey.Translate(Arguments).ToString();
        public static LandingReport Ok() => new LandingReport(true);
        public static LandingReport Reject(string key, params object[] args) => new LandingReport(false, key, args);
    }

    public sealed class LandingSourceMapData : IExposable
    {
        public string SourceKey = Guid.NewGuid().ToString("N");
        public List<int> EntityIds = new List<int>();
        public int MapId = -1;
        public List<IntVec3> SourceCells = new List<IntVec3>();
        public IntVec3 TargetOrigin;
        public LandingMapStatus Status;
        public string OperationId(string taskId) => $"landing:{taskId}:{SourceKey}";
        public void ExposeData()
        {
            Scribe_Values.Look(ref SourceKey, "sourceKey"); Scribe_Values.Look(ref MapId, "mapId", -1);
            Scribe_Collections.Look(ref EntityIds, "entityIds", LookMode.Value);
            Scribe_Collections.Look(ref SourceCells, "sourceCells", LookMode.Value);
            Scribe_Values.Look(ref TargetOrigin, "targetOrigin"); Scribe_Values.Look(ref Status, "status");
        }
    }

    public sealed class LandingSuspensionData : IExposable
    {
        public bool IsActive, IsMovable, IsCollidable;
        [Unsaved] internal object? StartupManeuverTracker;
        public void ExposeData()
        { Scribe_Values.Look(ref IsActive, "active"); Scribe_Values.Look(ref IsMovable, "movable"); Scribe_Values.Look(ref IsCollidable, "collidable"); }
    }

    public sealed class LandingFuelDebit : IExposable
    {
        public int EntityId = -1, MapId = -1, EntryIndex = -1;
        public string PropellantDefName = "";
        public double Before, After;
        public List<string> TankIds = new List<string>();
        public List<float> TankMasses = new List<float>();
        public void ExposeData()
        {
            Scribe_Values.Look(ref EntityId, "entityId", -1); Scribe_Values.Look(ref MapId, "mapId", -1);
            Scribe_Values.Look(ref EntryIndex, "entryIndex", -1); Scribe_Values.Look(ref PropellantDefName, "propellant");
            Scribe_Values.Look(ref Before, "before"); Scribe_Values.Look(ref After, "after");
            Scribe_Collections.Look(ref TankIds, "tankIds", LookMode.Value); Scribe_Collections.Look(ref TankMasses, "tankMasses", LookMode.Value);
        }
    }
    public sealed class LandingFuelReceipt : IExposable
    {
        public bool Committed;
        public double QuotedDeltaV, ConsumedKg;
        public List<LandingFuelDebit> Debits = new List<LandingFuelDebit>();
        public void ExposeData()
        {
            Scribe_Values.Look(ref Committed, "committed"); Scribe_Values.Look(ref QuotedDeltaV, "deltaV");
            Scribe_Values.Look(ref ConsumedKg, "consumedKg"); Scribe_Collections.Look(ref Debits, "debits", LookMode.Deep);
        }
    }
    public sealed class LandingCleanupProgress : IExposable
    {
        public bool Notified, SpaceRemoved;
        public void ExposeData() { Scribe_Values.Look(ref Notified, "notified"); Scribe_Values.Look(ref SpaceRemoved, "spaceRemoved"); }
    }
    public sealed class LandingVirtualPropulsionData : IExposable
    {
        public bool HasVirtualPropulsion;
        public string PropellantDefName = "";
        public float Ve, MassFlow, FuelCapacity;
        public double FuelMass;
        public void ExposeData()
        {
            Scribe_Values.Look(ref HasVirtualPropulsion, "hasVirtual"); Scribe_Values.Look(ref PropellantDefName, "propellant");
            Scribe_Values.Look(ref Ve, "ve"); Scribe_Values.Look(ref MassFlow, "massFlow");
            Scribe_Values.Look(ref FuelMass, "fuelMass"); Scribe_Values.Look(ref FuelCapacity, "capacity");
        }
    }

    public sealed class LandingTaskData : IExposable, ILoadReferenceable
    {
        public string TaskId = Guid.NewGuid().ToString("N");
        public int GroupId, TargetBodyId, StartTick, DurationTicks = 180;
        public int DestinationParentId = -1, DestinationMapId = -1, RecoverySiteId = -1;
        public List<int> EntityIds = new List<int>(), MaplessEntityIds = new List<int>();
        public PlanetTile TargetTile = PlanetTile.Invalid;
        public SystemVec3 DepartureCenter, StartPosition;
        public LandingTransitStatus Status, ResumeStage;
        public LandingFuelReceipt FuelReceipt = new LandingFuelReceipt();
        public LandingSuspensionData Suspension = new LandingSuspensionData();
        public List<LandingSourceMapData> SourceMaps = new List<LandingSourceMapData>();
        public LandingCleanupProgress Cleanup = new LandingCleanupProgress();
        public string FailureReason = "";
        public string GetUniqueLoadID() => "PFL_Landing_" + TaskId;
        public bool TryTransition(LandingTransitStatus next, out string reasonKey)
        {
            reasonKey = "";
            if (Status == LandingTransitStatus.Completed || Status == LandingTransitStatus.Blocked)
            { reasonKey = "PFL_LandingInvalidState"; return false; }
            bool allowed = next == LandingTransitStatus.FailedRecoverable || next == LandingTransitStatus.Blocked ||
                (Status == LandingTransitStatus.InFlight && (next == LandingTransitStatus.PreparingDestination || next == LandingTransitStatus.Completing)) ||
                (Status == LandingTransitStatus.PreparingDestination && next == LandingTransitStatus.AwaitingPlacement) ||
                (Status == LandingTransitStatus.AwaitingPlacement && next == LandingTransitStatus.Transferring) ||
                (Status == LandingTransitStatus.Transferring && (next == LandingTransitStatus.AwaitingPlacement || next == LandingTransitStatus.Completing)) ||
                (Status == LandingTransitStatus.Completing && next == LandingTransitStatus.Completed) ||
                (Status == LandingTransitStatus.FailedRecoverable && next == ResumeStage);
            if (!allowed) { reasonKey = "PFL_LandingInvalidState"; return false; }
            Status = next; return true;
        }
        public void ExposeData()
        {
            Scribe_Values.Look(ref TaskId, "taskId"); Scribe_Values.Look(ref GroupId, "groupId");
            Scribe_Values.Look(ref TargetBodyId, "bodyId"); Scribe_Values.Look(ref TargetTile, "targetTile");
            Scribe_Values.Look(ref StartTick, "startTick"); Scribe_Values.Look(ref DurationTicks, "duration", 180);
            Scribe_Values.Look(ref DestinationParentId, "destinationParent", -1); Scribe_Values.Look(ref DestinationMapId, "destinationMap", -1);
            Scribe_Values.Look(ref RecoverySiteId, "recoverySite", -1);
            Scribe_Collections.Look(ref EntityIds, "entities", LookMode.Value); Scribe_Collections.Look(ref MaplessEntityIds, "maplessEntities", LookMode.Value);
            SystemVec3.Scribe("departureCenter", ref DepartureCenter); SystemVec3.Scribe("startPosition", ref StartPosition);
            Scribe_Values.Look(ref Status, "status"); Scribe_Values.Look(ref ResumeStage, "resumeStage");
            Scribe_Deep.Look(ref FuelReceipt, "fuelReceipt"); Scribe_Deep.Look(ref Suspension, "suspension");
            Scribe_Collections.Look(ref SourceMaps, "sourceMaps", LookMode.Deep); Scribe_Deep.Look(ref Cleanup, "cleanup");
            Scribe_Values.Look(ref FailureReason, "failure");
        }
    }
}
