using System;
using System.Collections.Generic;
using System.Diagnostics;
using RimWorld;
using Verse;

namespace PioneerFramework.SpaceAI
{
    /// <summary>Main-thread encounter controller, driven explicitly after sensing and combat.</summary>
    public partial class SpaceAiManager : GameComponent
    {
        private List<AiShipRecord> ships = new();
        private int nextFleetId = 1, planningCursor;
        private double nextTrackPrune;
        private readonly AiTrackRegistry tracks = new();
        private sealed class FactionSnapshot
        {
            internal double at = double.NegativeInfinity;
            internal readonly List<Contact> contacts = new();
        }
        private readonly Dictionary<int,FactionSnapshot> pictures = new();
        private readonly Dictionary<int, AiFleetCoordinator> coordinators = new();
        private readonly Dictionary<int,double> fleetSnapshotAt = new();
        private readonly List<AiFleetMember> fleetMembers = new(32);
        private readonly GameComponentRef<SpaceGameComp> spaceRef = new();
        private readonly GameComponentRef<SensorManager> sensorRef = new();
        private readonly GameComponentRef<Combat.CombatManager> combatRef = new();
        public IReadOnlyList<AiShipRecord> Ships => ships;
        public AiMetrics Metrics { get; } = new();
        public SpaceAiManager(Game game) { }

