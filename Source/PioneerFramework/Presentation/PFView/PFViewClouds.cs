using System;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 云层的高度、近景淡出和动画相位（2026-10-19）。
    /// 纯视觉参数，不参与大气密度或轨道计算；绘制见 MainTabWindow_PFView.Clouds.cs。
    /// </summary>
    internal static class PFViewClouds
    {
        public const double AltitudeMeters = 10000.0;
        public const double FadeRangeMeters = 5000.0;
        public const double DriftPeriodTicks = 600000.0;
        public const double DistortionPeriodTicks = 6000.0;

        /// <summary>
        /// 半径 0.5 的外向球面。96×192 将地球档弦高控制在约 1.4 km 内，
        /// 10 km 云高仍能覆盖最大约 6.1 km 外扩的烘焙补丁；不复用低精行星 LOD。
        /// </summary>
        public static void BuildShellGeometry(out DVector3[] vertices, out int[] triangles)
        {
            const int lats = 96;
            const int longs = 192;
            vertices = new DVector3[(lats + 1) * (longs + 1)];
            triangles = new int[lats * longs * 6];
            for (int lat = 0; lat <= lats; lat++)
            {
                double phi = Math.PI * lat / lats;
                for (int lon = 0; lon <= longs; lon++)
                {
                    double theta = 2.0 * Math.PI * lon / longs;
                    vertices[lat * (longs + 1) + lon] = new DVector3(
                        Math.Sin(phi) * Math.Cos(theta) * 0.5,
                        Math.Cos(phi) * 0.5,
                        Math.Sin(phi) * Math.Sin(theta) * 0.5);
                }
            }
            int ti = 0;
            for (int lat = 0; lat < lats; lat++)
            {
                for (int lon = 0; lon < longs; lon++)
                {
                    int a = lat * (longs + 1) + lon;
                    int b = a + longs + 1;
                    triangles[ti++] = a; triangles[ti++] = a + 1; triangles[ti++] = b;
                    triangles[ti++] = a + 1; triangles[ti++] = b + 1; triangles[ti++] = b;
                }
            }
        }

        public static double RadiusFactor(double bodyRadiusMeters)
            => RadiusFactor(bodyRadiusMeters, AltitudeMeters);
        public static double RadiusFactor(double bodyRadiusMeters, double altitudeMeters)
            => double.IsFinite(bodyRadiusMeters) && bodyRadiusMeters > 0.0
                ? 1.0 + altitudeMeters / bodyRadiusMeters : 1.0;

        /// <summary>可见云壳也必须处于近裁面之外；只收紧当前近裁距离，不放大或修改相机位置。</summary>
        public static double LimitNearClip(double currentNearView, double shellGapView, double nearMinView)
            => shellGapView > 0.0
                ? Math.Min(currentNearView, Math.Max(nearMinView, shellGapView * 0.8)) : currentNearView;

        /// <summary>10 km 及以下不可见，10–15 km 平滑过渡到完整云层；依据相机高度。</summary>
        public static double Opacity(double cameraAltitudeMeters)
            => Opacity(cameraAltitudeMeters, AltitudeMeters, FadeRangeMeters);
        public static double Opacity(double cameraAltitudeMeters, double altitudeMeters, double fadeRangeMeters)
        {
            if (!double.IsFinite(cameraAltitudeMeters) || !double.IsFinite(altitudeMeters) || !double.IsFinite(fadeRangeMeters) || fadeRangeMeters <= 0) return 0.0;
            double t = Math.Clamp((cameraAltitudeMeters - altitudeMeters) / fadeRangeMeters, 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }

        /// <summary>先在 double 中取模再传给 GPU，长存档仍保留小步进；同一模拟时刻始终给同一相位。</summary>
        public static double AnimationPhase(double tick, double periodTicks)
        {
            if (!double.IsFinite(tick) || !double.IsFinite(periodTicks) || periodTicks <= 0.0) return 0.0;
            double phase = tick % periodTicks / periodTicks;
            return phase < 0.0 ? phase + 1.0 : phase;
        }
    }
}
