using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;

namespace PioneerFramework
{
    /// <summary>
    /// 转移到指定的目标圆轨（给定轨道半径和法向量）。
    /// 使用纯霍曼转移 + 平面变化解析公式，不需要 Lambert 求解。
    /// 转移时间 = 霍曼半周期，即时出发。
    /// </summary>
    public class OrbitTransferPlanner : Window
    {
        private SpaceEntityGroup selfGroup;
        private SpaceGameComp spaceGame;

        // 目标轨道参数
        private string targetRadiusKmText = "26315";
        private string inclinationDegText = "0";
        private string lanDegText = "0";

        // 法向量分量输入（替代 inc/LAN）
        private string normalXText = "0";
        private string normalYText = "0";
        private string normalZText = "1";
        private bool useNormalVectorInput;

        private double targetRadiusMeters = -1;
        private DVector3 targetNormal;

        private bool parametersValid;
        private bool solutionValid;
        private string resultMsg = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        private double departureDV;
        private double arrivalDV;
        private double totalDV;
        private double transferTimeSeconds;

        // 存储计算中间量用于确认
        private double tanDV1;
        private double tanDV2, normDV2;
        private int departureTick;
        private int arrivalTick;
        private DVector3 departurePosSystem;  // 世界坐标
        private DVector3 departureVelSystem;

        private const float ButtonHeight = 30f;
        private const float LabelHeight = 22f;
        private const float FieldWidth = 100f;
        private const float WindowWidth = 420f;
        private const float WindowHeight = 370f;

        public OrbitTransferPlanner(SpaceEntityGroup self, SpaceGameComp gameComp)
        {
            selfGroup = self;
            spaceGame = gameComp;

            forcePause = true;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            draggable = true;
            resizeable = true;
        }

