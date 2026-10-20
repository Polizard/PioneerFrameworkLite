using System;
using System.IO;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PioneerFramework;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class PFViewPilotSettingsTests
{
    [Fact]
    public void PilotSettingsRoundTripPreservesRemappedAndUnboundActionsWithExistingOptions()
    {
        var settings = new PioneerFrameworkLiteSettings
        {
            replaceWorldSky = false, spaceAiRandomEncounters = true,
            pfviewFocusCameraMode = PFViewGroupCameraMode.ThirdPersonVelocity,
            pfviewAssistStop = false, pfviewInvertPitch = true, pfviewPrecisionScale = .4f,
            pfviewFollowRoll = true, pfviewInvertMouseY = true, pfviewInvertWheel = true,
            pfviewRecenter = true, pfviewSensitivity = 1.7f, pfviewFollowSmoothing = .2f
        };
        Assert.True(settings.pfviewBindings.TrySet(SteeringAction.PitchUp, new(105), out _));
        Assert.True(settings.pfviewBindings.TrySet(SteeringAction.Forward, default, out _));
        var restored = RoundTrip(settings);
        Assert.False(restored.replaceWorldSky);
        Assert.True(restored.spaceAiRandomEncounters);
        Assert.Equal(PFViewGroupCameraMode.ThirdPersonVelocity, restored.pfviewFocusCameraMode);
        Assert.False(restored.pfviewAssistStop);
        Assert.True(restored.pfviewInvertPitch && restored.pfviewFollowRoll && restored.pfviewInvertMouseY
            && restored.pfviewInvertWheel && restored.pfviewRecenter);
        Assert.Equal(.4f, restored.pfviewPrecisionScale);
        Assert.Equal(1.7f, restored.pfviewSensitivity);
        Assert.Equal(.2f, restored.pfviewFollowSmoothing);
        Assert.True(restored.pfviewBindings.Matches(SteeringAction.PitchUp, 105, 0, false));
        Assert.False(restored.pfviewBindings[SteeringAction.Forward].IsBound);
    }

    [Fact]
    public void MissingPilotConfigurationEnablesStableAssistedDefaultsAndKeyboardDriving()
    {
        var settings = Load("<pilot-settings><replaceWorldSky>False</replaceWorldSky></pilot-settings>");
        Assert.False(settings.replaceWorldSky);
        Assert.True(settings.pfviewAssistStop);
        Assert.False(settings.pfviewFollowRoll || settings.pfviewInvertPitch || settings.pfviewRecenter);
        Assert.Equal(.25f, settings.pfviewPrecisionScale);
        Assert.True(settings.pfviewBindings.Matches(SteeringAction.PitchUp, 119, 0, false));
    }

    [Fact]
    public void DamagedNumericOptionsAreNormalizedToUsableRanges()
    {
        var settings = new PioneerFrameworkLiteSettings
        {
            pfviewPrecisionScale = float.NaN, pfviewSensitivity = -10, pfviewFollowSmoothing = 100
        };
        settings.NormalizePilotSettings();
        Assert.Equal(.25f, settings.pfviewPrecisionScale);
        Assert.Equal(.25f, settings.pfviewSensitivity);
        Assert.Equal(.30f, settings.pfviewFollowSmoothing);
    }

    private static PioneerFrameworkLiteSettings RoundTrip(PioneerFrameworkLiteSettings settings)
        => WithScribe(path =>
        {
            Scribe.saver.InitSaving(path, "pilot-settings");
            settings.ExposeData();
            Scribe.saver.FinalizeSaving();
            return Read(path);
        });

    private static PioneerFrameworkLiteSettings Load(string xml) => WithScribe(path =>
    {
        File.WriteAllText(path, xml);
        return Read(path);
    });

    private static PioneerFrameworkLiteSettings Read(string path)
    {
        var settings = new PioneerFrameworkLiteSettings();
        Scribe.loader.InitLoading(path);
        settings.ExposeData();
        Scribe.loader.FinalizeLoading();
        return settings;
    }

    private static PioneerFrameworkLiteSettings WithScribe(Func<string, PioneerFrameworkLiteSettings> action)
    {
        string path = Path.Combine(Path.GetTempPath(), "pfl-pilot-" + Guid.NewGuid() + ".xml");
        var prefs = AccessTools.Field(typeof(Prefs), "data");
        object previous = prefs.GetValue(null);
        prefs.SetValue(null, RuntimeHelpers.GetUninitializedObject(prefs.FieldType));
        try { return action(path); }
        finally { Scribe.ForceStop(); prefs.SetValue(null, previous); if (File.Exists(path)) File.Delete(path); }
    }
}