        public bool TryRegisterFleet(int faction, IReadOnlyList<int> ids, out int fleetId, int knownTarget = -1)
        {
            fleetId = 0;
            if (faction < 0 || ids == null || ids.Count == 0 || ids.Count > 32) return false;
            var unique = new HashSet<int>();
            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i] <= 0 || !unique.Add(ids[i])) return false;
                for (int j = 0; j < ships.Count; j++) if (ships[j].groupId == ids[i]) return false;
            }
            fleetId = nextFleetId++;
            for (int i = 0; i < ids.Count; i++)
                ships.Add(new AiShipRecord {groupId = ids[i], factionId = faction, fleetId = fleetId,
                    knownTargetGroupId = knownTarget, nextDecision = i * .15});
            ships.Sort((a, b) => a.groupId.CompareTo(b.groupId));
            return true;
        }

        public void UnregisterGroup(int groupId)
        {
            for (int i = ships.Count - 1; i >= 0; i--)
            {
                var record = ships[i];
                if (record.groupId != groupId) continue;
                var group = spaceRef.Get()?.GetGroupById(groupId);
                if (group != null) StopOwnedBurn(group, record);
                else record.ownsBurn = false;
                record.planning = null;
                ships.RemoveAt(i);
                bool fleetStillActive = false;
                foreach (var remaining in ships) if (remaining.fleetId == record.fleetId) { fleetStillActive = true; break; }
                if (!fleetStillActive) { coordinators.Remove(record.fleetId); fleetSnapshotAt.Remove(record.fleetId); }
            }
        }

        public static bool CanControl(bool active, bool movable, int owner, int faction, bool landing, bool manual) =>
            active && movable && owner == faction && !landing && !manual;

        internal static bool CanControl(SpaceEntityGroup group, AiShipRecord record) => CanControl(
            group.IsActive, group.IsMovable, record.factionId, group.FactionId, !string.IsNullOrEmpty(group.LandingTaskId),
            SteeringController.GroupId == group.GroupId || group.plannedManeuvers.Count > 0 || group.rendezvousArmed || group.hoverActive);

        internal void OnPhysicsStepCompleted()
        {
            if (ships.Count == 0)
            { tracks.Clear(); coordinators.Clear(); pictures.Clear(); fleetSnapshotAt.Clear(); return; }
            long start = Stopwatch.GetTimestamp(), allocated = GC.GetAllocatedBytesForCurrentThread();
            var game = spaceRef.Get(); var sensor = sensorRef.Get(); var combat = combatRef.Get();
            if (game == null || sensor == null || combat == null) return;
            double now = TimeScaleManager.GetPhysicsTickSafe();
            for (int i = ships.Count - 1; i >= 0; i--)
            {
                var record = ships[i]; var group = game.GetGroupById(record.groupId);
                if (group == null) { ships.RemoveAt(i); continue; }
                if (now < record.activateAt) continue;
                if (record.ownsBurn && record.ownedBurnVersion != group.BurnCommandVersion) record.ownsBurn = false;
                if (!CanControl(group, record))
                {
                    StopOwnedBurn(group, record); record.planning = null;
                    if (group.FactionId != record.factionId || !group.IsActive || !string.IsNullOrEmpty(group.LandingTaskId)) ships.RemoveAt(i);
                    continue;
                }
                if (record.ownsBurn && (now >= record.burnExpires || !record.withdrawing && !ContactTargetService.TryResolve(sensor, game,
                    group.FactionId, new(record.targetGroupId, record.targetDecoyId), now, out _))) StopOwnedBurn(group, record);
                if (!AiSchedule.IsDue(now, ref record.nextDecision, 1)) continue;
                using (Metrics.Measure(AiMetricStage.Snapshot)) record.adapter.Refresh(game, combat, group, record);
                if (!record.adapter.Ready) { StopOwnedBurn(group, record); record.planning = null; continue; }
                DecideAndExecute(record, group, game, sensor, combat, now);
            }
            using (Metrics.Measure(AiMetricStage.Planning)) AdvancePlanning(game, sensor, now);
            if (AiSchedule.IsDue(now,ref nextTrackPrune,10)) tracks.Prune(now,120);
            Metrics.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated);
        }

        private void DecideAndExecute(AiShipRecord record, SpaceEntityGroup group, SpaceGameComp game,
            SensorManager sensor, Combat.CombatManager combat, double now)
        {
            var state = record.adapter.State;
            long snapshotStart = Stopwatch.GetTimestamp();
            if (!pictures.TryGetValue(record.factionId,out var picture)) pictures[record.factionId] = picture = new();
            if (picture.at != now) { picture.contacts.Clear(); sensor.AppendVisible(record.factionId,picture.contacts); picture.at = now; }
            var rawContacts = picture.contacts;
            record.contacts.Clear();
            for (int i = 0; i < rawContacts.Count; i++)
            {
                var c = rawContacts[i];
                var physical = c.targetGroupId >= 0 ? game.GetGroupById(c.targetGroupId) : null;
                if (physical != null && physical.FactionId == record.factionId) continue;
                bool hostile = (c.source == ContactSource.Public && physical != null && FactionResolver.IsHostile(physical.FactionId, record.factionId)) ||
                    c.targetGroupId == record.knownTargetGroupId && record.knownTargetGroupId >= 0;
                DVector3 delta = ContactMemory.CoastedPosition(c, now) - state.Position;
                bool threatening = delta.magnitude < Math.Max(5000, state.WeaponRange * .8) &&
                    DVector3.Dot(delta.normalized, c.velocity - state.Velocity) < -1;
                record.contacts.Add(ContactTargetService.Project(c, tracks.Remember(record.factionId, c,now), hostile, threatening));
            }
            Metrics.RecordStage(AiMetricStage.Snapshot,Stopwatch.GetElapsedTime(snapshotStart).TotalMilliseconds);
            long decisionStart = Stopwatch.GetTimestamp();
            if (!coordinators.TryGetValue(record.fleetId, out var fleet)) coordinators[record.fleetId] = fleet = new();
            fleet.Expire(now);
            if (!fleetSnapshotAt.TryGetValue(record.fleetId,out double fleetAt) || fleetAt != now)
            {
                RefreshInFlight(fleet,record.fleetId,record.factionId,game,sensor,now);
                fleetSnapshotAt[record.fleetId] = now;
            }
            AiCandidateShortlist.Fill(state,record.contacts,now,record.candidates);
            bool preferredUsable = false;
            foreach (var c in record.candidates) if (c.TrackId == record.preferredTrack) preferredUsable = true;
            if (!preferredUsable || AiSchedule.IsDue(now,ref record.nextFleetUpdate,5))
            { record.preferredTrack = fleet.SelectTarget(state,record.candidates,now); record.nextFleetUpdate = now+5; }
            int preferred = record.preferredTrack;
            var decision = AiDecisionCore.Decide(state, record.candidates, now);
            if (preferred > 0 && !record.withdrawing && decision.Action != AiAction.Retreat)
            {
                for (int i = 0; i < record.candidates.Count; i++)
                    if (record.candidates[i].TrackId == preferred)
                    {
                        var chosen = record.candidates[i]; record.candidates.Clear(); record.candidates.Add(chosen);
                        decision = AiDecisionCore.Decide(state, record.candidates, now); break;
                    }
            }
            fleetMembers.Clear();
            foreach (var member in ships)
            {
                if (member.fleetId != record.fleetId) continue;
                var telemetry = member.groupId == record.groupId ? state : member.adapter.State;
                bool withdrawing = member.withdrawing || member.groupId == record.groupId && decision.Action == AiAction.Retreat;
                bool canCover = member.adapter.Ready && telemetry.Health >= .5 && telemetry.DeltaV > telemetry.ReserveDeltaV &&
                    (telemetry.HasJammer || telemetry.HasWeapons && telemetry.AmmoFraction > .1);
                fleetMembers.Add(new(member.groupId,member.role,withdrawing,canCover,member.retreatStarted));
            }
            var withdrawal = fleet.WithdrawalOrder(record.groupId,fleetMembers,now);
            if (withdrawal.Withdraw) decision = decision with {Action = AiAction.Retreat,Fire = false,ActiveRadar = false};
            if (withdrawal.Cover && decision.TrackId > 0) decision = decision with {Jamming = state.HasJammer};
            if (record.withdrawing) decision = decision with {Action = AiAction.Retreat, Fire = false, ActiveRadar = false};
            if (record.action != decision.Action)
            {
                if (now < record.actionUntil && record.action is AiAction.Engage or AiAction.Approach &&
                    decision.Action is AiAction.Engage or AiAction.Approach) decision = decision with {Action = record.action};
                else { record.nextPlan = 0; record.actionUntil = now+3; }
            }
            record.action = decision.Action; record.decision = decision;
            if (decision.Action != AiAction.Approach || record.withdrawing) record.planning = null;
            Metrics.RecordStage(AiMetricStage.Decision,Stopwatch.GetElapsedTime(decisionStart).TotalMilliseconds);
            using var submissionTime = Metrics.Measure(AiMetricStage.Submission);
            if (decision.Action == AiAction.Retreat)
            { record.withdrawing = true; if (record.retreatStarted < 0) record.retreatStarted = now; }
            group.ActivePingEnabled = decision.ActiveRadar;
            group.JammingEnabled = decision.Jamming;
            if (decision.EmitDecoy) record.adapter.EmitDecoy();
            if (decision.TrackId == 0) group.RadarAimMode = RadarAim.Scan;
            if (decision.TrackId == 0 || !tracks.TryGet(record.factionId, decision.TrackId, out var target))
            {
                if (record.withdrawing && AiSchedule.IsDue(now,ref record.nextPlan,15))
                {
                    var direction = record.retreatDirection;
                    if (direction.sqrMagnitude < .1) direction = state.Velocity.normalized;
                    ApplyBurn(record,group,game,direction*Math.Min(25,Math.Max(0,state.DeltaV)),now);
                }
                return;
            }
            record.targetGroupId = target.GroupId; record.targetDecoyId = target.DecoyId;
            if (!ContactTargetService.TryResolve(sensor, game, group.FactionId, target, now, out var contact)) return;
            record.retreatDirection = (state.Position-ContactMemory.CoastedPosition(contact,now)).normalized;
            if (decision.ActiveRadar)
            {
                group.RadarAimMode = RadarAim.Manual;
                group.RadarManualDir = ContactMemory.CoastedPosition(contact, now) - state.Position;
            }
            if (decision.Fire && !contact.isDecoy)
            {
                int fired = record.adapter.Fire(game,sensor,combat,group,target,2,fleet.MissileWindowOpen(now));
                if (fired > 0) fleet.Reserve(decision.TrackId,fired,now+10);
            }
            if (withdrawal.Cover)
            { record.planning = null; StopOwnedBurn(group,record); return; }
            if (!AiSchedule.IsDue(now, ref record.nextPlan, decision.Action == AiAction.Engage ? 5 : 15)) return;
            if (decision.Action is AiAction.Investigate or AiAction.Search) return;
            var aiContact = ContactTargetService.Project(contact, decision.TrackId, true, false);
            if (decision.Action == AiAction.Approach && (aiContact.PositionAt(now) - state.Position).magnitude > Math.Max(50000, state.WeaponRange * 3)
                && !group.HasActiveBurn && record.planning == null)
            {
                var bodies = CelestialBodyManager.GetCached();
                var body = bodies?.GetBodyById(group.CelestialBodyId);
                if (body != null && OrbitAdapter.TryToOrbitView(group, bodies, now, out var orbit))
                {
                    record.planningCelestialTarget = null;
                    if (bodies!.TryGetCelestialRuntime(out _))
                    {
                        var celestialTarget = bodies.CaptureTarget(body.bodyId);
                        if (!celestialTarget.Success) return;
                        record.planningCelestialTarget = celestialTarget.Value;
                    }
                    var own = orbit.GameFrameStateAtUT(now);
                    var targetPosition = FrameConvert.SystemToBody((SystemVec3)aiContact.PositionAt(now), group.GetCentralBodyOffset(now)).v;
                    var targetVelocity = FrameConvert.SystemToBody((SystemVec3)aiContact.Velocity, group.GetCentralBodyVelocity(now)).v;
                    record.planning = new(own.pos, own.vel, targetPosition, targetVelocity, body.gravitationalParameter,
                        body.radius + body.atmosphereThickness, Math.Max(0, state.DeltaV - state.ReserveDeltaV));
                    record.planningEpoch = now; record.planningTrack = decision.TrackId;
                    record.planningBody = group.CelestialBodyId; record.planningState = state;
                    return;
                }
            }
            DVector3 dv = AiPlanningCore.Plan(state, aiContact, decision.Action, now);
            ApplyBurn(record, group, game, dv, now);
        }

        private static void StopOwnedBurn(SpaceEntityGroup group, AiShipRecord record)
            => TryReleaseBurn(group,record);

        public static bool TryReleaseBurn(SpaceEntityGroup group,AiShipRecord record)
        {
            bool owned = record.ownsBurn && record.ownedBurnVersion == group.BurnCommandVersion;
            if (owned && group.HasActiveBurn) group.EndBurn();
            record.ownsBurn = false; return owned;
        }

        private static void ApplyBurn(AiShipRecord record, SpaceEntityGroup group, SpaceGameComp game, DVector3 dv, double now)
        {
            if (group.HasActiveBurn && !record.ownsBurn || dv.magnitude <= .1) return;
            var bodies = CelestialBodyManager.GetCached(); var body = bodies?.GetBodyById(group.CelestialBodyId);
            if (body != null)
            {
                double scale = RendezvousCore.MaxSafeDvScale(group.GetBodyPosition(now).v, group.GetBodyVelocity(now).v,
                    dv, body.radius + body.atmosphereThickness, body.gravitationalParameter);
                dv *= scale;
            }
            if (dv.magnitude <= .1) return;
            if (group.IsCircularOrbit) group.ExitCircularOrbit();
            StopOwnedBurn(group, record);
            group.BeginBurn(new ManeuverPlan { dv = dv, networks = ManeuverNetworkMode.All });
            record.ownsBurn = group.HasActiveBurn;
            record.ownedBurnVersion = group.BurnCommandVersion;
            record.burnExpires = now + Math.Max(30, dv.magnitude / Math.Max(.01, game.EstimateMaxAcceleration(group)) +
                Math.PI / Math.Max(.001, group.maxAngularRate));
        }

        private void AdvancePlanning(SpaceGameComp game, SensorManager sensor, double now)
        {
            if (ships.Count == 0) return;
            for (int n = 0; n < ships.Count; n++)
            {
                planningCursor = (planningCursor + 1) % ships.Count;
                var record = ships[planningCursor]; var job = record.planning;
                if (job == null) continue;
                var group = game.GetGroupById(record.groupId);
                var combat = combatRef.Get();
                if (group == null || combat == null || record.action != AiAction.Approach || record.withdrawing ||
                    !CanControl(group, record) || record.planningBody != group.CelestialBodyId ||
                    record.planningCelestialTarget != null && CelestialBodyManager.GetCached()?.ValidateTarget(record.planningCelestialTarget).Success != true ||
                    now < record.planningEpoch || now - record.planningEpoch >= 15 ||
                    record.decision.TrackId != record.planningTrack || !ContactTargetService.TryResolve(sensor, game,
                        record.factionId, new(record.targetGroupId, record.targetDecoyId), now, out _))
                { record.planning = null; continue; }
                // 提交前重新读取真实地图/设备/燃料，不能继续使用上次决定时的缓存。
                record.adapter.Refresh(game, combat, group, record);
                var state = record.adapter.State;
                if (!record.adapter.Ready || !PlanningResourcesValid(record.planningState, state))
                { record.planning = null; continue; }
                job.Step();
                if (!job.Completed && !job.HasSolution) return;
                record.planning = null;
                if (job.HasSolution && job.TotalDeltaV <= state.DeltaV-state.ReserveDeltaV)
                    ApplyBurn(record, group, game, job.DeltaV, now);
                return;
            }
        }

        internal static bool PlanningResourcesValid(in AiShipState planned, in AiShipState current) =>
            current.DeltaV > current.ReserveDeltaV && current.Acceleration > 0 &&
            current.Acceleration >= planned.Acceleration*.75 && current.Health >= planned.Health-.1 &&
            current.AmmoFraction >= planned.AmmoFraction && current.WeaponRange >= planned.WeaponRange*.9;

        internal void RefreshInFlight(AiFleetCoordinator fleet,int fleetId,int faction,SpaceGameComp game,SensorManager sensor,double now)
        {
            fleet.ClearInFlight();
            var vehicles = Current.Game?.GetComponent<AutonomousVehicleManager>()?.Vehicles;
            if (vehicles == null) return;
            foreach (var vehicle in vehicles)
            {
                if (vehicle.mission != AutonomousVehicleManager.MissionType.Attack) continue;
                bool belongs = false;
                foreach (var ship in ships)
                    if (ship.fleetId == fleetId && ship.factionId == faction && ship.groupId == vehicle.launcherGroupId)
                    { belongs = true; break; }
                if (!belongs) continue;
                var missile = game.GetGroupById(vehicle.groupId);
                if (missile == null || !missile.IsActive || missile.FactionId != faction ||
                    !ContactTargetService.TryResolve(sensor,game,faction,ContactTargetService.CopyTarget(vehicle),now,out var contact) ||
                    contact.isDecoy) continue;
                fleet.SetInFlight(tracks.Remember(faction,contact,now),Math.Max(1,vehicle.logicalCount));
            }
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref ships, "spaceAiShips", LookMode.Deep);
            Scribe_Values.Look(ref nextFleetId, "spaceAiNextFleet", 1);
            Scribe_Collections.Look(ref encounters, "spaceAiEncounters", LookMode.Deep);
            Scribe_Values.Look(ref nextEncounterId, "spaceAiNextEncounter", 1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                ships ??= new(); ships.Sort((a, b) => a.groupId.CompareTo(b.groupId));
                encounters ??= new(); generationQueued = false;
                tracks.Clear(); coordinators.Clear(); pictures.Clear(); fleetSnapshotAt.Clear();
                foreach (var record in ships) { record.nextDecision = 0; record.nextPlan = 0; }
            }
        }
    }
}
