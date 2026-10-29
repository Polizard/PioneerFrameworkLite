using System;
using System.Threading.Tasks;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialPublicationConcurrencyTests
{
    [Fact]
    public void ChangedPreparationInputsAbortBeforeAnyPublication()
    {
        var participant = new Mutation();
        var runtime = CelestialBenchFactory.Create(3, participants: new[] { participant });
        participant.Runtime = runtime;
        var previous = runtime.CurrentStamp;
        var result = runtime.Advance(new(runtime.WorldSessionId, previous.TopologyRevision, 0, 1));
        Assert.Equal("PublicationConflict", Assert.Single(result.Errors).Code);
        Assert.Equal(previous, runtime.CurrentStamp);
        Assert.False(runtime.Coordinator.HasPreparedStep);
    }
    private sealed class Mutation : ICelestialStepParticipant
    {
        internal CelestialRuntime Runtime;
        public string Id => "test.late-input";
        public CelestialStepParticipantPhase Phase => CelestialStepParticipantPhase.State;
        public CelestialResult<ICelestialPreparedStepPart> Prepare(CelestialPreparedStepContext context)
        {
            if (Runtime != null) Runtime.Create(new(new("late", Runtime.WorldSessionId, Runtime.Catalog.Revision), "missing", "late"));
            return CelestialResult.Ok<ICelestialPreparedStepPart>(new Part());
        }
    }
    [Fact]
    public void PublicReaderAndSecondAdvanceNeverWaitForHostPreparation()
    {
        var participant = new Reader();
        var runtime = CelestialBenchFactory.Create(3, participants: new[] { participant });
        participant.Runtime = runtime;
        var result = runtime.Advance(new(runtime.WorldSessionId, runtime.CurrentStamp.TopologyRevision, 0, 1));
        Assert.True(result.Success, string.Join(",", result.Errors));
        Assert.True(participant.QuerySucceeded);
        Assert.Equal(0, participant.ObservedSeconds);
        Assert.Equal("AdvanceInProgress", participant.ConcurrentError);
    }
    private sealed class Reader : ICelestialStepParticipant
    {
        internal CelestialRuntime Runtime;
        internal bool QuerySucceeded;
        internal double ObservedSeconds;
        internal string ConcurrentError;
        public string Id => "test.public-reader";
        public CelestialStepParticipantPhase Phase => CelestialStepParticipantPhase.State;
        public CelestialResult<ICelestialPreparedStepPart> Prepare(CelestialPreparedStepContext context)
        {
            if (Runtime == null) return CelestialResult.Ok<ICelestialPreparedStepPart>(new Part());
            var work = Task.Run(() => {
                ObservedSeconds = Runtime.CurrentStamp.PhysicalSeconds;
                QuerySucceeded = Runtime.Query(CelestialBenchFactory.Id(0)).Success;
                using var sampled = Runtime.Sample(new(Runtime.WorldSessionId, Runtime.CurrentStamp.TopologyRevision, new[] { CelestialBenchFactory.Id(0) })).Snapshot;
                QuerySucceeded &= sampled != null && sampled.Stamp.PhysicalSeconds == 0;
                var concurrent = Runtime.Advance(new(Runtime.WorldSessionId, Runtime.CurrentStamp.TopologyRevision, 0, 1));
                ConcurrentError = concurrent.Errors[0].Code;
            });
            return work.Wait(TimeSpan.FromSeconds(1)) ? CelestialResult.Ok<ICelestialPreparedStepPart>(new Part()) :
                CelestialResult.Reject<ICelestialPreparedStepPart>(new CelestialError("PublicReadBlockedByPreparation"));
        }
    }
    private sealed class Part : ICelestialPreparedStepPart { public void Commit() { } public void Abort() { } }
}
