using System;

namespace PioneerFramework
{
    /// <summary>太阳体积的投影、成本上限和周期时钟；不依赖 Unity/Verse，可独立验收。</summary>
    internal static class PFViewSolarCore
    {
        public const double DefaultAngularRadiusDegrees = 1.43;
        public const double OuterRadius = 1.65;
        public const double ProjectionExtent = 2.1;
        public const double PhasePeriodTicks = 36000.0;
        public const int FocusMaximumDimension = 768;

        /// <summary>日照方向只决定入场角度，后续环绕保持惯性 +Y 轴。</summary>
        public static (double Yaw, double Pitch) InitialOrbitAngles(double sunX, double sunY, double sunZ)
        {
            double scale = Math.Max(Math.Abs(sunX), Math.Max(Math.Abs(sunY), Math.Abs(sunZ)));
            if (!double.IsFinite(scale) || scale <= 0.0) return (0.0, 45.0);
            sunX /= scale;
            sunY /= scale;
            sunZ /= scale;
            double length = Math.Sqrt(sunX * sunX + sunY * sunY + sunZ * sunZ);
            return (Math.Atan2(-sunX, -sunZ) * 180.0 / Math.PI,
                Math.Asin(Math.Clamp(-sunY / length, -1.0, 1.0)) * 180.0 / Math.PI);
        }

        public static double DefaultDistance(double radius)
            => ValidRadius(radius) * 4.0;

        public static double CameraBoundary(double radius)
            => ValidRadius(radius) * OuterRadius;

        public static double ClampDistance(double distance, double radius, double maximum)
        {
            radius = ValidRadius(radius);
            if (!double.IsFinite(maximum) || maximum <= 0.0) maximum = radius * 256.0;
            double minimum = Math.Min(CameraBoundary(radius), maximum);
            if (!double.IsFinite(distance)) distance = DefaultDistance(radius);
            return Math.Clamp(distance, minimum, maximum);
        }

        public static double FarEyeRadius(double angularDegrees)
            => Math.Clamp(1.0 / Math.Sin(ValidAngle(angularDegrees) * Math.PI / 180.0), 8.0, 128.0);

        public static double ProjectionHalfTangent(double eyeRadius)
        {
            if (!double.IsFinite(eyeRadius) || eyeRadius <= ProjectionExtent) eyeRadius = 40.0;
            return ProjectionExtent / Math.Sqrt(eyeRadius * eyeRadius - ProjectionExtent * ProjectionExtent);
        }

        public static double FarQuadHalfTangent(double angularDegrees)
        {
            double eye = FarEyeRadius(angularDegrees);
            double coreHalfTangent = 1.0 / Math.Sqrt(eye * eye - 1.0);
            return ProjectionHalfTangent(eye) * Math.Tan(ValidAngle(angularDegrees) * Math.PI / 180.0) / coreHalfTangent;
        }

        public static double Phase(double simulationTick)
        {
            if (!double.IsFinite(simulationTick)) return 0.0;
            double wrapped = simulationTick % PhasePeriodTicks;
            if (wrapped < 0.0) wrapped += PhasePeriodTicks;
            return wrapped / PhasePeriodTicks * (Math.PI * 2.0);
        }

        public static (int Width, int Height) TextureSize(int width, int height, bool focused, double diameterPixels)
        {
            if (width <= 0 || height <= 0) return (64, 64);
            if (focused)
            {
                double scale = Math.Min(1.0, (double)FocusMaximumDimension / Math.Max(width, height));
                return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
            }
            double wanted = double.IsFinite(diameterPixels) ? Math.Clamp(diameterPixels * ProjectionExtent, 64.0, 256.0) : 64.0;
            int size = 64;
            while (size < wanted && size < 256) size *= 2;
            return (size, size);
        }

        private static double ValidRadius(double radius) => double.IsFinite(radius) && radius > 0.0 ? radius : 1.0;
        private static double ValidAngle(double angle)
            => double.IsFinite(angle) && angle > 0.0 ? Math.Min(angle, 89.0) : DefaultAngularRadiusDegrees;
    }
}
