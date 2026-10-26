using System;
using UnityEngine;

namespace PioneerFramework
{
    internal static class PFViewCelestialLighting
    {
        /// <summary>先在绝对系求位置差，再转换为视图系方向；不把不同天体归并到根行星。</summary>
        public static Vector3 Direction(DVector3 lightPosition, DVector3 samplePosition, Quaternion viewFrame)
        {
            DVector3 delta = lightPosition - samplePosition;
            double scale = Math.Max(Math.Abs(delta.x), Math.Max(Math.Abs(delta.y), Math.Abs(delta.z)));
            if (!double.IsFinite(scale) || scale <= 1e-12) return viewFrame * -Vector3.forward;
            return viewFrame * (Vector3)(delta / scale).normalized;
        }
    }
}
