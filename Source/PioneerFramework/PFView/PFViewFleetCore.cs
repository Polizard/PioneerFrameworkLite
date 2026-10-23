using System;

namespace PioneerFramework
{
    [Flags]
    internal enum PFViewFleetAlert
    {
        None = 0, Crippled = 1, PropellantEmpty = 2, LowPropellant = 4,
        Damage = 8, Overheated = 16, DisabledWeapons = 32, EmptyWeapons = 64
    }

    internal enum PFViewFleetEventKind { None, Node, Arrival }
    internal readonly record struct PFViewFleetEvent(PFViewFleetEventKind Kind, double Seconds);

    /// <summary>舰况的只读提示策略；不提交命令，不改变物理或规划判据。</summary>
    internal static class PFViewFleetCore
    {
        public static PFViewFleetAlert ResolveAlerts(bool movable, bool crippled, bool hasPropulsion,
            double mass, double capacity, int damaged, int destroyed, int overheated, int disabled, int emptyWeapons)
        {
            var result = crippled ? PFViewFleetAlert.Crippled : PFViewFleetAlert.None;
            if (movable && hasPropulsion && double.IsFinite(mass) && mass >= 0
                && double.IsFinite(capacity) && capacity > 0)
            {
                if (mass == 0) result |= PFViewFleetAlert.PropellantEmpty;
                else if (mass / capacity < .2) result |= PFViewFleetAlert.LowPropellant;
            }
            if (damaged > 0 || destroyed > 0) result |= PFViewFleetAlert.Damage;
            if (overheated > 0) result |= PFViewFleetAlert.Overheated;
            if (disabled > 0) result |= PFViewFleetAlert.DisabledWeapons;
            if (emptyWeapons > 0) result |= PFViewFleetAlert.EmptyWeapons;
            return result;
        }

        public static PFViewFleetEvent NextEvent(double now, double? node, double? arrival)
        {
            if (!double.IsFinite(now)) return default;
            bool hasNode = node.HasValue && double.IsFinite(node.Value);
            bool hasArrival = arrival.HasValue && double.IsFinite(arrival.Value);
            if (hasNode && (!hasArrival || node!.Value <= arrival!.Value))
                return new(PFViewFleetEventKind.Node, Math.Max(0, node!.Value - now));
            return hasArrival ? new(PFViewFleetEventKind.Arrival, Math.Max(0, arrival!.Value - now)) : default;
        }
    }
}
