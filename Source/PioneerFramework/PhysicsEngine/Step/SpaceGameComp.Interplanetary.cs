using System;
using System.Collections.Generic;
using PioneerFramework.Celestial;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **行星际粗转移入口**（2026-11-18，授权件 `后端变更授权-20261118-行星际粗转移`）。
    ///
    /// 只做"排一次出发点火"：扫描窗口 → 选最省 Δv 的 (等待时长, 转移时长) → 把 Δv 排进既有
    /// `plannedManeuvers` 队列（到点由现有执行器点火）。**不新增执行机制**。
    ///
    /// 到达捕获不在这里，在 `CheckGroupSoi` 进 SOI 的分支（见设计稿 §5.2）。
    /// </summary>
    public partial class SpaceGameComp
    {
        public const int InterplanetaryPhaseNone = 0;
        public const int InterplanetaryPhaseOutbound = 1;
        public const int InterplanetaryPhaseCapture = 2;
        public const int InterplanetaryPhaseCaptured = 3;
        public const int InterplanetaryPhaseFlyby = 4;

        // 导航结束结果（2026-11-20）：写在组上给 UI 显示"上一次导航为什么停了"。
        // 只有"结束"这个动作会写；进行中保持 None。
        public const int InterplanetaryOutcomeNone = 0;
        public const int InterplanetaryOutcomeCompleted = 1;
        public const int InterplanetaryOutcomeCompletedWithWarning = 2;
        public const int InterplanetaryOutcomeCancelled = 3;

        /// <summary>飞行追踪探针的瞬时状态（**不序列化**）：groupId → (下次打点 tick, 历史最近距离)。</summary>
        private readonly Dictionary<int, (double NextTick, double MinDistance)> transferProbes = new();

        /// <summary>燃烧段实测（不序列化）：groupId → (上一帧是否在烧, 起始 appliedBurnDV, 起始 tick)。</summary>
        private readonly Dictionary<int, (bool WasBurning, double DvAtStart, double StartTick)> burnWatch = new();
        private bool burnTelemetryProven;


        /// <summary>
        /// **行星际粗转移入口**：扫描窗口 → 选最省 Δv 的（等待, 时长）→ 把 Δv 排进既有计划队列。
        /// </summary>
        public bool TryBeginInterplanetaryTransfer(SpaceEntityGroup group, int targetBodyId, out string reason)
        {
            reason = string.Empty;
            if (group == null) { reason = "群组不存在"; return false; }
            if (group.interplanetaryPhase != InterplanetaryPhaseNone) { reason = "已有进行中的行星际转移"; return false; }
            if (group.landed) { reason = "已着陆"; return false; }
            if (group.IsCircularOrbit) { reason = "圆轨模式下不能直接转移（需先离开圆轨）"; return false; }
            if (group.HasActiveBurn) { reason = "正在执行机动"; return false; }

            var manager = celestialRef.Get();
            if (manager == null || !manager.TryGetCelestialRuntime(out _)) { reason = "天体框架未就绪"; return false; }
            var target = manager.GetBodyById(targetBodyId);
            if (target == null || !target.HasOrbit || !(target.orbitPeriod > 0.0)) { reason = "目标天体不存在或没有可解析轨道"; return false; }
            var star = manager.GetStar();
            if (star == null) { reason = "找不到主恒星"; return false; }
            if (targetBodyId == star.bodyId) { reason = "目标是恒星本身"; return false; }
            if (group.CelestialBodyId != star.bodyId) { reason = "需要先离开当前天体的影响范围（当前不在恒星帧）"; return false; }

            var source = GetGroupPropulsionSource(group);
            if (source == null || !(source.EffectiveVe > 0.0)) { reason = "没有可用推进"; return false; }

            double mu = star.gravitationalParameter;
            double now = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 r1 = group.CurrentOrbit.positionSystem.v;
            DVector3 v1 = group.CurrentOrbit.velocitySystem.v;
            if (!(r1.magnitude > 0.0) || !r1.IsFinite() || !v1.IsFinite()) { reason = "当前状态无效"; return false; }

            DVector3 targetNow = manager.GetBodyPositionParent(targetBodyId, now);
            double targetDistance = targetNow.magnitude;
            if (!(targetDistance > 0.0)) { reason = "目标位置无效"; return false; }

            double hohmann = InterplanetaryTransferCore.TransferTimeOfFlight(r1.magnitude, targetDistance, mu);
            if (!(hohmann > 0.0)) { reason = "转移时长无效"; return false; }
            double window = target.orbitPeriod > 0.0 ? target.orbitPeriod : hohmann * 2.0;

            var probe = manager.ForecastBody(targetBodyId, now + hohmann);
            if (!probe.Success) { reason = "目标位置预报不可用"; return false; }

            if (!InterplanetaryTransferCore.TryPlanDepartureWithScan(mu, r1, v1, hohmann, window,
                    offset =>
                    {
                        var forecast = manager.ForecastBody(targetBodyId, now + offset);
                        if (!forecast.Success) throw new InvalidOperationException("ForecastUnavailable");
                        return forecast.Value.PositionRelativeToParent;
                    },
                    out DVector3 dv, out double delaySeconds, out double timeOfFlight, out string planReason))
            { reason = "转移无解：" + planReason; return false; }
            if (!(dv.magnitude > 0.0) || !dv.IsFinite()) { reason = "转移 Δv 无效"; return false; }

            group.interplanetaryTargetBodyId = targetBodyId;
            group.interplanetaryPhase = InterplanetaryPhaseOutbound;
            group.interplanetaryCaptureRetried = false;

            OrbitMath.DeltaVSystemToComponents(r1, v1, dv, out double radial, out double tangential, out double normal);
            var estimate = BurnCalculator.Estimate((float)dv.magnitude, (float)GetGroupMass(group, null), source, source.Networks, null);
            double burnSeconds = estimate.BurnSeconds > 0.0 ? estimate.BurnSeconds : 0.0;
            int executeTick = (int)Math.Round(now + Math.Max(0.0, delaySeconds) + burnSeconds / 2.0);
            group.plannedManeuvers.Add(new SpaceEntityGroup.PlannedManeuver
            {
                active = true, executeTick = executeTick, owner = PlanOwner.Navigation, ladderEpoch = 0,
                radialDV = (float)radial, tangentialDV = (float)tangential, normalDV = (float)normal,
                plannedDvBody = dv, burnSecondsPlanned = burnSeconds,
            });

            Log.Message($"[粗转移] G{group.GroupId} → {target.label}：Δv={dv.magnitude:F0} m/s｜等待 "
                + $"{delaySeconds / 86400.0:F1} d｜转移 {timeOfFlight / 86400.0:F1} d｜中点 tick={executeTick}"
                + $"（燃烧 {burnSeconds:F0} s）");
            return true;
        }

        /// <summary>
        /// **火炬转移**入口（B 方案，2026-11-19 授权）：`TorchExecutionCore.TryPlan` 一次规划，
        /// 由 `AdvanceTorchPlan` 事件驱动点两次火（出航 τ / 捕获 τ），**不排队列节点**。
        ///
        /// A 方案（2026-11-18 的队列版：Lambert 长烧 + 两个节点）已于 2026-11-20 弃置，
        /// 原文留档在 `Source/已弃置/TorchQueueTransfer-A方案-20261120.txt`（留盘不编译）。
        /// </summary>
        /// <param name="timeBias">A 方案的等待滑条参数；B 方案不用，保留签名兼容 UI。</param>
        public bool TryBeginTorchTransfer(SpaceEntityGroup group, int targetBodyId, double accelerationInG,
            out string reason, double timeBias = 0.0, double forcedTimeOfFlightSeconds = 0.0)
        {
            // ── B 方案（2026-11-19，授权件追加段）：纯核规划 + 两个 BeginBurn 事件 ──
            return TryBeginTorchTransferEventDriven(group, targetBodyId, accelerationInG, forcedTimeOfFlightSeconds, out reason);
        }

        /// <summary>
        /// **进 SOI 的捕获入口**（`CheckGroupSoi` 的「进」分支、`SwitchGroupFrame` 之后调用）。
        /// 已界 ⇒ 直接记 Captured 不点火；否则算近心点椭圆所需 Δv 并按 3% 余量偏过烧。
        /// 燃料不足/解算失败 ⇒ Flyby + 红字，**绝不静默**。
        /// </summary>
        internal void TryArmInterplanetaryCapture(SpaceEntityGroup group, int bodyId, CelestialBodyManager manager, double physicsTick)
        {
            if (group.interplanetaryPhase != InterplanetaryPhaseOutbound) return;
            if (group.interplanetaryTargetBodyId != bodyId) return;

            var body = manager.GetBodyById(bodyId);
            if (body == null || !(body.gravitationalParameter > 0.0))
            {
                group.interplanetaryPhase = InterplanetaryPhaseFlyby;
                Log.Error("[粗转移] G" + group.GroupId + " 捕获失败：目标天体参数缺失 ⇒ 将飞掠。");
                return;
            }

            double mu = body.gravitationalParameter;
            DVector3 relativePosition = group.GetBodyPosition(physicsTick).v;
            DVector3 relativeVelocity = group.GetBodyVelocity(physicsTick).v;
            double r = relativePosition.magnitude;
            double v = relativeVelocity.magnitude;
            if (!(r > 0.0) || !relativeVelocity.IsFinite())
            {
                group.interplanetaryPhase = InterplanetaryPhaseFlyby;
                Log.Error("[粗转移] G" + group.GroupId + " 捕获失败：进 SOI 时状态无效 ⇒ 将飞掠。");
                return;
            }

            if (InterplanetaryTransferCore.IsBound(mu, r, v))
            {
                group.interplanetaryPhase = InterplanetaryPhaseCaptured;
                Log.Message($"[粗转移] G{group.GroupId} 进入 {body.label} SOI 时已是有界轨道"
                    + $"（v={v:F1} < 逃逸 {InterplanetaryTransferCore.EscapeSpeed(mu, r):F1} m/s）⇒ 直接捕获，不点火。");
                return;
            }

            double periapsis = body.radius + Math.Max(200_000.0, body.radius * 0.05);
            if (!InterplanetaryTransferCore.TryCaptureDv(mu, r, v, periapsis,
                InterplanetaryTransferCore.DefaultCaptureMargin, out double dv, out string why))
            {
                group.interplanetaryPhase = InterplanetaryPhaseFlyby;
                Log.Error("[粗转移] G" + group.GroupId + " 捕获解算失败（" + why + "）⇒ 将飞掠。");
                return;
            }
            if (!(dv > 0.0))
            {
                group.interplanetaryPhase = InterplanetaryPhaseCaptured;
                return;
            }

            if (!HasUsablePropellant(group))
            {
                group.interplanetaryPhase = InterplanetaryPhaseFlyby;
                Log.Error($"[粗转移] G{group.GroupId} 捕获失败：燃料不足（需要 Δv≈{dv:F0} m/s）⇒ 将飞掠。");
                return;
            }

            var plan = new ManeuverPlan { dv = Retrograde(relativeVelocity, dv), networks = ManeuverNetworkMode.All };
            if (!group.BeginBurn(in plan))
            {
                group.interplanetaryPhase = InterplanetaryPhaseFlyby;
                Log.Error($"[粗转移] G{group.GroupId} 捕获点火被拒 ⇒ 将飞掠。");
                return;
            }

            group.interplanetaryPhase = InterplanetaryPhaseCapture;
            Log.Message($"[粗转移] G{group.GroupId} 捕获点火：Δv={dv:F0} m/s｜r={r / 1000.0:F0} km"
                + $"｜v={v:F0} m/s（逃逸 {InterplanetaryTransferCore.EscapeSpeed(mu, r):F0}）");
        }

        /// <summary>
        /// 把"期望点火时刻"换成"计划节点时刻"（2026-11-18）。
        ///
        /// 执行器的两条时间规则：① 只在 `executeTick − lookahead` 之后处理本机动
        /// （`BurnExecutorPolicy.InPreBurnWindow`）；② 居中点火 = `executeTick − 燃时/2`。
        /// 实际点火 = 两者的较晚者 ⇒ 要让点火落在 `ignitionTick`，节点必须排在
        /// `ignitionTick + min(燃时/2, lookahead)`。短烧退化回经典居中排法；长烧（火炬）退化成
        /// "点火 + 窗口宽度"（窗口一开立即点火）。
        /// </summary>
        private static double NodeTickForIgnition(double ignitionTick, double burnSeconds, double lookaheadSeconds)
            => ignitionTick + Math.Min(Math.Max(burnSeconds, 0.0) * 0.5, Math.Max(lookaheadSeconds, 0.0));

        /// <summary>
        /// **飞行追踪探针**（2026-11-18，用户提议）：对处于转移中的组，每 3000 物理秒打一行
        /// 「船位置 / 目标位置 / 距离 / 最近距离 / 船速 / 是否在烧 / 质量 / 目标 SOI」。
        ///
        /// 目的：分辨"没飞 / 飞偏 / 飞对了但没进 SOI"三种情况，不再靠猜。
        /// 只读；状态放在本组件的瞬时字典里（不序列化、不进存档）。
        /// </summary>
        /// <summary>
        /// **燃烧段实测**（2026-11-18）：记录每段燃烧"命令 Δv vs 实交 Δv / 实际燃时"，
        /// 用来判断推力没交够是燃时被掐断还是估值口径不符。只在既有每步消费者里做一次状态比较。
        /// </summary>
        private void WatchBurnCompletion(SpaceEntityGroup group, double physicsTick)
        {
            bool burning = group.HasActiveBurn;
            burnWatch.TryGetValue(group.GroupId, out var watch);
            if (!burnTelemetryProven)
            {
                burnTelemetryProven = true;
                Log.Message("[火炬·遥测] 已挂载：仅对 phase != None 的组生效（无转移时零开销）");
            }

            if (burning && !watch.WasBurning)
            {
                burnWatch[group.GroupId] = (true, group.appliedBurnDV, physicsTick);
                Log.Message($"[火炬·实测] G{group.GroupId} 点火：appliedBurnDV={group.appliedBurnDV:F0} m/s｜t={physicsTick:F0}");
                return;
            }
            if (!burning && watch.WasBurning)
            {
                double applied = group.appliedBurnDV - watch.DvAtStart;
                Log.Message($"[火炬·实测] G{group.GroupId} 燃烧段结束：实交 Δv={applied:F0} m/s"
                    + $"｜实际燃时 {(physicsTick - watch.StartTick) / 3600.0:F2} h");
                burnWatch[group.GroupId] = (false, group.appliedBurnDV, physicsTick);
            }
        }
        private void ProbeInterplanetaryFlight(SpaceEntityGroup group, CelestialBodyManager manager, double physicsTick)
        {
            if (group.interplanetaryTargetBodyId < 0) return;
            var target = manager.GetBodyById(group.interplanetaryTargetBodyId);
            if (target == null) return;

            DVector3 ship = group.CurrentOrbit.positionSystem.v;
            DVector3 targetPosition = manager.GetBodyPositionParent(group.interplanetaryTargetBodyId, physicsTick);
            DVector3 gap = targetPosition - ship;
            double distance = gap.magnitude;
            group.torchTargetDistance = distance;
            double speed = group.CurrentOrbit.velocitySystem.v.magnitude;

            transferProbes.TryGetValue(group.GroupId, out var state);
            if (state.NextTick == 0.0) state = (0.0, double.MaxValue);
            if (distance < state.MinDistance) state.MinDistance = distance;

            if (physicsTick >= state.NextTick)
            {
                state.NextTick = physicsTick + 3000.0;
                double soi = OrbitAdapter.SoiRadiusApprox(target, manager);
                Log.Message($"[火炬·追踪] G{group.GroupId} t={physicsTick:F0}｜距离 {distance / 1e9:F3}e9 m"
                    + $"（最近 {state.MinDistance / 1e9:F3}e9）｜船速 {speed / 1000.0:F2} km/s"
                    + $"｜烧={group.HasActiveBurn}｜m={GetGroupMass(group, null):F0} kg"
                    + $"｜目标 SOI {soi / 1e9:F4}e9 m｜距离/ SOI = {distance / Math.Max(1.0, soi):F2}");
            }
            transferProbes[group.GroupId] = state;
        }

        /// <summary>
        /// **行星际导航结束**（2026-11-20）：记结果 → 释放阶段锁。
        ///
        /// 释放后 `ProbeInterplanetaryFlight` / `VerifyInterplanetaryTransfers` / `AdvanceTorchPlan`
        /// 都不再处理该组（它们以 `interplanetaryPhase != None` 为门）。
        /// 这一条同时修掉"转移一次后永远不能再发起弹道转移"的泄漏：在此之前
        /// `interplanetaryPhase` 只有写 1/2/3/4 的地方，**没有任何一处写回 `None`**。
        ///
        /// 不改任何判定：只在既有终态之外补一个收尾动作。
        /// </summary>
        private void CompleteInterplanetaryTransfer(SpaceEntityGroup group, int outcome, string reason, bool clearTorch)
        {
            group.interplanetaryEndOutcome = outcome;
            group.interplanetaryEndReason = reason;
            group.interplanetaryPhase = InterplanetaryPhaseNone;
            group.interplanetaryTargetBodyId = -1;
            group.interplanetaryCaptureRetried = false;

            if (clearTorch)
            {
                group.torchPlan = default;
                group.torchPhase = 0;
                group.torchStartTick = 0.0;
                group.torchSoi = 0.0;
                group.torchTargetBodyId = 0;
                group.torchTargetDistance = 0.0;
            }

            group.abFreezeDirection = false;
            burnWatch.Remove(group.GroupId);
            transferProbes.Remove(group.GroupId);
            trackedManeuvers.Remove(group.GroupId);
        }

        /// <summary>
        /// **取消进行中的行星际导航**（2026-11-20，用户授权）：适用于阶段 1（出航段）/ 2（捕获段）。
        ///
        /// 语义：
        /// · 燃烧中 ⇒ 走 `EmergencyStopBurn`（已施加 Δv 保留、剩余丢弃 —— 既有语义，不新造）；
        /// · 只删**本导航自己的**队列节点（`PlanOwner.Navigation`），玩家手排节点不动；
        /// · 清火炬计划、清圆化挂起、清探测器/遥测条目、复位冻结方向；
        /// · 最后把 `interplanetaryPhase` 归零并记「取消」结果。
        /// 阶段 3/4 属于既成终态（捕获已成功），返回 false + 理由。
        /// </summary>
        public bool TryCancelInterplanetaryTransfer(SpaceEntityGroup group, out string reason)
        {
            reason = "";
            if (group == null) { reason = "群组不存在"; return false; }

            int phase = group.interplanetaryPhase;
            if (phase == InterplanetaryPhaseNone) { reason = "没有进行中的行星际导航"; return false; }
            if (phase == InterplanetaryPhaseCaptured || phase == InterplanetaryPhaseFlyby)
            {
                reason = "导航已结束（" + (phase == InterplanetaryPhaseCaptured ? "已捕获" : "飞掠") + "），无需取消";
                return false;
            }

            string phaseText = phase == InterplanetaryPhaseOutbound ? "出航段" : "捕获段";
            bool wasBurning = group.HasActiveBurn;
            if (wasBurning) EmergencyStopBurn(group);

            int removed = group.RemoveNavigationManeuvers();

            if (group.HasPendingCircularize)
            {
                group.HasPendingCircularize = false;
                group.pendingCircularizeBodyId = -1;
            }

            CompleteInterplanetaryTransfer(group, InterplanetaryOutcomeCancelled,
                "玩家取消（" + phaseText + (wasBurning ? "，燃烧中" : "") + "）", clearTorch: true);

            PhysicsLogMessage($"[粗转移] G{group.GroupId} 导航已取消（{phaseText}）｜清除队列节点 {removed} 条｜燃烧中={wasBurning}");
            Messages.Message($"Group {group.GroupId} 行星际导航已取消（{phaseText}）。", MessageTypeDefOf.NeutralEvent, false);
            return true;
        }

        /// <summary>
        /// **烧后复核**（每个物理步末调用，只扫处于 `Capture` 且已停烧的组）：
        /// 仍有界 ⇒ Captured；未界且没补过 ⇒ 用当时状态补烧一次；否则 ⇒ Flyby + 红字。
        /// </summary>
        internal void VerifyInterplanetaryTransfers(double physicsTick)
        {
            var manager = celestialRef.Get();
            if (manager == null) return;

            foreach (var group in groups.Values)
            {
                // 2026-11-20：终态收口 —— Captured/Flyby 记结果并释放阶段锁。
                // 释放后 Probe/Verify 不再处理该组；"转移一次后永远不能再转移"的泄漏随之消失。
                if (group.interplanetaryPhase == InterplanetaryPhaseCaptured
                    || group.interplanetaryPhase == InterplanetaryPhaseFlyby)
                {
                    bool captured = group.interplanetaryPhase == InterplanetaryPhaseCaptured;
                    CompleteInterplanetaryTransfer(group, captured
                            ? InterplanetaryOutcomeCompleted
                            : InterplanetaryOutcomeCompletedWithWarning,
                        captured ? "已捕获（束缚轨道）" : "飞掠（未捕获，见日志）",
                        clearTorch: false);
                    continue;
                }

                if (group.interplanetaryPhase != InterplanetaryPhaseNone)
                {
                    ProbeInterplanetaryFlight(group, manager, physicsTick);
                    WatchBurnCompletion(group, physicsTick);
                    AdvanceTorchPlan(group, physicsTick);
                }

                if (group.interplanetaryPhase != InterplanetaryPhaseCapture) continue;
                if (group.HasActiveBurn) continue;

                var body = manager.GetBodyById(group.interplanetaryTargetBodyId);
                if (body == null || !(body.gravitationalParameter > 0.0))
                {
                    group.interplanetaryPhase = InterplanetaryPhaseFlyby;
                    continue;
                }

                double mu = body.gravitationalParameter;
                DVector3 relativePosition = group.GetBodyPosition(physicsTick).v;
                DVector3 relativeVelocity = group.GetBodyVelocity(physicsTick).v;
                double r = relativePosition.magnitude;
                double v = relativeVelocity.magnitude;

                if (InterplanetaryTransferCore.IsBound(mu, r, v))
                {
                    group.interplanetaryPhase = InterplanetaryPhaseCaptured;
                    Log.Message($"[粗转移] G{group.GroupId} 捕获成功：v={v:F1} < 逃逸 "
                        + $"{InterplanetaryTransferCore.EscapeSpeed(mu, r):F1} m/s。");
                    continue;
                }

                if (group.interplanetaryCaptureRetried)
                {
                    group.interplanetaryPhase = InterplanetaryPhaseFlyby;
                    Log.Error($"[粗转移] G{group.GroupId} 补烧后仍未捕获（v={v:F0}）⇒ 飞掠。");
                    continue;
                }

                double periapsis = body.radius + Math.Max(200_000.0, body.radius * 0.05);
                if (!InterplanetaryTransferCore.TryCaptureDv(mu, r, v, periapsis,
                        InterplanetaryTransferCore.DefaultCaptureMargin, out double dv, out _) || !(dv > 0.0))
                {
                    group.interplanetaryPhase = InterplanetaryPhaseFlyby;
                    continue;
                }
                if (!HasUsablePropellant(group))
                {
                    group.interplanetaryPhase = InterplanetaryPhaseFlyby;
                    Log.Error($"[粗转移] G{group.GroupId} 捕获欠烧且燃料不足（还需 Δv≈{dv:F0} m/s）⇒ 飞掠。");
                    continue;
                }

                group.interplanetaryCaptureRetried = true;
                var plan = new ManeuverPlan { dv = Retrograde(relativeVelocity, dv), networks = ManeuverNetworkMode.All };
                if (group.BeginBurn(in plan))
                {
                    Log.Message($"[粗转移] G{group.GroupId} 捕获欠烧 ⇒ 补烧一次：Δv={dv:F0} m/s。");
                }
                else
                {
                    group.interplanetaryPhase = InterplanetaryPhaseFlyby;
                }
            }
        }

        private bool HasUsablePropellant(SpaceEntityGroup group)
        {
            var source = GetGroupPropulsionSource(group);
            return source != null && source.EffectiveVe > 0.0 && source.TotalPropellantMass > 0.0;
        }

        /// <summary>沿相对速度反向（方向在点火时冻结，燃烧期间不更新）。</summary>
        private static DVector3 Retrograde(DVector3 relativeVelocity, double magnitude)
        {
            double speed = relativeVelocity.magnitude;
            if (!(speed > 1e-9)) return DVector3.zero;
            return relativeVelocity * (-magnitude / speed);
        }
        /// <summary>B 方案入口（2026-11-19）：纯核规划 → 存两个事件。</summary>
        private bool TryBeginTorchTransferEventDriven(SpaceEntityGroup group, int targetBodyId, double accelerationInG,
            double forcedTimeOfFlightSeconds, out string reason)
        {
            reason = "";
            // 2026-11-20：补上"已有进行中的转移"前置（此前只查燃烧 ⇒ 飞行中再点一次会静默覆盖 torchPlan）
            if (group.interplanetaryPhase != InterplanetaryPhaseNone) { reason = "已有进行中的行星际转移"; return false; }
            if (group.HasActiveBurn) { reason = "正在执行机动"; return false; }
            var manager = celestialRef.Get();
            if (manager == null || !manager.TryGetCelestialRuntime(out _)) { reason = "天体框架未就绪"; return false; }
            var target = manager.GetBodyById(targetBodyId);
            if (target == null || !target.HasOrbit) { reason = "目标天体不存在"; return false; }
            var star = manager.GetStar();
            if (star == null) { reason = "找不到主恒星"; return false; }
            if (targetBodyId == star.bodyId) { reason = "目标是恒星本身"; return false; }
            if (group.CelestialBodyId != star.bodyId) { reason = "需要先离开当前天体"; return false; }
            var source = GetGroupPropulsionSource(group);
            if (source == null || !(source.EffectiveVe > 0.0)) { reason = "没有可用推进"; return false; }

            double acceleration = accelerationInG * TorchTransferCore.StandardGravity;
            double mu = star.gravitationalParameter;
            double now = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 r1 = group.CurrentOrbit.positionSystem.v;
            DVector3 v1 = group.CurrentOrbit.velocitySystem.v;
            if (!(r1.magnitude > 0.0) || !r1.IsFinite() || !v1.IsFinite()) { reason = "当前状态无效"; return false; }

            var probe = manager.ForecastBody(targetBodyId, now);
            if (!probe.Success) { reason = "目标位置不可用"; return false; }
            double hohmann = InterplanetaryTransferCore.TransferTimeOfFlight(r1.magnitude,
                probe.Value.PositionRelativeToParent.magnitude, mu);
            if (!(hohmann > 0.0)) { reason = "转移时长无效"; return false; }
            double tof = forcedTimeOfFlightSeconds > 0.0 ? forcedTimeOfFlightSeconds : hohmann;

            double soi = OrbitAdapter.SoiRadiusApprox(target, manager);
            if (!(soi > 0.0)) { reason = "目标 SOI 无效"; return false; }
            double targetMu = target.gravitationalParameter;
            double baseTick = now;

            bool ok = TorchExecutionCore.TryPlan(r1, v1,
                t =>
                {
                    var forecast = manager.ForecastBody(targetBodyId, baseTick + t);
                    return forecast.Success
                        ? (forecast.Value.PositionRelativeToParent, forecast.Value.VelocityRelativeToParent)
                        : (DVector3.zero, DVector3.zero);
                },
                mu, targetMu, soi, acceleration, tof, 0.5 * soi,
                out TorchExecutionPlan plan, out string planReason);
            if (!ok) { reason = "规划失败：" + planReason; return false; }

            group.torchPlan = plan;
            group.torchPhase = 1;
            group.torchStartTick = now;
            group.torchTargetBodyId = targetBodyId;
            group.torchSoi = soi;
            group.interplanetaryPhase = InterplanetaryPhaseOutbound;
            Log.Message($"[火炬·B] 出航 Δv={plan.DepartureDv.magnitude:F0} m/s（{plan.DepartureSeconds / 3600.0:F2} h）"
                + $"｜到达 Δv={plan.CaptureDv.magnitude:F0} m/s（{plan.CaptureSeconds / 3600.0:F2} h，"
                + $"t={plan.CaptureStartSeconds / 3600.0:F2} h 点火）"
                + $"｜转移 {tof / 86400.0:F2} d｜SOI {soi / 1e9:F4}e9 m｜捕获点 {plan.EntrySeconds / 3600.0:F2} h");
            return true;
        }

        /// <summary>到点执行两段燃烧（事件驱动，非每步规划）。</summary>
        private void AdvanceTorchPlan(SpaceEntityGroup group, double physicsTick)
        {
            if (group.torchPhase <= 0 || group.torchPhase >= 3) return;

            // 捕获烧结束 ⇒ 自动圆化（失败不影响"已捕获"这个既成事实）
            if (group.torchPhase == 2 && !group.HasActiveBurn)
            {
                double elapsed2 = physicsTick - group.torchStartTick;
                if (elapsed2 >= group.torchPlan.CaptureStartSeconds + group.torchPlan.CaptureSeconds)
                {
                    // 2026-11-20：捕获段结束 = 本导航**真实可观测的终点**。
                    // ⚠ 阶段 3/4 的"自动圆化"分支当前不可达（守卫 `torchPhase >= 3` 先返回，见登记）；
                    //    等那个缺陷修好后，完成点要挪到阶段 4（开始圆化），而不是这里。
                    bool bound = false;
                    var mgr2 = celestialRef.Get();
                    if (mgr2 != null && group.torchTargetBodyId != 0 && group.CelestialBodyId == group.torchTargetBodyId)
                    {
                        var tb = mgr2.GetBodyById(group.torchTargetBodyId);
                        if (tb != null && tb.gravitationalParameter > 0.0)
                        {
                            DVector3 rp = group.GetBodyPosition(physicsTick).v;
                            DVector3 rv = group.GetBodyVelocity(physicsTick).v;
                            bound = InterplanetaryTransferCore.IsBound(tb.gravitationalParameter, rp.magnitude, rv.magnitude);
                        }
                    }

                    Log.Message($"[火炬·完成] 捕获段结束：{(bound ? "束缚" : "未束缚（将飞掠）")}"
                        + $"｜当前帧 {group.CelestialBodyId}｜目标体 {group.torchTargetBodyId}｜SOI {group.torchSoi / 1e9:F4}e9 m");
                    CompleteInterplanetaryTransfer(group, bound
                            ? InterplanetaryOutcomeCompleted
                            : InterplanetaryOutcomeCompletedWithWarning,
                        bound ? "火炬：已捕获（束缚轨道）" : "火炬：捕获段结束但轨道未束缚",
                        clearTorch: true);
                    return;
                }
            }

            // 阶段 3：等进到 0.60×SOI（SOI 深处）再圆化，避免圆轨贴着边界
            if (group.torchPhase == 3 && !group.HasActiveBurn
                && group.torchTargetBodyId != 0 && group.CelestialBodyId == group.torchTargetBodyId
                && group.torchSoi > 0.0)
            {
                double radiusNow = group.CurrentOrbit.positionSystem.v.magnitude;
                double elapsedRound = physicsTick - group.torchStartTick - group.torchPlan.CaptureStartSeconds - group.torchPlan.CaptureSeconds;
                if (radiusNow <= 0.70 * group.torchSoi)
                {
                    group.torchPhase = 4;
                    Log.Message($"[火炬·圆化] r={radiusNow / 1e9:F4}e9 m ≤ 0.70×SOI，开始圆化"
                        + $" → {BeginCircularize(group, group.torchTargetBodyId)}");
                }
                // 兜底：等了 3 天还没降到 0.60×SOI（近心点估计偏大）⇒ 在 ≤0.85×SOI 时直接圆化
                if (elapsedRound > 3.0 * 86400.0 && radiusNow <= 0.88 * group.torchSoi)
                {
                    group.torchPhase = 4;
                    Log.Warning($"[火炬·圆化] 超时兜底：r={radiusNow / 1e9:F4}e9 m，直接圆化"
                        + $" → {BeginCircularize(group, group.torchTargetBodyId)}");
                }
                return;
            }

            if (group.HasActiveBurn) return;
            TorchExecutionPlan plan = group.torchPlan;
            double elapsed = physicsTick - group.torchStartTick;
            double startAt = group.torchPhase == 1 ? plan.DepartureStartSeconds : plan.CaptureStartSeconds;
            if (elapsed + 1e-6 < startAt) return;

            DVector3 dv = group.torchPhase == 1 ? plan.DepartureDv : plan.CaptureDv;
            double seconds = group.torchPhase == 1 ? plan.DepartureSeconds : plan.CaptureSeconds;
            if (!(dv.magnitude > 0.5) || !(seconds > 0.0)) { group.torchPhase = 3; return; }

            var burn = new ManeuverPlan
            {
                dv = dv,
                nodeTick = (int)physicsTick,
                burnSeconds = seconds,
                commandAccel = 0.0,
                hasSolution = false,
            };
            if (!group.BeginBurn(in burn)) { Log.Warning("[火炬·B] 点火被拒绝"); return; }
            group.abFreezeDirection = true;
            Log.Message($"[火炬·实测] 阶段{group.torchPhase} 点火：命令 Δv={dv.magnitude:F0} m/s"
                + $"｜燃时 {seconds / 3600.0:F2} h｜t={physicsTick:F0}");
            group.torchPhase++;
        }
    }
}
