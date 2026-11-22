using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;
using PioneerFramework.Launch;

namespace PioneerFramework.GroundAssembly
{
    internal static class RocketRebuildResourceUtility
    {
        internal static bool HasEnough(Map map, RocketRecoveryDef recovery)
        {
            if (map == null || recovery == null)
                return false;

            List<ThingDefCountClass> needs = recovery.rebuildCost;
            if (needs == null)
                return true;

            for (int i = 0; i < needs.Count; i++)
            {
                ThingDefCountClass need = needs[i];
                if (need?.thingDef == null)
                    continue;

                if (CountOnMap(map, need.thingDef) < need.count)
                    return false;
            }

            return true;
        }

        internal static bool TryGetMissingSummary(Map map, RocketRecoveryDef recovery, out string summary)
        {
            summary = string.Empty;
            if (map == null || recovery == null)
                return false;

            List<ThingDefCountClass> needs = recovery.rebuildCost;
            if (needs == null)
                return false;

            StringBuilder sb = new StringBuilder();
            bool hasMissing = false;
            for (int i = 0; i < needs.Count; i++)
            {
                ThingDefCountClass need = needs[i];
                if (need?.thingDef == null)
                    continue;

                int available = CountOnMap(map, need.thingDef);
                if (available >= need.count)
                    continue;

                hasMissing = true;
                int missing = need.count - available;
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.Append(need.thingDef.label).Append(" 缺少 ").Append(missing);
            }

            summary = sb.ToString();
            return hasMissing;
        }

        private static int CountOnMap(Map map, ThingDef def)
        {
            if (map == null || def == null)
                return 0;

            int total = 0;
            List<Thing> things = map.listerThings.ThingsOfDef(def);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t.Spawned)
                    total += t.stackCount;
            }

            return total;
        }
    }
}
