using System;
using System.Collections.Generic;
using System.Numerics;
using Verse;

namespace PioneerFramework.PFView.ShipBuildingVisuals;

public readonly record struct MapVisualContext(Map Map, SpacePart? OwnerPart)
{
    public bool AllowsRelief => OwnerPart?.isSealed != true;
    public Quaternion RotationShip => OwnerPart == null ? Quaternion.Identity : VisualConvert.N(OwnerPart.rotationShip);
    public Vector3 PositionShip => OwnerPart == null ? Vector3.Zero : new((float)OwnerPart.positionShip.x, (float)OwnerPart.positionShip.y, (float)OwnerPart.positionShip.z);
    public Matrix4x4 MapToShip => Matrix4x4.CreateFromQuaternion(RotationShip) * Matrix4x4.CreateTranslation(PositionShip);
}

public readonly record struct VisualAttachment(Vector3 PositionShip, Quaternion RotationShip)
{
    public Matrix4x4 Matrix => Matrix4x4.CreateFromQuaternion(RotationShip) * Matrix4x4.CreateTranslation(PositionShip);
}

public static class VisualSurfaceResolver
{
    public static bool TryResolve(Building building, BuildingVisualMount mount, in MapVisualContext context,
        IReadOnlyList<VisualSurfaceBox>? relief, bool rocketHull, out VisualAttachment attachment)
    {
        attachment = default;
        if (!mount.IsValid) return false;
        var rect = building.OccupiedRect();
        var center = context.Map.Center;
        var local = VisualMath.FootprintCenter(rect.minX, rect.minZ, rect.maxX, rect.maxZ, center.x, center.z);
        bool bottom = mount.surface == VisualMountSurface.Bottom;
        float yaw = mount.followBuildingRotation ? building.Rotation.AsAngle : 0;
        var rotationOffset = Quaternion.CreateFromYawPitchRoll(mount.rotation.y * VisualMath.DegToRad,
            mount.rotation.x * VisualMath.DegToRad, mount.rotation.z * VisualMath.DegToRad);
        var mountRotation = VisualMath.MountRotation(bottom, yaw) * rotationOffset;
        if (mount.surface == VisualMountSurface.Explicit)
        {
            attachment = new(VisualConvert.N(mount.explicitPositionShip) + Vector3.Transform(VisualConvert.N(mount.offset), mountRotation), mountRotation);
            return true;
        }
        float height;
        if (relief != null)
        {
            if (!VisualSurface.TryHeight(relief, rect.minX - center.x - .5f, rect.minZ - center.z - .5f,
                rect.maxX - center.x + .5f, rect.maxZ - center.z + .5f, bottom, out height)) return false;
        }
        else
        {
            var owner = context.OwnerPart;
            if (rocketHull || owner == null || owner.isDestroyed) return false;
            float halfX = (float)owner.boundsSizeShip.x * .5f, halfZ = (float)owner.boundsSizeShip.z * .5f;
            if (MathF.Abs(local.X) > halfX || MathF.Abs(local.Z) > halfZ) return false;
            height = (float)owner.boundsSizeShip.y * (bottom ? -.5f : .5f);
            if (!float.IsFinite(height)) return false;
        }
        local.Y = height + (bottom ? -mount.surfaceOffset : mount.surfaceOffset);
        local += Vector3.Transform(VisualConvert.N(mount.offset), mountRotation);
        attachment = new(Vector3.Transform(local, context.MapToShip), Quaternion.Normalize(context.RotationShip * mountRotation));
        return true;
    }
}
