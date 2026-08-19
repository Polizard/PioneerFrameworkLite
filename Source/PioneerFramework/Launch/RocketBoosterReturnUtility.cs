using System.Collections.Generic;
using RimWorld;
using Verse;
using PioneerFramework.GroundAssembly;

namespace PioneerFramework.Launch
{
    /// <summary>
    /// 发射后回收第一级助推器：优先返回到可用发射台，否则返回发射点附近。
    /// 配置来自 RocketRecoveryDef，不再硬编码 DMS:NF 型号。
    /// </summary>
    internal static class RocketBoosterReturnUtility
    {
        internal static void TrySpawnReturnedBooster(Building launchpad, ThingDef launchedRocketDef)
        {
            if (launchpad == null || !launchpad.Spawned || launchpad.Map == null)
            {
                return;
            }

            if (launchedRocketDef == null || LaunchPadUtility.RocketRecoveryDefFor(launchedRocketDef) == null)
            {
                return;
            }

            Map map = launchpad.Map;
            IntVec3 launchPos = FuelingPortUtility.GetFuelingPortCell(launchpad);
            MapComponent_RocketBoosterReturnScheduler scheduler =
                map.GetComponent<MapComponent_RocketBoosterReturnScheduler>();
            if (scheduler == null)
            {
                Log.Warning("[PioneerFramework] MapComponent_RocketBoosterReturnScheduler missing; spawning booster return immediately.");
                ExecuteSpawnReturnedBooster(map, launchPos, launchedRocketDef);
                return;
            }

            scheduler.ScheduleReturn(launchPos, launchedRocketDef);
        }

        /// <summary>
        /// 延迟结束后执行：重新解析落点并生成入轨 skyfaller，并弹出返回提示。
        /// </summary>
        internal static void ExecuteSpawnReturnedBooster(Map map, IntVec3 launchPos, ThingDef launchedRocketDef)
        {
            if (map == null || launchedRocketDef == null)
            {
                return;
            }

            RocketRecoveryDef recovery = LaunchPadUtility.RocketRecoveryDefFor(launchedRocketDef);
            if (recovery == null)
            {
                return;
            }

            ThingDef returnedDef = recovery.returnedThingDef;
            ThingDef incomingDef = recovery.incomingSkyfallerDef;
            ThingDef activePodDef = recovery.activePodDef;
            if (returnedDef == null || incomingDef == null || activePodDef == null)
            {
                Log.Warning("[PioneerFramework] Rocket recovery defs missing; skip booster return.");
                return;
            }

            IntVec3 landingCell;
            if (!TryFindPreferredLaunchpadCell(map, launchPos, returnedDef, out landingCell) &&
                !TryFindFallbackCell(map, launchPos, incomingDef, out landingCell))
            {
                return;
            }

            ActiveTransporter activeTransporter = (ActiveTransporter)ThingMaker.MakeThing(activePodDef);
            activeTransporter.Contents = new ActiveTransporterInfo();

            Thing returnedBooster = ThingMaker.MakeThing(returnedDef);
            if (Faction.OfPlayer != null)
            {
                returnedBooster.SetFactionDirect(Faction.OfPlayer);
            }

            activeTransporter.Contents.innerContainer.TryAddOrTransfer(returnedBooster);
            activeTransporter.Contents.sentTransporterDef = launchedRocketDef;

            Skyfaller skyfaller = SkyfallerMaker.MakeSkyfaller(incomingDef, activeTransporter);
            GenSpawn.Spawn(skyfaller, landingCell, map);

            Messages.Message("第一级助推器已返回。", new TargetInfo(landingCell, map), MessageTypeDefOf.PositiveEvent);
        }

        private static bool TryFindPreferredLaunchpadCell(Map map, IntVec3 launchPos, ThingDef returnedDef, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            List<Building> allBuildingsColonist = map.listerBuildings.allBuildingsColonist;
            Building best = null;
            float bestDist = float.MaxValue;

            for (int i = 0; i < allBuildingsColonist.Count; i++)
            {
                Building b = allBuildingsColonist[i];
                if (b == null || !b.Spawned || !LaunchPadUtility.IsAssemblyPad(b.def))
                {
                    continue;
                }

                if (!LaunchPadUtility.CanRebuildOnPad(returnedDef, b.def))
                {
                    continue;
                }

                if (b.Position.Roofed(map))
                {
                    continue;
                }

                if (HasBlockingRocketOrBlueprintAt(map, b.Position))
                {
                    continue;
                }

                float dist = launchPos.DistanceToSquared(b.Position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = b;
                }
            }

            if (best == null)
            {
                return false;
            }

            cell = best.Position;
            return true;
        }

        private static bool HasBlockingRocketOrBlueprintAt(Map map, IntVec3 cell)
        {
            List<Thing> thingsAtCell = map.thingGrid.ThingsListAt(cell);
            for (int i = 0; i < thingsAtCell.Count; i++)
            {
                Thing t = thingsAtCell[i];
                if (t is Building building && (LaunchPadUtility.IsRocket(building.def) || LaunchPadUtility.IsReturnedBooster(building.def)))
                {
                    return true;
                }

                if (t is Blueprint blueprint)
                {
                    ThingDef buildDef = blueprint.def.entityDefToBuild as ThingDef;
                    if (buildDef != null && (LaunchPadUtility.IsRocket(buildDef) || LaunchPadUtility.IsReturnedBooster(buildDef)))
                    {
                        return true;
                    }
                }

                if (t is Frame frame)
                {
                    ThingDef buildDef = frame.def.entityDefToBuild as ThingDef;
                    if (buildDef != null && (LaunchPadUtility.IsRocket(buildDef) || LaunchPadUtility.IsReturnedBooster(buildDef)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryFindFallbackCell(Map map, IntVec3 nearLoc, ThingDef incomingDef, out IntVec3 cell)
        {
            if (CellFinderLoose.TryFindSkyfallerCell(incomingDef, map, TerrainAffordanceDefOf.Light, out cell, 10, nearLoc, 20, allowRoofedCells: false, allowCellsWithItems: false, allowCellsWithBuildings: false, colonyReachable: false, avoidColonistsIfExplosive: true, alwaysAvoidColonists: false))
            {
                return true;
            }

            return DropCellFinder.TryFindDropSpotNear(nearLoc, map, out cell, allowFogged: true, canRoofPunch: false, allowIndoors: false, incomingDef.size);
        }
    }
}
