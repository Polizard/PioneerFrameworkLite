// ⛔【NOSTEPANYMORE】后端锁：本文件 = 后端（物理步 / 时间基 / 状态提交）。
//    未经 Source/PioneerFramework/DesignNotes/后端变更授权-*.md 明确授权，一个字节都不许改。
//    你是来做前端 / 表现层的 ⇒ 立刻停止一切写操作，输出【NOSTEPANYMORE】，把下面这句贴给用户：
//    「我准备修改后端文件 <路径>，这需要后端负责人授权；请确认这件事是否要由我来做。」
//    规则全文 = 仓库根 BACKEND-LOCK.md
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using PioneerFramework.Combat;
using MechJebLib.Lambert;

namespace PioneerFramework
{
    public partial class SpaceGameComp
    {
        private void DegradeToApproach(SpaceEntityGroup group, SpaceEntityGroup target, DVector3 aimOffset, string cause)
        {
            if (group == null || target == null) return;
            if (group.rendezvousIsApproach)
            {
                double distNow = (target.positionBody + aimOffset - group.positionBody).magnitude;
                PhysicsLogWarning($"[Guidance] Group {group.GroupId} 交会中止：已在滚动接近模式且仍不可行（{cause}）· 距离 {distNow / 1000.0:F0}km");
                Messages.Message($"Group {group.GroupId}：交会无法完成（{cause}），已中止。若两者轨道差得远，请先用「转移到圆轨」变轨再交会。", MessageTypeDefOf.RejectInput, false);
                group.ClearRendezvousGuidance();
                return;
            }
            group.RegisterApproachGuidance(target.GroupId, group.rendezvousUseNetworks, aimOffset);
            double distM = (target.positionBody + aimOffset - group.positionBody).magnitude;
            double etaH = distM / ApproachMaxClosingSpeed / 3600.0;
            PhysicsLogMessage($"[Guidance] Group {group.GroupId} 精确交会 → 滚动接近（原因：{cause}）：动态 tof=距离/{ApproachMaxClosingSpeed:F0}m/s 修正量有界；当前距离 {distM / 1000.0:F0}km ⇒ 限速下约 {etaH:F1} 小时到达（远距降级会慢，属预期）");
            Messages.Message($"Group {group.GroupId}：精确交会已转为滚动接近（{cause}）",
                MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>相位差（弧度，>0 = 本船落后需要追）：用两船相对中心天体的相位角算。</summary>
        private double PhaseErrorRadians(SpaceEntityGroup group, SpaceEntityGroup target, double gm)
        {
            if (group == null || target == null) return 0.0;
            DVector3 rA = group.positionBody, rB = target.positionBody;
            DVector3 nA = DVector3.Cross(rA, group.velocityBody);
            if (nA.sqrMagnitude < 1e-12) return 0.0;
            DVector3 n = nA.normalized;
            // 目标在本船轨道面上的分量角
            DVector3 rBp = rB - n * DVector3.Dot(rB, n);
            if (rBp.sqrMagnitude < 1e-6 || rA.sqrMagnitude < 1e-6) return 0.0;
            double cosA = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(rA.normalized, rBp.normalized)));
            double angle = Math.Acos(cosA);
            // 目标在本船速度方向的哪一侧（>0 = 在前方 = 本船落后）
            double side = DVector3.Dot(DVector3.Cross(rA, rBp), n);
            return side >= 0.0 ? angle : -angle;
        }

