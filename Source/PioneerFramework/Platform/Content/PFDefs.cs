using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PioneerFramework
{
    [DefOf]
    public static class PFDefs
    {
        static PFDefs()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(PFDefs));
        }
        public static BiomeDef PF_MoonBiomeWater = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        public static BiomeDef PF_MoonBiomeBasalt = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        public static BiomeDef PF_MoonBiomeRegolith = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
    }
}
