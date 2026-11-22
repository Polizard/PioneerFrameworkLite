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

            // 2026-10-27：站点按**地图所在天体**解析（不再写死地球）；目标高度由 SOI/邻居/大气夹取。
            if (!LaunchSite.TryResolve(groundMap, out LaunchSite site, out string siteReason))
            {
                Messages.Message($"发射站点无法解析：{siteReason}", MessageTypeDefOf.RejectInput, false);
                return null;
            }
            LaunchGuidanceParams launchParams = LaunchGuidanceParams.ForBody(site.TargetInputs, out LaunchTargetResult target);
            if (!target.Feasible)
            {
                Messages.Message($"无法选择安全入轨高度：{target.Reason}", MessageTypeDefOf.RejectInput, false);
                Log.Warning($"[Launch] 站点 {site.Describe()} 目标不可行：{target.Reason}");
                return null;
            }
            Log.Message($"[Launch] 站点 {site.Describe()} → h={target.TargetAltitude / 1000.0:F0}km "
                + $"vC={target.VCircular:F0}m/s{(string.IsNullOrEmpty(target.Reason) ? "" : " | " + target.Reason)}");

            int groupId = spaceGame.AllocGroupId();
            int entityId = spaceGame.AllocEntityId();
            int factionId = Faction.OfPlayer?.loadID ?? 0;

            // ── 出生状态：天地映射（设计 §3，取代随机角度瞬移）──
            // 发射台 tile → 经纬度 → ECI 惯性状态；出生 = 地表上方 10km，速度继承自转初速 v_surface。
            // 出生半径 R+10km < 安全半径 R+100km 且初速 ~524 > 着陆阈值 10 → 由 RocketLaunchController 置碰撞豁免。
            const double LaunchAltitudeMeters = 10000.0;
            var (posBody, birthVelBody) = GetSurfaceState.GroundStateAtLaunch(groundMap, LaunchAltitudeMeters, site.BodyId);
            var (pos, birthVel) = site.ToWorld(posBody, birthVelBody, TimeScaleManager.GetPhysicsTickSafe());

            // **起飞摆放**（2026-10-28）：照 `MoonLaunchAcceptanceRunner` 那条**已经能飞**的路径——
            // 机头 +Z 转到当地径向朝上，并同时写进群姿态（渲染/物理都读它）。
            // 旧写法恒 `identity`（世界轴对齐），站点不在等效赤道方向时主推轴偏 70°~110°（日志实测 110°）。
            DVector3 upLocal = posBody.normalized;
            double cosTilt = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(DVector3.forward, upLocal)));
            DQuaternion launchRot = cosTilt > 1.0 - 1e-9 ? DQuaternion.identity
                : cosTilt < -1.0 + 1e-9 ? DQuaternion.AngleAxis(Math.PI, DVector3.up)
                : DQuaternion.AngleAxis(Math.Acos(cosTilt), DVector3.Cross(DVector3.forward, upLocal).normalized);

            var group = new SpaceEntityGroup(groupId, factionId)
            {
                IsActive = true,
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = site.BodyId
            };
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)pos,
                velocitySystem = (SystemVec3)birthVel,
                rotation = launchRot
            };
            spaceGame.RegisterGroup(group, GroupBirthKind.Instantiated);   // 发射建组：无母体（状态来自地表解算）

            // **生成飞船 = 无图实体**（2026-10-28 用户指出：发射本质上就是生成一个火箭）。
            // 旧写法用 `SpaceMapGenerator.CreateStationEntity`，它把 `HasMap` 置 **true**（那是地图载体/空间站的语义）
            // ⇒ 与"能飞的验收路径"不一致（验收里是 `new SpaceEntity { HasMap = false }`），
            // 零件推进合成、抛级重建、冲量落地这些按 `HasMap` 分流的代码全走错支。
            // 舱内地图分支稍后由 `LinkEntityToMap` 把 `HasMap` 正确置位。
            var entity = new SpaceEntity(entityId, factionId)
            {
                GroupId = groupId,
                HasMap = false,
                IsMovable = true,
                IsAffectedByGravity = true,
                CanInteract = false,
                IsSelectable = true,
            };
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation((UnityEngine.Quaternion)launchRot);

            // ── M1 第一刀（2026-10-17）：舱盒 + 目录命中的追加件 ──
            // 舱盒仍是 Part[0]（原点、地图载体、质量公式不变）；追加件沿 −Y 堆叠、不挂图。
            // ⚠ `includeUnknownDefs: false`：只有真 `ShipPartDef`（新目录）才追加 ⇒ 既有 DMS/ThingDef
            //   设计保持单 part 行为，直到它们的目录条目补齐。
            // **纯零件装配**（2026-10-28 用户令："发射一个纯粹由没有 Map 的 part 组成的火箭"）：
            // 不造舱盒（`includeCabin:false`）、不挂地图；零件沿 Y 从 0 起堆，
            // 几何/质量/组件全部来自目录件。
            // ⚠ `includeUnknownDefs: false`：只有真 `ShipPartDef` 才进船（既有 DMS/ThingDef 设计保持旧行为）。
            entity.SetParts(DesignPartAssembler.BuildParts(spec, spec.mapWidth, spec.mapHeight,
                out _, includeUnknownDefs: false, includeCabin: false,
                // 级间空隙（2026-10-30 用户方案）：抛级瞬间两组不接触 ⇒ 无贴面求解尖峰、无磕碰
                stageGapMeters: GroundAssembly.RocketDesignRules.StageGapMeters));

            // 2026-10-30（C2）：**把组原点重定到质心**。
            // 机头原点会让所有"船在哪"的消费者（高度/气动/落地判据/交会距离/相机/轨道读数）带一个
            // 半条船的偏移，而且姿态旋转会绕机头摆尾。重定后：世界几何逐件不变（~1e-10 m），
            // 存的速度 = 质心速度。发射瞬间 ω=0 ⇒ 速度不变；但仍按通用规则写 `V += ω×(R·c)`，免得将来错。
            var wholeMass = new SeparationMass();
            var unusedMass = new SeparationMass();
            StageSeparation.Accumulate(entity, -1, ref wholeMass, ref unusedMass);
            if (wholeMass.mass > 0.0)
            {
                DVector3 c = wholeMass.Center;
                DVector3 rc = DVector3.Rotate(c, launchRot);
                group.CurrentOrbit.positionSystem = (SystemVec3)(pos + rc);
                group.CurrentOrbit.velocitySystem = (SystemVec3)(birthVel + DVector3.Cross(group.angularVelocity, rc));
                entity.RecenterParts(c);
            }

            // **用户 2026-10-28 硬约束：发射的火箭一律没有地图。**
            spec.CabinType = RocketCabinType.None;

            // 燃料 = **罐件初装量之和**（零件是权威；设计级 `fuelAmount` 只作为读数/条件校验的镜像）。
            spec.fuelAmount = SumTankFuelKg(entity);

            // 推进 = **从零件合成**（每 (工质, RCS) 一条网络、只算活动级）——与"能飞"的验收路径同一口径。
            // 不写显式 `PropulsionData`：那会把"按级重建"（`PropulsionFromParts`）关掉。
            PartPropulsionBuilder.BuildForMapless(entity);
            Map? map = null;

            // 发射条件校验（机制层硬条件始终生效；人员相关项在 debug 模式跳过）
            List<string> errors = RocketLaunchConditions.Validate(spec, map, debug);
            if (errors.Count > 0)
            {
                foreach (string error in errors)
                    Messages.Message(error, MessageTypeDefOf.RejectInput, false);
                CleanupFailedLaunch(spaceGame, groupId, null, false);
                return null;
            }

            spaceGame.RegisterEntity(entity);
            group.AddEntity(entityId);

            // ── 爬升起点的硬校验：TWR > 1（设计 §4.1）──
            // F = Σ Networks.Thrust，W = m·g（m = 干质量 + 工质，g = GM/R² ≈ 8 m/s²）。
            // 不足则无法起飞，直接拒绝发射（保留地面火箭/发射台）。
            double gSurface = target.SurfaceGravity;
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
                CleanupFailedLaunch(spaceGame, groupId, null, false);
                return null;
            }

            // ── 爬升段制导（设计 §4）：注册 RocketLaunchController 持续引导，取代瞬移入轨 ──
            // 控制器每物理步闭环驱动 → 到达目标窗口 → BeginCircularize 真实入轨（消耗注入的工质）。
            var launchCtrl = Current.Game?.GetComponent<RocketLaunchController>();
            if (launchCtrl == null || !launchCtrl.RegisterLaunch(group, site.BodyId, launchParams))
            {
                CleanupFailedLaunch(spaceGame, groupId, null, false);
                return null;
            }

            DestroyGroundRocket(groundMap, center, rocketDef);
            TrySpawnLeavingSkyfaller(groundMap, center, rocketDef);
            TryTriggerLaunchFx(groundMap, center);
            // 2026-11-21：旧链退役，一级助推器回收线一并删除（用户裁定「回收线删掉」）。

            Log.Message($"[PioneerFramework] Launch initiated: entity={entityId}, group={groupId}, map={entity.MapId}, rocket={spec.rocketDefName}, body={site.BodyId}, TWR={thrustN / Math.Max(weightN, 1e-3):0.00}");
            Messages.Message("火箭已起飞，爬升段制导已开始。", MessageTypeDefOf.TaskCompletion, false);
            return entity;
        }
        /// <summary>**燃料 = 罐件初装量之和**（kg）。零件是权威；没有罐 ⇒ 0（发射条件会拒）。</summary>
        private static float SumTankFuelKg(SpaceEntity entity)
        {
            if (entity == null) return 0f;
            double sum = 0.0;
            var parts = entity.PartsList;
            for (int i = 0; i < parts.Count; i++)
            {
                var tank = parts[i]?.GetComponent<Components.PropellantTankComponentData>();
                if (tank != null && tank.massKg > 0.0) sum += tank.massKg;
            }
            return (float)sum;
        }

        /// <summary>发射失败清理：销毁群组/实体/口袋地图，地面火箭保留。</summary>
        private static void CleanupFailedLaunch(SpaceGameComp spaceGame, int groupId, Map? map, bool destroyMap = true)
        {
            spaceGame?.DestroyGroup(groupId);
            if (destroyMap && map != null)
                SpaceMapGenerator.DestroyPocketMap(map);
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

            // 2026-11-21：旧链发射台（CompAssemblyPad）退役 ⇒ 工位判定改为「带火箭组装器 Comp 的建筑」。
            Building? workstation = map.thingGrid.ThingsListAt(center)
                .OfType<Building>()
                .FirstOrDefault(b => b.TryGetComp<CompRocketAssembler>() != null);
            if (workstation == null)
                return;

            map.GetComponent<MapComponent_LaunchBlastFX>()?.ScheduleBlast(workstation);
        }

        /// <summary>2026-11-21：`LaunchPadUtility` 退役后内联的最小判定（带施工/发射 Comp 的 def 即视为火箭）。</summary>
        private static bool LooksLikeRocket(ThingDef def)
        {
            if (def == null)
                return false;
            if (def.defName == "PF_Building_RocketFrame_Basic")
                return true;
            return def.comps != null && def.comps.Any(c =>
                c is CompProperties_UnfinishedRocket || c is CompProperties_RocketBlueprint ||
                c is CompProperties_Launchable_TransportPod);
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

                bool isTargetRocket = t.def == rocketDef || LooksLikeRocket(t.def);
                if (isTargetRocket)
                {
                    t.Destroy(DestroyMode.Vanish);
                }
            }
        }

    }
}
