using System;
using System.IO;
using System.Text.RegularExpressions;
using HarmonyLib;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class TimeScalePersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScribePreservesPendingIntervalsAndMigratesLegacyClockWithoutHistoricalCatchup(bool legacy)
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            clock.SetFastMode();
            TimeScaleProtocolTests.SetTick(101);
            clock.SetCombatModeImmediate();
            var restored = RoundTrip(clock, legacy);
            Assert.Equal(108, restored.GetPhysicsTick());
            Assert.Equal(legacy ? TimeScaleLevel.Normal : TimeScaleLevel.Fast, restored.RequestedLevel);
            Assert.Equal(legacy ? 101 : 100, restored.CommittedGameTick);
            TimeScaleProtocolTests.SetTick(102);
            restored.BeginPhysicsBatch();
            double integrated = 0;
            while (restored.TryBeginPhysicsStep(102))
            {
                integrated += restored.PhysicsStepSeconds;
                restored.CompletePhysicsStep();
            }
            Assert.Equal(legacy ? 1.0 / 60.0 : 8 + 1.0 / 60.0, integrated, 10);
            restored.EndPhysicsBatch();
        });
    }

    private static TimeScaleManager RoundTrip(TimeScaleManager original, bool legacy)
    {
        string path = Path.Combine(Path.GetTempPath(), "pfl-clock-" + Guid.NewGuid() + ".xml");
        var prefs = AccessTools.Field(typeof(Prefs), "data");
        object previous = prefs.GetValue(null);
        prefs.SetValue(null, System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(prefs.FieldType));
        var patch = new Harmony("PFL.TimeScaleScribe");
        patch.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }),
            prefix: new HarmonyMethod(typeof(TimeScalePersistenceTests), nameof(ThrowError)));
        patch.Patch(AccessTools.Method(typeof(GenTypes), nameof(GenTypes.GetTypeInAnyAssembly), new[] { typeof(string), typeof(string) }),
            prefix: new HarmonyMethod(typeof(TimeScalePersistenceTests), nameof(FindType)));
        try
        {
            Scribe.saver.InitSaving(path, "clock-test");
            Scribe_Deep.Look(ref original, "clock", Current.Game);
            Scribe.saver.FinalizeSaving();
            if (legacy)
            {
                string xml = File.ReadAllText(path);
                xml = Regex.Replace(xml, @"<(physicsEpochGameTicks|physicsEpochTicks|physicsEpochLevels|physicsCommittedGameTick|physicsCommittedTick|requestedTimeScale)>.*?</\1>", "", RegexOptions.Singleline);
                File.WriteAllText(path, xml);
            }
            TimeScaleManager restored = null;
            Scribe.loader.InitLoading(path);
            Scribe_Deep.Look(ref restored, "clock", Current.Game);
            Scribe.loader.FinalizeLoading();
            return restored;
        }
        finally
        {
            patch.UnpatchAll(patch.Id);
            Scribe.ForceStop();
            prefs.SetValue(null, previous);
            File.Delete(path);
        }
    }

    private static bool ThrowError(string text) => throw new InvalidOperationException(text);
    private static bool FindType(string typeName, ref Type __result)
    {
        __result = typeof(TimeScaleManager).Assembly.GetType(typeName);
        return __result == null;
    }
}
