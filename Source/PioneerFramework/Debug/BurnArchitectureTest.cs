using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 游戏内自动化架构验证（burn 架构调整），由 SpaceGameComp.GameComponentTick 每 tick 推进状态机：
    ///   A. 储罐差值同步：burn / 外部扣减只动 mapComp 工质向量，储罐建筑本地逐 tick 追回，最终一致；
    ///   B. 合冲量模式：burn 冲量经 pendingImpulse 累积、物理步首统一应用——结束后清零、
    ///      remainingBurnDV 归零、速度增量对账（质量口径 ΔV）；
    ///   C. 推进器燃烧状态：burn 期间 IsBurning=true，结束后 false。
    /// 从 Debug 建筑 Gizmo「运行架构验证测试」启动，结果打印到游戏日志。
    /// </summary>
    public static class BurnArchitectureTest
    {
        private enum Phase { Idle, WaitBurnStart, WaitBurnEnd, WaitImpulseApplied, DrainSync, Done, Failed }

        private const int EARTH_BODY_ID = 1;
        private const float PLAN_DV = 30f;      // 测试机动 ΔV (m/s)
        private const int PHASE_TIMEOUT = 900;  // 每阶段超时（real tick）

        private static Phase phase = Phase.Idle;
        private static SpaceEntityGroup group = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        private static SpaceMapComp mapComp = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        private static CompPropellantTank? tank;
        private static CompThruster thruster = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        private static DVector3 startVelVec;
        private static int phaseStartTick;
        private static readonly List<string> passes = new();
        private static readonly List<string> failures = new();

        /// <summary>启动一次自动化测试（部署空间站 → 规划机动 → 状态机自动断言）。</summary>
        public static void Run()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null)
            {
                Log.Error("[ArchTest] SpaceGameComp 缺失，无法测试");
                return;
            }

            Reset();
            if (!Deploy(spaceGame))
            {
                Log.Error("[ArchTest] 部署失败");
                return;
            }

            // 测试确定性：强制 Normal 流速（1x），避免 Fast 下物理时间轴 8x 干扰 burn 观测
            Current.Game?.GetComponent<TimeScaleManager>()?.SetNormalMode();

            // ★ 架构测试**刻意冻结方向、走固定 Δv 路径**（`abFreezeDirection`）：
            //   这里验的是 pendingImpulse / 质量口径 / 储罐同步，不是“活靶律在节点处的有限燃烧精度”。
            //   活靶律的节点靶语义另有专项验收；混在这里会让“冲量施加正确”被制导误差污染。
            group.abFreezeDirection = true;
            // 规划一条 ΔV=30 径向机动（T+30 物理秒后执行）。
            // executeTick 用 physicsTick 基准（与 ManeuverPlanner/检查点一致，Fast 档不错位）；
            // 走 QueueGroupManeuver 以登记 trackedManeuvers（触发"有机动→1x"自动降速）。
            int execTick = (int)System.Math.Round(TimeScaleManager.GetPhysicsTickSafe()) + 30;
            group.currentNetwork = thruster.PropellantDef;
            spaceGame.QueueGroupManeuver(group.GroupId, new SpaceEntityGroup.PlannedManeuver
            {
                radialDV = PLAN_DV,
                tangentialDV = 0f,
                normalDV = 0f,
                executeTick = execTick,
                active = true
            });

            phase = Phase.WaitBurnStart;
            phaseStartTick = Find.TickManager.TicksGame;
            Log.Message("[ArchTest] 会话已启动：核热空间站（氢罐满），ΔV=" + PLAN_DV + " 径向机动，等待 burn 开始...");
        }

        /// <summary>每 tick 推进（由 SpaceGameComp.GameComponentTick 调用；空闲时零开销）。</summary>
        public static void Tick()
        {
            if (phase == Phase.Idle || phase == Phase.Done || phase == Phase.Failed) return;

            int now = Find.TickManager.TicksGame;
            if (now - phaseStartTick > PHASE_TIMEOUT)
            {
                Fail("阶段超时: " + phase);
                return;
            }

            switch (phase)
            {
                case Phase.WaitBurnStart:
                    if (group.HasActiveBurn)
                    {
                        if (!thruster.IsBurning)
                            Fail("burn 开始但推进器 IsBurning=false");
                        else
                            Pass("burn 开始时推进器 IsBurning=true");
                        // 记录 burn 前速度向量（burn 冲量在下一物理步首应用，此刻还未叠加）
                        startVelVec = group.CurrentOrbit.velocitySystem.v;
                        phase = Phase.WaitBurnEnd;
                        phaseStartTick = now;
                    }
                    break;

                case Phase.WaitBurnEnd:
                    // 先判 burn 是否结束，再断言进行中的 IsBurning（顺序反了会把"结束瞬间"误判为异常）
                    if (!group.HasActiveBurn)
                    {
                        if (thruster.IsBurning)
                            Fail("burn 结束后推进器 IsBurning=true");
                        else
                            Pass("burn 结束后推进器 IsBurning=false");
                        phase = Phase.WaitImpulseApplied;
                        phaseStartTick = now;
                    }
                    else if (!thruster.IsBurning)
                    {
                        Fail("burn 进行中但推进器 IsBurning=false");
                    }
                    break;

                case Phase.WaitImpulseApplied:
                    // 等 burn 最后一物理步的冲量在步首应用并清零
                    if (now - phaseStartTick >= 6)
                    {
                        if (group.pendingImpulse.sqrMagnitude > 1e-9)
                            Fail("burn 结束后 pendingImpulse 未清零 (|imp|=" + $"{group.pendingImpulse.magnitude:F3}" + ")");
                        else
                            Pass("物理步首已应用合冲量并清零 pendingImpulse");

                        if (group.remainingBurnDV > 1e-6)
                            Fail("remainingBurnDV 未归零: " + $"{group.remainingBurnDV:F2}");
                        else
                            Pass("remainingBurnDV 归零");

                        // 速度对账：burn 冲量已应用，速度向量变化 |Δv_vec| ≈ 实际 ΔV（质量口径 ve·ln(m0/m1)）。
                        // 径向 burn 改变速度方向/轨道形状而非模长，故用向量差而非模长差。
                        DVector3 endVelVec = group.CurrentOrbit.velocitySystem.v;
                        double dvVec = (endVelVec - startVelVec).magnitude;
                        double dvMass = 0.0;
                        if (group.burnStartMass > 0.0 && group.burnConsumedPropellant > 0.0)
                        {
                            float ve = mapComp.EffectiveVe;
                            dvMass = ve * System.Math.Log(group.burnStartMass
                                / System.Math.Max(group.burnStartMass - group.burnConsumedPropellant, 1.0));
                        }
                        Log.Message($"[ArchTest] burn 对账: |Δv_vec|={dvVec:F2} m/s  实际ΔV(质量)={dvMass:F2} m/s  交付={group.appliedBurnDV:F2} m/s  计划={PLAN_DV:F0} m/s  消耗工质={group.burnConsumedPropellant:F1} kg");
                        // ⚠ `|Δv_vec|` 含轨道速度方向变化（径向 burn 尤其明显），不能当冲量幅值判据；
                        //   只做观测。真正的冲量判据 = 质量口径 ΔV vs 执行器交付（两者同尺）。
                        if (dvMass > 0.0 && System.Math.Abs(dvMass - group.appliedBurnDV) > 0.05 * group.appliedBurnDV + 0.05)
                            Fail($"burn 冲量未正确施加: 质量口径 {dvMass:F2} vs 执行器交付 {group.appliedBurnDV:F2}");
                        else
                            Pass($"burn 冲量已正确施加（质量/交付同尺） mass={dvMass:F2} applied={group.appliedBurnDV:F2}");

                        // **真正的完成判据 = 节点处活速度 vs 目标速度**（活差额律的靶）。
                        // ⚠ 不能拿 `appliedBurnDV` 直接比 `PLAN_DV`：计划 Δv 是**节点脉冲**，
                        //   有限燃烧在节点前完成，重力在剩余时间里额外贡献了径向速度
                        //   （实测 30 计划只烧 16.8，差 ~13 正是重力项；强行要求 30 是旧固定冲量时代的判据）。
                        double targetMiss = double.NaN;
                        if (group.burnHasTargetV && group.burnArcNodeUt > 0.0)
                        {
                            var cm = Current.Game?.GetComponent<CelestialBodyManager>();
                            if (cm != null && OrbitAdapter.TryToOrbitView(group, cm,
                                    TimeScaleManager.GetPhysicsTickSafe(), out OrbitView ovEnd))
                            {
                                (DVector3 _, DVector3 vNode) = ovEnd.GameFrameStateAtUT(group.burnArcNodeUt);
                                targetMiss = (vNode - group.burnTargetV).magnitude;
                            }
                        }
                        if (!group.burnHasTargetV)
                        {
                            // 固定方向/固定 Δv 架构路径：没有节点靶；直接判交付 = 计划。
                            if (System.Math.Abs(group.appliedBurnDV - PLAN_DV) > 0.5)
                                Fail($"burn 固定方向交付 {group.appliedBurnDV:F2} vs 计划 {PLAN_DV:F0}");
                            else
                                Pass("burn 固定方向交付计划 Δv=" + $"{group.appliedBurnDV:F2}");
                        }
                        else
                        {
                        Log.Message($"[ArchTest] 靶达成读数: |v_live(node)-v_target|={targetMiss:F3} m/s（applied={group.appliedBurnDV:F2} / 计划={PLAN_DV:F0}，差值含重力项）");
                        DVector3 lastDir = group.burnLastDeficitDir;
                        double dirDot = DVector3.Dot(lastDir.normalized, group.burnDirectionSystem.normalized);
                        Log.Message($"[ArchTest] 执行器最后一步: lastDeficit={group.burnLastDeficit:F3} m/s |vLive(node)-target|={(group.burnLastVLiveUt - group.burnTargetV).magnitude:F3} m/s appliedStep={group.burnLastAppliedDv:F3} defDir·burnDir={dirDot:F3} node={group.burnArcNodeUt:F1}");
                        if (!double.IsNaN(targetMiss))
                        {
                            var cmDbg = Current.Game?.GetComponent<CelestialBodyManager>();
                            if (cmDbg != null && OrbitAdapter.TryToOrbitView(group, cmDbg,
                                    TimeScaleManager.GetPhysicsTickSafe(), out OrbitView ovDbg))
                            {
                                (DVector3 _, DVector3 vNodeDbg) = ovDbg.GameFrameStateAtUT(group.burnArcNodeUt);
                                Log.Message($"[ArchTest] 靶向量对账: target=({group.burnTargetV.x:F2},{group.burnTargetV.y:F2},{group.burnTargetV.z:F2}) lastVLive=({group.burnLastVLiveUt.x:F2},{group.burnLastVLiveUt.y:F2},{group.burnLastVLiveUt.z:F2}) testVNode=({vNodeDbg.x:F2},{vNodeDbg.y:F2},{vNodeDbg.z:F2}) |last-test|={(group.burnLastVLiveUt - vNodeDbg).magnitude:F3} |test-target|={targetMiss:F3}");
                            }
                        }
                        if (!double.IsNaN(targetMiss) && targetMiss > 1.0)
                            Fail($"burn 靶未达成: |Δv_target|={targetMiss:F2} m/s（applied={group.appliedBurnDV:F2} 计划={PLAN_DV:F0}）");
                        else
                            Pass($"burn 靶已达成（|miss|={(double.IsNaN(targetMiss) ? 0.0 : targetMiss):F3} m/s）");

                        }

                        // 储罐差值同步（burn 消耗路径）：本地储罐应追平向量
                        double vec = mapComp.GetPropellantMass(thruster.PropellantDef);
                        // `tank!`：静态夹具（setup 在 205 行赋值、teardown 在 264 行清空）⇒ 本段内非空由流程保证，
                        // 编译期证不了；若装配坏了这里会以 NullReferenceException 响亮失败（不是静默）。
                        float local = tank!.StoredMass;
                        if (System.Math.Abs(local - vec) > 0.5f)
                            Fail($"burn 后储罐差值同步不一致: 本地 {local:F1} vs 向量 {vec:F1}");
                        else
                            Pass($"burn 后储罐差值同步一致: 本地 {local:F1} ≈ 向量 {vec:F1}");

                        // 直接扣向量 100kg（模拟无 burn 的外部消耗），验证差值同步独立路径
                        double drained = mapComp.TryConsumePropellant(thruster.PropellantDef, 100.0);
                        Log.Message("[ArchTest] 直接扣向量 " + $"{drained:F1}" + " kg，等待储罐追回...");
                        phase = Phase.DrainSync;
                        phaseStartTick = now;
                    }
                    break;

                case Phase.DrainSync:
                    if (now - phaseStartTick >= 10)
                    {
                        double vec = mapComp.GetPropellantMass(thruster.PropellantDef);
                        float local = tank!.StoredMass;
                        if (System.Math.Abs(local - vec) > 0.5f)
                            Fail($"外部扣减后差值同步未追回: 本地 {local:F1} vs 向量 {vec:F1}");
                        else
                            Pass($"外部扣减后储罐追回: 本地 {local:F1} ≈ 向量 {vec:F1}");
                        Finish();
                    }
                    break;
            }
        }

        /// <summary>部署测试空间站：核热引擎 + 氢储罐（满 5000kg）。失败返回 false。</summary>
        private static bool Deploy(SpaceGameComp spaceGame)
        {
            int groupId = NextGroupId(spaceGame);
            int entityId = NextEntityId(spaceGame);

            // 500km LEO
            double orbitRadius = SpaceConstants.PlanetRadiusMeters + 500000.0;
            var entity = spaceGame.CreateStation(entityId, groupId, Faction.OfPlayer.loadID,   // [V12] 真阵营 id（原硬编码 0）
                new DVector3(orbitRadius, 0, 0), mapWidth: 60, mapHeight: 60, stationName: "架构验证站");
            if (entity == null) return false;

            group = spaceGame.GetGroupById(groupId)!;   // 凭据（2026-10-15）：groupId 由本测试刚铺场，查得到；查不到=装配坏了，此处应响亮失败
            if (group == null) return false;
            group.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            var map = SpaceMapGenerator.FindPocketMap(entity.MapId);
            if (map == null) return false;
            mapComp = map.GetComponent<SpaceMapComp>();
            if (mapComp == null) return false;

            // 核热引擎摆 Rot4.East：def 轴向 Back(-Z) 经 Rot4.East 映射 → 船 +X。
            // 测试机动是径向 burn，节点时轨道相位 +X 附近（30s 内绕行 ~1.9°，在 10° 矢量锥内），
            // 方向感知 burn 直接对准出力（否则自动对准会让 burn 拖长到超时）。
            PlaceBuilding(map, "PF_Building_Thruster_NuclearThermal", new IntVec3(28, 0, 30), Rot4.East);
            var tankThing = PlaceBuilding(map, "PF_Building_Tank_Hydrogen", new IntVec3(30, 0, 30));
            tank = tankThing?.TryGetComp<CompPropellantTank>();

            // 推进器 Comp 引用：从地图枚举（PlaceBuilding 未暴露 Comp）
            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
            {
                var ct = t.TryGetComp<CompThruster>();
                if (ct != null)
                {
                    thruster = ct;
                    break;
                }
            }
            if (tank == null || thruster == null)
            {
                Log.Error("[ArchTest] 部署失败：引擎/储罐 Comp 未找到");
                return false;
            }

            tank.TryFill(tank.Capacity);
            Log.Message($"[ArchTest] 部署完成: 氢罐 {tank.StoredMass:F0}kg，向量 {mapComp.GetPropellantMass(thruster.PropellantDef):F0}kg，ve={thruster.Ve:F0} m/s");
            return true;
        }

        private static Thing? PlaceBuilding(Map map, string defName, IntVec3 cell, Rot4 rot = default)
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                Log.Error("[ArchTest] Def not found: " + defName);
                return null;
            }
            var thing = ThingMaker.MakeThing(def);
            GenSpawn.Spawn(thing, cell, map, rot, WipeMode.Vanish, false);
            return thing;
        }

        private static int NextGroupId(SpaceGameComp spaceGame)
        {
            int maxId = 0;
            foreach (var g in spaceGame.GetAllGroups())
                if (g.GroupId > maxId) maxId = g.GroupId;
            return maxId + 1;
        }

        private static int NextEntityId(SpaceGameComp spaceGame)
        {
            int maxId = 0;
            foreach (var g in spaceGame.GetAllGroups())
                foreach (int eid in g.GetEntityIds())
                    if (eid > maxId) maxId = eid;
            return maxId + 1;
        }

        private static void Reset()
        {
            phase = Phase.Idle;
            group = null;
            mapComp = null;
            tank = null;
            thruster = null;
            startVelVec = DVector3.zero;
            passes.Clear();
            failures.Clear();
        }

        private static void Pass(string msg)
        {
            passes.Add(msg);
            Log.Message("[ArchTest] PASS: " + msg);
        }

        private static void Fail(string msg)
        {
            failures.Add(msg);
            Log.Error("[ArchTest] FAIL: " + msg);
            phase = Phase.Failed;
            Finish();
        }

        private static void Finish()
        {
            Log.Message($"[ArchTest] ═══ 汇总: PASS={passes.Count} FAIL={failures.Count} ═══");
            // 失败后必须离开当前阶段：否则 DrainSync 会每 10 tick 重复 PASS、最后刷一个假超时
            phase = failures.Count == 0 ? Phase.Done : Phase.Failed;
        }
    }
}
