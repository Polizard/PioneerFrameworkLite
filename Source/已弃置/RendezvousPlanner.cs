// 【已弃置 2026-09-27】旧范式交会规划器窗口。用户裁定「不留」（2026-09-22 已裁定该套旧律不能用了）。
// 归档而非删除：留盘不编译（`Source/` 下无跨目录通配 ⇒ 本目录不参与编译）。
// 原路径：Source/PioneerFramework/Base/Orbital/RendezvousPlanner.cs
// 解锁前提（若要恢复）：需同时恢复 SpaceGroupVisualManager 的预览绘制分支 + 调试页签入口。

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;
using MechJebLib.Lambert;

namespace PioneerFramework
{
    /// <summary>
    /// 交会规划窗口。
    /// </summary>
    public class RendezvousPlanner : Window
    {
        private SpaceEntityGroup selfGroup;
        private SpaceGameComp spaceGame;

        private int selectedTargetId = -1;
        private Vector2 scrollPos;

        private double transferTimeFraction = 0.5;
        // 转移时长上限放宽到 4 倍自轨道周期：LEO→同步这类跨高度转移需要 ~2.5 倍
        // （旧上限 2.0 恰好卡在门外，见 2026-09-16 实测 dt=11461s 顶到上界）
        private const double MaxTransferFractions = 4.0;
        /// <summary>出发延时：等待多少倍自轨道周期后再点火。</summary>
        private double departureDelayFraction = 0.0;
        // 等待上限放宽到 2 倍自轨道周期：覆盖一个会合周期（LEO↔同步 ≈1.1 倍自周期），
        // 让"相位不对就多绕一圈（零 Δv）"这条路可选——MechJeb 的相位优先在同一处发力。
        private const double MaxDepartureDelayFraction = 2.0;
        private bool progradeTransfer = true;
        /// <summary>精确交会（默认）：到达段启用闭环引导（飞行中按实际状态修正）。
        /// false = 粗交会：两段开环机动（出发+到达），沿用旧行为。</summary>
        private bool preciseRendezvous = true;

        private bool solutionValid;
        private bool computeAttempted;
        private string computeStatusMsg;
        private double departureDV;
        private double arrivalDV;
        private double totalDV;
        private double transferTimeSeconds;
        private DVector3 departurePos;
        private DVector3 arrivalPos;
        private DVector3 v1Transfer;
        private DVector3 v2Transfer;
        private DVector3 vTargetAtArrival;

        private double escapeVelocityAtDeparture;
        private bool dvWarningActive;
        private const double EscapeWarnRatio = 0.8;

        // 缓存 ComputeTransfer 中间结果供 ConfirmRendezvous 复用
        private DVector3 cachedV1DepartureRel;
        private DVector3 cachedCentralPosDepart;

        private List<Vector3> transferOrbitPoints;
        private DVector3 targetPosAtArrival;
        private bool previewComputed;

        // ── 扫参结果缓存 ──
        private bool sweepPerformed;
        private string sweepResultMsg;

        private const float EntryHeight = 24f;
        private const float ButtonHeight = 30f;
        private const float LabelHeight = 22f;
        private const float WindowWidth = 420f;
        private const float WindowHeight = 500f;

        #region 静态预览 (供 SpaceGroupVisualManager 绘图用)

        public static RendezvousPlanner ActivePlanner { get; private set; }

        public static List<Vector3> TransferOrbitUnity =>
            ActivePlanner?.transferOrbitPoints?.ConvertAll(p => (Vector3)((DVector3)p * SpaceConstants.UnitsPerMeter));

        public static Vector3? DeparturePosUnity =>
            ActivePlanner != null ? (Vector3?)(ActivePlanner.departurePos * SpaceConstants.UnitsPerMeter) : null;

        public static Vector3? ArrivalPosUnity =>
            ActivePlanner != null ? (Vector3?)(ActivePlanner.arrivalPos * SpaceConstants.UnitsPerMeter) : null;

        public static Vector3? TargetPosAtArrivalUnity =>
            ActivePlanner != null ? (Vector3?)(ActivePlanner.targetPosAtArrival * SpaceConstants.UnitsPerMeter) : null;

        public static bool PreviewValid => ActivePlanner?.previewComputed ?? false;

        #endregion

        public RendezvousPlanner(SpaceEntityGroup self, SpaceGameComp gameComp)
        {
            selfGroup = self;
            spaceGame = gameComp;
            ActivePlanner = this;

            forcePause = true;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            draggable = true;
            resizeable = true;
        }

