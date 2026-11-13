using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;
using NMatrix = System.Numerics.Matrix4x4;

namespace PioneerFramework.PFView.ShipBuildingVisuals;

/// <summary>
/// Session-owned presentation cache. Sources are sampled once per engine frame on the host thread.
/// Geometry stays in ship-local metres until the caller's common view transform is applied.
/// </summary>
public sealed class ShipBuildingVisualRenderer : IDisposable
{
    public static bool Enabled = true;
    private readonly ShipBuildingVisualRegistry registry;
    private readonly VisualProfileResolver profiles;
    private readonly VisualStateSampling sampling;
    private readonly Action<string> warn;
    private readonly HashSet<string> reported = new(StringComparer.Ordinal);
    private readonly Dictionary<Map, MapCache> maps = new();
    private readonly List<Map> deadMaps = new();
    private readonly VisualBatch batch = new();
    private int currentFrame = int.MinValue;
    private bool disposed;
    public int InstancesDrawn { get; private set; }
    public int SubmittedBatches { get; private set; }
    public int StaticRebuilds { get; private set; }

    private sealed class Instance
    {
        public Building Building = null!;
        public ResolvedVisualProfile Profile = null!;
        public IShipBuildingVisualStateProvider Provider = null!;
        public VisualAttachment Attachment;
        public NMatrix[] Pose = [];
        public bool[] Dynamic = [];
        public int[] Parents = [];
        public string Identity = "";
        public VisualState State;
        public VisualState EvaluatedState;
        public VisualLod EvaluatedLod = VisualLod.Hidden;
        public VisualLod Lod = VisualLod.Hidden;
        public int SampleFrame = int.MinValue;
    }
    private sealed class MapCache
    {
        public int StructureSignature;
        public bool Initialized;
        public int StaticSignature;
        public bool HasStatic;
        public readonly List<Instance> Instances = new();
        public readonly Dictionary<Color, Mesh> Static = new();
        public readonly Dictionary<Color, VisualGeometryBuffer> StaticBuffers = new();
        public readonly Dictionary<Color, VisualGeometryBuffer> DynamicBuffers = new();
        public readonly List<Color> UnusedColors = new();
    }

    public ShipBuildingVisualRenderer(Action<string> warn, ShipBuildingVisualRegistry? registry = null)
    {
        this.warn = warn;
        this.registry = registry ?? ShipBuildingVisualRegistry.Default;
        profiles = new(this.registry, WarnOnce);
        sampling = new(WarnOnce);
    }
    private void WarnOnce(string message) { if (reported.Add(message)) warn(message); }

