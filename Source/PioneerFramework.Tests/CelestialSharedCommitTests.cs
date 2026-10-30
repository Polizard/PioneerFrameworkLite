using System;
using System.Collections.Generic;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialSharedCommitTests
{
    [Fact]
    public void ParticipantPreparationFailureKeepsProviderShipDescriptorAndRealClockAtOldBoundary()
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var bridge = Provider(100);
            var ship = new StagedValueParticipant("ships");
            var description = new StagedValueParticipant("descriptions") { FailPreparation = true };
            var coordinator = new CelestialPhysicsCoordinator(bridge, bridge.CommittedStamp, new object(),
                new ICelestialStepParticipant[] { ship, description, clock.CreateCelestialClockParticipant() });
            clock.BeginPhysicsBatch();
            try
            {
                Assert.True(clock.TryBeginPhysicsStep(102));
                var result = coordinator.Prepare(new PhysicsIntervalRequest(bridge.SessionId, 0, 100, 102));
                Assert.False(result.Success);
                Assert.Equal(100, bridge.CommittedStamp.PhysicalSeconds);
                Assert.Equal(100, clock.CommittedPhysicsTick);
                Assert.Equal(0, ship.CommittedValue);
                Assert.Equal(0, description.CommittedValue);
                Assert.True(clock.TryBeginPhysicsStep(102));
                clock.CancelCelestialPhysicsStep();
            }
            finally { clock.EndPhysicsBatch(); }
        });
    }

    [Fact]
    public void SuccessfulPublicationCommitsPreparedValuesAndClockOnce()
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var bridge = Provider(100);
            var ship = new StagedValueParticipant("ships");
            var description = new StagedValueParticipant("descriptions");
            var coordinator = new CelestialPhysicsCoordinator(bridge, bridge.CommittedStamp, new object(),
                new ICelestialStepParticipant[] { ship, description, clock.CreateCelestialClockParticipant() });
            clock.BeginPhysicsBatch();
            try
            {
                Assert.True(clock.TryBeginPhysicsStep(102));
                var prepared = coordinator.Prepare(new PhysicsIntervalRequest(bridge.SessionId, 0, 100, 102)).Value;
                Assert.Equal(0, ship.CommittedValue);
                coordinator.Publish(prepared);
                coordinator.Publish(prepared);
                Assert.Equal(102, bridge.CommittedStamp.PhysicalSeconds);
                Assert.Equal(102, clock.CommittedPhysicsTick);
                Assert.Equal(102, ship.CommittedValue);
                Assert.Equal(102, description.CommittedValue);
                Assert.Equal(1, ship.CommitCount);
                Assert.Equal(bridge.CommittedStamp, coordinator.CommittedStamp);
            }
            finally { clock.EndPhysicsBatch(); }
        });
    }

    [Fact]
    public void PausedTopologyPublicationDoesNotCommitOrAdvanceTheClock()
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var bridge = Provider(100);
            var id = CelestialInstanceId.Parse("paused-body");
            var descriptor = new CelestialPhysicsDescriptor(id, 1, 30);
            var registration = new CelestialBodyRegistration("PF_Test", descriptor, new PhysicsInitialization(bridge.ProviderId, "replay", 1, ReadOnlySpan<byte>.Empty));
            bridge.SetFrame(100, new Dictionary<CelestialInstanceId, CelestialMotionState> { [id] = new(1, DVector3.zero, DVector3.zero, DQuaternion.identity, DVector3.zero) });
            bridge.Submit(new PhysicsChangeSet("paused", bridge.SessionId, 0, new[] { PhysicsBodyChange.Register(registration) }));
            var coordinator = new CelestialPhysicsCoordinator(bridge, bridge.CommittedStamp, new object(),
                new[] { clock.CreateCelestialClockParticipant() });
            var prepared = coordinator.Prepare(new PhysicsIntervalRequest(bridge.SessionId, 0, 100, 100, new[] { "paused" })).Value;
            coordinator.Publish(prepared);
            Assert.Equal(100, clock.CommittedPhysicsTick);
            Assert.Equal(100, bridge.CommittedStamp.PhysicalSeconds);
            Assert.Equal(1, bridge.CommittedStamp.TopologyRevision);
            Assert.Null(clock.CurrentPhysicsStep);
        });
    }

    [Fact]
    public void CommitContractViolationStopsSubsequentAdvancement()
    {
        var bridge = Provider(0);
        var broken = new StagedValueParticipant("broken") { ThrowOnCommit = true };
        var coordinator = new CelestialPhysicsCoordinator(bridge, bridge.CommittedStamp, new object(), new[] { broken });
        var step = coordinator.Prepare(new PhysicsIntervalRequest(bridge.SessionId, 0, 0, 1)).Value;
        Assert.Throws<InvalidOperationException>(() => coordinator.Publish(step));
        Assert.True(coordinator.IsFaulted);
        Assert.Equal("ConsistencyFault", Assert.Single(coordinator.Prepare(new PhysicsIntervalRequest(bridge.SessionId, 0, 1, 2)).Errors).Code);
    }

    [Fact]
    public void HostAdapterCancelsSelectedClockIntervalWhenProviderIsNotReady()
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var bridge = Provider(100);
            bridge.IsAvailable = false;
            var runtime = new CelestialRuntime(bridge, bridge.CommittedStamp, new Resources(), 1,
                _ => CelestialResult.Reject<CelestialBodyTemplate>(new CelestialError("DefinitionMissing")), new[] { clock.CreateCelestialClockParticipant() });
            clock.BeginPhysicsBatch();
            try
            {
                Assert.True(clock.TryBeginPhysicsStep(102));
                Assert.False(host.Space.AdvanceCelestialPhysicsInterval(runtime, clock).Success);
                Assert.Equal(100, clock.CommittedPhysicsTick);
                Assert.True(clock.TryBeginPhysicsStep(102));
                clock.CancelCelestialPhysicsStep();
            }
            finally { clock.EndPhysicsBatch(); }
        });
    }

    [Fact]
    public void AbortingSharedPreparationIsIdempotentAndKeepsTheClockReusable()
    {
        TimeScaleProtocolTests.WithClock((host, clock) =>
        {
            var bridge = Provider(100);
            var participant = new StagedValueParticipant("ships");
            var coordinator = new CelestialPhysicsCoordinator(bridge, bridge.CommittedStamp, new object(),
                new[] { clock.CreateCelestialClockParticipant(), participant });
            clock.BeginPhysicsBatch();
            try
            {
                Assert.True(clock.TryBeginPhysicsStep(102));
                var step = coordinator.Prepare(new PhysicsIntervalRequest(bridge.SessionId, 0, 100, 102)).Value;
                coordinator.Abort(step);
                coordinator.Abort(step);
                Assert.Equal(100, bridge.CommittedStamp.PhysicalSeconds);
                Assert.Equal(100, clock.CommittedPhysicsTick);
                Assert.Equal(0, participant.CommitCount);
                Assert.True(clock.TryBeginPhysicsStep(102));
                clock.CancelCelestialPhysicsStep();
            }
            finally { clock.EndPhysicsBatch(); }
        });
    }

    private sealed class Resources : ICelestialRemovalResources
    {
        public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id) => Array.Empty<CelestialError>();
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id) => CelestialResult.Ok(true);
    }

    private static DeterministicTestPhysicsBridge Provider(double start)
    {
        var result = new DeterministicTestPhysicsBridge(Guid.NewGuid(), start);
        foreach (double seconds in new[] { start, start + 0.5, start + 1, start + 2 })
            result.SetFrame(seconds, new Dictionary<CelestialInstanceId, CelestialMotionState>());
        return result;
    }
    private sealed class StagedValueParticipant(string id) : ICelestialStepParticipant
    {
        public string Id => id;
        public CelestialStepParticipantPhase Phase => CelestialStepParticipantPhase.State;
        public bool FailPreparation { get; set; }
        public bool ThrowOnCommit { get; set; }
        public double CommittedValue { get; private set; }
        public int CommitCount { get; private set; }
        public CelestialResult<ICelestialPreparedStepPart> Prepare(CelestialPreparedStepContext context)
            => FailPreparation ? CelestialResult.Reject<ICelestialPreparedStepPart>(new CelestialError("FixtureNotReady")) :
                CelestialResult.Ok<ICelestialPreparedStepPart>(new Part(this, context.Request.EndSeconds));
        private sealed class Part(StagedValueParticipant owner, double preparedValue) : ICelestialPreparedStepPart
        {
            public void Commit()
            {
                if (owner.ThrowOnCommit) throw new InvalidOperationException("fixture violates commit contract");
                owner.CommittedValue = preparedValue;
                owner.CommitCount++;
            }
            public void Abort() { }
        }
    }
}
