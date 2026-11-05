using UnityEngine;
using Verse;                    // Current.Game
using PioneerFramework.Launch;   // GetSurfaceState（自转角速度、tile ↔ ECI 的唯一出处）

namespace PioneerFramework
{
    /// <summary>
    /// **天体自转相位**（世界固定系 → PFView 绝对系的旋转）。
    ///
    /// 为什么要它（2026-10-10 用户实测报回："PFView 渲染的贴图和世界视图角度疑似不一致 ——
    /// 当世界视图那边 Group 处于陆地上方时，PFView 一侧却不是"）：
    /// PFView 里的**行星贴图是烘焙出来的、内容是"天体固连系"的地形**（逐格多边形直接来自 PlanetLayer），
    /// 而**飞船位置是 ECI/绝对系的**（轨道积分器给的）。两边要同时成立，画行星时必须把贴图**按自转相位转过去**；
    /// 原来这里写的是 `selfRot = Quaternion.identity`（"天体不自转"）——那是纯色球时代的写法，
    /// 有贴图之后它就是错的了：行星看着不动、飞船却在 ECI 上跑 ⇒ 两者差一个 ωt（地球 **6°/分钟**，越差越多）。
    /// 近档分格那版**本来有**这个相位（V-0 修过一次符号），随它归档一起丢了 ⇒ 这里恢复。
    ///
    /// ⚠ **符号不许手写**（仓库踩过的坑）：唯一权威 = `SurfaceFrame.FixedToEci`；
    /// 把该函数作用在三个基向量上取出**矩阵的三列**、再用纯算术（Shepperd/trace）还原四元数
    /// ⇒ 不碰任何"角度 = ±ωt"的约定（上一版拿 `atan2` 反推相位，符号写反 ⇒ 误差 2ωt 随时间长大）。
    /// 判据：`PFViewBodyRotationTests`（四元数作用 == 函数作用，逐向量比）。
    /// </summary>
    internal static class PFViewBodyRotation
    {
        /// <summary>
        /// 世界固定系 → PFView 绝对系的旋转四元数 = `SurfaceFrame.FixedToEci`（即 Ry(−ωt)，但**不写死**）。
        /// 生产与测试走的是**同一个**函数（不是"测试测复刻、生产另一份"）。
        /// </summary>
        public static Quaternion SurfaceFrameRotation(double omega, double tick)
        {
            return RotationFromColumns(
                (Vector3)SurfaceFrame.FixedToEci(new DVector3(1.0, 0.0, 0.0), omega, tick),
                (Vector3)SurfaceFrame.FixedToEci(new DVector3(0.0, 1.0, 0.0), omega, tick),
                (Vector3)SurfaceFrame.FixedToEci(new DVector3(0.0, 0.0, 1.0), omega, tick));
        }