        /// <summary>主推轴与目标方向的夹角（度）。无主推轴（全向类）返回 0。</summary>
        private double MainAxisBurnAngleDeg(SpaceEntityGroup group, DVector3 targetDir)
        {
            DVector3 axis = GetMainThrustAxisSystem(group);
            if (axis.sqrMagnitude < 1e-12 || targetDir.sqrMagnitude < 1e-12) return 0.0;
            double dot = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(axis.normalized, targetDir.normalized)));
            return Math.Acos(dot) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// 预对准开始日志：给出"当前夹角 / 按动量轮限速需要多久 / 距节点还有多久"三个数，
        /// 让"对准有没有留够时间"当场可判（预对准不耗料，故不涉及工质）。
        /// </summary>
        private void LogPreAlignStarted(SpaceEntityGroup group, int executeTick, double physicsTick)
        {
            double angleDeg = MainAxisBurnAngleDeg(group, group.pendingAlignDir);
            double rateDegS = Math.Max(group.MaxAngularRate, 1e-6) * Mathf.Rad2Deg;
            PhysicsLogMessage($"[预对准] G{group.GroupId} 开始转向下一条机动：主推轴夹角 {angleDeg:F1}°（按 {rateDegS:F1}°/s 约需 {angleDeg / rateDegS:F0}s，节点 T-{executeTick - physicsTick:F0}s）");
        }

        // ══════════════════════════════════════════════════════════════════════════════════
        // 批7 #2（2026-09-30）：阶梯规划的**本拍预取** —— 把"这一拍要重规划的组"的 `MakePlan`
        // 先**并行**算出来（k 个组从 Σ(k) 变 ≈max(k)；实测天花板：8 组同拍 12.49 → 2.95 ms，4.24×），
        // 主循环里照旧**串行提交**（排队/日志/状态写入一律留在主线程，R4 纪律）。
        //
        // ⚠ **行为不可能变**，靠三道保险：
        //   ① 预取只搬**纯计算**（`MakePlan` 是 (OrbitView, OrbitView, tick, Params) 的纯函数）；
        //   ② 主循环用到它之前**核对输入指纹**（两个 `OrbitView` 的 r/v **逐位**、tick、以及三个会变的
        //      Params 来源）—— 不一致 ⇒ **回退到原来那行内联调用**（结果必然正确）；
        //   ③ 预取是**最优努力**：没预取到、或预取多了没用上，都只是"少省一点"，不影响任何语义。
        // ══════════════════════════════════════════════════════════════════════════════════
        private struct LadderPrefetch
        {
            public double physicsTick;
            public OrbitView self, goal;
            public double desiredDistance, matchDistance, safeRadius;
            public RendezvousLadder.Rung rung;
            public List<PlanNode>? nodes;
            public string? status;
        }

        private Dictionary<int, LadderPrefetch> ladderPrefetch = new();

        /// <summary>本进程内**内联**阶梯规划被调用的次数（只喂 `[阶梯·耗时]` 的 `#序号`，DevMode 用）。</summary>
        private int ladderPlanCalls;

        /// <summary>后台 200 次 warmup 的曲线（完成前为 null）；由 `GameComponentTick` 在主线程打一次。</summary>
        private string? ladderWarmBg;

