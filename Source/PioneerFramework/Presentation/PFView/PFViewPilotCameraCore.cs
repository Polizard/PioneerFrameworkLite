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
        internal const float MinLookPitch = -85f;
        internal const float MaxLookPitch = 85f;

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

        internal static float LookPitch(float pitch)
            => float.IsFinite(pitch) ? Math.Clamp(pitch, MinLookPitch, MaxLookPitch) : 15;

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

    /// <summary>
    /// 右键拖拽只积累尚未显示的角位移，每个渲染帧按真实时间消耗。
    /// 40 ms 指数收敛吸收输入抖动；反向输入清除同轴旧余量，限位处不积累隐藏位移。
    /// 余量独立于显示角度的整圈归一化，编队自由视角可连续越过极点。
    /// </summary>
    internal sealed class PFViewOrbitDragState
    {
        private float pendingYaw;
        private float pendingPitch;

        public void AddDrag(float yaw, float pitch, float deltaYaw, float deltaPitch, PFViewFocusKind kind, bool thirdPerson)
        {
            float nextYaw = Accumulate(pendingYaw, deltaYaw);
            if (float.IsFinite(yaw) && float.IsFinite(nextYaw) && float.IsFinite(yaw + nextYaw))
                pendingYaw = nextYaw;
            float nextPitch = Accumulate(pendingPitch, deltaPitch);
            if (float.IsFinite(pitch) && float.IsFinite(nextPitch) && float.IsFinite(pitch + nextPitch))
            {
                float target = pitch + nextPitch;
                if (thirdPerson) target = PFViewPilotCameraCore.LookPitch(target);
                else if (kind == PFViewFocusKind.Body) target = PFViewCameraCore.ResolvePitch(kind, target);
                pendingPitch = target - pitch;
            }
        }

        public (float Yaw, float Pitch) Advance(float yaw, float pitch, float dt)
        {
            if (!float.IsFinite(dt) || dt <= 0) return (yaw, pitch);
            float blend = (float)(1 - Math.Exp(-dt / .04));
            return (yaw + Consume(ref pendingYaw, blend), pitch + Consume(ref pendingPitch, blend));
        }

        public void Reset() => pendingYaw = pendingPitch = 0;

        private static float Accumulate(float pending, float delta)
            => (pending > 0 && delta < 0) || (pending < 0 && delta > 0) ? delta : pending + delta;

        private static float Consume(ref float pending, float blend)
        {
            float step = pending * blend;
            float remaining = pending - step;
            if (MathF.Abs(remaining) <= .0001f)
            {
                step = pending;
                remaining = 0;
            }
            pending = remaining;
            return step;
        }
    }
}
