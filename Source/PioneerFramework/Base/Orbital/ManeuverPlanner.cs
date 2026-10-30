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
        private float radialDV ;
        private float tangentialDV ;
        private float normalDV ;
        /// <summary>预测时间（秒），用于标记轨迹上的未来位置和同系统卫星位置。</summary>
        private float predictionTimeSeconds = PredictionTimeDefault;

        private List<OrbitSegment> orbitSegments = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        private double currentPe;
        private double currentAp;
        private double currentInclination;
        private double postPe;
        private double postAp;
        private double dVTotal;

        private bool previewComputed ;
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
            public List<DVector3> pointsMeters = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）;       // 点列（米）
            public List<Vector3> pointsUnity = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）;        // 点列（Unity 单位）
            public bool isPostManeuver;
            public DVector3? nodePositionMeters;
            public Color color;
            public string label = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        }

        // ── 静态访问 (供 SpaceGroupVisualManager 绘图用) ──

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
                        // ⚠ **时钟基准（U-12，2026-10-01 修）**：天体位置是 **physicsTick** 的函数（同文件 `:367/:416/:459` 三处的同一条教训），
                        //   这里原写 `Find.TickManager.TicksGame` —— 两者相差一个**随切档/暂停而变的常量偏移**
                        //   ⇒ 冻结预览参考系会取到**错误时刻**的天体偏移（× 天体速度 = 该帧的位移）。
                        //   单独一笔修（不是"顺手改"）：改用 physicsTick 时间轴，与其余三处同基准。
                        plannedCentralOffset = group.GetCentralBodyOffset(TimeScaleManager.GetPhysicsTickSafe()).v;
                }
            }

            forcePause = true;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            draggable = true;

            RebuildOrbitPreview();
        }

        public override Vector2 InitialSize => new(WindowWidth, WindowHeight);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 30f),
                $"机动规划 - Group {group.GroupId}");
            Text.Font = GameFont.Small;

            float y = 35f;

            // 时间滑块
            Widgets.Label(new Rect(0f, y, LabelWidth, SliderHeight),
                $"机动时间: {maneuverTimeDeltaSeconds:F0}s");
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
                $"预测 t: {predictionTimeSeconds:F0}s");
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
                $"径向 (R): {radialDV:F0} m/s");
            y += SliderHeight;
            float newRadial = Widgets.HorizontalSlider(
                new Rect(0f, y, inRect.width, SliderHeight),
                radialDV, -SliderRange, SliderRange, false, null, null, null, 1f);
            if (Mathf.Abs(newRadial - radialDV) > 0.1f) { radialDV = newRadial; previewDirty = true; }
            y += SliderHeight + 4f;

            // 切向 dV
            Widgets.Label(new Rect(0f, y, LabelWidth, SliderHeight),
                $"切向 (T): {tangentialDV:F0} m/s");
            y += SliderHeight;
            float newTangential = Widgets.HorizontalSlider(
                new Rect(0f, y, inRect.width, SliderHeight),
                tangentialDV, -SliderRange, SliderRange, false, null, null, null, 1f);
            if (Mathf.Abs(newTangential - tangentialDV) > 0.1f) { tangentialDV = newTangential; previewDirty = true; }
            y += SliderHeight + 4f;

            // 法向 dV
            Widgets.Label(new Rect(0f, y, LabelWidth, SliderHeight),
                $"法向 (N): {normalDV:F0} m/s");
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
                $"当前: Pe={currentPe / 1000.0:F0}km Ap={currentAp / 1000.0:F0}km 倾角={currentInclination * Mathf.Rad2Deg:F1}°");
            y += LabelHeight;
            Widgets.Label(new Rect(0f, y, inRect.width, LabelHeight),
                $"机动后: Pe={postPe / 1000.0:F0}km Ap={postAp / 1000.0:F0}km |dV|={dVTotal:F1} m/s");
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
            string[] labels = ["-100", "-10", "+10", "+100"];
            float[] values = [-100f, -10f, 10f, 100f];
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
        private void PropagateOrbitBody(DVector3 posBody, DVector3 velBody, double dt, double gm,
            out DVector3 outPos, out DVector3 outVel)
        {
            double r = posBody.magnitude;
            double v2 = velBody.sqrMagnitude;
            double eps = 0.5 * v2 - gm / r;

            if (Math.Abs(eps) < 1e-10 || eps >= 0.0) // 抛物线/双曲
            {
                outPos = posBody;
                outVel = velBody;
                return;
            }

            double a = -gm / (2.0 * eps);
            double e = (DVector3.Cross(velBody, DVector3.Cross(posBody, velBody)) / gm - posBody / r).magnitude;

            if (e < 0.001 || Math.Abs(e) < 0.001) // 圆轨
            {
                double omega = Math.Sqrt(gm / (r * r * r));
                double angle = omega * dt;
                DVector3 normal = DVector3.Cross(posBody, velBody).normalized;
                // Rodrigues 旋转公式
                DVector3 rotated = posBody * Math.Cos(angle) + DVector3.Cross(normal, posBody) * Math.Sin(angle);
                DVector3 velRotated = velBody * Math.Cos(angle) + DVector3.Cross(normal, velBody) * Math.Sin(angle);
                outPos = rotated;
                outVel = velRotated;
                return;
            }

            // 椭圆：回落至 OrbitMath（按标准路径也可近似接受地心 GM）
            outPos = OrbitMath.GetPositionAtTimeDelta(posBody, velBody, dt, gm);
            outVel = OrbitMath.GetVelocityAtTimeDelta(posBody, velBody, dt, gm);
        }

        /// <summary>计算机动后 predictionTimeSeconds 时的卫星位置（系统系 Unity 坐标）。</summary>
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

            // 圆轨位置必须用 physicsTick 基准（与物理/EnterCircularOrbit 一致，TicksGame 会差常量偏移导致相位错）
            double now = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 pos, vel;
            if (group.IsCircularOrbit)
            {
                pos = group.GetCircularPositionAtTick(now);
                vel = group.GetCircularVelocityAtTick(now); // 解析速度（当前相位切线）
            }
            else
            {
                pos = group.CurrentOrbit.positionSystem.v;
                vel = group.CurrentOrbit.velocitySystem.v;
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
            PropagateOrbitBody(group.positionBody, group.velocityBody, maneuverTimeDeltaSeconds, centralGM,
                out DVector3 nodePosBody, out DVector3 nodeVelBody);
            DVector3 dV = OrbitMath.GetDeltaVSystem(nodePosBody, nodeVelBody, radialDV, tangentialDV, normalDV);

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
            // ⚠ 时刻用 **physicsTick 时间轴**（天体位置是它的函数）：原来用 `Find.TickManager.TicksGame`
            //   （真实 tick）⇒ 战斗 1/60× 流速下会预测到错误的未来。
            double futureTick = TimeScaleManager.GetPhysicsTickSafe() + predictionTimeSeconds;

            // 中心天体的卫星：**系统系**坐标（与 `SpaceGroupVisualManager` 叠加层同系 —— 2026-09-23 前
            // 这里给的是绝对系，标记与叠加层差 2,400 Unity 单位）
            for (int i = 0; i < primaryBody.moonBodies.Count; i++)
            {
                var moon = primaryBody.moonBodies[i];
                Vector3 moonPos = (Vector3)(ViewFrame.BodySystemPosition(moon.bodyId, futureTick) * toUnity);
                result.Add(moonPos);
            }

            return result;
        }

        /// <summary>
        /// 生成多体数值传播的预览点（扰动弹道）。
        /// 使用大步长三阶泰勒积分，比开普勒预测更符合实际轨迹。
        /// 返回中心天体参考系中的位置列表。游戏暂停时天体位置固定。
        /// **引力取该行星系全部天体**（A0）：中心天体局部系里，外部天体的贡献取
        /// 「差分形式」（= 该天体在弹位处与在局部系原点处之差），因此在局部系里既保住了
        /// 计划轨道、又带上正确的潮汐摄动——不再需要对父天体做手工"全量拉力"叠加
        /// （那会把均匀部分重复计入，在月球系是 ~224km 量级的错误）。
        /// </summary>
        private List<DVector3> GeneratePerturbedPreviewPoints(DVector3 posBody, DVector3 velBody, int numSteps, double stepSec)
        {
            var points = new List<DVector3>(numSteps);
            points.Add(posBody);

            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (celestialMgr == null) return points;

            var primaryBody = celestialMgr.GetBodyById(group.CelestialBodyId);
            if (primaryBody == null) return points;

            if (previewSourcesScratch == null) previewSourcesScratch = new List<GravitySource>();

            DVector3 pos = posBody;
            DVector3 vel = velBody;

            for (int i = 0; i < numSteps; i++)
            {
                // 天体位置用 physicsTick 基准（与物理推进/机动队列同基准；暂停时两者一致）
                double sampleTick = TimeScaleManager.GetPhysicsTickSafe() + i * stepSec;
                FillLocalFrameSources(celestialMgr, sampleTick);

                NBodyIntegrator.DifferentialAccelJerk(
                    pos, vel, DVector3.zero, DVector3.zero, previewSourcesScratch,
                    out DVector3 acc, out DVector3 jerk);

                NBodyIntegrator.ThirdOrderTaylorStep(
                    pos, vel, acc, jerk, stepSec,
                    out DVector3 newPos, out DVector3 newVel);

                pos = newPos;
                vel = newVel;
                points.Add(pos);
            }

            return points;
        }

        private List<GravitySource> previewSourcesScratch = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        private List<GravitySource> systemSourcesScratch = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        /// <summary>
        /// 把该行星系全部天体填成**中心天体局部系**的源（位置/速度均减去中心天体的系统系位置/速度）。
        /// 局部系原点 = 中心天体 → 其自身位置为零、速度为零。
        /// </summary>
        private void FillLocalFrameSources(CelestialBodyManager mgr, double tick)
        {
            if (systemSourcesScratch == null) systemSourcesScratch = new List<GravitySource>();
            if (previewSourcesScratch == null) previewSourcesScratch = new List<GravitySource>();

            mgr.CollectSystemSources(group.CelestialBodyId, tick, systemSourcesScratch);
            previewSourcesScratch.Clear();

            DVector3 originPos = DVector3.zero, originVel = DVector3.zero;
            for (int i = 0; i < systemSourcesScratch.Count; i++)
            {
                if (systemSourcesScratch[i].bodyId != group.CelestialBodyId) continue;
                originPos = systemSourcesScratch[i].position;
                originVel = systemSourcesScratch[i].velocity;
                break;
            }

            for (int i = 0; i < systemSourcesScratch.Count; i++)
            {
                var s = systemSourcesScratch[i];
                previewSourcesScratch.Add(new GravitySource(s.position - originPos, s.velocity - originVel, s.mu, s.bodyId));
            }
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

            // 获取当前轨道状态，支持圆轨+标准模式（圆轨用 physicsTick 基准 + 解析速度）
            double now = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 pos, vel;
            if (group.IsCircularOrbit)
            {
                pos = group.GetCircularPositionAtTick(now);
                vel = group.GetCircularVelocityAtTick(now); // 解析速度（当前相位切线）
            }
            else
            {
                pos = group.CurrentOrbit.positionSystem.v;
                vel = group.CurrentOrbit.velocitySystem.v;
            }

            // 获取中心天体的 GM 和世界坐标偏移（用于正确绘制轨道预览）
            double centralGM;
            DVector3 centralOffset;
            centralGM = spaceGame.GetBodyGM(group.CelestialBodyId);
            centralOffset = plannedCentralOffset;

            // 获取当前轨道要素（系统系）
            OrbitMath.GetEccentricity(pos, vel, out _, out double currentE, centralGM);
            double currentSMA = OrbitMath.SemiMajorAxis(pos, vel, centralGM);
            currentPe = OrbitMath.GetPeriapsisDistance(currentSMA, currentE);
            currentAp = OrbitMath.GetApoapsisDistance(currentSMA, currentE);
            // 【V2 收敛 2026-09-18】原先 `OrbitMath.GetInclination(pos, vel)` —— 它默认参考法向是
            // `DVector3.up`（游戏世界 +Y）。改走 `OrbitView.FromGameStateVectors`：那次换系的目的正是
            // 把游戏系 +Y 映射成适配层 +z ⇒ `.inclination` 得到的**正好**是"相对游戏世界赤道的倾角"，
            // 与旧口径逐字一致（不是"换了个参考系"，是"把同一个参考系说清楚"）。
            currentInclination = OrbitView.FromGameStateVectors(
                pos, vel, centralGM, TimeScaleManager.GetPhysicsTickSafe(), DVector3.up, 0.0).inclination;

            // 当前轨道的点列（中心天体参考系 → 转换到系统系）
            bool usePerturbed = CurrentPreviewMode == PreviewMode.Perturbed && group.CelestialBodyId >= 0;

            List<DVector3> currentPoints;
            if (usePerturbed)
                currentPoints = GeneratePerturbedPreviewPoints(group.positionBody, group.velocityBody, PerturbedStepCount, PerturbedStepSeconds);
            else
                currentPoints = OrbitMath.GetOrbitPoints(group.positionBody, group.velocityBody, 60, centralGM);
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
                  PropagateOrbitBody(group.positionBody, group.velocityBody, maneuverTimeDeltaSeconds, centralGM,
                      out DVector3 nodePosBody, out DVector3 nodeVelBody);

                // 读取滑块 dV，用节点处的中心天体相对坐标计算 R/T/N 基向量
                DVector3 dV = OrbitMath.GetDeltaVSystem(nodePosBody, nodeVelBody, radialDV, tangentialDV, normalDV);
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
                DVector3 postBurnNodeVelBody = nodeVelBody + dV;
                List<DVector3> postPoints;
                if (usePerturbed)
                    postPoints = GeneratePerturbedPreviewPoints(nodePosBody, postBurnNodeVelBody, PerturbedStepCount, PerturbedStepSeconds);
                else
                    postPoints = OrbitMath.GetOrbitPoints(nodePosBody, postBurnNodeVelBody, 60, centralGM);
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
            // 圆轨位置/速度用 physicsTick 基准 + 解析速度（与物理一致）
            double physicsTickNow = TimeScaleManager.GetPhysicsTickSafe();
            int now = Find.TickManager.TicksGame;
            DVector3 pos, vel;
            if (group.IsCircularOrbit)
            {
                pos = group.GetCircularPositionAtTick(physicsTickNow);
                vel = group.GetCircularVelocityAtTick(physicsTickNow); // 解析速度（当前相位切线）
            }
            else
            {
                pos = group.CurrentOrbit.positionSystem.v;
                vel = group.CurrentOrbit.velocitySystem.v;
            }

            // 保存 OrbitKeeping 状态（ExitCircularOrbit 会清空白名单），确保执行时仍保持
            bool hadOK = group.HasOrbitKeeping;
            HashSet<int>? savedOK = group.orbitKeepingWhitelist != null
                ? new HashSet<int>(group.orbitKeepingWhitelist) : null;

            if (group.IsCircularOrbit)
                group.ExitCircularOrbit();

            if (hadOK)
            {
                group.HasOrbitKeeping = true;
                group.orbitKeepingWhitelist = savedOK;
            }

            // R/T/N 基向量用中心天体相对坐标（group.positionBody/Velocity 由 SpaceGameComp 每帧同步）
            DVector3 dV = OrbitMath.GetDeltaVSystem(group.positionBody, group.velocityBody, radialDV, tangentialDV, normalDV);
            double radDV, tanDV, normDV;
            OrbitMath.DeltaVSystemToComponents(group.positionBody, group.velocityBody, dV, out radDV, out tanDV, out normDV);

            Log.Message($"[ManeuverPlanner] Group {group.GroupId} plan localFrame: bodyId={group.CelestialBodyId} |pos|={group.positionBody.magnitude / 1000.0:F0}km |vel|={group.velocityBody.magnitude:F1} m/s |dV|={dV.magnitude:F1} m/s");

            // 为预览缓存生成中心天体参考系的轨道点
            double centralGM;
            DVector3 centralOffset;
            centralGM = spaceGame.GetBodyGM(group.CelestialBodyId);
            centralOffset = plannedCentralOffset;
            var currentLocalPts = OrbitMath.GetOrbitPoints(group.positionBody, group.velocityBody, 60, centralGM);
            var postLocalPts = OrbitMath.GetPostManeuverOrbitPoints(group.positionBody, group.velocityBody, dV, 60, centralGM);
            var currentOrbitPts = currentLocalPts.ConvertAll(p => p + centralOffset);
            var postOrbitPts = postLocalPts.ConvertAll(p => p + centralOffset);

            // 2026-10-27：**走唯一入队口** `OrbitAdapter.TryQueuePlan`（此前手工拼 PlannedManeuver
            // ⇒ 手排计划不带 plannedDvBody，执行侧只能退回分量重分解）。
            var mgr = CelestialBodyManager.GetCached() ?? Current.Game?.GetComponent<CelestialBodyManager>();
            if (!OrbitAdapter.TryToOrbitView(group, mgr, physicsTickNow, out OrbitView ov))
            {
                Messages.Message("无法获取当前轨道，机动未排入。", MessageTypeDefOf.RejectInput, false);
                return;
            }
            var nodes = new List<PlanNode>
            {
                new PlanNode(dV, physicsTickNow + maneuverTimeDeltaSeconds, PlanOwner.Manual),
            };
            if (!OrbitAdapter.TryQueuePlan(group, ov, nodes, out string queueReason, out _, null,
                m =>
                {
                    m.previewCurrentOrbit = currentOrbitPts.ConvertAll(p => (Vector3)p);
                    m.previewNodePosition = pos;
                    m.previewPostOrbit = postOrbitPts.ConvertAll(p => (Vector3)p);
                    return m;
                }))
            {
                Messages.Message($"机动未排入：{queueReason}", MessageTypeDefOf.RejectInput, false);
                return;
            }
            Log.Message($"[ManeuverPlanner] Confirmed maneuver for Group {group.GroupId}: R={radDV:F1} T={tanDV:F1} N={normDV:F1} dt={maneuverTimeDeltaSeconds:F0}s");

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
