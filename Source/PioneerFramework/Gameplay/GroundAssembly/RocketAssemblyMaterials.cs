using System;
using System.Collections.Generic;
using Verse;

namespace PioneerFramework.GroundAssembly;

internal static class RocketAssemblyMaterials
{
    internal static bool TryQuote(RocketDesignSpec design, out List<ThingDefCountClass> materials, out string reason,
        Func<string, ShipPartDef?>? parts = null, Func<string, ThingDef?>? items = null)
    {
        materials = new List<ThingDefCountClass>();
        reason = string.Empty;
        parts ??= DefDatabase<ShipPartDef>.GetNamedSilentFail;
        items ??= DefDatabase<ThingDef>.GetNamedSilentFail;

        var totals = new SortedDictionary<string, ThingDefCountClass>(StringComparer.Ordinal);
        ThingDefCountClass[]? defaultCost = null;
        long totalParts = 0;
        foreach (DesignPartSpec entry in design.parts)
        {
            if (entry == null) continue;
            long count = (long)RocketDesignRules.StationCountOf(entry) * RocketDesignRules.ParallelOf(entry);
            totalParts += count;
            if (totalParts > DesignPartAssembler.MaxTotalParts)
            {
                reason = $"{"PF_AssemblyTooManyParts".Translate()} {DesignPartAssembler.MaxTotalParts}";
                return false;
            }

            ShipPartDef? part = string.IsNullOrEmpty(entry.defName) ? null : parts(entry.defName);
            if (part == null)
            {
                reason = $"{"PF_AssemblyUnknownPart".Translate()} {entry.defName}";
                return false;
            }

            IReadOnlyList<ThingDefCountClass> cost;
            if (part.assemblyCost == null)
            {
                defaultCost ??= new[] { new ThingDefCountClass(items("Steel"), 60),
                    new ThingDefCountClass(items("ComponentIndustrial"), 2) };
                cost = defaultCost;
            }
            else
            {
                cost = part.assemblyCost;
            }
            if (cost.Count == 0)
            {
                reason = $"{"PF_AssemblyInvalidCost".Translate()} {part.LabelCap}";
                return false;
            }

            foreach (var line in cost)
            {
                if (!ShipPartDef.IsValidAssemblyMaterial(line))
                {
                    reason = $"{"PF_AssemblyInvalidCost".Translate()} {part.LabelCap}";
                    return false;
                }

                string name = line.thingDef.defName;
                totals.TryGetValue(name, out ThingDefCountClass? existing);
                long needed = count * line.count + (existing?.count ?? 0);
                if (needed > int.MaxValue)
                {
                    reason = $"{"PF_AssemblyCostOverflow".Translate()} {line.thingDef.LabelCap}";
                    return false;
                }
                if (existing == null) totals.Add(name, new ThingDefCountClass(line.thingDef, (int)needed));
                else existing.count = (int)needed;
            }
        }
        materials.AddRange(totals.Values);
        return true;
    }
}
