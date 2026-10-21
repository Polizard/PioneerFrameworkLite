using System;
using System.IO;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PioneerFramework;
using UnityEngine;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public sealed class SteeringPrecisionModeTests : IDisposable
{
    private readonly PioneerFrameworkLiteSettings previous = PioneerFrameworkLiteMod.Settings;
    public void Dispose() { SteeringController.Close(); PioneerFrameworkLiteMod.Settings = previous; }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedModeControlsReleaseBehaviorAndEachPressChangesToggleOnlyOnce(bool toggle)
    {
        PioneerFrameworkLiteMod.Settings = LoadMode(toggle, roundTrip: true);
        SteeringController.Open(71);
        var profile = new SteeringBindingProfile();
        Snapshot(profile, [], 0);
        Snapshot(profile, [304, 119], SteeringModifiers.Shift);
        AssertPitchRate(.005);
        Snapshot(profile, [304, 119], SteeringModifiers.Shift);
        AssertPitchRate(.005);
        Snapshot(profile, [119], 0);
        AssertPitchRate(toggle ? .005 : .02);
        float force = 800, flow = 20;
        SteeringController.ScaleManualThrust(ref force, ref flow);
        Assert.Equal(toggle ? 200 : 800, force);
        Assert.Equal(toggle ? 5 : 20, flow);
        Snapshot(profile, [304, 119], SteeringModifiers.Shift);
        AssertPitchRate(toggle ? .02 : .005);
        Snapshot(profile, [304, 119], SteeringModifiers.Shift);
        AssertPitchRate(toggle ? .02 : .005);
        Snapshot(profile, [119], 0);
        AssertPitchRate(.02);
    }

    [Fact]
    public void ARemappedToggleChordDoesNotRetriggerUntilItsPrimaryKeyReleases()
    {
        PioneerFrameworkLiteMod.Settings = LoadMode(true);
        SteeringController.Open(71);
        var profile = new SteeringBindingProfile();
        Assert.True(profile.TrySet(SteeringAction.Precision, new(112, SteeringModifiers.Control), out _));
        Snapshot(profile, [], 0);
        Snapshot(profile, [306, 112, 119], SteeringModifiers.Control);
        AssertPitchRate(.005);
        Snapshot(profile, [112, 119], 0);
        AssertPitchRate(.005);
        Snapshot(profile, [306, 112, 119], SteeringModifiers.Control);
        AssertPitchRate(.005);
        Snapshot(profile, [306, 119], SteeringModifiers.Control);
        Assert.False(SteeringController.KeyPitchU);
        Snapshot(profile, [], 0);
        Snapshot(profile, [306, 112, 119], SteeringModifiers.Control);
        AssertPitchRate(.02);
    }

    [Fact]
    public void ToggleSurvivesSuspensionAndInputRearmingWithoutReplayingHeldKeys()
    {
        PioneerFrameworkLiteMod.Settings = LoadMode(true);
        SteeringController.Open(71);
        var profile = new SteeringBindingProfile();
        Snapshot(profile, [], 0);
        Snapshot(profile, [304, 119], SteeringModifiers.Shift);
        Snapshot(profile, [304, 119], SteeringModifiers.Shift, enabled: false);
        Assert.False(SteeringController.AnyRotation);
        Snapshot(profile, [304, 119], SteeringModifiers.Shift);
        Assert.False(SteeringController.AnyRotation);
        SteeringInputHost.Rearm();
        Snapshot(profile, [], 0);
        Snapshot(profile, [119], 0);
        AssertPitchRate(.005);
        Snapshot(profile, [304, 119], SteeringModifiers.Shift);
        AssertPitchRate(.02);
    }

    [Fact]
    public void NewTakeoverResetsPrecisionAndAppliesTheNewActivationMode()
    {
        PioneerFrameworkLiteMod.Settings = LoadMode(true);
        SteeringController.Open(71);
        var profile = new SteeringBindingProfile();
        Snapshot(profile, [], 0);
        Snapshot(profile, [304, 119], SteeringModifiers.Shift);
        Snapshot(profile, [119], 0);
        AssertPitchRate(.005);
        PioneerFrameworkLiteMod.Settings = LoadMode(false);
        Snapshot(profile, [119], 0);
        AssertPitchRate(.005);
        SteeringController.Open(72);
        Snapshot(profile, [304, 119], SteeringModifiers.Shift);
        Assert.False(SteeringController.AnyRotation);
        Assert.False(SteeringController.PrecisionHeld);
        Snapshot(profile, [], 0);
        Snapshot(profile, [119], 0);
        AssertPitchRate(.02);
        Snapshot(profile, [304, 119], SteeringModifiers.Shift);
        AssertPitchRate(.005);
        Snapshot(profile, [119], 0);
        AssertPitchRate(.02);
    }

    [Fact]
    public void ClearingThePrecisionBindingCannotLeavePrecisionLatchedWithoutAnExitKey()
    {
        PioneerFrameworkLiteMod.Settings = LoadMode(true);
        SteeringController.Open(71);
        var profile = new SteeringBindingProfile();
        Snapshot(profile, [], 0);
        Snapshot(profile, [304, 119], SteeringModifiers.Shift);
        Snapshot(profile, [119], 0);
        AssertPitchRate(.005);
        Assert.True(profile.TrySet(SteeringAction.Precision, default, out _));
        Snapshot(profile, [119], 0);
        AssertPitchRate(.02);
    }

    [Fact]
    public void ClearingAndRebindingDuringSuspensionStillExitsLatchedPrecision()
    {
        PioneerFrameworkLiteMod.Settings = LoadMode(true);
        SteeringController.Open(71);
        var profile = new SteeringBindingProfile();
        Snapshot(profile, [], 0);
        Snapshot(profile, [304, 119], SteeringModifiers.Shift);
        Snapshot(profile, [119], 0);
        AssertPitchRate(.005);
        Assert.True(profile.TrySet(SteeringAction.Precision, default, out _));
        Snapshot(profile, [], 0, enabled: false);
        Assert.True(profile.TrySet(SteeringAction.Precision, new(304), out _));
        Snapshot(profile, [], 0);
        Snapshot(profile, [119], 0);
        AssertPitchRate(.02);
    }

    private static void Snapshot(SteeringBindingProfile profile, int[] held, SteeringModifiers modifiers, bool enabled = true)
        => SteeringInputHost.ProcessSnapshot(profile, held, modifiers, enabled);

    private static void AssertPitchRate(double expected)
    {
        var group = new SpaceEntityGroup(71)
        {
            CurrentOrbit = new OrbitState { rotation = Quaternion.identity }, maxAngularRate = .02f
        };
        Assert.True(SteeringController.KeyPitchU);
        Assert.Equal(-expected, SteeringController.GetRotationWorldAngularVelocity(group).x, 7);
    }

    private static PioneerFrameworkLiteSettings LoadMode(bool toggle, bool roundTrip = false)
    {
        string path = Path.Combine(Path.GetTempPath(), "pfl-precision-" + Guid.NewGuid() + ".xml");
        var prefs = AccessTools.Field(typeof(Prefs), "data");
        object previousPrefs = prefs.GetValue(null);
        prefs.SetValue(null, RuntimeHelpers.GetUninitializedObject(prefs.FieldType));
        try
        {
            File.WriteAllText(path, "<pilot-settings><pfviewPrecisionToggle>" + (toggle ? "True" : "False")
                + "</pfviewPrecisionToggle></pilot-settings>");
            var settings = Read();
            if (roundTrip)
            {
                Scribe.saver.InitSaving(path, "pilot-settings");
                settings.ExposeData();
                Scribe.saver.FinalizeSaving();
                settings = Read();
            }
            return settings;
        }
        finally { Scribe.ForceStop(); prefs.SetValue(null, previousPrefs); if (File.Exists(path)) File.Delete(path); }

        PioneerFrameworkLiteSettings Read()
        {
            var settings = new PioneerFrameworkLiteSettings();
            Scribe.loader.InitLoading(path);
            settings.ExposeData();
            Scribe.loader.FinalizeLoading();
            return settings;
        }
    }
}
