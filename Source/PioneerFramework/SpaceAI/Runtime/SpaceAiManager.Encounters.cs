using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PioneerFramework.SpaceAI
{
    public partial class SpaceAiManager
    {
        private List<SpaceEncounterRecord> encounters = new();
        private int nextEncounterId = 1;
        private bool generationQueued;
        public IReadOnlyList<SpaceEncounterRecord> Encounters => encounters;
        public bool HasActiveEncounter
        {
            get { foreach (var entry in encounters) if (entry.status is SpaceEncounterStatus.Preparing or SpaceEncounterStatus.Active) return true; return false; }
        }
        internal int QueueEncounter(SpaceEncounterDef def,Faction faction,SpaceEntityGroup target,int count)
        {
            var combat = combatRef.Get();
            var entry = new SpaceEncounterRecord {id = nextEncounterId++,defName = def.defName,factionId = faction.loadID,
                targetGroupId = target.GroupId,requestedCount = def.ClampCount(count),anchor = combat.GetGroupPos(target),
                anchorVelocity = combat.GetGroupVelocity(target),started = TimeScaleManager.GetPhysicsTickSafe()};
            encounters.Add(entry); return entry.id;
        }

        public override void GameComponentTick()
        {
            var game = spaceRef.Get(); if (game == null || encounters.Count == 0) return;
            double now = TimeScaleManager.GetPhysicsTickSafe();
            foreach (var entry in encounters)
            {
                if (entry.status == SpaceEncounterStatus.Preparing && !generationQueued)
                {
                    generationQueued = true;
                    var session = Current.Game;
                    LongEventHandler.QueueLongEvent(() =>
                    {
                        try { if (ReferenceEquals(session,Current.Game)) BuildNextShip(entry,game); }
                        finally { generationQueued = false; }
                    },"GeneratingMap",false,e => { generationQueued = false; FailEncounter(entry,game,e); });
                    break;
                }
                if (entry.status != SpaceEncounterStatus.Active || !AiSchedule.IsDue(now,ref entry.nextCheck,5)) continue;
                var def = DefDatabase<SpaceEncounterDef>.GetNamedSilentFail(entry.defName);
                var objective = game.GetGroupById(entry.targetGroupId);
                var sensor = sensorRef.Get();
                bool observed = objective != null && sensor != null &&
                    sensor.TryGetKnownState(entry.factionId, objective, out _);
                bool engaged = false, preparing = false;
                foreach (var candidate in ships)
                {
                    if (candidate.fleetId != entry.fleetId) continue;
                    preparing |= now < candidate.activateAt;
                    engaged |= candidate.action is AiAction.Approach or AiAction.Engage && !candidate.withdrawing &&
                        ContactTargetService.TryResolve(sensor, game, entry.factionId,
                            new(candidate.targetGroupId,candidate.targetDecoyId),now,out var contact) && !contact.isDecoy;
                }
                bool abandon = !preparing && entry.UpdateSearch(now,observed,engaged,def?.searchAbandonSeconds ?? 180);
                int remaining = 0;
                foreach (int id in entry.groupIds)
                {
                    var group = game.GetGroupById(id); if (group == null) continue;
                    AiShipRecord? record = null;
                    foreach (var candidate in ships) if (candidate.groupId == id) { record = candidate; break; }
                    if (record == null) continue;
                    if (abandon || objective == null || objective.FactionId == entry.factionId)
                    {
                        if (!record.withdrawing) StopOwnedBurn(group,record);
                        record.withdrawing = true; record.planning = null;
                        if (record.retreatStarted < 0) record.retreatStarted = now;
                    }
                    double distance = (combatRef.Get().GetGroupPos(group) - (entry.anchor+entry.anchorVelocity*(now-entry.started))).magnitude;
                    bool visible = sensorRef.Get()?.IsVisibleTo(Faction.OfPlayer.loadID,group) ?? true;
                    if (record.withdrawing && !visible && CanFinish(entry,record,def,now,distance))
                    { StopOwnedBurn(group,record); UnregisterGroup(id); }
                    else remaining++;
                }
                if (remaining == 0) entry.status = SpaceEncounterStatus.Completed;
            }
        }

        private static bool CanFinish(SpaceEncounterRecord entry,AiShipRecord record,SpaceEncounterDef? def,double now,double distance) =>
            SpaceEncounterService.CanFinishWithdrawal(now,record.retreatStarted,distance,def?.withdrawalDistance ?? 150000,
                entry.searchAbandoned,def?.withdrawalSeconds ?? 120);

        private void BuildNextShip(SpaceEncounterRecord entry,SpaceGameComp game)
        {
            using var generationTime = Metrics.Measure(AiMetricStage.Generation);
            try
            {
                var def = DefDatabase<SpaceEncounterDef>.GetNamed(entry.defName);
                Faction? faction = null;
                foreach (var currentFaction in Find.FactionManager.AllFactions)
                    if (currentFaction.loadID == entry.factionId) { faction = currentFaction; break; }
                if (faction == null) throw new InvalidOperationException("Encounter faction missing");
                var target = game.GetGroupById(entry.targetGroupId) ?? throw new InvalidOperationException("Encounter target missing");
                var source = SpaceMapGenerator.GetAnchorMap() ?? throw new InvalidOperationException("No source map");
                int index = entry.groupIds.Count;
                var template = def.templates[index % def.templates.Count];
                double now = TimeScaleManager.GetPhysicsTickSafe();
                var body = CelestialBodyManager.GetCached()?.GetBodyById(target.CelestialBodyId) ??
                    throw new InvalidOperationException("Encounter central body missing");
                var state = target.GetBodyState(now);
                if (!AiSpawnOrbitCore.TryCreate(state.pos.v,state.vel.v,def.spawnDistance,
                    (index-(entry.requestedCount-1)*.5)*def.shipSeparation,body.gravitationalParameter,
                    body.radius+body.atmosphereThickness+10000,out var spawnPosition,out var spawnVelocity))
                    throw new InvalidOperationException("No safe encounter spawn orbit");
                var position = FrameConvert.BodyToSystem((BodyVec3)spawnPosition,target.GetCentralBodyOffset(now));
                var velocity = FrameConvert.BodyToSystemVelocity((BodyVec3)spawnVelocity,target.GetCentralBodyVelocity((int)Math.Round(now)));
                var group = EncounterShipFactory.Create(template,faction,source,position,velocity,target.CelestialBodyId);
                entry.groupIds.Add(group.GroupId);
                if (entry.groupIds.Count < entry.requestedCount) return;
                if (!TryRegisterFleet(faction.loadID,entry.groupIds,out entry.fleetId,entry.targetGroupId))
                    throw new InvalidOperationException("Fleet registration failed");
                for (int i = 0; i < entry.groupIds.Count; i++)
                    foreach (var record in ships) if (record.groupId == entry.groupIds[i])
                    {
                        record.role = def.templates[i % def.templates.Count].role;
                        record.activateAt = TimeScaleManager.GetPhysicsTickSafe()+def.preparationSeconds;
                        record.nextDecision = record.activateAt+i*.15;
                    }
                entry.status = SpaceEncounterStatus.Active;
                Find.LetterStack.ReceiveLetter("PF_AI_EncounterLabel".Translate(),"PF_AI_EncounterText".Translate(),LetterDefOf.ThreatBig);
            }
            catch (Exception e) { FailEncounter(entry,game,e); }
        }
        private void FailEncounter(SpaceEncounterRecord entry,SpaceGameComp game,Exception error)
        {
            using var rollback = new EncounterBuildTransaction(id => { UnregisterGroup(id); game.DestroyGroup(id,"Encounter rollback"); });
            foreach (int id in entry.groupIds) rollback.Add(id);
            entry.status = SpaceEncounterStatus.Failed;
            Log.Error("[SpaceAI] Encounter generation failed: " + error);
        }
    }
}
