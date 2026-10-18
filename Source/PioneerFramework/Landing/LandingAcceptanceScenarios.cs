using System.Collections.Generic;
using System.Linq;
using Verse;

namespace PioneerFramework.Landing
{
    public static class LandingAcceptanceScenarios
    {
        private static readonly Dictionary<string, LandingTransitStatus> stages = new Dictionary<string, LandingTransitStatus>();
        public static void Observe(LandingTaskData task)
        {
            if (!Prefs.DevMode || (stages.TryGetValue(task.TaskId, out var old) && old == task.Status)) return;
            stages[task.TaskId] = task.Status;
            var sg = Current.Game.GetComponent<SpaceGameComp>(); var group = sg.GetGroupById(task.GroupId);
            string maps = string.Join(",", task.SourceMaps.Select(s => s.SourceKey + ":" + s.Status));
            Log.Message($"[PFL Landing acceptance] task={task.TaskId} group={task.GroupId} stage={task.Status} active={group?.IsActive} movable={group?.IsMovable} destination={task.DestinationMapId} maps=[{maps}] charged={task.FuelReceipt.ConsumedKg:F3}kg recovery={task.RecoverySiteId}");
        }
    }
}