    public void BeginFrame(int frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frame == currentFrame) return;
        currentFrame = frame;
        InstancesDrawn = SubmittedBatches = 0;
        batch.Collect(frame);
        deadMaps.Clear();
        foreach (var entry in maps)
            if (!ReferenceEquals(SpaceMapGenerator.FindPocketMap(entry.Key.uniqueID), entry.Key)) deadMaps.Add(entry.Key);
        foreach (var map in deadMaps) { Release(maps[map], frame); maps.Remove(map); }
    }

    /// <summary>Conservative pre-cull radius including every configured attachment and owner-part offset.</summary>
    public float MeasureRadius(in MapVisualContext context)
    {
        var index = context.Map.GetComponent<MapComponent_BuildingVisualIndex>();
        if (index == null) return 0;
        float radius = 0;
        foreach (var building in index.GetBuildings())
        {
            var mounts = building.def.GetModExtension<ShipBuildingVisualExtension>()?.mounts;
            if (mounts == null) continue;
            foreach (var mount in mounts)
            {
                if (mount == null || !mount.IsValid || !profiles.TryResolve(mount.visual, out var profile)) continue;
                var rect = building.OccupiedRect();
                var local = VisualMath.FootprintCenter(rect.minX, rect.minZ, rect.maxX, rect.maxZ, context.Map.Center.x, context.Map.Center.z);
                float reach = mount.surface == VisualMountSurface.Explicit ? mount.explicitPositionShip.magnitude
                    : context.PositionShip.Length() + local.Length()
                        + (context.OwnerPart == null ? 5 : MathF.Max(5, (float)context.OwnerPart.boundsSizeShip.y * .5f));
                radius = MathF.Max(radius, reach + profile.Core.MotionRadius + mount.offset.magnitude + mount.surfaceOffset);
            }
        }
        return radius;
    }

    public void DrawMap(in MapVisualContext context, SpaceEntity entity, SpaceEntityGroup group,
        IReadOnlyList<VisualSurfaceBox>? surfaces, bool rocketHull, Vector3 viewPos, Quaternion viewRotation,
        float metersToView, float scale, Vector3 cameraPosition, Vector3 cameraForward, float focalPixels,
        Material material, MaterialPropertyBlock properties,
        int layer, int frame, double renderTick)
    {
        if (!Enabled || disposed || material == null) return;
        BeginFrame(frame);
        var index = context.Map.GetComponent<MapComponent_BuildingVisualIndex>();
        if (index == null) return;
        var buildings = index.GetBuildings();
        if (!maps.TryGetValue(context.Map, out var cache)) maps.Add(context.Map, cache = new());
        var signature = new HashCode();
        signature.Add(index.Version); signature.Add(surfaces == null ? 0 : RuntimeHelpers.GetHashCode(surfaces)); signature.Add(rocketHull);
        signature.Add(entity.EntityId);
        signature.Add(context.PositionShip);
        signature.Add(context.RotationShip);
        signature.Add(context.OwnerPart?.boundsSizeShip);
        foreach (var b in buildings) { signature.Add(b); signature.Add(b.Position); signature.Add(b.Rotation.AsInt); }
        int structure = signature.ToHashCode();
        if (!cache.Initialized || structure != cache.StructureSignature)
        {
            cache.Instances.Clear();
            foreach (var building in buildings)
            {
                if (!MapComponent_BuildingVisualIndex.IsCandidate(building) || !ReferenceEquals(building.Map, context.Map)) continue;
                var mounts = building.def.GetModExtension<ShipBuildingVisualExtension>()!.mounts;
                var mountIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var mount in mounts)
                {
                    if (mount == null || !mount.IsValid || !mountIds.Add(mount.id))
                    { WarnOnce("[PFL·BuildingVisual] Invalid/duplicate mount on " + building.def.defName); continue; }
                    if (!profiles.TryResolve(mount.visual, out var profile)) continue;
                    if (!registry.TryGetStateProvider(mount.stateProvider, out var provider))
                    { WarnOnce("[PFL·BuildingVisual] Unknown state provider: " + mount.stateProvider); continue; }
                    if (!VisualSurfaceResolver.TryResolve(building, mount, context, surfaces, rocketHull, out var attachment))
                    { WarnOnce("[PFL·BuildingVisual] SurfaceUnavailable: " + building.def.defName + "/" + mount.id); continue; }
                    int count = profile.Core.NodeCount;
                    var instance = new Instance { Building = building, Profile = profile, Provider = provider,
                        Attachment = attachment, Pose = new NMatrix[count], Dynamic = new bool[count], Parents = new int[count],
                        Identity = entity.EntityId + "/" + context.Map.uniqueID + "/" + building.thingIDNumber + "/" + mount.id };
                    for (int i = 0; i < count; i++)
                    {
                        var node = profile.Core.Nodes[i];
                        int parent = -1;
                        for (int j = 0; j < i; j++) if (profile.Core.Nodes[j].Id == node.ParentId) { parent = j; break; }
                        instance.Parents[i] = parent;
                        instance.Dynamic[i] = node.BindYaw || node.BindPitch || node.BindDeploy || node.BindScan || node.ActiveOnly || (parent >= 0 && instance.Dynamic[parent]);
                    }
                    cache.Instances.Add(instance);
                }
            }
            cache.StructureSignature = structure;
            cache.Initialized = true;
            cache.HasStatic = false;
        }
        var staticSignature = new HashCode();
        staticSignature.Add(group.FactionId);
        float viewScale = metersToView * scale;
        foreach (var instance in cache.Instances)
        {
            if (instance.SampleFrame != frame)
            {
                var input = new VisualStateContext(instance.Building, entity, group, instance.Attachment.RotationShip, renderTick);
                var sampled = sampling.Sample(instance.Provider, input, instance.Identity);
                // Missing/degenerate direction retains the last trusted joint, including at the poles.
                if ((sampled.Channels & VisualChannels.Yaw) == 0 && (instance.State.Channels & VisualChannels.Yaw) != 0)
                    sampled = sampled with { Yaw = instance.State.Yaw, Channels = sampled.Channels | VisualChannels.Yaw };
                if ((sampled.Channels & VisualChannels.Pitch) == 0 && (instance.State.Channels & VisualChannels.Pitch) != 0)
                    sampled = sampled with { Pitch = instance.State.Pitch, Channels = sampled.Channels | VisualChannels.Pitch };
                if (sampled.Active && (sampled.Channels & VisualChannels.Scan) == 0 && (instance.State.Channels & VisualChannels.Scan) != 0)
                    sampled = sampled with { ScanFraction = instance.State.ScanFraction, Channels = sampled.Channels | VisualChannels.Scan };
                instance.State = sampled;
                instance.SampleFrame = frame;
            }
            Vector3 center = viewPos + viewRotation * VisualConvert.U(instance.Attachment.PositionShip * viewScale);
            float depth = Vector3.Dot(center - cameraPosition, cameraForward);
            float radius = instance.Profile.Core.MotionRadius * viewScale;
            float pixels = depth + radius <= 0 ? 0 : radius * 2 * focalPixels / MathF.Max(.01f, depth);
            instance.Lod = VisualMath.SelectLod(pixels,
                instance.Lod, instance.Profile.DetailedPixels, instance.Profile.HiddenPixels);
            staticSignature.Add(instance.Lod);
            staticSignature.Add(DamageColorStep(instance.State));
            var evaluate = instance.Lod == VisualLod.Detailed ? instance.State : instance.State with { Channels = instance.State.Channels & ~(VisualChannels.Yaw | VisualChannels.Pitch) };
            if (instance.EvaluatedLod != instance.Lod || instance.EvaluatedState != evaluate)
            {
                instance.Profile.Core.Evaluate(evaluate, instance.Pose);
                instance.EvaluatedState = evaluate; instance.EvaluatedLod = instance.Lod;
            }
        }
        int staticKey = staticSignature.ToHashCode();
        if (!cache.HasStatic || staticKey != cache.StaticSignature)
        {
            foreach (var old in cache.Static.Values) batch.Retire(old, frame);
            cache.Static.Clear();
            foreach (var buffer in cache.StaticBuffers.Values) buffer.Clear();
            foreach (var instance in cache.Instances) Append(instance, cache.StaticBuffers, false, group.FactionId);
            RemoveUnusedBuffers(cache.StaticBuffers, cache.UnusedColors);
            foreach (var entry in cache.StaticBuffers) if (entry.Value.Positions.Count > 0) cache.Static.Add(entry.Key, batch.Build(entry.Value));
            cache.StaticSignature = staticKey; cache.HasStatic = true; StaticRebuilds++;
        }
        var matrix = Matrix4x4.TRS(viewPos, viewRotation, Vector3.one * viewScale);
        foreach (var entry in cache.Static) Submit(entry.Value, entry.Key, matrix, material, properties, layer);
        foreach (var buffer in cache.DynamicBuffers.Values) buffer.Clear();
        foreach (var instance in cache.Instances)
        {
            Append(instance, cache.DynamicBuffers, true, group.FactionId);
            if (instance.Lod != VisualLod.Hidden) InstancesDrawn++;
        }
        RemoveUnusedBuffers(cache.DynamicBuffers, cache.UnusedColors);
        foreach (var entry in cache.DynamicBuffers)
            if (entry.Value.Positions.Count > 0) Submit(batch.Dynamic(entry.Value, frame), entry.Key, matrix, material, properties, layer);
    }

    private void Append(Instance instance, Dictionary<Color, VisualGeometryBuffer> buffers, bool dynamic, int factionId)
    {
        if (instance.Lod == VisualLod.Hidden) return;
        var profile = instance.Profile;
        for (int i = 0; i < profile.Core.NodeCount; i++)
        {
            var data = profile.Meshes[i];
            if (data == null || instance.Dynamic[i] != dynamic) continue;
            bool visible = true;
            for (int parent = i; parent >= 0; parent = instance.Parents[parent])
                if (!VisualStateSampling.IsVisible(profile.Core.Nodes[parent], instance.State)
                    || (instance.Lod != VisualLod.Detailed && profile.Styles[parent].DetailedOnly)) { visible = false; break; }
            if (!visible) continue;
            Color color = profile.Styles[i].Color;
            int damage = DamageColorStep(instance.State);
            if (damage != 0) color = Color.Lerp(color, ShipModelRenderer.DamagedPartColor, damage * (.75f / 16));
            if (profile.FactionTint) color *= ShipSkin.ShipSkinPalette.PartTint(factionId);
            if (!buffers.TryGetValue(color, out var buffer)) buffers.Add(color, buffer = new());
            var transform = instance.Pose[i] * instance.Attachment.Matrix;
            if (!buffer.TryAppend(data, transform))
                WarnOnce("[PFL·BuildingVisual] Invalid geometry transform: " + instance.Identity + "/" + profile.Core.Nodes[i].Id);
        }
    }

    private static int DamageColorStep(in VisualState state)
        => (state.Channels & VisualChannels.Damage) != 0 && float.IsFinite(state.DamageFraction)
            ? (int)MathF.Round(Math.Clamp(state.DamageFraction, 0, 1) * 16)
            : 0;

    private static void RemoveUnusedBuffers(Dictionary<Color, VisualGeometryBuffer> buffers, List<Color> unused)
    {
        foreach (var entry in buffers)
            if (entry.Value.Positions.Count == 0) unused.Add(entry.Key);
        foreach (var color in unused) buffers.Remove(color);
        unused.Clear();
    }
    private void Submit(Mesh mesh, Color color, in Matrix4x4 matrix, Material material, MaterialPropertyBlock properties, int layer)
    {
        properties.SetColor("_Color", color);
        Graphics.DrawMesh(mesh, matrix, material, layer, null, 0, properties);
        SubmittedBatches++;
    }
    private void Release(MapCache cache, int frame)
    {
        foreach (var mesh in cache.Static.Values) batch.Retire(mesh, frame);
        cache.Static.Clear(); cache.Instances.Clear();
    }
    public void Dispose()
    {
        if (disposed) return;
        foreach (var cache in maps.Values) Release(cache, currentFrame);
        maps.Clear(); batch.Dispose(); reported.Clear(); disposed = true;
    }
}
