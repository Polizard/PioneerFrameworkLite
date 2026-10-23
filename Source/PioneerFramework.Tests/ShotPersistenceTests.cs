using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HarmonyLib;
using PioneerFramework.Combat;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    public class ShotPersistenceTests
    {
        [Theory]
        [InlineData("current")]
        [InlineData("legacy")]
        [InlineData("missing-reference")]
        public void ScribePreservesSelectionAndResourcesAndRejectsLostExplicitSelection(string mode)
        {
            const string name = "PF_Test_ScribeShot";
            var shot = DefDatabase<ShotDef>.GetNamedSilentFail(name);
            if (shot == null)
            {
                shot = ShotRuntimeTests.Ballistic();
                shot.defName = name;
                DefDatabase<ShotDef>.Add(shot);
            }
            var definition = new WeaponDef { defName = "ScribeGun", defaultShot = shot, allowedShots = new() { shot } };
            var original = new WeaponInstance { def = definition, ammo = 9, cooldownRemaining = 17, overheatCounter = 3 };
            Assert.True(original.TrySelectShot(shot));
            var copy = RoundTrip(original, xml => mode switch
            {
                "legacy" => Regex.Replace(xml, @"<selectedShot(?:Name)?>.*?</selectedShot(?:Name)?>", "", RegexOptions.Singleline),
                "missing-reference" => Regex.Replace(xml, @"<selectedShot>.*?</selectedShot>", "", RegexOptions.Singleline),
                _ => xml
            });
            copy.def = definition;
            Assert.Equal(9, copy.ammo);
            Assert.Equal(17, copy.cooldownRemaining);
            Assert.Equal(3, copy.overheatCounter);
            Assert.Equal(mode == "current" ? shot : null, copy.selectedShot);
            Assert.Equal(mode != "missing-reference", copy.TryGetSelectedShot(out var restored));
            if (mode != "missing-reference") Assert.Same(shot, restored);
        }

        private static WeaponInstance RoundTrip(WeaponInstance original, Func<string, string> edit)
        {
            string path = Path.Combine(Path.GetTempPath(), "pfl-shot-" + Guid.NewGuid() + ".xml");
            var prefs = AccessTools.Field(typeof(Prefs), "data");
            object previous = prefs.GetValue(null);
            prefs.SetValue(null, RuntimeHelpers.GetUninitializedObject(prefs.FieldType));
            var bootstrap = new Harmony("PFL.ShotScribeTest");
            bootstrap.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(ShotPersistenceTests), nameof(ThrowError)));
            bootstrap.Patch(AccessTools.Method(typeof(GenTypes), nameof(GenTypes.GetTypeInAnyAssembly), new[] { typeof(string), typeof(string) }),
                prefix: new HarmonyMethod(typeof(ShotPersistenceTests), nameof(FindType)));
            try
            {
                Scribe.saver.InitSaving(path, "shot-test");
                Scribe_Deep.Look(ref original, "data");
                Scribe.saver.FinalizeSaving();
                File.WriteAllText(path, edit(File.ReadAllText(path)));
                WeaponInstance restored = null;
                Scribe.loader.InitLoading(path);
                Scribe_Deep.Look(ref restored, "data");
                Scribe.loader.FinalizeLoading();
                return restored;
            }
            finally
            {
                bootstrap.UnpatchAll(bootstrap.Id);
                Scribe.ForceStop();
                prefs.SetValue(null, previous);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static bool ThrowError(string text) => throw new InvalidOperationException(text);
        private static bool FindType(string typeName, ref Type __result)
        {
            __result = typeof(WeaponInstance).Assembly.GetType(typeName);
            return __result == null;
        }
    }
}
