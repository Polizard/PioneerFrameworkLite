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
    public void ManualTakeoverIsPrivateUntilShipAndClockPublishTogether()
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var runtime = ProductionRuntime(host);
            var manager = host.Game.GetComponent<CelestialBodyManager>();
            var ship = host.AddShip();
            ship.CurrentOrbit = new() { positionSystem = (SystemVec3)new DVector3(10000000, 0, 0), rotation = DQuaternion.identity };
            var native = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map)); native.uniqueID = 41;
            host.Space.RegisterMapComponent(41, new SpaceMapComp(native));
            host.Space.RegisterEntity(new SpaceEntity(30, 2) { GroupId = ship.GroupId, HasMap = true, MapId = 41 });
            ship.StartBurn(new DVector3(5, 0, 0));
            Assert.True(host.Space.SetHover(ship, true));
            SteeringController.Open(ship.GroupId);
            SteeringController.KeyYawL = true;
            SteeringController.SasKind = SasTargetKind.Prograde;
            SteeringController.ActualThrust = 7;
            Assert.True(runtime.Coordinator.AttachHostParticipants(new[] { clock.CreateCelestialClockParticipant(), host.Space.CreateCelestialShipParticipant(clock, manager) }).Success);
            clock.BeginPhysicsBatch();
            try
            {
                Assert.True(clock.TryBeginPhysicsStep(102));
                var step = runtime.Coordinator.Prepare(new(runtime.WorldSessionId, 1, 100, 102));
                Assert.True(step.Success, string.Join(",", step.Errors));
                Assert.True(ship.HasActiveBurn);
                Assert.True(ship.hoverActive);
                Assert.Equal(SasTargetKind.Prograde, SteeringController.SasKind);
                Assert.Equal(7, SteeringController.ActualThrust);
                Assert.Equal(DVector3.zero, ship.angularVelocity);
                runtime.Coordinator.Abort(step.Value);
                Assert.True(ship.HasActiveBurn);
                Assert.True(clock.TryBeginPhysicsStep(102));
                step = runtime.Coordinator.Prepare(new(runtime.WorldSessionId, 1, 100, 102));
                Assert.True(step.Success, string.Join(",", step.Errors));
                runtime.Coordinator.Publish(step.Value);
                Assert.False(ship.HasActiveBurn);
                Assert.False(ship.hoverActive);
                Assert.Equal(SasTargetKind.None, SteeringController.SasKind);
                Assert.Equal(0, SteeringController.ActualThrust);
                Assert.True(ship.angularVelocity.sqrMagnitude > 0);
                Assert.Equal(102, clock.CommittedPhysicsTick);
            }
            finally { SteeringController.Close(); clock.EndPhysicsBatch(); }
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

    [Fact]
    public void PreparedMappedBurnPreservesTankPriorityClosedFuelGroupAndNativeObjectIdentity()
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var runtime = ProductionRuntime(host);
            var manager = host.Game.GetComponent<CelestialBodyManager>();
            var ship = host.AddShip();
            ship.CurrentOrbit = new() { positionSystem = (SystemVec3)new DVector3(10000000, 0, 0), rotation = DQuaternion.identity };
            var fuel = new PropellantDef { defName = "PF_TestPreparedTankFuel" };
            SpacePart Tank(int id, int group, byte priority) => new() {
                partId = id, linkedMapId = 41, fuelGroupId = group, fuelPriority = priority, boundsSizeShip = new(2, 2, 2),
                components = new() { new PropellantTankComponentData { propellantDefName = fuel.defName, massKg = 100, capacityKg = 100 } } };
            var low = Tank(1, 0, 1); var high = Tank(2, 0, 2); var closed = Tank(3, 1, 0);
            var entity = new SpaceEntity(30, 2) { GroupId = ship.GroupId, HasMap = true, MapId = 41 };
            entity.SetParts(new() { low, high, closed });
            host.Space.RegisterEntity(entity);
            var nativeMap = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map)); nativeMap.uniqueID = 41;
            var map = new SpaceMapComp(nativeMap) { ownerEntityId = entity.EntityId };
            host.Space.RegisterMapComponent(41, map);
            map.RegisterEngine(fuel, 1000, 1, ThrustAxis.Forward, isRcs: true);
            map.AddPropellantMass(fuel, 200);
            var lowTank = low.GetComponent<PropellantTankComponentData>()!;
            var highTank = high.GetComponent<PropellantTankComponentData>()!;
            var closedTank = closed.GetComponent<PropellantTankComponentData>()!;
            lowTank.massKg = 50; highTank.massKg = 100; closedTank.massKg = 50;
            entity.InvalidateFuelPlans();
            ship.StartBurn(new DVector3(5, 0, 0));
            Assert.True(runtime.Coordinator.AttachHostParticipants(new[] { clock.CreateCelestialClockParticipant(), host.Space.CreateCelestialShipParticipant(clock, manager) }).Success);
            clock.BeginPhysicsBatch();
            try
            {
                Assert.True(clock.TryBeginPhysicsStep(102));
                var prepared = runtime.Coordinator.Prepare(new(runtime.WorldSessionId, runtime.CurrentStamp.TopologyRevision, 100, 102));
                Assert.True(prepared.Success, string.Join(",", prepared.Errors));
                Assert.Equal(200, map.TotalPropellantMass);
                Assert.Equal(50, lowTank.massKg);
                runtime.Coordinator.Abort(prepared.Value);
                Assert.True(clock.TryBeginPhysicsStep(102));
                prepared = runtime.Coordinator.Prepare(new(runtime.WorldSessionId, runtime.CurrentStamp.TopologyRevision, 100, 102));
                Assert.True(prepared.Success, string.Join(",", prepared.Errors));
                runtime.Coordinator.Publish(prepared.Value);
                Assert.True(map.TotalPropellantMass < 200);
                Assert.Equal(200 - map.TotalPropellantMass, 50 - lowTank.massKg, 10);
                Assert.Equal(100, highTank.massKg);
                Assert.Equal(50, closedTank.massKg);
                Assert.Same(low, entity.Parts[0]);
                Assert.Same(lowTank, entity.Parts[0].components[0]);
                Assert.Same(map, host.Space.GetMapComponent<SpaceMapComp>(41));
            }
            finally { clock.EndPhysicsBatch(); }
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

    [Fact]
    public void LaterParticipantFailureLeavesRealProductionHostUnchangedAndTheSameIntervalCanRetry()
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var runtime = ProductionRuntime(host);
            var ship = host.AddShip();
            ship.CurrentOrbit = new() { positionSystem = (SystemVec3)new DVector3(10000000, 0, 0),
                velocitySystem = (SystemVec3)new DVector3(30, 0, 0), rotation = DQuaternion.identity };
            var fuel = new PropellantDef { defName = "PF_TestPreparedFuel" };
            var entity = new SpaceEntity(30, 2) { GroupId = ship.GroupId };
            entity.AddPropulsion(new PropulsionData { propellant = fuel, ve = 1000, massFlow = 1, mass = 100, capacity = 100, isVectoredRcs = true });
            host.Space.RegisterEntity(entity);
            ship.GetEntityIds().Add(entity.EntityId);
            ship.StartBurn(new DVector3(5, 0, 0));
            var fail = new LateFailure();
            Assert.True(runtime.Coordinator.AttachHostParticipants(new[] { fail }).Success);
            var before = ship.CurrentOrbit;
            var stamp = runtime.CurrentStamp;
            TimeScaleProtocolTests.SetTick(102);
            host.Space.AdvancePhysics(102);
            Assert.Equal(before, ship.CurrentOrbit);
            Assert.Equal(100, entity.TotalPropellantMass);
            Assert.Equal(stamp, runtime.CurrentStamp);
            Assert.Equal(100, clock.CommittedPhysicsTick);
            Assert.Null(clock.CurrentPhysicsStep);
            fail.Fail = false;
            host.Space.AdvancePhysics(102);
            Assert.True(entity.TotalPropellantMass < 100);
            Assert.NotEqual(before, ship.CurrentOrbit);
            Assert.Equal(102, runtime.CurrentStamp.PhysicalSeconds);
            double remaining = entity.TotalPropellantMass;
            var orbit = ship.CurrentOrbit;
            host.Space.AdvancePhysics(102);
            Assert.Equal(remaining, entity.TotalPropellantMass);
            Assert.Equal(orbit, ship.CurrentOrbit);
        });
    }

    [Fact]
    public void RealShipPreparationAbortKeepsOrbitFuelAndClockAndRetryPublishesOnce()
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var manager = host.Game.GetComponent<CelestialBodyManager>();
            var ship = host.AddShip();
            ship.CurrentOrbit = new() { positionSystem = (SystemVec3)new DVector3(10000000, 0, 0), velocitySystem = (SystemVec3)new DVector3(30, 0, 0), rotation = DQuaternion.identity };
            var fuel = new PropellantDef { defName = "PF_TestPreparedFuel" };
            var entity = new SpaceEntity(30, 2) { GroupId = ship.GroupId, IsAffectedByGravity = false };
            entity.AddPropulsion(new PropulsionData { propellant = fuel, ve = 1000, massFlow = 1, mass = 100, capacity = 100, isVectoredRcs = true });
            host.Space.RegisterEntity(entity);
            ship.GetEntityIds().Add(entity.EntityId);
            ship.StartBurn(new DVector3(5, 0, 0));
            var provider = new ExistingCelestialEphemerisBridge(Guid.NewGuid(), 100);
            var coordinator = new CelestialPhysicsCoordinator(provider, provider.CommittedStamp, new object(),
                new[] { clock.CreateCelestialClockParticipant(), host.Space.CreateCelestialShipParticipant(clock, manager) });
            var before = ship.CurrentOrbit;
            clock.BeginPhysicsBatch();
            try
            {
                Assert.True(clock.TryBeginPhysicsStep(102));
                var first = coordinator.Prepare(new(provider.SessionId, 0, 100, 102));
                Assert.True(first.Success, string.Join(",", first.Errors));
                Assert.Equal(before, ship.CurrentOrbit);
                Assert.Equal(100, entity.TotalPropellantMass);
                Assert.Equal(100, clock.CommittedPhysicsTick);
                coordinator.Abort(first.Value);
                Assert.Equal(before, ship.CurrentOrbit);
                Assert.Equal(100, entity.TotalPropellantMass);
                Assert.True(clock.TryBeginPhysicsStep(102));
                var retry = coordinator.Prepare(new(provider.SessionId, 0, 100, 102));
                Assert.True(retry.Success, string.Join(",", retry.Errors));
                coordinator.Publish(retry.Value);
                double afterFuel = entity.TotalPropellantMass;
                var afterOrbit = ship.CurrentOrbit;
                Assert.True(afterFuel < 100);
                Assert.NotEqual(before.positionSystem, afterOrbit.positionSystem);
                Assert.Equal(102, clock.CommittedPhysicsTick);
                Assert.Equal(102, provider.CommittedStamp.PhysicalSeconds);
                coordinator.Publish(retry.Value);
                Assert.Equal(afterFuel, entity.TotalPropellantMass);
                Assert.Equal(afterOrbit, ship.CurrentOrbit);
            }
            finally { clock.EndPhysicsBatch(); }
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
    private sealed class LateFailure : ICelestialStepParticipant
    {
        public string Id => "zz.RejectAfterRealShipPreparation";
        public CelestialStepParticipantPhase Phase => CelestialStepParticipantPhase.State;
        public bool Fail = true;
        public CelestialResult<ICelestialPreparedStepPart> Prepare(CelestialPreparedStepContext context)
            => Fail ? CelestialResult.Reject<ICelestialPreparedStepPart>(new CelestialError("AcceptanceNotReady"))
                : CelestialResult.Ok<ICelestialPreparedStepPart>(new Part());
        private sealed class Part : ICelestialPreparedStepPart { public void Commit() { } public void Abort() { } }
    }
}
