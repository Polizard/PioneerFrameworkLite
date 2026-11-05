using System;
using UnityEngine;

namespace PioneerFramework
{
    /// <summary>恒星材质的局部采样和动画参数；不参与相机取景。</summary>
    internal static class PFViewStellarVisualCore
    {
        public const float Extent = 2.1f;
        public const int SurfaceSteps = 64;
        public const int HaloSteps = SurfaceSteps;
        public const float FlowSpeed = 2f;

        public static Vector3 Eye(Vector3 camera, Vector3 center, Quaternion rotation, float radius)
            => Quaternion.Inverse(rotation) * (camera - center) / MathF.Max(radius, .0001f);

        public static PFViewStellarFrame Frame(double tick)
        {
            float phase = Phase(tick);
            float flow = phase * 17f * FlowSpeed;
            return new PFViewStellarFrame(phase,
                new Vector4(MathF.Sin(flow), MathF.Cos(flow), MathF.Sin(flow * 2f + 1.3f), 0f),
                new Vector4(ArchHeight(phase, .27f, .4f), ArchHeight(phase, .31f, 1.8f),
                    ArchHeight(phase, .22f, 3.2f), ArchHeight(phase, .23f, 4.7f)),
                new Vector4(ArchHeight(phase, .19f, 1.1f), ArchHeight(phase, .28f, 5.6f),
                    ArchHeight(phase, .23f, 2.5f), ArchHeight(phase, .17f, 3.9f)));
        }

        private static float ArchHeight(float phase, float height, float offset)
            => height * (.85f + .15f * MathF.Sin(phase * 7f + offset));

        public static float Phase(double tick)
        {
            if (!double.IsFinite(tick)) return 0f;
            double wrapped = tick % 36000.0;
            if (wrapped < 0) wrapped += 36000.0;
            return (float)(wrapped * (Math.PI * 2.0 / 36000.0));
        }
    }

    internal readonly record struct PFViewStellarFrame(
        float Phase, Vector4 Drift, Vector4 ArchHeight0, Vector4 ArchHeight1);
}
