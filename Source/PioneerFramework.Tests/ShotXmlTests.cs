using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml;
using HarmonyLib;
using PioneerFramework.Combat;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    public class ShotXmlTests
    {
        [Fact]
        public void UnresolvedExplicitXmlShotDoesNotBecomeALegacyWeapon()
        {
            var prefs = AccessTools.Field(typeof(Prefs), "data");
            object old = prefs.GetValue(null);
            prefs.SetValue(null, RuntimeHelpers.GetUninitializedObject(prefs.FieldType));
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml("<WeaponDef><defName>BadReferenceGun</defName><defaultShot>MissingMod_UnknownShot</defaultShot></WeaponDef>");
                var definition = DirectXmlToObject.ObjectFromXml<WeaponDef>(doc.DocumentElement, false);
                Assert.True(definition.UsesShotComponents);
                var weapon = new WeaponInstance { def = definition, ammo = 2 };
                Assert.False(weapon.CanFire());
                weapon.Fire();
                Assert.Equal(2, weapon.ammo);
            }
            finally { prefs.SetValue(null, old); }
        }

        [Fact]
        public void FourthShotVariantLoadsThroughNativeXmlAndExecutesComposedEffects()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var prefs = AccessTools.Field(typeof(Prefs), "data");
            object previousPrefs = prefs.GetValue(null);
            prefs.SetValue(null, RuntimeHelpers.GetUninitializedObject(prefs.FieldType));
            var patch = new Harmony("PFL.ShotXmlTest");
            patch.Patch(AccessTools.Method(typeof(GenTypes), nameof(GenTypes.GetTypeInAnyAssembly), new[] { typeof(string), typeof(string) }),
                prefix: new HarmonyMethod(typeof(ShotXmlTests), nameof(ResolveType)));
            patch.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(ShotXmlTests), nameof(ThrowXmlError)));
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml("""
                    <PioneerFramework.Combat.ShotDef>
                      <defName>OtherMod_CustomSlug</defName><label>custom slug</label>
                      <comps>
                        <li Class="PioneerFramework.Combat.ShotCompProperties_Ballistic">
                          <referenceSpeed>321</referenceSpeed><lifetimeSeconds>7</lifetimeSeconds><massKg>2</massKg>
                        </li>
                        <li Class="PioneerFramework.Combat.ShotCompProperties_DirectDamage">
                          <partDamage>19</partDamage><mapDamage>0</mapDamage>
                        </li>
                        <li Class="PioneerFramework.Combat.ShotCompProperties_Penetration">
                          <initialBudget>12</initialBudget><defaultResistance>5</defaultResistance><damageRetention>0.5</damageRetention>
                        </li>
                      </comps>
                    </PioneerFramework.Combat.ShotDef>
                    """);
                var definition = DirectXmlToObject.ObjectFromXml<ShotDef>(doc.DocumentElement, false);
                Assert.Equal("OtherMod_CustomSlug", definition.defName);
                Assert.True(definition.TryCreateRuntime(2, 1, out var shot, out var error), error);
                Assert.Equal(642, shot.Profile.Speed);
                Assert.Equal(7, shot.Profile.LifetimeSeconds);
                float actual = 0;
                shot.Contact(new ShotHitContext { Shot = shot, ApplyDirect = (_, part, _, _) => actual += part });
                Assert.True(shot.TryPenetrate(5));
                shot.Contact(new ShotHitContext { Shot = shot, ApplyDirect = (_, part, _, _) => actual += part });
                Assert.Equal(28.5f, actual);
            }
            finally { patch.UnpatchAll(patch.Id); prefs.SetValue(null, previousPrefs); }
        }

        private static bool ResolveType(string typeName, ref Type __result)
        {
            __result = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName)).FirstOrDefault(t => t != null);
            return false;
        }
        private static bool ThrowXmlError(string text) => throw new InvalidOperationException(text);
    }
}
