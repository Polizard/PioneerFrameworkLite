using System;
using UnityEngine;

namespace PioneerFramework
{
    internal readonly record struct PFViewPilotCameraOptions(bool FollowRoll, bool InvertMouseY, bool InvertWheel,
        bool Recenter, float Sensitivity, float Smoothing)
    {
        internal static PFViewPilotCameraOptions Default => new(false, false, false, false, 1, .10f);
    }

    /// <summary>Managed camera math; does not construct a camera or initialize PFView rendering resources.</summary>
    internal static class PFViewPilotCameraCore
    {
        internal static Quaternion TransportHeading(Quaternion previous, Vector3 forward)
        {
            previous = PFViewCameraCore.UnitRotation(previous);
            float maximum = MathF.Max(MathF.Abs(forward.x), MathF.Max(MathF.Abs(forward.y), MathF.Abs(forward.z)));
            if (!float.IsFinite(maximum) || maximum <= 1e-6f) return previous;
            forward /= maximum;
            float length = MathF.Sqrt(forward.x * forward.x + forward.y * forward.y + forward.z * forward.z);
            if (!float.IsFinite(length) || length <= 1e-6f) return previous;
            forward /= length;
            var delta = PFViewCameraCore.MinimalRotation(previous * Vector3.forward, forward, previous * Vector3.up);
            return PFViewCameraCore.UnitRotation(delta * previous);
        }

        internal static Quaternion SmoothFollow(Quaternion current, Quaternion target, float dt, float timeConstant)
        {
            if (!float.IsFinite(dt) || dt <= 0) return current;
            float blend = !float.IsFinite(timeConstant) || timeConstant <= 0 ? 1 : 1 - MathF.Exp(-dt / timeConstant);
            return PFViewCameraCore.InterpolateRotation(current, target, blend);
        }

        internal static float LookYaw(float yaw)
            => float.IsFinite(yaw) ? (yaw % 360 + 540) % 360 - 180 : 0;

        internal static float LookPitch(float pitch) => float.IsFinite(pitch) ? Math.Clamp(pitch, -85, 85) : 15;

        internal static (float Yaw, float Pitch) LookDrag(float yaw, float pitch, float dx, float dy, float sensitivity, bool invertY)
            => (yaw + dx * sensitivity, pitch + dy * sensitivity * (invertY ? -1 : 1));

        internal static (float Yaw, float Pitch) RecenterLook(float yaw, float pitch, float dt)
        {
            if (!float.IsFinite(dt) || dt <= 0) return (LookYaw(yaw), LookPitch(pitch));
            float decay = MathF.Exp(-dt / .18f);
            float nextYaw = LookYaw(yaw) * decay, nextPitch = 15 + (LookPitch(pitch) - 15) * decay;
            return (MathF.Abs(nextYaw) < .01f ? 0 : nextYaw, MathF.Abs(nextPitch - 15) < .01f ? 15 : nextPitch);
        }

        internal static Vector3 PanDelta(Vector3 right, Vector3 up, float dx, float dy, float worldPerPixel)
            => (-right * dx + up * dy) * worldPerPixel;
    }
}
