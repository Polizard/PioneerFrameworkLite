using System;

namespace PioneerFramework
{
    /// <summary>火炬一次转移的执行计划（B 方案：两个事件 + BeginBurn）。</summary>
    public struct TorchExecutionPlan
    {
        public DVector3 DepartureDv;
        public double DepartureSeconds;
        public double DepartureStartSeconds;
        public DVector3 CaptureDv;
        public double CaptureSeconds;
        public double CaptureStartSeconds;
        public double EntrySeconds;
        public double EntryRadius;
        public double RelativeSpeed;
    }

    /// <summary>
    /// **火炬的纯核规划与传播**（2026-11-19 重写）。
    ///
    /// 纪律：
    /// ① 计划与验证共用同一个 `Propagate`；
    /// ② "找边界"（`SearchBoundary`）与"解捕获"（`SolveCapture`）各自是纯函数，可单测；
    /// ③ `TryPlan` 只做拼装：Lambert → 迭代(找边界 ↔ 解捕获) → 验证。
    ///
    /// 只依赖 System，由 Pure.Tests 链接编译。
    /// </summary>
    public static class TorchExecutionCore
    {
        public const double StepSeconds = 30.0;

        /// <summary>推进一段时间（推力窗口 + 恒星引力 + 入界后目标引力）。</summary>
        public static void Propagate(ref DVector3 position, ref DVector3 velocity, double fromSeconds, double toSeconds,
            TorchExecutionPlan plan, Func<double, (DVector3 Position, DVector3 Velocity)> targetAt,
            double muStar, double muTarget, double soi, double acceleration)
        {
            DVector3 departureDirection = plan.DepartureDv.magnitude > 1e-9
                ? plan.DepartureDv * (1.0 / plan.DepartureDv.magnitude)
                : DVector3.zero;
            DVector3 captureDirection = plan.CaptureDv.magnitude > 1e-9
                ? plan.CaptureDv * (1.0 / plan.CaptureDv.magnitude)
                : DVector3.zero;
            double departureEnd = plan.DepartureStartSeconds + plan.DepartureSeconds;
            double captureEnd = plan.CaptureStartSeconds + plan.CaptureSeconds;

            for (double t = fromSeconds; t < toSeconds; t += StepSeconds)
            {
                double dt = Math.Min(StepSeconds, toSeconds - t);
                double starRadius = position.magnitude;
                DVector3 accel = position * (-muStar / (starRadius * starRadius * starRadius));

                (DVector3 targetPosition, _) = targetAt(t);
                DVector3 relative = position - targetPosition;
                double targetRadius = relative.magnitude;
                if (targetRadius < soi)
                    accel = accel + relative * (-muTarget / (targetRadius * targetRadius * targetRadius));

                if (t >= plan.DepartureStartSeconds && t < departureEnd) accel = accel + departureDirection * acceleration;
                if (t >= plan.CaptureStartSeconds && t < captureEnd) accel = accel + captureDirection * acceleration;

                velocity = velocity + accel * dt;
                position = position + velocity * dt;
            }
        }

        /// <summary>从起点推进到指定时刻（与 Propagate 同一份代码）。</summary>
        public static (DVector3 Position, DVector3 Velocity) StateAt(double seconds,
            DVector3 shipPosition, DVector3 shipVelocity, TorchExecutionPlan plan,
            Func<double, (DVector3 Position, DVector3 Velocity)> targetAt,
            double muStar, double muTarget, double soi, double acceleration)
        {
            DVector3 position = shipPosition;
            DVector3 velocity = shipVelocity;
            Propagate(ref position, ref velocity, 0.0, seconds, plan, targetAt, muStar, muTarget, soi, acceleration);
            return (position, velocity);
        }

