using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PioneerFramework.SpaceAI
{
    public enum SpaceEncounterStatus { Preparing, Active, Completed, Failed }
    public sealed class SpaceEncounterRecord : IExposable
    {
        public int id, factionId, targetGroupId, requestedCount, fleetId;
        public string defName = string.Empty;
        public SpaceEncounterStatus status;
        public List<int> groupIds = new();
        public DVector3 anchor, anchorVelocity;
        public double started, nextCheck;
        public double searchLostAt = -1;
        public bool searchAbandoned;
        internal bool UpdateSearch(double now, bool objectiveObserved, bool engaged, double timeout)
        {
            if (searchAbandoned) return true;
            if (objectiveObserved || engaged) { searchLostAt = -1; return false; }
            if (searchLostAt < 0 || now < searchLostAt) searchLostAt = now;
            if (now-searchLostAt >= timeout) searchAbandoned = true;
            return searchAbandoned;
        }
        public void ExposeData()
        {
            Scribe_Values.Look(ref id,"id"); Scribe_Values.Look(ref factionId,"faction");
            Scribe_Values.Look(ref targetGroupId,"target"); Scribe_Values.Look(ref requestedCount,"count");
            Scribe_Values.Look(ref fleetId,"fleet"); Scribe_Values.Look(ref defName,"def");
            Scribe_Values.Look(ref status,"status"); Scribe_Collections.Look(ref groupIds,"groups",LookMode.Value);
            DVector3.Scribe("anchor",ref anchor); DVector3.Scribe("anchorVelocity",ref anchorVelocity);
            Scribe_Values.Look(ref started,"started");
            Scribe_Values.Look(ref searchLostAt,"searchLostAt",-1);
            Scribe_Values.Look(ref searchAbandoned,"searchAbandoned");
        }
    }
    public static class SpaceEncounterService
    {
        public static bool CanFinishWithdrawal(double now,double started,double distance,double requiredDistance,
            bool searchAbandoned = false,double withdrawalSeconds = 120) =>
            started >= 0 && now-started >= withdrawalSeconds && (distance > requiredDistance || searchAbandoned);

        public static bool TryStart(SpaceEncounterDef def, Faction faction, int targetGroupId, int count, out int encounterId)
        {
            encounterId = 0;
            var game = Current.Game?.GetComponent<SpaceGameComp>();
            var manager = Current.Game?.GetComponent<SpaceAiManager>();
            var target = game?.GetGroupById(targetGroupId);
            if (def == null || faction == null || target == null || manager == null || !target.IsActive ||
                !target.IsMovable || !FactionResolver.IsHostile(faction.loadID,target.FactionId) || manager.HasActiveEncounter) return false;
            foreach (string error in def.ConfigErrors()) { Log.Error("[SpaceAI] Invalid encounter: " + error); return false; }
            foreach (var template in def.templates) if (template == null || !template.TryValidate(out _)) return false;
            encounterId = manager.QueueEncounter(def,faction,target,count);
            return true;
        }

        public static SpaceEntityGroup? FindPlayerShip()
        {
            var game = Current.Game?.GetComponent<SpaceGameComp>();
            if (game == null || Faction.OfPlayer == null) return null;
            SpaceEntityGroup? found = null;
            foreach (var g in game.GetAllGroups())
                if (g.IsActive && g.IsMovable && g.FactionId == Faction.OfPlayer.loadID && string.IsNullOrEmpty(g.LandingTaskId) &&
                    Current.Game.GetComponent<AutonomousVehicleManager>()?.GetRecord(g.GroupId) == null &&
                    (found == null || g.GroupId < found.GroupId)) found = g;
            return found;
        }
    }
}
