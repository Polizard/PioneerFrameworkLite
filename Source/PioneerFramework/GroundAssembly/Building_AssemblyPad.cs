using System.Linq;
using RimWorld;
using Verse;
using PioneerFramework.Launch;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 通用总装发射台建筑：实现 INotifyLaunchableLaunch，
    /// 在走原版 CompLaunchable 发射路径时也能触发 PFL 点火特效与助推器回收。
    /// </summary>
    public class Building_AssemblyPad : Building, INotifyLaunchableLaunch
    {
        public void Notify_LaunchableLaunched(CompLaunchable launchable)
        {
            if (!Spawned)
            {
                return;
            }

            MapComponent_LaunchBlastFX fx = Map.components.OfType<MapComponent_LaunchBlastFX>().FirstOrDefault();
            fx?.ScheduleBlast(this);

            ThingDef launchedDef = launchable?.parent?.def;
            if (launchedDef != null)
            {
                RocketBoosterReturnUtility.TrySpawnReturnedBooster(this, launchedDef);
            }
        }
    }
}
