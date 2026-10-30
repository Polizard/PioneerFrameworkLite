using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class CelestialRemovalTests
{
    [Theory]
    [InlineData(CelestialReferenceKind.Map, "MapExists")]
    [InlineData(CelestialReferenceKind.WorldObject, "WorldObjectExists")]
    [InlineData(CelestialReferenceKind.Ship, "ShipAttached")]
    [InlineData(CelestialReferenceKind.Megastructure, "MegastructureAttached")]
    [InlineData(CelestialReferenceKind.Transport, "TransportActive")]
    [InlineData(CelestialReferenceKind.ExternalPhysics, "ExternalPhysicsAttachment")]
    [InlineData(CelestialReferenceKind.Extension, "ExtensionReference")]
    public void LiveReferencesBlockRemovalWithSpecificReasons(CelestialReferenceKind kind, string code)
    {
        var fixture = new Fixture();
        var reference = fixture.References.Register(new CelestialReference("reference", fixture.Target, kind, "owner",
            fixture.Core.Bridge.SessionId, fixture.Core.Catalog.Revision));
        Assert.True(reference.Success);
        Assert.Contains(fixture.Removal.CanRemove(fixture.Target).Blockers, blocker => blocker.Code == code);
        Assert.True(fixture.References.Release(reference.Value).Success);
        Assert.True(fixture.Removal.CanRemove(fixture.Target).Allowed);
    }

    [Fact]
    public void RootAndChildrenAreProtectedWithoutImplicitCascade()
    {
        var fixture = new Fixture(child: true);
        Assert.Contains(fixture.Removal.CanRemove(fixture.Target).Blockers, blocker => blocker.Code == "ChildBodyExists");
        fixture.Core.Pipeline.RootSurfaceId = fixture.Target;
        Assert.Contains(fixture.Removal.CanRemove(fixture.Target).Blockers, blocker => blocker.Code == "RootSurfaceProtected");
        Assert.Equal(2, fixture.Core.Catalog.Capture().Bodies.Count);
    }

    [Fact]
    public void RemovingRejectsNewAssociationsAndKeepsResourcesUntilPhysicsAcknowledgement()
    {
        var fixture = new Fixture();
        var operation = fixture.Remove("remove");
        Assert.Equal(CelestialOperationStatus.Pending, operation.Status);
        Assert.Equal(CelestialLifecycle.Removing, fixture.Core.Pipeline.Query(fixture.Target).Value.Lifecycle);
        Assert.Equal(0, fixture.Resources.ReleaseCount);
        Assert.True(fixture.Core.Pipeline.Query(fixture.Target).Value.CommittedMembership);
        var late = fixture.References.Register(new CelestialReference("late", fixture.Target, CelestialReferenceKind.Ship, "ship",
            fixture.Core.Bridge.SessionId, fixture.Core.Catalog.Revision));
        Assert.Equal("BodyRemoving", Assert.Single(late.Errors).Code);
        fixture.Apply();
        Assert.Equal(CelestialOperationStatus.Applied, fixture.Removal.GetOperation("remove").Status);
        Assert.Equal(1, fixture.Resources.ReleaseCount);
        Assert.Equal(CelestialLifecycle.Removed, fixture.Core.Pipeline.Query(fixture.Target).Value.Lifecycle);
        Assert.Single(fixture.Core.Catalog.Capture().Bodies);
        Assert.Same(fixture.Removal.GetOperation("remove"), fixture.Remove("remove", expectedRevision: 1));
    }

    [Fact]
    public void CleanupFailureRemainsRetryableWithoutAnotherUnregisterOrIdentityReuse()
    {
        var fixture = new Fixture();
        fixture.Resources.FailCleanup = true;
        fixture.Remove("remove");
        fixture.Apply();
        Assert.Equal(CelestialOperationStatus.Pending, fixture.Removal.GetOperation("remove").Status);
        Assert.Contains(fixture.Removal.GetOperation("remove").Errors, error => error.Code == "CleanupPending");
        Assert.False(fixture.Core.Pipeline.Query(fixture.Target).Value.CommittedMembership);
        Assert.Empty(fixture.Removal.PendingPhysicsOperationIds);
        fixture.Resources.FailCleanup = false;
        fixture.Removal.Synchronize();
        Assert.Equal(CelestialOperationStatus.Applied, fixture.Removal.GetOperation("remove").Status);
        Assert.Equal(1, fixture.Core.Bridge.RegistrationCount(fixture.Target));
        var replacement = fixture.Core.Pipeline.CreateBatch(new CreateBatchRequest(fixture.Core.Context("reuse"), fixture.Manifest));
        Assert.Equal("IdentityReserved", Assert.Single(replacement.Errors).Code);
    }

    [Fact]
    public void AuthoritativeResourceChecksCanBlockEvenWithoutRegisteredReferences()
    {
        var fixture = new Fixture();
        fixture.Resources.HasMap = true;
        Assert.Contains(fixture.Removal.CanRemove(fixture.Target).Blockers, blocker => blocker.Code == "MapExists");
        var rejected = fixture.Remove("blocked");
        Assert.Equal(CelestialOperationStatus.Rejected, rejected.Status);
        Assert.Empty(fixture.Removal.PendingPhysicsOperationIds);
        Assert.Equal(CelestialLifecycle.Ready, fixture.Core.Pipeline.Query(fixture.Target).Value.Lifecycle);
        fixture.Resources.HasMap = false;
        Assert.Equal(CelestialOperationStatus.Pending, fixture.Remove("retry").Status);
    }

    [Fact]
    public void ReferenceLeasesFromOldLoadSessionsCannotReleaseCurrentAssociations()
    {
        var fixture = new Fixture();
        var first = new CelestialReference("owner", fixture.Target, CelestialReferenceKind.Extension, "extension",
            fixture.Core.Bridge.SessionId, fixture.Core.Catalog.Revision);
        var lease = fixture.References.Register(first).Value;
        var restored = new CelestialReferenceIndex(fixture.Core.Catalog, Guid.NewGuid(), fixture.References.Capture());
        Assert.Equal("StaleSession", Assert.Single(restored.Release(lease).Errors).Code);
        var newLease = restored.Register(first with { WorldSessionId = restored.WorldSessionId }).Value;
        Assert.True(restored.Release(newLease).Success);
        Assert.Empty(restored.Capture());
    }

    [Fact]
    public void PendingUnregisterIsExcludedWhenAnAuthoritativeResourceAppearsBeforeCommit()
    {
        var fixture = new Fixture();
        fixture.Remove("remove");
        fixture.Resources.HasMap = true;
        Assert.Empty(fixture.Removal.PendingPhysicsOperationIds);
        fixture.Removal.Synchronize();
        Assert.Contains(fixture.Removal.GetOperation("remove").Errors, error => error.Code == "MapExists");
        Assert.True(fixture.Core.Pipeline.Query(fixture.Target).Value.CommittedMembership);
        Assert.Equal(0, fixture.Resources.ReleaseCount);
        fixture.Resources.HasMap = false;
        fixture.Apply();
        Assert.Equal(CelestialOperationStatus.Applied, fixture.Removal.GetOperation("remove").Status);
    }

    private sealed class Fixture
    {
        public CelestialCreationTests.Fixture Core { get; } = new();
        public CelestialReferenceIndex References { get; }
        public Resources Resources { get; } = new();
        public CelestialRemovalPipeline Removal { get; }
        public CelestialSpawnManifest Manifest { get; }
        public CelestialInstanceId Target => Manifest.Items[0].Id;
        public Fixture(bool child = false)
        {
            var original = Core.Manifest(2);
            Manifest = !child ? original : new CelestialSpawnManifest(original.SystemDefinitionId, original.WorldSeed, null,
                original.Items.Select((item, index) => new CelestialSpawnItem(item.Id, item.EntryKey, item.Index, item.GenerationPath,
                    item.Template, item.Components, index == 1 ? original.Items[0].Id : null)));
            Core.Pipeline.CreateBatch(new CreateBatchRequest(Core.Context("initial"), Manifest));
            Core.Apply();
            References = new CelestialReferenceIndex(Core.Catalog, Core.Bridge.SessionId);
            Removal = new CelestialRemovalPipeline(Core.Catalog, Core.Pipeline, Core.Bridge, Core.Operations, References, Resources);
        }
        public CelestialOperation Remove(string operationId, long? expectedRevision = null)
            => Removal.Remove(new RemoveBodyRequest(new CelestialOperationContext(operationId, Core.Bridge.SessionId,
                expectedRevision ?? Core.Catalog.Revision), Target));
        public void Apply()
        {
            var prepared = Core.Bridge.PrepareInterval(new PhysicsIntervalRequest(Core.Bridge.SessionId, Core.Bridge.CommittedStamp.TopologyRevision,
                0, 0, Removal.PendingPhysicsOperationIds));
            Assert.Equal(PhysicsPreparationStatus.Prepared, prepared.Status);
            Core.Bridge.CommitPreparedInterval(prepared.Token!.Value);
            Core.Pipeline.Synchronize(Core.Bridge.CommittedStamp);
            Removal.Synchronize();
        }
    }
    private sealed class Resources : ICelestialRemovalResources
    {
        public bool HasMap { get; set; }
        public bool FailCleanup { get; set; }
        public int ReleaseCount { get; private set; }
        public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id)
            => HasMap ? new[] { new CelestialError("MapExists", id) } : Array.Empty<CelestialError>();
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id)
        {
            ReleaseCount++;
            return FailCleanup ? CelestialResult.Reject<bool>(new CelestialError("EmptyLayerCleanupFailed", id)) : CelestialResult.Ok(true);
        }
    }
}
