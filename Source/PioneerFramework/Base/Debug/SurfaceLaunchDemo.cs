using System;
using System.Collections.Generic;
using PioneerFramework.Components;
using PioneerFramework.Launch;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **三级火箭·地表发射演示**（2026-10-27 v4）：
    /// · 站点按**地图所在天体**解析（`LaunchSite`，不再写死地球）；
    /// · 目标高度由 `LaunchTargeting` 夹取（SOI/邻居/大气），不可行则拒绝；
    /// · 制导参数 `LaunchGuidanceParams.ForBody`（按天体 R/μ/大气标定）；
    /// · 复用现成直接入轨链路（`RocketLaunchController`，目标 R+hTarget）；
    /// · 真实体轴：机头 +Z、尾部 −Z、引擎 `Back`（喷口 −Z ⇒ 推力 +Z），姿态把 +Z 转到当地径向；
    /// · 抛级按"当前级油尽"触发。
    /// </summary>
    public static class SurfaceLaunchDemo
    {
        private const double LaunchAltitudeMeters = 10_000.0;

        public static void Run()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var ctrl = Current.Game?.GetComponent<RocketLaunchController>();
            if (sg == null || ctrl == null) { Log.Message("[发射] SpaceGameComp / RocketLaunchController 缺失"); return; }

            var map = Find.AnyPlayerHomeMap ?? Find.CurrentMap;
            if (map == null) { Log.Message("[发射] 没有可用的殖民地地图"); return; }

            if (!LaunchSite.TryResolve(map, out LaunchSite site, out string siteReason))
            {
                Log.Message($"[发射] 站点无法解析：{siteReason}");
                return;
            }
            LaunchGuidanceParams prms = LaunchGuidanceParams.ForBody(site.TargetInputs, out LaunchTargetResult target);
            if (!target.Feasible)
            {
                Log.Message($"[发射] 目标不可行：{target.Reason} | {site.Describe()}");
                return;
            }

            // 出生：地图 tile → 经纬度 → ECI（含地表共转速度），与真发射同口径。
            var (posBody, birthVelBody) = GetSurfaceState.GroundStateAtLaunch(map, LaunchAltitudeMeters, site.BodyId);
            var (pos, birthVel) = site.ToWorld(posBody, birthVelBody, TimeScaleManager.GetPhysicsTickSafe());

            // 机头 +Z → 当地径向朝上（任意纬度通用）。
            DVector3 up0 = pos.normalized;
            double cosTilt = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(DVector3.forward, up0)));
            DQuaternion rot;
            if (cosTilt > 1.0 - 1e-9) rot = DQuaternion.identity;
            else if (cosTilt < -1.0 + 1e-9) rot = DQuaternion.AngleAxis(Math.PI, DVector3.up);
            else rot = DQuaternion.AngleAxis(Math.Acos(cosTilt), DVector3.Cross(DVector3.forward, up0).normalized);

            var parts = new List<SpacePart>
            {
                P(1, 0, +12f, 3f, 4f, hull: 1000f, control: true),
                P(2, 0, +9f, 3f, 6f, tank: true, capacity: 1360.0, fuel: 1360.0),
                P(3, 0, +6f, 3f, 2f, ve: 3500f, flow: 30f),
                P(4, 1, +2f, 3f, 8f, tank: true, capacity: 5250.0, fuel: 5250.0),
                P(5, 1, -3f, 3f, 2f, ve: 3500f, flow: 80f),
                P(6, 2, -9f, 3f, 10f, tank: true, capacity: 15100.0, fuel: 15100.0),
                P(7, 2, -15f, 3f, 2f, ve: 3500f, flow: 120f),
            };

            // 上面级 RCS（2026-10-27，A 路线）：独立肼罐 + 全矢量小推力器（不参与主推轴聚合）。
            var rcsEngine = new SpacePart
            {
                partId = 8, stageId = 0, defName = "launch_demo_rcs",
                positionShip = new DVector3(0, 0, 7), boundsSizeShip = new DVector3(3, 3, 2),
            };
            rcsEngine.AddComponent(new EngineComponentData
            {
                exhaustVelocity = 1500f, massFlowRate = 2f,
                propellantDefName = "PF_Propellant_Hydrazine", isVectoredRcs = true,
            });
            var rcsTank = new SpacePart
            {
                partId = 9, stageId = 0, defName = "launch_demo_rcs_tank",
                positionShip = new DVector3(0, 0, 4.5), boundsSizeShip = new DVector3(2, 2, 2),
            };
            rcsTank.AddComponent(new PropellantTankComponentData
            {
                propellantDefName = "PF_Propellant_Hydrazine",
                capacityKg = 200.0, massKg = 200.0, rcsSupply = true,
            });
            parts.Add(rcsEngine);
            parts.Add(rcsTank);

            int gid = sg.AllocGroupId();
            var g = new SpaceEntityGroup(gid, Faction.OfPlayer?.loadID ?? 0)
            { IsActive = true, IsMovable = true, IsCollidable = true, CelestialBodyId = site.BodyId };
            g.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)pos,
                velocitySystem = (SystemVec3)birthVel,
                rotation = rot,
            };
            g.exemptPlanetCollision = true;
            sg.RegisterGroup(g);

            var e = new SpaceEntity(sg.AllocEntityId(), g.FactionId)
            { GroupId = gid, HasMap = false, IsMovable = true, IsAffectedByGravity = true, CanInteract = false };
            e.SetParts(parts);
            sg.RegisterEntity(e);

            DVector3 up = pos.normalized;
            DVector3 bodyAxis = e.GetMainThrustAxisShip();
            var stats = sg.GetGroupDeltaV(g);
            Log.Message($"[发射] 站点 {site.Describe()} → h={target.TargetAltitude / 1000.0:F0}km vC={target.VCircular:F0}m/s"
                + $"{(string.IsNullOrEmpty(target.Reason) ? "" : " | " + target.Reason)}");
            Log.Message($"[发射] 三级火箭部署：R0={pos.magnitude / 1000.0:F0}km 干重={e.AggregatedStats.mass:F0}kg "
                + $"ΔV余={stats.remainingDv:F0}m/s 网络={e.propulsion.Count} 机头=({bodyAxis.x:F2},{bodyAxis.y:F2},{bodyAxis.z:F2}) "
                + $"径向=({up.x:F2},{up.y:F2},{up.z:F2}) "
                + $"夹角={Math.Acos(Math.Max(-1, Math.Min(1, DVector3.Dot(bodyAxis, up)))) * 180.0 / Math.PI:F1}°");

            bool ok = ctrl.RegisterLaunch(g, site.BodyId, prms);
            Log.Message($"[发射] 制导注册={(ok ? "成功" : "失败")}（目标 R+{target.TargetAltitude / 1000.0:F0} km）");
            if (!ok) return;

            sg.AddAutoStageFuelPlan(gid, fuelFraction: 0.02, stages: 2, minSeconds: 20.0);
        }

        private static SpacePart P(int id, int stage, float z, float w, float len,
            float hull = 0f, bool control = false, bool tank = false,
            float ve = 0f, float flow = 0f, double capacity = 0.0, double fuel = 0.0)
        {
            var p = new SpacePart
            {
                partId = id,
                stageId = stage,
                defName = "launch_demo",
                positionShip = new DVector3(0, 0, z),   // 沿体轴 +Z 叠：机头 +Z、尾部 −Z
                boundsSizeShip = new DVector3(w, w, len),
            };
            if (hull > 0f) p.AddComponent(new HullMassComponent { mass = hull });
            if (control) p.AddComponent(new ControlCoreComponentData());
            if (tank) p.AddComponent(new PropellantTankComponentData
            { propellantDefName = "PF_Propellant_Chemical", capacityKg = capacity, massKg = fuel });
            if (flow > 0f) p.AddComponent(new EngineComponentData
            {
                exhaustVelocity = ve,
                massFlowRate = flow,
                propellantDefName = "PF_Propellant_Chemical",
                thrustAxis = ThrustAxis.Back,   // 喷口 −Z ⇒ 推力 +Z（机头方向）
            });
            return p;
        }
    }
}
