using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialCallbackBoundaryTests
{
    [Fact]
    public void ForeignAdvanceCannotImplicitlyTakeOwnershipOfNativeHostWork()
    {
        var runtime = CelestialBenchFactory.Create(3);
        CelestialResult<bool> result = null;
        var worker = new Thread(() => result = runtime.Advance(new(runtime.WorldSessionId, runtime.CurrentStamp.TopologyRevision, 0, 1)));
        worker.Start(); Assert.True(worker.Join(TimeSpan.FromSeconds(2)));
        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Code == "WrongHostThread");
        Assert.True(runtime.HostWork.IsHostThread);
        Assert.Equal(0, runtime.CurrentStamp.PhysicalSeconds);
    }
    [Fact]
    public void CommandCallbacksCanAwaitWorkerQueriesAndNewCommandsKeepNextBoundaryFifo()
    {
        var runtime = CelestialBenchFactory.Create(3, pending: true);
        var calls = new List<string>();
        Assert.True(runtime.RegisterCommandHandler("test.query", new Handler(command => {
            if (!ReadFromWorker(runtime, command.TargetId)) return CelestialResult.Reject<bool>(new CelestialError("CallbackReadBlocked"));
            Assert.Contains(new CelestialSaveAdapter(runtime).CaptureCheckpoint().Errors, error => error.Code == "HostWorkPending");
            Assert.Contains(runtime.CanRemove(command.TargetId).Blockers, error => error.Code == "HostWorkPending");
            Assert.Contains(runtime.Advance(new(runtime.WorldSessionId, runtime.CurrentStamp.TopologyRevision, 0, 1)).Errors, error => error.Code == "AdvanceInProgress");
            calls.Add(command.OperationId);
            if (command.OperationId == "z-first") Assert.True(runtime.Submit(new("test.query", "late", command.TargetId, runtime.WorldSessionId, 1)).Success);
            return CelestialResult.Ok(true);
        })).Success);
        CelestialBenchFactory.Advance(runtime, 0);
        var id = CelestialBenchFactory.Id(0);
        Assert.True(runtime.Submit(new("test.query", "z-first", id, runtime.WorldSessionId, 1)).Success);
        Assert.True(runtime.Submit(new("test.query", "a-second", id, runtime.WorldSessionId, 1)).Success);
        CelestialBenchFactory.Advance(runtime, 1);
        Assert.Equal(new[] { "z-first", "a-second" }, calls);
        Assert.Equal(CelestialOperationStatus.Applied, runtime.GetOperation("z-first").Status);
        Assert.Equal(CelestialOperationStatus.Pending, runtime.GetOperation("late").Status);
        CelestialBenchFactory.Advance(runtime, 2);
        Assert.Equal(new[] { "z-first", "a-second", "late" }, calls);
    }

    [Fact]
    public void ScheduledAndEventComponentsCanAwaitWorkerQueriesWithoutHoldingPublication()
    {
        var runtime = CelestialBenchFactory.Create(3, transform: template => {
            var properties = new Properties();
            return CelestialBodyTemplate.Create(template.DefinitionId, template.Label, template.Category, template.Tags,
                template.Components.Append(new CelestialCompDefinition(Probe.DescriptorValue, new Factory(), _ => CelestialResult.Ok<ICelestialResolvedProperties>(properties)))).Value;
        });
        foreach (var id in runtime.CaptureCatalog().Bodies.Select(body => body.Id))
        {
            var probe = runtime.GetCapabilities<Probe>(id).Single(); probe.Runtime = runtime;
            Assert.True(runtime.EnqueueEvent(runtime.WorldSessionId, id, new("probe"), "probe").Success);
        }
        CelestialBenchFactory.Advance(runtime, 1);
        foreach (var id in runtime.CaptureCatalog().Bodies.Select(body => body.Id))
        {
            Assert.Equal(CelestialLifecycle.Ready, runtime.Query(id).Value.Lifecycle);
            var probe = runtime.GetCapabilities<Probe>(id).Single();
            Assert.Equal(1, probe.AdvanceCalls); Assert.Equal(1, probe.EventCalls);
        }
    }

    [Fact]
    public void PrepareUsesCommittedCapabilityMembershipAndDeclinesLiveRemovalChecks()
    {
        var participant = new PreparingReader();
        var runtime = CelestialBenchFactory.Create(3, participants: new[] { participant });
        participant.Runtime = runtime;
        var result = runtime.Advance(new(runtime.WorldSessionId, runtime.CurrentStamp.TopologyRevision, 0, 1));
        Assert.True(participant.Consistent, "Query and capability membership must agree with the pre-Prepare committed boundary.");
        Assert.Contains(participant.Removal.Blockers, error => error.Code == "HostWorkPending");
        Assert.Contains(result.Errors, error => error.Code == "PublicationConflict");
    }

    private static bool ReadFromWorker(CelestialRuntime runtime, CelestialInstanceId id)
    {
        var done = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() => {
            try
            {
                var stamp = runtime.CurrentStamp;
                using var sampled = runtime.Sample(new(runtime.WorldSessionId, stamp.TopologyRevision, new[] { id })).Snapshot;
                done.TrySetResult(runtime.Query(id).Success && runtime.CaptureCatalog().Bodies.Any(body => body.Id == id) &&
                    runtime.GetCapabilities<CelestialBodyComp>(id).Count > 0 && runtime.CurrentStamp.WorldSessionId == runtime.WorldSessionId &&
                    sampled != null && sampled.Stamp.PhysicalSeconds == stamp.PhysicalSeconds && runtime.CaptureEpochUsage().Count > 0);
            }
            catch (Exception) { done.TrySetResult(false); }
        }) { IsBackground = true };
        worker.Start();
        // A failed old implementation releases its lock after this bounded wait; do not leave a blocked test process.
        return done.Task.Wait(TimeSpan.FromSeconds(2)) && done.Task.Result;
    }
    private sealed class Handler(Func<CelestialCommand, CelestialResult<bool>> execute) : ICelestialCommandHandler
    { public CelestialResult<bool> Execute(CelestialCommand command) => execute(command); }
    private sealed class Properties : ICelestialResolvedProperties
    {
        public string Key => "probe"; public string HandlerId => "test.callback-probe"; public int Version => 1;
        public ReadOnlyMemory<byte> Serialize() => ReadOnlyMemory<byte>.Empty;
    }
    private sealed class Factory : ICelestialCompFactory
    { public CelestialBodyComp Create(ICelestialResolvedProperties properties) => new Probe(); }
    private sealed class Probe() : CelestialBodyComp(DescriptorValue)
    {
        internal static CelestialCompDescriptor DescriptorValue => new("probe", "test.callback-probe", 1, updatePolicy: CelestialUpdatePolicy.Every(1));
        internal CelestialRuntime Runtime;
        internal int AdvanceCalls, EventCalls;
        public override void Advance(CelestialUpdateContext update)
        { if (!ReadFromWorker(Runtime, Context.InstanceId)) throw new InvalidOperationException("ScheduledReadBlocked"); AdvanceCalls++; }
        public override void Handle(CelestialEvent message)
        { if (!ReadFromWorker(Runtime, Context.InstanceId)) throw new InvalidOperationException("EventReadBlocked"); EventCalls++; }
    }
    private sealed class PreparingReader : ICelestialStepParticipant
    {
        internal CelestialRuntime Runtime;
        internal bool Consistent;
        internal CelestialRemovalCheck Removal;
        public string Id => "test.committed-capabilities";
        public CelestialStepParticipantPhase Phase => CelestialStepParticipantPhase.State;
        public CelestialResult<ICelestialPreparedStepPart> Prepare(CelestialPreparedStepContext context)
        {
            if (Runtime != null)
            {
                var created = Runtime.Create(new(new("during-prepare", Runtime.WorldSessionId, Runtime.Catalog.Revision), "PF_BenchBody", "new"));
                var id = created.InstanceIds[0];
                Consistent = !Runtime.Query(id).Success && Runtime.GetCapabilities<CelestialBodyComp>(id).Count == 0;
                Removal = Runtime.CanRemove(CelestialBenchFactory.Id(1));
            }
            return CelestialResult.Ok<ICelestialPreparedStepPart>(new Part());
        }
    }
    private sealed class Part : ICelestialPreparedStepPart { public void Commit() { } public void Abort() { } }
}