        /// <summary>
        /// **找 SOI 边界穿越**：从起点推进，第一次 |r − target| ≤ soiLimit 的时刻与相对状态。
        /// 纯函数，可单测（给一条已知轨迹就能验）。
        /// </summary>
        public static bool SearchBoundary(DVector3 shipPosition, DVector3 shipVelocity, TorchExecutionPlan plan,
            Func<double, (DVector3 Position, DVector3 Velocity)> targetAt,
            double muStar, double muTarget, double soi, double soiLimit, double acceleration, double maxSeconds,
            out double boundarySeconds, out DVector3 relativePosition, out DVector3 relativeVelocity)
        {
            boundarySeconds = 0.0;
            relativePosition = DVector3.zero;
            relativeVelocity = DVector3.zero;

            DVector3 position = shipPosition;
            DVector3 velocity = shipVelocity;
            for (double t = 0.0; t < maxSeconds; t += StepSeconds)
            {
                Propagate(ref position, ref velocity, t, t + StepSeconds, plan, targetAt, muStar, muTarget, soi, acceleration);
                (DVector3 targetPosition, DVector3 targetVelocity) = targetAt(t + StepSeconds);
                DVector3 relative = position - targetPosition;
                if (relative.magnitude <= soiLimit)
                {
                    boundarySeconds = t + StepSeconds;
                    relativePosition = relative;
                    relativeVelocity = velocity - targetVelocity;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// **解捕获**：把相对速度沿反方向削到 `factor × 圆轨速度(燃尽半径)`。
        /// 燃尽半径依赖燃烧时长，迭代 3 次收敛。纯函数，可单测（给定状态即可核对能量）。
        /// </summary>
        public static bool SolveCapture(DVector3 relativePosition, DVector3 relativeVelocity,
            double muTarget, double acceleration, double factor, double rpGoal,
            out DVector3 captureDv, out double captureSeconds, out string reason)
        {
            captureDv = DVector3.zero;
            captureSeconds = 0.0;
            reason = "";
            double radius = relativePosition.magnitude;
            double speed = relativeVelocity.magnitude;
            if (!(radius > 1.0)) { reason = "相对位置退化"; return false; }
            if (!(speed > 1e-6)) { return true; }
            if (!(acceleration > 0.0)) { reason = "加速度无效"; return false; }

            // 二维目标：远心点在当前半径、近心点在 rpGoal（高轨），切向速度按 vis-viva
            DVector3 radialUnit = relativePosition * (1.0 / radius);
            double radialComponent = DVector3.Dot(relativeVelocity, radialUnit);
            DVector3 transverse = relativeVelocity - radialUnit * radialComponent;
            if (!(transverse.magnitude > 1e-6))
            {
                DVector3 fallback = Math.Abs(radialUnit.x) < 0.9 ? new DVector3(1, 0, 0) : new DVector3(0, 1, 0);
                transverse = fallback - radialUnit * DVector3.Dot(fallback, radialUnit);
            }
            transverse = transverse * (1.0 / transverse.magnitude);
            double semiMajor = 0.5 * (radius + rpGoal);
            double requiredSpeed = Math.Sqrt(muTarget * (2.0 / radius - 1.0 / semiMajor));
            DVector3 targetVelocity = transverse * requiredSpeed;
            captureDv = targetVelocity - relativeVelocity;
            captureSeconds = captureDv.magnitude / acceleration;
            return true;

            double radiusAtBurnEnd = radius;
            double targetSpeed = 0.0;
            for (int k = 0; k < 3; k++)
            {
                targetSpeed = factor * Math.Sqrt(muTarget / Math.Max(1.0, radiusAtBurnEnd));
                double deltaVEstimate = Math.Max(0.0, speed - targetSpeed);
                radiusAtBurnEnd = radius + speed * (deltaVEstimate / acceleration) * 0.5;
            }

            double deltaV = Math.Max(0.0, speed - targetSpeed);
            captureDv = relativeVelocity * (-deltaV / speed);
            captureSeconds = deltaV / acceleration;
            return true;
        }

        /// <summary>规划：Lambert（含中点修正）→ 迭代(找边界 ↔ 解捕获) → 验证。</summary>
        public static bool TryPlan(DVector3 shipPosition, DVector3 shipVelocity,
            Func<double, (DVector3 Position, DVector3 Velocity)> targetAt,
            double muStar, double muTarget, double soi, double acceleration,
            double timeOfFlightSeconds, double periapsisGoal,
            out TorchExecutionPlan plan, out string reason)
        {
            plan = default;
            reason = "";
            if (!(acceleration > 0.0)) { reason = "加速度无效"; return false; }
            if (!(muStar > 0.0) || !(muTarget > 0.0)) { reason = "GM 无效"; return false; }
            if (!(soi > 1.0)) { reason = "SOI 无效"; return false; }
            if (!(timeOfFlightSeconds > 0.0)) { reason = "转移时长无效"; return false; }

            (DVector3 arrivalPosition, _) = targetAt(timeOfFlightSeconds);
            if (!InterplanetaryTransferCore.TryPlanDeparture(muStar, shipPosition, shipVelocity, arrivalPosition,
                    timeOfFlightSeconds, out DVector3 departureDv, out string departureReason))
            {
                reason = "出航无解：" + departureReason;
                return false;
            }
            if (!(departureDv.magnitude > 0.0) || !departureDv.IsFinite()) { reason = "出航 Δv 无效"; return false; }

            // 出航段中点修正
            for (int correction = 0; correction < 3; correction++)
            {
                double tau = departureDv.magnitude / acceleration;
                if (!(tau > 0.0) || tau * 0.5 >= timeOfFlightSeconds) break;
                DVector3 midPosition = OrbitMath.GetPositionAtTimeDelta(shipPosition, shipVelocity, tau * 0.5, muStar);
                DVector3 midVelocity = OrbitMath.GetVelocityAtTimeDelta(shipPosition, shipVelocity, tau * 0.5, muStar);
                if (!InterplanetaryTransferCore.TryPlanDeparture(muStar, midPosition, midVelocity, arrivalPosition,
                        timeOfFlightSeconds - tau * 0.5, out DVector3 corrected, out _)) break;
                if (!(corrected.magnitude > 0.0) || !corrected.IsFinite()) break;
                double change = (corrected - departureDv).magnitude;
                departureDv = corrected;
                if (change < 1.0) break;
            }

            double departureSeconds = departureDv.magnitude / acceleration;
            TorchExecutionPlan working = new()
            {
                DepartureDv = departureDv,
                DepartureSeconds = departureSeconds,
                DepartureStartSeconds = 0.0,
                CaptureDv = DVector3.zero,
                CaptureSeconds = 0.0,
                CaptureStartSeconds = double.PositiveInfinity,
            };

            double soiLimit = soi * 0.90;
            double maxSeconds = timeOfFlightSeconds * 1.3;
            const double factor = 0.72;   // 目标速度 = f×圆轨速度（燃尽半径），f<1 才留得住）

            // 迭代：找边界（带当前烧）→ 解捕获 → 更新烧。两轮收敛。
            for (int pass = 0; pass < 3; pass++)
            {
                if (!SearchBoundary(shipPosition, shipVelocity, working, targetAt, muStar, muTarget, soi, soiLimit,
                        acceleration, maxSeconds, out double boundary, out DVector3 relPos, out DVector3 relVel))
                {
                    reason = "轨迹未进入 SOI";
                    return false;
                }
                if (!SolveCapture(relPos, relVel, muTarget, acceleration, factor, 0.5 * soi,
                        out DVector3 captureDv, out double captureSeconds, out string captureReason))
                {
                    reason = "捕获无解：" + captureReason;
                    return false;
                }

                working.CaptureDv = captureDv;
                working.CaptureSeconds = captureSeconds;
                working.CaptureStartSeconds = Math.Max(departureSeconds, boundary - captureSeconds * 0.5);
                working.EntrySeconds = boundary;
                working.EntryRadius = relPos.magnitude;
                working.RelativeSpeed = relVel.magnitude;
            }

            // 验证：燃尽时刻必须束缚，且远心点 ≤ 0.95×SOI
            double verifyTime = working.CaptureStartSeconds + working.CaptureSeconds;
            (DVector3 finalPosition, DVector3 finalVelocity) = StateAt(verifyTime,
                shipPosition, shipVelocity, working, targetAt, muStar, muTarget, soi, acceleration);
            (DVector3 endTargetPosition, DVector3 endTargetVelocity) = targetAt(verifyTime);
            DVector3 endRelativePosition = finalPosition - endTargetPosition;
            DVector3 endRelativeVelocity = finalVelocity - endTargetVelocity;
            double r = endRelativePosition.magnitude;
            double v = endRelativeVelocity.magnitude;
            if (!(r > 1.0)) { reason = "验证时相对位置退化"; return false; }

            double energy = v * v / 2.0 - muTarget / r;
            if (energy >= 0.0)
            {
                reason = $"捕获后仍未束缚（ε={energy:E2}｜r={r / 1e6:F1}e6 m｜v={v / 1000.0:F2} km/s"
                    + $"｜边界 r={working.EntryRadius / 1e6:F1}e6 m、v={working.RelativeSpeed / 1000.0:F2} km/s）";
                return false;
            }

            double semiMajor = -muTarget / (2.0 * energy);
            DVector3 momentum = new(
                endRelativePosition.y * endRelativeVelocity.z - endRelativePosition.z * endRelativeVelocity.y,
                endRelativePosition.z * endRelativeVelocity.x - endRelativePosition.x * endRelativeVelocity.z,
                endRelativePosition.x * endRelativeVelocity.y - endRelativePosition.y * endRelativeVelocity.x);
            double eccentricitySquared = 1.0 + 2.0 * energy * momentum.sqrMagnitude / (muTarget * muTarget);
            double eccentricity = Math.Sqrt(Math.Max(0.0, eccentricitySquared));
            double apoapsis = semiMajor * (1.0 + eccentricity);
            double periapsisAchieved = semiMajor * (1.0 - eccentricity);
            if (periapsisAchieved > soi * 0.70)
            {
                reason = $"近心点 {periapsisAchieved / 1e6:F1}e6 m 高于 0.70×SOI（运行时会等不到圆化时机）";
                return false;
            }
            if (apoapsis > soi * 0.95)
            {
                reason = $"远心点 {apoapsis / 1e6:F1}e6 m 超出 0.95×SOI（{soi * 0.95 / 1e6:F1}e6 m）"
                    + $"｜r_p={semiMajor * (1.0 - eccentricity) / 1e6:F1}e6 m｜e={eccentricity:F3}";
                return false;
            }

            plan = working;
            return true;
        }
    }
}
