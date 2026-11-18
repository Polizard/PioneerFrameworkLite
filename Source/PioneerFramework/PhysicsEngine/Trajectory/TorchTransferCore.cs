using System;

namespace PioneerFramework
{
    /// <summary>
    /// **火炬弹道纯核**：翻转燃烧的闭式解、沿航迹解、恒推力积分、规划期打靶、捕获解。
    /// 只依赖 System，由 PioneerFramework.Pure.Tests 链接编译。
    /// </summary>
    public static class TorchTransferCore
    {
        /// <summary>标准重力加速度（m/s²）。</summary>
        public const double StandardGravity = 9.80665;

        /// <summary>规划允许的最大质量比。</summary>
        public const double MaximumMassRatio = 10.0;

        /// <summary>半程时间：τ = √(d/a)。</summary>
        public static double HalfTime(double distance, double acceleration)
            => distance > 0.0 && acceleration > 0.0 && double.IsFinite(distance) && double.IsFinite(acceleration)
                ? Math.Sqrt(distance / acceleration)
                : 0.0;

        /// <summary>总 Δv = 2√(d·a)。</summary>
        public static double TotalDeltaV(double distance, double acceleration)
            => distance > 0.0 && acceleration > 0.0 ? 2.0 * Math.Sqrt(distance * acceleration) : 0.0;

        /// <summary>齐奥尔科夫斯基质量比。</summary>
        public static double MassRatio(double totalDeltaV, double exhaustVelocity)
            => exhaustVelocity > 0.0 && double.IsFinite(exhaustVelocity) && totalDeltaV >= 0.0
                ? Math.Exp(totalDeltaV / exhaustVelocity)
                : double.PositiveInfinity;

        /// <summary>一站式：距离 + 加速度档（g）+ 排气速度 → 半程时间 / 总 Δv / 质量比。</summary>
        public static bool TrySolve(double distance, double accelerationInG, double exhaustVelocity,
            out double halfTimeSeconds, out double totalDeltaV, out double massRatio, out string reason)
        {
            halfTimeSeconds = 0.0;
            totalDeltaV = 0.0;
            massRatio = double.PositiveInfinity;
            reason = "";
            if (!(distance > 1.0) || !double.IsFinite(distance)) { reason = "距离无效"; return false; }
            if (!(accelerationInG > 0.0) || !double.IsFinite(accelerationInG)) { reason = "加速度无效"; return false; }
            if (!(exhaustVelocity > 0.0) || !double.IsFinite(exhaustVelocity)) { reason = "排气速度无效"; return false; }

            double acceleration = accelerationInG * StandardGravity;
            halfTimeSeconds = HalfTime(distance, acceleration);
            totalDeltaV = TotalDeltaV(distance, acceleration);
            massRatio = MassRatio(totalDeltaV, exhaustVelocity);
            if (!double.IsFinite(halfTimeSeconds) || halfTimeSeconds <= 0.0) { reason = "解算失败"; return false; }
            return true;
        }

        /// <summary>
        /// **恒推力一维积分**：恒定推力、质量随工质下降，找"走过一半距离"的时刻与速度。
        /// </summary>
        public static bool TrySimulateConstantThrust(double distance, double dryMass, double propellantMass,
            double thrust, double massFlow, out double halfTimeSeconds, out double halfDeltaV, out string reason)
        {
            halfTimeSeconds = 0.0;
            halfDeltaV = 0.0;
            reason = "";
            if (!(distance > 1.0) || !double.IsFinite(distance)) { reason = "距离无效"; return false; }
            if (!(dryMass > 0.0) || !(propellantMass > 0.0)) { reason = "质量无效"; return false; }
            if (!(thrust > 0.0) || !(massFlow > 0.0)) { reason = "推力或流率无效"; return false; }

            const double step = 10.0;
            double mass = dryMass + propellantMass;
            double position = 0.0, velocity = 0.0, time = 0.0;
            double target = distance * 0.5;
            int guard = 0;
            while (position < target && mass > dryMass && guard++ < 5_000_000)
            {
                double acceleration = thrust / mass;
                velocity += acceleration * step;
                position += velocity * step;
                mass -= massFlow * step;
                time += step;
                if (!double.IsFinite(time) || !double.IsFinite(position)) { reason = "积分发散"; return false; }
            }
            if (position < target) { reason = "工质不足以走过半程"; return false; }
            halfTimeSeconds = time;
            halfDeltaV = velocity;
            return true;
        }

