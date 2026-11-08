using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace PioneerFramework.PFView.ShipBuildingVisuals;

/// <summary>Owned GPU meshes; no same-frame slot wrap and no dependence on the backend batch pool.</summary>
internal sealed class VisualBatch : IDisposable
{
    private readonly VisualFrameSlots slots = new();
    private readonly List<Mesh?> pool = new();
    private readonly List<Vector3> positions = new();
    private readonly List<Vector3> normals = new();
    private readonly List<Vector2> uvs = new();
    private readonly List<(Mesh Mesh, int Frame)> retired = new();

    public Mesh Build(VisualGeometryBuffer data)
    {
        var mesh = new Mesh { name = "PF_BuildingVisual_Static" };
        Upload(mesh, data);
        return mesh;
    }
    public Mesh Dynamic(VisualGeometryBuffer data, int frame)
    {
        int slot = slots.Acquire(frame);
        while (pool.Count <= slot) pool.Add(null);
        var mesh = pool[slot] ??= new Mesh { name = "PF_BuildingVisual_Dynamic" };
        Upload(mesh, data);
        return mesh;
    }
    private void Upload(Mesh mesh, VisualGeometryBuffer data)
    {
        positions.Clear(); normals.Clear(); uvs.Clear();
        for (int i = 0; i < data.Positions.Count; i++)
        {
            positions.Add(VisualConvert.U(data.Positions[i]));
            normals.Add(VisualConvert.U(data.Normals[i]));
            uvs.Add(new(data.Uvs[i].X, data.Uvs[i].Y));
        }
        mesh.Clear();
        if (positions.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
        mesh.SetVertices(positions); mesh.SetNormals(normals); mesh.SetUVs(0, uvs);
        mesh.SetTriangles(data.Indices, 0); mesh.RecalculateBounds();
    }
    public void Retire(Mesh mesh, int frame) => retired.Add((mesh, frame));
    public void Collect(int frame)
    {
        for (int i = retired.Count - 1; i >= 0; i--)
            if (frame - retired[i].Frame >= 2) { Object.Destroy(retired[i].Mesh); retired.RemoveAt(i); }
    }
    public void Dispose()
    {
        foreach (var mesh in pool) if (mesh != null) Object.Destroy(mesh);
        foreach (var entry in retired) Object.Destroy(entry.Mesh);
        pool.Clear(); retired.Clear(); positions.Clear(); normals.Clear(); uvs.Clear();
    }
}
