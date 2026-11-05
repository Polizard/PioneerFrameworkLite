using System;
using System.Collections.Generic;
using PioneerFramework.Components;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **动量轮 + RCS 卸载验收**（2026-10-26）：自动建舰，三档跑批。
    /// 判据：对称引擎 τ≈0 ⇒ 轮子不动、不耗油；偏置引擎 ⇒ 轮子积累 + 扣油；
    /// RCS 空 ⇒ 不卸载、轮子撞容量上限夹紧。
    /// </summary>
    public static class WheelAcceptanceRunner
    {
        private static int pass, fail;

        public static void Run()
        {
            pass = 0; fail = 0;
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) { Log.Message("[动量轮] 无 SpaceGameComp"); return; }

            RunCase(sg, "对称引擎", symmetric: true, tankMassKg: 1000.0, steps: 1);
            RunCase(sg, "偏置引擎", symmetric: false, tankMassKg: 1000.0, steps: 1);
            RunCase(sg, "RCS 空", symmetric: false, tankMassKg: 0.0, steps: 10);

            Log.Message($"[动量轮] 结论：{(fail == 0 ? "PASS" : "FAIL")}（PASS={pass} FAIL={fail}）");
        }

        private static void RunCase(SpaceGameComp sg, string name, bool symmetric, double tankMassKg, int steps)
        {
            Log.Message($"[动量轮] ── 场景：{name}（steps={steps}）──");

            int gid = sg.AllocGroupId();
            var g = new SpaceEntityGroup(gid, Faction.OfPlayer?.loadID ?? 0)
            { IsActive = true, IsMovable = true, IsCollidable = true, CelestialBodyId = 1 };
            g.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)new DVector3(7_000_000, 0, 0),
                velocitySystem = (SystemVec3)new DVector3(0, 7500, 0),
                rotation = DQuaternion.identity,
            };
            sg.RegisterGroup(g);

            var tank = new PropellantTankComponentData
            { propellantDefName = "P", capacityKg = 1000.0, massKg = tankMassKg, rcsSupply = true, rcsEfficiency = 0.8f };

            var parts = new List<SpacePart>
            {
                Part(1, 0, 0, 0, new HullMassComponent { mass = 1000f }, null),
                Part(2, 0, 0, 0, new HullMassComponent { mass = 300f }, new ControlCoreComponentData()),
                Part(3, 0, 0, 0, new HullMassComponent { mass = 500f }, tank),
                Part(4, 0, 10, 0, new HullMassComponent { mass = 500f }, Engine()),
            };
            if (symmetric)
                parts.Add(Part(5, 0, -10, 0, new HullMassComponent { mass = 500f }, Engine()));

            var e = new SpaceEntity(sg.AllocEntityId(), g.FactionId)
            { GroupId = gid, HasMap = false, IsMovable = true, IsAffectedByGravity = true, CanInteract = true };
            e.SetParts(parts);
            sg.RegisterEntity(e);

            Check(e.wheelCapacity > 0.0, "轮子容量按质量给", $"cap={e.wheelCapacity:F0}");

            g.StartBurn(new DVector3(10, 0, 0));
            Check(g.HasActiveBurn, "点火成功（无欠电/失控门）",
                $"blockedPower={g.powerThermal.BurnIgnitionBlockedByPower} blockedControl={g.BurnIgnitionBlockedNoControl}");
            if (!g.HasActiveBurn) { return; }

            // 直接判根因：力矩本身（比看积分后的 L 干净，夹具错会当场红）
            var inertia = g.InertiaResult;
            DVector3 com = new(inertia.comX, inertia.comY, inertia.comZ);
            DVector3 tau = e.GetEngineTorqueAboutCom(com, out _);
            if (symmetric)
                Check(tau.magnitude < 1.0, "对称：力矩≈0", $"|τ|={tau.magnitude:F3} com.y={com.y:F3}");
            else
                Check(tau.magnitude > 1.0, "偏置：力矩≠0", $"|τ|={tau.magnitude:F0} com.y={com.y:F3}");

            double massBefore = tank.massKg;
            for (int i = 0; i < steps; i++) sg.StepAllGroupsWheel(2f);
            g.EndBurn();

            double L = e.wheelMomentum.magnitude;
            double used = massBefore - tank.massKg;

            if (symmetric)
            {
                Check(L < 1.0, "对称引擎：轮子不动", $"L={L:F3}");
                Check(used <= 1e-9, "对称引擎：不耗 RCS", $"used={used:F6}kg");
            }
            else if (tankMassKg <= 0.0)
            {
                Check(L > 0.0, "RCS 空：轮子仍积累", $"L={L:F0}");
                Check(Math.Abs(L - e.wheelCapacity) < 1e-3, "RCS 空：撞容量后夹紧",
                    $"L={L:F0} cap={e.wheelCapacity:F0}");
                Check(used <= 1e-9, "RCS 空：不耗油", $"used={used:F6}kg");
            }
            else
            {
                Check(L > 0.0 && L < 1_000_000.0, "偏置引擎：轮子积累", $"L={L:F0}");
                Check(used > 0.0, "偏置引擎：扣了 RCS 油", $"used={used:F6}kg");
                Check(L <= e.wheelCapacity + 1e-6, "未超容量", $"L={L:F0} cap={e.wheelCapacity:F0}");
            }
        }

        private static SpacePart Part(int id, double x, double y, double z,
            PartComponentData? hullOrComponent, PartComponentData? extra)
        {
            var p = new SpacePart
            {
                partId = id,
                defName = "wheel_test",
                positionShip = new DVector3(x, y, z),
                boundsSizeShip = new DVector3(2, 2, 2),
            };
            if (hullOrComponent != null) p.AddComponent(hullOrComponent);
            if (extra != null) p.AddComponent(extra);
            return p;
        }

        private static EngineComponentData Engine() => new()
        {
            exhaustVelocity = 3000f,
            massFlowRate = 10f,
            propellantDefName = "P",
            thrustAxis = ThrustAxis.Right,
        };

        private static void Check(bool ok, string what, string detail = "")
        {
            if (ok) pass++; else fail++;
            Log.Message($"[动量轮] {(ok ? "PASS" : "FAIL")} {what}{(detail.Length > 0 ? "：" + detail : "")}");
        }
    }
}
