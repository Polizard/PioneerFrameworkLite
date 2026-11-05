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
        public static bool TryCollect(CombatManager? combat, int groupId, int playerFactionId,
            out PFViewFleetSnapshot snapshot)
        {
            snapshot = null!;
            if (playerFactionId < 0 || !PhysicsApi.TryGetSpacecraft(groupId, out var craft)) return false;
            if (!craft.IsActive || craft.FactionId != playerFactionId) return false;
            if (!PhysicsQueries.TryCollectTelemetry(groupId, out var telemetry)) return false;

            snapshot = new() { GroupId = groupId };
            for (int i = 0; i < telemetry.Members.Count; i++)
            {
                var member = telemetry.Members[i];
                snapshot.Members.Add(new(member.EntityId, member.PartCount, member.DestroyedPartCount));
            }
            snapshot.HasMap = telemetry.HasMap;
            snapshot.UnloadedMaps = telemetry.UnloadedMaps;
            snapshot.DamagedBuildings = telemetry.DamagedBuildings;
            snapshot.DestroyedParts = telemetry.DestroyedParts;
            snapshot.MinimumPartHp = telemetry.MinimumPartHp;
            snapshot.HasPropulsion = telemetry.HasPropulsion;
            snapshot.PropellantMass = telemetry.PropellantMass;
            snapshot.PropellantCapacity = telemetry.PropellantCapacity;
            snapshot.ReferenceDeltaV = telemetry.ReferenceDeltaV;
            snapshot.EnvironmentDependent = telemetry.EnvironmentDependent;
            snapshot.ShortestEndurance = telemetry.ShortestEndurance;
            for (int i = 0; i < telemetry.Networks.Count; i++)
            {
                var network = telemetry.Networks[i];
                snapshot.Networks.Add(new(network.Name, network.MassKg, network.EnvironmentDependent,
                    network.EnduranceSeconds));
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
            snapshot.ManeuverCount = craft.PlannedManeuverCount;
            if (double.IsFinite(craft.NextManeuverTick)) snapshot.NextNodeTick = craft.NextManeuverTick;
            if (craft.HasRendezvousGuidance && double.IsFinite(craft.RendezvousArrivalTick) && craft.RendezvousArrivalTick > 0)
                snapshot.ArrivalTick = craft.RendezvousArrivalTick;
            snapshot.ActivityKey = craft.IsCrippled ? "PFV_FleetActivity_Crippled"
                : craft.HasActiveBurn ? "PFV_FleetActivity_Burning"
                : craft.HasApproachGuidance ? "PFV_FleetActivity_Approach"
                : craft.HasRendezvousGuidance ? "PFV_FleetActivity_Rendezvous"
                : craft.TransferActive ? "PFV_FleetActivity_Transfer"
                : !string.IsNullOrEmpty(craft.LandingTaskId) ? "PFV_FleetActivity_Landing"
                : snapshot.ManeuverCount > 0 ? "PFV_FleetActivity_Queued" : "PFV_FleetActivity_Idle";
            snapshot.Alerts = PFViewFleetCore.ResolveAlerts(craft.IsMovable, craft.IsCrippled, snapshot.HasPropulsion,
                snapshot.PropellantMass, snapshot.PropellantCapacity, snapshot.DamagedBuildings, snapshot.DestroyedParts,
                snapshot.OverheatedWeapons, snapshot.DisabledWeapons, snapshot.EmptyWeapons);
            return true;
        }
    }
}
