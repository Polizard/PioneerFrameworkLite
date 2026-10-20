using System;
using System.Globalization;
using PioneerFramework.SpaceAI;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    public static class SpaceAiDebugActions
    {
        public static void SpawnThree() => Spawn(3);
        public static void SpawnSix() => Spawn(6);
        private static void Spawn(int count)
        {
            var target = SpaceEncounterService.FindPlayerShip();
            var faction = IncidentWorker_SpaceEncounter.FindEnemy();
            var def = DefDatabase<SpaceEncounterDef>.GetNamedSilentFail("PF_Encounter_RaidingFleet");
            if (target == null || faction == null || def == null)
            { Messages.Message("需要一艘活动的玩家航天器、敌对势力和遭遇定义。",MessageTypeDefOf.RejectInput,false); return; }
            if (!SpaceEncounterService.TryStart(def,faction,target.GroupId,count,out int id))
            { Messages.Message("遭遇启动失败：已有活动遭遇或条件无效。",MessageTypeDefOf.RejectInput,false); return; }
            Log.Message(FormattableString.Invariant($"[SpaceAI] Queued encounter {id}, {count} ships, target G{target.GroupId}"));
        }
        public static void Report()
        {
            var manager = Current.Game?.GetComponent<SpaceAiManager>(); if (manager == null) return;
            var report = manager.Metrics.Report();
            Log.Message(string.Format(CultureInfo.InvariantCulture,
                "[SpaceAI] controlled={0}, samples={1}, mean={2:F3}ms p95={3:F3}ms p99={4:F3}ms max={5:F3}ms allocated={6}",
                manager.Ships.Count,report.Samples,report.MeanMilliseconds,report.P95Milliseconds,report.P99Milliseconds,report.MaxMilliseconds,report.AllocatedBytes));
            foreach (var ship in manager.Ships)
                Log.Message(FormattableString.Invariant($"[SpaceAI] G{ship.groupId}: {ship.role}, {ship.action}, retreat={ship.withdrawing}"));
            foreach (var stage in Enum.GetValues<AiMetricStage>())
            {
                var sample = manager.Metrics.Report(stage);
                Log.Message(FormattableString.Invariant($"[SpaceAI] {stage}: n={sample.Samples} mean={sample.MeanMilliseconds:F3}ms p95={sample.P95Milliseconds:F3}ms max={sample.MaxMilliseconds:F3}ms"));
            }
        }
    }
}
