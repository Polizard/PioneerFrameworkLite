using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using HarmonyLib;
using PioneerFramework;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class WorldRadarVisualSettingsTests
{
    // Reopening the game must preserve a hidden effect; an old config keeps it visible.
    [Theory]
    [InlineData("False", false)]
    [InlineData("True", true)]
    [InlineData(null, true)]
    public void RadarEffectVisibilitySurvivesConfigLoadAndSave(string storedValue, bool expectedVisible)
    {
        string path = Path.Combine(Path.GetTempPath(), "pfl-world-radar-" + Guid.NewGuid() + ".xml");
        var prefs = AccessTools.Field(typeof(Prefs), "data");
        object previous = prefs.GetValue(null);
        prefs.SetValue(null, RuntimeHelpers.GetUninitializedObject(prefs.FieldType));
        try
        {
            string value = storedValue == null ? ""
                : $"<showWorldRadarScanEffect>{storedValue}</showWorldRadarScanEffect>";
            File.WriteAllText(path, $"<world-radar-settings>{value}</world-radar-settings>");
            var settings = new PioneerFrameworkLiteSettings();
            for (int i = 0; i < 2; i++)
            {
                Scribe.loader.InitLoading(path);
                settings.ExposeData();
                Scribe.loader.FinalizeLoading();
                Scribe.saver.InitSaving(path, "world-radar-settings");
                settings.ExposeData();
                Scribe.saver.FinalizeSaving();

                string savedValue = XDocument.Load(path).Root?.Element("showWorldRadarScanEffect")?.Value;
                Assert.Equal(expectedVisible, bool.Parse(savedValue ?? "True"));
                settings = new PioneerFrameworkLiteSettings();
            }
        }
        finally
        {
            Scribe.ForceStop();
            prefs.SetValue(null, previous);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
