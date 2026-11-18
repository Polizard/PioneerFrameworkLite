using System;
using System.Collections.Generic;
using PioneerFramework.Celestial;
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
                active = true, executeTick = executeTick, owner = PlanOwner.Manual, ladderEpoch = 0,
                radialDV = (float)radial, tangentialDV = (float)tangential, normalDV = (float)normal,
                plannedDvBody = dv, burnSecondsPlanned = burnSeconds,
            });

            Log.Message($"[粗转移] G{group.GroupId} → {target.label}：Δv={dv.magnitude:F0} m/s｜等待 "
                + $"{delaySeconds / 86400.0:F1} d｜转移 {timeOfFlight / 86400.0:F1} d｜中点 tick={executeTick}"
                + $"（燃烧 {burnSeconds:F0} s）");
            return true;
        }

        /// <summary>
        /// **火炬转移**（2026-11-18 重构：圆锥 + 长烧 + 到达减速）。
        ///
        /// 出航：Lambert 解出的 Δv 摊成长烧（燃时 = |Δv|/a，方向 = Δv）。
        /// 到达：按 Lambert 的**到达状态**算出需要的 Δv（相对目标），在到达前 burnSeconds 排一次反推。
        /// 为什么不能只靠 SOI 捕获：260 km/s 穿 SOI 只要 500 s，而 1 g 消掉它要 7.4 h。
        /// </summary>
        public bool TryBeginTorchTransfer(SpaceEntityGroup group, int targetBodyId, double accelerationInG,
            out string reason, double timeBias = 0.0, double forcedTimeOfFlightSeconds = 0.0)
        {
            // ── B 方案（2026-11-19，授权件追加段）：纯核规划 + 两个 BeginBurn 事件 ──
            if (TryBeginTorchTransferEventDriven(group, targetBodyId, accelerationInG, forcedTimeOfFlightSeconds, out reason))
                return true;
            return false;

            // 以下为旧队列实现（不可达，保留作回退参照）
            reason = string.Empty;
            if (group == null) { reason = "群组不存在"; return false; }
            if (group.interplanetaryPhase != InterplanetaryPhaseNone) { reason = "已有进行中的转移"; return false; }
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

            double acceleration = accelerationInG * TorchTransferCore.StandardGravity;
            if (!(acceleration > 0.0)) { reason = "加速度无效"; return false; }

            double mu = star.gravitationalParameter;
            double now = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 r1 = group.CurrentOrbit.positionSystem.v;
            DVector3 v1 = group.CurrentOrbit.velocitySystem.v;
            if (!(r1.magnitude > 0.0) || !r1.IsFinite() || !v1.IsFinite()) { reason = "当前状态无效"; return false; }

            var probe = manager.ForecastBody(targetBodyId, now);
            if (!probe.Success) { reason = "目标位置不可用"; return false; }
            double hohmann = InterplanetaryTransferCore.TransferTimeOfFlight(r1.magnitude, probe.Value.PositionRelativeToParent.magnitude, mu);
            if (!(hohmann > 0.0)) { reason = "转移时长无效"; return false; }
            double window = target.orbitPeriod > 0.0 ? target.orbitPeriod : hohmann * 2.0;

            double dryMass = GetGroupMass(group, null);
            double propellantMass = source.TotalPropellantMass;
            double massFlow = 0.0;
            foreach (var network in source.Networks) massFlow += network.MassFlow;
            double deltaVBudget = dryMass > 0.0 && propellantMass > 0.0
                ? source.EffectiveVe * Math.Log((dryMass + propellantMass) / dryMass) * 0.9
                : double.PositiveInfinity;

            DVector3 dv;
            double delaySeconds, timeOfFlight;
            if (forcedTimeOfFlightSeconds > 0.0)
            {
                timeOfFlight = forcedTimeOfFlightSeconds;
                delaySeconds = 0.0;
                var forcedTarget = manager.ForecastBody(targetBodyId, now + timeOfFlight);
                if (!forcedTarget.Success) { reason = "目标位置预报不可用"; return false; }
                if (!InterplanetaryTransferCore.TryPlanDeparture(mu, r1, v1,
                        forcedTarget.Value.PositionRelativeToParent, timeOfFlight, out dv, out string forcedReason))
                { reason = "指定时长无解：" + forcedReason; return false; }
            }
            else
            {
                if (!InterplanetaryTransferCore.TryPlanDepartureWithScan(mu, r1, v1, hohmann, window,
                        offset =>
                        {
                            var forecast = manager.ForecastBody(targetBodyId, now + offset);
                            if (!forecast.Success) throw new InvalidOperationException("ForecastUnavailable");
                            return forecast.Value.PositionRelativeToParent;
                        },
                        out dv, out delaySeconds, out timeOfFlight, out string planReason,
                        timeBias: timeBias, maxDeltaV: deltaVBudget))
                { reason = "火炬转移无解：" + planReason; return false; }
            }
            if (!(dv.magnitude > 0.0) || !dv.IsFinite()) { reason = "转移 Δv 无效"; return false; }

            // ★ **有限燃烧的中点修正**（2026-11-18 实测：实交 Δv 与命令一分不差，但落点晚 ~1.9 h）：
            //   Lambert 把 Δv 当冲量，真实却烧了 τ=|Δv|/a 秒，等效冲量在 τ/2 处。
            //   所以从"燃烧中点状态"重解 Lambert，迭代到 Δv 稳定。规划期一次，飞行期零决策。
            for (int correction = 0; correction < 3; correction++)
            {
                double tauGuess = dv.magnitude / acceleration;
                if (!(tauGuess > 0.0) || tauGuess * 0.5 >= timeOfFlight) break;
                DVector3 midPosition, midVelocity;
                try
                {
                    midPosition = OrbitMath.GetPositionAtTimeDelta(r1, v1, tauGuess * 0.5, mu);
                    midVelocity = OrbitMath.GetVelocityAtTimeDelta(r1, v1, tauGuess * 0.5, mu);
                }
                catch { break; }
                var arrivalTargetProbe = manager.ForecastBody(targetBodyId, now + timeOfFlight);
                if (!arrivalTargetProbe.Success) break;
                if (!InterplanetaryTransferCore.TryPlanDeparture(mu, midPosition, midVelocity,
                        arrivalTargetProbe.Value.PositionRelativeToParent, timeOfFlight - tauGuess * 0.5,
                        out DVector3 corrected, out _)) break;
                if (!(corrected.magnitude > 0.0) || !corrected.IsFinite()) break;
                double change = (corrected - dv).magnitude;
                dv = corrected;
                if (change < 1.0) break;
            }
            var departureEstimate = BurnCalculator.Estimate((float)dv.magnitude,
                (float)GetGroupMass(group, null), source, source.Networks, null);
            double burnSeconds = departureEstimate.Feasible && departureEstimate.BurnSeconds > 0.0
                ? departureEstimate.BurnSeconds
                : dv.magnitude / acceleration;
            double propellantNeeded = massFlow * burnSeconds;
            if (!(burnSeconds > 0.0) || !double.IsFinite(burnSeconds)) { reason = "燃时无效"; return false; }
            if (propellantNeeded > propellantMass)
            {
                reason = $"工质不足：Δv={dv.magnitude:F0} m/s 在 {accelerationInG:F2} g 下需 {propellantNeeded:F0} kg，只有 {propellantMass:F0} kg";
                return false;
            }

            DVector3 arrivalDv = DVector3.zero;
            double arrivalBurnSeconds = 0.0;
            double arrivalPropellant = 0.0;
            double arrivalEntrySeconds = timeOfFlight;
            try
            {
                // ★ **主动进 SOI**（2026-11-18 修正）：Lambert 的到达时刻把船放在**目标点上**
                //   （冲量模型），所以"从 tof 向前找 SOI 边界"永远找不到（已经在里面，r≈0）。
                //   正确做法：用**渐近线**定入口 —— 入口相对位置 = −v̂_rel × 0.98×SOI，
                //   入口时刻 = tof − 0.98×SOI / |v_rel|。
                double tauNow = dv.magnitude / acceleration;
                DVector3 midPos = OrbitMath.GetPositionAtTimeDelta(r1, v1, tauNow * 0.5, mu);
                DVector3 midVel = OrbitMath.GetVelocityAtTimeDelta(r1, v1, tauNow * 0.5, mu);
                double coast = Math.Max(1.0, timeOfFlight - tauNow * 0.5);
                DVector3 arrivalPosition = OrbitMath.GetPositionAtTimeDelta(midPos, midVel + dv, coast, mu);
                DVector3 arrivalVelocity = OrbitMath.GetVelocityAtTimeDelta(midPos, midVel + dv, coast, mu);
                var arrivalForecast = manager.ForecastBody(targetBodyId, now + timeOfFlight);
                if (arrivalForecast.Success && arrivalVelocity.IsFinite() && arrivalPosition.IsFinite())
                {
                    double soi = OrbitAdapter.SoiRadiusApprox(target, manager);
                    if (!(soi > 0.0)) soi = 1.0e8;
                    double muTarget = target.gravitationalParameter;
                    DVector3 relVelocity = arrivalVelocity - arrivalForecast.Value.VelocityRelativeToParent;
                    double relSpeed = relVelocity.magnitude;
                    if (muTarget > 0.0 && relSpeed > 1.0 && soi > 1.0)
                    {
                        double entryRadius = soi * 0.98;
                        DVector3 incoming = relVelocity * (1.0 / relSpeed);
                        DVector3 entryPosition = incoming * (-entryRadius);
                        double entrySeconds = timeOfFlight - entryRadius / relSpeed;
                        double periapsisGoal = 0.5 * soi;
                        if (TorchTransferCore.TryPlanCapture(muTarget, entryPosition, relVelocity, periapsisGoal,
                                out double captureDeltaV, out string captureReason) && captureDeltaV > 1.0)
                        {
                            arrivalDv = (incoming * -1.0) * captureDeltaV;
                            // ★ 与执行器估值对齐（2026-11-18）：队列自己用 BurnCalculator.Estimate 定燃时，
                            //   我们若按 1 g 写 3.2 h 而它算 16 h，点火时刻就会错。这里直接用它的估值。
                            var captureEstimate = BurnCalculator.Estimate((float)captureDeltaV,
                                (float)GetGroupMass(group, null), source, source.Networks, null);
                            arrivalBurnSeconds = captureEstimate.Feasible && captureEstimate.BurnSeconds > 0.0
                                ? captureEstimate.BurnSeconds
                                : captureDeltaV / acceleration;
                            arrivalPropellant = massFlow * arrivalBurnSeconds;
                            arrivalEntrySeconds = Math.Max(1.0, entrySeconds);
                            Log.Message($"[火炬·到达] 相对速度 {relSpeed / 1000.0:F1} km/s｜SOI {soi / 1e9:F4}e9 m"
                                + $"｜入口 {entryRadius / 1e9:F4}e9 m｜捕获 Δv={captureDeltaV:F0} m/s"
                                + $"（{arrivalBurnSeconds / 3600.0:F2} h）");
                        }
                        else
                        {
                            Log.Warning($"[火炬·到达] 捕获解算失败：{captureReason}"
                                + $"（SOI {soi / 1e9:F4}e9，相对速度 {relSpeed / 1000.0:F1} km/s）");
                        }
                    }
                }
            }
            catch (Exception error)
            {
                Log.Warning("[火炬] 到达状态解算失败：" + error.Message);
            }
            // ★ **到达段的中点修正**（2026-11-18 实测：刹车提前 1.29e9 m 停住）：
            //   冲量模型把减速放在到达瞬间，真实燃烧等效于中点 ⇒ 会停短 v_rel·τ/2。
            //   等价改法：把瞄准点往前挪同样的量，让"刹车中点"落在目标上。迭代 2 次。
            if (false && arrivalDv.magnitude > 1.0 && timeOfFlight > 0.0)   // 被"主动进 SOI"取代
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    double tauArr = arrivalDv.magnitude / acceleration;
                    if (!(tauArr > 0.0)) break;
                    var targetProbe = manager.ForecastBody(targetBodyId, now + timeOfFlight);
                    if (!targetProbe.Success) break;
                    DVector3 relativeDirection = arrivalDv * (-1.0 / arrivalDv.magnitude);   // 船相对目标的来向
                    DVector3 shiftedAim = targetProbe.Value.PositionRelativeToParent
                        + relativeDirection * (arrivalDv.magnitude * tauArr * 0.5);

                    double tauDep = dv.magnitude / acceleration;
                    DVector3 depMidPosition = OrbitMath.GetPositionAtTimeDelta(r1, v1, tauDep * 0.5, mu);
                    DVector3 depMidVelocity = OrbitMath.GetVelocityAtTimeDelta(r1, v1, tauDep * 0.5, mu);
                    if (!InterplanetaryTransferCore.TryPlanDeparture(mu, depMidPosition, depMidVelocity, shiftedAim,
                            Math.Max(1.0, timeOfFlight - tauDep * 0.5), out DVector3 shiftedDv, out _)) break;
                    if (!(shiftedDv.magnitude > 0.0) || !shiftedDv.IsFinite()) break;
                    dv = shiftedDv;

                    double tau2 = dv.magnitude / acceleration;
                    DVector3 mid2Position = OrbitMath.GetPositionAtTimeDelta(r1, v1, tau2 * 0.5, mu);
                    DVector3 mid2Velocity = OrbitMath.GetVelocityAtTimeDelta(r1, v1, tau2 * 0.5, mu);
                    double remaining = Math.Max(1.0, timeOfFlight - tau2 * 0.5);
                    DVector3 arrivalVel = OrbitMath.GetVelocityAtTimeDelta(mid2Position, mid2Velocity + dv, remaining, mu);
                    arrivalDv = targetProbe.Value.VelocityRelativeToParent - arrivalVel;
                    arrivalBurnSeconds = arrivalDv.magnitude / acceleration;
                    arrivalPropellant = massFlow * arrivalBurnSeconds;
                    burnSeconds = dv.magnitude / acceleration;
                    propellantNeeded = massFlow * burnSeconds;
                }
            }
            if (arrivalDv.magnitude > 1.0 && propellantNeeded + arrivalPropellant > propellantMass)
            {
                reason = $"工质不足：出航 {propellantNeeded:F0} kg + 到达 {arrivalPropellant:F0} kg > 罐容 {propellantMass:F0} kg";
                return false;
            }

            group.abFreezeDirection = true;
            OrbitMath.DeltaVSystemToComponents(r1, v1, dv, out double radial, out double tangential, out double normal);
            int departureTick = (int)Math.Round(now + Math.Max(0.0, delaySeconds)
                + Math.Min(burnSeconds * 0.5, ManeuverPreBurnLookaheadTicks));
            group.plannedManeuvers.Add(new SpaceEntityGroup.PlannedManeuver
            {
                active = true, executeTick = departureTick, owner = PlanOwner.Manual, ladderEpoch = 0,
                radialDV = (float)radial, tangentialDV = (float)tangential, normalDV = (float)normal,
                plannedDvBody = dv, burnSecondsPlanned = burnSeconds,
            });

            if (arrivalDv.magnitude > 1.0 && arrivalPropellant <= propellantMass)
            {
                OrbitMath.DeltaVSystemToComponents(r1, v1, arrivalDv, out double rA, out double tA, out double nA);
                double ignitionAt = Math.Max(now, now + arrivalEntrySeconds - arrivalBurnSeconds);
                int arrivalTick = (int)Math.Round(ignitionAt + Math.Min(arrivalBurnSeconds * 0.5, ManeuverPreBurnLookaheadTicks));
                group.plannedManeuvers.Add(new SpaceEntityGroup.PlannedManeuver
                {
                    active = true, executeTick = arrivalTick, owner = PlanOwner.Manual, ladderEpoch = 0,
                    radialDV = (float)rA, tangentialDV = (float)tA, normalDV = (float)nA,
                    plannedDvBody = arrivalDv, burnSecondsPlanned = arrivalBurnSeconds,
                });
            }

            group.interplanetaryTargetBodyId = targetBodyId;
            group.interplanetaryPhase = InterplanetaryPhaseOutbound;
            group.interplanetaryCaptureRetried = false;

            Log.Message($"[火炬] G{group.GroupId} → {target.label}：{accelerationInG:F2} g｜出航 Δv={dv.magnitude:F0} m/s"
                + $"（{(forcedTimeOfFlightSeconds > 0.0 ? "指定" : "滑条 " + timeBias.ToString("F2"))}）"
                + $"｜燃时 {burnSeconds / 3600.0:F2} h｜等待 {delaySeconds / 86400.0:F2} d｜转移 {timeOfFlight / 86400.0:F3} d"
                + (arrivalDv.magnitude > 1.0
                    ? $"｜到达减速 Δv={arrivalDv.magnitude:F0} m/s（{arrivalBurnSeconds / 3600.0:F2} h，到达前 {arrivalBurnSeconds / 3600.0:F2} h 点火）"
                    : "｜无需到达减速")
                + $"｜预算 Δv≤{deltaVBudget / 1000.0:F0} km/s");
            return true;
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
        /// **烧后复核**（每个物理步末调用，只扫处于 `Capture` 且已停烧的组）：
        /// 仍有界 ⇒ Captured；未界且没补过 ⇒ 用当时状态补烧一次；否则 ⇒ Flyby + 红字。
        /// </summary>
        internal void VerifyInterplanetaryTransfers(double physicsTick)
        {
            var manager = celestialRef.Get();
            if (manager == null) return;

            foreach (var group in groups.Values)
            {
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
                    group.torchPhase = 3;
                    Log.Message($"[火炬·圆化] 捕获段完成（阶段3：等待降至 0.70×SOI 再圆化）"
                        + $"｜当前帧 {group.CelestialBodyId}｜目标体 {group.torchTargetBodyId}｜SOI {group.torchSoi / 1e9:F4}e9 m");
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