        public override Vector2 InitialSize => new(WindowWidth, WindowHeight);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 30f),
                $"转移到圆轨 - Group {selfGroup.GroupId}");
            Text.Font = GameFont.Small;

            float y = 35f;

            // ── 目标轨道参数输入 ──
            DrawInputRow(ref y, inRect.width, "目标轨道半径 (km):", ref targetRadiusKmText);

            if (Widgets.ButtonText(new Rect(0f, y, inRect.width, 22f),
                useNormalVectorInput ? "[法向量 (X, Y, Z)]  点击切换 inc/LAN" : "[倾角 + LAN]  点击切换法向量"))
            {
                useNormalVectorInput = !useNormalVectorInput;
            }
            y += 26f;

            if (useNormalVectorInput)
            {
                DrawInputRow(ref y, inRect.width, "轨道法向量 X:", ref normalXText);
                DrawInputRow(ref y, inRect.width, "轨道法向量 Y:", ref normalYText);
                DrawInputRow(ref y, inRect.width, "轨道法向量 Z:", ref normalZText);
            }
            else
            {
                DrawInputRow(ref y, inRect.width, "倾角 (°):", ref inclinationDegText);
                DrawInputRow(ref y, inRect.width, "升交点经度 LAN (°):", ref lanDegText);
            }
            y += 4f;

            // 解析输入
            parametersValid = ParseInput();
            if (!parametersValid)
            {
                Widgets.Label(new Rect(0f, y, inRect.width, LabelHeight), "请输入有效的轨道参数");
                return;
            }

            if (Widgets.ButtonText(new Rect(0f, y, inRect.width, ButtonHeight), "计算转移轨道"))
            {
                ComputeHohmannTransfer();
            }
            y += ButtonHeight + 2f;

            if (!string.IsNullOrEmpty(resultMsg))
            {
                Widgets.Label(new Rect(0f, y, inRect.width, LabelHeight), resultMsg);
                y += LabelHeight;
            }

            if (solutionValid)
            {
                Widgets.Label(new Rect(0f, y, inRect.width, LabelHeight),
                    $"出发 dV: {departureDV:F1} m/s");
                y += LabelHeight;
                Widgets.Label(new Rect(0f, y, inRect.width, LabelHeight),
                    $"到达圆化 dV: {arrivalDV:F1} m/s");
                y += LabelHeight;
                Widgets.Label(new Rect(0f, y, inRect.width, LabelHeight),
                    $"总 dV: {totalDV:F1} m/s | 转移: {transferTimeSeconds:F0}s");
                y += LabelHeight + 4f;

                if (Widgets.ButtonText(new Rect(0f, y, inRect.width, ButtonHeight), "确认转移"))
                {
                    ConfirmTransfer();
                }
                y += ButtonHeight + 4f;
            }

            if (Widgets.ButtonText(new Rect(0f, y, inRect.width, ButtonHeight), "关闭"))
            {
                Close();
            }
        }

        private void DrawInputRow(ref float y, float width, string label, ref string text)
        {
            Widgets.Label(new Rect(0f, y, width - FieldWidth - 10f, LabelHeight), label);
            text = Widgets.TextField(new Rect(width - FieldWidth, y, FieldWidth, 22f), text);
            y += LabelHeight + 2f;
        }

        private bool ParseInput()
        {
            if (!double.TryParse(targetRadiusKmText, out double rKm) || rKm <= 0)
                return false;
            targetRadiusMeters = rKm * 1000.0;

            if (useNormalVectorInput)
            {
                if (!double.TryParse(normalXText, out double nx) ||
                    !double.TryParse(normalYText, out double ny) ||
                    !double.TryParse(normalZText, out double nz))
                    return false;
                targetNormal = new DVector3(nx, ny, nz);
                if (targetNormal.sqrMagnitude < 1e-12) return false;
                targetNormal = targetNormal.normalized;
                return true;
            }
            else
            {
                if (!double.TryParse(inclinationDegText, out double incDeg) ||
                    !double.TryParse(lanDegText, out double lanDeg))
                    return false;
                double incRad = incDeg * Mathf.Deg2Rad;
                double lanRad = lanDeg * Mathf.Deg2Rad;
                targetNormal = new DVector3(
                    System.Math.Sin(lanRad) * System.Math.Sin(incRad),
                    -System.Math.Cos(lanRad) * System.Math.Sin(incRad),
                    System.Math.Cos(incRad)
                ).normalized;
                return true;
            }
        }

        /// <summary>
        /// 获取自机当前的世界坐标系位置和速度。
        /// </summary>
        private (DVector3 pos, DVector3 vel) GetCurrentWorldState()
        {
            int nowTick = (int)Math.Round(TimeScaleManager.GetPhysicsTickSafe());

            if (selfGroup.IsCircularOrbit)
            {
                DVector3 pos = selfGroup.GetCircularPositionAtTick(nowTick);
                // 解析速度（当前相位切线），不能裸读 CurrentOrbit.velocity（会停在入轨瞬间）
                return (pos, selfGroup.GetCircularVelocityAtTick(nowTick));
            }

            return (selfGroup.CurrentOrbit.positionSystem.v, selfGroup.CurrentOrbit.velocitySystem.v);
        }

        /// <summary>
        /// 获取中心天体在给定 tick 时的世界坐标偏移和速度。
        /// 地球 = 零；月球 = 月心的位置+速度。
        /// </summary>
        private (DVector3 offset, DVector3 vel) GetCentralBodyOffsetAndVel(int tick)
        {
            int bodyId = selfGroup.CelestialBodyId;
            if (bodyId < 0) return (DVector3.zero, DVector3.zero);
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return (DVector3.zero, DVector3.zero);
            var body = mgr.GetBodyById(bodyId);
            if (body == null || body.bodyType == CelestialBodyType.Planet)
                return (DVector3.zero, DVector3.zero);
            return (mgr.GetBodyPositionParent(bodyId, tick), mgr.GetBodyVelocityParent(bodyId, tick));
        }

        /// <summary>
        /// 使用霍曼转移解析公式计算 dV。不依赖 Lambert。
        /// </summary>
        private void ComputeHohmannTransfer()
        {
            solutionValid = false;
            resultMsg = "";

            double mu = spaceGame.GetBodyGM(selfGroup.CelestialBodyId);

            // 获取世界坐标状态，然后转换到中心天体坐标系
            var (r1System, v1System) = GetCurrentWorldState();
            var (bodyOff, bodyVel) = GetCentralBodyOffsetAndVel((int)Math.Round(TimeScaleManager.GetPhysicsTickSafe()));
            DVector3 r1 = r1System - bodyOff;   // 中心天体相对坐标
            DVector3 v1 = v1System - bodyVel;

            double r1Mag = r1.magnitude;
            double r2Mag = targetRadiusMeters;
            DVector3 h1 = DVector3.Cross(r1, v1).normalized;

            // 霍曼转移半长轴
            double a = (r1Mag + r2Mag) * 0.5;

            // 转移轨道速度
            double vp = System.Math.Sqrt(mu * (2.0 / r1Mag - 1.0 / a)); // 近地点速度
            double va = System.Math.Sqrt(mu * (2.0 / r2Mag - 1.0 / a)); // 远地点速度

            // 当前圆轨速度
            double vCirc1 = System.Math.Sqrt(mu / r1Mag);
            double vCirc2 = System.Math.Sqrt(mu / r2Mag);

            // 切向分量
            double dvTan1 = vp - vCirc1;

            // 轨道平面夹角
            DVector3 h2 = targetNormal;
            double cosDeltaI = DVector3.Dot(h1, h2);
            cosDeltaI = Mathf.Clamp((float)cosDeltaI, -1f, 1f);
            double deltaI = System.Math.Acos(cosDeltaI);

            // 到达机动：组合圆化 + 平面变化（在远地点做平面变化更省燃料）
            // dvTan₂ = vCirc₂·cosΔi − va      （切向：目标圆轨在旧平面的投影）
            // dvNorm = vCirc₂·sinΔi            （法向：抬升轨道平面）
            double dvTan2 = vCirc2 * cosDeltaI - va;
            double dvNormAtArrival = vCirc2 * System.Math.Sin(deltaI);
            double combinedArrivalDV = System.Math.Sqrt(dvTan2 * dvTan2 + dvNormAtArrival * dvNormAtArrival);

            // 法向符号：根据 h₁×h₂ 在径向方向的分量确定上抬/下压
            DVector3 hCross = DVector3.Cross(h1, h2);
            double normSign = hCross.magnitude > 1e-12
                ? System.Math.Sign(DVector3.Dot(hCross, r1.normalized))
                : 0.0;

            // 存储结果
            tanDV1 = dvTan1;
            // 出发不做平面变化，留在到达组合圆化+平面变化

            tanDV2 = dvTan2;
            normDV2 = dvNormAtArrival * normSign;

            departureDV = System.Math.Abs(dvTan1);
            arrivalDV = combinedArrivalDV;
            totalDV = departureDV + arrivalDV;

            // 转移时间
            transferTimeSeconds = System.Math.PI * System.Math.Sqrt(a * a * a / mu);

            departurePosSystem = r1System;      // 世界坐标（供预览）
            departureVelSystem = v1System;
            double physicsTickNow = TimeScaleManager.GetPhysicsTickSafe();
            departureTick = (int)Math.Round(physicsTickNow);
            arrivalTick = departureTick + UnityEngine.Mathf.RoundToInt((float)transferTimeSeconds);

            solutionValid = true;

            if (useNormalVectorInput)
            {
                resultMsg = $"霍曼转移: 总dV={totalDV:F1} m/s, 转移={transferTimeSeconds:F0}s";
            }
            else
            {
                resultMsg = $"霍曼转移: 总dV={totalDV:F1} m/s, 转移={transferTimeSeconds:F0}s, Δi={deltaI * Mathf.Rad2Deg:F1}°";
            }
        }

        private void ConfirmTransfer()
        {
            if (!solutionValid) return;

            // 出发机动：纯切向霍曼加速
            var departureManeuver = new SpaceEntityGroup.PlannedManeuver
            {
                radialDV = 0f,
                tangentialDV = (float)tanDV1,
                normalDV = 0f,
                executeTick = departureTick,
                active = true,
                previewNodePosition = departurePosSystem
            };
            spaceGame.QueueGroupManeuver(selfGroup.GroupId, departureManeuver);

            // 到达机动：圆化 + 平面变化（如果 Δi > 0）
            var arrivalManeuver = new SpaceEntityGroup.PlannedManeuver
            {
                radialDV = 0f,
                tangentialDV = (float)tanDV2,
                normalDV = (float)normDV2,
                executeTick = arrivalTick,
                active = true,
                previewNodePosition = DVector3.zero // 到达位置未知，留空
            };
            spaceGame.QueueGroupManeuver(selfGroup.GroupId, arrivalManeuver);

            // 启用轨道保持
            if (selfGroup.CelestialBodyId >= 0)
            {
                selfGroup.EnterOrbitKeeping(new HashSet<int> { selfGroup.CelestialBodyId });
            }

            Log.Message($"[OrbitTransfer] Group {selfGroup.GroupId} Hohmann: r={targetRadiusMeters / 1000.0:F0}km dv1={departureDV:F1} dv2={arrivalDV:F1} total={totalDV:F1} dt={transferTimeSeconds:F0}s");

            Close();
        }
    }
}
