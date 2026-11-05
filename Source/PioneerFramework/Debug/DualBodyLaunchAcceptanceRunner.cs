using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PioneerFramework.Components;
using PioneerFramework.Launch;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **双天体 × 10 发爬升验收**（2026-10-29 用户令："两个天体各射 10 发"）。
    ///
    /// 目的：把"收尾锁存 + 上升律"的一般性**在游戏里**跑一遍——台架（`LaunchGuidanceCoreTests`）只证明纯数学，
    /// 这里走真实链路：`LaunchSite` 解析 → `ForBody` 参数推导 → 建 group/实体 → `RocketLaunchController` 注册 →
    /// 每步 `SetBurnCommand` → 抛级 → 圆化。
    ///
    /// 每个天体 10 发：纬度 −60°…+60° 均匀铺开、经度每发错开 36°（不共点、不互撞）。
    /// 判据（**异步**，由 `Tick` 结算）：
    ///   · PASS = `group.IsCircularOrbit`（进圆轨）；
    ///   · FAIL = 群组消失（坠毁）、`landed`（落回地面）、或超时。
    /// 结算时打印逐发读数 + 汇总（PASS/FAIL），并写 `Log` 一行总账。
    /// </summary>
    public static class DualBodyLaunchAcceptanceRunner
    {
        private const int PerBody = 10;
        private const double LaunchAltitudeMeters = 10_000.0;
        /// <summary>单发超时（**真实** tick；3 倍速下约 11 分钟）。</summary>
        private const int TimeoutRealTicks = 120_000;

        private sealed class Arm
        {
            public int GroupId = -1;
            public int BodyId;
            public double LatDeg;
            public double RTarget;
            public double TolR;
            public int StartTick;
            public bool Done;
            public string Result = "";
        }

        private static readonly List<Arm> arms = new();
        private static bool running;
        private static int pass, fail;

        /// <summary>一次性起 20 发（地球 10 + 月球 10）。</summary>
        public static void Stage()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var ctrl = Current.Game?.GetComponent<RocketLaunchController>();
            if (sg == null || ctrl == null) { Log.Warning("[双体10发] 不在太空场景"); return; }

            arms.Clear();
            pass = 0; fail = 0;
            double tick = TimeScaleManager.GetPhysicsTickSafe();
            int now = Find.TickManager.TicksGame;

            var sb = new StringBuilder();
            sb.Append("[双体10发] ═══ 地球 10 发 + 月球 10 发（纬度 −60°…+60°）═══");

            int[] bodies = { 1, 2 };
            for (int b = 0; b < bodies.Length; b++)
            {
                int bodyId = bodies[b];
                if (!LaunchSite.TryResolve(bodyId, out LaunchSite site, out string why))
                {
                    Log.Warning($"[双体10发] 天体 {bodyId} 解析失败：{why}");
                    continue;
                }

                var prms = LaunchGuidanceParams.ForBody(site.TargetInputs, out var target);
                if (!target.Feasible)
                {
                    Log.Warning($"[双体10发] 天体 {bodyId} 目标不可行（R={site.Radius:F0}）");
                    continue;
                }

                sb.Append(CultureInfo.InvariantCulture, $"\n  · {site.Describe()} → h={target.TargetAltitude / 1000.0:F0}km vC={target.VCircular:F0}");

                for (int i = 0; i < PerBody; i++)
                {
                    double latDeg = -60.0 + 120.0 * i / (PerBody - 1);
                    double latRad = latDeg * Math.PI / 180.0;
                    double lonRad = (i * 36.0) * Math.PI / 180.0;

                    var surface = GetSurfaceState.SurfaceState(site.Radius, latRad, lonRad, site.Omega, tick);
                    var birth = GetSurfaceState.SurfaceStateAbove(surface, LaunchAltitudeMeters);
                    var (posWorld, velWorld) = site.ToWorld(birth.pos, birth.vel, tick);

                    // 机头 +Z → 当地径向朝上（与 `LaunchService` 同一口径）
                    DVector3 up0 = birth.pos.normalized;
                    double cosTilt = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(DVector3.forward, up0)));
                    DQuaternion rot = cosTilt > 1.0 - 1e-9 ? DQuaternion.identity
                        : cosTilt < -1.0 + 1e-9 ? DQuaternion.AngleAxis(Math.PI, DVector3.up)
                        : DQuaternion.AngleAxis(Math.Acos(cosTilt), DVector3.Cross(DVector3.forward, up0).normalized);

                    int gid = sg.AllocGroupId();
                    var g = new SpaceEntityGroup(gid, Faction.OfPlayer?.loadID ?? 0)
                    { IsActive = true, IsMovable = true, IsCollidable = true, CelestialBodyId = bodyId };
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
                    e.SetParts(BuildRocketParts());
                    e.SetRotation((UnityEngine.Quaternion)rot);
                    sg.RegisterEntity(e);

                    if (!ctrl.RegisterLaunch(g, bodyId, prms))
                    {
                        Log.Warning($"[双体10发] G{gid} 制导注册失败（body={bodyId} lat={latDeg:F0}°）");
                        continue;
                    }

                    arms.Add(new Arm
                    {
                        GroupId = gid,
                        BodyId = bodyId,
                        LatDeg = latDeg,
                        RTarget = prms.rTarget,
                        TolR = prms.tolR,
                        StartTick = now,
                    });
                }
            }

            sb.Append(CultureInfo.InvariantCulture, $"\n  共 {arms.Count} 发已登记；PASS = 进圆轨，FAIL = 坠毁/落地/超时");
            Log.Message(sb.ToString());
            running = arms.Count > 0;
        }

        /// <summary>单级测试火箭：控制核心 + 9 台引擎（ve 3300 / ṁ 200）+ 400 t 罐。</summary>
        private static List<SpacePart> BuildRocketParts()
        {
            var parts = new List<SpacePart>();
            int id = 1;

            var control = new SpacePart
            {
                partId = id++, defName = "dual_body_probe", stageId = 0,
                positionShip = DVector3.zero, boundsSizeShip = new DVector3(2, 2, 2),
            };
            control.AddComponent(new HullMassComponent { mass = 100f });
            control.AddComponent(new ControlCoreComponentData());
            parts.Add(control);

            var tank = new SpacePart
            {
                partId = id++, defName = "dual_body_probe", stageId = 0,
                positionShip = new DVector3(0, 0, -22), boundsSizeShip = new DVector3(4, 4, 40),
            };
            tank.AddComponent(new HullMassComponent { mass = 20_000f });
            tank.AddComponent(new PropellantTankComponentData
            { propellantDefName = "PF_Propellant_Chemical", capacityKg = 400_000.0, massKg = 400_000.0 });
            parts.Add(tank);

            for (int i = 0; i < 9; i++)
            {
                var eng = new SpacePart
                {
                    partId = id++, defName = "dual_body_probe", stageId = 0,
                    positionShip = new DVector3(0, 0, -44), boundsSizeShip = new DVector3(2, 2, 3),
                };
                eng.AddComponent(new HullMassComponent { mass = 500f });
                eng.AddComponent(new EngineComponentData
                {
                    exhaustVelocity = 3300f,
                    massFlowRate = 200f,
                    propellantDefName = "PF_Propellant_Chemical",
                    thrustAxis = ThrustAxis.Back,
                });
                parts.Add(eng);
            }
            return parts;
        }

        /// <summary>每 tick 推进（由 `SpaceGameComp.GameComponentTick` 调用；空闲时第一行返回）。</summary>
        public static void Tick()
        {
            if (!running) return;
            int now = Find.TickManager.TicksGame;
            if (now % 30 != 0) return;   // 每 30 tick 结算一次，零成本

            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (sg == null) { running = false; return; }

            for (int i = 0; i < arms.Count; i++)
            {
                var a = arms[i];
                if (a.Done) continue;

                var g = sg.GetGroupById(a.GroupId);
                if (g == null)
                {
                    a.Done = true; a.Result = "FAIL 群组消失（坠毁）"; fail++;
                    Log.Message($"[双体10发] G{a.GroupId} body={a.BodyId} lat={a.LatDeg:F0}° → {a.Result}");
                    continue;
                }

                if (g.IsCircularOrbit)
                {
                    a.Done = true; a.Result = $"PASS 圆轨 r={g.CurrentOrbit.positionSystem.v.magnitude / 1000.0:F0}km"; pass++;
                    Log.Message($"[双体10发] G{a.GroupId} body={a.BodyId} lat={a.LatDeg:F0}° → {a.Result}");
                    continue;
                }

                if (g.landed)
                {
                    a.Done = true; a.Result = "FAIL 落回地面（未入轨）"; fail++;
                    Log.Message($"[双体10发] G{a.GroupId} body={a.BodyId} lat={a.LatDeg:F0}° → {a.Result}");
                    continue;
                }

                if (now - a.StartTick > TimeoutRealTicks)
                {
                    double r = g.CurrentOrbit.positionSystem.v.magnitude;
                    double v = g.CurrentOrbit.velocitySystem.v.magnitude;
                    a.Done = true; a.Result = $"FAIL 超时 r={r / 1000.0:F0}km |v|={v:F0}"; fail++;
                    Log.Message($"[双体10发] G{a.GroupId} body={a.BodyId} lat={a.LatDeg:F0}° → {a.Result}");
                }
            }

            int done = 0;
            for (int i = 0; i < arms.Count; i++) if (arms[i].Done) done++;
            if (done < arms.Count) return;

            running = false;
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"[双体10发] 结算：PASS={pass} FAIL={fail}（共 {arms.Count} 发）");
            for (int i = 0; i < arms.Count; i++)
                sb.Append(CultureInfo.InvariantCulture, $"\n  G{arms[i].GroupId} body={arms[i].BodyId} lat={arms[i].LatDeg,5:F1}° → {arms[i].Result}");
            Log.Message(sb.ToString());
            Messages.Message($"[双体10发] PASS={pass} / FAIL={fail}（明细见日志）",
                fail == 0 ? MessageTypeDefOf.TaskCompletion : MessageTypeDefOf.NegativeEvent, false);
        }
    }
}