        /// <summary>本拍预取的阶梯计划（`UpdateRendezvousGuidance` 开头调一次；主循环里消费）。</summary>
        private void PrefetchLadderPlans(double physicsTick)
        {
            long pfClock = Prefs.DevMode ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            ladderPrefetch.Clear();
            var mgr = celestialRef.Get();
            if (mgr == null) return;

            // ── 串行：筛候选 + 快照输入（只读；与主循环同序） ──
            var ids = new List<int>();
            var selfs = new List<OrbitView>();
            var goals = new List<OrbitView>();
            var parsList = new List<RendezvousLadder.Params>();
            foreach (var g in groups.Values)
            {
                if (!g.HasRendezvousGuidance || !g.IsMovable || !g.ladderEnabled) continue;
                if (g.HasActiveBurn && !g.burnIsCorrection) continue;
                if (physicsTick - g.ladderLastPlanTick < g.ladderMinReplanSeconds) continue;   // 节流没过 ⇒ 本拍不规划
                if (!groups.TryGetValue(g.rendezvousTargetId, out var tgt) || !tgt.IsActive) continue;
                // 走新律的组在主循环里**提前 return**、到不了阶梯块 ⇒ 别为它们白算（预取要最优努力，别浪费）
                if (UseNewRendezvousLaw && !g.rendezvousIsApproach && tgt.CelestialBodyId == g.CelestialBodyId) continue;
                if (!OrbitAdapter.TryToOrbitView(g, mgr, physicsTick, out OrbitView s)) continue;
                if (!OrbitAdapter.TryToOrbitView(tgt, mgr, physicsTick, out OrbitView t)) continue;

                ids.Add(g.GroupId);
                selfs.Add(s);
                goals.Add(t);
                parsList.Add(new RendezvousLadder.Params
                {
                    DesiredDistance = g.EffectiveRendezvousGoal.distanceTol,
                    MatchDistance = g.rendezvousMatchDist,
                    MaxClosingSpeed = 100.0,
                    MaxPhasingOrbits = 5.0,
                    MinRadius = GetBodySafeRadius(g.CelestialBodyId),
                });
            }
            if (ids.Count == 0) return;

            // ── 并行：每组各写自己的槽位（单项成本取实测量级 ~1.7 ms 是保守的） ──
            int n = ids.Count;
            var rungs = new RendezvousLadder.Rung[n];
            var nodesArr = new List<PlanNode>[n];
            var statusArr = new string[n];
            ParallelOps.For(n, 2e-3, k =>
            {
                rungs[k] = RendezvousLadder.MakePlan(selfs[k], goals[k], physicsTick, parsList[k],
                    out List<PlanNode> nodes, out string status);
                nodesArr[k] = nodes;
                statusArr[k] = status;
            });

            // ── 串行：入表（键 = 组 id；指纹随条目一起存） ──
            for (int k = 0; k < n; k++)
                ladderPrefetch[ids[k]] = new LadderPrefetch
                {
                    physicsTick = physicsTick,
                    self = selfs[k],
                    goal = goals[k],
                    desiredDistance = parsList[k].DesiredDistance,
                    matchDistance = parsList[k].MatchDistance,
                    safeRadius = parsList[k].MinRadius,
                    rung = rungs[k],
                    nodes = nodesArr[k],
                    status = statusArr[k],
                };

            // 慢预取读数（2026-10-19）：2.1ms 引导尖峰到底是一份重几何，还是多组同拍重规划。
            if (pfClock != 0)
            {
                double pfMs = (System.Diagnostics.Stopwatch.GetTimestamp() - pfClock) * 1000.0
                              / System.Diagnostics.Stopwatch.Frequency;
                if (pfMs > 2.0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append(System.Globalization.CultureInfo.CurrentCulture, $"[阶梯·预取] {pfMs:F1} ms｜{n} 份");
                    for (int k = 0; k < n; k++)
                        sb.Append(System.Globalization.CultureInfo.CurrentCulture, $" G{ids[k]}/{rungs[k]}");
                    PhysicsLogMessage(sb.ToString());
                }
            }
        }

        /// <summary>
        /// 取本拍预取的阶梯计划：**只有输入指纹与当下完全一致才认**（逐位比 r/v + tick + 三个 Params 来源）。
        /// 认不了就返回 false ⇒ 调用点回退到内联 `MakePlan`（行为与没有预取时**一模一样**）。
        /// </summary>
        private bool TryUseLadderPrefetch(SpaceEntityGroup group, OrbitView self, OrbitView goal,
            double physicsTick, double desiredDistance, double matchDistance, double safeRadius,
            out RendezvousLadder.Rung rung, out List<PlanNode> nodes, out string status)
        {
            rung = default;
            nodes = new List<PlanNode>();
            status = "";
            if (!ladderPrefetch.TryGetValue(group.GroupId, out LadderPrefetch p)) return false;
            if (p.physicsTick != physicsTick) return false;
            if (!p.self.r.Equals(self.r) || !p.self.v.Equals(self.v)) return false;      // 逐位（`DVector3.Equals` 按位）
            if (!p.goal.r.Equals(goal.r) || !p.goal.v.Equals(goal.v)) return false;
            if (p.desiredDistance != desiredDistance || p.matchDistance != matchDistance || p.safeRadius != safeRadius)
                return false;
            if (p.nodes == null || p.status == null) return false;

            rung = p.rung;
            nodes = p.nodes;
            status = p.status;
            return true;
        }

