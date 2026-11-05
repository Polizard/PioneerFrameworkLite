using System;
using System.Collections.Generic;
using PioneerFramework.Components;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **来袭性能场景**（2026-10-02）：铺一支"导弹群 + 舰队"来袭想定，量**整步**性能消耗，**自动清场**。
    ///
    /// ## 它回答什么
    /// 离线蒙特卡洛台架（`MissileSwarmMonteCarloBench`）只量**感知段**（判据 + 融合），而"2040 组能不能跑"
    /// 的答案在**整步**里：积分 / 引力场 / 操舵 / 轨道保持 / 碰撞网格 / 各消费者。本场景把那部分量出来。
    ///
    /// ## 两个读数（同一次运行里 A/B）
    /// 1. **整步周期**（墙钟，本场景自己测）：两次物理步之间的真实间隔 ⇒ **含全部消费者**（
    ///    `UpdateAllMovements` + `CombatManager` + `AVM` + 探针 + 场景自己）。这是"游戏跟不跟得上"的直接读数：
    ///    Normal 档的**下限 ≈ 33.4 ms/步**（2 tick × 1/60 s），超过它就是在掉速。
    /// 2. **物理步**（常驻仪器 `[步耗时]`）：`UpdateAllMovements` 一段（操舵 + 冲量 + 群组物理 + 耗散 + 交会引导），
    ///    口径见上一条——它**不含**碰撞/AVM/渲染。
    ///
    /// ## 时序（一次点击跑完）
    /// `基线 8 步 → 铺 2×(舰 + 弹) → 稳定 3 步 → 计量 20 步 → 出报告 → 自动清场`
    ///
    /// ## 护栏
    /// · 清场走 `PflTestKit.SnapshotGroups/CleanupSince`（**只销毁本次新建的组**，不动玩家资产）；
    /// · **保险丝**：任一步周期 > <see cref="EmergencyStepMs" /> 连续 2 步 ⇒ 立刻清场并报红（别把玩家的游戏拖死）。
    ///
    /// ## 明确不含（登记，别把读数当全貌）
    /// · **导弹末制导**（AVM 的攻击任务）：本场景的弹是**弹道来袭体**（不点火、不导引）⇒ 少一块
    ///   `TickAttack` 的每弹每步成本；要含它得走真实发射链（`CombatManager` 齐射）——**登记为下一档**。
    /// · 开火 / 命中 / 爆炸事件（本场景不交战）。
    /// · 渲染（导弹的绘制成本）。
    /// </summary>
    public static class SwarmAttackPerfRunner
    {
        private enum Stage { Idle, Baseline, Settle, Measure }

        private const int BaselineSteps = 8;
        private const int SettleSteps = 3;
        private const int MeasureSteps = 20;
        /// <summary>保险丝（整步周期上限）。2026-10-02 从 400 → **1500 ms**：实测 2040 组约 495 ms/步，
        /// 20 步 ≈ 10 s（不快，但不危险）⇒ 熔断只该防"多秒级冻结"，不该把完整计量窗切掉。</summary>
        private const double EmergencyStepMs = 1500.0;
        private const double LeoAltitudeMeters = 500000.0;

        private static Stage stage = Stage.Idle;
        private static int stepsInStage;
        private static int shipsPerSide, missilesPerSide;
        private static int groupCountBefore, groupCountAfter;
        private static string baselineClock = "", swarmClock = "", tickClock = "", extraClock = "";
        private static double[] basePeriods = new double[BaselineSteps];
        private static double[] measPeriods = new double[MeasureSteps];
        private static int baseN, measN;
        private static long lastTs;
        private static long lastStepTs;
        private static int overBudgetStreak;

        public static bool Running => stage != Stage.Idle;

        // ═══════════════════════════════════════════════════════════════════
        //  入口
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>小档：2×(5 舰 + 100 弹) = 210 组。</summary>
        public static void RunSmall() => Run(5, 100);

        /// <summary>中档：2×(20 舰 + 500 弹) = 1040 组。</summary>
        public static void RunMedium() => Run(20, 500);

        /// <summary>全档（用户想定）：2×(20 舰 + 1000 弹) = 2040 组。</summary>
        public static void RunFull() => Run(20, 1000);

        public static void Run(int ships, int missiles)
        {
            if (Running)
            {
                Log.Message("[来袭·实测] 已在跑 ⇒ 忽略本次");
                return;
            }
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (sg == null || mgr == null)
            {
                Log.Warning("[来袭·实测] 缺 SpaceGameComp / CelestialBodyManager（不在太空场景？）");
                return;
            }
            if (SwarmScaleAcceptanceRunner.Running)
            {
                Log.Warning("[来袭·实测] 规模验收在跑 ⇒ 等它跑完再点（两个场景会互相污染读数）");
                return;
            }

            shipsPerSide = ships;
            missilesPerSide = missiles;
            groupCountBefore = CountGroups(sg);
            PflTestKit.SnapshotGroups();          // 清场口径：只销毁本次新建的组
            baseN = measN = 0;
            overBudgetStreak = 0;
            PerfProbe.ResetStepClock();
            stage = Stage.Baseline;
            stepsInStage = 0;
            lastTs = lastStepTs = System.Diagnostics.Stopwatch.GetTimestamp();
            Log.Message($"[来袭·实测] 开始：基线 {BaselineSteps} 步 → 铺 2×({ships} 舰 + {missiles} 弹) = {2 * (ships + missiles)} 组 → 稳定 {SettleSteps} 步 → 计量 {MeasureSteps} 步 → 自动清场｜当前世界 {groupCountBefore} 组");
        }

        /// <summary>中止：清场 + 回 Idle。</summary>
        public static void Abort()
        {
            if (!Running) return;
            int n = PflTestKit.CleanupSince();
            Log.Message($"[来袭·实测] 手动中止，已清 {n} 组");
            stage = Stage.Idle;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  物理步驱动（`SpaceGameComp` 每物理步调一次）
        // ═══════════════════════════════════════════════════════════════════

        public static void Tick()
        {
            if (stage == Stage.Idle) return;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double periodMs = (now - lastStepTs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            lastStepTs = now;

            switch (stage)
            {
                case Stage.Baseline:
                    if (baseN < basePeriods.Length) basePeriods[baseN++] = periodMs;
                    if (++stepsInStage >= BaselineSteps)
                    {
                        baselineClock = PerfProbe.StepClockReport();
                        PerfProbe.ResetStepClock();
                        SpawnSwarm();
                        stage = Stage.Settle;
                        stepsInStage = 0;
                    }
                    break;

                case Stage.Settle:
                    if (++stepsInStage >= SettleSteps)
                    {
                        var sg = Current.Game?.GetComponent<SpaceGameComp>();
                        groupCountAfter = CountGroups(sg);
                        PerfProbe.ResetStepClock();
                        PerfProbe.ResetTickClock();      // 整 tick 钟与物理步钟同时清零 ⇒ 读数窗口一致
                        PerfProbe.ResetExtraClocks();    // 块/消费者钟同窗口（2026-10-02）
                        stage = Stage.Measure;
                        stepsInStage = 0;
                    }
                    break;

                case Stage.Measure:
                    if (measN < measPeriods.Length) measPeriods[measN++] = periodMs;
                    if (periodMs > EmergencyStepMs)
                    {
                        if (++overBudgetStreak >= 2)
                        {
                            Log.Warning($"[来袭·实测] **保险丝熔断**：连续两步整步周期 > {EmergencyStepMs:F0} ms（实测 {periodMs:F1} ms）⇒ 立刻清场（2040 组把这一步拖得太狠，先收场再看读数）");
                            Finish(aborted: true);
                            return;
                        }
                    }
                    else overBudgetStreak = 0;

                    if (++stepsInStage >= MeasureSteps) Finish(aborted: false);
                    break;
            }
        }

        private static void Finish(bool aborted)
        {
            // ⚠ 2026-10-02 修：读数必须**在这里**取（两条路径共用）——上一版只在"跑满计量步"那条路上取，
            //   于是**熔断路径**打的是**上一次运行**留下的字符串（2040 组那轮报了 1040 组的数与 tick）。
            swarmClock = PerfProbe.StepClockReport();
            tickClock = PerfProbe.TickClockReport();
            extraClock = PerfProbe.ExtraClockReport();
            int destroyed = PflTestKit.CleanupSince();
            stage = Stage.Idle;
            Log.Message($"[来袭·实测] 清场完成：销毁 {destroyed} 组");
            Report(aborted);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  铺场
        // ═══════════════════════════════════════════════════════════════════

        private static void SpawnSwarm()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (sg == null || mgr == null) { stage = Stage.Idle; return; }

            var planets = mgr.GetPlanets();
            int bodyId = planets != null && planets.Count > 0 ? planets[0].bodyId : 1;
            double mu = sg.GetBodyGM(bodyId);
            if (!(mu > 0.0)) { Log.Warning("[来袭·实测] 根天体 GM 非正 ⇒ 铺不出场"); stage = Stage.Idle; return; }

            int playerFaction = Faction.OfPlayer?.loadID ?? 0;
            int enemyFaction = FactionResolver.HostileFactionIdForPlayer();
            string enemyNote = enemyFaction >= 0 ? enemyFaction.ToString() : "无敌对阵营 ⇒ 两侧都用玩家阵营（探测接线前不影响读数）";
            if (enemyFaction < 0) enemyFaction = playerFaction;

            double r = SpaceConstants.PlanetRadiusMeters + LeoAltitudeMeters;
            double v = Math.Sqrt(mu / r);
            var centers = new DVector3[2];
            for (int side = 0; side < 2; side++)
            {
                double phase = side == 0 ? 0.0 : Math.PI / 3.0;    // 两侧相隔 60°
                centers[side] = new DVector3(Math.Cos(phase), 0.0, Math.Sin(phase)) * r;
            }

            int spawned = 0;
            for (int side = 0; side < 2; side++)
            {
                int faction = side == 0 ? playerFaction : enemyFaction;
                double phase = side == 0 ? 0.0 : Math.PI / 3.0;
                DVector3 aim = (centers[1 - side] - centers[side]).normalized;

                for (int i = 0; i < shipsPerSide; i++)
                {
                    double rr = r + i * 2000.0;                      // 半径错开，避免同轨叠加
                    double ph = phase + i * (2.0 * Math.PI / (shipsPerSide * 20.0));
                    var radial = new DVector3(Math.Cos(ph), 0.0, Math.Sin(ph));
                    var tangent = DVector3.Cross(radial, DVector3.up).normalized;
                    var group = DebugShipSpawner.CreateNoMapShipGroup(sg, bodyId, radial * rr, tangent * Math.Sqrt(mu / rr),
                        out int entityId, overThrust: false, circularOrbit: true);
                    if (group == null) { Log.Warning("[来袭·实测] 舰创建失败（工质 Def 缺失？）"); continue; }
                    group.FactionId = faction;
                    var e = sg.GetEntityById(entityId);
                    if (e != null) e.FactionId = faction;
                    spawned++;
                }

                for (int j = 0; j < missilesPerSide; j++)
                {
                    // 来袭体：从本侧出发、指向对面舰队；**非圆轨** ⇒ 走三阶泰勒积分 + 引力场（重的那条路）
                    double ph = phase + (j % 8) * 0.0006;
                    var radial = new DVector3(Math.Cos(ph), 0.0, Math.Sin(ph));
                    var tangent = DVector3.Cross(radial, DVector3.up).normalized;
                    var pos = radial * (r + (j % 5) * 3000.0);
                    var vel = tangent * v + aim * 1500.0;            // 顺行的基础上加 1.5 km/s 的接近速度
                    if (SpawnMissile(sg, bodyId, faction, pos, vel)) spawned++;
                }
            }
            Log.Message($"[来袭·实测] 铺场完成：{spawned} 组（两侧阵营 {playerFaction} / {enemyNote}）");
        }

        /// <summary>来袭体：小尺寸、单部件、无推进、匿踪闸门开（[N3] 的导弹口径）。</summary>
        private static bool SpawnMissile(SpaceGameComp sg, int bodyId, int factionId, DVector3 pos, DVector3 vel)
        {
            int groupId = sg.AllocGroupId();
            int entityId = sg.AllocEntityId();

            var group = new SpaceEntityGroup(groupId, factionId)
            {
                IsMovable = true,
                IsCollidable = true,
                IsStealth = true,                 // 导弹 = 不广播坐标（[N3]）
                CelestialBodyId = bodyId,
            };
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)pos,
                velocitySystem = (SystemVec3)vel,
                rotation = DQuaternion.identity,
            };
            sg.RegisterGroup(group);

            var entity = new SpaceEntity(entityId, factionId)
            {
                HasMap = false,
                IsMapArchived = false,
                IsCollidable = true,
                IsMovable = true,
                CanInteract = false,
                IsAffectedByGravity = true,
            };
            entity.GroupId = groupId;
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(Quaternion.identity);
            entity.mainAxisShip = new DVector3(0, 0, -1);

            var part = new SpacePart
            {
                partId = 1,
                defName = "PF_ShipPart_Cube",
                positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(10.0, 10.0, 30.0),
                hp = 150f,
                isIndestructible = false,
            };
            part.AddComponent(new HullMassComponent { mass = 400f });
            entity.SetParts(new List<SpacePart> { part });

            sg.RegisterEntity(entity);
            group.AddEntity(entityId);
            return true;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  报告
        // ═══════════════════════════════════════════════════════════════════

        private static void Report(bool aborted)
        {
            int groups = 2 * (shipsPerSide + missilesPerSide);
            Log.Message($"[来袭·实测] 想定 2×({shipsPerSide} 舰 + {missilesPerSide} 弹) = {groups} 组｜世界组数 {groupCountBefore} → {groupCountAfter}（可动 {CountMovable(Current.Game?.GetComponent<SpaceGameComp>())}）");
            Log.Message($"[来袭·实测·基线] 整步周期 p50 {Pct(basePeriods, baseN, 0.50):F1} ms / max {Max(basePeriods, baseN):F1}（含全部消费者）｜物理步：{baselineClock}");
            Log.Message($"[来袭·实测·来袭] 整步周期 p50 {Pct(measPeriods, measN, 0.50):F1} ms / max {Max(measPeriods, measN):F1}（含全部消费者）｜物理步：{swarmClock}");
            // 整 tick 钟（2026-10-02）：把"物理步"与"本组件整 tick"分开 ⇒ 剩余的整步周期才好归因
            //（实测教训：2040 组那轮物理步 72 ms、整步周期 701 ms，差值当时无处可查）。
            Log.Message($"[来袭·实测·来袭] 整 tick 钟：{tickClock}（物理步钟只圈 `UpdateAllMovements`，本钟圈整个 `GameComponentTick`）");
            Log.Message($"[来袭·实测·来袭] 块/消费者钟：{extraClock}");
            double p50Base = Pct(basePeriods, baseN, 0.50), p50Swarm = Pct(measPeriods, measN, 0.50);
            Log.Message($"[来袭·实测] 增量：整步周期 {p50Swarm - p50Base:+0.0;-0.0;0} ms（{p50Base:F1} → {p50Swarm:F1}）｜下限 33.4 ms/步 = Normal 档 2 tick{(aborted ? "｜**本轮被保险丝中止**（读数仍有效，但计量段不完整）" : "")}");
            Log.Message("[来袭·实测] 不含：导弹末制导 / 开火命中 / 渲染（登记为下一档）");
        }

        private static int CountGroups(SpaceGameComp? sg)
        {
            if (sg == null) return 0;
            int n = 0;
            foreach (var g in sg.GetAllGroups()) n++;
            return n;
        }

        private static int CountMovable(SpaceGameComp? sg)
        {
            if (sg == null) return 0;
            int n = 0;
            foreach (var g in sg.GetAllGroups()) if (g.IsMovable) n++;
            return n;
        }

        private static double Pct(double[] xs, int n, double p)
        {
            if (n <= 0) return 0.0;
            var copy = new double[n];
            Array.Copy(xs, copy, n);
            Array.Sort(copy);
            int i = (int)Math.Round(p * (n - 1));
            return copy[Math.Max(0, Math.Min(n - 1, i))];
        }

        private static double Max(double[] xs, int n)
        {
            double m = 0.0;
            for (int i = 0; i < n && i < xs.Length; i++) if (xs[i] > m) m = xs[i];
            return m;
        }
    }
}
