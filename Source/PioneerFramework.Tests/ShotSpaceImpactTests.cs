using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PioneerFramework.Combat;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    public class ShotSpaceImpactTests
    {
        [Fact]
        public void BallisticInterceptRemovesOnlyHitMissileAndDetonatesIt()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var shooter = host.AddShip();
            var target = AddTarget(host, 21, 100);
            var sibling = new SpaceEntity(122, 3) { GroupId = 21, Position = new DVector3(0, 0, 20) };
            sibling.SetParts(new List<SpacePart> { new() { partId = 1, hp = 100, boundsSizeShip = new DVector3(10, 10, 10) } });
            sibling.SetRotation(Quaternion.identity);
            host.Space.RegisterEntity(sibling);
            var avm = new AutonomousVehicleManager(host.Game);
            host.Game.components.Add(avm);
            const string missileName = "PF_Test_ShotInterceptMissile";
            if (Verse.DefDatabase<MissileDef>.GetNamedSilentFail(missileName) == null)
                Verse.DefDatabase<MissileDef>.Add(new MissileDef { defName = missileName, blastRadius = 1, blastCenterDamage = 0, blastImpulseEnergy = 0 });
            var record = avm.RegisterMissileLeader(21, target.EntityId, 1, 10, 2, missileName, null,
                AutonomousVehicleManager.MissionType.Standby, -1);
            var definition = ShotRuntimeTests.Ballistic(new ShotCompProperties_DirectDamage { partDamage = 1, mapDamage = 0 });
            var weapon = new WeaponInstance { def = new WeaponDef { defName = "InterceptGun", defaultShot = definition, allowedShots = new() { definition } } };
            var combat = host.Game.GetComponent<CombatManager>();
            combat.FireWeapon(shooter.GroupId, weapon, new DVector3(200, 0, 0));
            var projectile = Assert.Single(combat.ActiveProjectiles);
            projectile.position = (SystemVec3)new DVector3(200, 0, 0);
            AccessTools.Method(typeof(CombatManager), "CheckProjectileCollision").Invoke(combat, new object[] { projectile, host.Space });
            Assert.Null(host.Space.GetEntityById(target.EntityId));
            Assert.Same(sibling, host.Space.GetEntityById(sibling.EntityId));
            Assert.Equal(1, record.logicalCount);
            Assert.Empty(combat.ActiveProjectiles);
            Assert.Single(combat.ActiveExplosions);
            var detonation = Assert.Single(Array.FindAll(ReadImpacts(combat), e => e.Kind == CombatImpactKind.Detonation));
            Assert.Equal(missileName, detonation.SourceDef.defName);
            Assert.Equal(target.EntityId, detonation.SourceEntityId);
        }

        [Fact]
        public void RayRemovesMissileOnlyWhenItsPartsAreDestroyed()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var shooter = host.AddShip();
            var target = AddTarget(host, 21, 100);
            var avm = new AutonomousVehicleManager(host.Game);
            host.Game.components.Add(avm);
            avm.RegisterMissileLeader(21, target.EntityId, 1, 10, 1, null, null,
                AutonomousVehicleManager.MissionType.Standby, -1);
            var damage = new ShotCompProperties_DirectDamage { partDamage = 10, mapDamage = 0 };
            var shot = new ShotDef { defName = "MissileDamageRay", comps = new()
            { new ShotCompProperties_InstantRay { rangeMeters = 500 }, damage } };
            var weapon = new WeaponInstance { def = new WeaponDef { defName = "MissileDamageGun", defaultShot = shot, allowedShots = new() { shot } } };
            var combat = host.Game.GetComponent<CombatManager>();
            combat.FireWeapon(shooter.GroupId, weapon, new DVector3(200, 0, 0));
            Assert.Same(target, host.Space.GetEntityById(target.EntityId));
            Assert.Equal(90, target.Parts[0].hp);
            weapon.cooldownRemaining = 0;
            damage.partDamage = 100;
            combat.FireWeapon(shooter.GroupId, weapon, new DVector3(200, 0, 0));
            Assert.Null(host.Space.GetEntityById(target.EntityId));
            Assert.Null(host.Space.GetGroupById(21));
            Assert.Null(avm.GetRecord(21));
            Assert.Empty(combat.ActiveExplosions);
        }

        [Fact]
        public void RayHitsNearestPartAndIgnoresWeaponTypeForPropagation()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var shooter = host.AddShip();
            var farther = AddTarget(host, 22, 200);
            var nearer = AddTarget(host, 21, 100);
            var shot = new ShotDef { defName = "CustomRay", label = "custom ray", comps = new()
            {
                new ShotCompProperties_InstantRay { rangeMeters = 500 },
                new ShotCompProperties_DirectDamage { partDamage = 17, mapDamage = 0 }
            }};
            var weapon = new WeaponInstance { def = new WeaponDef { defName = "TestGun", weaponType = WeaponType.Projectile,
                defaultShot = shot, allowedShots = new() { shot }, maxRange = 500 }, ammo = 2 };
            var combat = host.Game.GetComponent<CombatManager>();
            combat.FireWeapon(shooter.GroupId, weapon, new DVector3(300, 0, 0));
            Assert.Equal(83, nearer.Parts[0].hp);
            Assert.Equal(100, farther.Parts[0].hp);
            Assert.Empty(host.Game.GetComponent<CombatManager>().ActiveProjectiles);
            Assert.Equal(1, weapon.ammo);
            var impact = Assert.Single(ReadImpacts(combat));
            Assert.Equal(CombatImpactKind.Contact, impact.Kind);
            Assert.True(impact.IsInstant);
            Assert.Same(shot, impact.SourceDef);
            Assert.Equal(nearer.EntityId, impact.TargetEntityId);
            Assert.Equal(new DVector3(-5, 0, 0), impact.TargetLocalPoint);

            weapon.cooldownRemaining = 0;
            combat.FireWeapon(shooter.GroupId, weapon, new DVector3(0, 300, 0));
            Assert.Single(ReadImpacts(combat));
        }

        [Fact]
        public void PenetrationVisitsTargetsInOrderAndStopsAtConfiguredHitBudget()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var shooter = host.AddShip();
            var fourth = AddTarget(host, 24, 400);
            var second = AddTarget(host, 22, 200);
            var first = AddTarget(host, 21, 100);
            var third = AddTarget(host, 23, 300);
            var definition = new ShotDef { defName = "PiercingRay", label = "piercing", comps = new()
            {
                new ShotCompProperties_InstantRay { rangeMeters = 500 },
                new ShotCompProperties_DirectDamage { partDamage = 32, mapDamage = 0 },
                new ShotCompProperties_Penetration { initialBudget = 1000, damageRetention = 0.5f, maxHits = 3 }
            }};
            var weapon = new WeaponInstance { def = new WeaponDef { defName = "PiercingGun", defaultShot = definition, allowedShots = new() { definition }, maxRange = 500 } };
            var combat = host.Game.GetComponent<CombatManager>();
            combat.FireWeapon(shooter.GroupId, weapon, new DVector3(500, 0, 0));
            Assert.Equal(68, first.Parts[0].hp);
            Assert.Equal(84, second.Parts[0].hp);
            Assert.Equal(92, third.Parts[0].hp);
            Assert.Equal(100, fourth.Parts[0].hp);
            var impacts = ReadImpacts(combat);
            Assert.Equal(new[] { first.EntityId, second.EntityId, third.EntityId },
                Array.ConvertAll(impacts, e => e.TargetEntityId));
            Assert.All(impacts, e => Assert.Equal(impacts[0].HostTick, e.HostTick));
        }

        [Fact]
        public void LegacyRayPublishesConfirmedContactsInTheSameHostTick()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var shooter = host.AddShip();
            var target = AddTarget(host, 21, 100);
            var def = new WeaponDef { defName = "LegacyRay", weaponType = WeaponType.Ray,
                damagePerShot = 10, maxRange = 500, rayMaxLength = 500 };
            var weapon = new WeaponInstance { def = def, ammo = 3 };
            var combat = host.Game.GetComponent<CombatManager>();
            combat.FireWeapon(shooter.GroupId, weapon, new DVector3(300, 0, 0));
            weapon.cooldownRemaining = 0;
            combat.FireWeapon(shooter.GroupId, weapon, new DVector3(300, 0, 0));
            weapon.cooldownRemaining = 0;
            combat.FireWeapon(shooter.GroupId, weapon, new DVector3(0, 300, 0));

            var hits = ReadImpacts(combat);
            Assert.Equal(2, hits.Length);
            Assert.Equal(hits[0].Sequence + 1, hits[1].Sequence);
            Assert.All(hits, hit =>
            {
                Assert.Same(def, hit.SourceDef);
                Assert.True(hit.IsInstant);
                Assert.Equal(target.EntityId, hit.TargetEntityId);
                Assert.Equal(hits[0].HostTick, hit.HostTick);
            });
            Assert.True(target.Parts[0].hp < 100);
        }

        [Fact]
        public void ExplosionUsesItsOwnXmlRadiusAndFalloffOnSpaceParts()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var shooter = host.AddShip();
            var first = AddTarget(host, 21, 100);
            var second = AddTarget(host, 22, 110);
            var outside = AddTarget(host, 23, 130);
            var definition = new ShotDef { defName = "ExplosiveRay", label = "explosive", comps = new()
            {
                new ShotCompProperties_InstantRay { rangeMeters = 500 },
                new ShotCompProperties_Explosion { mapCenterDamage = 0, mapRadiusCells = 0, spaceRadiusMeters = 20, partCenterDamage = 60 }
            }};
            var weapon = new WeaponInstance { def = new WeaponDef { defName = "ExplosiveGun", defaultShot = definition, allowedShots = new() { definition }, maxRange = 500 } };
            var combat = host.Game.GetComponent<CombatManager>();
            combat.FireWeapon(shooter.GroupId, weapon, new DVector3(500, 0, 0));
            Assert.Equal(55, first.Parts[0].hp);
            Assert.Equal(85, second.Parts[0].hp);
            Assert.Equal(100, outside.Parts[0].hp);
            var detonation = Assert.Single(Array.FindAll(ReadImpacts(combat), e => e.Kind == CombatImpactKind.Detonation));
            Assert.Equal(20, detonation.RadiusMeters);
            Assert.True(detonation.IsInstant);
            Assert.Equal(first.EntityId, detonation.TargetEntityId);
            Assert.True(Assert.Single(combat.ActiveExplosions).HasImpactEvent);
        }

        [Fact]
        public void BallisticRuntimeSnapshotsSelectionAndMuzzleFactors()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var shooter = host.AddShip();
            var a = ShotRuntimeTests.Ballistic();
            var b = ShotRuntimeTests.Ballistic();
            ((ShotCompProperties_Ballistic)b.comps[0]).referenceSpeed = 200;
            var weapon = new WeaponInstance { def = new WeaponDef { defName = "Gun", defaultShot = a,
                allowedShots = new() { a, b }, muzzleVelocityFactor = 2 }, mountVelocityFactor = 0.5f, ammo = 3 };
            var combat = host.Game.GetComponent<CombatManager>();
            combat.FireWeapon(shooter.GroupId, weapon, new DVector3(1000, 0, 0));
            var projectile = Assert.Single(combat.ActiveProjectiles);
            Assert.Equal(1000, projectile.velocity.x);
            Assert.True(weapon.TrySelectShot(b));
            Assert.Same(a, projectile.shot.Definition);
            Assert.Equal(1000, projectile.shot.Profile.Speed);
            Assert.Equal(2, weapon.ammo);
        }

        private static SpaceEntity AddTarget(SpaceAiRuntimeTestHost host, int id, double x)
        {
            var group = new SpaceEntityGroup(id, 3) { CelestialBodyId = 0, IsCollidable = true };
            group.CurrentOrbit = new OrbitState { positionSystem = (SystemVec3)new DVector3(x, 0, 0) };
            var entity = new SpaceEntity(id + 100, 3) { GroupId = id };
            entity.SetParts(new List<SpacePart> { new SpacePart { partId = 1, hp = 100, boundsSizeShip = new DVector3(10, 10, 10) } });
            entity.SetRotation(Quaternion.identity);
            host.Space.RegisterGroup(group);
            host.Space.RegisterEntity(entity);
            return entity;
        }

        private static CombatImpactEvent[] ReadImpacts(CombatManager combat)
        {
            var events = new CombatImpactEvent[16];
            int count = combat.CopyImpactEvents(0, events);
            Array.Resize(ref events, count);
            return events;
        }
    }
}
