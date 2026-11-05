using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using PioneerFramework.Components;
using PioneerFramework.Celestial;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialRealPhysicsAcceptanceTests
{
    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(40)]
    public void ProductionBurnsPublishAcrossTheRealParallelThreshold(int count)
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var runtime = ProductionRuntime(host);
            var fuel = new PropellantDef { defName = "PF_TestParallelCombatFuel" };
            var ships = new List<SpaceEntityGroup>();
            var entities = new List<SpaceEntity>();
            for (int index = 0; index < count; index++)
            {
                var ship = new SpaceEntityGroup(100 + index, 2) { CelestialBodyId = 1, IsActive = true,
                    IsMovable = true, exemptPlanetCollision = true,
                    CurrentOrbit = new() { positionSystem = (SystemVec3)new DVector3(10000000 + index * 1000, 0, 0),
                        velocitySystem = (SystemVec3)new DVector3(0, 0, 100), rotation = DQuaternion.identity } };
                host.Space.RegisterGroup(ship);
                var entity = new SpaceEntity(1000 + index, 2) { GroupId = ship.GroupId, IsAffectedByGravity = false };
                entity.AddPropulsion(new PropulsionData { propellant = fuel, ve = 1000, massFlow = 1,
                    mass = 100, capacity = 100, isVectoredRcs = true });
                host.Space.RegisterEntity(entity);
                ship.GetEntityIds().Add(entity.EntityId);
                ship.StartBurn(new DVector3(5, 0, 0));
                ships.Add(ship); entities.Add(entity);
            }
            TimeScaleProtocolTests.SetTick(102);
            host.Space.AdvancePhysics(102);
            Assert.Empty(host.Space.CelestialPhysicsErrors);
            Assert.Equal(102, runtime.CurrentStamp.PhysicalSeconds);
            Assert.Equal(102, clock.CommittedPhysicsTick);
            Assert.False(SpaceGameComp.ParallelPhysicsInProgress);
            for (int index = 0; index < count; index++)
            {
                Assert.Same(ships[index], host.Space.GetGroupById(100 + index));
                Assert.True(entities[index].TotalPropellantMass < 100);
                Assert.True(entities[index].TotalPropellantMass > 0);
                Assert.NotEqual(0, ships[index].CurrentOrbit.positionSystem.v.z);
            }
        });
    }

    [Theory]
    [InlineData("orbit")]
    [InlineData("body-view")]
    [InlineData("forecast")]
    [InlineData("expired-history")]
    [InlineData("future-authority")]
    public void PreparedBurnWorkerReadsNeverAcquireTheWaitingHostPublicationLock(string query)
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var runtime = ProductionRuntime(host);
            var manager = host.Game.GetComponent<CelestialBodyManager>();
            var ship = host.AddShip();
            ship.CelestialBodyId = 1;
            ship.CurrentOrbit = new() { positionSystem = (SystemVec3)new DVector3(10000000, 0, 0),
                velocitySystem = (SystemVec3)new DVector3(0, 0, 100), rotation = DQuaternion.identity };
            var stamp = runtime.CurrentStamp;
            var prepared = runtime.Coordinator.Prepare(new(runtime.WorldSessionId, stamp.TopologyRevision, 100, 102)).Value;
            using var scope = manager.UseCelestialInterval(prepared.Sampler);
            System.Threading.Tasks.Task<bool> worker = null;
            try
            {
                runtime.WithPublication(() => {
                    SpaceGameComp.ParallelPhysicsInProgress = true;
                    try
                    {
                        worker = System.Threading.Tasks.Task.Run(() => {
                            switch (query)
                            {
                                case "orbit": return OrbitAdapter.TryToOrbitView(ship, manager, 101, out _);
                                case "body-view": return OrbitAdapter.ToBodyView(manager.GetBodyById(1), manager).bodyId == 1;
                                case "forecast":
                                    var result = manager.ForecastBody(1, 10000);
                                    return result.Success && result.Value.BasedOn == stamp;
                                default:
                                    var failure = Assert.Throws<InvalidOperationException>(() => manager.GetBodyMotion(1, query == "expired-history" ? 99 : 103));
                                    return failure.Message.Contains("TimeUnavailable", StringComparison.Ordinal);
                            }
                        });
                        Assert.True(worker.Wait(TimeSpan.FromSeconds(2)), query + " waited for the host publication lock.");
                        Assert.True(worker.Result);
                    }
                    finally { SpaceGameComp.ParallelPhysicsInProgress = false; }
                    return true;
                });
            }
            finally
            {
                worker?.Wait(TimeSpan.FromSeconds(3));
                runtime.Coordinator.Abort(prepared);
            }
            Assert.Equal(stamp, runtime.CurrentStamp);
        });
    }

    [Fact]
    public void ProductionClockQueriesStayAtCommittedTimeWhilePlanningCanExplicitlyForecast()
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var runtime = ProductionRuntime(host);
            var manager = host.Game.GetComponent<CelestialBodyManager>();
            TimeScaleProtocolTests.SetTick(101);
            Assert.Equal(100, clock.GetPhysicsTick());
            Assert.Equal(101, clock.GetPhysicsTick(101));
            var future = manager.ForecastBody(1, 10000);
            Assert.True(future.Success);
            Assert.Equal(runtime.CurrentStamp, future.Value.BasedOn);
            Assert.Throws<InvalidOperationException>(() => manager.GetBodyMotion(1, 10000));
            var ship = host.AddShip(); ship.CelestialBodyId = 1;
            ship.CurrentOrbit = new() { positionSystem = (SystemVec3)new DVector3(2000, 0, 0),
                velocitySystem = (SystemVec3)new DVector3(30, 0, 0), rotation = DQuaternion.identity };
            Assert.True(OrbitAdapter.TryToOrbitView(ship, manager, 10000, out var planned));
            var local = Circular(1000, 400, 0, 10000);
            var state = planned.GameFrameStateAtUT(10000);
            Assert.Equal(ship.CurrentOrbit.positionSystem.v - local.Position, state.pos);
            Assert.Equal(ship.CurrentOrbit.velocitySystem.v - local.Velocity, state.vel);
            Assert.Equal(100, runtime.CurrentStamp.PhysicalSeconds);
            host.Space.AdvancePhysics(101);
            Assert.Equal(101, clock.GetPhysicsTick());
            Assert.Equal(101, runtime.CurrentStamp.PhysicalSeconds);
        });
    }

    [Theory]
    [InlineData(TimeSpeed.Normal)]
    [InlineData(TimeSpeed.Paused)]
    public void RenderFocusNeverSamplesAheadOfTheSharedCommittedBoundary(TimeSpeed speed)
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var runtime = ProductionRuntime(host);
            var manager = host.Game.GetComponent<CelestialBodyManager>();
            TimeScaleProtocolTests.SetTick(101);
            HarmonyLib.AccessTools.Field(typeof(TickManager), "curTimeSpeed").SetValue(Find.TickManager, speed);
            for (int frame = 0; frame < 8; frame++)
            {
                RealTime.frameCount++;
                Assert.Equal(100, clock.GetRenderTick());
                Assert.Equal(manager.GetBodyPositionAbs(1, 100), manager.GetBodyPositionAbs(1, clock.GetRenderTick()));
            }
            Assert.Equal(100, runtime.CurrentStamp.PhysicalSeconds);
        });
    }

    [Fact]
    public void AttachingProviderInvalidatesCachedRenderTimeBeforeItsFirstAvailableHistory()
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            TimeScaleProtocolTests.SetTick(101);
            clock.GetRenderTick();
            RealTime.frameCount++;
            Assert.True(clock.GetRenderTick() > 100);
            var runtime = ProductionRuntime(host);
            Assert.Equal(100, clock.GetRenderTick());
            var manager = host.Game.GetComponent<CelestialBodyManager>();
            Assert.Equal(manager.GetBodyPositionAbs(1, 100), manager.GetBodyPositionAbs(1, clock.GetRenderTick()));
            host.Space.AdvancePhysics(101);
            RealTime.frameCount++;
            double interpolated = clock.GetRenderTick();
            Assert.InRange(interpolated, 100, 101);
            Assert.Equal(manager.GetBodyPositionAbs(1, interpolated), manager.GetBodyPositionAbs(1, clock.GetRenderTick()));
            Assert.Equal(101, runtime.CurrentStamp.PhysicalSeconds);
        });
    }

    [Fact]
    public void TopologyPublicationInvalidatesEarlierPresentationWithinTheSameFrame()
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var runtime = ProductionRuntime(host);
            var manager = host.Game.GetComponent<CelestialBodyManager>();
            Assert.Equal(100, clock.GetRenderTick());
            TimeScaleProtocolTests.SetTick(102);
            host.Space.AdvancePhysics(102);
            RealTime.frameCount++;
            Assert.InRange(clock.GetRenderTick(), 100, 101);
            var removed = runtime.Remove(new(new("remove-rendered-star", runtime.WorldSessionId, runtime.CaptureCatalog().Revision),
                CelestialInstanceId.Parse("c-star")));
            Assert.Equal(CelestialOperationStatus.Pending, removed.Status);
            Assert.True(runtime.Advance(new(runtime.WorldSessionId, runtime.CurrentStamp.TopologyRevision, 102, 102)).Success);
            Assert.Equal(102, clock.GetRenderTick());
            Assert.Equal(manager.GetBodyPositionAbs(1, 102), manager.GetBodyPositionAbs(1, clock.GetRenderTick()));
            Assert.Equal(CelestialOperationStatus.Applied, runtime.GetOperation("remove-rendered-star").Status);
        });
    }

    [Theory]
    [InlineData(TimeScaleLevel.Normal)]
    [InlineData(TimeScaleLevel.Fast)]
    public void ProductionHostSamplesBothMovingStarsAtEveryRealIntegratorSubstep(TimeScaleLevel level)
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var runtime = ProductionRuntime(host);
            var ship = host.AddShip();
            ship.CurrentOrbit = new() { positionSystem = (SystemVec3)new DVector3(2000, 0, 0),
                velocitySystem = (SystemVec3)new DVector3(30, 0, 0), rotation = DQuaternion.identity };
            var position = ship.CurrentOrbit.positionSystem.v;
            var velocity = ship.CurrentOrbit.velocitySystem.v;
            int count = level == TimeScaleLevel.Fast ? 8 : 1;
            for (int i = 0; i < count; i++)
            {
                double t = 100 + 2 * i;
                var first = Circular(1000, 400, 0, t);
                var second = Circular(1400, 530, 0.7, t);
                var sources = new List<GravitySource> { new(DVector3.zero, DVector3.zero, 0, 0),
                    new(first.Position, first.Velocity, 1e8, 1), new(second.Position, second.Velocity, 7e7, 2) };
                NBodyIntegrator.FieldAccelJerk(position, velocity, sources, out var acceleration, out var jerk);
                NBodyIntegrator.ThirdOrderTaylorStep(position, velocity, acceleration, jerk, 2, out position, out velocity);
            }
            clock.SetTimeScale(level);
            TimeScaleProtocolTests.SetTick(102);
            host.Space.AdvancePhysics(102);
            Assert.Equal(position, ship.CurrentOrbit.positionSystem.v);
            Assert.Equal(velocity, ship.CurrentOrbit.velocitySystem.v);
            Assert.Equal(100 + 2 * count, runtime.CurrentStamp.PhysicalSeconds);
            Assert.Equal(runtime.CurrentStamp.PhysicalSeconds, clock.CommittedPhysicsTick);
            Assert.Equal(clock.CommittedPhysicsTick, host.Space.LastPhysicsStepTick);
            Assert.Same(ship, Assert.Single(host.Space.GetPlanetGrids()[0].GetNearby(position, 1)));
        });
    }

    private static CelestialRuntime ProductionRuntime(SpaceAiRuntimeTestHost host)
    {
        var root = CelestialInstanceId.Parse("a-root");
        CelestialSpawnItem Body(string key, double radius, double period, double phase, double gm, CelestialInstanceId? parent)
        {
            var id = CelestialInstanceId.Parse(key);
            var registration = CelestialExistingEphemerisTests.Registration(id, 0, parent, radius, period, phase, type: "Star");
            var template = new CelestialBodyDef { defName = key, comps = new() {
                new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { value = 10 } },
                new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = "PFL.ExistingEphemeris", modeId = "existing.circular.v1",
                    gravitationalParameter = new() { value = gm }, initializationPayload = Convert.ToBase64String(registration.Initialization.Payload.Span) } } }.ToTemplate().Value;
            return new(id, key, 0, key, template, template.Resolve(new(1, key)).Value, parent,
                new Dictionary<string, CelestialInstanceId> { ["frameOrigin"] = root });
        }
        var items = new[] { Body(root.Value, 0, 0, 0, 0, null), Body("b-star", 1000, 400, 0, 1e8, root), Body("c-star", 1400, 530, 0.7, 7e7, root) };
        var provider = new ExistingCelestialEphemerisBridge(Guid.NewGuid(), 100);
        var runtime = new CelestialRuntime(provider, provider.CommittedStamp, new EmptyResources(), 1, _ => throw new InvalidOperationException());
        Assert.Equal(CelestialOperationStatus.Pending, runtime.CreateBatch(new(new("start", runtime.WorldSessionId, 0), new("test", 1, null, items))).Status);
        Assert.True(runtime.Advance(new(runtime.WorldSessionId, 0, 100, 100)).Success);
        Assert.True(host.Game.GetComponent<CelestialBodyManager>().AttachCelestialRuntime(runtime).Success);
        return runtime;
    }
    private static (DVector3 Position, DVector3 Velocity) Circular(double radius, double period, double phase0, double t)
    {
        double omega = 2.0 * Math.PI / period, phase = phase0 + omega * t;
        double cos = Math.Cos(phase), sin = Math.Sin(phase);
        var x = new DVector3(1, 0, 0); var y = new DVector3(0, 0, -1);
        return (radius * (cos * x + sin * y), (radius * omega) * (-sin * x + cos * y));
    }
    private sealed class EmptyResources : ICelestialRemovalResources
    {
        public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id) => Array.Empty<CelestialError>();
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id) => CelestialResult.Ok(true);
    }
}
