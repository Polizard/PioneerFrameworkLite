using UnityEngine;

namespace PioneerFramework
{
    // Keep CPU geometry independent of the window's GPU-initializing static constructor.
    internal static class PFViewOrbitLineGeometry
    {
        internal static bool TryBuildMatrix(Vector3 a, Vector3 b, float width, out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            float length = (b - a).magnitude;
            if (length <= 1e-6f || width <= 0f || !float.IsFinite(length) || !float.IsFinite(width)) return false;
            matrix = Matrix4x4.TRS((a + b) * 0.5f, Quaternion.LookRotation(b - a), new Vector3(width, width, length));
            return true;
        }
    }
}