        /// <summary>
        /// **沿航迹初速非零的翻转弹道**：t₁ = (−2u₀ + √(2u₀² + 4AR)) / (2A)，t₂ = t₁ + u₀/A。
        /// </summary>
        public static bool TrySolveAlongLine(double distance, double alongVelocity, double acceleration,
            out double accelSeconds, out double decelSeconds, out double accelDeltaV, out double decelDeltaV,
            out string reason)
        {
            accelSeconds = 0.0;
            decelSeconds = 0.0;
            accelDeltaV = 0.0;
            decelDeltaV = 0.0;
            reason = "";
            if (!(distance > 1.0) || !double.IsFinite(distance)) { reason = "距离无效"; return false; }
            if (!(acceleration > 0.0) || !double.IsFinite(acceleration)) { reason = "加速度无效"; return false; }

            double radicand = 2.0 * alongVelocity * alongVelocity + 4.0 * acceleration * distance;
            if (!(radicand > 0.0) || !double.IsFinite(radicand)) { reason = "沿航迹解算失败（根号内为负）"; return false; }
            double t1 = (-2.0 * alongVelocity + Math.Sqrt(radicand)) / (2.0 * acceleration);
            if (!(t1 > 0.0) || !double.IsFinite(t1))
            {
                reason = $"初速沿航迹分量 {alongVelocity / 1000.0:F2} km/s 过大（距离 {distance / 1e9:F2}e9 m 太近），应改为先减速";
                return false;
            }
            accelSeconds = t1;
            decelSeconds = t1 + alongVelocity / acceleration;
            accelDeltaV = acceleration * accelSeconds;
            decelDeltaV = acceleration * decelSeconds;
            if (!(decelSeconds > 0.0) || !double.IsFinite(decelSeconds)) { reason = "减速段时长解算失败"; return false; }
            return true;
        }

        /// <summary>**恒推力翻转弹道的解析解**（质量随燃烧下降）：二分求翻转质量 m₁。</summary>
        public static bool TrySolveBangBang(double distance, double dryMass, double propellantMass,
            double exhaustVelocity, double massFlow,
            out double accelSeconds, out double decelSeconds, out double halfDeltaV,
            out double massAtFlip, out string reason)
        {
            accelSeconds = 0.0;
            decelSeconds = 0.0;
            halfDeltaV = 0.0;
            massAtFlip = 0.0;
            reason = "";
            if (!(distance > 1.0) || !double.IsFinite(distance)) { reason = "距离无效"; return false; }
            if (!(dryMass > 0.0) || !(propellantMass > 0.0)) { reason = "质量无效"; return false; }
            if (!(exhaustVelocity > 0.0) || !(massFlow > 0.0)) { reason = "排气速度或流率无效"; return false; }

            double m0 = dryMass + propellantMass;
            double mMin = Math.Sqrt(dryMass * m0);
            if (!(mMin < m0)) { reason = "没有可用工质"; return false; }

            double coefficient = exhaustVelocity / massFlow;
            double AccelDistance(double m) => coefficient * (m0 - m + m * Math.Log(m / m0));
            double DecelDistance(double m1)
            {
                double m2 = m1 * m1 / m0;
                return coefficient * (m1 - m2 - m2 * Math.Log(m0 / m1));
            }
            double Total(double m1) => AccelDistance(m1) + DecelDistance(m1);

            double maximum = Total(mMin);
            if (!double.IsFinite(maximum) || maximum < distance)
            {
                reason = $"射程不足：烧到干重只有 {maximum / 1e9:F2}e9 m，需要 {distance / 1e9:F2}e9 m";
                return false;
            }

            double low = mMin, high = m0;
            for (int i = 0; i < 80; i++)
            {
                double middle = 0.5 * (low + high);
                if (Total(middle) >= distance) low = middle; else high = middle;
            }

            massAtFlip = 0.5 * (low + high);
            double massFinal = massAtFlip * massAtFlip / m0;
            accelSeconds = (m0 - massAtFlip) / massFlow;
            decelSeconds = (massAtFlip - massFinal) / massFlow;
            halfDeltaV = exhaustVelocity * Math.Log(m0 / massAtFlip);
            if (!(accelSeconds > 0.0) || !(decelSeconds > 0.0) || !(halfDeltaV > 0.0))
            {
                reason = "解析解退化";
                return false;
            }
            return true;
        }

