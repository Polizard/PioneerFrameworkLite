using System;

namespace PioneerFramework
{
    /// <summary>
    /// **霍曼转移纯核**（2026-10-27）：从 `OrbitTransferPlanner` 的窗口内数学抽出。
    /// 输入 = 中心天体相对状态 + 目标半径/法向；输出 = 两发 Δv、到达状态、转移时长。
    /// 参考系：`r1/v1` 所在系（体心或世界都行——Δv 与系只差平移，方向不变）。
    /// 纯函数：只 using System + `OrbitMath`/`DVector3`（两者都在纯测试工程里）。
    /// </summary>
    public readonly struct HohmannResult
    {
        public readonly bool Valid;
        public readonly string Reason;

        /// <summary>出发切向 Δv（m/s，带符号；降轨为负）。</summary>
        public readonly double TanDv1;
        /// <summary>到达切向 Δv（m/s，带符号）。</summary>
        public readonly double TanDv2;
        /// <summary>到达法向 Δv（m/s，含上下抬符号）。</summary>
        public readonly double NormDv2;
        /// <summary>到达组合 |Δv| = √(tan² + norm²)。</summary>
        public readonly double ArrivalDv;
        /// <summary>出发 |Δv| + 到达组合 |Δv|。</summary>
        public readonly double TotalDv;
        /// <summary>两轨道面夹角（rad）。</summary>
        public readonly double DeltaI;
        /// <summary>转移时长（s）= 半周期。</summary>
        public readonly double TransferSeconds;

        /// <summary>出发 Δv 向量（与输入同系）。</summary>
        public readonly DVector3 Dv1;
        /// <summary>到达 Δv 向量（在**转移轨道到达点**的 RTN 上算）。</summary>
        public readonly DVector3 Dv2;
        /// <summary>转移速度（出发状态 + Dv1）。</summary>
        public readonly DVector3 TransferVel;
        /// <summary>到达点状态（转移轨道传播半个周期）。</summary>
        public readonly DVector3 ArrivalPos;
        public readonly DVector3 ArrivalVel;

        public HohmannResult(bool valid, string reason, double tanDv1, double tanDv2, double normDv2,
            double arrivalDv, double totalDv, double deltaI, double transferSeconds,
            DVector3 dv1, DVector3 dv2, DVector3 transferVel, DVector3 arrivalPos, DVector3 arrivalVel)
        {
            Valid = valid; Reason = reason;
            TanDv1 = tanDv1; TanDv2 = tanDv2; NormDv2 = normDv2;
            ArrivalDv = arrivalDv; TotalDv = totalDv; DeltaI = deltaI; TransferSeconds = transferSeconds;
            Dv1 = dv1; Dv2 = dv2; TransferVel = transferVel;
            ArrivalPos = arrivalPos; ArrivalVel = arrivalVel;
        }

        public static HohmannResult Invalid(string reason) =>
            new(false, reason, 0, 0, 0, 0, 0, 0, 0, DVector3.zero, DVector3.zero, DVector3.zero, DVector3.zero, DVector3.zero);
    }

    public static class HohmannCore
    {
        /// <summary>
        /// 计算一次霍曼转移（含到达平面变化）。
        /// </summary>
        /// <param name="r1">出发位置（中心天体相对，m）。</param>
        /// <param name="v1">出发速度（同系，m/s）。</param>
        /// <param name="mu">中心天体 GM。</param>
        /// <param name="targetRadius">目标圆轨半径（m）。</param>
        /// <param name="targetNormal">目标轨道法向（单位向量；≈0 = 不改变平面）。</param>
        public static HohmannResult Compute(DVector3 r1, DVector3 v1, double mu,
            double targetRadius, DVector3 targetNormal)
        {
            double r1m = r1.magnitude;
            if (!(mu > 0.0) || !(r1m > 1.0) || !(targetRadius > 1.0))
                return HohmannResult.Invalid("参数非法（mu/r1/r2 必须为正）");

            DVector3 h1 = DVector3.Cross(r1, v1);
            if (h1.sqrMagnitude < 1e-12)
                return HohmannResult.Invalid("位置与速度共线（无轨道平面）");
            h1 = h1.normalized;

            double a = 0.5 * (r1m + targetRadius);
            double vp = Math.Sqrt(mu * (2.0 / r1m - 1.0 / a));          // 转移近地点速度
            double va = Math.Sqrt(mu * (2.0 / targetRadius - 1.0 / a)); // 转移远地点速度
            double vc1 = Math.Sqrt(mu / r1m);
            double vc2 = Math.Sqrt(mu / targetRadius);

            double dvTan1 = vp - vc1;

            DVector3 h2 = targetNormal.sqrMagnitude > 1e-12 ? targetNormal.normalized : h1;
            double cosDi = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(h1, h2)));
            double deltaI = Math.Acos(cosDi);

            double dvTan2 = vc2 * cosDi - va;
            double dvNorm = vc2 * Math.Sin(deltaI);
            DVector3 hCross = DVector3.Cross(h1, h2);
            double normSign = hCross.sqrMagnitude > 1e-12
                ? Math.Sign(DVector3.Dot(hCross, r1.normalized))
                : 0.0;
            double dvNormSigned = dvNorm * normSign;

            DVector3 dv1 = OrbitMath.GetDeltaVSystem(r1, v1, 0.0, dvTan1, 0.0);
            DVector3 vTransfer = v1 + dv1;
            double tof = Math.PI * Math.Sqrt(a * a * a / mu);

            DVector3 rA = OrbitMath.GetPositionAtTimeDelta(r1, vTransfer, tof, mu);
            DVector3 vA = OrbitMath.GetVelocityAtTimeDelta(r1, vTransfer, tof, mu);
            DVector3 dv2 = OrbitMath.GetDeltaVSystem(rA, vA, 0.0, dvTan2, dvNormSigned);

            double arrivalDv = Math.Sqrt(dvTan2 * dvTan2 + dvNormSigned * dvNormSigned);
            double totalDv = Math.Abs(dvTan1) + arrivalDv;

            return new HohmannResult(true, "", dvTan1, dvTan2, dvNormSigned,
                arrivalDv, totalDv, deltaI, tof, dv1, dv2, vTransfer, rA, vA);
        }
    }
}
