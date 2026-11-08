using System;
using System.Numerics;

namespace PioneerFramework.PFView.ShipBuildingVisuals;

public static class VisualMath
{
    public const float DegToRad = MathF.PI / 180;
    public static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    public static Vector3 FootprintCenter(int minX, int minZ, int maxX, int maxZ, int centerX, int centerZ)
        => new(((float)minX + maxX) * .5f - centerX, 0, ((float)minZ + maxZ) * .5f - centerZ);
    public static Vector3 YawDirection(float degrees) => new(MathF.Sin(degrees * DegToRad), 0, MathF.Cos(degrees * DegToRad));
    public static float DeltaAngle(float from, float to) => ((to - from) % 360 + 540) % 360 - 180;
    public static Quaternion MountRotation(bool bottom, float yawDegrees)
        => Quaternion.CreateFromAxisAngle(Vector3.UnitX, bottom ? MathF.PI : 0)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawDegrees * DegToRad);

    public static VisualChannels LocalAngles(Vector3 direction, Quaternion mount, out float yaw, out float pitch)
    {
        yaw = pitch = 0;
        float directionLength = direction.LengthSquared();
        float mountLength = mount.LengthSquared();
        if (!float.IsFinite(directionLength) || directionLength < 1e-12f
            || !float.IsFinite(mountLength) || mountLength < 1e-12f)
            return VisualChannels.None;
        var local = Vector3.Transform(Vector3.Normalize(direction), Quaternion.Inverse(mount));
        float horizontal = MathF.Sqrt(local.X * local.X + local.Z * local.Z);
        pitch = -MathF.Atan2(local.Y, horizontal) / DegToRad;
        if (horizontal < 1e-6f) return VisualChannels.Pitch;
        yaw = MathF.Atan2(local.X, local.Z) / DegToRad;
        return VisualChannels.Yaw | VisualChannels.Pitch;
    }

    public static VisualLod SelectLod(float pixels, VisualLod previous, float detailed, float hidden)
    {
        if (!float.IsFinite(pixels) || pixels <= 0) return VisualLod.Hidden;
        float visibleThreshold = previous == VisualLod.Hidden ? hidden * 1.2f : hidden;
        if (pixels < visibleThreshold) return VisualLod.Hidden;
        float detailThreshold = previous == VisualLod.Detailed ? detailed : detailed * 1.2f;
        return pixels >= detailThreshold ? VisualLod.Detailed : VisualLod.Simplified;
    }
}