        /// <summary>**引力一阶位移**：整段路径中点处常值引力造成的位移 ≈ ½g(mid)T²。</summary>
        public static DVector3 GravityDisplacement(DVector3 shipPosition, DVector3 aimPoint, double mu, double totalSeconds)
        {
            if (!(mu > 0.0) || !(totalSeconds > 0.0)) return DVector3.zero;
            DVector3 middle = (shipPosition + aimPoint) * 0.5;
            double radius = middle.magnitude;
            if (!(radius > 1.0) || !double.IsFinite(radius)) return DVector3.zero;
            DVector3 gravity = middle * (-mu / (radius * radius * radius));
            return gravity * (0.5 * totalSeconds * totalSeconds);
        }

        /// <summary>
        /// **二维捕获解**（2026-11-19）：把速度改成"当前点即远心点、近心点 = periapsisGoal"的椭圆所需速度。
        /// 与一维反推的区别：同时消掉径向分量并把切向分量调到 v_req，因此 **r_p 被真正控制住**。
        /// 一维反推只保证"有界"，r_p 由入口几何决定，实测会掉到 0.01×SOI（几乎撞表面）。
        /// </summary>
        public static bool TryPlanCaptureVector(double muTarget, DVector3 relativePosition, DVector3 relativeVelocity,
            double periapsisGoal, out DVector3 captureDv, out string reason)
        {
            captureDv = DVector3.zero;
            reason = "";
            if (!(muTarget > 0.0)) { reason = "目标 GM 无效"; return false; }
            double radius = relativePosition.magnitude;
            if (!(radius > 1.0) || !double.IsFinite(radius)) { reason = "相对位置无效"; return false; }
            if (!(periapsisGoal > 1.0) || periapsisGoal >= radius) { reason = "目标近心点必须小于当前位置"; return false; }

            DVector3 radialUnit = relativePosition * (1.0 / radius);
            double radialComponent = DVector3.Dot(relativeVelocity, radialUnit);
            DVector3 transverse = relativeVelocity - radialUnit * radialComponent;
            if (!(transverse.magnitude > 1e-6))
            {
                // 纯径向下落：任取一个垂直方向作为切向
                DVector3 fallback = Math.Abs(radialUnit.x) < 0.9 ? new DVector3(1, 0, 0) : new DVector3(0, 1, 0);
                transverse = fallback - radialUnit * DVector3.Dot(fallback, radialUnit);
            }
            transverse = transverse.normalized;

            double semiMajor = 0.5 * (radius + periapsisGoal);
            double requiredSpeed = Math.Sqrt(muTarget * (2.0 / radius - 1.0 / semiMajor));
            DVector3 targetVelocity = transverse * requiredSpeed;
            captureDv = targetVelocity - relativeVelocity;
            if (!captureDv.IsFinite()) { reason = "捕获 Δv 解算失败"; return false; }
            return true;
        }
        /// <summary>
        /// **主动进 SOI 的捕获解**（2026-11-18）：把双曲线改成"近心点 = periapsisGoal 的椭圆"所需的 Δv。
        /// 与旧做法（把相对速度刹到 0）的区别：只刹到"能留在 SOI 内"。
        /// </summary>
        public static bool TryPlanCapture(double muTarget, DVector3 relativePosition, DVector3 relativeVelocity,
            double periapsisGoal, out double captureDeltaV, out string reason)
        {
            captureDeltaV = 0.0;
            reason = "";
            if (!(muTarget > 0.0)) { reason = "目标 GM 无效"; return false; }
            double r = relativePosition.magnitude;
            double speed = relativeVelocity.magnitude;
            if (!(r > 1.0) || !double.IsFinite(r)) { reason = "相对位置无效"; return false; }
            if (!(speed > 1e-9)) return true;
            if (!(periapsisGoal > 1.0) || periapsisGoal >= r) { reason = "目标近心点必须小于当前位置"; return false; }
            double semiMajor = 0.5 * (r + periapsisGoal);
            double requiredSpeed = Math.Sqrt(muTarget * (2.0 / r - 1.0 / semiMajor));
            captureDeltaV = speed - requiredSpeed;
            if (!double.IsFinite(captureDeltaV)) { reason = "捕获 Δv 解算失败"; return false; }
            if (captureDeltaV <= 0.0) captureDeltaV = 0.0;
            return true;
        }
    }
}
