using System.Collections.Generic;
using PioneerFramework.Components;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **三级火箭"升空序列"验收**（2026-10-27 用户令）：不是画曲线，而是验升空必须成立的链条：
    /// 活动级供能 → 点火 → 抛级 → 下一级自动接管 → 再点火 → 再抛 → 上面级 → 单级拒绝。
    /// 每级引擎参数不同（ve/ṁ），所以"切级"在读数上看得见。
    /// </summary>
    public static class ThreeStageLaunchAcceptanceRunner
    {
        private static int pass, fail;

        public static void Run()
        {
            pass = 0; fail = 0;
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) { Log.Message("[升空] 无 SpaceGameComp"); return; }

            var parts = new List<SpacePart>
            {
                Part(1, 0, hull: 1000f, control: true),
                Part(2, 0, hull: 500f, tank: true),
                Part(3, 0, hull: 500f, ve: 3000f, flow: 10f),
                Part(4, 1, hull: 500f, tank: true),
                Part(5, 1, hull: 500f, ve: 2000f, flow: 20f),
                Part(6, 2, hull: 500f, tank: true),
                Part(7, 2, hull: 500f, ve: 1000f, flow: 40f),
            };

            int gid = sg.AllocGroupId();
            var g = new SpaceEntityGroup(gid, Faction.OfPlayer?.loadID ?? 0)
            { IsActive = true, IsMovable = true, IsCollidable = true, CelestialBodyId = 1 };
            g.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)new DVector3(7_010_000, 0, 0),   // 地表上方 10 km
                velocitySystem = (SystemVec3)new DVector3(0, 7500, 0),
                rotation = DQuaternion.identity,
            };
            sg.RegisterGroup(g);
            var e = new SpaceEntity(sg.AllocEntityId(), g.FactionId)
            { GroupId = gid, HasMap = false, IsMovable = true, IsAffectedByGravity = true, CanInteract = true };
            e.SetParts(parts);
            sg.RegisterEntity(e);

            CheckPropulsion(e, "初始", expectVe: 1000f, expectFlow: 40f);
            CheckIgnite(g, "地面级点火");

            var s2 = sg.TrySeparateStage(g, out string r1);
            Check(s2 != null, "抛地面级（stage 2）", r1);
            if (s2 == null) { Verdict(); return; }
            Check(e.Parts.Count == 5, "母舰剩 5 件", $"count={e.Parts.Count}");
            CheckPropulsion(e, "中级接管", expectVe: 2000f, expectFlow: 20f);
            CheckIgnite(g, "中级点火");

            var s1 = sg.TrySeparateStage(g, out string r2);
            Check(s1 != null, "抛中级（stage 1）", r2);
            if (s1 == null) { Verdict(); return; }
            Check(e.Parts.Count == 3, "母舰剩 3 件（上面级）", $"count={e.Parts.Count}");
            CheckPropulsion(e, "上面级接管", expectVe: 3000f, expectFlow: 10f);
            CheckIgnite(g, "上面级点火");

            var none = sg.TrySeparateStage(g, out string r3);
            Check(none == null && r3 == "single-stage", "单级后拒绝再抛", r3);

            Verdict();
        }

        private static void CheckPropulsion(SpaceEntity e, string tag, float expectVe, float expectFlow)
        {
            bool ok = e.HasPropulsionData && e.propulsion.Count == 1
                && System.Math.Abs(e.propulsion[0].ve - expectVe) < 0.5f
                && System.Math.Abs(e.propulsion[0].massFlow - expectFlow) < 0.05f;
            Check(ok, $"{tag}：活动级供能",
                e.HasPropulsionData
                    ? $"ve={e.propulsion[0].ve:F0} flow={e.propulsion[0].massFlow:F1}（期望 {expectVe:F0}/{expectFlow:F1}）"
                    : "无推进");
        }

        private static void CheckIgnite(SpaceEntityGroup g, string tag)
        {
            g.StartBurn(new DVector3(10, 0, 0));
            Check(g.HasActiveBurn && !g.powerThermal.BurnIgnitionBlockedByPower && !g.BurnIgnitionBlockedNoControl,
                $"{tag}成功", $"burning={g.HasActiveBurn}");
            g.EndBurn();
        }

        private static SpacePart Part(int id, int stage, float hull,
            bool control = false, bool tank = false, float ve = 0f, float flow = 0f)
        {
            var p = new SpacePart
            {
                partId = id,
                stageId = stage,
                defName = "launch_test",
                positionShip = new DVector3(0, -id * 3.0, 0),
                boundsSizeShip = new DVector3(2, 2, 2),
            };
            p.AddComponent(new HullMassComponent { mass = hull });
            if (control) p.AddComponent(new ControlCoreComponentData());
            if (tank) p.AddComponent(new PropellantTankComponentData
            { propellantDefName = "PF_Propellant_Chemical", capacityKg = 1000.0, massKg = 800.0 });
            if (flow > 0f) p.AddComponent(new EngineComponentData
            {
                exhaustVelocity = ve,
                massFlowRate = flow,
                propellantDefName = "PF_Propellant_Chemical",
                thrustAxis = ThrustAxis.Right,
            });
            return p;
        }

        private static void Check(bool ok, string what, string detail = "")
        {
            if (ok) pass++; else fail++;
            Log.Message($"[升空] {(ok ? "PASS" : "FAIL")} {what}{(detail.Length > 0 ? "：" + detail : "")}");
        }

        private static void Verdict()
            => Log.Message($"[升空] 结论：{(fail == 0 ? "PASS" : "FAIL")}（PASS={pass} FAIL={fail}）");
    }
}
