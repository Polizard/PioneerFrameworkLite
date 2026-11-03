using System;
using System.Collections.Generic;
using PioneerFramework.Components;
using PioneerFramework.Launch;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **月球·自动发射演示**（2026-10-27）：验证"各种天体表面自动发射"这条链。
    /// 流程：找月球 → `LaunchSite.TryResolve(bodyId)` → `ForBody`（SOI/邻居夹取）→
    /// 月球层 tile → 出生状态 → 真实体轴姿态 → 注册现成制导。
    /// 判据：站点/目标/推重比/注册/圆轨速度合理；飞行结果看 `[RocketLaunch]` 与遥测。
    /// </summary>
    public static class MoonLaunchAcceptanceRunner
    {
        private static int pass, fail;
        private const double LaunchAltitudeMeters = 10_000.0;

        public static void Run()
        {
            pass = 0; fail = 0;
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var ctrl = Current.Game?.GetComponent<RocketLaunchController>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (sg == null || ctrl == null || mgr == null)
            {
                Log.Message("[月发] SpaceGameComp / RocketLaunchController / CelestialBodyManager 缺失");
                return;
            }

            int moonId = FindMoon(mgr);
            Check(moonId > 0, "找到月球（hasSurface & Moon）", $"bodyId={moonId}");
            if (moonId <= 0) { Verdict(); return; }

            bool siteOk = LaunchSite.TryResolve(moonId, out LaunchSite site, out string siteReason);
            Check(siteOk, "站点解析", siteOk ? site.Describe() : siteReason);
            if (!siteOk) { Verdict(); return; }

            LaunchGuidanceParams prms = LaunchGuidanceParams.ForBody(site.TargetInputs, out LaunchTargetResult target);
            Check(target.Feasible, "目标可行", target.Feasible ? $"h={target.TargetAltitude / 1000.0:F0}km vC={target.VCircular:F0}m/s" : target.Reason);
            if (!target.Feasible) { Verdict(); return; }
            Check(target.TargetRadius > site.Radius && target.TargetRadius < site.SoiApprox,
                "目标在表面与 SOI 之间",
                $"R={site.Radius / 1000.0:F0}km rTarget={target.TargetRadius / 1000.0:F0}km SOI={site.SoiApprox / 1000.0:F0}km");
            Check(target.VCircular < Math.Sqrt(2.0 * site.Mu / target.TargetRadius),
                "圆轨速度低于逃逸速度", $"vC={target.VCircular:F0} vEsc={Math.Sqrt(2.0 * site.Mu / target.TargetRadius):F0}");

            var layer = GetSurfaceState.GetLayerForBody(mgr, moonId);
            Check(layer != null, "月球 PlanetLayer");
            if (layer == null) { Verdict(); return; }

            int tile = DebugShipSpawner.PickEquatorialTile(layer);
            var ll = layer.LongLatOf(tile);
            double latRad = ll.y * Math.PI / 180.0;
            double lonRad = ll.x * Math.PI / 180.0;
            var surface = GetSurfaceState.SurfaceState(site.Radius, latRad, lonRad, site.Omega, TimeScaleManager.GetPhysicsTickSafe());
            var birth = GetSurfaceState.SurfaceStateAbove(surface, LaunchAltitudeMeters);
            // 体心系 → 世界系（非根天体必须加星历偏移；`CurrentOrbit` 是世界系）。
            var (posWorld, velWorld) = site.ToWorld(birth.pos, birth.vel, TimeScaleManager.GetPhysicsTickSafe());

            // 真实体轴：机头 +Z → 当地径向朝上。
            DVector3 up0 = birth.pos.normalized;
            double cosTilt = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(DVector3.forward, up0)));
            DQuaternion rot = cosTilt > 1.0 - 1e-9 ? DQuaternion.identity
                : cosTilt < -1.0 + 1e-9 ? DQuaternion.AngleAxis(Math.PI, DVector3.up)
                : DQuaternion.AngleAxis(Math.Acos(cosTilt), DVector3.Cross(DVector3.forward, up0).normalized);

            // 单级探针：干 1000 + 工质 2000，ve 3000 / ṁ 60 ⇒ F=180 kN；月球 g≈1.6 ⇒ TWR≈37。
            var parts = new List<SpacePart>
            {
                Part(1, hull: 1000f, control: true),
                Part(2, tank: true, capacity: 2000.0, fuel: 2000.0),
                Part(3, ve: 3000f, flow: 60f),
            };

            int gid = sg.AllocGroupId();
            var g = new SpaceEntityGroup(gid, Faction.OfPlayer?.loadID ?? 0)
            { IsActive = true, IsMovable = true, IsCollidable = true, CelestialBodyId = site.BodyId };
            g.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)posWorld,
                velocitySystem = (SystemVec3)velWorld,
                rotation = rot,
            };
            g.exemptPlanetCollision = true;
            sg.RegisterGroup(g);

            var e = new SpaceEntity(sg.AllocEntityId(), g.FactionId)
            { GroupId = gid, HasMap = false, IsMovable = true, IsAffectedByGravity = true, CanInteract = false };
            e.SetParts(parts);
            sg.RegisterEntity(e);

            double dryMass = e.AggregatedStats.mass;
            double thrustN = 3000.0 * 60.0;
            double weightN = (dryMass + 2000.0) * target.SurfaceGravity;
            Check(thrustN > weightN, "TWR > 1（按月球 g）", $"F={thrustN / 1000.0:F0}kN W={weightN / 1000.0:F1}kN TWR={thrustN / Math.Max(weightN, 1e-6):F1}");

            bool ok = ctrl.RegisterLaunch(g, site.BodyId, prms);
            Check(ok, "制导注册", ok ? $"目标 R+{target.TargetAltitude / 1000.0:F0}km" : "失败");

            // 主推轴是**船体系**：先旋到世界系再和当地径向比（2026-10-27 修：原先漏了这一步）。
            DVector3 axisBody = e.GetMainThrustAxisShip();
            DVector3 axisWorld = DVector3.Rotate(axisBody, rot);
            Check(DVector3.Dot(axisWorld, up0) > 0.99, "机头朝上（世界系 +Z→径向）",
                $"dot={DVector3.Dot(axisWorld, up0):F3}");

            Log.Message($"[月发] 飞行已开始：G{gid} body={site.BodyId} 干重={dryMass:F0}kg 工质=2000kg "
                + $"hTarget={target.TargetAltitude / 1000.0:F0}km vC={target.VCircular:F0}m/s");
            Verdict();
        }

        private static int FindMoon(CelestialBodyManager mgr)
        {
            var members = mgr.GetSystemMembers(1);
            if (members != null)
            {
                for (int i = 0; i < members.Count; i++)
                {
                    var b = members[i];
                    if (b != null && b.bodyId != 1 && b.hasSurface && b.bodyType == CelestialBodyType.Moon)
                        return b.bodyId;
                }
            }
            var fallback = mgr.GetBodyById(2);
            return fallback != null && fallback.hasSurface ? fallback.bodyId : -1;
        }

        private static SpacePart Part(int id, float hull = 0f, bool control = false, bool tank = false,
            float ve = 0f, float flow = 0f, double capacity = 0.0, double fuel = 0.0)
        {
            var p = new SpacePart
            {
                partId = id,
                stageId = 0,
                defName = "moon_launch_demo",
                positionShip = new DVector3(0, 0, (3 - id) * 4.0),
                boundsSizeShip = new DVector3(3, 3, 4),
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
                thrustAxis = ThrustAxis.Back,
            });
            return p;
        }

        private static void Check(bool ok, string what, string detail = "")
        {
            if (ok) pass++; else fail++;
            Log.Message($"[月发] {(ok ? "PASS" : "FAIL")} {what}{(detail.Length > 0 ? "：" + detail : "")}");
        }

        private static void Verdict()
            => Log.Message($"[月发] 结论：{(fail == 0 ? "PASS" : "FAIL")}（PASS={pass} FAIL={fail}）");
    }
}
