using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialRuntimeTests
{
    [Fact]
    public void ManagerOwnsOneOrdinaryRuntimeAndQueriesDoNotAdvanceComponentsOrDrainCommands()
    {
        var fixture = new Fixture();
        var manager = new CelestialBodyManager(null);
        Assert.True(manager.AttachCelestialRuntime(fixture.Runtime).Success);
        Assert.True(manager.AttachCelestialRuntime(fixture.Runtime).Success);
        Assert.True(manager.TryGetCelestialRuntime(out var owned));
        Assert.Same(fixture.Runtime, owned);
        Assert.False(typeof(GameComponent).IsAssignableFrom(typeof(CelestialRuntime)));
        var id = fixture.CreateBody();
        Assert.True(fixture.Runtime.Advance(new PhysicsIntervalRequest(fixture.Bridge.SessionId, 0, 0, 0)).Success);
        fixture.Runtime.Submit(new CelestialCommand("fixture", "command", id, fixture.Bridge.SessionId, fixture.Runtime.Catalog.Revision));
        int before = fixture.Counter.AdvanceCount;
        for (int index = 0; index < 5; index++)
        {
            Assert.True(fixture.Runtime.Query(id).Success);
            fixture.Runtime.GetOperation("create");
            using var snapshot = fixture.Runtime.Sample(new PhysicsSampleRequest(fixture.Bridge.SessionId, 1, new[] { id })).Snapshot;
        }
        Assert.Equal(before, fixture.Counter.AdvanceCount);
        Assert.Equal(0, fixture.Handler.Count);
        Assert.Equal(1, fixture.Runtime.Commands.PendingCount);
    }

    [Fact]
    public void SchedulingUsesCommittedPhysicalIntervalsAndEventsAreExplicit()
    {
        var fixture = new Fixture();
        var id = fixture.CreateBody();
        fixture.Advance(0, 0);
        fixture.Advance(0, 0.5);
        Assert.Equal(0, fixture.Counter.AdvanceCount);
        fixture.Advance(0.5, 1);
        Assert.Equal(1, fixture.Counter.AdvanceCount);
        Assert.Equal(1, fixture.Counter.TotalSeconds);
        fixture.Runtime.EnqueueEvent(fixture.Bridge.SessionId, id, new CelestialEvent("changed"));
        Assert.Equal(0, fixture.Counter.EventCount);
        fixture.Advance(1, 2);
        Assert.Equal(2, fixture.Counter.AdvanceCount);
        Assert.Equal(1, fixture.Counter.EventCount);
        fixture.Runtime.Submit(new CelestialCommand("fixture", "command", id, fixture.Bridge.SessionId, fixture.Runtime.Catalog.Revision));
        fixture.Advance(2, 2);
        Assert.Equal(1, fixture.Handler.Count);
        Assert.Equal(CelestialOperationStatus.Applied, fixture.Runtime.GetOperation("command").Status);
    }

    [Theory]
    [InlineData(CelestialUpdateMode.None, 0)]
    [InlineData(CelestialUpdateMode.EventsOnly, 1)]
    public void InactiveAndEventOnlyComponentsNeverReceiveScheduledAdvancement(CelestialUpdateMode mode, int expectedEvents)
    {
        var fixture = new Fixture(mode == CelestialUpdateMode.None ? CelestialUpdatePolicy.None : CelestialUpdatePolicy.EventsOnly);
        var id = fixture.CreateBody();
        fixture.Advance(0, 0);
        fixture.Runtime.EnqueueEvent(fixture.Bridge.SessionId, id, new CelestialEvent("event"));
        fixture.Advance(0, 2);
        Assert.Equal(0, fixture.Counter.AdvanceCount);
        Assert.Equal(expectedEvents, fixture.Counter.EventCount);
    }

    [Fact]
    public void RemovedInstancesDoNotRetainSchedulerCursors()
    {
        var fixture = new Fixture();
        var id = fixture.CreateBody();
        fixture.Advance(0, 0);
        Assert.Contains(fixture.Runtime.Scheduler.CaptureCursors(), cursor => cursor.InstanceId == id);
        var removal = fixture.Runtime.Remove(new(new("remove-cursors", fixture.Bridge.SessionId,
            fixture.Runtime.Catalog.Revision), id));
        Assert.Equal(CelestialOperationStatus.Pending, removal.Status);
        fixture.Advance(0, 0);
        Assert.Equal(CelestialOperationStatus.Applied, fixture.Runtime.GetOperation("remove-cursors").Status);
        Assert.DoesNotContain(fixture.Runtime.Scheduler.CaptureCursors(), cursor => cursor.InstanceId == id);
    }

    [Fact]
    public void BlockedInstancesKeepTheirLastCommittedSchedulerCursors()
    {
        var fixture = new Fixture();
        var id = fixture.CreateBody();
        fixture.Advance(0, 0);
        var previous = fixture.Runtime.Scheduler.CaptureCursors().Where(cursor => cursor.InstanceId == id).ToArray();
        fixture.Counter.ThrowOnAdvance = true;
        fixture.Advance(0, 1);
        Assert.Equal(CelestialLifecycle.Blocked, fixture.Runtime.Query(id).Value.Lifecycle);
        Assert.Equal(previous, fixture.Runtime.Scheduler.CaptureCursors().Where(cursor => cursor.InstanceId == id).ToArray());
    }

    private sealed class Fixture
    {
        public DeterministicTestPhysicsBridge Bridge { get; } = new(Guid.NewGuid());
        public CelestialRuntime Runtime { get; }
        public Counter Counter { get; }
        public Handler Handler { get; } = new();
        public Fixture(CelestialUpdatePolicy policy = null)
        {
            Counter = new Counter(policy);
            var geometry = new TestCelestialGeometryProperties { key = "shape" };
            var motion = new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = Bridge.ProviderId, modeId = "replay" };
            var counter = new CelestialCompDefinition(Counter.Descriptor, new Factory(Counter), _ => CelestialResult.Ok<ICelestialResolvedProperties>(new Properties()));
            var template = CelestialBodyTemplate.Create("PF_Test", "test", "Planet", Array.Empty<string>(), new[] { geometry.ToDefinition(), motion.ToDefinition(), counter }).Value;
            Runtime = new CelestialRuntime(Bridge, Bridge.CommittedStamp, new Resources(), 1, _ => CelestialResult.Ok(template));
            Runtime.Commands.Register("fixture", Handler);
        }
        public CelestialInstanceId CreateBody()
        {
            var operation = Runtime.Create(new CreateBodyRequest(new CelestialOperationContext("create", Bridge.SessionId, 0), "PF_Test", "body"));
            var id = Assert.Single(operation.InstanceIds);
            foreach (double seconds in new[] { 0.0, 0.5, 1.0, 2.0 })
                Bridge.SetFrame(seconds, new Dictionary<CelestialInstanceId, CelestialMotionState> { [id] = new(1, DVector3.zero, DVector3.zero, DQuaternion.identity, DVector3.zero) });
            return id;
        }
        public void Advance(double start, double end) => Assert.True(Runtime.Advance(new PhysicsIntervalRequest(Bridge.SessionId, Runtime.Coordinator.CommittedStamp.TopologyRevision, start, end)).Success);
    }
    private sealed class Counter : CelestialBodyComp
    {
        public Counter(CelestialUpdatePolicy policy = null) : base(new CelestialCompDescriptor("counter", "test.counter", 1, updatePolicy: policy ?? CelestialUpdatePolicy.Every(1))) { }
        public int AdvanceCount { get; private set; }
        public int EventCount { get; private set; }
        public double TotalSeconds { get; private set; }
        public bool ThrowOnAdvance { get; set; }
        public override void Advance(CelestialUpdateContext update)
        {
            if (ThrowOnAdvance) throw new InvalidOperationException("scheduler cursor fixture");
            AdvanceCount++; TotalSeconds += update.EndSeconds - update.StartSeconds;
        }
        public override void Handle(CelestialEvent message) => EventCount++;
    }
    private sealed record Properties : ICelestialResolvedProperties
    {
        public string Key => "counter";
        public string HandlerId => "test.counter";
        public int Version => 1;
        public ReadOnlyMemory<byte> Serialize() => ReadOnlyMemory<byte>.Empty;
    }
    private sealed class Factory(Counter counter) : ICelestialCompFactory
    {
        public CelestialBodyComp Create(ICelestialResolvedProperties properties) => counter;
    }
    private sealed class Resources : ICelestialRemovalResources
    {
        public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId instanceId) => Array.Empty<CelestialError>();
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId instanceId) => CelestialResult.Ok(true);
    }
    private sealed class Handler : ICelestialCommandHandler
    {
        public int Count { get; private set; }
        public CelestialResult<bool> Execute(CelestialCommand command) { Count++; return CelestialResult.Ok(true); }
    }
}
