using System;
using System.Collections.Generic;
using Verse;

namespace PioneerFramework.SpaceAI
{
    public sealed class EncounterBuilding
    {
        public string defName = string.Empty;
        public IntVec3 cell;
        public Rot4 rotation = Rot4.North;
    }
    public class SpaceShipTemplateDef : Def
    {
        public int width = 40, height = 40;
        public float dryMass = 30000;
        public int ammunition = 24;
        public AiRole role;
        public List<EncounterBuilding> buildings = new();
        public bool TryValidate(out string reason)
        {
            reason = string.Empty;
            if (width < 24 || height < 24 || width > 100 || height > 100 || !(dryMass > 0) || ammunition < 1)
            { reason = "Invalid ship dimensions, mass or ammunition"; return false; }
            if (buildings == null || buildings.Count == 0) { reason = "Ship has no equipment"; return false; }
            foreach (var item in buildings)
                if (item == null || string.IsNullOrWhiteSpace(item.defName) || item.cell.x < 3 || item.cell.z < 3 ||
                    item.cell.x >= width - 3 || item.cell.z >= height - 3)
                { reason = "Invalid equipment placement"; return false; }
            return true;
        }
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (!TryValidate(out string reason)) yield return reason;
        }
    }
    public class SpaceEncounterDef : Def
    {
        public int minShips = 3, maxShips = 6;
        public float spawnDistance = 30000, shipSeparation = 7500, withdrawalDistance = 150000;
        public float preparationSeconds = 30;
        public float searchAbandonSeconds = 180;
        public float withdrawalSeconds = 120;
        public List<SpaceShipTemplateDef> templates = new();
        public int ClampCount(int count) => Math.Clamp(count, minShips, maxShips);
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (minShips < 1 || maxShips < minShips || maxShips > 32) yield return "Invalid fleet size range";
            if (!(spawnDistance > 10000) || !(shipSeparation > 1000) || !(withdrawalDistance > spawnDistance) || preparationSeconds < 0)
                yield return "Invalid encounter geometry or preparation time";
            if (templates == null || templates.Count == 0) yield return "No ship templates";
            if (!float.IsFinite(searchAbandonSeconds) || searchAbandonSeconds < 30)
                yield return "Search abandonment must be finite and at least 30 simulation seconds";
            if (!float.IsFinite(withdrawalSeconds) || withdrawalSeconds < 0)
                yield return "Withdrawal duration must be finite and nonnegative";
        }
    }
}
