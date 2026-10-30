using System;
using System.Collections.Generic;
using PioneerFramework.Combat;
using Verse;

namespace PioneerFramework
{
    internal readonly record struct PFViewFleetNetwork(string Name, double Mass, bool EnvironmentDependent, double Endurance);
    internal readonly record struct PFViewFleetMember(int EntityId, int Parts, int Destroyed);

    internal sealed class PFViewFleetSnapshot
    {
        public int GroupId { get; init; }
        public int MemberCount => Members.Count;
        public List<PFViewFleetMember> Members { get; } = new();
        public List<PFViewFleetNetwork> Networks { get; } = new();
        public bool HasPropulsion { get; set; }
        public double PropellantMass { get; set; }
        public double PropellantCapacity { get; set; }
        public float ReferenceDeltaV { get; set; }
        public bool EnvironmentDependent { get; set; }
        public double ShortestEndurance { get; set; } = double.PositiveInfinity;
        public int DamagedBuildings { get; set; }
        public int DestroyedParts { get; set; }
        public float? MinimumPartHp { get; set; }
        public int Weapons { get; set; }
        public int ReadyWeapons { get; set; }
        public int OverheatedWeapons { get; set; }
        public int DisabledWeapons { get; set; }
        public int EmptyWeapons { get; set; }
        public bool HasMap { get; set; }
        public bool UnloadedMaps { get; set; }
        public string ActivityKey { get; set; } = "PFV_FleetActivity_Idle";
        public double? NextNodeTick { get; set; }
        public double? ArrivalTick { get; set; }
        public int ManeuverCount { get; set; }
        public PFViewFleetAlert Alerts { get; set; }
        public string? AlertLabel { get; set; }
    }

    /// <summary>
    /// 我方编队的只读 UI 快照。先验证所有权，再读取推进、地图与武器；不保留可变游戏对象。
    /// 部件没有统一最大 HP，故只报原始 HP / 已毁数；建筑损伤使用实际 HitPoints / MaxHitPoints。
    /// </summary>
    internal static class PFViewFleetTelemetry
    {
        public static bool TryCollect(SpaceGameComp? space, CombatManager? combat, int groupId,
            int playerFactionId, out PFViewFleetSnapshot snapshot)
        {
            snapshot = null!;
            var group = space?.GetGroupById(groupId);
            if (group == null || !group.IsActive || playerFactionId < 0 || group.FactionId != playerFactionId) return false;
            snapshot = new() { GroupId = groupId };
            var maps = new HashSet<int>();
            foreach (int id in group.GetEntityIds())
            {
                var entity = space!.GetEntityById(id);
                if (entity == null || entity.GroupId != groupId) continue;
                int destroyed = 0;
                foreach (var part in entity.Parts)
                {
                    if (part.isDestroyed) { destroyed++; continue; }
                    if (!part.isIndestructible && float.IsFinite(part.hp))
                        snapshot.MinimumPartHp = Math.Min(snapshot.MinimumPartHp ?? part.hp, part.hp);
                }
                snapshot.DestroyedParts += destroyed;
                snapshot.Members.Add(new(id, entity.Parts.Count, destroyed));
                if (entity.IsMapArchived) { snapshot.UnloadedMaps = true; continue; }
                var mapIds = entity.GetMapIds();
                for (int i = 0; i < mapIds.Count; i++)
                {
                    int mapId = mapIds[i];
                    if (!maps.Add(mapId)) continue;
                    var mc = space!.GetMapComponent<SpaceMapComp>(mapId);
                    if (mc?.map == null) { snapshot.UnloadedMaps = true; continue; }
                    if (entity.HasMap && entity.MapId == mapId) snapshot.HasMap = true;
                    var buildings = mc.map.listerThings?.ThingsInGroup(ThingRequestGroup.BuildingArtificial);
                    if (buildings == null) continue;
                    for (int b = 0; b < buildings.Count; b++)
                    {
                        var building = buildings[b];
                        if (!building.Destroyed && building.def.useHitPoints && building.HitPoints < building.MaxHitPoints)
                            snapshot.DamagedBuildings++;
                    }
                }
            }
            snapshot.Members.Sort((a, b) => a.EntityId.CompareTo(b.EntityId));
            var source = space!.GetGroupPropulsionSource(group);
            snapshot.HasPropulsion = source != null && source.Networks.Count > 0;
            var (dv, _, mass, capacity) = space.GetGroupDeltaV(group);
            snapshot.ReferenceDeltaV = dv;
            snapshot.PropellantMass = mass;
            snapshot.PropellantCapacity = capacity;
            foreach (var network in space.GetGroupEnduranceReport(group))
            {
                double networkMass = source!.GetPropellantMass(network.propellant);
                double endurance = network.enduranceSeconds;
                snapshot.Networks.Add(new(network.propellant.LabelCap, networkMass, network.environmentDependent, endurance));
                if (network.environmentDependent)
                {
                    snapshot.EnvironmentDependent = true;
                    snapshot.ShortestEndurance = Math.Min(snapshot.ShortestEndurance, endurance);
                }
            }
            var weapons = combat?.GetGroupWeapons(groupId);
            if (weapons != null)
                for (int i = 0; i < weapons.Count; i++)
                {
                    var weapon = weapons[i];
                    snapshot.Weapons++;
                    if (weapon.CanFire()) snapshot.ReadyWeapons++;
                    if (weapon.isOverheated) snapshot.OverheatedWeapons++;
                    if (weapon.isDisabled) snapshot.DisabledWeapons++;
                    if (weapon.ammo == 0) snapshot.EmptyWeapons++;
                }
            foreach (var node in group.plannedManeuvers)
            {
                if (!node.active) continue;
                snapshot.ManeuverCount++;
                snapshot.NextNodeTick = Math.Min(snapshot.NextNodeTick ?? node.executeTick, node.executeTick);
            }
            if (group.HasRendezvousGuidance && double.IsFinite(group.rendezvousArrivalTick) && group.rendezvousArrivalTick > 0)
                snapshot.ArrivalTick = group.rendezvousArrivalTick;
            snapshot.ActivityKey = group.IsCrippled ? "PFV_FleetActivity_Crippled"
                : group.HasActiveBurn ? "PFV_FleetActivity_Burning"
                : group.HasApproachGuidance ? "PFV_FleetActivity_Approach"
                : group.HasRendezvousGuidance ? "PFV_FleetActivity_Rendezvous"
                : group.transferActive ? "PFV_FleetActivity_Transfer"
                : !string.IsNullOrEmpty(group.LandingTaskId) ? "PFV_FleetActivity_Landing"
                : snapshot.ManeuverCount > 0 ? "PFV_FleetActivity_Queued" : "PFV_FleetActivity_Idle";
            snapshot.Alerts = PFViewFleetCore.ResolveAlerts(group.IsMovable, group.IsCrippled, snapshot.HasPropulsion,
                mass, capacity, snapshot.DamagedBuildings, snapshot.DestroyedParts, snapshot.OverheatedWeapons,
                snapshot.DisabledWeapons, snapshot.EmptyWeapons);
            return true;
        }
    }
}
