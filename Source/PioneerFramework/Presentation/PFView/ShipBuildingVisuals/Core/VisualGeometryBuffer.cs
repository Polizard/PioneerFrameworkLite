using System;
using System.Collections.Generic;
using System.Numerics;

namespace PioneerFramework.PFView.ShipBuildingVisuals;

/// <summary>Reusable CPU geometry, preserving each vertex attribute and inverse-transpose normals.</summary>
public sealed class VisualGeometryBuffer
{
    public List<Vector3> Positions { get; } = new();
    public List<Vector3> Normals { get; } = new();
    public List<Vector2> Uvs { get; } = new();
    public List<int> Indices { get; } = new();
    public void Clear() { Positions.Clear(); Normals.Clear(); Uvs.Clear(); Indices.Clear(); }
    internal static bool TryNormalMatrix(in Matrix4x4 transform, out Matrix4x4 normalMatrix)
    {
        bool invertible = Matrix4x4.Invert(transform, out var inverse);
        normalMatrix = Matrix4x4.Transpose(inverse);
        return invertible && VisualMath.Finite(inverse.Translation)
            && VisualMath.Finite(new(inverse.M11, inverse.M12, inverse.M13))
            && VisualMath.Finite(new(inverse.M21, inverse.M22, inverse.M23))
            && VisualMath.Finite(new(inverse.M31, inverse.M32, inverse.M33));
    }

    public bool TryAppend(VisualMeshData mesh, in Matrix4x4 transform)
    {
        if (!TryNormalMatrix(transform, out var normalMatrix)) return false;
        int start = Positions.Count;
        Positions.EnsureCapacity(start + mesh.Positions.Length);
        Normals.EnsureCapacity(start + mesh.Positions.Length);
        Uvs.EnsureCapacity(start + mesh.Positions.Length);
        Indices.EnsureCapacity(Indices.Count + mesh.Indices.Length);
        for (int i = 0; i < mesh.Positions.Length; i++)
        {
            var position = Vector3.Transform(mesh.Positions[i], transform);
            var normal = Vector3.Normalize(Vector3.TransformNormal(mesh.Normals[i], normalMatrix));
            if (!VisualMath.Finite(position) || !VisualMath.Finite(normal) || normal == Vector3.Zero)
            {
                Positions.RemoveRange(start, i);
                Normals.RemoveRange(start, i);
                Uvs.RemoveRange(start, i);
                return false;
            }
            Positions.Add(position);
            Normals.Add(normal);
            Uvs.Add(mesh.Uvs[i]);
        }
        for (int i = 0; i < mesh.Indices.Length; i++) Indices.Add(start + mesh.Indices[i]);
        return true;
    }
}

/// <summary>Unbounded slots within a frame; alternate generations retain queued mesh references.</summary>
public sealed class VisualFrameSlots
{
    private int frame = int.MinValue;
    private int generation;
    private int slot;
    public int Acquire(int engineFrame)
    {
        if (engineFrame != frame) { frame = engineFrame; generation ^= 1; slot = 0; }
        return checked(slot++ * 2 + generation);
    }
}
