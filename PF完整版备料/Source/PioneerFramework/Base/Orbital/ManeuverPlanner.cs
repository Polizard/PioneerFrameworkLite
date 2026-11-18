using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;

namespace PioneerFramework
{
    /// <summary>
    /// 机动规划窗口。
    /// </summary>
    public class ManeuverPlanner : Window
    {
        private SpaceEntityGroup group;
        private SpaceGameComp spaceGame;

        // 滑块值
        private float maneuverTimeDeltaSeconds = 60f;
        private float radialDV = 0f;
        private float tangentialDV = 0f;
        private float normalDV = 0f;
        /// <summary>预测时间（秒），用于标记轨迹上的未来位置和同系统卫星位置。</summary>
        private float predictionTimeSeconds = PredictionTimeDefault;

        private List<OrbitSegment> orbitSegments;

        private double currentPe;
        private double currentAp;
        private double currentInclination;
        private double postPe;
        private double postAp;
        private double dVTotal;

        private bool previewComputed = false;
        private bool previewDirty = true;

        /// <summary>规划发起时中心天体的世界坐标偏移（用于冻结预览参考系）。</summary>
        private DVector3 plannedCentralOffset;

        // ── 预测标记 ──
        /// <summary>卫星在 predictionTimeSeconds 后的预测位置（Unity 坐标），供可视化绘制。</summary>
        public static Vector3? PredictedSatellitePosUnity => ActivePlanner?.ComputePredictedPositionUnity();
        /// <summary>本行星系各卫星在 predictionTimeSeconds 后的位置（Unity 坐标），供可视化绘制。</summary>
        public static List<Vector3> PredictedMoonPositionsUnity => ActivePlanner?.ComputeMoonPositionsUnity();
        /// <summary>当前预测时间（秒），供可视化同步。</summary>
        public static float PredictionTimeSec => ActivePlanner?.predictionTimeSeconds ?? 0f;

        private const float SliderHeight = 22f;
        private const float LabelWidth = 150f;
        private const float SliderRange = 1000f;
        private const float NormalSliderRange = 500f;
        private const float MaxTimeSeconds = 3600f;
        private const float MaxPredictionTimeSeconds = 14400f;
        private const float PredictionTimeDefault = 600f;
        private const float ButtonHeight = 30f;
        private const float PresetButtonWidth = 60f;
        private const float PresetStepDV = 10f;
        private const float BigPresetStepDV = 100f;
        private const float LabelHeight = 22f;

        private const float WindowWidth = 400f;
        private const float WindowHeight = 590f;

        // ── 真实预览模式 ──
        public enum PreviewMode { Kepler, Perturbed }
        /// <summary>全局预览模式：Kepler（默认，闭合圆锥曲线）或 Perturbed（多体数值传播）。</summary>
        public static PreviewMode CurrentPreviewMode = PreviewMode.Kepler;
        /// <summary>真实预览步长（秒），远大于物理步长 2s。</summary>
        private const double PerturbedStepSeconds = 60.0;
        /// <summary>真实预览步数。</summary>
        private const int PerturbedStepCount = 200;

        /// <summary>
        /// 一段轨道（用于分段绘制）。
        /// </summary>
        public class OrbitSegment
        {
            public List<DVector3> pointsMeters;       // 点列（米）
            public List<Vector3> pointsUnity;        // 点列（Unity 单位）
            public bool isPostManeuver;
            public DVector3? nodePositionMeters;
            public Color color;
            public string label;
        }

        #region 静态访问 (供 SpaceGroupVisualManager 绘图用)

        public static ManeuverPlanner ActivePlanner { get; private set; }
        public static bool HasPlanner => ActivePlanner != null;

        /// <summary>预览是否已成功计算。</summary>
        public static bool PreviewValid => ActivePlanner != null && ActivePlanner.previewComputed;

        public static List<OrbitSegment> PreviewSegments => ActivePlanner?.orbitSegments;

