using System;
using System.Collections.Generic;

namespace PioneerFramework.PFView.ShipBuildingVisuals;

/// <summary>Register from startup constructors before the first visual resolution. No reflection discovery.</summary>
public sealed class ShipBuildingVisualRegistry
{
    public static ShipBuildingVisualRegistry Default { get; } = new();
    private readonly Dictionary<string, IShipBuildingVisualStateProvider> states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IShipBuildingVisualMeshProvider> meshes = new(StringComparer.Ordinal);
    public bool IsFrozen { get; private set; }

    public ShipBuildingVisualRegistry()
    {
        states.Add("PFL.Static.v1", new StaticVisualStateProvider());
        states.Add("PFL.MapTurret.v1", new MapTurretVisualStateProvider());
        states.Add("PFL.ActiveRadar.v1", new ActiveRadarVisualStateProvider());
        meshes.Add("PFL.Primitives.v1", new PrimitiveVisualMeshProvider());
        meshes.Add("PFL.AssetBundle.v1", new AssetBundleVisualMeshProvider());
    }

    public void RegisterStateProvider(string id, IShipBuildingVisualStateProvider provider) => Register(states, id, provider);
    public void RegisterMeshProvider(string id, IShipBuildingVisualMeshProvider provider) => Register(meshes, id, provider);
    private void Register<T>(Dictionary<string, T> dictionary, string id, T provider) where T : class
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(id) || !id.Contains('.')) throw new ArgumentException("Provider ID must be namespaced.", nameof(id));
        if (IsFrozen) throw new InvalidOperationException("Building visual registry is frozen: " + id);
        if (!dictionary.TryAdd(id, provider)) throw new InvalidOperationException("Duplicate building visual provider: " + id);
    }
    public void Freeze() => IsFrozen = true;
    public bool TryGetStateProvider(string id, out IShipBuildingVisualStateProvider provider) => states.TryGetValue(id, out provider!);
    public bool TryGetMeshProvider(string id, out IShipBuildingVisualMeshProvider provider) => meshes.TryGetValue(id, out provider!);
}

public sealed class PrimitiveVisualMeshProvider : IShipBuildingVisualMeshProvider
{
    private readonly Dictionary<VisualPrimitive, VisualMeshData> cache = new();
    public VisualMeshData GetMesh(string name)
    {
        if (!Enum.TryParse<VisualPrimitive>(name, true, out var kind) || !Enum.IsDefined(kind)) throw new ArgumentException("Unknown primitive: " + name, nameof(name));
        if (!cache.TryGetValue(kind, out var data)) cache.Add(kind, data = VisualGeometry.Build(kind));
        return data;
    }
}
