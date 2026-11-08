// ⛔【NOSTEPANYMORE】表现层文件（PFView 引擎喷焰）。本文件不读写任何后端状态，
//    只消费 SpaceEntityGroup / SpacePart / EngineComponentData 的公开只读字段。
//    若后续需要改 Base/** 等后端文件，按 BACKEND-LOCK.md 停机请示，不得顺手改。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// **引擎喷焰的角色**（表现层口径，不是物理概念）：
    /// Main = 主推进网络；Rcs = 全矢量 RCS 网络。None = 本帧不该画。
    /// </summary>
    internal enum EnginePlumeRole
    {
        None = 0,
        Main = 1,
        Rcs = 2,
    }

    /// <summary>
    /// 一台引擎件的**纯数据输入**（无 Verse/Unity 依赖）。
    /// 由 PFView 适配层从 SpacePart + EngineComponentData 逐件填充；
    /// 这样纯核可以链接进 PioneerFramework.Pure.Tests 做 headless 判据（锚点/筛选/LOD/闪烁）。
    /// </summary>
    internal readonly struct EnginePlumePartInput
    {
        public readonly int partId;
        public readonly bool hasEngine;
        public readonly bool destroyed;
        public readonly int stageId;
        public readonly bool isRcs;
        public readonly DVector3 positionShip;
        public readonly DVector3 boundsShip;
        public readonly DQuaternion rotationShip;
        /// <summary>零件局部系的**排气方向**（= ThrustAxis.ShipVector()；不是推力方向）。</summary>
        public readonly DVector3 exhaustAxisLocal;
        public readonly float exhaustVelocity;
        public readonly float massFlowRate;
        public readonly string? propellantDefName;

        public EnginePlumePartInput(int partId, bool hasEngine, bool destroyed, int stageId, bool isRcs,
            DVector3 positionShip, DVector3 boundsShip, DQuaternion rotationShip, DVector3 exhaustAxisLocal,
            float exhaustVelocity, float massFlowRate, string? propellantDefName)
        {
            this.partId = partId;
            this.hasEngine = hasEngine;
            this.destroyed = destroyed;
            this.stageId = stageId;
            this.isRcs = isRcs;
            this.positionShip = positionShip;
            this.boundsShip = boundsShip;
            this.rotationShip = rotationShip;
            this.exhaustAxisLocal = exhaustAxisLocal;
            this.exhaustVelocity = exhaustVelocity;
            this.massFlowRate = massFlowRate;
            this.propellantDefName = propellantDefName;
        }
    }

    /// <summary>
    /// 一台引擎件的**喷口解算结果**（船体系，米/单位向量）。
    /// nozzleShip = 喷口中心（件中心沿排气方向外移到 AABB 表面）；
    /// exhaustShip = 船体系排气方向（已含件姿态 rotationShip）。
    /// </summary>
    internal readonly struct EnginePlumeSpec
    {
        public readonly int partId;
        public readonly DVector3 nozzleShip;
        public readonly DVector3 exhaustShip;
        public readonly double nozzleRadiusMeters;
        public readonly EnginePlumeRole role;
        public readonly int colorBucket;

        public EnginePlumeSpec(int partId, DVector3 nozzleShip, DVector3 exhaustShip,
            double nozzleRadiusMeters, EnginePlumeRole role, int colorBucket)
        {
            this.partId = partId;
            this.nozzleShip = nozzleShip;
            this.exhaustShip = exhaustShip;
            this.nozzleRadiusMeters = nozzleRadiusMeters;
            this.role = role;
            this.colorBucket = colorBucket;
        }
    }

    /// <summary>
    /// **引擎喷焰纯核**（2026-11-08，PFView 引擎视觉特效 P0/P1）。
    ///
    /// 纪律：
    /// · 只做算术与选择，不碰 UnityEngine / Verse；纯核链接进 Pure.Tests。
    /// · 不写任何后端字段；输入全部由适配层按当前帧只读采样。
    /// · 方向口径：零件链路的 thrustAxis 是**排气方向**（Back = −Z），与
    ///   SpaceEntity.GetMainThrustAxisShip 的取反口径一致（主推轴 = 反方向，火焰沿排气方向）。
    /// · 喷管几何没有后端字段（§8-B3）⇒ 用 AABB 沿排气轴的半长外推；这是表现层近似，不是物理数据。
    /// </summary>
    internal static class EnginePlumeCore
    {
        public const int BucketUnknown = 0;
        public const int BucketChemical = 1;
        public const int BucketHydrogen = 2;
        public const int BucketXenon = 3;
        public const int BucketHydrazine = 4;
        public const int BucketIntakeAir = 5;

        /// <summary>喷口最小半径（米）：再小的喷口也画得出一点光。</summary>
        public const double MinNozzleRadiusMeters = 0.15;

        /// <summary>喷口向件体外移的距离（米）：保证加性圆点不被引擎自身盒体深度挡住。</summary>
        public const double NozzleOutsetMeters = 0.05;

        // 距离 LOD（相机到舰船的米数）。与 PartRenderDist（300 km）同口径。
        public const double LodFullMeters = 5_000.0;
        public const double LodMediumMeters = 30_000.0;
        public const double LodDotMeters = 300_000.0;

        /// <summary>活动级 = 存活件里最大的 stageId（与外壳 RocketHullMesh 同一条规则）。</summary>
        public static int ResolveActiveStage(IReadOnlyList<EnginePlumePartInput> parts)
        {
            int active = 0;
            if (parts == null) return active;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.destroyed) continue;
                if (p.stageId > active) active = p.stageId;
            }
            return active;
        }

        /// <summary>
        /// 本帧该画哪一类引擎。burning 必须由适配层用
        /// HasActiveBurn && burnLastCmdDv &gt; 0 判——只有真正交付 Δv 的步才算"在推进"。
        /// </summary>
        public static EnginePlumeRole ResolveRole(bool burning, bool rcsMode, bool hovering)
        {
            if (burning) return rcsMode ? EnginePlumeRole.Rcs : EnginePlumeRole.Main;
            if (hovering) return EnginePlumeRole.Rcs;
            return EnginePlumeRole.None;
        }

        /// <summary>
        /// 逐件解算喷口。返回 false = 本件不参与本帧表现（非引擎/损毁/非活动级/角色不符/轴向退化）。
        /// </summary>
        public static bool TryBuildSpec(in EnginePlumePartInput part, int activeStage,
            EnginePlumeRole role, out EnginePlumeSpec spec)
        {
            spec = default;
            if (!part.hasEngine || part.destroyed) return false;
            if (part.stageId != activeStage) return false;
            if (role == EnginePlumeRole.Main && part.isRcs) return false;
            if (role == EnginePlumeRole.Rcs && !part.isRcs) return false;

            DVector3 axisLocal = part.exhaustAxisLocal;
            if (axisLocal.sqrMagnitude < 1e-12) return false;
            axisLocal = axisLocal.normalized;
            DVector3 axisBody = part.rotationShip == DQuaternion.identity
                ? axisLocal
                : DVector3.Rotate(axisLocal, part.rotationShip);
            if (axisBody.sqrMagnitude < 1e-12) return false;
            axisBody = axisBody.normalized;

            double hx = part.boundsShip.x * 0.5;
            double hy = part.boundsShip.y * 0.5;
            double hz = part.boundsShip.z * 0.5;
            if (hx < 0.0) hx = 0.0;
            if (hy < 0.0) hy = 0.0;
            if (hz < 0.0) hz = 0.0;

            // boundsSizeShip 是**件局部** AABB（渲染矩阵 = TRS(pos, gRot*partRot, bounds)）：
            // 支持半长必须在件局部系里算，再把"喷口偏移"整体旋到船体系。
            double ax = Math.Abs(axisLocal.x);
            double ay = Math.Abs(axisLocal.y);
            double az = Math.Abs(axisLocal.z);
            double extent = ax * hx + ay * hy + az * hz;

            // 喷口半径 = 件局部两个"径向"半长的较小者（排除排气轴自己）。
            double radius;
            if (ax >= ay && ax >= az) radius = Math.Min(hy, hz);
            else if (ay >= az) radius = Math.Min(hx, hz);
            else radius = Math.Min(hx, hy);
            radius = Math.Max(MinNozzleRadiusMeters, radius);

            // 喷口向件体外**外移**一点（不是内收）：dot 在加性/深度测试下要露出件表面，
            // 否则会被自身盒体挡住（2026-11-08 审查 P0-2）。
            DVector3 offsetLocal = axisLocal * (extent + NozzleOutsetMeters);
            DVector3 offsetShip = part.rotationShip == DQuaternion.identity
                ? offsetLocal
                : DVector3.Rotate(offsetLocal, part.rotationShip);
            DVector3 nozzle = part.positionShip + offsetShip;

            spec = new EnginePlumeSpec(part.partId, nozzle, axisBody, radius, role,
                ColorBucketFromPropellant(part.propellantDefName));
            return true;
        }

        /// <summary>工质 → 颜色桶（纯字符串判定；表现层颜色表见 EnginePlumeStyle）。</summary>
        public static int ColorBucketFromPropellant(string? defName)
        {
            if (string.IsNullOrEmpty(defName)) return BucketUnknown;
            switch (defName)
            {
                case "PF_Propellant_Chemical": return BucketChemical;
                case "PF_Propellant_Hydrogen": return BucketHydrogen;
                case "PF_Propellant_Xenon": return BucketXenon;
                case "PF_Propellant_Hydrazine": return BucketHydrazine;
                case "PF_Propellant_IntakeAir": return BucketIntakeAir;
                default: return BucketUnknown;
            }
        }

        public static bool IsIntakeAir(string? defName) => defName == "PF_Propellant_IntakeAir";

        /// <summary>角色强度：主燃 1.0；RCS/悬停更短更暗（表现层口径，不是油门）。</summary>
        public static double PlumeIntensity(EnginePlumeRole role) => role switch
        {
            EnginePlumeRole.Main => 1.0,
            EnginePlumeRole.Rcs => 0.45,
            _ => 0.0,
        };

        /// <summary>
        /// 焰心长度（米）：按喷口直径给基准，按强度缩短；大气内略长（环境压强让羽流展开）。
        /// 没有逐引擎油门 ⇒ 长度不随油门变（§8-B1），这是明确近似。
        /// </summary>
        public static double PlumeLengthMeters(double nozzleRadiusMeters, double intensity, bool inAtmosphere)
        {
            double diameter = Math.Max(MinNozzleRadiusMeters * 2.0, nozzleRadiusMeters * 2.0);
            double baseLength = Math.Clamp(diameter * 10.0, 6.0, 60.0);
            double length = baseLength * (0.55 + 0.45 * Math.Clamp(intensity, 0.0, 1.0));
            if (inAtmosphere) length *= 1.2;
            return length;
        }

        /// <summary>每台引擎本帧应画几片：3 = 喷口+焰心+外辉光；2 = 喷口+焰心；1 = 仅喷口；0 = 不画。</summary>
        public static int LodQuadCount(double distanceMeters)
        {
            if (distanceMeters < LodFullMeters) return 3;
            if (distanceMeters < LodMediumMeters) return 2;
            if (distanceMeters < LodDotMeters) return 1;
            return 0;
        }

        /// <summary>
        /// 按**投影长度**分档（喷焰是用长度表达的主要视觉）：只按喷口半径分档会在常用相机距离
        /// （约 1 km）把整条尾焰判成"只剩一个 1 px 点"，看起来就像没有效果。
        /// 阈值以 1080p 参考画幅为准；&lt;4 px 只留喷口点，&lt;14 px 焰心，&gt;=14 px 加外辉光。
        /// </summary>
        public static int LodQuadCountForProjectedLength(double projectedLengthPixels)
        {
            if (projectedLengthPixels < 4.0) return 1;
            if (projectedLengthPixels < 14.0) return 2;
            return 3;
        }

        /// <summary>
        /// 确定性闪烁（0.92–1.08）：按 (partId, 30 Hz 时间格) 做整数哈希，不用随机数。
        /// 暂停时时间格不动 ⇒ 冻结。
        /// </summary>
        public static double Flicker(int salt, double timeSeconds)
        {
            double step = Math.Floor(Math.Max(0.0, timeSeconds) * 30.0);
            ulong h = (ulong)(uint)salt * 0x9E3779B97F4A7C15UL;
            h += (ulong)(long)step * 0xBF58476D1CE4E5B9UL;
            h ^= h >> 30;
            h *= 0xBF58476D1CE4E5B9UL;
            h ^= h >> 27;
            h *= 0x94D049BB133111EBUL;
            h ^= h >> 31;
            double u = (h & 0xFFFFFFUL) / (double)0xFFFFFFUL;
            return 1.0 + (u * 2.0 - 1.0) * 0.08;
        }
    }
}