        public static List<Vector3> CurrentOrbitUnity
        {
            get
            {
                var segs = ActivePlanner?.orbitSegments;
                if (segs == null || segs.Count == 0) return null;
                for (int i = 0; i < segs.Count; i++)
                    if (!segs[i].isPostManeuver)
                        return segs[i].pointsUnity;
                return segs[0].pointsUnity;
            }
        }
        public static Vector3? NodePositionUnity
        {
            get
            {
                var segs = ActivePlanner?.orbitSegments;
                if (segs == null) return null;
                for (int i = 0; i < segs.Count; i++)
                    if (segs[i].nodePositionMeters.HasValue)
                        return (Vector3)(segs[i].nodePositionMeters.Value * SpaceConstants.UnitsPerMeter);
                return null;
            }
        }

        #endregion

        public ManeuverPlanner(SpaceEntityGroup targetGroup)
        {
            group = targetGroup;
            spaceGame = Current.Game.GetComponent<SpaceGameComp>();
            ActivePlanner = this;

            // 缓存中心天体的世界坐标偏移，用于冻结预览参考系
            plannedCentralOffset = DVector3.zero;
            int bodyId = group.CelestialBodyId;
            if (bodyId >= 0)
            {
                var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
                if (mgr != null)
                {
                    var body = mgr.GetBodyById(bodyId);
                    if (body != null && body.bodyType != CelestialBodyType.Planet)
                        plannedCentralOffset = group.GetCentralBodyOffset(Find.TickManager.TicksGame);
                }
            }

            forcePause = true;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            draggable = true;

            RebuildOrbitPreview();
        }

