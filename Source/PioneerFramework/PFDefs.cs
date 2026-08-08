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
        public static BiomeDef PF_MoonBiomeWater;
        public static BiomeDef PF_MoonBiomeBasalt;
        public static BiomeDef PF_MoonBiomeRegolith;
    }
}