        /// <summary>
        /// 由旋转矩阵的**三列**还原四元数（Shepperd/trace 法，纯算术 ⇒ headless 可钉）。
        /// `FixedToEci` 只给矢量、不给四元数；自己拿 `atan2` 反推相位极易把符号写反（2026-10-10 就这么错过一次）
        /// ⇒ 改成"列进四元数出"，不再有角度约定的空间。
        /// </summary>
        public static Quaternion RotationFromColumns(Vector3 colX, Vector3 colY, Vector3 colZ)
        {
            float m00 = colX.x, m10 = colX.y, m20 = colX.z;
            float m01 = colY.x, m11 = colY.y, m21 = colY.z;
            float m02 = colZ.x, m12 = colZ.y, m22 = colZ.z;
            float trace = m00 + m11 + m22;

            if (trace > 0f)
            {
                float s = Mathf.Sqrt(trace + 1f) * 2f;   // s = 4w
                return new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
            }
            if (m00 > m11 && m00 > m22)
            {
                float s = Mathf.Sqrt(1f + m00 - m11 - m22) * 2f;   // s = 4x
                return new Quaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            if (m11 > m22)
            {
                float s = Mathf.Sqrt(1f + m11 - m00 - m22) * 2f;   // s = 4y
                return new Quaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            {
                float s = Mathf.Sqrt(1f + m22 - m00 - m11) * 2f;   // s = 4z
                return new Quaternion((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s);
            }
        }

        /// <summary>
        /// 生产入口：当前帧该用的自转。ω 从 `GetSurfaceState.RotationOmega` 取（天体→层→自转周期的唯一出处），
        /// tick 走物理时钟（与飞船位置同一个时刻 ⇒ 不会因为"取的时刻差一帧"而错位）。
        /// </summary>
        public static Quaternion FixedToEciRotation(int bodyId)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr?.TryGetCelestialRuntime(out _) == true)
                return (Quaternion)mgr.GetBodyMotion(bodyId, TimeScaleManager.GetPhysicsTickSafe()).Orientation;
            double omega = GetSurfaceState.RotationOmega(mgr, bodyId);
            double tick = TimeScaleManager.GetPhysicsTickSafe();
            return SurfaceFrameRotation(omega, tick);
        }

        /// <summary>
        /// **PFView 绝对系 → 天体固连系** 的旋转四元数 = `SurfaceFrame.EciToFixed`（`SurfaceFrameRotation` 的逆）。
        ///
        /// 用途（2026-10-12「**PFView 参考系按场景选**」）：贴地/低空/悬浮场景下把**整套场景**乘这个旋转
        /// ⇒ 地面静止、飞船相对地面动（轨道场景仍用恒等旋转 = 惯性系）。
        /// ⚠ 与 `SurfaceFrameRotation` 同一纪律：**符号不许手写** —— 由 `EciToFixed` 作用在基向量上取三列反推；
        /// 判据 `PFViewBodyRotationTests`：两个旋转互逆（四元数作用逐向量对上）。
        /// </summary>
        public static Quaternion EciToSurfaceFrameRotation(double omega, double tick)
        {
            return RotationFromColumns(
                (Vector3)SurfaceFrame.EciToFixed(new DVector3(1.0, 0.0, 0.0), omega, tick),
                (Vector3)SurfaceFrame.EciToFixed(new DVector3(0.0, 1.0, 0.0), omega, tick),
                (Vector3)SurfaceFrame.EciToFixed(new DVector3(0.0, 0.0, 1.0), omega, tick));
        }

        /// <summary>生产入口：当前帧"绝对系 → 固连系"的自转（ω/tick 与 `FixedToEciRotation` 同源同刻）。</summary>
        public static Quaternion EciToFixedRotation(int bodyId)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr?.TryGetCelestialRuntime(out _) == true)
                return (Quaternion)DQuaternion.Inverse(mgr.GetBodyMotion(bodyId, TimeScaleManager.GetPhysicsTickSafe()).Orientation);
            double omega = GetSurfaceState.RotationOmega(mgr, bodyId);
            double tick = TimeScaleManager.GetPhysicsTickSafe();
            return EciToSurfaceFrameRotation(omega, tick);
        }

        /// <summary>
        /// **自转自检的"我这一侧"**：把 tile 中心方向按绘制用的自转转到 ECI、再缩到天体半径（米）。
        ///
        /// ⚠ **必须归一化** —— `PlanetLayer.GetTileCenter` 返回的是**"layer 半径"长的向量**（不是单位向量；
        /// 1 Unity 单位 = 50 km ⇒ 地球层 ≈100、月球层 ≈30），权威侧 `GetSurfaceState.TileToFixedPosition`
        /// 就是这么做的（`.normalized * bodyRadiusMeters`），这里必须同口径。
        ///
        /// ⚠ **2026-10-11 踩过（仪器自误，不是自转错）**：自检原式 `(selfRot * GetTileCenter(tile)) * body.radius`
        /// 漏了归一化 ⇒ `|mine|` 大了 layer 半径倍 ⇒ 假红 **earth 494,998 km / moon 43,454 km**
        /// （与 `(100−1)×5000 km`、`(30−1)×1500 km` 逐位对上）⇒ 渲染那侧本来就是同一个旋转。
        /// </summary>
        public static DVector3 SurfacePointEci(Quaternion fixedToEci, Vector3 tileCenterRaw, double bodyRadiusMeters)
        {
            Vector3 dir = tileCenterRaw.normalized;
            return (DVector3)(fixedToEci * dir) * bodyRadiusMeters;
        }
    }
}
