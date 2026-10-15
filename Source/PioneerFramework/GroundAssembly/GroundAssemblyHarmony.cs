using System.Reflection;
using HarmonyLib;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    [StaticConstructorOnStartup]
    public static class GroundAssemblyHarmony
    {
        static GroundAssemblyHarmony()
        {
            // 2026-10-16 反射清理（轻量）：`Assembly.GetExecutingAssembly()` → `typeof(本类).Assembly`
            // （同义，少一个反射入口；`PatchAll` 扫的仍是本程序集）。
            new Harmony("PioneerFramework.GroundAssembly").PatchAll(typeof(GroundAssemblyHarmony).Assembly);
        }
    }
}
