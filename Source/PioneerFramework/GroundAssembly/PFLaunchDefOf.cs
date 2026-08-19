using RimWorld;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>PFL 地面总装/回收重建所需的 Def 引用。</summary>
    [DefOf]
    public static class PFLaunchDefOf
    {
        public static JobDef PF_RebuildRocket = null!;

        public static DesignationDef PF_DesignateRebuildRocket = null!;

        static PFLaunchDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(PFLaunchDefOf));
        }
    }
}
