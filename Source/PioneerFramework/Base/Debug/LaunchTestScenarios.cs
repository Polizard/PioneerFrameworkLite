using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using PioneerFramework.Launch;
using PioneerFramework.Components;

namespace PioneerFramework
{
    /// <summary>
    /// 火箭发射/爬升段测试场景（调试/验收用，与游戏 UI 解耦）。
    /// 不依赖地面发射台：直接构造「无 Map 纯物理测试火箭」（自带大储罐 PropulsionData，
    /// 绕过 debug spec 1000kg 燃料不足的限制），在天体表面上方经纬度出生，注册 RocketLaunchController
    /// 走完整爬升链（持续引导 → 到达窗口 → BeginCircularize 入轨）。
    /// </summary>
    public static class LaunchTestScenarios
    {
        public const int EARTH_BODY_ID = 1;

        /// <summary>测试火箭出生离地高度 (m)。</summary>
        public const double TestLaunchAltitude = 10000.0;

        /// <summary>测试火箭干质量（结构，kg）——「强力大火箭」重载规格。</summary>
        public const double TestDryMass = 4000.0;

        /// <summary>
        /// 强力大火箭引擎：ve=3500 m/s（化学），ṁ=300 kg/s → F=1.05 MN。
        /// TWR = F/(m·g) = 1050000/(54000×8) ≈ 2.43。
        /// </summary>
        public const float TestVe = 3500f;
        public const float TestMassFlow = 300f;
        /// <summary>
        /// 初始工质 50000 kg（capacity 52000，质量比 13.5 → 理论 ΔV≈9330 m/s，实测 50t 所需仅 ~7100）。
        /// 设计 09-02 已定论：70t 时 TWR≈1.77 过低，重力损失≈3500m/s 吃光全部余量（实测烧 10135 vs 理想 6600），
        /// 制导目标常数改 vis-viva 无效。**必须用 50t（TWR 2.43）健康规格** → 高纬殖民地实测到达后仍剩 ~2.9t 燃料。
        /// </summary>
        public const float TestFuelKg = 50000f;
        public const float TestCapacityKg = 52000f;

        /// <summary>构造一份测试火箭推进（全矢量虚拟引擎，RCS 风格：任意方向出力免对准）。</summary>
        public static List<PropulsionData> TestPropulsion()
        {
            var prop = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Chemical");
            return new List<PropulsionData>
            {
                new PropulsionData { propellant = prop, ve = TestVe, massFlow = TestMassFlow, mass = TestFuelKg, capacity = TestCapacityKg }
            };
        }

        /// <summary>
        /// 部署一次爬升测试：从当前地面地图（map）tile 上方出生纯物理火箭 → 注册爬升段。
        /// 与设计 §3 天地映射一致：tile → 经纬度 → ECI 状态（发射台 = 所在 tile 中心）。
        /// </summary>
        /// <param name="map">地面地图（取 tile 经纬度）。null 用传入的 latRad/lonRad0（赤道顺行等）。</param>
        /// <param name="latRad">纬度 (rad)。仅在 map 为 null 时生效。</param>
        /// <param name="lonRad0">t=0 经度 (rad)。仅在 map 为 null 时生效。</param>
        public static int RunAscentTest(Map map, double latRad = 0.0, double lonRad0 = 0.0)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) { Log.Message("[RocketLaunchTest] SpaceGameComp 缺失"); return -1; }
            var launchCtrl = Current.Game?.GetComponent<RocketLaunchController>();
            if (launchCtrl == null) { Log.Message("[RocketLaunchTest] RocketLaunchController 缺失"); return -1; }

            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            double radius = GetSurfaceState.BodyRadius(mgr, EARTH_BODY_ID);
            double omega = GetSurfaceState.RotationOmega(mgr, EARTH_BODY_ID);
            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();

            // 出生经纬度：从当前地面地图 tile 起飞（tile → 经纬度 → ECI）；map 为 null 用传入方位角
            string launchSrcDesc = "自定义方位角";
            if (map != null)
            {
                var longLat = Find.WorldGrid.LongLatOf(map.Tile);
                latRad = longLat.y * Mathf.Deg2Rad;
                lonRad0 = longLat.x * Mathf.Deg2Rad;
                launchSrcDesc = string.Format("当前Map(uid{0}, tile {1})", map.uniqueID, map.Tile);
                // 世界视图发射点标记（红色正方体）钉在这个 tile 上
                SpaceGroupVisualManager.SetLaunchSiteTile(map.Tile);
            }
            var surface = GetSurfaceState.SurfaceState(radius, latRad, lonRad0, omega, physicsTick);
            var birth = GetSurfaceState.SurfaceStateAbove(surface, TestLaunchAltitude);
            DVector3 pos = birth.pos;
            DVector3 vel = birth.vel;

            // ── 无 Map 纯物理火箭（CreateTransitVehicle 同构）──
            int groupId = spaceGame.AllocGroupId();
            int entityId = spaceGame.AllocEntityId();
            int factionId = Faction.OfPlayer?.loadID ?? 0;

            var group = new SpaceEntityGroup(groupId, factionId)
            {
                IsActive = true,
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = EARTH_BODY_ID
            };
            group.CurrentOrbit = new OrbitState { position = pos, velocity = vel, rotation = Quaternion.identity };
            spaceGame.RegisterGroup(group);

            var entity = new SpaceEntity(entityId, factionId)
            {
                HasMap = false,
                IsMapArchived = false,
                IsCollidable = true,
                IsMovable = true,
                CanInteract = false,
                IsAffectedByGravity = true
            };
            entity.GroupId = groupId;
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(Quaternion.identity);   // 不设就留 (0,0,0,0)：物理端恰好等价单位姿态，但已不是合法四元数
            var part = new SpacePart
            {
                partId = 1,
                defName = "PF_ShipPart_TestRocket",
                positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(30, 30, 30),
                hp = 500f,
                isIndestructible = false
            };
            part.AddComponent(new HullMassComponent { mass = (float)TestDryMass });
            // [V7] 火箭的导航雷达（500 km）
            part.AddComponent(new SensorArrayComponentData { passiveRangeMeters = 500_000f });
            entity.SetParts(new List<SpacePart> { part });
            foreach (var p in TestPropulsion())
                entity.AddPropulsion(p);
            spaceGame.RegisterEntity(entity);
            group.AddEntity(entityId);

            // ── 注册爬升段制导 ──
            if (!launchCtrl.RegisterLaunch(group, EARTH_BODY_ID, LaunchGuidanceParams.CreateDefault(radius)))
            {
                spaceGame.DestroyGroup(groupId);
                Log.Message("[RocketLaunchTest] 注册爬升段失败");
                return -1;
            }

            var stats = spaceGame.GetGroupDeltaV(group);
            Log.Message(string.Format(
                "[RocketLaunchTest] 爬升测试火箭已部署：出生={0} φ={1:F1}° λ0={2:F1}° 位=({3:F0},{4:F0},{5:F0})km 速={6:F1}m/s R={7:F0}km 目标R={8:F0}km ΔV余={9:F0}m/s",
                launchSrcDesc, latRad * Mathf.Rad2Deg, lonRad0 * Mathf.Rad2Deg,
                pos.x / 1000.0, pos.y / 1000.0, pos.z / 1000.0, vel.magnitude,
                pos.magnitude / 1000.0, (radius + 500000.0) / 1000.0, stats.remainingDv));
            Messages.Message("爬升测试火箭已起飞，爬升段制导开始。", MessageTypeDefOf.TaskCompletion, false);
            return groupId;
        }
    }
}