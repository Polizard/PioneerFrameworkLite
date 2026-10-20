using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PioneerFramework.Combat;
using PioneerFramework.SpaceAI;
using UnityEngine;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    public class PFViewMapWeaponTests
    {
        [Fact]
        public void PlayerWeaponListUsesActualMapMountsAndDropsArchivedOrRemovedEquipment()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var group = host.AddShip();
            var (entity, building, turret, mc) = AddMapGun(host, group);
            var combat = host.Game.GetComponent<CombatManager>();

            Assert.Same(turret.Mount.weapon, Assert.Single(combat.GetGroupWeapons(group.GroupId)));
            Assert.True(combat.TryGetMapWeaponMount(group.GroupId, turret.Mount.weapon, out var owner, out var mount));
            Assert.Same(entity, owner); Assert.Same(building, mount);
            Assert.False(combat.TryGetMapWeaponMount(group.GroupId, new WeaponInstance(), out _, out _));
            mc.ownerEntityId = 31;
            Assert.Empty(combat.GetGroupWeapons(group.GroupId));
            mc.ownerEntityId = 30;
            entity.IsMapArchived = true;
            Assert.Empty(combat.GetGroupWeapons(group.GroupId));
            Assert.False(combat.TryGetMapWeaponMount(group.GroupId, turret.Mount.weapon, out _, out _));
            entity.IsMapArchived = false;
            Assert.Single(combat.GetGroupWeapons(group.GroupId));
            mc.UnregisterSpaceWeapon(building);
            Assert.Empty(combat.GetGroupWeapons(group.GroupId));
        }

        [Theory]
        [InlineData(true, 100, true, true)]
        [InlineData(false, 100, true, true)]
        [InlineData(true, 0, true, false)]
        [InlineData(true, 100, false, false)]
        public void ContactFireUsesObservedAimAndRequiresWorkingEquipment(bool movable, int hitPoints, bool known, bool expected)
        {
            using var host = new SpaceAiRuntimeTestHost();
            var group = host.AddShip();
            group.IsMovable = movable;
            var (entity, building, turret, _) = AddMapGun(host, group);
            AccessTools.Field(typeof(Thing), "hitPointsInt").SetValue(building, hitPoints);
            var target = new SpaceEntityGroup(20, 3) { IsStealth = true, CelestialBodyId = 0 };
            target.CurrentOrbit = new OrbitState { positionSystem = (SystemVec3)new DVector3(0, 0, 1000000) };
            host.Space.RegisterGroup(target);
            var sensor = new SensorManager(host.Game);
            host.Game.components.Add(sensor);
            if (known)
            {
                var pictures = (Dictionary<int, SensorManager.FactionPicture>)AccessTools.Field(typeof(SensorManager), "pictures").GetValue(sensor);
                pictures[2] = new SensorManager.FactionPicture();
                pictures[2].tracks.Add(new SensorManager.ContactRecord { memorySeconds = 30, contact = new Contact
                    { targetGroupId = 20, decoyId = -1, position = new DVector3(1000, 0, 0), lastUpdatePhysics = 0 } });
            }
            var weapon = turret.Mount.weapon;
            weapon.ammo = 3;
            var combat = host.Game.GetComponent<CombatManager>();
            Assert.Equal(expected, ContactTargetService.TryFire(host.Space, sensor, combat, group, entity, building, weapon, new ContactTarget(20, -1)));
            Assert.Equal(expected ? 2 : 3, weapon.ammo);
            if (expected)
            {
                var projectile = Assert.Single(combat.ActiveProjectiles);
                Assert.True(projectile.velocity.x > 0);
                Assert.Equal(0, projectile.velocity.z);
                Assert.Equal(20, projectile.spawnPosition.x, 6);
            }
            else Assert.Empty(combat.ActiveProjectiles);
        }

        [Fact]
        public void UnidentifiedFalseContactCanBeFiredOnAndExpiresWithSensorKnowledge()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var group = host.AddShip();
            var (entity, building, turret, _) = AddMapGun(host, group);
            AccessTools.Field(typeof(Thing), "hitPointsInt").SetValue(building, 100);
            var sensor = new SensorManager(host.Game);
            var contact = new Contact { targetGroupId = -1, decoyId = 9, position = new DVector3(1000, 0, 0) };
            var pictures = (Dictionary<int, SensorManager.FactionPicture>)AccessTools.Field(typeof(SensorManager), "pictures").GetValue(sensor);
            pictures[2] = new SensorManager.FactionPicture();
            pictures[2].tracks.Add(new SensorManager.ContactRecord { contact = contact });
            var decoys = (Dictionary<int, SensorManager.DecoyRecord>)AccessTools.Field(typeof(SensorManager), "decoys").GetValue(sensor);
            decoys[9] = new SensorManager.DecoyRecord { contact = contact, expirePhysics = 10, ownerFactionId = 3 };
            var combat = host.Game.GetComponent<CombatManager>();
            var weapon = turret.Mount.weapon;
            weapon.ammo = 3;
            Assert.True(ContactTargetService.TryFire(host.Space, sensor, combat, group, entity, building, weapon, new ContactTarget(-1, 9)));
            Assert.Single(combat.ActiveProjectiles);
            weapon.cooldownRemaining = 0;
            sensor.RemoveDecoy(9);
            Assert.False(ContactTargetService.TryFire(host.Space, sensor, combat, group, entity, building, weapon, new ContactTarget(-1, 9)));
            Assert.Single(combat.ActiveProjectiles);
            Assert.Equal(2, weapon.ammo);
        }

        private static (SpaceEntity entity, Building building, CompMapTurret turret, SpaceMapComp map) AddMapGun(
            SpaceAiRuntimeTestHost host, SpaceEntityGroup group)
        {
            var entity = new SpaceEntity(30, 2) { GroupId = group.GroupId, HasMap = true, MapId = 42 };
            entity.BoundsSize = new DVector3(40, 8, 40);
            entity.SetRotation(Quaternion.identity);
            host.Space.RegisterEntity(entity);
            var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
            map.uniqueID = 42;
            map.info = new MapInfo { Size = new IntVec3(40, 1, 40) };
            AccessTools.Field(typeof(Game), "maps").SetValue(host.Game, new List<Map> { map });
            var mc = new SpaceMapComp(map) { ownerEntityId = 30 };
            map.components = new List<MapComponent> { mc };
            host.Space.RegisterMapComponent(42, mc);
            var buildingDef = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef));
            buildingDef.defName = "PF_PlayerWeaponTestBuilding";
            var building = new Building { def = buildingDef, Position = new IntVec3(20, 0, 20) };
            var mapIndex = AccessTools.Field(typeof(Thing), "mapIndexOrState");
            mapIndex.SetValue(building, Convert.ChangeType(0, mapIndex.FieldType));
            const string weaponName = "PF_PlayerWeaponTestCannon";
            if (DefDatabase<WeaponDef>.GetNamedSilentFail(weaponName) == null)
                DefDatabase<WeaponDef>.Add(new WeaponDef { defName = weaponName });
            var turret = new CompMapTurret { parent = building };
            turret.Initialize(new CompProperties_MapTurret { weaponDefName = weaponName });
            AccessTools.Field(typeof(ThingWithComps), "comps").SetValue(building, new List<ThingComp> { turret });
            turret.PostSpawnSetup(false);
            return (entity, building, turret, mc);
        }
    }
}
