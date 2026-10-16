using System;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Landing
{
    public static class LandingDestinationRuntime
    {
        public static Map Generate(LandingTaskData task, IntVec2 size)
        {
            var parent = Find.WorldObjects.ObjectsAt(task.TargetTile).OfType<MapParent>().FirstOrDefault();
            if (parent == null)
            {
                parent = (MapParent)WorldObjectMaker.MakeWorldObject(DefDatabase<WorldObjectDef>.GetNamed("PF_LandedShipSite"));
                parent.Tile = task.TargetTile; parent.SetFaction(Faction.OfPlayer);
                Find.WorldObjects.Add(parent);
            }
            task.DestinationParentId = parent.ID;
            if (parent.HasMap) return parent.Map;
            if (parent is not LandedShipMapParent site || site.EverGenerated) throw new InvalidOperationException("Existing map cannot be safely regenerated.");
            return GetOrGenerateMapUtility.GetOrGenerateMap(task.TargetTile, new IntVec3(size.x, 1, size.z), parent.def);
        }
    }
}