        public override Vector2 InitialSize => new Vector2(WindowWidth, WindowHeight);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 30f),
                string.Format("机动规划 - Group {0}", group.GroupId));
            Text.Font = GameFont.Small;

            float y = 35f;

            // 时间滑块
            Widgets.Label(new Rect(0f, y, LabelWidth, SliderHeight),
                string.Format("机动时间: {0:F0}s", maneuverTimeDeltaSeconds));
            y += SliderHeight;
            float newTime = Widgets.HorizontalSlider(
                new Rect(0f, y, inRect.width, SliderHeight),
                maneuverTimeDeltaSeconds, 0f, MaxTimeSeconds, false, null, null, null, 1f);
            if (Mathf.Abs(newTime - maneuverTimeDeltaSeconds) > 0.5f)
            {
                maneuverTimeDeltaSeconds = newTime;
                previewDirty = true;
            }
            y += SliderHeight + 4f;

            // 真实预览模式切换（有中心天体的群组可用）
            if (group.CelestialBodyId >= 0)
            {
                string modeLabel = CurrentPreviewMode == PreviewMode.Perturbed ? "真实(摄动)" : "开普勒";
                if (Widgets.ButtonText(new Rect(0f, y, inRect.width, ButtonHeight), "预览模式: " + modeLabel))
                {
                    CurrentPreviewMode = CurrentPreviewMode == PreviewMode.Kepler
                        ? PreviewMode.Perturbed
                        : PreviewMode.Kepler;
                    previewDirty = true;
                }
                y += ButtonHeight + 4f;
            }

            // 预测时间滑块
            Widgets.Label(new Rect(0f, y, inRect.width, SliderHeight),
                string.Format("预测 t: {0:F0}s", predictionTimeSeconds));
            y += SliderHeight;
            float newPrediction = Widgets.HorizontalSlider(
                new Rect(0f, y, inRect.width, SliderHeight),
                predictionTimeSeconds, 0f, MaxPredictionTimeSeconds, false, null, null, null, 1f);
            if (Mathf.Abs(newPrediction - predictionTimeSeconds) > 0.5f)
            {
                predictionTimeSeconds = newPrediction;
            }
            y += SliderHeight + 4f;

            // 径向 dV
            Widgets.Label(new Rect(0f, y, LabelWidth, SliderHeight),
                string.Format("径向 (R): {0:F0} m/s", radialDV));
            y += SliderHeight;
            float newRadial = Widgets.HorizontalSlider(
                new Rect(0f, y, inRect.width, SliderHeight),
                radialDV, -SliderRange, SliderRange, false, null, null, null, 1f);
            if (Mathf.Abs(newRadial - radialDV) > 0.1f) { radialDV = newRadial; previewDirty = true; }
            y += SliderHeight + 4f;

            // 切向 dV
            Widgets.Label(new Rect(0f, y, LabelWidth, SliderHeight),
                string.Format("切向 (T): {0:F0} m/s", tangentialDV));
            y += SliderHeight;
            float newTangential = Widgets.HorizontalSlider(
                new Rect(0f, y, inRect.width, SliderHeight),
                tangentialDV, -SliderRange, SliderRange, false, null, null, null, 1f);
            if (Mathf.Abs(newTangential - tangentialDV) > 0.1f) { tangentialDV = newTangential; previewDirty = true; }
            y += SliderHeight + 4f;

            // 法向 dV
            Widgets.Label(new Rect(0f, y, LabelWidth, SliderHeight),
                string.Format("法向 (N): {0:F0} m/s", normalDV));
            y += SliderHeight;
            float newNormal = Widgets.HorizontalSlider(
                new Rect(0f, y, inRect.width, SliderHeight),
                normalDV, -NormalSliderRange, NormalSliderRange, false, null, null, null, 1f);
            if (Mathf.Abs(newNormal - normalDV) > 0.1f) { normalDV = newNormal; previewDirty = true; }
            y += SliderHeight + 4f;

            // 预设按钮
            DrawPresetButtons(ref y, inRect.width);

            // 轨道要素显示
            y += 4f;
            Widgets.Label(new Rect(0f, y, inRect.width, LabelHeight),
                string.Format("当前: Pe={0:F0}km Ap={1:F0}km 倾角={2:F1}°",
                    currentPe / 1000.0, currentAp / 1000.0, currentInclination * Mathf.Rad2Deg));
            y += LabelHeight;
            Widgets.Label(new Rect(0f, y, inRect.width, LabelHeight),
                string.Format("机动后: Pe={0:F0}km Ap={1:F0}km |dV|={2:F1} m/s",
                    postPe / 1000.0, postAp / 1000.0, dVTotal));
            y += LabelHeight + 4f;

            // 确认按钮
            if (Widgets.ButtonText(new Rect(0f, y, inRect.width, ButtonHeight), "确 认 机 动"))
            {
                ConfirmManeuver();
            }
            y += ButtonHeight + 4f;

            // 添加至队列（不关闭规划器）
            if (Widgets.ButtonText(new Rect(0f, y, inRect.width, ButtonHeight), "加入队列 (继续规划)"))
            {
                ConfirmManeuver();
                ResetSliders();
            }
            y += ButtonHeight + 4f;

            // 清除队列
            if (Widgets.ButtonText(new Rect(0f, y, inRect.width, ButtonHeight), "清除所有机动规划"))
            {
                group.ClearManeuvers();
                RebuildOrbitPreview();
            }
            y += ButtonHeight + 4f;

            // 关闭
            if (Widgets.ButtonText(new Rect(0f, y, inRect.width, ButtonHeight), "关闭"))
            {
                Close();
            }

            // 脏标记刷新
            if (previewDirty)
            {
                previewDirty = false;
                RebuildOrbitPreview();
            }
        }

        private void DrawPresetButtons(ref float y, float width)
        {
            float btnY = y;
            // 快速预设：±10, ±100
            string[] labels = new string[] { "-100", "-10", "+10", "+100" };
            float[] values = new float[] { -100f, -10f, 10f, 100f };
            for (int i = 0; i < 4; i++)
            {
                float bx = i * (PresetButtonWidth + 4f);
                if (Widgets.ButtonText(new Rect(bx, btnY, PresetButtonWidth, 22f), labels[i]))
                {
                    tangentialDV += values[i];
                    previewDirty = true;
                }
            }
            y = btnY + 26f;
        }

        private void ResetSliders()
        {
            maneuverTimeDeltaSeconds = 60f;
            radialDV = 0f;
            tangentialDV = 0f;
            normalDV = 0f;
            predictionTimeSeconds = PredictionTimeDefault;
            previewDirty = true;
        }

        /// <summary>
        /// 用指定 GM 传播中心天体相对坐标，在 ManeuverPlanner 中统一替代 OrbitMath 函数使用。
        /// 圆轨直接用角速度旋转（正确支持任何 GM），非圆轨回落至 OrbitMath。
        /// </summary>
        private void PropagateLocalOrbit(DVector3 localPos, DVector3 localVel, double dt, double gm,
            out DVector3 outPos, out DVector3 outVel)
        {
            double r = localPos.magnitude;
            double v2 = localVel.sqrMagnitude;
            double eps = 0.5 * v2 - gm / r;

            if (Math.Abs(eps) < 1e-10 || eps >= 0.0) // 抛物线/双曲
            {
                outPos = localPos;
                outVel = localVel;
                return;
            }

            double a = -gm / (2.0 * eps);
            double e = (DVector3.Cross(localVel, DVector3.Cross(localPos, localVel)) / gm - localPos / r).magnitude;

            if (e < 0.001 || Math.Abs(e) < 0.001) // 圆轨
            {
                double omega = Math.Sqrt(gm / (r * r * r));
                double angle = omega * dt;
                DVector3 normal = DVector3.Cross(localPos, localVel).normalized;
                // Rodrigues 旋转公式
                DVector3 rotated = localPos * Math.Cos(angle) + DVector3.Cross(normal, localPos) * Math.Sin(angle);
                DVector3 velRotated = localVel * Math.Cos(angle) + DVector3.Cross(normal, localVel) * Math.Sin(angle);
                outPos = rotated;
                outVel = velRotated;
                return;
            }

            // 椭圆：回落至 OrbitMath（按标准路径也可近似接受地心 GM）
            outPos = OrbitMath.GetPositionAtTimeDelta(localPos, localVel, dt, gm);
            outVel = OrbitMath.GetVelocityAtTimeDelta(localPos, localVel, dt, gm);
        }

        /// <summary>计算机动后 predictionTimeSeconds 时的卫星位置（地心系 Unity 坐标）。</summary>
        private Vector3? ComputePredictedPositionUnity()
        {
            if (group == null) return null;
            double t = predictionTimeSeconds;
            double toUnity = SpaceConstants.UnitsPerMeter;

            // 获取正确的 GM
            double centralGM;
            DVector3 centralOffset;
            centralGM = spaceGame.GetBodyGM(group.CelestialBodyId);
            centralOffset = plannedCentralOffset;

            int now = Find.TickManager.TicksGame;
            DVector3 pos, vel;
            if (group.IsCircularOrbit)
            {
                pos = group.GetCircularPositionAtTick(now);
                vel = group.CurrentOrbit.velocity;
            }
            else
            {
                pos = group.CurrentOrbit.position;
                vel = group.CurrentOrbit.velocity;
            }

            // 如果预测时间在机动之前，直接沿当前轨道传播
            DVector3 futurePos;
            if (t <= maneuverTimeDeltaSeconds)
            {
                futurePos = OrbitMath.GetPositionAtTimeDelta(pos, vel, t, centralGM);
                return (Vector3)(futurePos * toUnity);
            }

            // 传播到机动节点 → 施加 dV → 继续传播到预测时间
            DVector3 nodePos = OrbitMath.GetPositionAtTimeDelta(pos, vel, maneuverTimeDeltaSeconds, centralGM);
            DVector3 nodeVel = OrbitMath.GetVelocityAtTimeDelta(pos, vel, maneuverTimeDeltaSeconds, centralGM);

            // 用正确的 GM 传播节点处的中心天体相对位置
            PropagateLocalOrbit(group.localPosition, group.localVelocity, maneuverTimeDeltaSeconds, centralGM,
                out DVector3 nodeLocalPos, out DVector3 nodeLocalVel);
            DVector3 dV = OrbitMath.GetWorldDeltaV(nodeLocalPos, nodeLocalVel, radialDV, tangentialDV, normalDV);

            DVector3 postBurnVel = nodeVel + dV;
            double remainingT = t - maneuverTimeDeltaSeconds;
            futurePos = OrbitMath.GetPositionAtTimeDelta(nodePos, postBurnVel, remainingT, centralGM);
            return (Vector3)(futurePos * toUnity);
        }

        /// <summary>计算 predictionTimeSeconds 后，本行星系所有天体的位置（Unity 坐标）。</summary>
        private List<Vector3> ComputeMoonPositionsUnity()
        {
            var result = new List<Vector3>();
            int bodyId = group.CelestialBodyId;
            if (bodyId < 0) return result;
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return result;
            var primaryBody = mgr.GetBodyById(bodyId);
            if (primaryBody == null) return result;

            double toUnity = SpaceConstants.UnitsPerMeter;
            int futureTick = Find.TickManager.TicksGame + Mathf.RoundToInt(predictionTimeSeconds);

            // 添加中心天体的卫星（用 GetBodyWorldPosition 自动递归算世界坐标）
            for (int i = 0; i < primaryBody.moonBodies.Count; i++)
            {
                var moon = primaryBody.moonBodies[i];
                DVector3 moonWorldPos = mgr.GetBodyWorldPosition(moon.bodyId, futureTick);
                result.Add((Vector3)(moonWorldPos * toUnity));
            }

            return result;
        }

        /// <summary>
        /// 生成多体数值传播的预览点（扰动弹道）。
        /// 使用大步长三阶泰勒积分，比开普勒预测更符合实际轨迹。
        /// 返回中心天体参考系中的位置列表。游戏暂停时天体位置固定。
        /// 对卫星群组自动包含其父天体的引力摄动。
        /// </summary>
        private List<DVector3> GeneratePerturbedPreviewPoints(DVector3 localPos, DVector3 localVel, int numSteps, double stepSec)
        {
            var points = new List<DVector3>(numSteps);
            points.Add(localPos);

            int pausedTick = Find.TickManager.TicksGame;
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (celestialMgr == null) return points;

            var primaryBody = celestialMgr.GetBodyById(group.CelestialBodyId);
            if (primaryBody == null) return points;

            // 获取父天体的世界坐标（用于卫星群组的摄动计算）
            // 只有中心天体不是行星时（如月球），才需要父天体摄动
            DVector3 parentWorldPos = DVector3.zero;
            double parentMu = 0.0;
            if (primaryBody.bodyType != CelestialBodyType.Planet && primaryBody.ParentBodyId >= 0)
            {
                var parentBody = celestialMgr.GetBodyById(primaryBody.ParentBodyId);
                if (parentBody != null)
                {
                    parentWorldPos = celestialMgr.GetBodyWorldPosition(primaryBody.ParentBodyId, pausedTick);
                    parentMu = parentBody.gravitationalParameter;
                }
            }

            // 中心天体的世界坐标（local系原点）
            DVector3 centralWorldPos = celestialMgr.GetBodyWorldPosition(group.CelestialBodyId, pausedTick);

            DVector3 pos = localPos;
            DVector3 vel = localVel;

            for (int i = 0; i < numSteps; i++)
            {
                // 1) 中心天体的引力（MultiBodyAccelJerk 在中心天体坐标系中计算）
                NBodyIntegrator.MultiBodyAccelJerk(
                    pos, vel,
                    pausedTick, primaryBody, celestialMgr,
                    null,
                    out DVector3 acc, out DVector3 jerk);

                // 2) 父天体摄动（只有卫星群组才有）
                if (parentMu > 0.0)
                {
                    // 卫星在世界的绝对位置
                    DVector3 satWorldPos = centralWorldPos + pos;
                    // 从卫星指向父天体的相对矢量
                    DVector3 rToParent = parentWorldPos - satWorldPos;
                    double rSq = rToParent.sqrMagnitude;
                    if (rSq > 0.01)
                    {
                        double r = Math.Sqrt(rSq);
                        double muOverR3 = parentMu / (rSq * r);
                        DVector3 aParent = muOverR3 * rToParent;
                        acc += aParent;
                        // 急动度忽略（大阶梯下意义不大）
                    }
                }

                NBodyIntegrator.ThirdOrderTaylorStep(
                    pos, vel, acc, jerk, stepSec,
                    out DVector3 newPos, out DVector3 newVel);

                pos = newPos;
                vel = newVel;
                points.Add(pos);
            }

            return points;
        }

        /// <summary>
        /// 读取滑块值并建立预览分段（包括已有队列 + 新机动）。
        /// </summary>
        public void RebuildOrbitPreview()
        {
            if (group == null) return;

            previewComputed = false;
            double toUnity = SpaceConstants.UnitsPerMeter;
            orbitSegments = new List<OrbitSegment>();

            // 获取当前轨道状态，支持圆轨+标准模式
            int now = Find.TickManager.TicksGame;
            DVector3 pos, vel;
            if (group.IsCircularOrbit)
            {
                pos = group.GetCircularPositionAtTick(now);
                vel = group.CurrentOrbit.velocity;
            }
            else
            {
                pos = group.CurrentOrbit.position;
                vel = group.CurrentOrbit.velocity;
            }

            // 获取中心天体的 GM 和世界坐标偏移（用于正确绘制轨道预览）
            double centralGM;
            DVector3 centralOffset;
            centralGM = spaceGame.GetBodyGM(group.CelestialBodyId);
            centralOffset = plannedCentralOffset;

            // 获取当前轨道要素（地心系）
            OrbitMath.GetEccentricity(pos, vel, out _, out double currentE, centralGM);
            double currentSMA = OrbitMath.SemiMajorAxis(pos, vel, centralGM);
            currentPe = OrbitMath.GetPeriapsisDistance(currentSMA, currentE);
            currentAp = OrbitMath.GetApoapsisDistance(currentSMA, currentE);
            currentInclination = OrbitMath.GetInclination(pos, vel);

            // 当前轨道的点列（中心天体参考系 → 转换到地心系）
            bool usePerturbed = CurrentPreviewMode == PreviewMode.Perturbed && group.CelestialBodyId >= 0;

            List<DVector3> currentPoints;
            if (usePerturbed)
                currentPoints = GeneratePerturbedPreviewPoints(group.localPosition, group.localVelocity, PerturbedStepCount, PerturbedStepSeconds);
            else
                currentPoints = OrbitMath.GetOrbitPoints(group.localPosition, group.localVelocity, 60, centralGM);
            if (currentPoints.Count > 0)
            {
                var geocentricPoints = currentPoints.ConvertAll(p => p + centralOffset);
                var seg = new OrbitSegment
                {
                    pointsMeters = geocentricPoints,
                    pointsUnity = geocentricPoints.ConvertAll(p => (Vector3)(p * toUnity)),
                    isPostManeuver = false,
                    color = new Color(0.3f, 0.8f, 1f, 0.8f),
                    label = "Current"
                };
                orbitSegments.Add(seg);

                // 机动节点位置（沿当前轨道传播 maneuverTimeDeltaSeconds）
                DVector3 nodePos = OrbitMath.GetPositionAtTimeDelta(pos, vel, maneuverTimeDeltaSeconds, centralGM);
                DVector3 nodeVel = OrbitMath.GetVelocityAtTimeDelta(pos, vel, maneuverTimeDeltaSeconds, centralGM);
                seg.nodePositionMeters = nodePos;

                // 机动节点处的中心天体相对坐标（用正确的 GM 传播）
                  PropagateLocalOrbit(group.localPosition, group.localVelocity, maneuverTimeDeltaSeconds, centralGM,
                      out DVector3 nodeLocalPos, out DVector3 nodeLocalVel);

                // 读取滑块 dV，用节点处的中心天体相对坐标计算 R/T/N 基向量
                DVector3 dV = OrbitMath.GetWorldDeltaV(nodeLocalPos, nodeLocalVel, radialDV, tangentialDV, normalDV);
                float dVMag = Mathf.Sqrt(radialDV * radialDV + tangentialDV * tangentialDV + normalDV * normalDV);
                dVTotal = dVMag;

                // 机动后速度 = 节点处速度 + dV
                DVector3 postBurnVel = nodeVel + dV;

                // 计算机动后轨道要素
                double postA = OrbitMath.SemiMajorAxis(nodePos, postBurnVel, centralGM);
                if (postA > 0 && !double.IsInfinity(postA))
                {
                    OrbitMath.GetEccentricity(nodePos, postBurnVel, out _, out double postE, centralGM);
                    postPe = OrbitMath.GetPeriapsisDistance(postA, postE);
                    postAp = OrbitMath.GetApoapsisDistance(postA, postE);
                }
                else
                {
                    postPe = -1;
                    postAp = -1;
                }

                // 添加机动后轨道段（从节点位置开始）
                DVector3 postBurnNodeLocalVel = nodeLocalVel + dV;
                List<DVector3> postPoints;
                if (usePerturbed)
                    postPoints = GeneratePerturbedPreviewPoints(nodeLocalPos, postBurnNodeLocalVel, PerturbedStepCount, PerturbedStepSeconds);
                else
                    postPoints = OrbitMath.GetOrbitPoints(nodeLocalPos, postBurnNodeLocalVel, 60, centralGM);
                if (postPoints.Count > 0)
                {
                    var geocentricPostPoints = postPoints.ConvertAll(p => p + centralOffset);
                    var postSeg = new OrbitSegment
                    {
                        pointsMeters = geocentricPostPoints,
                        pointsUnity = geocentricPostPoints.ConvertAll(p => (Vector3)(p * toUnity)),
                        isPostManeuver = true,
                        color = new Color(1f, 0.7f, 0.1f, 0.6f),
                        label = "PostManeuver",
                        nodePositionMeters = nodePos
                    };
                    orbitSegments.Add(postSeg);
                }
            }

            // 标记脏
            previewComputed = true;
            previewDirty = false;
        }

        /// <summary>
        /// 确认机动并加入队列，关闭规划器。
        /// </summary>
        private void ConfirmManeuver()
        {
            double physicsTickNow = TimeScaleManager.GetPhysicsTickSafe();
            int now = Find.TickManager.TicksGame;
            DVector3 pos, vel;
            if (group.IsCircularOrbit)
            {
                pos = group.GetCircularPositionAtTick(now);
                vel = group.CurrentOrbit.velocity;
            }
            else
            {
                pos = group.CurrentOrbit.position;
                vel = group.CurrentOrbit.velocity;
            }

            // 保存 OrbitKeeping 状态（ExitCircularOrbit 会清空白名单），确保执行时仍保持
            bool hadOK = group.HasOrbitKeeping;
            HashSet<int> savedOK = group.orbitKeepingWhitelist != null
                ? new HashSet<int>(group.orbitKeepingWhitelist) : null;

            if (group.IsCircularOrbit)
                group.ExitCircularOrbit();

            if (hadOK)
            {
                group.HasOrbitKeeping = true;
                group.orbitKeepingWhitelist = savedOK;
            }

            // R/T/N 基向量用中心天体相对坐标（group.localPosition/Velocity 由 SpaceGameComp 每帧同步）
            DVector3 dV = OrbitMath.GetWorldDeltaV(group.localPosition, group.localVelocity, radialDV, tangentialDV, normalDV);
            double radDV, tanDV, normDV;
            OrbitMath.WorldDeltaVToComponents(group.localPosition, group.localVelocity, dV, out radDV, out tanDV, out normDV);

            Log.Message(string.Format("[ManeuverPlanner] Group {0} plan localFrame: bodyId={1} |pos|={2:F0}km |vel|={3:F1} m/s |dV|={4:F1} m/s",
                group.GroupId, group.CelestialBodyId, group.localPosition.magnitude / 1000.0, group.localVelocity.magnitude, dV.magnitude));

            // 为预览缓存生成中心天体参考系的轨道点
            double centralGM;
            DVector3 centralOffset;
            centralGM = spaceGame.GetBodyGM(group.CelestialBodyId);
            centralOffset = plannedCentralOffset;
            var currentLocalPts = OrbitMath.GetOrbitPoints(group.localPosition, group.localVelocity, 60, centralGM);
            var postLocalPts = OrbitMath.GetPostManeuverOrbitPoints(group.localPosition, group.localVelocity, dV, 60, centralGM);
            var currentOrbitPts = currentLocalPts.ConvertAll(p => p + centralOffset);
            var postOrbitPts = postLocalPts.ConvertAll(p => p + centralOffset);

            var maneuver = new SpaceEntityGroup.PlannedManeuver
            {
                radialDV = (float)radDV,
                tangentialDV = (float)tanDV,
                normalDV = (float)normDV,
                executeTick = (int)Math.Round(physicsTickNow + maneuverTimeDeltaSeconds),
                active = true,
                previewCurrentOrbit = currentOrbitPts.ConvertAll(p => (Vector3)p),
                previewNodePosition = pos,
                previewPostOrbit = postOrbitPts.ConvertAll(p => (Vector3)p)
            };

            spaceGame.QueueGroupManeuver(group.GroupId, maneuver);
            Log.Message(string.Format("[ManeuverPlanner] Confirmed maneuver for Group {0}: " +
                "R={1:F1} T={2:F1} N={3:F1} dt={4:F0}s",
                group.GroupId, radDV, tanDV, normDV, maneuverTimeDeltaSeconds));

            Close();
        }

        public override void Close(bool doCloseSound = true)
        {
            ActivePlanner = null;
            CurrentPreviewMode = PreviewMode.Kepler; // 关闭时重置为开普勒
            base.Close(doCloseSound);
        }
    }
}
