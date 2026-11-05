using System;
using System.Collections.Generic;
using PioneerFramework.Components;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **按级号抛级验收**（2026-10-27）：自动造三级舰，连抛两次，核对件数/状态/推进/再点火/单级拒绝。
    /// 无分离器件；级号由设计标定（0 = 上面级）。
    /// </summary>
    public static class StageSeparationAcceptanceRunner
    {
        private static int pass, fail;

        public static void Run()
        {
            pass = 0; fail = 0;
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) { Log.Message("[分级] 无 SpaceGameComp"); return; }

            var parts = new List<SpacePart>
            {
                Part(1, 0, hull: 1000f, control: true),
                Part(2, 0, hull: 500f, tank: true),
                Part(3, 0, hull: 500f, engine: true),
                Part(4, 1, hull: 500f, tank: true),
                Part(5, 1, hull: 500f, engine: true),
                Part(6, 2, hull: 500f, tank: true),
                Part(7, 2, hull: 500f, engine: true),
            };

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
            var e = new SpaceEntity(sg.AllocEntityId(), g.FactionId)
            { GroupId = gid, HasMap = false, IsMovable = true, IsAffectedByGravity = true, CanInteract = true };
            e.SetParts(parts);
            sg.RegisterEntity(e);
            Check(e.Parts.Count == 7, "母舰 7 件", $"count={e.Parts.Count}");

            // 第一次：抛 stage 2
            var s2 = sg.TrySeparateStage(g, out string r1);
            Check(s2 != null, "第一次抛级（stage 2）", r1);
            if (s2 == null) { Verdict(); return; }
            Check(SameState(g, s2), "级 2 状态逐位一致", StateText(s2));
            Check(e.Parts.Count == 5, "母舰剩 5 件", $"count={e.Parts.Count}");
            var e2 = First(sg, s2);
            Check(e2 != null && e2.Parts.Count == 2, "级 2 有 2 件", $"count={e2?.Parts.Count}");
            Check(e2 != null && e2.PropulsionFromParts && e2.HasPropulsionData, "级 2 按零件合成推进");
            Check(e2 != null && e2.Parts[0].stageId == 2 && e2.Parts[1].stageId == 2, "级 2 全是 stage 2");

            // 第二次：抛 stage 1
            var s1 = sg.TrySeparateStage(g, out string r2);
            Check(s1 != null, "第二次抛级（stage 1）", r2);
            if (s1 != null) Check(SameState(g, s1), "级 1 状态逐位一致", StateText(s1));
            Check(e.Parts.Count == 3, "母舰剩 3 件（全 stage 0）", $"count={e.Parts.Count}");
            Check(e.Parts.Count == 3 && e.Parts[0].stageId == 0 && e.Parts[1].stageId == 0 && e.Parts[2].stageId == 0,
                "母舰只剩 stage 0");

            // 母舰仍能点火（控制核心在上三级里）
            g.StartBurn(new DVector3(10, 0, 0));
            Check(g.HasActiveBurn, "抛级后母舰仍可点火",
                $"blockedPower={g.powerThermal.BurnIgnitionBlockedByPower} blockedControl={g.BurnIgnitionBlockedNoControl}");
            g.EndBurn();

            // 单级拒绝
            var none = sg.TrySeparateStage(g, out string r3);
            Check(none == null && r3 == "single-stage", "单级船拒绝抛级", r3);

            Verdict();
        }

        private static SpacePart Part(int id, int stage, float hull, bool control = false, bool tank = false, bool engine = false)
        {
            var p = new SpacePart
            {
                partId = id,
                stageId = stage,
                defName = "stage_test",
                positionShip = new DVector3(0, -id * 3.0, 0),
                boundsSizeShip = new DVector3(2, 2, 2),
            };
            p.AddComponent(new HullMassComponent { mass = hull });
            if (control) p.AddComponent(new ControlCoreComponentData());
            if (tank) p.AddComponent(new PropellantTankComponentData
            { propellantDefName = "PF_Propellant_Chemical", capacityKg = 1000.0, massKg = 500.0 });
            if (engine) p.AddComponent(new EngineComponentData
            { exhaustVelocity = 3000f, massFlowRate = 10f, propellantDefName = "PF_Propellant_Chemical", thrustAxis = ThrustAxis.Right });
            return p;
        }

        private static SpaceEntity? First(SpaceGameComp sg, SpaceEntityGroup g)
        {
            foreach (int eid in g.GetEntityIds())
            {
                var e = sg.GetEntityById(eid);
                if (e != null) return e;
            }
            return null;
        }

        private static bool SameState(SpaceEntityGroup a, SpaceEntityGroup b)
            => a.CurrentOrbit.positionSystem.v.x == b.CurrentOrbit.positionSystem.v.x
            && a.CurrentOrbit.positionSystem.v.y == b.CurrentOrbit.positionSystem.v.y
            && a.CurrentOrbit.positionSystem.v.z == b.CurrentOrbit.positionSystem.v.z
            && a.CurrentOrbit.velocitySystem.v.x == b.CurrentOrbit.velocitySystem.v.x
            && a.CurrentOrbit.velocitySystem.v.y == b.CurrentOrbit.velocitySystem.v.y
            && a.CurrentOrbit.velocitySystem.v.z == b.CurrentOrbit.velocitySystem.v.z
            && a.CurrentOrbit.rotation == b.CurrentOrbit.rotation;

        private static string StateText(SpaceEntityGroup g)
            => $"pos=({g.CurrentOrbit.positionSystem.v.x:F0},{g.CurrentOrbit.positionSystem.v.y:F0},{g.CurrentOrbit.positionSystem.v.z:F0})";

        private static void Check(bool ok, string what, string detail = "")
        {
            if (ok) pass++; else fail++;
            Log.Message($"[分级] {(ok ? "PASS" : "FAIL")} {what}{(detail.Length > 0 ? "：" + detail : "")}");
        }

        private static void Verdict()
            => Log.Message($"[分级] 结论：{(fail == 0 ? "PASS" : "FAIL")}（PASS={pass} FAIL={fail}）");
    }
}
