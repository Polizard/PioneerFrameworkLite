using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 调试动作的静态入口（2026-10-18）：玩家工具条/面板上的测试与作弊项搬进
    /// DebugTabMenu_PFL 后，由这里找到当前打开的 PFView 窗口执行。
    /// 没有开窗时只写一行日志——不静默。
    /// </summary>
    internal static class PFViewDebugActions
    {
        private static MainTabWindow_PFView? Window
            => Find.WindowStack?.WindowOfType<MainTabWindow_PFView>();

        public static void DeployCombatPair()
        {
            var w = Window;
            if (w == null) { Log.Message("[PFView·调试] 先打开 PFView 页签"); return; }
            w.DebugDeployCombatPair();
        }

        public static void TeleportTarget()
        {
            var w = Window;
            if (w == null) { Log.Message("[PFView·调试] 先打开 PFView 页签"); return; }
            w.DebugTeleportTarget();
        }

        public static void TargetSpeedUp()
        {
            var w = Window;
            if (w == null) { Log.Message("[PFView·调试] 先打开 PFView 页签"); return; }
            w.DebugAdjustTargetSpeed(100.0);
        }

        public static void TargetSpeedDown()
        {
            var w = Window;
            if (w == null) { Log.Message("[PFView·调试] 先打开 PFView 页签"); return; }
            w.DebugAdjustTargetSpeed(-100.0);
        }

        public static void PregenerateRooms()
        {
            var w = Window;
            if (w == null) { Log.Message("[PFView·调试] 先打开 PFView 页签"); return; }
            w.DebugPregenerateRoomMaps();
        }

        public static void SpawnSoyuzPrism()
        {
            var group = DebugShipSpawner.SpawnSoyuzPrismDemo();
            if (group == null) return;
            var w = Window;
            if (w == null)
            {
                Log.Message("[PFView·调试] 联盟号已生成 G" + group.GroupId
                    + "；打开 PFView 后再在对象列表选择/聚焦该组。");
                return;
            }
            w.DebugSelectAndFocusGroup(group.GroupId);
        }

        public static void SpawnX15()
        {
            var group = DebugShipSpawner.SpawnX15Demo();
            if (group == null) return;
            var w = Window;
            if (w == null)
            {
                Log.Message("[PFView·调试] X-15 已生成 G" + group.GroupId
                    + "；打开 PFView 后再在对象列表选择/聚焦该组。");
                return;
            }
            w.DebugSelectAndFocusGroup(group.GroupId);
        }

        public static void SpawnCrewDragon()
        {
            var group = DebugShipSpawner.SpawnCrewDragonDemo();
            if (group == null) return;
            var w = Window;
            if (w == null)
            {
                Log.Message("[PFView·调试] 龙飞船已生成 G" + group.GroupId
                    + "；打开 PFView 后再在对象列表选择/聚焦该组。");
                return;
            }
            w.DebugSelectAndFocusGroup(group.GroupId);
        }
    }
}
