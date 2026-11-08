using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;

namespace PioneerFramework.PFView.ShipBuildingVisuals;

[Flags]
public enum VisualChannels { None = 0, Yaw = 1, Pitch = 2, Active = 4, Damage = 8, Deploy = 16, Scan = 32 }
public enum VisualPrimitive { Box, Cylinder, Dish }
public enum VisualLod { Hidden, Simplified, Detailed }

/// <summary>Angles are mount-local degrees. Missing channels retain the configured rest pose.</summary>
public readonly record struct VisualState
{
    public float Yaw { get; init; }
    public float Pitch { get; init; }
    public bool Active { get; init; }
    public float DamageFraction { get; init; }
    public float DeployFraction { get; init; }
    public float ScanFraction { get; init; }
    public VisualChannels Channels { get; init; }
}

/// <summary>Immutable node in metres, +Y out of the mount and +Z forward. Scale is positive.</summary>
public sealed record VisualNode(string Id)
{
    public string ParentId { get; init; } = "";
    public Vector3 Position { get; init; }
    public Quaternion Rotation { get; init; } = Quaternion.Identity;
    public Vector3 Scale { get; init; } = Vector3.One;
    public Vector3 Pivot { get; init; }
    public bool BindYaw { get; init; }
    public bool BindPitch { get; init; }
    public bool BindDeploy { get; init; }
    public bool BindScan { get; init; }
    public bool ActiveOnly { get; init; }
}

/// <summary>Pure, validated topological plan. Numerics matrices use row-vector composition.</summary>
public sealed class CompiledVisualProfile
{
    public const int MaxNodes = 32;
    public ReadOnlyCollection<VisualNode> Nodes { get; }
    public int NodeCount => Nodes.Count;
    public float MotionRadius { get; }
    private readonly int[] parents;

    private CompiledVisualProfile(VisualNode[] nodes, int[] parents, float radius)
    { Nodes = Array.AsReadOnly(nodes); this.parents = parents; MotionRadius = radius; }

    public static CompiledVisualProfile Compile(IReadOnlyList<VisualNode> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count is < 1 or > MaxNodes) throw new ArgumentException("Visual requires 1..32 nodes.", nameof(source));
        var byId = new Dictionary<string, VisualNode>(StringComparer.Ordinal);
        for (int i = 0; i < source.Count; i++)
        {
            var n = source[i];
            if (n == null || string.IsNullOrWhiteSpace(n.Id) || !byId.TryAdd(n.Id, n)
                || !VisualMath.Finite(n.Position) || !VisualMath.Finite(n.Scale) || !VisualMath.Finite(n.Pivot)
                || n.Scale.X <= 0 || n.Scale.Y <= 0 || n.Scale.Z <= 0
                || !float.IsFinite(n.Rotation.LengthSquared()) || n.Rotation.LengthSquared() < 1e-12f)
                throw new ArgumentException("Invalid node ID or transform.", nameof(source));
        }
        var ordered = new List<VisualNode>(source.Count);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        void Visit(VisualNode node)
        {
            if (indices.ContainsKey(node.Id)) return;
            if (!visiting.Add(node.Id)) throw new ArgumentException("Visual node cycle.", nameof(source));
            if (!string.IsNullOrEmpty(node.ParentId))
            {
                if (!byId.TryGetValue(node.ParentId, out var parent)) throw new ArgumentException("Unknown parent node.", nameof(source));
                Visit(parent);
            }
            visiting.Remove(node.Id);
            indices.Add(node.Id, ordered.Count);
            ordered.Add(node with { Rotation = Quaternion.Normalize(node.Rotation) });
        }
        for (int i = 0; i < source.Count; i++) Visit(source[i]);
        var parents = new int[ordered.Count];
        var reaches = new float[ordered.Count];
        var scales = new float[ordered.Count];
        float radius = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            var n = ordered[i];
            int p = string.IsNullOrEmpty(n.ParentId) ? -1 : indices[n.ParentId];
            parents[i] = p;
            float parentScale = p < 0 ? 1 : scales[p];
            scales[i] = parentScale * MathF.Max(n.Scale.X, MathF.Max(n.Scale.Y, n.Scale.Z));
            reaches[i] = (p < 0 ? 0 : reaches[p]) + parentScale * (n.Position.Length() + 2 * n.Pivot.Length());
            radius = MathF.Max(radius, reaches[i] + scales[i] * .8660254f);
            if (!float.IsFinite(radius)) throw new ArgumentException("Visual transform overflow.", nameof(source));
        }
        var profile = new CompiledVisualProfile(ordered.ToArray(), parents, radius);
        Span<Matrix4x4> restPose = stackalloc Matrix4x4[ordered.Count];
        profile.Evaluate(default, restPose);
        foreach (var matrix in restPose)
            if (!VisualGeometryBuffer.TryNormalMatrix(matrix, out _))
                throw new ArgumentException("Visual node transform is not invertible at render precision.", nameof(source));
        return profile;
    }

    public void Evaluate(in VisualState state, Span<Matrix4x4> matrices)
    {
        if (matrices.Length < NodeCount) throw new ArgumentException("Insufficient pose storage.", nameof(matrices));
        for (int i = 0; i < NodeCount; i++)
        {
            var n = Nodes[i];
            float yaw = n.BindYaw && state.Channels.HasFlag(VisualChannels.Yaw) && float.IsFinite(state.Yaw) ? state.Yaw : 0;
            float pitch = n.BindPitch && state.Channels.HasFlag(VisualChannels.Pitch) && float.IsFinite(state.Pitch) ? state.Pitch : 0;
            float deploy = n.BindDeploy && state.Channels.HasFlag(VisualChannels.Deploy) && float.IsFinite(state.DeployFraction)
                ? Math.Clamp(state.DeployFraction, 0, 1) : 1;
            if (n.BindScan && (state.Channels & VisualChannels.Scan) != 0 && float.IsFinite(state.ScanFraction))
                deploy = Math.Clamp(state.ScanFraction, 0, 1);
            var dynamicRotation = Quaternion.CreateFromYawPitchRoll(yaw * VisualMath.DegToRad, pitch * VisualMath.DegToRad, 0);
            var local = Matrix4x4.CreateTranslation(-n.Pivot) * Matrix4x4.CreateScale(n.Scale)
                * Matrix4x4.CreateFromQuaternion(n.Rotation) * Matrix4x4.CreateFromQuaternion(dynamicRotation)
                * Matrix4x4.CreateTranslation(n.Pivot + n.Position * deploy);
            matrices[i] = parents[i] < 0 ? local : local * matrices[parents[i]];
        }
    }
}
