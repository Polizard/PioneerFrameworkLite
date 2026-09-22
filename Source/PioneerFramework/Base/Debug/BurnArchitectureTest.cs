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
        private static SpaceEntityGroup group;
        private static SpaceMapComp mapComp;
        private static CompPropellantTank tank;
        private static CompThruster thruster;
        private static DVector3 startVelVec;
        private static int phaseStartTick;
        private static readonly List<string> passes = new List<string>();
        private static readonly List<string> failures = new List<string>();

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
                        startVelVec = group.CurrentOrbit.velocity;
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
                            Fail("burn 结束后 pendingImpulse 未清零 (|imp|=" + group.pendingImpulse.magnitude.ToString("F3") + ")");
                        else
                            Pass("物理步首已应用合冲量并清零 pendingImpulse");

                        if (group.remainingBurnDV > 1e-6)
                            Fail("remainingBurnDV 未归零: " + group.remainingBurnDV.ToString("F2"));
                        else
                            Pass("remainingBurnDV 归零");

                        // 速度对账：burn 冲量已应用，速度向量变化 |Δv_vec| ≈ 实际 ΔV（质量口径 ve·ln(m0/m1)）。
                        // 径向 burn 改变速度方向/轨道形状而非模长，故用向量差而非模长差。
                        DVector3 endVelVec = group.CurrentOrbit.velocity;
                        double dvVec = (endVelVec - startVelVec).magnitude;
                        double dvMass = 0.0;
                        if (group.burnStartMass > 0.0 && group.burnConsumedPropellant > 0.0)
                        {
                            float ve = mapComp.EffectiveVe;
                            dvMass = ve * System.Math.Log(group.burnStartMass
                                / System.Math.Max(group.burnStartMass - group.burnConsumedPropellant, 1.0));
                        }
                        Log.Message(string.Format("[ArchTest] burn 对账: |Δv_vec|={0:F2} m/s  实际ΔV(质量)={1:F2} m/s  消耗工质={2:F1} kg",
                            dvVec, dvMass, group.burnConsumedPropellant));
                        if (dvMass > 0.0 && (dvVec < 0.5 * dvMass || dvVec > 1.5 * dvMass))
                            Fail(string.Format("burn 冲量未正确施加: |Δv_vec|={0:F2} 与质量口径 {1:F2} 偏差过大", dvVec, dvMass));
                        else
                            Pass("burn 冲量已正确施加 |Δv_vec|=" + dvVec.ToString("F2"));

                        // 储罐差值同步（burn 消耗路径）：本地储罐应追平向量
                        float vec = mapComp.GetPropellantMass(thruster.PropellantDef);
                        float local = tank.StoredMass;
                        if (System.Math.Abs(local - vec) > 0.5f)
                            Fail(string.Format("burn 后储罐差值同步不一致: 本地 {0:F1} vs 向量 {1:F1}", local, vec));
                        else
                            Pass(string.Format("burn 后储罐差值同步一致: 本地 {0:F1} ≈ 向量 {1:F1}", local, vec));

                        // 直接扣向量 100kg（模拟无 burn 的外部消耗），验证差值同步独立路径
                        float drained = mapComp.TryConsumePropellant(thruster.PropellantDef, 100f);
                        Log.Message("[ArchTest] 直接扣向量 " + drained.ToString("F1") + " kg，等待储罐追回...");
                        phase = Phase.DrainSync;
                        phaseStartTick = now;
                    }
                    break;

                case Phase.DrainSync:
                    if (now - phaseStartTick >= 10)
                    {
                        float vec = mapComp.GetPropellantMass(thruster.PropellantDef);
                        float local = tank.StoredMass;
                        if (System.Math.Abs(local - vec) > 0.5f)
                            Fail(string.Format("外部扣减后差值同步未追回: 本地 {0:F1} vs 向量 {1:F1}", local, vec));
                        else
                            Pass(string.Format("外部扣减后储罐追回: 本地 {0:F1} ≈ 向量 {1:F1}", local, vec));
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

            group = spaceGame.GetGroupById(groupId);
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
            Log.Message(string.Format("[ArchTest] 部署完成: 氢罐 {0:F0}kg，向量 {1:F0}kg，ve={2:F0} m/s",
                tank.StoredMass, mapComp.GetPropellantMass(thruster.PropellantDef), thruster.Ve));
            return true;
        }

        private static Thing PlaceBuilding(Map map, string defName, IntVec3 cell, Rot4 rot = default)
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
            Log.Message(string.Format("[ArchTest] ═══ 汇总: PASS={0} FAIL={1} ═══", passes.Count, failures.Count));
            if (failures.Count == 0)
                phase = Phase.Done;
        }
    }
}
