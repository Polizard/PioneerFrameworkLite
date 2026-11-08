using System.Collections.Generic;
using System.Collections.ObjectModel;
using Verse;

namespace PioneerFramework.PFView.ShipBuildingVisuals;

/// <summary>Presentation-only map index. Events mark structure dirty; no map scan in simulation callbacks.</summary>
public sealed class MapComponent_BuildingVisualIndex : MapComponent
{
    private readonly List<Building> buildings = new();
    private readonly ReadOnlyCollection<Building> view;
    private bool dirty = true;
    private bool subscribed;
    public int Version { get; private set; }

    public MapComponent_BuildingVisualIndex(Map map) : base(map)
    { view = buildings.AsReadOnly(); Subscribe(); }

    private void Subscribe()
    {
        if (subscribed || map.events == null) return;
        map.events.BuildingSpawned += Changed;
        map.events.BuildingDespawned += Changed;
        subscribed = true;
    }
    private void Changed(Building building)
    {
        if (building.def?.GetModExtension<ShipBuildingVisualExtension>() != null) NotifyChanged();
    }
    public void NotifyChanged() { dirty = true; Version++; }

    public override void MapRemoved()
    {
        if (subscribed && map.events != null)
        {
            map.events.BuildingSpawned -= Changed;
            map.events.BuildingDespawned -= Changed;
        }
        subscribed = false;
        buildings.Clear();
        NotifyChanged();
        base.MapRemoved();
    }

    public IReadOnlyList<Building> GetBuildings()
    {
        Subscribe();
        if (!dirty) return view;
        buildings.Clear();
        if (map.spawnedThings != null)
            foreach (var thing in map.spawnedThings)
                if (IsCandidate(thing)) buildings.Add((Building)thing);
        dirty = false;
        return view;
    }
    public static bool IsCandidate(Thing? thing) => thing is Building && thing.Spawned && !thing.Destroyed
        && thing.def != null && !thing.def.IsFrame && thing.def.entityDefToBuild == null
        && thing.def.GetModExtension<ShipBuildingVisualExtension>()?.mounts is { Count: > 0 };
}
