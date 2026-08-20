using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using PioneerFramework.GroundAssembly;

namespace PioneerFramework.Launch
{
    /// <summary>
    /// 地面发射服务。这是 PFL 的天地往返入口：
    /// 地面发射台/火箭框架 -> 生成 SpaceEntity + 群组 + 口袋地图 -> 入轨。
    /// 当前为调试级实现，后续接入发射条件、载荷部署、分级/回收钩子。
    /// </summary>
    public static class LaunchService
    {
        private const int EarthBodyId = 1;

        /// <summary>生成一份 PFL 调试火箭设计，供发射台调试 Gizmo 使用。</summary>
        public static RocketDesignSpec CreateDebugDesign(bool recoverable = true)
        {
            return new RocketDesignSpec
            {
                rocketDefName = recoverable
                    ? "PF_Building_Rocket_Recoverable_Basic"
                    : "PF_Building_Rocket_Basic",
                frameSize = 3,
                mapWidth = 40,
                mapHeight = 40,
                fuelAmount = 1000f,
                parts = new List<RocketPartSpec>
                {
                    new RocketPartSpec { defName = "PF_RocketPart_Debug_Thruster", category = "thruster", quantity = 1 },
                    new RocketPartSpec { defName = "PF_RocketPart_Debug_Tank", category = "tank", quantity = 1 }
                },
                payloads = new List<RocketPayloadSpec>
                {
                    new RocketPayloadSpec { defName = "PF_Payload_Debug", installPosition = "nose", quantity = 1 }
                }
            };
        }

        /// <summary>
        /// 在发射台中心生成调试用未完工火箭框架。
        /// 框架 Def 由 PFL 调试 Def 提供；DMS:NF 正式型号可替换为自己的框架 ThingDef。
        /// </summary>
        public static bool SpawnDebugRocketFrame(Map map, IntVec3 center, RocketDesignSpec spec)
        {
            if (map == null || spec == null)
                return false;

            ThingDef rocketDef = DefDatabase<ThingDef>.GetNamedSilentFail(spec.rocketDefName);
            if (rocketDef == null)
            {
                Log.Error($"[PioneerFramework] Missing rocket def for frame: {spec.rocketDefName}");
                return false;
            }

            string frameDefName = rocketDef.defName == "PF_Building_Rocket_Basic"
                ? "PF_Building_RocketFrame_Basic_NonRecoverable"
                : "PF_Building_RocketFrame_Basic";
            ThingDef frameDef = DefDatabase<ThingDef>.GetNamedSilentFail(frameDefName);
            if (frameDef == null)
            {
                Log.Error($"[PioneerFramework] Missing debug rocket frame def: {frameDefName}");
                return false;
            }

            if (frameDef.entityDefToBuild != rocketDef)
            {
                Log.Error($"[PioneerFramework] Frame {frameDef.defName} is bound to {frameDef.entityDefToBuild?.defName ?? "null"}, not {spec.rocketDefName}.");
                return false;
            }

            Frame frame = ThingMaker.MakeThing(frameDef) as Frame;
            if (frame == null)
            {
                Log.Error($"[PioneerFramework] {frameDef.defName} is not a RimWorld.Frame; cannot spawn rocket frame.");
                return false;
            }

            frame.SetFactionDirect(Faction.OfPlayer);
            GenSpawn.Spawn(frame, center, map, Rot4.North, WipeMode.VanishOrMoveAside);
            if (!frame.Spawned)
            {
                Log.Error($"[PioneerFramework] Failed to spawn debug rocket frame at {center}.");
                return false;
            }

            var comp = frame.TryGetComp<CompUnfinishedRocket>();
            if (comp != null)
                comp.AssignSpec(spec);

            return true;
        }

        /// <summary>
        /// 地面发射入口：创建太空实体/群组/口袋地图，并把实体放入绕地圆轨道。
        /// 当前是调试级“瞬间入轨”，不包含倒计时、燃料消耗、发射条件校验。
        /// </summary>
        public static SpaceEntity LaunchFromGround(Map groundMap, IntVec3 center, RocketDesignSpec spec)
        {
            if (groundMap == null || spec == null)
                return null;

            ThingDef rocketDef = DefDatabase<ThingDef>.GetNamedSilentFail(spec.rocketDefName);
            if (rocketDef == null)
            {
                Log.Error($"[PioneerFramework] Missing rocket def for launch: {spec.rocketDefName}");
                return null;
            }

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null)
            {
                Log.Error("[PioneerFramework] SpaceGameComp missing; cannot launch.");
                return null;
            }

            int groupId = GetNextGroupId(spaceGame);
            int entityId = GetNextEntityId(spaceGame);
            int factionId = Faction.OfPlayer?.loadID ?? 0;

            // 简单占位轨道：500km 圆轨道，角度随机。
            double orbitRadiusMeters = SpaceConstants.PlanetRadiusMeters + 500000.0;
            double angle = Rand.Value * Math.PI * 2.0;
            var pos = new DVector3(
                Math.Cos(angle) * orbitRadiusMeters,
                0,
                Math.Sin(angle) * orbitRadiusMeters);

