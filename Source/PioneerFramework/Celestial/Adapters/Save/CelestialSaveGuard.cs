using System.Linq;
using HarmonyLib;
using Verse;

namespace PioneerFramework.Celestial
{
    /// <summary>Scribe_Deep catches component exceptions. Reject unsafe saves before SafeSaver can touch the destination.</summary>
    internal static class CelestialSaveGuard
    {
        private static bool installed;
        internal static void Install()
        {
            if (installed) return;
            new Harmony("PioneerFramework.Celestial.SaveGuard").Patch(CelestialNativeCompatibility.RequireMethod("GameDataSaveLoader.SaveGame"),
                prefix: new HarmonyMethod(typeof(CelestialSaveGuard), nameof(BeforeSave)));
            CelestialNativeCompatibility.RequireInstalledHooks("GameDataSaveLoader.SaveGame.prefix.BeforeSave");
            installed = true;
        }
        internal static bool BeforeSave()
        {
            var manager = Current.Game?.GetComponent<CelestialBodyManager>();
            if (manager == null) return true;
            foreach (var map in Current.Game.Maps)
                if (map.components?.OfType<MapComponent_CelestialEnvironment>().SingleOrDefault()?.EnsureRestored() == false)
                { manager.NotifyCelestialSaveRejected("RoomEnvironmentRestoreBlocked"); return false; }
            // 在 SafeSaver 接触目标文件前完成一次捕获：地图/地表身份、宿主作业与恢复态失败都在这里拒绝，
            // 而不是等 ExposeData 在 Scribe 写档中途抛错。捕获到的文档由同一 adapter 在 ExposeData 中复用。
            var prepared = manager.PrepareCelestialSave();
            if (!prepared.Success)
            {
                manager.NotifyCelestialSaveRejected(prepared.Errors.Count > 0 ? prepared.Errors[0].Code : "SavePreconditionFailed");
                return false;
            }
            return true;
        }
    }
}
