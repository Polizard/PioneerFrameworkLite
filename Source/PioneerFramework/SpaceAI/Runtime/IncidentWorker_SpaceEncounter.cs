using System;
using RimWorld;
using Verse;

namespace PioneerFramework.SpaceAI
{
    /// <summary>Opt-in storyteller integration. Existing fleets keep their AI when the setting is disabled.</summary>
    public sealed class IncidentWorker_SpaceEncounter : IncidentWorker
    {
        protected override bool CanFireNowSub(IncidentParms parms) =>
            PioneerFrameworkLiteMod.Settings?.spaceAiRandomEncounters == true &&
            Current.Game?.GetComponent<SpaceAiManager>()?.HasActiveEncounter == false &&
            SpaceEncounterService.FindPlayerShip() != null;
        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            if (!CanFireNowSub(parms)) return false;
            var target = SpaceEncounterService.FindPlayerShip();
            var encounter = DefDatabase<SpaceEncounterDef>.GetNamedSilentFail("PF_Encounter_RaidingFleet");
            var faction = FindEnemy();
            return target != null && faction != null && encounter != null && SpaceEncounterService.TryStart(encounter,faction,
                target.GroupId,Math.Clamp((int)(parms.points/500),3,6),out _);
        }
        public static Faction? FindEnemy()
        {
            foreach (var faction in Find.FactionManager.AllFactions)
                if (!faction.defeated && faction != Faction.OfPlayer && faction.HostileTo(Faction.OfPlayer)) return faction;
            return null;
        }
    }
}
