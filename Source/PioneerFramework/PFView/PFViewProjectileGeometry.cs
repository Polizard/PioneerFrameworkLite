using System;
using UnityEngine;

namespace PioneerFramework.ProjectileVisuals
{
    internal readonly record struct ProjectileStreakQuad(
        Vector3 TailLeft, Vector3 TailRight, Vector3 HeadLeft, Vector3 HeadRight,
        float HalfLengthInRadii, float Coverage);

    /// <summary>Perspective projection in camera space with positive forward Z and centered pixel coordinates.</summary>
    internal readonly record struct ProjectileProjection(
        int Width, int Height, float FocalPixels, float Near, float Far)
    {
        internal static DVector3 AdjustTailForMotion(
            DVector3 sampledTail, DVector3 bodyVelocity, DVector3 cameraVelocity, double durationSeconds)
            => sampledTail + (cameraVelocity - bodyVelocity) * durationSeconds;

        internal static DVector3 CarrierVelocity(
            DVector3 previous, DVector3 current, DVector3 velocity, double previousTick, double currentTick, double tick)
            => double.IsFinite(previousTick) && double.IsFinite(currentTick) &&
                previousTick < currentTick && tick >= previousTick && tick < currentTick
                ? (current - previous) / (currentTick - previousTick)
                : velocity;

        internal bool TryBuild(Vector3 head, Vector3 tail, in ProjectileStreakStyle style, out ProjectileStreakQuad quad)
            => TryBuild(head, tail, style.WidthPixels, style.MaxLengthPixels, out quad);

        internal bool TryBuildImpact(Vector3 head, Vector3 tail, float radiusPixels, out ProjectileStreakQuad quad)
            => TryBuild(head, tail, radiusPixels * 2f, 64f, out quad);

        private bool TryBuild(Vector3 head, Vector3 tail, float widthPixels, float maxLengthPixels, out ProjectileStreakQuad quad)
        {
            quad = default;
            if (!IsFinite(head) || !IsFinite(tail) || FocalPixels <= 0f ||
                !ClipDepth(ref head, ref tail, Near, keepGreater: true) ||
                !ClipDepth(ref head, ref tail, Far, keepGreater: false))
                return false;

            Vector2 headPixels = ToPixels(head);
            Vector2 tailPixels = ToPixels(tail);
            Vector2 line = headPixels - tailPixels;
            float length = line.magnitude;
            if (!float.IsFinite(length)) return false;

            float scale = Height / 1080f;
            float requestedRadius = widthPixels * scale * 0.5f;
            // A smaller soft dot can miss every pixel center on a low-resolution render target.
            float radius = Math.Max(1f, requestedRadius);
            float coverage = requestedRadius / radius;
            float limitedLength = Math.Min(length, maxLengthPixels * scale);
            Vector2 direction = length > 0.0001f ? line / length : Vector2.right;
            float fraction = length > 0.0001f ? limitedLength / length * Math.Min(1f, length / radius) : 0f;
            // Screen interpolation is linear in reciprocal depth. Keep occlusion correct at the shortened endpoint.
            float tailDepth = 1f / ((1f - fraction) / head.z + fraction / tail.z);
            limitedLength = length * fraction;
            tailPixels = headPixels - direction * limitedLength;
            if (Math.Max(headPixels.x, tailPixels.x) < -Width * 0.5f - radius ||
                Math.Min(headPixels.x, tailPixels.x) > Width * 0.5f + radius ||
                Math.Max(headPixels.y, tailPixels.y) < -Height * 0.5f - radius ||
                Math.Min(headPixels.y, tailPixels.y) > Height * 0.5f + radius)
                return false;

            Vector2 across = new Vector2(-direction.y, direction.x) * radius;
            Vector2 backCap = tailPixels - direction * radius;
            Vector2 frontCap = headPixels + direction * radius;
            quad = new(ToEye(backCap - across, tailDepth), ToEye(backCap + across, tailDepth),
                ToEye(frontCap - across, head.z), ToEye(frontCap + across, head.z),
                limitedLength / (2f * radius), coverage);
            return true;
        }

        private Vector2 ToPixels(Vector3 eye) => new Vector2(eye.x, eye.y) * (FocalPixels / eye.z);

        private Vector3 ToEye(Vector2 pixels, float depth)
        {
            Vector2 xy = pixels * (depth / FocalPixels);
            return new(xy.x, xy.y, depth);
        }

        private static bool IsFinite(Vector3 value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        private static bool ClipDepth(ref Vector3 a, ref Vector3 b, float depth, bool keepGreater)
        {
            bool outsideA = keepGreater ? a.z < depth : a.z > depth;
            bool outsideB = keepGreater ? b.z < depth : b.z > depth;
            if (outsideA && outsideB) return false;
            if (outsideA == outsideB) return true;
            Vector3 intersection = Vector3.Lerp(a, b, (depth - a.z) / (b.z - a.z));
            intersection.z = depth;
            if (outsideA) a = intersection;
            else b = intersection;
            return true;
        }
    }
}
