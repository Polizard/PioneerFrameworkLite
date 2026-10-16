using System;
using System.IO;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PioneerFramework.RingWorld;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    [CollectionDefinition("Ring Scribe",DisableParallelization=true)]
    public class RingScribeCollection { }

    [Collection("Ring Scribe")]
    public class RingWorldSaveTests
    {
        [Fact]
        public void ScribeValuesRoundTripPreservesNonDefaultGridIdentityAndRadius()
        {
            string path=Path.Combine(Path.GetTempPath(),"PFL-ring-save-"+Guid.NewGuid()+".xml");
            var expected=new RingParameters("second-ring",2,89431,128,24,18375000);
            RingWorldSaveData data=RingWorldSaveData.From(expected);
            // Scribe's profiler reads Prefs.LogVerbose; the standalone host has no preferences bootstrap.
            var prefsField=AccessTools.Field(typeof(Prefs),"data");
            object previousPrefs=prefsField.GetValue(null);
            prefsField.SetValue(null,RuntimeHelpers.GetUninitializedObject(prefsField.FieldType));
            try
            {
                Scribe.saver.InitSaving(path,"ring-test");
                Scribe_Deep.Look(ref data,"ring");
                Scribe.saver.FinalizeSaving();
                var restored=new RingWorldSaveData();
                Scribe.loader.InitLoading(path);
                Assert.True(Scribe.EnterNode("ring"));
                restored.ExposeData();
                Scribe.ExitNode();
                Scribe.loader.FinalizeLoading();
                Assert.Equal(expected,restored.ToParameters());
            }
            finally { Scribe.ForceStop(); prefsField.SetValue(null,previousPrefs); if(File.Exists(path)) File.Delete(path); }
        }
    }
}
