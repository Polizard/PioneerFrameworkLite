using System;
using UnityEngine;
using Verse;
using NQuaternion = System.Numerics.Quaternion;

namespace PioneerFramework.PFView.ShipBuildingVisuals;

/// <summary>Read-only sampling contract. Providers must never tick, fire or mutate their source.</summary>
public readonly record struct VisualStateContext(Thing? Building, SpaceEntity? Entity, SpaceEntityGroup? Group,
    NQuaternion InstallationRotationShip, double RenderTick);

public interface IShipBuildingVisualStateProvider
{
    VisualState Sample(in VisualStateContext context);
}

/// <summary>Unit-cube CPU mesh, immutable until registry disposal. No engine object ownership transfer.</summary>
public interface IShipBuildingVisualMeshProvider
{
    VisualMeshData GetMesh(string name);
}

public static class VisualConvert
{
    public static System.Numerics.Vector3 N(Vector3 v) => new(v.x, v.y, v.z);
    public static Vector3 U(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
    public static NQuaternion N(Quaternion q) => new(q.x, q.y, q.z, q.w);
    public static Quaternion U(NQuaternion q) => new(q.X, q.Y, q.Z, q.W);
    public static NQuaternion N(DQuaternion q) => new((float)q.x, (float)q.y, (float)q.z, (float)q.w);
    public static Matrix4x4 U(System.Numerics.Matrix4x4 m)
    {
        // Numerics uses row vectors; Unity uses column vectors.
        var u = new Matrix4x4();
        u.m00 = m.M11; u.m01 = m.M21; u.m02 = m.M31; u.m03 = m.M41;
        u.m10 = m.M12; u.m11 = m.M22; u.m12 = m.M32; u.m13 = m.M42;
        u.m20 = m.M13; u.m21 = m.M23; u.m22 = m.M33; u.m23 = m.M43;
        u.m30 = m.M14; u.m31 = m.M24; u.m32 = m.M34; u.m33 = m.M44;
        return u;
    }
}
