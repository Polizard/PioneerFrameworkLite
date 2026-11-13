using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using HarmonyLib;
using PioneerFramework.Combat;
using PioneerFramework.PFView.ShipBuildingVisuals;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class ShipBuildingVisualHostTests
{
    [Fact]
    public void MissingActiveChannelCannotEnableAnActiveOnlyNode()
    {
        Assert.False(VisualStateSampling.IsVisible(new VisualNode("lamp") { ActiveOnly = true }, default));
        Assert.True(VisualStateSampling.IsVisible(new VisualNode("lamp") { ActiveOnly = true }, new VisualState { Active = true, Channels = VisualChannels.Active }));
    }

    [Fact]
    public void ThrowingStateProviderIsIsolatedAndWarnsOnce()
    {
        int warnings = 0;
        var sampling = new VisualStateSampling(_ => warnings++);
        var context = new VisualStateContext(null, null, null, Quaternion.Identity, 20);
        Assert.Equal(default, sampling.Sample(new Throws(), context, "instance1"));
        Assert.Equal(default, sampling.Sample(new Throws(), context, "instance1"));
        Assert.Equal(1, warnings);
        var state = sampling.Sample(new StaticVisualStateProvider(), context, "instance2");
        Assert.True(state.Channels.HasFlag(VisualChannels.Active));
    }

    [Fact]
    public void UnbuiltOrUnconfiguredThingsAreNotCandidates()
    {
        var def = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef));
        var building = new Building { def = def };
        Assert.False(MapComponent_BuildingVisualIndex.IsCandidate(building));
        Assert.False(MapComponent_BuildingVisualIndex.IsCandidate(null));
    }

    [Theory]
    [InlineData(0, 90)]
    [InlineData(90, 0)]
    public void RadarSamplingUsesAuthoritativeGroupDirectionWithoutWritingState(float groupYaw, float expectedYaw)
    {
        var group = new SpaceEntityGroup(90, 2) { RadarCap = new RadarCapability { beamRangeMeters = 1000, scanRateDegPerSec = 6 }, ActivePingEnabled = true };
        group.CurrentOrbit.rotation = new DQuaternion(0, Math.Sin(groupYaw * Math.PI / 360), 0, Math.Cos(groupYaw * Math.PI / 360));
        group.RadarAimMode = RadarAim.Manual;
        group.RadarManualDir = new DVector3(1, 0, 0);
        var provider = new ActiveRadarVisualStateProvider();
        var input = new VisualStateContext(null, null, group, Quaternion.Identity, 10);
        var state = provider.Sample(input);
        Assert.True(state.Active);
        Assert.Equal(expectedYaw, state.Yaw, 3);
        Assert.InRange(state.ScanFraction, 0, 1);
        Assert.Equal(RadarAim.Manual, group.RadarAimMode);
        Assert.Equal(new DVector3(1, 0, 0), group.RadarManualDir);
        Assert.True(group.ActivePingEnabled);
        group.ActivePingEnabled = false;
        Assert.False(provider.Sample(input).Active);
    }

    [Fact]
    public void TurretSamplingConvertsShipAngleAndPreservesWeaponState()
    {
        var building = new Building();
        var turret = new CompMapTurret { parent = building };
        var weapon = new WeaponInstance { ammo = 7, cooldownRemaining = 12 };
        var mount = new TurretMount { rotation = 90, weapon = weapon };
        AccessTools.Field(typeof(CompMapTurret), "mount").SetValue(turret, mount);
        AccessTools.Field(typeof(ThingWithComps), "comps").SetValue(building, new List<ThingComp> { turret });
        var input = new VisualStateContext(building, null, null, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * .5f), 10);
        // MaxHitPoints is a host stat; isolate only that query while exercising the real adapter.
        var patch = new Harmony("PFL.Tests.BuildingVisualStats");
        patch.Patch(AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.MaxHitPoints)), prefix: new HarmonyMethod(typeof(ShipBuildingVisualHostTests), nameof(MaxHitPoints)));
        try
        {
            var state = new MapTurretVisualStateProvider().Sample(input);
            Assert.InRange(MathF.Abs(state.Yaw), 0, .001f);
            Assert.Equal(90, mount.rotation);
            Assert.Equal(7, weapon.ammo);
            Assert.Equal(12, weapon.cooldownRemaining);
        }
        finally { patch.UnpatchAll(patch.Id); }
    }

    [Fact]
    public void RendererDisposalIsIdempotentAndRejectsReuse()
    {
        var renderer = new ShipBuildingVisualRenderer(_ => { }, new ShipBuildingVisualRegistry());
        renderer.Dispose(); renderer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => renderer.BeginFrame(1));
    }

    private static bool MaxHitPoints(ref int __result) { __result = 100; return false; }

    private sealed class Throws : IShipBuildingVisualStateProvider
    {
        public VisualState Sample(in VisualStateContext context) => throw new InvalidOperationException("test");
    }
}
