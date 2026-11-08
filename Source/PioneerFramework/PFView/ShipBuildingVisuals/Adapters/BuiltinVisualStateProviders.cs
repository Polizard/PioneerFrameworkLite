using System;
using System.Collections.Generic;
using System.Numerics;
using Verse;

namespace PioneerFramework.PFView.ShipBuildingVisuals;

public sealed class StaticVisualStateProvider : IShipBuildingVisualStateProvider
{
    public VisualState Sample(in VisualStateContext context)
    {
        float damage = context.Building == null ? 0 : 1f - (float)context.Building.HitPoints / Math.Max(1, context.Building.MaxHitPoints);
        return new() { Active = true, DamageFraction = Math.Clamp(damage, 0, 1), Channels = VisualChannels.Active | VisualChannels.Damage };
    }
}

public sealed class MapTurretVisualStateProvider : IShipBuildingVisualStateProvider
{
    private readonly StaticVisualStateProvider common = new();
    public VisualState Sample(in VisualStateContext context)
    {
        var state = common.Sample(context);
        var mount = context.Building?.TryGetComp<CompMapTurret>()?.Mount;
        if (mount == null || !float.IsFinite(mount.rotation)) return state;
        var angles = VisualMath.LocalAngles(VisualMath.YawDirection(mount.rotation), context.InstallationRotationShip, out float yaw, out _);
        if ((angles & VisualChannels.Yaw) != 0)
            state = state with { Yaw = yaw, Channels = state.Channels | VisualChannels.Yaw };
        return state;
    }
}

public sealed class ActiveRadarVisualStateProvider : IShipBuildingVisualStateProvider
{
    private readonly StaticVisualStateProvider common = new();
    public VisualState Sample(in VisualStateContext context)
    {
        var state = common.Sample(context);
        var group = context.Group;
        bool active = group != null && group.IsActive && group.ActivePingEnabled && group.RadarCap.beamRangeMeters > 0;
        state = state with { Active = active };
        if (!active || !double.IsFinite(context.RenderTick)) return state;
        var system = group!.RadarBoresight(context.RenderTick);
        var shipDirection = Vector3.Transform(new Vector3((float)system.x, (float)system.y, (float)system.z),
            Quaternion.Inverse(VisualConvert.N(group.CurrentOrbit.rotation)));
        var angles = VisualMath.LocalAngles(shipDirection, context.InstallationRotationShip, out float yaw, out float pitch);
        state = state with { Yaw = yaw, Pitch = pitch, Channels = state.Channels | angles };
        if ((angles & VisualChannels.Yaw) != 0)
            state = state with { ScanFraction = (yaw + 180) / 360, Channels = state.Channels | VisualChannels.Scan };
        return state;
    }
}

/// <summary>Per-session exception isolation. Failed reads do not suppress another instance.</summary>
public sealed class VisualStateSampling(Action<string> warn)
{
    private readonly HashSet<string> reported = new(StringComparer.Ordinal);
    public VisualState Sample(IShipBuildingVisualStateProvider provider, in VisualStateContext context, string instance)
    {
        try { return provider.Sample(context); }
        catch (Exception e)
        {
            if (reported.Add(instance)) warn("[PFL·BuildingVisual] State " + instance + ": " + e.Message);
            return default;
        }
    }
    public static bool IsVisible(VisualNode node, in VisualState state)
        => !node.ActiveOnly || ((state.Channels & VisualChannels.Active) != 0 && state.Active);
}
