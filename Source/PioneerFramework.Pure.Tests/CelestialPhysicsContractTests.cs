using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class CelestialPhysicsContractTests
{
    private static readonly CelestialInstanceId First = CelestialInstanceId.Parse("first");
    private static readonly CelestialInstanceId Second = CelestialInstanceId.Parse("second");

    [Fact]
    public void SnapshotSharesTimeFrameAndMatchingDescriptorsAndDoesNotAdvance()
    {
        var bridge = Ready();
        var before = bridge.CommittedStamp;
        using var snapshot = bridge.Sample(Request(bridge)).Snapshot!;
        Assert.Equal(before, snapshot.Stamp);
        foreach (var id in new[] { First, Second })
        {
            Assert.True(snapshot.TryGetBodyState(id, out var state));
            Assert.True(snapshot.TryGetBodyDescriptor(id, out var description));
            Assert.Equal(description.DescriptorRevision, state.DescriptorRevision);
        }
        Assert.Equal(before, bridge.CommittedStamp);
    }

    [Fact]
    public void OldSnapshotRetainsOldStateAndDescriptorAcrossCommit()
    {
        var bridge = Ready();
        using var old = bridge.Sample(Request(bridge)).Snapshot!;
        Assert.True(old.TryGetBodyState(First, out var original));
        var updated = Registration(First, 2);
        bridge.SetFrame(0, States(0, firstRevision: 2));
        bridge.SetFrame(0.5, States(0.5, firstRevision: 2));
        bridge.SetFrame(1, States(1, firstRevision: 2));
        var change = new PhysicsChangeSet("update", bridge.SessionId, bridge.CommittedStamp.TopologyRevision,
            new[] { PhysicsBodyChange.Update(updated) });
        bridge.Submit(change);
        var prepared = bridge.PrepareInterval(new PhysicsIntervalRequest(bridge.SessionId, bridge.CommittedStamp.TopologyRevision, 0, 1, new[] { "update" }));
        Assert.Equal(PhysicsPreparationStatus.Prepared, prepared.Status);
        bridge.CommitPreparedInterval(prepared.Token!.Value);
        using var current = bridge.Sample(Request(bridge)).Snapshot!;
        Assert.True(old.TryGetBodyState(First, out var stillOld));
        Assert.Equal(original, stillOld);
        Assert.True(old.TryGetBodyDescriptor(First, out var oldDescriptor));
        Assert.True(current.TryGetBodyDescriptor(First, out var newDescriptor));
        Assert.Equal(1, oldDescriptor.DescriptorRevision);
        Assert.Equal(2, newDescriptor.DescriptorRevision);
        Assert.True(current.TryGetBodyState(First, out var state));
        Assert.Equal(newDescriptor.DescriptorRevision, state.DescriptorRevision);
    }

    [Fact]
    public void InvalidQueryContextsReturnSpecificErrors()
    {
        var bridge = Ready();
        Assert.Equal("StaleSession", Error(bridge.Sample(new PhysicsSampleRequest(Guid.NewGuid(), 1, new[] { First }))));
        Assert.Equal("RevisionConflict", Error(bridge.Sample(new PhysicsSampleRequest(bridge.SessionId, 0, new[] { First }))));
        Assert.Equal("TimeUnavailable", Error(bridge.Sample(new PhysicsSampleRequest(bridge.SessionId, 1, new[] { First }, physicalSeconds: 1))));
        Assert.Equal("BodyMissing", Error(bridge.Sample(new PhysicsSampleRequest(bridge.SessionId, 1, new[] { CelestialInstanceId.Parse("missing") }))));
    }

    [Fact]
    public void AuthoritativeIntervalIncludesEndpointsAndInternalSamplesWithoutPublishingFuture()
    {
        var bridge = Ready();
        var stamp = bridge.CommittedStamp;
        var prepared = bridge.PrepareInterval(new PhysicsIntervalRequest(bridge.SessionId, 1, 0, 1));
        Assert.Equal(PhysicsPreparationStatus.Prepared, prepared.Status);
        foreach (double time in new[] { 0.0, 0.5, 1.0 })
        {
            using var sample = prepared.Sampler!.Sample(time, new[] { First, Second }).Snapshot!;
            Assert.Equal(time, sample.Stamp.PhysicalSeconds);
            Assert.Equal(PhysicsSampleKind.AuthoritativeInterval, sample.Stamp.SampleKind);
        }
        Assert.Equal(stamp, bridge.CommittedStamp);
        Assert.Equal("TimeUnavailable", Error(bridge.Sample(new PhysicsSampleRequest(bridge.SessionId, 1, new[] { First }, physicalSeconds: 1))));
        bridge.CommitPreparedInterval(prepared.Token!.Value);
        var committed = bridge.CommittedStamp;
        bridge.CommitPreparedInterval(prepared.Token.Value);
        Assert.Equal(committed, bridge.CommittedStamp);
        Assert.Equal(1, committed.PhysicalSeconds);
    }

    [Fact]
    public void AbortAndNotReadyKeepTheCommittedBoundary()
    {
        var bridge = Ready();
        var before = bridge.CommittedStamp;
        var prepared = bridge.PrepareInterval(new PhysicsIntervalRequest(bridge.SessionId, 1, 0, 1));
        bridge.AbortPreparedInterval(prepared.Token!.Value);
        bridge.AbortPreparedInterval(prepared.Token.Value);
        Assert.Equal(before, bridge.CommittedStamp);
        bridge.IsAvailable = false;
        Assert.Equal(PhysicsPreparationStatus.Pending, bridge.PrepareInterval(new PhysicsIntervalRequest(bridge.SessionId, 1, 0, 1)).Status);
        Assert.Equal(before, bridge.CommittedStamp);
    }

    [Fact]
    public void SubmissionDuringPreparedIntervalSurvivesCommitAndWaitsForNextBoundary()
    {
        var bridge = Ready();
        var prepared = bridge.PrepareInterval(new PhysicsIntervalRequest(bridge.SessionId, 1, 0, 1));
        var request = new PhysicsChangeSet("late", bridge.SessionId, 1, new[] { PhysicsBodyChange.Unregister(Second) });
        Assert.Equal(PhysicsOperationStatus.Pending, bridge.Submit(request).Status);
        bridge.CommitPreparedInterval(prepared.Token!.Value);
        Assert.Equal(PhysicsOperationStatus.Pending, bridge.GetOperation("late").Status);
        using var current = bridge.Sample(Request(bridge)).Snapshot!;
        Assert.True(current.TryGetBodyState(Second, out _));
        var next = bridge.PrepareInterval(new PhysicsIntervalRequest(bridge.SessionId, 1, 1, 1, new[] { "late" }));
        bridge.CommitPreparedInterval(next.Token!.Value);
        Assert.Equal(PhysicsOperationStatus.Applied, bridge.GetOperation("late").Status);
    }

    [Fact]
    public void OperationLedgerChecksSessionThenSubstanceBeforeExpectedRevision()
    {
        var bridge = Ready();
        var same = InitialChange(bridge.SessionId);
        Assert.Equal(PhysicsOperationStatus.Applied, bridge.Submit(same).Status);
        Assert.Equal(1, bridge.RegistrationCount(First));
        var conflict = new PhysicsChangeSet("initial", bridge.SessionId, 0, new[] { PhysicsBodyChange.Unregister(First) });
        Assert.Equal("OperationConflict", Assert.Single(bridge.Submit(conflict).Errors).Code);
        Assert.Equal("StaleSession", Assert.Single(bridge.Submit(InitialChange(Guid.NewGuid())).Errors).Code);
    }

    [Fact]
    public void CheckpointRestoresCommittedIdentityAndPendingLedgerInANewSession()
    {
        var bridge = Ready();
        var pending = new PhysicsChangeSet("remove", bridge.SessionId, 1, new[] { PhysicsBodyChange.Unregister(Second) });
        Assert.Equal(PhysicsOperationStatus.Pending, bridge.Submit(pending).Status);
        var checkpoint = bridge.CaptureCheckpoint();
        var restored = Empty(Guid.NewGuid());
        Assert.True(restored.RestoreCheckpoint(checkpoint, new[] { Registration(First), Registration(Second) }).Success);
        Assert.NotEqual(bridge.CommittedStamp.WorldSessionId, restored.CommittedStamp.WorldSessionId);
        Assert.Equal("RestorePendingResubmission", Assert.Single(restored.PrepareInterval(
            new PhysicsIntervalRequest(restored.SessionId, 1, 0, 0, new[] { "remove" })).Errors).Code);
        Assert.Equal(PhysicsOperationStatus.Pending, restored.Submit(new PhysicsChangeSet("remove", restored.SessionId, 1, pending.Changes)).Status);
        var prepared = restored.PrepareInterval(new PhysicsIntervalRequest(restored.SessionId, 1, 0, 0, new[] { "remove" }));
        restored.CommitPreparedInterval(prepared.Token!.Value);
        Assert.Equal(PhysicsOperationStatus.Applied, restored.GetOperation("remove").Status);
        using var snapshot = restored.Sample(new PhysicsSampleRequest(restored.SessionId, 2, new[] { First })).Snapshot!;
        Assert.False(snapshot.TryGetBodyState(Second, out _));
        Assert.Equal(1, restored.RegistrationCount(First));
    }

    [Fact]
    public void MalformedCheckpointVectorIsRejectedWithoutEscapingAnException()
    {
        var original = Ready();
        var checkpoint = original.CaptureCheckpoint();
        var json = System.Text.Json.Nodes.JsonNode.Parse(checkpoint.Payload.Span)!;
        json["States"]![First.Value]!["Position"] = System.Text.Json.Nodes.JsonNode.Parse("[0]");
        var malformed = new PhysicsCheckpoint(checkpoint.ProviderId, checkpoint.ContractVersion, checkpoint.PayloadVersion, checkpoint.CheckpointId,
            checkpoint.Stamp, System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
        var result = Empty(Guid.NewGuid()).RestoreCheckpoint(malformed, new[] { Registration(First), Registration(Second) });
        Assert.False(result.Success);
        Assert.Equal("RestoreMismatch", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void RejectedPhysicsReceiptRetainsTheAffectedIdentityAcrossCheckpoint()
    {
        var bridge = Empty(Guid.NewGuid());
        bridge.SetFrame(0, new Dictionary<CelestialInstanceId, CelestialMotionState> { [First] = new(99, DVector3.zero, DVector3.zero, DQuaternion.identity, DVector3.zero) });
        bridge.Submit(new("invalid", bridge.SessionId, 0, new[] { PhysicsBodyChange.Register(Registration(First)) }));
        Assert.Equal(PhysicsPreparationStatus.Rejected, bridge.PrepareInterval(new(bridge.SessionId, 0, 0, 0, new[] { "invalid" })).Status);
        var before = Assert.Single(bridge.GetOperation("invalid").Errors);
        Assert.Equal(First, before.InstanceId);
        var restored = Empty(Guid.NewGuid());
        Assert.True(restored.RestoreCheckpoint(bridge.CaptureCheckpoint(), Array.Empty<CelestialBodyRegistration>()).Success);
        Assert.Equal(before, Assert.Single(restored.GetOperation("invalid").Errors));
    }

    [Fact]
    public void InvalidStateCannotBecomeAValidSnapshot()
    {
        var state = new CelestialMotionState(1, new DVector3(double.NaN, 0, 0), DVector3.zero, DQuaternion.identity, DVector3.zero);
        var stamp = new PhysicsSampleStamp(Guid.NewGuid(), 0, 0, 0, 0, "test.inertial", PhysicsSampleKind.Committed);
        var result = CelestialPhysicsSnapshot.Create(stamp, new[] { Registration(First).Descriptor },
            new Dictionary<CelestialInstanceId, CelestialMotionState> { [First] = state });
        Assert.Equal("InvalidState", Error(result));
    }

    internal static DeterministicTestPhysicsBridge Ready()
    {
        var bridge = Empty(Guid.NewGuid());
        Assert.Equal(PhysicsOperationStatus.Pending, bridge.Submit(InitialChange(bridge.SessionId)).Status);
        var prepared = bridge.PrepareInterval(new PhysicsIntervalRequest(bridge.SessionId, 0, 0, 0, new[] { "initial" }));
        Assert.Equal(PhysicsPreparationStatus.Prepared, prepared.Status);
        bridge.CommitPreparedInterval(prepared.Token!.Value);
        return bridge;
    }
    internal static DeterministicTestPhysicsBridge Empty(Guid session)
    {
        var bridge = new DeterministicTestPhysicsBridge(session);
        foreach (double time in new[] { 0.0, 0.5, 1.0 }) bridge.SetFrame(time, States(time));
        return bridge;
    }
    internal static CelestialBodyRegistration Registration(CelestialInstanceId id, long revision = 1)
        => new("PF_Test", new CelestialPhysicsDescriptor(id, revision, 100, 20),
            new PhysicsInitialization(DeterministicTestPhysicsBridge.Id, "replay", 1, new byte[] { 1 }));
    private static Dictionary<CelestialInstanceId, CelestialMotionState> States(double time, long firstRevision = 1)
        => new() { [First] = State(time, firstRevision), [Second] = State(time + 10, 1) };
    internal static CelestialMotionState State(double position, long revision = 1)
        => new(revision, new DVector3(position, 0, 0), DVector3.zero, DQuaternion.identity, DVector3.zero);
    private static PhysicsChangeSet InitialChange(Guid session) => new("initial", session, 0,
        new[] { PhysicsBodyChange.Register(Registration(First)), PhysicsBodyChange.Register(Registration(Second)) });
    private static PhysicsSampleRequest Request(DeterministicTestPhysicsBridge bridge)
        => new(bridge.SessionId, bridge.CommittedStamp.TopologyRevision, new[] { First, Second });
    private static string Error(PhysicsSnapshotResult result) => Assert.Single(result.Errors).Code;
}
