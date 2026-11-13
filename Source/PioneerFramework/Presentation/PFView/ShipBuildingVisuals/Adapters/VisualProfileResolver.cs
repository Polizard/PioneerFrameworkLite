using System;
using System.Collections.Generic;
using UnityEngine;

namespace PioneerFramework.PFView.ShipBuildingVisuals;

public readonly record struct VisualNodeStyle(Color Color, bool DetailedOnly);

public sealed class ResolvedVisualProfile
{
    public CompiledVisualProfile Core { get; }
    public IReadOnlyList<VisualMeshData?> Meshes { get; }
    public IReadOnlyList<VisualNodeStyle> Styles { get; }
    public float DetailedPixels { get; }
    public float HiddenPixels { get; }
    public bool FactionTint { get; }
    internal ResolvedVisualProfile(CompiledVisualProfile core, VisualMeshData?[] meshes, VisualNodeStyle[] styles, ShipBuildingVisualDef source)
    { Core = core; Meshes = Array.AsReadOnly(meshes); Styles = Array.AsReadOnly(styles); DetailedPixels = source.detailedPixels; HiddenPixels = source.hiddenPixels; FactionTint = source.factionTint; }
}

public sealed class VisualProfileResolver(ShipBuildingVisualRegistry registry, Action<string> warn)
{
    private readonly Dictionary<ShipBuildingVisualDef, ResolvedVisualProfile?> cache = new();
    public bool TryResolve(ShipBuildingVisualDef profile, out ResolvedVisualProfile resolved)
    {
        if (profile == null) { resolved = null!; return false; }
        if (cache.TryGetValue(profile, out var cached)) { resolved = cached!; return cached != null; }
        registry.Freeze();
        try
        {
            foreach (string error in profile.VisualErrors()) throw new ArgumentException(error);
            var core = profile.CompileStructure();
            var nodes = new Dictionary<string, VisualNodeXml>(StringComparer.Ordinal);
            foreach (var node in profile.nodes) nodes.Add(node.id, node);
            var meshes = new VisualMeshData?[core.NodeCount];
            var styles = new VisualNodeStyle[core.NodeCount];
            for (int i = 0; i < core.NodeCount; i++)
            {
                var node = nodes[core.Nodes[i].Id];
                styles[i] = new(node.color, node.detailedOnly);
                if (string.IsNullOrEmpty(node.mesh)) continue;
                if (!registry.TryGetMeshProvider(node.meshProvider, out var provider)) throw new ArgumentException("Unknown mesh provider: " + node.meshProvider);
                var data = provider.GetMesh(node.mesh);
                ValidateMesh(data);
                // Own a snapshot, so a third-party provider cannot alter an already compiled profile.
                meshes[i] = new((System.Numerics.Vector3[])data.Positions.Clone(), (System.Numerics.Vector3[])data.Normals.Clone(),
                    (System.Numerics.Vector2[])data.Uvs.Clone(), (int[])data.Indices.Clone());
            }
            resolved = new(core, meshes, styles, profile);
            cache.Add(profile, resolved);
            return true;
        }
        catch (Exception e)
        {
            cache.Add(profile, null);
            warn("[PFL·BuildingVisual] " + profile.defName + ": " + e.Message);
            resolved = null!;
            return false;
        }
    }

    public static void ValidateMesh(VisualMeshData data)
    {
        if (data?.Positions == null || data.Normals == null || data.Uvs == null || data.Indices == null
            || data.Positions.Length is < 3 or > 65536 || data.Normals.Length != data.Positions.Length
            || data.Uvs.Length != data.Positions.Length || data.Indices.Length is < 3 or > 196608 || data.Indices.Length % 3 != 0)
            throw new ArgumentException("Invalid mesh attribute lengths.");
        for (int i = 0; i < data.Positions.Length; i++)
        {
            var p = data.Positions[i]; var n = data.Normals[i]; var uv = data.Uvs[i];
            if (!VisualMath.Finite(p) || !VisualMath.Finite(n) || n.LengthSquared() < 1e-12f || !float.IsFinite(uv.X) || !float.IsFinite(uv.Y)
                || MathF.Abs(p.X) > .50001f || MathF.Abs(p.Y) > .50001f || MathF.Abs(p.Z) > .50001f)
                throw new ArgumentException("Mesh must have finite attributes within the unit cube.");
        }
        foreach (int index in data.Indices) if ((uint)index >= (uint)data.Positions.Length) throw new ArgumentException("Invalid mesh index.");
    }
}
