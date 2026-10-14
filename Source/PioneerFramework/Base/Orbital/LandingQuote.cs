using System;

namespace PioneerFramework
{
    /// <summary>
    /// **选 tile 主动降落的报价与判据**（纯核，2026-09-22）。
    ///
    /// ## 语义（用户裁定）
    /// 接近某天体时，玩家选一个**地表 tile**、**付出一定 Δv**，换取一次**保证安全**的瞬间降落。
    /// · **Δv 报价** = `|v 船 − v 落点地表|`（同一参考系内的向量差 —— "支付把速度变成跟着地面一起转的那一份"）
    /// · **距离门** = 落点与**星下点**之间的地表大圆距离 ≤ <see cref="MaxSurfaceDistance"/>
    /// · 付不起 / 太远 / 参数不全 ⇒ **拒绝**（不落地、不动轨道；游戏侧照此发消息）
    ///
    /// ## ⚠ 参考系硬约定（最容易错的一处）
    /// 船的 `(r, v)` 与天体的 `(bodyOffsetPos, bodyOffsetVel)` **必须同系**。游戏侧一律传：
    /// · 船 = `group.CurrentOrbit.position/velocity`（**系统系 = 根行星（地球）系**）；
    /// · 天体 = `SpaceGameComp.GetBodyOffsetSystem/VelOffset`（对**根行星**返零、对卫星给"绕父天体"的偏移）。
    /// **不要**传 `group.positionBody/velocityBody`（那是**天体相对**的）—— 与天体偏移混用会凭空多/少
    /// 一份天体平动（月球 3141 m/s 量级 ⇒ 报价直接错一个数量级）。与"跨系运算必须显式换算"同一条纪律。
    ///
    /// 「绕地的船落到月球」就是靠这条成立的：目标状态按 `月球绕地偏移 + 月球固连自转` 拼进系统系即可。
    ///
    /// ## 已登记的简化（刻意不做，别当隐藏债）
    /// 只有"速度差"这一项，**没有转移/落下能量项** ⇒ 理论上"又远又慢"的船能用接近零的 Δv 落到月球。
    /// 正常玩法碰不到（远距离低速停车的船几乎不存在；贴近天体时这条报价就对）。要管时加一个自由落体项
    /// `sqrt(2μ(1/R − 1/d))` 即可 —— 加之前先问用户。
    ///
    /// **纯**：只吃 double / DVector3，不碰 Verse/UnityEngine ⇒ 链进 `MechJebLibTest` 可单测。
    /// </summary>
    public static class LandingQuote
    {
        /// <summary>报价结果。`ok = false` 时读 <see cref="reason"/>（`tilePos/tileVel` 仍填，便于诊断）。</summary>
        public readonly struct Result
        {
            public readonly bool ok;
            /// <summary>报价 Δv（m/s）= |v 船 − v 落点地表|。</summary>
            public readonly double deltaV;
            /// <summary>星下点 ↔ 落点的**地表大圆距离**（m）。</summary>
            public readonly double surfaceDistance;
            /// <summary>本天体的距离门上界（m）= <see cref="MaxSurfaceDistance"/>。</summary>
            public readonly double maxDistance;
            /// <summary>落点世界状态（与入参同系）。</summary>
            public readonly DVector3 tilePos;
            public readonly DVector3 tileVel;
            /// <summary>拒绝原因；`ok` 时是报价摘要（供日志）。</summary>
            public readonly string reason;

            public Result(bool ok, double deltaV, double surfaceDistance, double maxDistance,
                DVector3 tilePos, DVector3 tileVel, string reason)
            {
                this.ok = ok;
                this.deltaV = deltaV;
                this.surfaceDistance = surfaceDistance;
                this.maxDistance = maxDistance;
                this.tilePos = tilePos;
                this.tileVel = tileVel;
                this.reason = reason;
            }
        }

        /// <summary>
        /// **距离门（m）的唯一实现**：`min(绝对上限, 比例 × 天体半径)`。
        /// 地球 → 1000 km（绝对上限生效）；月球 → 525 km（比例生效）。
        /// </summary>
        public static double MaxSurfaceDistance(double bodyRadius)
            => Math.Min(SpaceConstants.MaxLandingSurfaceDistanceMeters,
                        SpaceConstants.MaxLandingDistanceRadiiFraction * bodyRadius);

        /// <summary>
        /// 报价（口径与参考系约定见类注释）。`tileDirFixed` = 落点 tile 在**固连系**的方向（无需归一）。
        /// </summary>
        public static Result Evaluate(
            DVector3 shipPos, DVector3 shipVel,
            DVector3 bodyOffsetPos, DVector3 bodyOffsetVel,
            double bodyRadius, double omega,
            DVector3 tileDirFixed, double physicsTick)
        {
            double maxDist = MaxSurfaceDistance(bodyRadius);
            bool badDir = !(tileDirFixed.sqrMagnitude > 1e-12);
            if (!(bodyRadius > 0.0) || !(omega > 0.0) || badDir)
                return new Result(false, 0.0, double.NaN, maxDist, DVector3.zero, DVector3.zero,
                    $"参数不完整（天体半径 {bodyRadius:F0} m、自转角速度 {omega:E2} rad/s、落点方向{(badDir ? "未给" : "已给")}）");

            DVector3 pEci = SurfaceFrame.FixedToEci(tileDirFixed.normalized * bodyRadius, omega, physicsTick);
            DVector3 pSystem = bodyOffsetPos + pEci;
            DVector3 vSystem = bodyOffsetVel + SurfaceFrame.SurfaceVelocity(pEci, omega);
            double dv = (shipVel - vSystem).magnitude;

            DVector3 rBody = shipPos - bodyOffsetPos;
            if (!(rBody.sqrMagnitude > 1e-12))
                return new Result(false, dv, double.NaN, maxDist, pSystem, vSystem, "船就在天体中心（星下点无定义）");

            double cos = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(rBody.normalized, pEci.normalized)));
            double dist = bodyRadius * Math.Acos(cos);

            if (dist > maxDist)
                return new Result(false, dv, dist, maxDist, pSystem, vSystem,
                    $"落点太远：地表 {dist / 1000.0:F0} km > 上界 {maxDist / 1000.0:F0} km");

            return new Result(true, dv, dist, maxDist, pSystem, vSystem,
                $"可落：地表距离 {dist / 1000.0:F0} km、Δv {dv:F0} m/s");
        }
    }
}