            var group = new SpaceEntityGroup(groupId, factionId)
            {
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = EarthBodyId
            };
            group.CurrentOrbit = new OrbitState
            {
                position = pos,
                velocity = DVector3.zero,
                rotation = UnityEngine.Quaternion.identity
            };
            spaceGame.RegisterGroup(group);

            var entity = SpaceMapGenerator.CreateStationEntity(entityId, factionId, pos, spec.mapWidth, spec.mapHeight);
            entity.GroupId = groupId;
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(UnityEngine.Quaternion.identity);

            var map = RocketPocketMapGenerator.GenerateRocketMap(spec);
            if (map == null)
            {
                spaceGame.UnregisterGroup(group);
                return null;
            }

            SpaceMapGenerator.LinkEntityToMap(entity, map);
            spaceGame.RegisterEntity(entity);
            group.AddEntity(entityId);

            group.EnterCircularOrbit(Find.TickManager.TicksGame, EarthBodyId);

            DestroyGroundRocket(groundMap, center, rocketDef);
            TrySpawnLeavingSkyfaller(groundMap, center, rocketDef);
            TryTriggerLaunchFx(groundMap, center);
            TryTriggerBoosterRecovery(groundMap, center, spec.rocketDefName);

            Log.Message($"[PioneerFramework] Launch complete: entity={entityId}, group={groupId}, map={entity.MapId}, rocket={spec.rocketDefName}");
            Messages.Message("火箭已入轨。", MessageTypeDefOf.TaskCompletion, false);
            return entity;
        }

        /// <summary>发射瞬间生成 DMS 风格的离港 Skyfaller，让火箭贴图沿轨迹飞离而不是直接消失。</summary>
        private static void TrySpawnLeavingSkyfaller(Map map, IntVec3 center, ThingDef rocketDef)
        {
            if (map == null || rocketDef?.comps == null)
                return;

            CompProperties_Launchable launchable = null;
            for (int i = 0; i < rocketDef.comps.Count; i++)
            {
                if (rocketDef.comps[i] is CompProperties_Launchable la)
                {
                    launchable = la;
                    break;
                }
            }

            ThingDef skyfallerDef = launchable?.skyfallerLeaving;
            if (skyfallerDef == null)
            {
                Log.Warning($"[PioneerFramework] No skyfallerLeaving for {rocketDef.defName}; launch animation skipped.");
                return;
            }

            SkyfallerMaker.SpawnSkyfaller(skyfallerDef, center, map);
        }

        private static void TryTriggerLaunchFx(Map map, IntVec3 center)
        {
            if (map == null)
                return;

            Building launchpad = map.thingGrid.ThingsListAt(center)
                .OfType<Building>()
                .FirstOrDefault(b => LaunchPadUtility.IsAssemblyPad(b.def));
            if (launchpad == null)
                return;

            map.GetComponent<MapComponent_LaunchBlastFX>()?.ScheduleBlast(launchpad);
        }

        /// <summary>如果发射的是可回收火箭，则触发第一级助推器回收。</summary>
        private static void TryTriggerBoosterRecovery(Map map, IntVec3 center, string rocketDefName)
        {
            if (map == null || rocketDefName.NullOrEmpty())
                return;

            ThingDef rocketDef = DefDatabase<ThingDef>.GetNamedSilentFail(rocketDefName);
            if (rocketDef == null || LaunchPadUtility.RocketRecoveryDefFor(rocketDef) == null)
                return;

            Building launchpad = map.thingGrid.ThingsListAt(center)
                .OfType<Building>()
                .FirstOrDefault(b => LaunchPadUtility.IsAssemblyPad(b.def));
            if (launchpad == null)
                return;

            RocketBoosterReturnUtility.TrySpawnReturnedBooster(launchpad, rocketDef);
        }

        /// <summary>移除地面上的火箭/调试火箭框架（发射后地面火箭消失，发射台保留）。</summary>
        private static void DestroyGroundRocket(Map map, IntVec3 center, ThingDef rocketDef)
        {
            if (map == null)
                return;

            var things = map.thingGrid.ThingsListAt(center);
            for (int i = things.Count - 1; i >= 0; i--)
            {
                Thing t = things[i];
                if (t == null || t.Destroyed)
                    continue;

                bool isTargetRocket = t.def == rocketDef
                    || t.def.defName == "PF_Building_RocketFrame_Basic"
                    || LaunchPadUtility.IsRocket(t.def);
                if (isTargetRocket)
                {
                    t.Destroy(DestroyMode.Vanish);
                }
            }
        }

        private static int GetNextGroupId(SpaceGameComp spaceGame)
        {
            int maxId = 0;
            foreach (var g in spaceGame.GetAllGroups())
                if (g.GroupId > maxId)
                    maxId = g.GroupId;
            return maxId + 1;
        }

        private static int GetNextEntityId(SpaceGameComp spaceGame)
        {
            int maxId = 0;
            foreach (var g in spaceGame.GetAllGroups())
            {
                foreach (int eid in g.GetEntityIds())
                    if (eid > maxId)
                        maxId = eid;
            }
            return maxId + 1;
        }
    }
}
