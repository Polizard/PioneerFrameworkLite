using System.Collections.Generic;
using PioneerFramework.Components;
using PioneerFramework.GroundAssembly;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **载荷舱·套娃验收**（2026-10-26）：一键自建"带载荷舱的舰"，再连续释放两级，核对状态。
    ///
    /// 为什么要有它：目录里没有现成的"带载荷舱的舰"，而部署路径需要 `SpaceGameComp`（headless 测不了）
    /// ⇒ 用游戏内跑批器做自动验收。载荷设计**自身也含载荷舱**，所以二级释放就是"套娃"。
    ///
    /// 判据：注册设计 · 母舰 4 件 · 一级释放成功 · 子组位置/速度/姿态与母组**逐位相等** ·
    /// 母舰少一件 · 子组装出 4 件 · 子组也有载荷舱 · 二级释放成功 · 孙组状态与子组逐位相等。
    /// 只打读数，不改任何既有状态（除了它自己造的组）。
    /// </summary>
    public static class PayloadNestingAcceptanceRunner
    {
        private static int pass, fail;

        public static void Run()
        {
            pass = 0;
            fail = 0;
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null)
            {
                Log.Message("[载荷套娃] 无 SpaceGameComp（不在游戏中）");
                return;
            }

            // 载荷设计 = 控制核心 + 罐 + 引擎 + 载荷舱（自身含舱 ⇒ 可再释放一级）
            var design = new RocketDesignSpec
            {
                parts = new List<DesignPartSpec>
                {
                    new() { defName = "PF_ShipPart_ControlCore_Debug", category = "control", quantity = 1 },
                    new() { defName = "PF_ShipPart_Tank_Debug", category = "tank", quantity = 1 },
                    new() { defName = "PF_ShipPart_Thruster_Debug", category = "thruster", quantity = 1 },
                    new() { defName = "PF_ShipPart_PayloadBay_Debug", category = "payload-bay", quantity = 1 },
                },
            };
            int designId = sg.RegisterPayloadDesign(design);
            Check(designId > 0, "注册载荷设计", $"id={designId}");

            var parentParts = DesignPartAssembler.BuildParts(design, 1, 1, out _,
                includeUnknownDefs: false, includeCabin: false);
            Check(parentParts.Count == 4, "母舰装出 4 件", $"count={parentParts.Count}");

            int gid = sg.AllocGroupId();
            var parent = new SpaceEntityGroup(gid, Faction.OfPlayer?.loadID ?? 0)
            {
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = 1,
            };
            parent.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)new DVector3(7_000_000, 0, 0),
                velocitySystem = (SystemVec3)new DVector3(0, 7500, 0),
                rotation = DQuaternion.identity,
            };
            sg.RegisterGroup(parent);

            var parentEntity = new SpaceEntity(sg.AllocEntityId(), parent.FactionId)
            {
                GroupId = gid,
                HasMap = false,
                IsMovable = true,
                IsAffectedByGravity = true,
                CanInteract = true,
            };
            parentEntity.SetParts(parentParts);
            sg.RegisterEntity(parentEntity);

            var bay1 = FindBay(parentEntity);
            Check(bay1 != null, "母舰有载荷舱");
            if (bay1 == null) { Verdict(); return; }
            bay1!.GetComponent<PayloadBayComponentData>()!.payloadDesignId = designId;

            var child = sg.TryDeployPayload(parent, bay1, out string reason1);
            Check(child != null, "一级释放成功", reason1);
            if (child == null) { Verdict(); return; }

            Check(SameState(parent, child!), "子组状态与母组逐位一致", StateText(child!));
            Check(parentEntity.Parts.Count == 3, "母舰少一件", $"count={parentEntity.Parts.Count}");

            var childEntity = FirstEntity(sg, child!);
            Check(childEntity != null && childEntity!.Parts.Count == 4, "子组装出 4 件",
                $"count={childEntity?.Parts.Count}");

            // 2026-10-26 追加：载荷必须"放出来能活" —— 零件→推进合成 + 能自动点火。
            if (childEntity != null)
            {
                Check(childEntity.PropulsionFromParts && childEntity.HasPropulsionData, "子组按零件合成推进",
                    childEntity.HasPropulsionData
                        ? $"ve={childEntity.propulsion[0].ve:F0} flow={childEntity.propulsion[0].massFlow:F1}"
                        : "无推进");
                bool controlOk = child!.IsControllableNow();
                child.StartBurn(new DVector3(10, 0, 0));
                Check(controlOk && child.HasActiveBurn && !child.powerThermal.BurnIgnitionBlockedByPower
                        && !child.BurnIgnitionBlockedNoControl, "子组可自动点火",
                    $"control={controlOk} burning={child.HasActiveBurn} blockedPower={child.powerThermal.BurnIgnitionBlockedByPower} blockedControl={child.BurnIgnitionBlockedNoControl}");
                child.EndBurn();
            }

            var bay2 = childEntity != null ? FindBay(childEntity) : null;
            Check(bay2 != null, "子组也有载荷舱");
            if (bay2 != null)
            {
                bay2!.GetComponent<PayloadBayComponentData>()!.payloadDesignId = designId;
                var grandchild = sg.TryDeployPayload(child!, bay2, out string reason2);
                Check(grandchild != null, "二级释放成功", reason2);
                if (grandchild != null)
                {
                    Check(SameState(child!, grandchild!), "孙组状态与子组逐位一致", StateText(grandchild!));
                    bool gcControl = grandchild!.IsControllableNow();
                    grandchild.StartBurn(new DVector3(10, 0, 0));
                    Check(gcControl && grandchild.HasActiveBurn && !grandchild.powerThermal.BurnIgnitionBlockedByPower
                            && !grandchild.BurnIgnitionBlockedNoControl, "孙组可自动点火",
                        $"control={gcControl} burning={grandchild.HasActiveBurn}");
                    grandchild.EndBurn();
                }
            }

            Verdict();
        }

        private static void Check(bool ok, string what, string detail = "")
        {
            if (ok) pass++; else fail++;
            Log.Message($"[载荷套娃] {(ok ? "PASS" : "FAIL")} {what}{(detail.Length > 0 ? "：" + detail : "")}");
        }

        private static void Verdict()
            => Log.Message($"[载荷套娃] 结论：{(fail == 0 ? "PASS" : "FAIL")}（PASS={pass} FAIL={fail}）");

        private static SpacePart? FindBay(SpaceEntity e)
        {
            for (int i = 0; i < e.Parts.Count; i++)
                if (e.Parts[i].GetComponent<PayloadBayComponentData>() != null) return e.Parts[i];
            return null;
        }

        private static SpaceEntity? FirstEntity(SpaceGameComp sg, SpaceEntityGroup g)
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
            => $"pos=({g.CurrentOrbit.positionSystem.v.x:F0},{g.CurrentOrbit.positionSystem.v.y:F0},{g.CurrentOrbit.positionSystem.v.z:F0}) "
             + $"vel=({g.CurrentOrbit.velocitySystem.v.x:F1},{g.CurrentOrbit.velocitySystem.v.y:F1},{g.CurrentOrbit.velocitySystem.v.z:F1})";
    }
}
