using System;
using System.Runtime.CompilerServices;
using PioneerFramework;
using UnityEngine;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class SteeringRuntimeTests : IDisposable
{
    private readonly PioneerFrameworkLiteSettings previous = PioneerFrameworkLiteMod.Settings;
    public SteeringRuntimeTests() { PioneerFrameworkLiteMod.Settings = new(); SteeringController.Open(71); }
    public void Dispose() { SteeringController.Close(); PioneerFrameworkLiteMod.Settings = previous; }

    private static SpaceEntityGroup Group() => new(71)
    {
        CurrentOrbit = new OrbitState { rotation = DQuaternion.identity }, maxAngularRate = .02f
    };

    [Fact]
    public void RemappedInputPublishesOnlyItsActionAndRequiresReleaseAfterSuspension()
    {
        var profile = new SteeringBindingProfile();
        Assert.True(profile.TrySet(SteeringAction.PitchUp, new(105), out _));
        SteeringInputHost.ProcessSnapshot(profile, Array.Empty<int>(), 0, true);
        SteeringInputHost.ProcessSnapshot(profile, new[] { 119 }, 0, true);
        Assert.False(SteeringController.KeyPitchU);
        SteeringInputHost.ProcessSnapshot(profile, new[] { 105, 304 }, SteeringModifiers.Shift, true);
        Assert.True(SteeringController.KeyPitchU);
        Assert.True(SteeringController.PrecisionHeld);
        SteeringInputHost.ProcessSnapshot(profile, new[] { 105 }, 0, false);
        SteeringInputHost.ProcessSnapshot(profile, new[] { 105 }, 0, true);
        Assert.False(SteeringController.KeyPitchU);
        SteeringInputHost.ProcessSnapshot(profile, Array.Empty<int>(), 0, true);
        SteeringInputHost.ProcessSnapshot(profile, new[] { 105 }, 0, true);
        Assert.True(SteeringController.KeyPitchU);
    }

    [Fact]
    public void LeftYawAndRollUseLeftwardBodyDirectionsAndRespectTheGroupLimit()
    {
        var group = Group();
        SteeringController.KeyYawL = true;
        var yaw = SteeringController.GetRotationWorldAngularVelocity(group);
        Assert.True(yaw.y < 0);
        Assert.Equal(.02, yaw.magnitude, 7);
        SteeringController.KeyRollL = SteeringController.KeyPitchU = true;
        var diagonal = SteeringController.GetRotationWorldAngularVelocity(group);
        Assert.True(diagonal.x < 0 && diagonal.y < 0 && diagonal.z > 0);
        Assert.Equal(.02, diagonal.magnitude, 7);
    }

    [Fact]
    public void ManualTurnExitsSasAndAssistedReleaseStopsSpin()
    {
        var group = Group();
        SteeringController.SasKind = SasTargetKind.Prograde;
        SteeringController.KeyPitchU = true;
        SteeringController.ApplyManualRotation(group, .12);
        Assert.Equal(SasTargetKind.None, SteeringController.SasKind);
        double initial = group.angularVelocity.magnitude;
        SteeringController.KeyPitchU = false;
        SteeringController.ApplyManualRotation(group, .12);
        Assert.Equal(initial / Math.E, group.angularVelocity.magnitude, 10);
        SteeringController.SasKind = SasTargetKind.Retrograde;
        double before = group.angularVelocity.magnitude;
        SteeringController.ApplyManualRotation(group, .12);
        Assert.Equal(before, group.angularVelocity.magnitude);
    }

    [Fact]
    public void AssistAndPitchOptionsAreCapturedAtTakeoverAndInertiaRetainsSpin()
    {
        PioneerFrameworkLiteMod.Settings.pfviewAssistStop = false;
        PioneerFrameworkLiteMod.Settings.pfviewInvertPitch = true;
        SteeringController.Open(71);
        PioneerFrameworkLiteMod.Settings.pfviewAssistStop = true;
        PioneerFrameworkLiteMod.Settings.pfviewInvertPitch = false;
        var group = Group();
        SteeringController.KeyPitchU = true;
        SteeringController.ApplyManualRotation(group, .12);
        Assert.True(group.angularVelocity.x > 0);
        double initial = group.angularVelocity.x;
        SteeringController.KeyPitchU = false;
        SteeringController.ApplyManualRotation(group, .12);
        Assert.Equal(initial, group.angularVelocity.x);
    }

    [Fact]
    public void PrecisionScalesForceAndConsumptionByTheSameFactor()
    {
        SteeringController.PrecisionHeld = true;
        float force = 800, flow = 20;
        SteeringController.ScaleManualThrust(ref force, ref flow);
        Assert.Equal(200, force);
        Assert.Equal(5, flow);
        SteeringController.KeyYawL = true;
        Assert.Equal(.005, SteeringController.GetRotationWorldAngularVelocity(Group()).magnitude, 7);
    }

    [Fact]
    public void OpposingAxesDoNotTriggerASasTakeover()
    {
        SteeringController.KeyYawL = SteeringController.KeyYawR = true;
        SteeringController.KeyForward = SteeringController.KeyBack = true;
        SteeringController.SasKind = SasTargetKind.Prograde;
        Assert.False(SteeringController.AnyRotation);
        Assert.False(SteeringController.AnyTranslation);
        SteeringController.ApplyManualRotation(Group(), .12);
        Assert.Equal(SasTargetKind.Prograde, SteeringController.SasKind);
    }

    [Fact]
    public void GroupShortcutCannotTurnIntoATimeCommandWhenItsModifierReleases()
    {
        var profile = new SteeringBindingProfile();
        SteeringInputHost.ProcessSnapshot(profile, Array.Empty<int>(), 0, true);
        SteeringInputHost.ProcessSnapshot(profile, new[] { 306, 49 }, SteeringModifiers.Control, true);
        var group = new Event { type = EventType.KeyDown, keyCode = KeyCode.Alpha1 };
        Assert.True(SteeringInputHost.TryShortcut(group, true, out var action));
        Assert.Equal(SteeringAction.Group1, action);
        SteeringInputHost.ProcessSnapshot(profile, new[] { 49 }, 0, true);
        var repeated = new Event { type = EventType.KeyDown, keyCode = KeyCode.Alpha1 };
        Assert.False(SteeringInputHost.TryShortcut(repeated, true, out _));
        Assert.False(SteeringInputHost.AcceptNativeTimeKey(repeated));
        SteeringInputHost.ProcessSnapshot(profile, Array.Empty<int>(), 0, true);
        SteeringInputHost.ProcessSnapshot(profile, new[] { 49 }, 0, true);
        Assert.True(SteeringInputHost.AcceptNativeTimeKey(new Event { type = EventType.KeyDown, keyCode = KeyCode.Alpha1 }));
    }

    [Fact]
    public void AStandaloneModifierCanStillBeTheNativePauseKey()
    {
        var profile = new SteeringBindingProfile();
        Assert.True(profile.TrySet(SteeringAction.Precision, default, out _));
        SteeringInputHost.ProcessSnapshot(profile, Array.Empty<int>(), 0, true);
        SteeringInputHost.ProcessSnapshot(profile, new[] { 304 }, SteeringModifiers.Shift, true);
        Assert.True(SteeringInputHost.AcceptNativeTimeKey(new Event { type = EventType.KeyDown, keyCode = KeyCode.LeftShift }));
    }

    [Fact]
    public void ANewGameCannotInheritAnOldGamesControlSessionOrHeldInput()
    {
        Game previousGame = Current.Game;
        try
        {
            Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
            SteeringController.Open(71, true);
            var profile = new SteeringBindingProfile();
            SteeringInputHost.ProcessSnapshot(profile, Array.Empty<int>(), 0, true);
            SteeringInputHost.ProcessSnapshot(profile, new[] { 119 }, 0, true);
            Assert.True(SteeringController.KeyPitchU);
            Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
            SteeringInputHost.ProcessSnapshot(profile, new[] { 119 }, 0, true);
            Assert.False(SteeringController.IsOpen);
            Assert.False(SteeringController.KeyPitchU);
        }
        finally { Current.Game = previousGame; }
    }
}
