using System.Collections.Generic;
using RimWorld;
using Verse;
using PioneerFramework.Launch;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 将返回舱转换为目标火箭 Frame，并通过“预装差值材料”让 Frame 的剩余需求等于回收重建配方。
    /// 配置来自 RocketRecoveryDef，不硬编码 DMS:NF 型号。
    /// </summary>
    internal static class RocketRebuildFrameUtility
    {
        internal static bool TryConvertReturnedToFrame(Building returnedRocket, Pawn worker, out string failReason)
        {
            failReason = string.Empty;
            if (returnedRocket == null || worker == null || returnedRocket.Map == null)
            {
                failReason = "RebuildFrameConvertFailed".Translate();
                return false;
            }

            RocketRecoveryDef? recovery = FindRecoveryForReturned(returnedRocket.def);
            if (recovery == null)
            {
                failReason = "RebuildFrameConvertFailed".Translate();
                return false;
            }

            ThingDef rocketDef = recovery.launchedRocketDef ?? DefDatabase<ThingDef>.GetNamedSilentFail(recovery.defName);
            ThingDef? frameDef = recovery.frameDef ?? rocketDef?.frameDef;
            if (rocketDef == null || frameDef == null)
            {
                failReason = "RebuildFrameTargetDefMissing".Translate(rocketDef?.defName ?? recovery.defName);
                return false;
            }

            Frame? frame = ThingMaker.MakeThing(frameDef) as Frame;
            if (frame == null)
            {
                failReason = "RebuildFrameConvertFailed".Translate();
                return false;
            }

            Map map = returnedRocket.Map;
            IntVec3 pos = returnedRocket.Position;
            Rot4 rot = rocketDef.rotatable ? returnedRocket.Rotation : Rot4.North;
            CellRect existingRect = returnedRocket.OccupiedRect();
            IntVec3 spawnPos = GetMatchingPos(existingRect, pos, rot, rocketDef);

            PreloadResourcesForRecipeRemainder(frame, rocketDef, recovery.rebuildCost);
            frame.SetFactionDirect(worker.Faction ?? Faction.OfPlayer);
            returnedRocket.Destroy(DestroyMode.Vanish);
            GenSpawn.Spawn(frame, spawnPos, map, rot, WipeMode.VanishOrMoveAside);
            return true;
        }

        private static RocketRecoveryDef? FindRecoveryForReturned(ThingDef returnedDef)
        {
            if (returnedDef == null)
                return null;

            foreach (RocketRecoveryDef recovery in DefDatabase<RocketRecoveryDef>.AllDefsListForReading)
            {
                if (recovery.returnedThingDef == returnedDef)
                    return recovery;
            }

            return null;
        }

        private static void PreloadResourcesForRecipeRemainder(Frame frame, ThingDef rocketDef, List<ThingDefCountClass> recipe)
        {
            if (recipe == null)
                recipe = new List<ThingDefCountClass>();

            Dictionary<ThingDef, int> remainingMap = BuildNeedMap(recipe);
            List<ThingDefCountClass> totalCosts = rocketDef.CostList;
            if (totalCosts == null)
            {
                return;
            }

            for (int i = 0; i < totalCosts.Count; i++)
            {
                ThingDefCountClass total = totalCosts[i];
                int remaining = remainingMap.TryGetValue(total.thingDef, out int need) ? need : 0;
                int preload = total.count - remaining;
                if (preload < 0)
                {
                    Log.Warning($"[PioneerFramework] Rebuild recipe exceeds base cost for {rocketDef.defName}:{total.thingDef.defName} ({remaining}>{total.count}), clamped.");
                    preload = 0;
                }
                if (preload > total.count)
                {
                    preload = total.count;
                }

                AddThingToFrame(frame, total.thingDef, preload);
            }

            foreach (KeyValuePair<ThingDef, int> kv in remainingMap)
            {
                bool inCostList = false;
                for (int i = 0; i < totalCosts.Count; i++)
                {
                    if (totalCosts[i].thingDef == kv.Key)
                    {
                        inCostList = true;
                        break;
                    }
                }
                if (!inCostList && kv.Value > 0)
                {
                    Log.Warning($"[PioneerFramework] Rebuild recipe material {kv.Key.defName} is not in base cost list of {rocketDef.defName}.");
                }
            }
        }

        private static Dictionary<ThingDef, int> BuildNeedMap(List<ThingDefCountClass> recipe)
        {
            Dictionary<ThingDef, int> map = new Dictionary<ThingDef, int>();
            if (recipe == null)
                return map;

            for (int i = 0; i < recipe.Count; i++)
            {
                ThingDefCountClass entry = recipe[i];
                if (entry?.thingDef == null || entry.count <= 0)
                    continue;

                if (map.TryGetValue(entry.thingDef, out int prev))
                    map[entry.thingDef] = prev + entry.count;
                else
                    map[entry.thingDef] = entry.count;
            }

            return map;
        }

        private static void AddThingToFrame(Frame frame, ThingDef def, int count)
        {
            if (frame == null || def == null || count <= 0)
            {
                return;
            }

            int remaining = count;
            while (remaining > 0)
            {
                Thing thing = ThingMaker.MakeThing(def);
                int stack = remaining > def.stackLimit ? def.stackLimit : remaining;
                thing.stackCount = stack;
                remaining -= stack;
                frame.GetDirectlyHeldThings().TryAdd(thing);
            }
        }

        private static IntVec3 GetMatchingPos(CellRect existingRect, IntVec3 cell, Rot4 rot, ThingDef thingDef)
        {
            foreach (IntVec3 pos in existingRect.Cells)
            {
                CellRect rect = GenAdj.OccupiedRect(pos, rot, thingDef.Size);
                if (rect.minX == existingRect.minX && rect.minZ == existingRect.minZ)
                {
                    return pos;
                }
            }

            return cell;
        }
    }
}
