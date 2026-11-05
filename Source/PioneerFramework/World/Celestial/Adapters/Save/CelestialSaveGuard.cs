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
            if (manager.CelestialRestorePending)
            { manager.NotifyCelestialSaveRejected("RestorePending"); return false; }
            if (manager.TryGetCelestialRuntime(out var runtime) &&
                (runtime.IsRestoring || runtime.Coordinator.HasPreparedStep || runtime.IsExecutingCallbacks || runtime.HostWork.IsBusy))
            { manager.NotifyCelestialSaveRejected("HostWorkPending"); return false; }
            return true;
        }
    }
}
