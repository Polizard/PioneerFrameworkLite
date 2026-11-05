using System;

namespace PioneerFramework
{
    /// <summary>
    /// **自动发射的目标轨道求解**（2026-10-27；纯函数，可离线单测）。
    ///
    /// 用户口径：自动发射要适用于**各种天体表面**，且目标轨道**不能太高**（否则被别的天体吸走）。
    /// 规则：
    /// · 基本低轨 `hBase = max(0.10·R, 1.25·大气厚度)`；
    /// · **SOI 上限**：`hSoi = 0.5·(soiApprox − R)`（Hill 半径只是近似，留 2× 余量）；
    /// · **邻居上限**：`hNear = 0.25·D_nearest − R`（最近天体中心距的四分之一）；
    /// · 下限 `hMin = max(0.02·R, 1.10·大气厚度)`；被压到下限以下 ⇒ **不可行**（明确拒绝，不静默）。
    ///
    /// 这里只做算术，不碰游戏对象；`LaunchSite` 负责把天体数据喂进来。
    /// </summary>
    public readonly struct LaunchTargetInputs
    {
        /// <summary>天体半径（m）。</summary>
        public readonly double Radius;
        /// <summary>天体 GM（m³/s²）。</summary>
        public readonly double Mu;
        /// <summary>大气厚度（m；0 = 真空）。</summary>
        public readonly double AtmosphereThickness;
        /// <summary>SOI 近似半径（m；NaN = 未知，不参与夹取）。</summary>
        public readonly double SoiApprox;
        /// <summary>最近邻天体中心距（m；NaN/∞ = 无邻居，不参与夹取）。</summary>
        public readonly double NearestBodyDistance;

        public LaunchTargetInputs(double radius, double mu, double atmosphereThickness,
            double soiApprox, double nearestBodyDistance)
        {
            Radius = radius;
            Mu = mu;
            AtmosphereThickness = atmosphereThickness;
            SoiApprox = soiApprox;
            NearestBodyDistance = nearestBodyDistance;
        }
    }

    public readonly struct LaunchTargetResult
    {
        public readonly double TargetRadius;
        public readonly double TargetAltitude;
        public readonly double VCircular;
        public readonly double SurfaceGravity;
        public readonly bool Feasible;
        public readonly bool ClampedBySoi;
        public readonly bool ClampedByNeighbor;
        public readonly string Reason;

        public LaunchTargetResult(double targetRadius, double targetAltitude, double vCircular,
            double surfaceGravity, bool feasible, bool clampedBySoi, bool clampedByNeighbor, string reason)
        {
            TargetRadius = targetRadius;
            TargetAltitude = targetAltitude;
            VCircular = vCircular;
            SurfaceGravity = surfaceGravity;
            Feasible = feasible;
            ClampedBySoi = clampedBySoi;
            ClampedByNeighbor = clampedByNeighbor;
            Reason = reason;
        }
    }

    public static class LaunchTargeting
    {
        public const double BaseAltitudeFactor = 0.10;
        public const double MinAltitudeFactor = 0.02;
        public const double AtmosphereMargin = 1.25;
        public const double SoiFraction = 0.50;
        public const double NeighborFraction = 0.25;

        public static LaunchTargetResult Compute(in LaunchTargetInputs x)
        {
            if (!(x.Radius > 0.0) || !(x.Mu > 0.0))
                return Fail(0.0, 0.0, $"半径/GM 无效（R={x.Radius:E2}, μ={x.Mu:E2}）");

            double g = x.Mu / (x.Radius * x.Radius);
            double atmo = x.AtmosphereThickness > 0.0 ? x.AtmosphereThickness : 0.0;
            double hMin = Math.Max(MinAltitudeFactor * x.Radius, 1.10 * atmo);
            double hBase = Math.Max(BaseAltitudeFactor * x.Radius, AtmosphereMargin * atmo);

            double hCap = double.PositiveInfinity;
            double hSoi = double.PositiveInfinity;
            double hNear = double.PositiveInfinity;
            if (IsFinite(x.SoiApprox) && x.SoiApprox > x.Radius)
                hSoi = SoiFraction * (x.SoiApprox - x.Radius);
            if (IsFinite(x.NearestBodyDistance) && x.NearestBodyDistance > 0.0)
                hNear = NeighborFraction * x.NearestBodyDistance - x.Radius;

            hCap = Math.Min(hSoi, hNear);
            double h = Math.Min(hBase, hCap);

            if (h < hMin)
            {
                return Fail(0.0, h, $"目标高度 {h / 1000.0:F1} km 低于下限 {hMin / 1000.0:F1} km"
                    + $"（R={x.Radius / 1000.0:F0} km, SOI={Fmt(x.SoiApprox)}, 邻居={Fmt(x.NearestBodyDistance)}）");
            }

            bool clamped = h < hBase - 1e-9;
            bool bySoi = clamped && hSoi <= hNear + 1e-9;
            bool byNear = clamped && hNear < hSoi - 1e-9;

            double r = x.Radius + h;
            double vC = Math.Sqrt(x.Mu / r);
            string reason = clamped
                ? $"目标高度被压到 {h / 1000.0:F1} km（基本 {hBase / 1000.0:F1} km；SOI {hSoi / 1000.0:F0} km / 邻居 {hNear / 1000.0:F0} km）"
                : "";
            return new LaunchTargetResult(r, h, vC, g, true, bySoi, byNear, reason);
        }

        private static LaunchTargetResult Fail(double vC, double h, string reason)
            => new(0.0, h, vC, 0.0, false, false, false, reason);

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private static string Fmt(double v) => IsFinite(v) ? $"{v / 1000.0:F0} km" : "未知";
    }
}
