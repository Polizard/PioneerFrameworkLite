using System.Linq;
using Verse;

namespace PioneerFramework.Launch
{
    /// <summary>逐 tick 结算已到终点的起飞过场，状态保存在 WorldObject 自身。</summary>
    public class LaunchTransitManager : GameComponent
    {
        public LaunchTransitManager(Game game) { }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            if (Find.WorldObjects == null || Find.TickManager == null) return;

            int tick = Find.TickManager.TicksGame;
            var transits = Find.WorldObjects.AllWorldObjects.OfType<LaunchTransitWorldObject>().ToArray();
            foreach (var transit in transits)
            {
                if (transit.Destroyed) continue;
                if (transit.Status == LaunchTransitStatus.Completed)
                {
                    transit.Destroy();
                    continue;
                }
                if (!transit.IsDue(tick)) continue;
                if (!LaunchTransitService.TryComplete(transit, out string reason))
                {
                    transit.MarkFailed(reason, tick);
                    Log.Error("[LaunchTransit] " + reason);
                }
            }
        }
    }
}