        /// <summary>
        /// **闭式霍曼的 DevMode 影子对照**（2026-10-21 正确性保险；交接 §11.36-N「若要加保险」）。
        ///
        /// 闭式霍曼与旧搜索**不再逐位相同**（台架：Δv ≤0.01%、点火时刻 ≤0.11% 周期）。这条探针让
        /// 这种差异在**每一份真实计划上**自证一次，而不是只靠语料：同一几何再跑一次原搜索，
        /// 把两边的 Δv 与点火时刻差打进日志。
        ///
        /// · **不参与任何决策**：结果只打日志、随即丢弃（计划/节点/组状态一个字段都不动）；
        /// · **默认关**：`DebugShadowCompareClosedForm` 打开才跑（每份闭式计划多一次搜索 ≈1.7–2.7 ms，
        ///   用户 2026-10-21 令"不允许增加性能消耗" ⇒ 关着时调用点只多读一个 static bool）；
        /// · 调用点在 `[阶梯·耗时]` 计时块**之后** ⇒ 不污染规划成本读数；
        /// · 搜索侧无解/抛异常也只打一行（闭式已给出计划，不影响）。
        /// </summary>
        private void ShadowCompareClosedFormHohmann(SpaceEntityGroup group, OrbitView self, OrbitView goal,
            double now, List<PlanNode> nodes)
        {
            try
            {
                (DVector3 sv1, double st1, DVector3 sv2, double st2) =
                    OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(self, goal, now, coplanar: false);
                if (double.IsNaN(st1) || double.IsInfinity(st1) || double.IsNaN(st2) || double.IsInfinity(st2))
                {
                    Log.Message($"[引导·影子对照] G{group.GroupId} 搜索侧无解（闭式已给出计划，不影响）");
                    return;
                }
                double dvSearch = sv1.magnitude + sv2.magnitude;
                double dvClosed = RendezvousLadder.TotalDv(nodes);
                double rel = dvSearch > 1e-9 ? Math.Abs(dvClosed - dvSearch) / dvSearch : 0.0;
                double utDelta = nodes.Count > 0 ? Math.Abs(nodes[0].ut - st1) : double.NaN;
                double period = self.period;
                double utRel = period > 0.0 ? utDelta / period : double.NaN;
                Log.Message($"[引导·影子对照] G{group.GroupId} Δv 闭式 {dvClosed:F2} / 搜索 {dvSearch:F2} m/s" +
                            $"（相对差 {rel:P2}）｜点火 UT 差 {utDelta:F0} s（{utRel:P2} 周期）");
            }
            catch (Exception ex)
            {
                Log.Message($"[引导·影子对照] G{group.GroupId} 搜索侧抛异常（闭式已给出计划，不影响）：" +
                            $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 交会/接近引导：为处于引导的群组按实际状态修正轨迹（MechJeb 式"评估→修正→冷却"节奏）。
        /// 两种模式共用一套引擎：
        /// - 精确交会（rendezvousIsApproach=false）：出发段点火后生效，固定到达时刻，巡航 Lambert + 终端消速；
        /// - 近距离接近（rendezvousIsApproach=true）：登记即生效，到达时刻按当前距离动态重算（tof=dist/限速），
        ///   巡航 Lambert 拦截 + 终端"消速+温柔收拢"，贴近且相对速度归零即完成。
        /// 无引导 / 燃烧中 / 队列里还有未执行机动（出发段或玩家接管）时跳过。
        /// 巡航段：检查节流（30s）+ 预测脱靶量门控（&lt; 2km 视为 on-course 零烧），脱靶才 Lambert 修正；
        /// 终端段（tof &lt; 60s）：消相对速度。修正烧后冷却 60s，避免追逐残差。
        /// 修正烧复用 StartBurn（固定惯性方向 + 工质消耗 + 撞行星保护）。
        /// 计算成本：节流后每次检查 = 1~2 次解析传播 + 偶尔 1 次 Lambert（µs 级），无性能压力。
        /// </summary>
        private void UpdateRendezvousGuidance(double physicsTick)
        {
            PrefetchLadderPlans(physicsTick);   // 批7 #2：本拍要重规划的组先并行算好（见其文档的三道保险）
            foreach (var group in groups.Values)
            {
                if (!group.HasRendezvousGuidance) continue;
                if (!group.IsMovable) continue;
                if (group.HasActiveBurn && !group.burnIsCorrection) continue; // 机动 burn 不打断；修正 burn 允许重规划

                // 队列里还有未执行机动（出发段在排队 / 玩家新排队）→ 引导让位
                // ★ B5：判据本体在 `BurnExecutorPolicy.GuidanceYieldsToQueue`（任何条目都让，不只 Manual ——
                //   比纯规则 `PlanOwnerRules.GuidanceMustYieldTo` 严；台架同一份）
                if (BurnExecutorPolicy.GuidanceYieldsToQueue(group.plannedManeuvers.Count)) continue;

                // 出发段尚未执行（仅精确交会模式）：若机动已被弹出（被阻止/取消）则引导作废
                // 接近模式无出发段（登记即生效），跳过此检查
                if (!group.rendezvousArmed && !group.rendezvousIsApproach)
                {
                    PhysicsLogMessage($"[Guidance] Group {group.GroupId} departure maneuver never executed, rendezvous guidance cancelled.");
                    group.ClearRendezvousGuidance();
                    continue;
                }

                if (!groups.TryGetValue(group.rendezvousTargetId, out var target) || !target.IsActive)
                {
                    PhysicsLogMessage($"[Guidance] Group {group.GroupId} rendezvous target {group.rendezvousTargetId} gone, guidance cancelled.");
                    group.ClearRendezvousGuidance();
                    continue;
                }

                // 瞄准偏移（跳帮落点）：目标局部系偏移 → 世界米偏移（含目标实体偏移与姿态旋转）。
                // 零偏移 = 瞄准 group 中心（原行为）。
                DVector3 aimOffset = ResolveTargetAimOffset(group, target);

                DVector3 r1Rel = group.positionBody;
                DVector3 v1Rel = group.velocityBody;

                // ── 新远场律（2026-09-17 接线）──────────────────────────────────────
                //   与纯测试台架**共用同一个入口** `RendezvousGuidanceLaw.Compose`（律只写一次）。
                //   本分支只负责"取状态 / 定预算 / 选终端"，产出的 Δv 走**与旧路径共用的落地段**
                //   （死区→可行性→撞行星保护→执行器→点火），故新律**原样继承**旧路径的全部保护。
                //
                //   口径差异（相对旧路径）：旧路径围绕"固定到达时刻 + 巡航 Lambert 修正"展开；
                //   新律是**无截止时刻的反馈律**、自带分腿 ⇒ 不用 rendezvousArrivalTick，也**不设**
                //   pendingAlignDir——旧预对准瞄的是"到达点消速方向"，对新律是错的目标，会与切向
                //   nudge 打架（小 Δv 走 RCS 全矢量、大 Δv 由 burn 内部对准，都不需要它）。
                if (UseNewRendezvousLaw && !group.rendezvousIsApproach
                    && target.CelestialBodyId == group.CelestialBodyId)
                {
                    RunNewRendezvousLaw(group, target, aimOffset, r1Rel, v1Rel, physicsTick);
                    continue;
                }

                // ⚠ 参考系前提（跨天体时显式挡住，不静默算错）：
                //   `TryPredictTargetAtTick` 给的是**目标自己中心天体**系下的状态，而 r1Rel 是
                //   追方中心天体系的 —— 两者只有在**同一中心天体**时才同系（旧路径隐含同一前提）。
                //   跨天体（绕地追绕月）时两系相差一个平移与速度 ⇒ 新律会去追一个**幻影相位差**并
                //   持续烧工质。这里退回旧路径（它至少还有脱靶量门控），并每检查周期报一次原因。
                if (UseNewRendezvousLaw && !group.rendezvousIsApproach
                    && target.CelestialBodyId != group.CelestialBodyId
                    && physicsTick >= group.rendezvousLastCheckTick + NewLawControlDt)
                {
                    group.rendezvousLastCheckTick = physicsTick;
                    PhysicsLogWarning($"[Guidance] Group {group.GroupId} 与目标 {target.GroupId} 的中心天体不同（{group.CelestialBodyId} vs {target.CelestialBodyId}）：新律的相位差需要同系状态，已退回旧路径。跨天体交会需先做参考系拼接（未实现）。");
                }

                // ── 巡航预对准（持续）：出发 burn 完成后立刻调头，为下一次主推（终端消速）提前对准 ──
                // 终端 dv ≈ (目标到达速度 − 本船速度)，用当前相对速度方向做代理姿态；
                // 终端点火时已对准 → 无转向死区（否则最后 180° 调头 + 4km/s 消速叠一起必炸）。
                // 修正 burn 点火时由 burn 内部 AlignMainAxis 覆盖此姿态，烧完自动回归。
                if (!group.HasActiveBurn)
                {
                    if (TryPredictTargetAtTick(target, group.rendezvousArrivalTick, out _, out var vTargetHold))
                    {
                        DVector3 killDir = (vTargetHold - group.velocityBody).normalized;
                        if (killDir.sqrMagnitude > 1e-9)
                            group.pendingAlignDir = killDir;
                    }
                }

                double arrivalTick;
                double tof;
                if (group.rendezvousIsApproach)
                {
                    // 接近模式：动态到达时刻 = now + dist / 限速（修正量有界、轨道扰动小）；
                    // 贴近且相对速度归零 → 接近完成
                    double distNow = (target.positionBody + aimOffset - r1Rel).magnitude;
                    double relvelNow = (target.velocityBody - v1Rel).magnitude;
                    if (distNow < ApproachCompletionDistance && relvelNow < ApproachCompletionRelVel)
                    {
                        PhysicsLogMessage($"[Guidance] Group {group.GroupId} approach complete: dist={distNow:F0}m relvel={relvelNow:F1} m/s");
                        Messages.Message($"Group {group.GroupId} 已接近 Group {group.rendezvousTargetId}（距离 {distNow:F0} m）", MessageTypeDefOf.TaskCompletion);
                        group.ClearRendezvousGuidance();
                        continue;
                    }
                    // ★ 时间律改成 **MJ 的形式**（2026-09-20，条目 16：我们抄错了 MJ）：
                    //   `tof = 逐轴 d / min(√(2·d·a), 限速)`（见 `RendezvousGuidanceCore.ApproachTimeSeconds`）。
                    //   ⚠ 旧写法是常数时间律 `tof = dist/50` ✗ + 一个"进 5 km 就 tof=1"的补丁压极限环 ✗✗；
                    //     MJ 这条对距离**连续** ⇒ **补丁不需要了**（顺手删掉一个静默失效面）。
                    double aApp = Math.Max(EstimateMainAcceleration(group), 0.01);
                    // 相对位置在**本船 RTN**上分解（`r1Rel/v1Rel` 就是本船相对中心天体的状态 ⇒ 它的 RTN 即本船 RTN ✓）
                    DVector3 rhatA = r1Rel.normalized;
                    DVector3 hhatA = DVector3.Cross(r1Rel, v1Rel).normalized;
                    DVector3 thatA = DVector3.Cross(hhatA, rhatA);
                    DVector3 relA = target.positionBody + aimOffset - r1Rel;      // 本船 → 目标
                    tof = Math.Max(RendezvousGuidanceCore.ApproachTimeSeconds(
                        DVector3.Dot(relA, rhatA), DVector3.Dot(relA, thatA), DVector3.Dot(relA, hhatA),
                        aApp, ApproachMaxClosingSpeed), 1.0);
                    arrivalTick = physicsTick + tof;
                }
                else
                {
                    arrivalTick = group.rendezvousArrivalTick;
                    tof = arrivalTick - physicsTick;
                    if (tof <= 0.0)
                    {
                        // 到达点已过：交会完成（终端消速已在上个检查周期内完成），不追打
                        PhysicsLogMessage($"[Guidance] Group {group.GroupId} rendezvous complete (arrival passed).");
                        group.ClearRendezvousGuidance();
                        continue;
                    }
                }

                // ── 终端判定（动态）：固定窗口 60s，或「消相对速度所需时间已逼近剩余时间」→ 提前进终端 ──
                // 巡航 Lambert 只保证到点位置、不保证到点速度；必须提前开始消速（相对速度大时动辄数百秒）。
                bool inTerminal = tof < GuidanceTerminalWindow;
                double aMain = EstimateMainAcceleration(group);
                if (!inTerminal && aMain > 0.01
                    && TryPredictTargetAtTick(target, arrivalTick, out _, out var vTermRel0))
                {
                    DVector3 drEst = target.positionBody + aimOffset - r1Rel;
                    double distEst = drEst.magnitude;
                    double relVel = (vTermRel0 - v1Rel).magnitude;
                    // 终端 = 第二次主推（主引擎全力消速）：在"刹车距离"处入场——
                    // 刹车距离 = relVel²/(2·aMain)，此时全力消速恰好覆盖剩余距离，到目标时速度归零。
                    // 过早入场会停短（曾出现 ~1000km 停短）；过晚入场会冲过。
                    double brakingDist = relVel * relVel / (2.0 * aMain);
                    if (distEst <= brakingDist + 30000.0) // +30km 余量（引导 10s 节拍），末段位置收敛补正
                        inTerminal = true;
                }

                // ── 巡航预对准（持续）：出发 burn 完成后立刻调头，为下一次主推（终端消速）提前对准 ──
                // 终端 dv ≈ (目标到达速度 − 本船速度)，用当前相对速度方向做代理姿态；
                // 终端点火时已对准 → 无转向死区。修正 burn 点火时由 burn 内部 AlignMainAxis 临时覆盖。
                if (!group.HasActiveBurn)
                {
                    if (TryPredictTargetAtTick(target, arrivalTick, out _, out var vTargetHold))
                    {
                        DVector3 killDir = (vTargetHold - v1Rel).normalized;
                        if (killDir.sqrMagnitude > 1e-9)
                            group.pendingAlignDir = killDir;
                    }
                }

                // 修正烧后冷却 + 检查节流：避免追逐残差与高频点火（on-course 时大多更新直接跳过）。
                // 终端段跳过冷却（消速要连续迭代）；进行中的修正 burn 也不受冷却限制（允许打断重烧）。
                if (!group.HasActiveBurn && !inTerminal
                    && physicsTick < group.rendezvousLastBurnTick + GuidanceBurnCooldown) continue;
                double checkInterval = inTerminal ? GuidanceTerminalCheckInterval : GuidanceCheckInterval;
                if (physicsTick < group.rendezvousLastCheckTick + checkInterval) continue;
                group.rendezvousLastCheckTick = physicsTick;

                double gm = GetBodyGM(group.CelestialBodyId);
                double missM = double.NaN;
                DVector3 dv;

                if (inTerminal)
                {
                    // 终端 = 第二次主推（主引擎全力消速）：dv = 目标速度 − 当前速度（4.4km/s 级）。
                    // 入场已按刹车距离门控（relVel²/2aMain），全力消速恰好覆盖剩余距离 → 到目标速度归零。
                    // 速度接近匹配但距离仍大时补位置收敛（轨迹整形），避免"停在远处"。
                    if (!TryPredictTargetAtTick(target, arrivalTick, out _, out var vTargetRel))
                        continue;
                    DVector3 drClose = target.positionBody + aimOffset - r1Rel;
                    double distClose = drClose.magnitude;
                    dv = vTargetRel - v1Rel;
                    double relVel = dv.magnitude;
                    if (relVel < 100.0 && distClose > 10000.0)
                    {
                        // 速度已匹配但距离还大：补位置收敛（dr/tof，RCS 级小修正）
                        dv = vTargetRel + drClose / Math.Max(tof, 1.0) - v1Rel;
                    }
                    double needDv = dv.magnitude; // 数学模型：主推 Δv（或末段综合 dv）
                    // 对比日志：距离 + 相对速度 + 主推 Δv（数学模型目标）
                    if (DebugDetailedSteering)
                    {
                        PhysicsLogMessage($"[Terminal] G{group.GroupId} tof={tof:F0}s dist={distClose / 1000.0:F0}km relVel={relVel:F1}m/s mainDv={needDv:F1}m/s");
                    }
                }
                else
                {
                    // 巡航：预测脱靶量门控 → 只有真的会脱靶才修，on-course 零烧
                    if (!TryPredictTargetAtTick(target, arrivalTick, out var r2Rel, out _))
                        continue;
                    DVector3 r2Aim = r2Rel + aimOffset;
                    DVector3 r1ArrivalPred = OrbitMath.GetPositionAtTimeDelta(r1Rel, v1Rel, tof, gm);
                    missM = (r1ArrivalPred - r2Aim).magnitude;
                    if (double.IsNaN(missM) || missM < GuidanceMissThreshold)
                        continue;
                    TransferGeometry geom = group.rendezvousPrograde
                        ? TransferGeometry.ShortWay : TransferGeometry.LongWay;
                    DVector3 vi;
                    try
                    {
                        (vi, _) = Gooding.Solve(gm, r1Rel, r2Aim, tof, geom);
                    }
                    catch (Exception)
                    {
                        continue; // 本次无解，下次再试
                    }
                    dv = vi - v1Rel;
                }

                ApplyGuidanceCorrection(group, target, aimOffset, gm, physicsTick, r1Rel, v1Rel, dv,
                    inTerminal, inTerminal, tof, missM,
                    group.rendezvousIsApproach ? "(approach)" : inTerminal ? "(terminal)" : "",
                    newLawMode: false, deadbandMps: GuidanceBurnDeadband);
            }
        }

        /// <summary>
        /// 交会的**终点**：如实放弃并告诉玩家。不复用"降级到另一套引擎"——那在远目标上会算出
        /// 没道理的账单（限速 167 小时 / 一次 3828 m/s 拦截），实测还会每周期重挂一次引导空转。
        /// 消息里给出**可执行的下一步**（先用「转移到圆轨」变轨），而不是只报错。
        /// </summary>
        private void GiveUpRendezvous(SpaceEntityGroup group, SpaceEntityGroup target, DVector3 aimOffset, string cause)
        {
            if (group == null) return;
            double distKm = target != null
                ? (target.positionBody + aimOffset - group.positionBody).magnitude / 1000.0
                : double.NaN;
            PhysicsLogWarning($"[Guidance] Group {group.GroupId} 交会中止：{cause}（距离 {distKm:F0}km）");
            Messages.Message($"Group {group.GroupId}：交会中止（{cause}）。若两者轨道相差较远，请先用「转移到圆轨」变轨后再交会。", MessageTypeDefOf.RejectInput, false);
            group.ClearRendezvousGuidance();
        }
    }
}