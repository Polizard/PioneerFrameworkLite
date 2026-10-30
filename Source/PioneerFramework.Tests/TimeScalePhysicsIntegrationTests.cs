using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PioneerFramework.Combat;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class TimeScalePhysicsIntegrationTests
{
    [Theory]
    [InlineData(100, false)]
    [InlineData(101, true)]
    public void RealShipAndProjectileAdvanceOverTheSameSplitInterval(int start, bool clockFirst)
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            TimeScaleProtocolTests.SetTick(start);
            var ship = AddMovingShip(host);
            var combat = host.Game.GetComponent<CombatManager>();
            var projectile = new ProjectileInstance
            {
                sourceGroupId = ship.GroupId, affectedByGravity = false, remainingLife = 100,
                position = (SystemVec3)new DVector3(10000000, 0, 0),
                spawnPosition = (SystemVec3)new DVector3(10000000, 0, 0),
                velocity = (SystemVec3)new DVector3(30, 0, 0),
                spawnVelocity = (SystemVec3)new DVector3(30, 0, 0),
                spawnPhysicsTick = clock.GetPhysicsTick(), prevPhysicsTick = clock.GetPhysicsTick()
            };
            AddProjectile(combat, projectile);
            TimeScaleProtocolTests.SetTick(start + 1);
            clock.SetCombatModeImmediate();
            TimeScaleProtocolTests.SetTick(start + 2);
            if (clockFirst) clock.GameComponentTick();
            host.Space.AdvancePhysics(start + 2);
            if (!clockFirst) clock.GameComponentTick();
            Assert.Equal(10000030.5, ship.CurrentOrbit.positionSystem.v.x, 6);
            Assert.Equal(ship.CurrentOrbit.positionSystem.v.x, projectile.position.v.x, 6);
            Assert.Equal(1 + 1.0 / 60.0, projectile.totalFlightSeconds, 6);
            Assert.Equal(clock.GetPhysicsTick(), host.Space.LastPhysicsStepTick);
            Assert.Equal(10000030.25, host.Space.RenderedGroupPosition(ship, start + 1 + 1.0 / 120.0).x, 6);
        }, start);
    }

    [Theory]
    [InlineData(TimeScaleLevel.Normal, 10000030)]
    [InlineData(TimeScaleLevel.Combat, 10000000.5)]
    [InlineData(TimeScaleLevel.Fast, 10000240)]
    public void OutsideStepProjectileBirthUsesTheShipsStateAtItsOwnTimestamp(TimeScaleLevel level, double birthX)
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var ship = AddMovingShip(host);
            clock.SetTimeScale(level);
            TimeScaleProtocolTests.SetTick(101);
            var shot = ShotRuntimeTests.Ballistic();
            var weapon = new WeaponInstance { def = new WeaponDef { defName = "ClockGun", defaultShot = shot, allowedShots = new() { shot } } };
            var combat = host.Game.GetComponent<CombatManager>();
            combat.FireWeapon(ship.GroupId, weapon, new DVector3(20000000, 0, 0));
            var projectile = Assert.Single(combat.ActiveProjectiles);
            TimeScaleProtocolTests.SetTick(102);
            host.Space.AdvancePhysics(102);
            Assert.Equal(birthX, projectile.spawnPosition.v.x, 6);
            Assert.Equal(birthX + 1030.0 / 60.0, projectile.position.v.x, 6);
            Assert.Equal(1.0 / 60.0, projectile.totalFlightSeconds, 6);
        });
    }

    [Fact]
    public void BirthStateIsPublishedOnlyAfterItsTimestampAndSourceAreResolved()
    {
        var draw = AccessTools.Method(typeof(MainTabWindow_PFView), "DrawProjectiles");
        Assert.Contains(PatchProcessor.GetOriginalInstructions(draw),
            instruction => instruction.operand is MethodInfo method && method.Name == "IsVisibleAt");
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var source = AddMovingShip(host);
            TimeScaleProtocolTests.SetTick(101);
            var shot = ShotRuntimeTests.Ballistic();
            var weapon = new WeaponInstance { def = new WeaponDef { defName = "ClockGun", defaultShot = shot, allowedShots = new() { shot } } };
            var combat = host.Game.GetComponent<CombatManager>();
            combat.FireWeapon(source.GroupId, weapon, new DVector3(20000000, 0, 0));
            var projectile = Assert.Single(combat.ActiveProjectiles);
            Assert.False(projectile.IsVisibleAt(101));
            TimeScaleProtocolTests.SetTick(102);
            host.Space.AdvancePhysics(102);
            Assert.False(projectile.IsVisibleAt(100.999));
            Assert.True(projectile.IsVisibleAt(101));
            Assert.Equal(10000030, projectile.RenderedPosition(101).x, 6);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingBirthRecomputesAimFromTheCorrectedMuzzle(bool lead)
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var source = AddMovingShip(host);
            source.CurrentOrbit.velocitySystem = (SystemVec3)new DVector3(0, 30, 0);
            TimeScaleProtocolTests.SetTick(101);
            var shot = ShotRuntimeTests.Ballistic();
            var weapon = new WeaponInstance { def = new WeaponDef { defName = "ClockGun", defaultShot = shot, allowedShots = new() { shot } } };
            var combat = host.Game.GetComponent<CombatManager>();
            combat.FireWeapon(source.GroupId, weapon, new DVector3(10001000, 0, 0),
                targetVelocity: lead ? DVector3.zero : null);
            var projectile = Assert.Single(combat.ActiveProjectiles);
            TimeScaleProtocolTests.SetTick(102);
            host.Space.AdvancePhysics(102);
            var muzzle = projectile.spawnVelocity.v - new DVector3(0, 30, 0);
            Assert.Equal(1000, muzzle.magnitude, 7);
            if (lead)
            {
                double timeToTarget = 1000 / projectile.spawnVelocity.v.x;
                Assert.True(timeToTarget > 0);
                Assert.Equal(0, projectile.spawnPosition.v.y + projectile.spawnVelocity.v.y * timeToTarget, 7);
            }
            else
            {
                var expected = new DVector3(1000, -30, 0).normalized * 1000;
                Assert.Equal(expected.x, muzzle.x, 7);
                Assert.Equal(expected.y, muzzle.y, 7);
            }
        });
    }

    [Theory]
    [InlineData(TimeScaleLevel.Normal)]
    [InlineData(TimeScaleLevel.Fast)]
    public void MovingGravitySourceIsSampledAtTheStateEpoch(TimeScaleLevel level)
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var ship = AddMovingShip(host);
            var bodies = host.Game.GetComponent<CelestialBodyManager>();
            AccessTools.Property(typeof(CelestialBodyInfo), nameof(CelestialBodyInfo.ParentBodyId)).SetValue(bodies.GetBodyById(0), -1);
            var moon = new CelestialBodyInfo { bodyId = 1, bodyType = CelestialBodyType.Moon, ParentBodyId = 0, orbitRadius = 1000,
                orbitPeriod = 400, orbitNormal = UnityEngine.Vector3.up, gravitationalParameter = 1e8 };
            ((Dictionary<int, CelestialBodyInfo>)AccessTools.Field(typeof(CelestialBodyManager), "bodiesById").GetValue(bodies)).Add(1, moon);
            ship.CurrentOrbit.positionSystem = (SystemVec3)new DVector3(2000, 0, 0);
            var expectedPosition = ship.CurrentOrbit.positionSystem.v;
            var expectedVelocity = ship.CurrentOrbit.velocitySystem.v;
            int count = level == TimeScaleLevel.Fast ? 8 : 1;
            // 独立圆轨夹具：法线 +y，参考轴 +x/-z；每个 2 秒积分段按段首星历求力。
            for (int i = 0; i < count; i++)
            {
                double phase = (100 + 2 * i) * Math.PI / 200;
                var sources = new List<GravitySource>
                {
                    new(DVector3.zero, DVector3.zero, 1e-100, 0),
                    new(new DVector3(1000 * Math.Cos(phase), 0, -1000 * Math.Sin(phase)),
                        new DVector3(-5 * Math.PI * Math.Sin(phase), 0, -5 * Math.PI * Math.Cos(phase)), 1e8, 1)
                };
                NBodyIntegrator.FieldAccelJerk(expectedPosition, expectedVelocity, sources, out var acceleration, out var jerk);
                NBodyIntegrator.ThirdOrderTaylorStep(expectedPosition, expectedVelocity, acceleration, jerk, 2,
                    out expectedPosition, out expectedVelocity);
            }
            clock.SetTimeScale(level);
            TimeScaleProtocolTests.SetTick(102);
            host.Space.AdvancePhysics(102);
            Assert.Equal(expectedPosition.x, ship.CurrentOrbit.positionSystem.v.x, 7);
            Assert.Equal(expectedPosition.z, ship.CurrentOrbit.positionSystem.v.z, 7);
            Assert.Equal(expectedVelocity.x, ship.CurrentOrbit.velocitySystem.v.x, 7);
        });
    }

    [Fact]
    public void FractionalBodyStateUsesOneEpochForPositionAndVelocity()
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var ship = AddMovingShip(host);
            var bodies = host.Game.GetComponent<CelestialBodyManager>();
            var moon = new CelestialBodyInfo { bodyId = 1, bodyType = CelestialBodyType.Moon, ParentBodyId = 0, orbitRadius = 1000,
                orbitPeriod = 400, orbitNormal = UnityEngine.Vector3.up, gravitationalParameter = 1e8 };
            ((Dictionary<int, CelestialBodyInfo>)AccessTools.Field(typeof(CelestialBodyManager), "bodiesById").GetValue(bodies)).Add(1, moon);
            ship.CelestialBodyId = 1;
            double phase = 100.5 * Math.PI / 200;
            ship.CurrentOrbit.positionSystem = (SystemVec3)new DVector3(1000 * Math.Cos(phase) + 100, 0, -1000 * Math.Sin(phase));
            ship.CurrentOrbit.velocitySystem = (SystemVec3)new DVector3(-5 * Math.PI * Math.Sin(phase), 1, -5 * Math.PI * Math.Cos(phase));
            var (position, velocity) = ship.GetBodyState(100.5);
            Assert.Equal(100, position.v.x, 7);
            Assert.Equal(0, position.v.z, 7);
            Assert.Equal(0, velocity.v.x, 7);
            Assert.Equal(1, velocity.v.y, 7);
            Assert.Equal(0, velocity.v.z, 7);
        });
    }

    [Fact]
    public void ANewGroupCannotIntegrateTimeBeforeItsBirth()
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            AddMovingShip(host);
            TimeScaleProtocolTests.SetTick(101);
            var newborn = new SpaceEntityGroup(11, 2)
            {
                CelestialBodyId = 0,
                CurrentOrbit = new OrbitState
                {
                    positionSystem = (SystemVec3)new DVector3(20000000, 0, 0),
                    velocitySystem = (SystemVec3)new DVector3(40, 0, 0), rotation = DQuaternion.identity
                }
            };
            host.Space.RegisterGroup(newborn);
            TimeScaleProtocolTests.SetTick(102);
            host.Space.AdvancePhysics(102);
            Assert.Equal(20000040, newborn.CurrentOrbit.positionSystem.v.x, 6);
        });
    }

    [Fact]
    public void PendingMissileBirthBindsToTheSameSourceStateAsAProjectile()
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var source = AddMovingShip(host);
            TimeScaleProtocolTests.SetTick(101);
            var missile = new SpaceEntityGroup(11, 2)
            {
                CelestialBodyId = 0,
                CurrentOrbit = new OrbitState
                {
                    positionSystem = (SystemVec3)new DVector3(20000000, 0, 0),
                    velocitySystem = (SystemVec3)new DVector3(40, 0, 0), rotation = DQuaternion.identity
                }
            };
            host.Space.RegisterGroup(missile);
            host.Space.AnchorGroupBirth(missile, source, source.CurrentOrbit.positionSystem.v, source.CurrentOrbit.velocitySystem.v);
            Assert.False(host.Space.GroupIsVisibleAt(missile, 101));
            TimeScaleProtocolTests.SetTick(102);
            host.Space.AdvancePhysics(102);
            Assert.Equal(20000070, missile.CurrentOrbit.positionSystem.v.x, 6);
            Assert.Equal(-1, missile.physicsBirthSourceId);
            Assert.False(host.Space.GroupIsVisibleAt(missile, 100.999));
            Assert.True(host.Space.GroupIsVisibleAt(missile, 101));
        });
    }

    [Fact]
    public void ARequestInsideAPhysicsStepCannotChangeItsPublishedClock()
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            TimeScaleProtocolTests.SetTick(102);
            clock.BeginPhysicsBatch();
            Assert.True(clock.TryBeginPhysicsStep(102));
            double time = clock.GetPhysicsTick();
            clock.SetCombatModeImmediate();
            AddProjectile(host.Game.GetComponent<CombatManager>(), new ProjectileInstance());
            Assert.Equal(TimeScaleLevel.Normal, clock.CurrentLevel);
            Assert.Equal(2, clock.PhysicsStepSeconds);
            Assert.Equal(time, clock.GetPhysicsTick());
            clock.CompletePhysicsStep();
            clock.EndPhysicsBatch();
            Assert.Equal(TimeScaleLevel.Combat, clock.CurrentLevel);
            Assert.Equal(time, clock.GetPhysicsTick());
        });
    }

    private static SpaceEntityGroup AddMovingShip(SpaceAiRuntimeTestHost host)
    {
        // 显式给出近零引力参数，避免根天体 GM=0 触发生产的地球引力兜底；保留真实积分路径。
        var bodies = host.Game.GetComponent<CelestialBodyManager>();
        var table = (Dictionary<int, CelestialBodyInfo>)AccessTools.Field(typeof(CelestialBodyManager), "bodiesById").GetValue(bodies);
        table[0] = table[0] with { gravitationalParameter = 1e-100 };
        var ship = host.AddShip();
        ship.CurrentOrbit = new OrbitState
        {
            positionSystem = (SystemVec3)new DVector3(10000000, 0, 0),
            velocitySystem = (SystemVec3)new DVector3(30, 0, 0), rotation = DQuaternion.identity
        };
        return ship;
    }

    private static void AddProjectile(CombatManager combat, ProjectileInstance projectile)
        => ((List<ProjectileInstance>)AccessTools.Field(typeof(CombatManager), "activeProjectiles").GetValue(combat)).Add(projectile);
}
