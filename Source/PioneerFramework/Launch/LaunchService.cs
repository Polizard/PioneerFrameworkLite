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
                CabinType = recoverable ? RocketCabinType.Spacecraft : RocketCabinType.None,
                frameSize = 3,
                mapWidth = 50,
                mapHeight = 50,
                fuelAmount = 1000f,
                parts = new List<DesignPartSpec>
                {
                    new() { defName = "PF_ShipPart_Thruster_Debug", category = "thruster", quantity = 1 },
                    new() { defName = "PF_ShipPart_Tank_Debug", category = "tank", quantity = 1 }
                },
                payloads = new List<RocketPayloadSpec>
                {
                    new() { defName = "PF_Payload_Debug", installPosition = "nose", quantity = 1 }
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

            // 走原版建造流程：先生成 Blueprint，pawn 搬运建材后再生成 Frame。
            // 这样玩家能正常看到“搬运建材 → 建造框架”的完整施工链。
            if (rocketDef.frameDef == null || rocketDef.blueprintDef == null)
            {
                Log.Error($"[PioneerFramework] Rocket def {rocketDef.defName} has no frameDef/blueprintDef; cannot spawn construction blueprint.");
                return false;
            }

            if (rocketDef.frameDef.comps == null)
                rocketDef.frameDef.comps = new List<CompProperties>();
            if (!rocketDef.frameDef.comps.Any(c => c is CompProperties_UnfinishedRocket))
                rocketDef.frameDef.comps.Add(new CompProperties_UnfinishedRocket());

            if (rocketDef.blueprintDef.comps == null)
                rocketDef.blueprintDef.comps = new List<CompProperties>();
            if (!rocketDef.blueprintDef.comps.Any(c => c is CompProperties_RocketBlueprint))
                rocketDef.blueprintDef.comps.Add(new CompProperties_RocketBlueprint());

            Blueprint_Build blueprint = GenConstruct.PlaceBlueprintForBuild(
                rocketDef, center, map, Rot4.North, Faction.OfPlayer, null);
            if (blueprint == null || !blueprint.Spawned)
            {
                Log.Error($"[PioneerFramework] Failed to spawn construction blueprint for {rocketDef.defName} at {center}.");
                return false;
            }

            var comp = blueprint.TryGetComp<CompRocketBlueprint>();
            if (comp != null)
                comp.AssignSpec(spec);

            return true;
        }

        /// <summary>
        /// 地面发射入口：创建太空实体/群组/口袋地图，注入 Part 推进参数，
        /// 校验发射条件后进入「正式入轨机动」（两段式圆轨化，而不是瞬间免费入轨）。
        /// </summary>
        /// <param name="debug">
        /// true = 调试直发：跳过「有人控制台 / 舱室封闭」两项人员校验；
        /// false = 正式发射：执行完整发射条件校验。
        /// </param>
        public static SpaceEntity? LaunchFromGround(Map groundMap, IntVec3 center, RocketDesignSpec? spec, bool debug = false)
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

            int groupId = spaceGame.AllocGroupId();
            int entityId = spaceGame.AllocEntityId();
            int factionId = Faction.OfPlayer?.loadID ?? 0;

            // ── 出生状态：天地映射（设计 §3，取代随机角度瞬移）──
            // 发射台 tile → 经纬度 → ECI 惯性状态；出生 = 地表上方 10km，速度继承自转初速 v_surface。
            // 出生半径 R+10km < 安全半径 R+100km 且初速 ~524 > 着陆阈值 10 → 由 RocketLaunchController 置碰撞豁免。
            const double LaunchAltitudeMeters = 10000.0;
            var (pos, birthVel) = GetSurfaceState.GroundStateAtLaunch(groundMap, LaunchAltitudeMeters, EarthBodyId);

            var group = new SpaceEntityGroup(groupId, factionId)
            {
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = EarthBodyId
            };
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)pos,
                velocitySystem = (SystemVec3)birthVel,
                rotation = DQuaternion.identity
            };
            spaceGame.RegisterGroup(group);

            var entity = SpaceMapGenerator.CreateStationEntity(entityId, factionId, pos, spec.mapWidth, spec.mapHeight);
            entity.GroupId = groupId;
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(UnityEngine.Quaternion.identity);

            // ── M1 第一刀（2026-10-17）：舱盒 + 目录命中的追加件 ──
            // 舱盒仍是 Part[0]（原点、地图载体、质量公式不变）；追加件沿 −Y 堆叠、不挂图。
            // ⚠ `includeUnknownDefs: false`：只有真 `ShipPartDef`（新目录）才追加 ⇒ 既有 DMS/ThingDef
            //   设计保持单 part 行为，直到它们的目录条目补齐。
            entity.SetParts(DesignPartAssembler.BuildParts(spec, spec.mapWidth, spec.mapHeight,
                out _, includeUnknownDefs: false));

            bool hasCabinMap = spec.CabinType == RocketCabinType.Spacecraft;
            Map? map = null;
            bool hadExistingMap = false;
            if (hasCabinMap)
            {
                map = FindExistingRocketMap(groundMap, center);
                hadExistingMap = map != null;
                if (map == null)
                {
                    Messages.Message("尚未登上火箭，未生成舱内地图，无法进行发射。", MessageTypeDefOf.RejectInput, false);
                    spaceGame.UnregisterGroup(group);
                    return null;
                }

                SpaceMapGenerator.LinkEntityToMap(entity, map);
                ConfigureRocketPropulsion(map, spec);
            }
            else
            {
                // 普通运输仓：无舱内地图，使用实体级自带推进数据。
                ConfigureEntityPropulsion(entity, spec);
            }

            // 发射条件校验（机制层硬条件始终生效；人员相关项在 debug 模式跳过）
            List<string> errors = RocketLaunchConditions.Validate(spec, map, debug);
            if (errors.Count > 0)
            {
                foreach (string error in errors)
                    Messages.Message(error, MessageTypeDefOf.RejectInput, false);
                CleanupFailedLaunch(spaceGame, groupId, map, hasCabinMap && !hadExistingMap);
                return null;
            }

            spaceGame.RegisterEntity(entity);
            group.AddEntity(entityId);

            // ── 爬升起点的硬校验：TWR > 1（设计 §4.1）──
            // F = Σ Networks.Thrust，W = m·g（m = 干质量 + 工质，g = GM/R² ≈ 8 m/s²）。
            // 不足则无法起飞，直接拒绝发射（保留地面火箭/发射台）。
            double surfaceRadius = GetSurfaceState.BodyRadius(Current.Game?.GetComponent<CelestialBodyManager>(), EarthBodyId);
            double gSurface = spaceGame.GetBodyGM(EarthBodyId) / (surfaceRadius * surfaceRadius);
            var propulsionSrc = spaceGame.GetGroupPropulsionSource(group);
            double thrustN = 0.0;
            if (propulsionSrc != null)
            {
                for (int n = 0; n < propulsionSrc.Networks.Count; n++)
                    thrustN += propulsionSrc.Networks[n].Thrust;
            }
            double dryMass = 0.0;
            foreach (int eid in group.GetEntityIds())
            {
                var e = spaceGame.GetEntityById(eid);
                if (e != null) dryMass += e.TotalMass;
            }
            double propMass = propulsionSrc != null ? propulsionSrc.TotalPropellantMass : 0.0;
            double weightN = (dryMass + propMass) * gSurface;
            if (thrustN <= weightN)
            {
                Messages.Message($"推力不足，无法起飞：F={thrustN / 1000.0:0.#} kN ≤ 重力 {weightN / 1000.0:0.#} kN（TWR≤1）。", MessageTypeDefOf.RejectInput, false);
                CleanupFailedLaunch(spaceGame, groupId, map, !hadExistingMap);
                return null;
            }

            // ── 爬升段制导（设计 §4）：注册 RocketLaunchController 持续引导，取代瞬移入轨 ──
            // 控制器每物理步闭环驱动 → 到达目标窗口 → BeginCircularize 真实入轨（消耗注入的工质）。
            var launchCtrl = Current.Game?.GetComponent<RocketLaunchController>();
            if (launchCtrl == null || !launchCtrl.RegisterLaunch(group, EarthBodyId, LaunchGuidanceParams.CreateDefault(surfaceRadius)))
            {
                CleanupFailedLaunch(spaceGame, groupId, map, !hadExistingMap);
                return null;
            }

            DestroyGroundRocket(groundMap, center, rocketDef);
            TrySpawnLeavingSkyfaller(groundMap, center, rocketDef);
            TryTriggerLaunchFx(groundMap, center);
            TryTriggerBoosterRecovery(groundMap, center, spec.rocketDefName);

            Log.Message($"[PioneerFramework] Launch initiated: entity={entityId}, group={groupId}, map={entity.MapId}, rocket={spec.rocketDefName}, TWR={thrustN / Math.Max(weightN, 1e-3):0.00}");
            Messages.Message("火箭已起飞，爬升段制导已开始。", MessageTypeDefOf.TaskCompletion, false);
            return entity;
        }
        /// <summary>
        /// 把 RocketDesignSpec 中的 Part 数据注入舱内地图的 SpaceMapComp，
        /// 使火箭在入轨后拥有真实的推进器/储罐聚合（Part 不以地图实体出现）。
        /// </summary>
        private static void ConfigureRocketPropulsion(Map map, RocketDesignSpec spec)
        {
            if (map == null || spec?.parts == null)
                return;

            var mapComp = map.GetComponent<SpaceMapComp>();
            if (mapComp == null)
                return;

            var summary = PartPropulsionResolver.Resolve(spec);
            PropellantDef? propellant = ResolvePropellant(summary);
            if (propellant == null)
                return;

            // 燃料量仍是设计字段（后续由分级/加注链接管）；容量不足时按旧口径退化为燃料量。
            float fuelMass = Math.Max(0f, spec.fuelAmount);
            float capacity = summary.capacity > 0f ? summary.capacity : fuelMass;

            // 统一交给 SpaceMapComp：注册虚拟推进 + 持久化 RocketDesignSpec/燃料/引擎参数。
            mapComp.SetVirtualRocketPropulsion(spec, propellant, summary.exhaustVelocity, summary.massFlow,
                fuelMass, capacity);
            mapComp.SyncOwnerTankFuelFromPools();   // B4a：初装油量分布到 tank part（COM/惯量）
            LogPropulsion("Rocket", summary, propellant, capacity);
        }

        /// <summary>为无舱内地图的普通运输仓配置实体级自带推进数据。</summary>
        private static void ConfigureEntityPropulsion(SpaceEntity entity, RocketDesignSpec spec)
        {
            if (entity == null || spec?.parts == null)
                return;

            var summary = PartPropulsionResolver.Resolve(spec);
            PropellantDef? propellant = ResolvePropellant(summary);
            if (propellant == null)
                return;

            float fuelMass = Math.Max(0f, spec.fuelAmount);
            float capacity = summary.capacity > 0f ? summary.capacity : fuelMass;

            entity.AddPropulsion(new PropulsionData
            {
                propellant = propellant,
                ve = summary.exhaustVelocity,
                massFlow = summary.massFlow,
                mass = fuelMass,
                capacity = capacity
            });
            entity.SyncTankFuelDistribution(propellant.defName, fuelMass);   // B4a：初装油量分布到 tank part
            LogPropulsion("Entity rocket", summary, propellant, capacity);
        }

        /// <summary>
        /// 工质选择：目录/建筑给出的首个工质优先，缺失或查不到时回退化学工质。
        /// 单池虚拟推进（v1）一次只注册一种工质——跨级异工质多池是分级线的扩展点。
        /// </summary>
        private static PropellantDef? ResolvePropellant(PartPropulsionResolver.Summary summary)
        {
            if (!string.IsNullOrEmpty(summary.propellantDefName))
            {
                PropellantDef named = DefDatabase<PropellantDef>.GetNamedSilentFail(summary.propellantDefName);
                if (named != null)
                    return named;
                Log.Warning($"[PioneerFramework] Part propellant '{summary.propellantDefName}' not found; " +
                            "falling back to PF_Propellant_Chemical.");
            }

            PropellantDef chemical = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Chemical");
            if (chemical == null)
                Log.Warning("[PioneerFramework] PF_Propellant_Chemical missing; rocket propulsion not configured.");
            return chemical;
        }

        private static void LogPropulsion(string source, PartPropulsionResolver.Summary summary,
            PropellantDef propellant, float capacity)
        {
            Log.Message($"[PioneerFramework] {source} propulsion injected: thrusters={summary.thrusterCount}" +
                        $" (catalog {summary.catalogThrusters}), ve={summary.exhaustVelocity:0.#} m/s, " +
                        $"massFlow={summary.massFlow:0.#} kg/s, tanks={summary.tankCount}" +
                        $" (catalog {summary.catalogTanks}), capacity={capacity:0.#} kg, " +
                        $"propellant={propellant.defName}");
        }

        /// <summary>发射失败清理：销毁群组/实体/口袋地图，地面火箭保留。</summary>
        private static void CleanupFailedLaunch(SpaceGameComp spaceGame, int groupId, Map map, bool destroyMap = true)
        {
            spaceGame?.DestroyGroup(groupId);
            if (destroyMap && map != null)
                SpaceMapGenerator.DestroyPocketMap(map);
        }

        /// <summary>
        /// 查找地面火箭/发射台已经生成并关联的舱内口袋地图。
        /// 若存在则发射时复用，保证玩家提前装修好的舱内人员/建筑随火箭入轨。
        /// </summary>
        private static Map? FindExistingRocketMap(Map groundMap, IntVec3 center)
        {
            if (groundMap == null)
                return null;

            foreach (Thing thing in groundMap.thingGrid.ThingsListAt(center))
            {
                var rocketMap = thing?.TryGetComp<CompRocketPocketMap>();
                if (rocketMap != null)
                {
                    var map = rocketMap.GetPocketMap();
                    if (map != null)
                        return map;
                }
            }

            foreach (Thing thing in groundMap.thingGrid.ThingsListAt(center))
            {
                if (thing is not Building building)
                    continue;
                var pad = building.TryGetComp<CompAssemblyPad>();
                if (pad != null)
                {
                    var map = pad.GetPocketMap();
                    if (map != null)
                        return map;
                }
            }

            return null;
        }

        /// <summary>发射瞬间生成 DMS 风格的离港 Skyfaller，让火箭贴图沿轨迹飞离而不是直接消失。</summary>
        private static void TrySpawnLeavingSkyfaller(Map map, IntVec3 center, ThingDef rocketDef)
        {
            if (map == null || rocketDef?.comps == null)
                return;

            CompProperties_Launchable? launchable = null;
            for (int i = 0; i < rocketDef.comps.Count; i++)
            {
                if (rocketDef.comps[i] is CompProperties_Launchable la)
                {
                    launchable = la;
                    break;
                }
            }

            ThingDef? skyfallerDef = launchable?.skyfallerLeaving;
            if (skyfallerDef == null)
            {
                Log.Warning($"[PioneerFramework] No skyfallerLeaving for {rocketDef.defName}; launch animation skipped.");
                return;
            }

            Skyfaller skyfaller = launchable?.activeTransporterDef != null
                ? SkyfallerMaker.MakeSkyfaller(skyfallerDef, launchable.activeTransporterDef)
                : SkyfallerMaker.MakeSkyfaller(skyfallerDef);

            // PFL 的离港动画只是视觉演出，不创建真实运输舱世界物体；
            // 若不关掉 createWorldObject，FlyShipLeaving 会在离图时因 groupID=-1 报错。
            if (skyfaller is RimWorld.FlyShipLeaving flyLeaving)
            {
                if (launchable?.activeTransporterDef == null)
                {
                    Log.Warning($"[PioneerFramework] {skyfallerDef.defName} is FlyShipLeaving but has no activeTransporterDef; skipping launch animation.");
                    skyfaller.Destroy(DestroyMode.Vanish);
                    return;
                }

                flyLeaving.createWorldObject = false;
            }

            GenSpawn.Spawn(skyfaller, center, map);
        }

        private static void TryTriggerLaunchFx(Map map, IntVec3 center)
        {
            if (map == null)
                return;

            Building? launchpad = map.thingGrid.ThingsListAt(center)
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

            Building? launchpad = map.thingGrid.ThingsListAt(center)
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

    }
}
