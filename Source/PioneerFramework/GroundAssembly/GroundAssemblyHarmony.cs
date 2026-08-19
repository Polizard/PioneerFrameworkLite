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
            new Harmony("PioneerFramework.GroundAssembly").PatchAll(Assembly.GetExecutingAssembly());
        }
    }
}
