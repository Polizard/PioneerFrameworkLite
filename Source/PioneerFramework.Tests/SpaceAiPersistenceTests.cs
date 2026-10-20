using System;
using System.IO;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PioneerFramework.Combat;
using PioneerFramework.SpaceAI;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    public class SpaceAiPersistenceTests
    {
        private static bool ThrowError(string text) => throw new InvalidOperationException(text);
        private static bool FindType(string typeName,ref Type __result)
        {
            Type found=typeof(SpaceAiManager).Assembly.GetType(typeName) ?? typeof(SpaceAiPersistenceTests).Assembly.GetType(typeName);
            if(found==null)return true;__result=found;return false;
        }
        [Fact] public void ShipIntent_PreservesActivationWithdrawalAndControlVersion()
        {
            var original=new AiShipRecord {groupId=10,factionId=2,fleetId=4,activateAt=100,
                targetDecoyId=7,ownsBurn=true,ownedBurnVersion=21,withdrawing=true,retreatStarted=90,
                retreatDirection=new DVector3(1,2,3)};
            var copy=RoundTrip(original);
            Assert.Equal(100,copy.activateAt);Assert.Equal(21,copy.ownedBurnVersion);Assert.True(copy.withdrawing);
            Assert.Equal(7,copy.targetDecoyId);Assert.Equal(original.retreatDirection,copy.retreatDirection);
        }
        [Fact] public void MissileContactIdentity_PreservesNoEntityTarget()
        {
            var original=new AutonomousVehicleManager.VehicleRecord {groupId=10,targetGroupId=-1,targetDecoyId=7,
                contactOnlyAim=true,mission=AutonomousVehicleManager.MissionType.Attack};
            var copy=RoundTrip(original);
            Assert.Equal(-1,copy.targetGroupId);Assert.Equal(7,copy.targetDecoyId);Assert.True(copy.contactOnlyAim);
        }
        [Fact] public void PreparingEncounter_PreservesPartialGenerationForRecovery()
        {
            var original=new SpaceEncounterRecord {id=4,factionId=2,requestedCount=3,defName="PF_Encounter_RaidingFleet",status=SpaceEncounterStatus.Preparing};
            original.groupIds.Add(10);
            var copy=RoundTrip(original);
            Assert.Equal(SpaceEncounterStatus.Preparing,copy.status);Assert.Single(copy.groupIds);Assert.Equal(10,copy.groupIds[0]);Assert.Equal(3,copy.requestedCount);
        }
        [Fact] public void SearchLifecycle_SurvivesReloadWithoutRestartingTimeout()
        {
            var entry = new SpaceEncounterRecord();
            Assert.False(entry.UpdateSearch(100, false, false, 180));
            var copy = RoundTrip(entry);
            Assert.Equal(100, copy.searchLostAt);
            Assert.True(copy.UpdateSearch(280, false, false, 180));
            var abandoned = RoundTrip(copy);
            Assert.True(abandoned.searchAbandoned);
        }
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WeaponComponents_PreserveFiniteAmmoHeatAndCooldownAfterRespawn(bool missile)
        {
            var original = new SpaceAiWeaponComponentState { missile = missile };
            var weapon = original.Weapon;
            weapon.Update(100);
            weapon.ammo = 9;
            weapon.cooldownRemaining = 120;
            weapon.overheatCounter = 7;
            weapon.isOverheated = true;
            var copy = RoundTrip(original);
            copy.Respawn();
            Assert.Equal(9, copy.Weapon.ammo);
            Assert.Equal(120, copy.Weapon.cooldownRemaining);
            Assert.Equal(7, copy.Weapon.overheatCounter);
            Assert.True(copy.Weapon.isOverheated);
            copy.Weapon.Update(105);
            Assert.Equal(115, copy.Weapon.cooldownRemaining);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LegacyWeaponComponents_KeepInfiniteAmmoDefault(bool missile)
        {
            var original = new SpaceAiWeaponComponentState { missile = missile };
            var copy = RoundTrip(original, xml => System.Text.RegularExpressions.Regex.Replace(
                xml, @"<weapon\b[^>]*>.*?</weapon>", "", System.Text.RegularExpressions.RegexOptions.Singleline));
            copy.Respawn();
            Assert.Equal(WeaponInstance.AmmoInfinite, copy.Weapon.ammo);
            Assert.Equal(0, copy.Weapon.cooldownRemaining);
        }

        private static T RoundTrip<T>(T original, Func<string,string> edit = null) where T:IExposable
        {
            string path=Path.Combine(Path.GetTempPath(),"pfl-ai-"+Guid.NewGuid()+".xml");
            var prefs=AccessTools.Field(typeof(Prefs),"data");object previous=prefs.GetValue(null);
            prefs.SetValue(null,RuntimeHelpers.GetUninitializedObject(prefs.FieldType));
            var bootstrap=new Harmony("PFL.SpaceAI.Scribe");
            bootstrap.Patch(AccessTools.Method(typeof(Log),nameof(Log.Error),new[]{typeof(string)}),prefix:new HarmonyMethod(typeof(SpaceAiPersistenceTests),nameof(ThrowError)));
            bootstrap.Patch(AccessTools.Method(typeof(GenTypes),nameof(GenTypes.GetTypeInAnyAssembly),new[]{typeof(string),typeof(string)}),prefix:new HarmonyMethod(typeof(SpaceAiPersistenceTests),nameof(FindType)));
            try
            {
                Scribe.saver.InitSaving(path,"space-ai-test");Scribe_Deep.Look(ref original,"data");Scribe.saver.FinalizeSaving();
                if (edit != null) File.WriteAllText(path, edit(File.ReadAllText(path)));
                T restored=default;Scribe.loader.InitLoading(path);Scribe_Deep.Look(ref restored,"data");Scribe.loader.FinalizeLoading();return restored;
            }
            finally {bootstrap.UnpatchAll(bootstrap.Id);Scribe.ForceStop();prefs.SetValue(null,previous);if(File.Exists(path))File.Delete(path);}
        }
    }

    public class SpaceAiWeaponComponentState : IExposable
    {
        public bool missile;
        private readonly CompMapTurret turret;
        private readonly CompMissileLauncher launcher;
        public SpaceAiWeaponComponentState()
        {
            const string name = "PF_SpaceAiPersistenceWeapon";
            var def = DefDatabase<WeaponDef>.GetNamedSilentFail(name);
            if (def == null) { def = new WeaponDef { defName = name }; DefDatabase<WeaponDef>.Add(def); }
            turret = new CompMapTurret { parent = new ThingWithComps() };
            turret.Initialize(new CompProperties_MapTurret { weaponDefName = name });
            AccessTools.Field(typeof(CompMapTurret), "mount").SetValue(turret,
                new TurretMount { weapon = new WeaponInstance { def = def } });
            launcher = new CompMissileLauncher { parent = new ThingWithComps() };
            launcher.Initialize(new CompProperties_MissileLauncher { weaponDefName = name });
            AccessTools.Field(typeof(CompMissileLauncher), "weapon").SetValue(launcher, new WeaponInstance { def = def });
        }
        public WeaponInstance Weapon => missile ? launcher.Weapon : turret.Mount.weapon;
        public void Respawn()
        {
            if (missile) launcher.PostSpawnSetup(true);
            else turret.PostSpawnSetup(true);
        }
        public void ExposeData()
        {
            Scribe_Values.Look(ref missile, "missile");
            if (missile) launcher.PostExposeData();
            else turret.PostExposeData();
        }
    }
}