        public override Vector2 InitialSize => new Vector2(WindowWidth, WindowHeight);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 30f),
                string.Format("交会规划 - Group {0}", selfGroup.GroupId));
            Text.Font = GameFont.Small;

            float y = 35f;

            Widgets.Label(new Rect(0f, y, inRect.width, LabelHeight), "选择交会目标:");
            y += LabelHeight + 2f;

            var allGroups = spaceGame.GetAllGroups()
                .Where(g => g.IsActive && g.GroupId != selfGroup.GroupId)
                .ToList();

            Rect listRect = new Rect(0f, y, inRect.width, 150f);
            Widgets.BeginScrollView(listRect, ref scrollPos, new Rect(0f, 0f, listRect.width - 16f, allGroups.Count * EntryHeight));

            for (int i = 0; i < allGroups.Count; i++)
            {
                var g = allGroups[i];
                Rect entryRect = new Rect(0f, i * EntryHeight, listRect.width - 16f, EntryHeight);

                bool isSelected = g.GroupId == selectedTargetId;
                if (Widgets.RadioButtonLabeled(entryRect,
                    string.Format("Group {0} (r={1:F0}km)", g.GroupId,
                        g.CurrentOrbit.position.magnitude / 1000.0),
                    isSelected))
                {
                    selectedTargetId = g.GroupId;
                    previewComputed = false;
                    // 自适应默认转移时间：霍曼转移半周期
                    double r1 = selfGroup.CurrentOrbit.position.magnitude;
                    if (selfGroup.IsCircularOrbit)
                    {
                        double physicsTickSel = TimeScaleManager.GetPhysicsTickSafe();
                        int nowTick = (int)Math.Round(physicsTickSel);
                        r1 = selfGroup.GetCircularPositionAtTick(nowTick).magnitude;
                    }
                    double r2 = g.CurrentOrbit.position.magnitude;
                    double aTransfer = (r1 + r2) / 2.0;
                    double hohmannHalfPeriod = System.Math.PI * System.Math.Sqrt(aTransfer * aTransfer * aTransfer / OrbitMath.GM);
                    double selfPeriodSec = EstimateOrbitPeriod(selfGroup);
                    if (selfPeriodSec > 0.0)
                        transferTimeFraction = Mathf.Clamp((float)(hohmannHalfPeriod / selfPeriodSec), 0.1f, 2.0f);
                    // 自动扫参找最佳出发延时 + 转移时间
                    AutoSweepTransfer(g);
                }
            }

            Widgets.EndScrollView();
            y += listRect.height + 6f;

            if (selectedTargetId < 0)
            {
                Widgets.Label(new Rect(0f, y, inRect.width, LabelHeight), "请选择交会目标");
                return;
            }

            var targetGroup = spaceGame.GetGroupById(selectedTargetId);
            if (targetGroup == null) return;

            double selfPeriod = EstimateOrbitPeriod(selfGroup);

            // ── 出发延时滑块 ──
            Widgets.Label(new Rect(0f, y, inRect.width, LabelHeight),
                string.Format("出发延时: {0:F1}% 轨道周期 ({1:F0} 秒)",
                    departureDelayFraction * 100.0, departureDelayFraction * selfPeriod));
            y += LabelHeight + 2f;
            departureDelayFraction = Widgets.HorizontalSlider(
                new Rect(0f, y, inRect.width, 22f),
                (float)departureDelayFraction, 0f, (float)MaxDepartureDelayFraction, false, null, null, null, 0.01f);
            y += 26f;

            // ── 转移时间滑块 ──
            Widgets.Label(new Rect(0f, y, inRect.width, LabelHeight),
                string.Format("转移时间: {0:F1}% 轨道周期 ({1:F0} 秒)",
                    transferTimeFraction * 100.0, transferTimeFraction * selfPeriod));
            y += LabelHeight + 2f;
            transferTimeFraction = Widgets.HorizontalSlider(
                new Rect(0f, y, inRect.width, 22f),
                (float)transferTimeFraction, 0.1f, (float)MaxTransferFractions, false, null, null, null, 0.01f);
            y += 26f;

            Rect toggleRect = new Rect(0f, y, inRect.width, LabelHeight);
            if (Widgets.ButtonText(new Rect(0f, y, inRect.width * 0.48f, LabelHeight),
                progradeTransfer ? "[顺行]" : "顺行"))
            {
                progradeTransfer = true;
                previewComputed = false;
            }
            if (Widgets.ButtonText(new Rect(inRect.width * 0.52f, y, inRect.width * 0.48f, LabelHeight),
                !progradeTransfer ? "[逆行]" : "逆行"))
            {
                progradeTransfer = false;
                previewComputed = false;
            }
            y += LabelHeight + 6f;

            // ── 交会模式：精确（闭环引导）/ 粗（两段开环）──
            if (Widgets.ButtonText(new Rect(0f, y, inRect.width * 0.48f, LabelHeight),
                preciseRendezvous ? "[精确交会]" : "精确交会"))
            {
                preciseRendezvous = true;
                previewComputed = false;
            }
            if (Widgets.ButtonText(new Rect(inRect.width * 0.52f, y, inRect.width * 0.48f, LabelHeight),
                !preciseRendezvous ? "[粗交会]" : "粗交会"))
            {
                preciseRendezvous = false;
                previewComputed = false;
            }
            y += LabelHeight + 6f;

            if (Widgets.ButtonText(new Rect(0f, y, inRect.width, ButtonHeight), "计算转移轨道"))
            {
                ComputeTransfer(targetGroup);
            }
            y += ButtonHeight + 2f;

            if (sweepPerformed)
            {
                Widgets.Label(new Rect(0f, y, inRect.width, LabelHeight), sweepResultMsg);
                y += LabelHeight;
            }

            if (computeAttempted)
            {
                Widgets.Label(new Rect(0f, y, inRect.width, LabelHeight), computeStatusMsg);
                y += LabelHeight;
            }

            if (solutionValid)
            {
                Widgets.Label(new Rect(0f, y, inRect.width, LabelHeight),
                    string.Format("出发 dV: {0:F1} m/s  (逃逸速度: {1:F1} m/s)",
                        departureDV, escapeVelocityAtDeparture));
                y += LabelHeight;

                if (dvWarningActive)
                {
                    GUI.color = Color.red;
                    Widgets.Label(new Rect(0f, y, inRect.width, LabelHeight),
                        "⚠ 警告: 机动后速度超过逃逸速度 80%，群组可能逃逸!");
                    GUI.color = Color.white;
                    y += LabelHeight;
                }

                Widgets.Label(new Rect(0f, y, inRect.width, LabelHeight),
                    string.Format("到达 dV: {0:F1} m/s", arrivalDV));
                y += LabelHeight;
                Widgets.Label(new Rect(0f, y, inRect.width, LabelHeight),
                    string.Format("总 dV: {0:F1} m/s", totalDV));
                y += LabelHeight;
                Widgets.Label(new Rect(0f, y, inRect.width, LabelHeight),
                    string.Format("转移时间: {0:F0} 秒", transferTimeSeconds));
                y += LabelHeight + 4f;

                if (Widgets.ButtonText(new Rect(0f, y, inRect.width, ButtonHeight),
                    preciseRendezvous ? "确认交会 (精确·闭环引导)" : "确认交会 (粗·两段机动)"))
                {
                    ConfirmRendezvous(targetGroup);
                }
                y += ButtonHeight + 4f;
            }

            if (Widgets.ButtonText(new Rect(0f, y, inRect.width, ButtonHeight), "关闭"))
            {
                Close();
            }
        }

        /// <summary>
        /// 自动扫参：扫描出发延时和转移时间的 2D 网格，寻总 dV 最小且安全的解。
        /// 出发延时范围 0~100% 轨道周期，转移时间 10%~200% 轨道周期，步长 0.05。
        /// </summary>
        private void AutoSweepTransfer(SpaceEntityGroup targetGroup)
        {
            sweepPerformed = false;
            double bestDelay = departureDelayFraction;
            double bestTransfer = transferTimeFraction;
            double bestDV = double.MaxValue;
            double savedDelay = departureDelayFraction;
            double savedTransfer = transferTimeFraction;

            double physicsTickNow = TimeScaleManager.GetPhysicsTickSafe();

            // 粗扫：等待跨 2 圈（覆盖会合周期），转移时长到 4 倍自周期
            for (double delay = 0.0; delay <= 2.001; delay += 0.1)
            {
                for (double frac = 0.1; frac <= 4.001; frac += 0.1)
                {
                    departureDelayFraction = delay;
                    transferTimeFraction = frac;
                    ComputeTransfer(targetGroup);
                    if (solutionValid && totalDV < bestDV)
                    {
                        DVector3 v1Departure = selfGroup.localVelocity;
                        // 推演 v1Departure 到出发时刻需要沿轨道运动 departureDelay 时间
                        double selfPeriod = EstimateOrbitPeriod(selfGroup);
                        if (selfGroup.IsCircularOrbit)
                        {
                            int departTick = (int)Math.Round(physicsTickNow + selfPeriod * delay);
                            DVector3 radialDir = (departurePos - selfGroup.GetCentralBodyOffset(departTick)).normalized;
                            DVector3 tangentDir = DVector3.Cross(selfGroup.circularOrbitNormal, radialDir).normalized;
                            v1Departure = tangentDir * (selfGroup.circularAngularVelocity * selfGroup.circularRadius);
                        }
                        else
                        {
                            DVector3 posNow = selfGroup.CurrentOrbit.position;
                            DVector3 velNow = selfGroup.CurrentOrbit.velocity;
                            v1Departure = OrbitMath.GetVelocityAtTimeDelta(posNow, velNow, selfPeriod * delay, spaceGame.GetBodyGM(selfGroup.CelestialBodyId));
                        }
                        DVector3 dV1 = v1Transfer - v1Departure;
                        DVector3 postBurnVel = v1Departure + dV1;
                        // 中心天体参考系安全检测
                        int safeTick = (int)Math.Round(physicsTickNow + selfPeriod * delay);
                        DVector3 centralOffSafe = selfGroup.GetCentralBodyOffset(safeTick);
                        DVector3 relPosSafe = departurePos - centralOffSafe;
                        double safeGM = spaceGame.GetBodyGM(selfGroup.CelestialBodyId);
                        double safeR = spaceGame.GetBodySafeRadius(selfGroup.CelestialBodyId);
                        if (OrbitMath.IsOrbitSafe(relPosSafe, postBurnVel, safeR, safeGM))
                        {
                            bestDV = totalDV;
                            bestDelay = delay;
                            bestTransfer = frac;
                        }
                    }
                }
            }

            // 细扫：在粗扫最优点邻域加密（相位窗口窄，粗扫 0.1 圈 ≈ 573s 会错过窗口）
            if (bestDV < double.MaxValue)
            {
                double fineDelayLo = Math.Max(0.0, bestDelay - 0.1);
                double fineTransferLo = Math.Max(0.1, bestTransfer - 0.1);
                for (double delay = fineDelayLo; delay <= bestDelay + 0.1001; delay += 0.025)
                {
                    for (double frac = fineTransferLo; frac <= bestTransfer + 0.1001; frac += 0.025)
                    {
                        departureDelayFraction = delay;
                        transferTimeFraction = frac;
                        ComputeTransfer(targetGroup);
                        if (solutionValid && totalDV < bestDV)
                        {
                            bestDV = totalDV;
                            bestDelay = delay;
                            bestTransfer = frac;
                        }
                    }
                }
            }

            if (bestDV < double.MaxValue)
            {
                departureDelayFraction = bestDelay;
                transferTimeFraction = bestTransfer;
                ComputeTransfer(targetGroup);
                sweepPerformed = true;
                double selfPeriod = EstimateOrbitPeriod(selfGroup);
                sweepResultMsg = string.Format("扫参完成: 等待 {0:F0}s（{1:F1} 圈）+ 转移 {2:F0}s, 总dV={3:F1} m/s",
                    bestDelay * selfPeriod, bestDelay, bestTransfer * selfPeriod, bestDV);
            }
            else
            {
                departureDelayFraction = savedDelay;
                transferTimeFraction = savedTransfer;
                ComputeTransfer(targetGroup);
                sweepResultMsg = "扫参结束: 未找到安全的转移轨道。";
            }
        }

        private void ComputeTransfer(SpaceEntityGroup targetGroup)
        {
            solutionValid = false;
            previewComputed = false;
            computeAttempted = true;
            dvWarningActive = false;

            double physicsTickNow = TimeScaleManager.GetPhysicsTickSafe();

            DVector3 r1;
            DVector3 v1Current;

            if (selfGroup.IsCircularOrbit)
            {
                int nowTick = (int)Math.Round(physicsTickNow);
                r1 = selfGroup.GetCircularPositionAtTick(nowTick);
                v1Current = selfGroup.CurrentOrbit.velocity;
            }
            else
            {
                r1 = selfGroup.CurrentOrbit.position;
                v1Current = selfGroup.CurrentOrbit.velocity;
            }

            // ── 出发延时：先沿当前轨道飞行一段时间再点火 ──
            double selfPeriod = EstimateOrbitPeriod(selfGroup);
            double departureDelaySeconds = selfPeriod * departureDelayFraction;
            double dt = selfPeriod * transferTimeFraction;
            if (dt <= 0.0)
            {
                computeStatusMsg = "错误: 转移时间无效";
                return;
            }

            // 获取中心天体的 GM 和位置/速度偏移（用于转换到中心天体参考系）
            double centralGM = spaceGame.GetBodyGM(selfGroup.CelestialBodyId);
            int centralBodyId = selfGroup.CelestialBodyId;
            DVector3 centralPosNow = DVector3.zero;
            DVector3 centralVelNow = DVector3.zero;
            DVector3 centralPosDepart = DVector3.zero;
            DVector3 centralVelDepart = DVector3.zero;
            DVector3 centralPosArrival = DVector3.zero;
            DVector3 centralVelArrival = DVector3.zero;
            if (centralBodyId >= 0)
            {
                var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
                if (mgr != null)
                {
                    var centralDef = mgr.GetBodyById(centralBodyId);
                    if (centralDef != null && centralDef.bodyType != CelestialBodyType.Planet)
                    {
                        int nowTick = (int)Math.Round(physicsTickNow);
                        centralPosNow = mgr.GetBodyLocalPosition(centralBodyId, nowTick);
                        centralVelNow = mgr.GetBodyLocalVelocity(centralBodyId, nowTick);
                        int departTick = (int)Math.Round(physicsTickNow + departureDelaySeconds);
                        centralPosDepart = mgr.GetBodyLocalPosition(centralBodyId, departTick);
                        centralVelDepart = mgr.GetBodyLocalVelocity(centralBodyId, departTick);
                        int arrivalTickCalc = departTick + UnityEngine.Mathf.RoundToInt((float)dt);
                        centralPosArrival = mgr.GetBodyLocalPosition(centralBodyId, arrivalTickCalc);
                        centralVelArrival = mgr.GetBodyLocalVelocity(centralBodyId, arrivalTickCalc);
                        cachedCentralPosDepart = centralPosDepart;
                    }
                }
            }

            // 推演自身到出发时刻的位置和速度（地心系）
            DVector3 r1Departure, v1Departure;
            if (selfGroup.IsCircularOrbit)
            {
                int departTick = (int)Math.Round(physicsTickNow + departureDelaySeconds);
                r1Departure = selfGroup.GetCircularPositionAtTick(departTick);
                DVector3 radialDir = (r1Departure - selfGroup.GetCentralBodyOffset(departTick)).normalized;
                DVector3 tangentDir = DVector3.Cross(selfGroup.circularOrbitNormal, radialDir).normalized;
                v1Departure = tangentDir * (selfGroup.circularAngularVelocity * selfGroup.circularRadius);
                // v1Departure 为中心天体相对速度（不加中心天体轨道速度）
            }
            else
            {
                // 非圆轨：先转到中心天体相对系再传播，避免世界坐标+错误 GM 的问题
                DVector3 r1Rel = r1 - centralPosNow;
                DVector3 v1Rel = v1Current - centralVelNow;
                r1Departure = OrbitMath.GetPositionAtTimeDelta(r1Rel, v1Rel, departureDelaySeconds, centralGM) + centralPosDepart;
                v1Departure = OrbitMath.GetVelocityAtTimeDelta(r1Rel, v1Rel, departureDelaySeconds, centralGM) + centralVelDepart;
            }

            // 转换为中心天体相对坐标（Lambert 求解使用中心天体的 GM）
            // 圆轨：v1Departure 已是月心相对速度 → 不减速
            // 非圆轨：v1Departure = 相对传播速度 + centralVelDepart → 减后得月心相对速度
            DVector3 r1DepartureRel = r1Departure - centralPosDepart;
            DVector3 v1DepartureRel = selfGroup.IsCircularOrbit
                ? v1Departure
                : v1Departure - centralVelDepart;
            cachedV1DepartureRel = v1DepartureRel;

            // 推演目标到到达时刻的位置（出发延时 + 转移时间）
            // 使用同自身一样的相对系传播，避免世界坐标+错误 GM
            double totalSeconds = departureDelaySeconds + dt;
            DVector3 r2ArrivalRel;
            if (targetGroup.IsCircularOrbit)
            {
                int arrivalTick = (int)Math.Round(physicsTickNow + totalSeconds);
                r2ArrivalRel = targetGroup.GetCircularPositionAtTick(arrivalTick) - centralPosArrival;
            }
            else
            {
                DVector3 targetRelPos = targetGroup.CurrentOrbit.position - centralPosNow;
                DVector3 targetRelVel = targetGroup.CurrentOrbit.velocity - centralVelNow;
                r2ArrivalRel = OrbitMath.GetPositionAtTimeDelta(targetRelPos, targetRelVel, totalSeconds, centralGM);
            }
            DVector3 r2Arrival = r2ArrivalRel + centralPosArrival;


            // 使用 Gooding Lambert 求解器（中心天体参考系 + 正确 GM）
            TransferGeometry geomDir = progradeTransfer ? TransferGeometry.ShortWay : TransferGeometry.LongWay;
            bool success = true;
            try
            {
                (v1Transfer, v2Transfer) = Gooding.Solve(centralGM, r1DepartureRel, r2ArrivalRel, dt, geomDir);
            }
            catch (Exception)
            {
                success = false;
            }

            if (!success)
            {
                computeStatusMsg = string.Format("Lambert 无解 (r1={0:F0}km r2={1:F0}km dt={2:F0}s)",
                    r1DepartureRel.magnitude / 1000.0, r2ArrivalRel.magnitude / 1000.0, dt);
                Log.Message(string.Format("[Rendezvous] Lambert failed for Group {0}->{1}: {2}",
                    selfGroup.GroupId, targetGroup.GroupId, computeStatusMsg));
                return;
            }

            // v1Transfer, v2Transfer 是中心天体相对速度
            DVector3 dV1 = v1Transfer - v1DepartureRel;
            departureDV = dV1.magnitude;

            double rMag = r1DepartureRel.magnitude;
            escapeVelocityAtDeparture = rMag > 0.001
                ? System.Math.Sqrt(2.0 * centralGM / rMag) : 0.0;
            double vAfterBurn = (v1DepartureRel + dV1).magnitude;
            dvWarningActive = escapeVelocityAtDeparture > 0.0
                && vAfterBurn > escapeVelocityAtDeparture * EscapeWarnRatio;

            double arrivalDelaySeconds = totalSeconds;
            // 目标速度也用相对系传播（与位置一致）
            if (targetGroup.IsCircularOrbit)
            {
                DVector3 radialDir = r2ArrivalRel.normalized;
                DVector3 tangentDir = DVector3.Cross(targetGroup.circularOrbitNormal, radialDir).normalized;
                vTargetAtArrival = tangentDir * (targetGroup.circularAngularVelocity * targetGroup.circularRadius);
            }
            else
            {
                DVector3 targetRelPos = targetGroup.CurrentOrbit.position - centralPosNow;
                DVector3 targetRelVel = targetGroup.CurrentOrbit.velocity - centralVelNow;
                vTargetAtArrival = OrbitMath.GetVelocityAtTimeDelta(targetRelPos, targetRelVel, arrivalDelaySeconds, centralGM);
            }

            // 将 vTargetAtArrival 转为中心天体相对速度，供 ConfirmRendezvous 使用
            // 圆轨目标：vTargetAtArrival 已是月心相对速度 → 不减速
            // 非圆轨目标：vTargetAtArrival 是月心相对速度（已在相对系传播）→ 不减速
            // （与自身非圆轨的传播方式一致，不再需要 ±centralVel）

            Log.Message(string.Format("[Rendezvous] Group {0} → {1}: v1DepartureRel={2:F1} v1Transfer={3:F1} vTargetAtArrivalRel={4:F1} v2Transfer={5:F1} centralGM={6:E3}",
                selfGroup.GroupId, targetGroup.GroupId,
                v1DepartureRel.magnitude, v1Transfer.magnitude,
                vTargetAtArrival.magnitude, v2Transfer.magnitude, centralGM));

            DVector3 dV2 = vTargetAtArrival - v2Transfer;
            arrivalDV = dV2.magnitude;

            totalDV = departureDV + arrivalDV;
            transferTimeSeconds = dt;
            departurePos = r1Departure;
            arrivalPos = r2Arrival;
            targetPosAtArrival = r2Arrival;

            var dPoints = OrbitMath.GetOrbitPoints(r1DepartureRel, v1Transfer, 60, centralGM);
            transferOrbitPoints = dPoints.ConvertAll(p => (Vector3)(p + centralPosDepart));

            solutionValid = true;
            previewComputed = true;
            computeStatusMsg = string.Format("计算成功: 总dV={0:F1} m/s, 出发延时={1:F0}s, 转移时间={2:F0}s",
                totalDV, departureDelaySeconds, transferTimeSeconds);

            Log.Message(string.Format("[Rendezvous] Group {0} -> {1}: dV depart={2:F1} arrive={3:F1} total={4:F1} departureDelay={5:F0}s dt={6:F0}s",
                selfGroup.GroupId, targetGroup.GroupId, departureDV, arrivalDV, totalDV, departureDelaySeconds, dt));
        }

        private void ConfirmRendezvous(SpaceEntityGroup targetGroup)
        {
            ComputeTransfer(targetGroup);
            if (!solutionValid)
            {
                computeStatusMsg = "交会计算失败，请调整参数重试。";
                return;
            }

            double physicsTickNow = TimeScaleManager.GetPhysicsTickSafe();

            double selfPeriod = EstimateOrbitPeriod(selfGroup);
            double departureDelaySeconds = selfPeriod * departureDelayFraction;
            int confirmDepartTick = (int)Math.Round(physicsTickNow + departureDelaySeconds);
            double centralGMC = spaceGame.GetBodyGM(selfGroup.CelestialBodyId);
            DVector3 v1DepartureRel = cachedV1DepartureRel;
            DVector3 centralOffsetC = cachedCentralPosDepart;

            DVector3 dV1 = v1Transfer - v1DepartureRel;

            DVector3 postBurnVelRel = v1DepartureRel + dV1;
            double safeRadius = spaceGame.GetBodySafeRadius(selfGroup.CelestialBodyId);
            if (!OrbitMath.IsOrbitSafe(departurePos - centralOffsetC, postBurnVelRel, safeRadius, centralGMC))
            {
                string msg = string.Format(
                    "转移轨道近地点低于大气安全边界 ({0:F0} km)，可能撞击行星，已取消。",
                    safeRadius / 1000.0);
                Log.Message(string.Format("[Rendezvous] BLOCKED: Group {0} transfer orbit impacts planet! pe < {1:F0} km",
                    selfGroup.GroupId, safeRadius / 1000.0));
                Find.LetterStack.ReceiveLetter("交会机动被阻止", msg, LetterDefOf.NegativeEvent);
                return;
            }

            var currentOrbitPts = OrbitMath.GetOrbitPoints(departurePos - centralOffsetC, v1DepartureRel, 60, centralGMC);
            currentOrbitPts = currentOrbitPts.ConvertAll(p => p + centralOffsetC);

            double radDV1, tanDV1, normDV1;
            int departTick = confirmDepartTick;
            DVector3 departCentralOffset = selfGroup.GetCentralBodyOffset(departTick);
            DVector3 relDepartPos = departurePos - departCentralOffset;
            DVector3 relDepartVel = v1DepartureRel;
            OrbitMath.WorldDeltaVToComponents(relDepartPos, relDepartVel,
                dV1, out radDV1, out tanDV1, out normDV1);

            int departureTick = departTick;

            Log.Message(string.Format("[RendezvousPlanner] Group {0} depart localFrame: bodyId={1} |pos|={2:F0}km |vel|={3:F1} m/s |dV|={4:F1} m/s",
                selfGroup.GroupId, selfGroup.CelestialBodyId, relDepartPos.magnitude / 1000.0, relDepartVel.magnitude, dV1.magnitude));

            var departureManeuver = new SpaceEntityGroup.PlannedManeuver
            {
                radialDV = (float)radDV1,
                tangentialDV = (float)tanDV1,
                normalDV = (float)normDV1,
                executeTick = departureTick,
                active = true,
                // 规划时刻的世界系 dV：执行时直接用作惯性方向，避免点火时刻换算的帧旋转误差
                plannedWorldDv = dV1,
                // 精确交会：出发段点火后激活闭环引导
                armRendezvousGuidance = preciseRendezvous,
                previewCurrentOrbit = currentOrbitPts.ConvertAll(p => (Vector3)p),
                previewNodePosition = departurePos,
                previewPostOrbit = transferOrbitPoints != null
                    ? new List<Vector3>(transferOrbitPoints)
                    : new List<Vector3>()
            };
            spaceGame.QueueGroupManeuver(selfGroup.GroupId, departureManeuver);

            DVector3 dV2 = vTargetAtArrival - v2Transfer;
            int arrivalTick = departureTick + UnityEngine.Mathf.RoundToInt((float)transferTimeSeconds);

            if (preciseRendezvous)
            {
                // ── 精确交会：到达段交给闭环引导 ──
                // 不再排队固定到达机动；出发段点火后（armRendezvousGuidance）引导生效，
                // 飞行中按实际状态逐次 Lambert 修正，接近终点时消相对速度。
                var networks = selfGroup.plannedManeuvers.Count > 0
                    ? selfGroup.plannedManeuvers[0].useNetworks : null;
                selfGroup.RegisterRendezvousGuidance(targetGroup.GroupId, arrivalTick, progradeTransfer, networks);

                if (selfGroup.CelestialBodyId >= 0)
                {
                    selfGroup.EnterOrbitKeeping(new HashSet<int> { selfGroup.CelestialBodyId });
                    Log.Message(string.Format("[Rendezvous] OrbitKeeping enabled for Group {0}: primary body {1} whitelisted.",
                        selfGroup.GroupId, selfGroup.CelestialBodyId));
                }

                Log.Message(string.Format("[Rendezvous] Confirmed (PRECISE)! Group {0} -> {1}: departure burn queued, arrival guidance active. " +
                    "Depart dV={2:F1} m/s arrivalTick={3} prograde={4}",
                    selfGroup.GroupId, targetGroup.GroupId, departureDV, arrivalTick, progradeTransfer));
                Close();
                return;
            }

            // ── 粗交会：两段开环机动（出发 + 到达）──
            double radDV2, tanDV2, normDV2;
            DVector3 arrivalCentralOffset = selfGroup.GetCentralBodyOffset(arrivalTick);
            DVector3 relArrivalPos = arrivalPos - arrivalCentralOffset;
            DVector3 relArrivalVel = v2Transfer;
            OrbitMath.WorldDeltaVToComponents(relArrivalPos, relArrivalVel,
                dV2, out radDV2, out tanDV2, out normDV2);

            Log.Message(string.Format("[RendezvousPlanner] Group {0} arrival localFrame: bodyId={1} |pos|={2:F0}km |vel|={3:F1} m/s |dV|={4:F1} m/s",
                selfGroup.GroupId, selfGroup.CelestialBodyId, relArrivalPos.magnitude / 1000.0, relArrivalVel.magnitude, dV2.magnitude));

            var postArrivalLocalPts = OrbitMath.GetOrbitPoints(relArrivalPos, relArrivalVel, 60, centralGMC);
            var postArrivalOrbitPts = postArrivalLocalPts.ConvertAll(p => p + arrivalCentralOffset);

            var arrivalManeuver = new SpaceEntityGroup.PlannedManeuver
            {
                radialDV = (float)radDV2,
                tangentialDV = (float)tanDV2,
                normalDV = (float)normDV2,
                executeTick = arrivalTick,
                active = true,
                plannedWorldDv = dV2,
                previewCurrentOrbit = transferOrbitPoints != null
                    ? new List<Vector3>(transferOrbitPoints)
                    : new List<Vector3>(),
                previewNodePosition = arrivalPos,
                previewPostOrbit = postArrivalOrbitPts.ConvertAll(p => (Vector3)p)
            };
            spaceGame.QueueGroupManeuver(selfGroup.GroupId, arrivalManeuver);

            // 启用轨道保持
            if (selfGroup.CelestialBodyId >= 0)
            {
                selfGroup.EnterOrbitKeeping(new HashSet<int> { selfGroup.CelestialBodyId });
                Log.Message(string.Format("[Rendezvous] OrbitKeeping enabled for Group {0}: primary body {1} whitelisted.",
                    selfGroup.GroupId, selfGroup.CelestialBodyId));
            }

            Log.Message(string.Format("[Rendezvous] Confirmed (COARSE)! Group {0} -> {1}: queued 2 maneuvers. " +
                "Depart dV={2:F1} Arrive dV={3:F1} dt={4:F0}s",
                selfGroup.GroupId, targetGroup.GroupId, departureDV, arrivalDV, transferTimeSeconds));

            Close();
        }

        private double EstimateOrbitPeriod(SpaceEntityGroup group)
        {
            double gm = spaceGame.GetBodyGM(group.CelestialBodyId);
            if (group.IsCircularOrbit)
            {
                double a = group.circularRadius;
                return 2.0 * System.Math.PI * System.Math.Sqrt(a * a * a / gm);
            }
            DVector3 pos = group.CurrentOrbit.position;
            DVector3 vel = group.CurrentOrbit.velocity;

            // 对非行星天体，转相对系再计算半长轴
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr != null && group.CelestialBodyId >= 0)
            {
                var body = mgr.GetBodyById(group.CelestialBodyId);
                if (body != null && body.bodyType != CelestialBodyType.Planet)
                {
                    double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
                    int nowTick = (int)Math.Round(physicsTick);
                    pos -= mgr.GetBodyLocalPosition(group.CelestialBodyId, nowTick);
                    vel -= mgr.GetBodyLocalVelocity(group.CelestialBodyId, nowTick);
                }
            }

            // ── 周期收敛（《大修计划》§14.3『周期』行）──
            // 本方法原先自带一份 2π√(a³/μ)。现在统一由 `OrbitView.period` 给（唯一权威）。
            // ⚠ **只换了算式，上面的参考系处理一字未动**——换错参考系是这类 bug 的老家，
            //   而这一步是"收敛"不是"改行为"，读数必须逐位不变。
            OrbitView ov = OrbitView.FromStateVectors(pos, vel, gm, 0.0);
            double period = ov.period;
            if (double.IsNaN(period) || period <= 0.0) return 10000.0; // 原有兜底（双曲/退化）
            return period;
        }

        public override void Close(bool doCloseSound = true)
        {
            ActivePlanner = null;
            base.Close(doCloseSound);
        }
    }
}
