using System;
using MechJebLib.Functions;
using MechJebLib.TwoBody;
using System.Collections.Generic;
using System.Text;
using MechJebLib.Primitives;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **真飞验收**（对照"一键验收"里的计划侧判据）。
    ///
    /// ## 为什么需要它
    /// 一键验收（`PlanEditorAcceptanceRunner`）是**非侵入式**的：它算**计划**、用**解析传播**验交会，
    /// 从不真的点火、入队后立刻清空。好处是秒出结果、可反复跑；**代价是执行侧完全没验**——
    /// 点火循环 / 姿态对准 / 工质消耗 / 燃烧居中 / 长燃烧方向律（B2.3）/ 队列与引导归属（B26）
    /// 全都没被这些判据覆盖。**真飞验收补的就是这半边。**
    ///
    /// ## 用法（两个按钮，因为"要等"）
    /// ① 「真飞验收·布置」：铺一条偏心轨道的船，**真的**排一条"在该点圆化"的机动（带意图 ⇒
    ///    执行层会走活重解），并打印**期望值**（圆化后 e≈0、半长轴=那一刻半径、计划 Δv）。
    /// ② 等它烧完（船是过推档，几百 m/s 大约几分钟游戏时间；开 3 倍速更快）。
    /// ③ 「真飞验收·检查」：读那条船的**实际**轨道与已耗 Δv，与①的期望值对照出结论。
    ///
    /// ⚠ 这是**唯一**会真的改变游戏状态的验收工具（船会真的变轨）⇒ 只在你主动按它时才跑。
    /// </summary>
    public static class LiveBurnAcceptanceRunner
    {
        private const int EARTH_BODY_ID = 1;
        private const double R0 = 7_000_000.0;

        // ── 自动检验（用户 2026-09-19 要求：布置完执行到就自己出结论，不必手动按「检查」）──
        private static bool autoPending;
        private static int autoStagedTick;                 // 登记时刻（真实 tick）——超时判据用
        /// <summary>
        /// 等**真实 tick** 上限：超过就出 UNCOVERED 结论（2026-09-24 补）。
        /// 为什么必须有：原来"等一个可能永不到来的条件"没有任何出口 ⇒ 点火没发生 / 燃烧被中止时
        /// **一个字都不打**，用户只能干等（本库纪律：未达成 ⇒ UNCOVERED + 写清在等什么，绝不沉默）。
        /// 60,000 真实 tick ≈ 1× 下 17 分钟、3× 下 5.6 分钟；真跑的燃烧（几分钟游戏时间）绰绰有余。
        /// </summary>
        private const int AutoTimeoutRealTicks = 60000;
        private static int autoGroupId = -1;
        private static double autoNodeTick, autoPlanDv;
        private static double autoBurnSeconds;

        /// <summary>
        /// 每物理步调用（空闲时第一行就返回，零成本）。
        /// 条件：**该组不再燃烧**、已烧过 Δv、且已过节点 ⇒ 自动跑一遍与「检查」同一套判据。
        /// ⚠ 用**实际已烧 Δv**（`appliedBurnDV`）而不是"调度一次延时任务"：这样即使燃烧被中止也能给出结论。
        /// </summary>
        public static void Tick()
        {
            TickCoast();
            if (!autoPending) return;

            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (sg == null || mgr == null) return;

            int nowReal = Find.TickManager?.TicksGame ?? 0;
            if (nowReal - autoStagedTick > AutoTimeoutRealTicks)
            {
                autoPending = false;
                var still = sg.GetGroupById(autoGroupId);
                Log.Message($"[真飞验收] **UNCOVERED**：等了 {AutoTimeoutRealTicks} 真实 tick 仍没等到燃烧结束且过节点（编队{(still == null ? "已消失" : $"仍在，appliedDv={still.appliedBurnDV:F1} m/s")}）⇒ 结论不出（不是通过，也不是失败）｜要看原因请开 [Burn]/[Thrust] 日志");
                return;
            }

            SpaceEntityGroup? g = sg.GetGroupById(autoGroupId);
            if (g == null) { autoPending = false; return; }

            double tick = TimeScaleManager.GetPhysicsTickSafe();
            if (g.HasActiveBurn) return;                             // 还在烧
            if (g.appliedBurnDV <= 0.0) return;                       // 还没点火
            if (tick < autoNodeTick + 5.0) return;                    // 刚过节点，等几步让冲量落定
            autoPending = false;

            Report(g, mgr, tick, "自动检验（燃烧已结束）");
        }

        // ── 自由飞行（不烧）漂移检查：把"燃烧"整个移出变量，只问"环境/动力学是否等于纯二体" ──
        private static bool coastPending;
        private static int coastGroupId = -1;
        private static double coastStartTick;
        private static DVector3 coastR, coastV;
        private static double coastMu;

        // 烧前状态（正本系）+ 计划的 Δv/节点 ⇒ 供"模型无关"的不变量判据用
        private static DVector3 stagedPreR, stagedPreV, stagedPlanDvVec;
        private static double stagedPreTick, stagedNodeUt, stagedMu;

        private static int stagedGroupId = -1;
        private static int stagedNodeTick = -1;
        private static double stagedPlanDv = -1.0;
        private static double stagedExpectedSma = -1.0;

        /// <summary>布置：铺船 + 真排队（会真的烧）。</summary>
        public static void Stage()
        {
            var sb = new StringBuilder();
            sb.Append("[真飞验收] ═══ 布置（这条会**真的变轨**）═══");

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || celestialMgr == null)
            {
                Log.Warning("[真飞验收] 不在太空场景（缺 SpaceGameComp / CelestialBodyManager）");
                return;
            }

            double mu = SpaceConstants.PrimaryGravitationalParameter;
            CelestialBodyDef? earth = celestialMgr.GetBodyById(EARTH_BODY_ID);
            if (earth != null && earth.gravitationalParameter > 0.0) mu = earth.gravitationalParameter;

            double tick = TimeScaleManager.GetPhysicsTickSafe();
            double vCirc = Math.Sqrt(mu / R0);
            double vA = Math.Sqrt(1.3) * vCirc; // e = 0.3（与其它受控场景同一条）

            SpaceEntityGroup? g = DebugShipSpawner.CreateNoMapShipGroup(spaceGame, EARTH_BODY_ID,
                new DVector3(R0, 0, 0), new DVector3(0, 0, -vA), out int _, overThrust: true, circularOrbit: false);

            if (g == null)
            {
                Log.Warning("[真飞验收] 编队没铺出来（工质 Def 缺失？见日志 Error）");
                return;
            }

            if (!OrbitAdapter.TryToOrbitView(g, celestialMgr, tick, out OrbitView ov))
            {
                Log.Warning("[真飞验收] 拿不到 OrbitView");
                return;
            }

            // 真的排一条"60 秒后圆化"的机动（带意图 ⇒ 执行层会走活重解那条路）
            var op = new OperationCircularize();
            op.TimeSelector.TrySetTimeRef(TimeReference.X_FROM_NOW);
            // ⚠ 原来写 60 s ⇒ 90 s 的燃烧"居中"就要吃掉 45 s ⇒ 只剩 15 s 对准（5°/s 只能转 75°）
            // ⇒ **物理上不可能对准**（实测残留 107°）。改 300 s：够烧完 + 够转 180°。
            op.TimeSelector.LeadTime.Val = 300.0;

            List<PlanNode>? nodes = op.MakeNodes(ov, tick, new FixedTargetView(ov));
            if (nodes == null || nodes.Count == 0)
            {
                Log.Warning("[真飞验收] 算不出计划：" + (op.GetErrorMessage() ?? "无理由"));
                return;
            }

            g.ClearManeuvers();
            PlanIntent intent = PlanIntent.Capture(op, absoluteTick: nodes[0].ut);
            bool queued = OrbitAdapter.TryQueuePlan(g, ov, nodes, out string why, out string summary, intent);

            stagedGroupId = g.GroupId;
            autoPending = true;
            autoStagedTick = Find.TickManager?.TicksGame ?? 0;
            autoGroupId = g.GroupId;
            stagedNodeTick = (int)Math.Round(nodes[0].ut);
            stagedPlanDv = nodes[0].dV.magnitude;
            autoPlanDv = stagedPlanDv;
            autoNodeTick = nodes[0].ut;
            autoBurnSeconds = 0.0; // 由执行器估，这里只作记录
            (stagedPreR, stagedPreV) = ov.RightHandedStateVectorsAtUT(tick);
            stagedPreTick = tick;
            stagedPlanDvVec = nodes[0].dV;
            stagedNodeUt = nodes[0].ut;
            stagedMu = ov.mu;
            stagedExpectedSma = ov.Radius(nodes[0].ut); // 圆化 = 半长轴变成那一刻的半径

            sb.Append("\n  船：G").Append(g.GroupId)
              .Append("（偏心 e=0.3，半径 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(ov.Radius(tick) / 1000.0):F0}").Append(" km）");
            sb.Append("\n  机动：tick=").Append(stagedNodeTick)
              .Append("（T-").Append(Math.Max(0, stagedNodeTick - (int)tick)).Append("s）")
              .Append("｜计划 |Δv|=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{stagedPlanDv:F2}").Append(" m/s")
              .Append("｜带意图=").Append(intent.operationName);
            sb.Append("\n  排队：").Append(queued ? summary : why);
            sb.Append("\n  期望：圆化后 e ≈ 0、半长轴 ≈ ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(stagedExpectedSma / 1000.0):F0}")
              .Append(" km（那一刻的半径）");
            sb.Append("\n  ⇒ **等它烧完**（过推档，几百 m/s 约几分钟游戏时间）再按「真飞验收·检查」。");

            Log.Message(sb.ToString());
            Messages.Message($"真飞验收：G{g.GroupId} 已排入 {nodes.Count} 条真机动（|Δv|={stagedPlanDv:F1} m/s），烧完按「检查」",
                MessageTypeDefOf.NeutralEvent);
        }

        /// <summary>
        /// **布置"自由飞行漂移"检查**（用户 2026-09-19 要求；判据来自"冷静下来看差别在哪"的分析）：
        /// 放一条船、**不排任何机动**，记下此刻状态；约 120 s 后与**纯二体传播**（`Shepperd`）对比。
        ///
        /// 为什么这个实验最干净：它把**燃烧整个移出变量**，只问一件事——
        /// "环境/动力学（同天体项 + 差分项 + 轨道保持 `acc -= keepAcc`）是否精确等于纯二体？"
        /// · **漂了** ⇒ 第 1 项（动力学）是真凶，且"轨道保持把问题变成纯二体"这个**前提要重新审视** ✗
        /// · **没漂** ⇒ 残差只能出在燃烧的应用细节里 ⇒ 回去逐项抠剖面/取样时刻/台阶
        /// </summary>
        public static void StageCoast()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || celestialMgr == null)
            {
                Log.Warning("[漂移检查] 不在太空场景");
                return;
            }

            double mu = SpaceConstants.PrimaryGravitationalParameter;
            CelestialBodyDef? earth = celestialMgr.GetBodyById(EARTH_BODY_ID);
            if (earth != null && earth.gravitationalParameter > 0.0) mu = earth.gravitationalParameter;

            double tick = TimeScaleManager.GetPhysicsTickSafe();
            double vc = Math.Sqrt(mu / R0);
            double vA = Math.Sqrt(1.3) * vc;

            SpaceEntityGroup? g = DebugShipSpawner.CreateNoMapShipGroup(spaceGame, EARTH_BODY_ID,
                new DVector3(R0, 0, 0), new DVector3(0, 0, -vA), out int _, overThrust: true, circularOrbit: false);
            if (g == null)
            {
                Log.Warning("[漂移检查] 编队没铺出来");
                return;
            }

            if (!OrbitAdapter.TryToOrbitView(g, celestialMgr, tick, out OrbitView ov))
            {
                Log.Warning("[漂移检查] 拿不到 OrbitView");
                return;
            }

            (coastR, coastV) = ov.RightHandedStateVectorsAtUT(tick);
            coastMu = mu;
            coastGroupId = g.GroupId;
            coastStartTick = tick;
            coastPending = true;

            Log.Message($"[漂移检查] 布置：G{g.GroupId} **不排任何机动**｜tick={tick:F0}｜e={ov.eccentricity:F4} a={ov.semiMajorAxis / 1000.0:F0}km｜120 s 后自动对比纯二体传播");
            Messages.Message($"漂移检查：G{g.GroupId} 已放置（不烧），120 s 后自动出结论", MessageTypeDefOf.NeutralEvent);
        }

        /// <summary>自由飞行漂移检查的自动判定（由 <see cref="Tick" /> 驱动）。</summary>
        private static void TickCoast()
        {
            if (!coastPending) return;

            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (sg == null || mgr == null) return;

            SpaceEntityGroup? g = sg.GetGroupById(coastGroupId);
            if (g == null) { coastPending = false; return; }

            double tick = TimeScaleManager.GetPhysicsTickSafe();
            double elapsed = tick - coastStartTick;
            if (elapsed < 120.0) return;
            coastPending = false;

            if (!OrbitAdapter.TryToOrbitView(g, mgr, tick, out OrbitView ovNow))
            {
                Log.Warning($"[漂移检查] G{g.GroupId} 拿不到 OrbitView");
                return;
            }

            // 纯二体传播（与规划侧同一套 Shepperd）= "该在的地方"
            (DVector3 rPred, DVector3 vPred) = Shepperd.Solve(coastMu, elapsed, coastR, coastV);
            (DVector3 rNow, DVector3 vNow) = ovNow.RightHandedStateVectorsAtUT(tick);

            double dr = Math.Sqrt((rNow.x - rPred.x) * (rNow.x - rPred.x) + (rNow.y - rPred.y) * (rNow.y - rPred.y) + (rNow.z - rPred.z) * (rNow.z - rPred.z));
            double dv = Math.Sqrt((vNow.x - vPred.x) * (vNow.x - vPred.x) + (vNow.y - vPred.y) * (vNow.y - vPred.y) + (vNow.z - vPred.z) * (vNow.z - vPred.z));

            // 判据：轨道保持的**文档速率**（LEO ≈6 m/s/天）在 120 s 上是 8.3e-3 m/s ⇒ 门设在 0.05 m/s（6× 余量）。
            // 漂移量级若远超此门 ⇒ 环境/动力学（同天体项+差分项+轨道保持）**不等于**纯二体 ⇒ 那才是残差之源。
            bool pass = dv <= 0.05;
            var sb = new StringBuilder();
            sb.Append("[漂移检查] ═══ 自由飞行 120 s：实测 vs 纯二体 ═══");
            sb.Append("\n  G").Append(g.GroupId).Append("｜不排任何机动｜经过 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{elapsed:F0}").Append('s');
            sb.Append("\n  |Δr|=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{dr:E3}").Append(" m｜|Δv|=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{dv:E3}").Append(" m/s");
            sb.Append("\n  轨道：e ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{Astro.EccFromStateVectors(coastMu, coastR, coastV):E3}")
              .Append(" → ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{ovNow.eccentricity:E3}")
              .Append("｜a ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(coastR.magnitude / 1000.0):F0}")
              .Append("km → ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(ovNow.semiMajorAxis / 1000.0):F1}").Append("km");
            sb.Append("\n  ⇒ 判据：|Δv| ≤ 0.05 m/s（文档速率 6 m/s/天 ⇒ 120 s 上 8.3e-3，6× 余量）｜")
              .Append(pass ? "**PASS：环境确实等于纯二体** ⇒ 残差出在燃烧侧" : "**FAIL：环境不等于纯二体** ⇒ 这才是残差之源");
            Log.Message(sb.ToString());
            Messages.Message($"漂移检查：G{g.GroupId} |Δv|={dv:E2} m/s ⇒ {(pass ? "环境=纯二体（PASS）" : "环境有漂移（FAIL）")}",
                MessageTypeDefOf.NeutralEvent);
        }

        /// <summary>检查：读实际轨道与已耗 Δv，与布置时的期望对照。</summary>
        public static void Check()
        {
            _ = autoPlanDv; _ = autoBurnSeconds;

            var sb = new StringBuilder();
            sb.Append("[真飞验收] ═══ 检查（读实际结果）═══");

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || celestialMgr == null || stagedGroupId < 0)
            {
                Log.Warning("[真飞验收] 没有已布置的场景（先按「布置」）");
                return;
            }

            SpaceEntityGroup? g = spaceGame.GetGroupById(stagedGroupId);
            if (g == null)
            {
                Log.Warning($"[真飞验收] 找不到 G{stagedGroupId}（船没了？）");
                return;
            }

            double tick = TimeScaleManager.GetPhysicsTickSafe();
            if (!OrbitAdapter.TryToOrbitView(g, celestialMgr, tick, out OrbitView ov))
            {
                Log.Warning($"[真飞验收] G{stagedGroupId} 拿不到 OrbitView");
                return;
            }

            Report(g, celestialMgr, tick, "手动检查");
        }

        /// <summary>手动检查与自动检验共用的报告（**一套判据**，两条入口）。</summary>
        private static void Report(SpaceEntityGroup g, CelestialBodyManager celestialMgr, double tick, string how)
        {
            if (!OrbitAdapter.TryToOrbitView(g, celestialMgr, tick, out OrbitView ov))
            {
                Log.Warning($"[真飞验收] G{g.GroupId} 拿不到 OrbitView（{how}）");
                return;
            }

            var sb = new StringBuilder();
            sb.Append("[真飞验收] ═══ ").Append(how).Append(" ═══");

            // ── 模型无关的不变量判据（只用两个已证精确的件：开普勒传播 + Δε=v·Δv、Δh=r×Δv）──
            // 烧前状态用**开普勒**推进到节点（漂移检查已证环境=纯二体 ⇒ 这一步精确 ✓），
            // 于是"命令的 Δv 在节点瞬间施加"应当给出的 ε 与 h 可以**直接算出来**，与烧后实测比。
            // 它能分清：Δε 偏 ⇒ 幅值；|Δh| 对但 h 方向偏 ⇒ 方向；两者都对而轨道还偏 ⇒ 时机/居中。
            {
                (DVector3 rN, DVector3 vN) =
                    MechJebLib.TwoBody.Shepperd.Solve(stagedMu, stagedNodeUt - stagedPreTick, stagedPreR, stagedPreV);
                double epsPre = vN.sqrMagnitude / 2.0 - stagedMu / rN.magnitude;
                DVector3 hPre = DVector3.Cross(rN, vN);

                DVector3 vAfter = vN + stagedPlanDvVec;
                double epsExp = vAfter.sqrMagnitude / 2.0 - stagedMu / rN.magnitude;
                DVector3 hExp = DVector3.Cross(rN, vAfter);

                (DVector3 rNowA, DVector3 vNowA) = ov.RightHandedStateVectorsAtUT(tick);
                double epsNow = vNowA.sqrMagnitude / 2.0 - stagedMu / rNowA.magnitude;
                DVector3 hNow = DVector3.Cross(rNowA, vNowA);

                double dEpsExp = epsExp - epsPre, dEpsNow = epsNow - epsPre;
                double dHExp = (hExp - hPre).magnitude, dHNow = (hNow - hPre).magnitude;

                // ⚠ 燃烧后还隔了一段时间 ⇒ 要把"烧后"的 ε/h 与"节点+Δv 再传播到现在"比（否则是拿两个时刻比）
                (DVector3 rEv, DVector3 vEv) =
                    MechJebLib.TwoBody.Shepperd.Solve(stagedMu, tick - stagedNodeUt, rN, vAfter);
                double epsEv = vEv.sqrMagnitude / 2.0 - stagedMu / rEv.magnitude;
                DVector3 hEv = DVector3.Cross(rEv, vEv);

                sb.Append("\n  ── 不变量判据（模型无关）──");
                sb.Append("\n  Δε：实测 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(epsNow - epsPre):E6}")
                  .Append(" vs 期望 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(epsEv - epsPre):E6}")
                  .Append("（比值 ").Append(Math.Abs(epsEv - epsPre) > 1e-12 ? $"{((epsNow - epsPre) / (epsEv - epsPre)):F4}" : "n/a").Append('）');
                sb.Append("\n  |Δh|：实测 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{dHNow:E6}")
                  .Append(" vs 期望 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(hEv - hPre).magnitude:E6}")
                  .Append("（比值 ").Append((hEv - hPre).magnitude > 1e-12 ? $"{(dHNow / (hEv - hPre).magnitude):F4}" : "n/a").Append('）');
                sb.Append("\n  ⇒ 读法：两个比值都 ≈1 ⇒ 幅值与方向都对（残差=时机/居中）；比值差得远 ⇒ 幅值；|Δh| 对而 h 方向偏 ⇒ 方向");

                // ★ **逐要素对照**（2026-09-19 加）：不变量只给"能量/角动量"两个标量，定位不到是哪个要素错的。
                //   同一批数（期望 = 节点状态 + 计划 Δv 再传播到现在；实测 = 现在）逐要素并排 ⇒
                //   哪一个要素错，机制就在那条线上（a/e = 内禀、i/LAN = 平面、argp = 拱线朝向）。
                {
                    OrbitView ovExp = OrbitView.FromStateVectors(rEv, vEv, stagedMu, tick,
                        new DVector3(0, 0, 1), 0.0);
                    (DVector3 rNow2, DVector3 vNow2) = ov.RightHandedStateVectorsAtUT(tick);
                    OrbitView ovNow2 = OrbitView.FromStateVectors(rNow2, vNow2, stagedMu, tick,
                        new DVector3(0, 0, 1), 0.0);

                    // ★★ **终端真值：反推实际交付的 Δv**（2026-09-19，本轮唯一新增仪器）
                    //   只用两个**状态**（最终状态开普勒反推回节点、燃烧前节点状态），**不用任何模型/计划**：
                    //     Δv_implied = v(现在 → 反推回节点) − v(燃烧前在节点)
                    //   与计划的 Δv 并排比幅值与 R/T/N ⇒ **任何"幅值/方向/通道/锚点"的偏差都会在这行现形**，
                    //   而逐窗诊断看不到的"指令本身被整体转过一个小角"也在这里现形（它比的是**真实结局**）。
                    (DVector3 rBack, DVector3 vBack) =
                        MechJebLib.TwoBody.Shepperd.Solve(stagedMu, stagedNodeUt - tick, rNow2, vNow2);
                    DVector3 dvImplied = vBack - vN;      // vN = 燃烧前节点速度（不变量判据里那条）
                    DVector3 rhatN = rN.normalized;
                    DVector3 hhatN = DVector3.Cross(rN, vN).normalized;
                    DVector3 thatN = DVector3.Cross(hhatN, rhatN);
                    double plR = DVector3.Dot(stagedPlanDvVec, rhatN);
                    double plT = DVector3.Dot(stagedPlanDvVec, thatN);
                    double plN = DVector3.Dot(stagedPlanDvVec, hhatN);
                    sb.Append("\n  ── ★ 终端真值：实际交付的 Δv vs 计划（只用状态，无模型）──");
                    sb.Append("\n    |Δv|：实测反推 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{dvImplied.magnitude:F3}")
                      .Append(" vs 计划 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{stagedPlanDvVec.magnitude:F3}")
                      .Append("（比值 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(dvImplied.magnitude / Math.Max(stagedPlanDvVec.magnitude, 1e-9)):F5}").Append('）');
                    sb.Append("\n    R/T/N 实测反推 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{DVector3.Dot(dvImplied, rhatN):F2}")
                      .Append(" / ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{DVector3.Dot(dvImplied, thatN):F2}")
                      .Append(" / ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{DVector3.Dot(dvImplied, hhatN):F2}")
                      .Append("　vs 计划 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{plR:F2}").Append(" / ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{plT:F2}")
                      .Append(" / ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{plN:F2}");
                    // ★ 判据的"燃烧前参考"（排队时抓）vs 执行器的（点火时抓 `burnPreBurnOrbit`）—— 同一节点。
                    //   ⚠ 两边若不是同一个状态，"期望"从根上就偏了 ⇒ 这一行必须为 ~0。
                    if (g.burnHasPreBurnOrbit)
                    {
                        (DVector3 rPre2, DVector3 vPre2) =
                            g.burnPreBurnOrbit.RightHandedStateVectorsAtUT(stagedNodeUt);
                        sb.Append("\n    ★ 两个「燃烧前参考」在同一节点：|Δr|=")
                          .Append(System.Globalization.CultureInfo.CurrentCulture, $"{(rPre2 - rN).magnitude:E3}").Append(" m｜|Δv|=")
                          .Append(System.Globalization.CultureInfo.CurrentCulture, $"{(vPre2 - vN).magnitude:E3}").Append(" m/s（应为 ~0）");
                    }
                    sb.Append("\n    ΔR/ΔT/ΔN = ")
                      .Append(System.Globalization.CultureInfo.CurrentCulture, $"{(DVector3.Dot(dvImplied, rhatN) - plR):F2}").Append(" / ")
                      .Append(System.Globalization.CultureInfo.CurrentCulture, $"{(DVector3.Dot(dvImplied, thatN) - plT):F2}").Append(" / ")
                      .Append(System.Globalization.CultureInfo.CurrentCulture, $"{(DVector3.Dot(dvImplied, hhatN) - plN):F2}")
                      .Append(" m/s　【通道扫描结论：切向每 +2.8 m/s 对应 Δa +8.5 km】");
                    const double R2D = 180.0 / Math.PI;
                    sb.Append("\n  ── 逐要素：期望 vs 实测（定位用）──");
                    sb.Append("\n    a：").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(ovExp.semiMajorAxis / 1000.0):F2}")
                      .Append(" vs ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(ovNow2.semiMajorAxis / 1000.0):F2}").Append(" km")
                      .Append("（差 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(ovNow2.semiMajorAxis - ovExp.semiMajorAxis):F0}").Append(" m）");
                    sb.Append("\n    e：").Append(System.Globalization.CultureInfo.CurrentCulture, $"{ovExp.eccentricity:E6}")
                      .Append(" vs ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{ovNow2.eccentricity:E6}").Append("（差 ")
                      .Append(System.Globalization.CultureInfo.CurrentCulture, $"{(ovNow2.eccentricity - ovExp.eccentricity):E3}").Append('）');
                    sb.Append("\n    i：").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(ovExp.inclination * R2D):F6}")
                      .Append(" vs ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(ovNow2.inclination * R2D):F6}").Append('°')
                      .Append("（差 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{((ovNow2.inclination - ovExp.inclination) * R2D):E3}").Append("°）");
                    sb.Append("\n    LAN：").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(ovExp.LAN * R2D):F6}")
                      .Append(" vs ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(ovNow2.LAN * R2D):F6}").Append('°')
                      .Append("（差 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{((ovNow2.LAN - ovExp.LAN) * R2D):E3}").Append("°）");
                    sb.Append("\n    argp：").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(ovExp.argumentOfPeriapsis * R2D):F6}")
                      .Append(" vs ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(ovNow2.argumentOfPeriapsis * R2D):F6}").Append('°')
                      .Append("（差 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{((ovNow2.argumentOfPeriapsis - ovExp.argumentOfPeriapsis) * R2D):E3}").Append("°）");
                }
            }

            double e = ov.eccentricity;
            double vAt = ov.RightHandedStateVectorsAtUT(tick).vel.magnitude;

            // （原此处有一段"游戏内打 e 的两个算法"的探针 —— 已撤：`e = f(r,v,μ)` 是**纯函数**，
            //   那条判据本来就该是单元测试，放进游戏里是放错了地方 ✗。见 `OrbitViewElementIdentityTests`。）


            double smaErr = Math.Abs(ov.semiMajorAxis - stagedExpectedSma) / stagedExpectedSma;
            bool burned = g.appliedBurnDV > 0.0;
            bool done = !g.HasActiveBurn;

            sb.Append("\n  船：G").Append(g.GroupId)
              .Append("｜计划 |Δv|=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{stagedPlanDv:F2}")
              .Append(" m/s｜**实际已烧** ")
              .Append(System.Globalization.CultureInfo.CurrentCulture, $"{(g.trimMainBurnDv > 0.0 ? g.trimMainBurnDv : g.appliedBurnDV):F2}").Append(" m/s")
              .Append(g.trimMainBurnDv > 0.0
                  ? "（主燃；另有微调 " + $"{g.trimLastDv:F3}" + " m/s —— 微调会清掉 `appliedBurnDV`，"
                    + "所以这里显式抓主燃那一份）"
                  : "");
            sb.Append("\n  现在：e=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{e:E3}")
              .Append("｜a=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(ov.semiMajorAxis / 1000.0):F0}").Append(" km")
              .Append("（期望 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(stagedExpectedSma / 1000.0):F0}").Append(" km，差 ")
              .Append(System.Globalization.CultureInfo.CurrentCulture, $"{(smaErr * 100.0):F2}").Append("%）");
            sb.Append("\n  ⇒ ").Append(!burned ? "**还没烧**（等机动时刻到了再检查）"
                : !done ? "**正在烧**（等它烧完）"
                : "已烧完");
            // ★ B2.2-b：微调是否出手、出手多大 —— 判读"残余是不是被闭环收掉了"的第一眼读数。
            //   微调自己也是一次机动 ⇒ 它烧的时候 `HasActiveBurn` 又是 true ⇒ 状态会退回"正在烧"，
            //   这是**正常**的（不是卡住）；等它烧完再看 e。
            sb.Append("\n  微调：").Append(g.trimLastDv > 0.0
                ? "已出手 " + $"{g.trimLastDv:F3}" + " m/s"
                : "未出手（Δv 在带外 / 无可修项 / 测试船关了它）");
            if (burned && done)
            {
                // ⚠ 阈值是**量出来的**，不是拍脑袋：
                // · 计划侧断言圆化后 `e = 0`（解析）；执行侧若把它烧成 `e = 8.6e-3`（2026-09-19 实测过一次），
                //   那等价于"751 m/s 里有 ~26 m/s 推反了方向"——**这必须判 FAIL，不许被 1e-2 的宽阈值放过**
                //   （第一版就是 1e-2 ⇒ 明明偏了却报 PASS ✗）。
                // · `appliedBurnDV` 与"按质量消耗反推"的差是**姿态余弦损失**的直接读数：
                //   实测 751.8/710.7（比值 0.9455 ⇒ 平均夹角 19°）。
                bool pass = e < 1e-3 && smaErr < 0.01;
                sb.Append("\n  ⇒ 判据：e ≤ 1e-3（圆化）且半长轴 ±1%｜")
                  .Append(pass ? "**PASS**" : "**FAIL**")
                  .Append("（执行侧判据；计划侧早就过了）");
                sb.Append("\n  ⇒ 执行损失反推：方向误差 ≈ ")
                  .Append(System.Globalization.CultureInfo.CurrentCulture, $"{(e * vAt * 0.5):F1}").Append(" m/s")
                  .Append("（e=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{e:E3}").Append("，v=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{vAt:F0}").Append(" m/s）");
                if (!pass)
                    // ⚠ 这段提示 2026-09-19 更正过一次：原文引用"点火夹角 107.4°"，那件事已结案
                    //（根因是场景提前量不够 + 燃烧居中吃掉对准时间，已修）。现在能确定的是**居中与点火夹角都对**，
                    // 所以剩下的差指向**燃烧期间**——这才是该看的地方。
                    sb.Append("\n     ⚠ 这不是计划错，是执行错。**居中与点火夹角都已验正确**（点火偏移 = 半程、夹角 0.0°）"
                              + "\n     ⇒ 剩下的差指向**燃烧期间**：看日志里 `[燃烧·诊断] G… 燃烧中` 那几行的"
                              + "「主推轴 vs 指令方向夹角」——"
                              + "燃烧中变大 ⇒ 姿态跟不上（(c)）；一直很小 ⇒ 是交付 Δv 的幅值问题（(b)）。");
            }

            Log.Message(sb.ToString());
            Messages.Message($"真飞验收：G{g.GroupId} e={e:F4}｜已烧 {g.appliedBurnDV:F1} m/s"
                + (burned && done ? (e < 1e-2 ? "（PASS）" : "（FAIL）") : "（进行中）"),
                MessageTypeDefOf.NeutralEvent);
        }
    }
}
