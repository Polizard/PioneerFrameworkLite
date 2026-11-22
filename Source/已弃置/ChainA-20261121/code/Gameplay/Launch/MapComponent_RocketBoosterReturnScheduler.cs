using System.Collections.Generic;
using Verse;

namespace PioneerFramework.Launch
{
    /// <summary>
    /// 将助推器回收返回延后若干 tick 执行，避免发射瞬间即入轨。
    /// </summary>
    public class MapComponent_RocketBoosterReturnScheduler : MapComponent
    {
        internal const int ReturnDelayTicks = 1200;

        private List<int> pendingAtTick = null!;

        private List<IntVec3> pendingLaunchPos = null!;

        private List<string> pendingLaunchedRocketDefName = null!;

        public MapComponent_RocketBoosterReturnScheduler(Map map)
            : base(map)
        {
        }

        public override void MapComponentTick()
        {
            if (pendingAtTick == null || pendingAtTick.Count == 0)
            {
                return;
            }

            int now = Find.TickManager.TicksGame;
            for (int i = pendingAtTick.Count - 1; i >= 0; i--)
            {
                if (now < pendingAtTick[i])
                {
                    continue;
                }

                IntVec3 launchPos = pendingLaunchPos[i];
                string defName = pendingLaunchedRocketDefName[i];
                pendingAtTick.RemoveAt(i);
                pendingLaunchPos.RemoveAt(i);
                pendingLaunchedRocketDefName.RemoveAt(i);

                ThingDef launchedDef = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                RocketBoosterReturnUtility.ExecuteSpawnReturnedBooster(map, launchPos, launchedDef);
            }
        }

        internal void ScheduleReturn(IntVec3 launchPos, ThingDef launchedRocketDef)
        {
            if (launchedRocketDef == null)
                return;
            ScheduleReturn(launchPos, launchedRocketDef.defName);
        }

        internal void ScheduleReturn(IntVec3 launchPos, string launchedRocketDefName)
        {
            if (pendingAtTick == null)
            {
                pendingAtTick = new List<int>();
                pendingLaunchPos = new List<IntVec3>();
                pendingLaunchedRocketDefName = new List<string>();
            }

            pendingAtTick.Add(Find.TickManager.TicksGame + ReturnDelayTicks);
            pendingLaunchPos.Add(launchPos);
            pendingLaunchedRocketDefName.Add(launchedRocketDefName);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref pendingAtTick, "pendingAtTick", LookMode.Value);
            Scribe_Collections.Look(ref pendingLaunchPos, "pendingLaunchPos", LookMode.Value);
            Scribe_Collections.Look(ref pendingLaunchedRocketDefName, "pendingLaunchedRocketDefName", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (pendingAtTick == null)
                {
                    pendingAtTick = new List<int>();
                }

                if (pendingLaunchPos == null)
                {
                    pendingLaunchPos = new List<IntVec3>();
                }

                if (pendingLaunchedRocketDefName == null)
                {
                    pendingLaunchedRocketDefName = new List<string>();
                }

                // 存档损坏/版本变更可能导致三表长度不一致，MapComponentTick 按同一索引并行读取会越界；
                // 检测到不一致时丢弃未执行的助推器回收调度，避免每 tick 抛 ArgumentOutOfRangeException。
                if (pendingAtTick.Count != pendingLaunchPos.Count
                    || pendingAtTick.Count != pendingLaunchedRocketDefName.Count)
                {
                    Log.Error($"[PioneerFramework] RocketBoosterReturnScheduler: saved list lengths mismatch "
                        + $"({pendingAtTick.Count}/{pendingLaunchPos.Count}/{pendingLaunchedRocketDefName.Count}); "
                        + "clearing booster-return schedules.");
                    pendingAtTick.Clear();
                    pendingLaunchPos.Clear();
                    pendingLaunchedRocketDefName.Clear();
                }
            }
        }
    }
}
