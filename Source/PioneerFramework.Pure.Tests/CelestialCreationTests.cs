using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class CelestialCreationTests
{
    [Fact]
    public void PendingPhysicsCannotPublishReadyAndBatchBecomesVisibleTogether()
    {
        var fixture = new Fixture();
        var manifest = fixture.Manifest(2);
        var operation = fixture.Pipeline.CreateBatch(new CreateBatchRequest(fixture.Context("create"), manifest));
        Assert.Equal(CelestialOperationStatus.Pending, operation.Status);
        Assert.Empty(fixture.Catalog.Capture().Bodies);
        Assert.All(manifest.Items, item => Assert.Equal(CelestialLifecycle.Registering, fixture.Pipeline.Query(item.Id).Value.Lifecycle));
        fixture.Apply();
        Assert.Equal(CelestialOperationStatus.Applied, fixture.Pipeline.GetOperation("create").Status);
        Assert.Equal(2, fixture.Catalog.Capture().Bodies.Count);
        Assert.All(manifest.Items, item => Assert.True(fixture.Pipeline.Query(item.Id).Value.CommittedMembership));
        Assert.All(fixture.Components, component => Assert.Equal(1, component.ReadyCount));
    }

    [Fact]
    public void IdempotencyUsesContentBeforeExpectedRevisionAndRejectsOldSession()
    {
        var fixture = new Fixture();
        var request = new CreateBatchRequest(fixture.Context("create"), fixture.Manifest(2));
        var first = fixture.Pipeline.CreateBatch(request);
        Assert.Same(first, fixture.Pipeline.CreateBatch(request));
        fixture.Apply();
        var applied = fixture.Pipeline.CreateBatch(request);
        Assert.Equal(CelestialOperationStatus.Applied, applied.Status);
        Assert.All(request.Manifest.Items, item => Assert.Equal(1, fixture.Bridge.RegistrationCount(item.Id)));
        var conflict = fixture.Pipeline.CreateBatch(new CreateBatchRequest(request.Context, fixture.Manifest(1)));
        Assert.Equal("OperationConflict", Assert.Single(conflict.Errors).Code);
        var stale = fixture.Pipeline.CreateBatch(new CreateBatchRequest(new CelestialOperationContext("create", Guid.NewGuid(), 0), request.Manifest));
        Assert.Equal("StaleSession", Assert.Single(stale.Errors).Code);
    }

    [Fact]
    public void SingleCreateUsesTheSamePipelineWithStableOperationIdentity()
    {
        var fixture = new Fixture();
        var request = new CreateBodyRequest(fixture.Context("single"), "PF_Body", "moon", seed: 42);
        var first = fixture.Pipeline.Create(request);
        Assert.Equal(CelestialOperationStatus.Pending, first.Status);
        Assert.Single(first.InstanceIds);
        var id = first.InstanceIds[0];
        fixture.Bridge.SetFrame(0, new Dictionary<CelestialInstanceId, CelestialMotionState> { [id] = CelestialPhysicsContractTests.State(0) });
        fixture.Apply();
        Assert.Equal(CelestialOperationStatus.Applied, fixture.Pipeline.Create(request).Status);
        Assert.Equal(id, fixture.Catalog.Capture().Bodies[0].Id);
    }

    [Fact]
    public void FactoryFailurePublishesNoPartialManifestAndReleasesCreatedResources()
    {
        var fixture = new Fixture { ThrowOnCreateNumber = 2 };
        var manifest = fixture.Manifest(2);
        var operation = fixture.Pipeline.CreateBatch(new CreateBatchRequest(fixture.Context("broken"), manifest));
        Assert.Equal(CelestialOperationStatus.Rejected, operation.Status);
        Assert.Empty(fixture.Catalog.Capture().Bodies);
        Assert.All(fixture.Components, component => Assert.True(component.Released));
        Assert.All(manifest.Items, item => Assert.Equal(0, fixture.Bridge.RegistrationCount(item.Id)));
    }

    [Fact]
    public void ReadyCallbackFailureUsesTrackedCompensationBeforeReleasingResources()
    {
        var fixture = new Fixture { ThrowOnReady = true };
        var manifest = fixture.Manifest(2);
        fixture.Pipeline.CreateBatch(new CreateBatchRequest(fixture.Context("broken"), manifest));
        fixture.Apply();
        var waiting = fixture.Pipeline.GetOperation("broken");
        Assert.Equal(CelestialOperationStatus.Pending, waiting.Status);
        Assert.NotNull(waiting.RelatedOperationId);
        Assert.Empty(fixture.Catalog.Capture().Bodies);
        Assert.All(fixture.Components, component => Assert.False(component.Released));
        fixture.Apply();
        var failed = fixture.Pipeline.GetOperation("broken");
        Assert.Equal(CelestialOperationStatus.Rejected, failed.Status);
        Assert.Contains(failed.Errors, error => error.Code == "ComponentReadyFailed");
        Assert.All(fixture.Components, component => Assert.True(component.Released));
        Assert.All(manifest.Items, item => Assert.False(fixture.Pipeline.Query(item.Id).Value.CommittedMembership));
    }

    [Fact]
    public void GeometryOnlyInstanceNeedsNoPhysicsProviderAndHasNoPhysicalMembership()
    {
        var fixture = new Fixture();
        var body = CelestialSystemGenerationTests.Template();
        var id = CelestialInstanceId.Parse("description-only");
        var item = new CelestialSpawnItem(id, "body", 0, "body[0]", body, body.Resolve(new CelestialGenerationContext(1, "body[0]")).Value);
        fixture.Bridge.IsAvailable = false;
        var operation = fixture.Pipeline.CreateBatch(new CreateBatchRequest(fixture.Context("describe"), new CelestialSpawnManifest("direct", 1, null, new[] { item })));
        fixture.Pipeline.Synchronize();
        Assert.Equal(CelestialOperationStatus.Applied, fixture.Pipeline.GetOperation(operation.OperationId).Status);
        Assert.False(fixture.Pipeline.Query(id).Value.CommittedMembership);
    }

    [Fact]
    public void BatchAssemblyUsesDependencyOrderAndBindsBinaryInitializationReferences()
    {
        var fixture = new Fixture();
        var original = fixture.Manifest(3);
        var items = original.Items.Select((item, index) => new CelestialSpawnItem(item.Id, item.EntryKey, item.Index,
            item.GenerationPath, item.Template, item.Components.Reverse(), index == 2 ? original.Items[0].Id : null,
            new Dictionary<string, CelestialInstanceId> { [index == 2 ? "primary" : "partner"] = original.Items[index == 0 ? 1 : 0].Id })).ToArray();
        var manifest = new CelestialSpawnManifest("binary", 123, null, items);
        var operation = fixture.Pipeline.CreateBatch(new CreateBatchRequest(fixture.Context("binary"), manifest));
        Assert.Equal(CelestialOperationStatus.Pending, operation.Status);
        fixture.Apply();
        Assert.Equal(3, fixture.Catalog.Capture().Bodies.Count);
        Assert.Equal(items[0].Id, fixture.Pipeline.GetRegistration(items[2].Id)!.ParentInstanceId);
        Assert.Equal(items[1].Id, fixture.Pipeline.GetRegistration(items[0].Id)!.InitializationReferences["partner"]);
    }

    [Fact]
    public void SavedOperationLedgerKeepsIdentityWhenDeliveryContextChanges()
    {
        var original = new CelestialOperationStore(Guid.NewGuid());
        var context = new CelestialOperationContext("create", original.WorldSessionId, 0);
        var id = CelestialInstanceId.Parse("saved");
        original.Begin(context, "content", 0, new[] { id });
        var restored = new CelestialOperationStore(Guid.NewGuid(), original.Capture());
        var resumed = restored.Begin(new CelestialOperationContext("create", restored.WorldSessionId, 9), "content", 9, new[] { id });
        Assert.False(resumed.Value.IsNew);
        Assert.Equal(id, Assert.Single(resumed.Value.Operation.InstanceIds));
        Assert.Equal("StaleSession", Assert.Single(restored.Begin(context, "content", 9, new[] { id }).Errors).Code);
    }

    internal sealed class Fixture
    {
        public CelestialCatalog Catalog { get; } = new();
        public DeterministicTestPhysicsBridge Bridge { get; } = new(Guid.NewGuid());
        public CelestialCreationPipeline Pipeline { get; }
        public CelestialOperationStore Operations { get; }
        public List<BindingComp> Components { get; } = new();
        public int ThrowOnCreateNumber { get; set; }
        public bool ThrowOnReady { get; set; }
        private int created;
        public Fixture()
        {
            Operations = new CelestialOperationStore(Bridge.SessionId);
            Pipeline = new CelestialCreationPipeline(Catalog, Bridge, Operations, 123,
                _ => CelestialResult.Ok(Template()));
        }
        public CelestialOperationContext Context(string operation) => new(operation, Bridge.SessionId, Catalog.Revision);
        public CelestialSpawnManifest Manifest(int count)
        {
            var template = Template();
            var items = Enumerable.Range(0, count).Select(index => {
                string path = "body[" + index + "]";
                return new CelestialSpawnItem(CelestialInstanceId.Parse(path), "body", index, path, template,
                    template.Resolve(new CelestialGenerationContext(123, path)).Value); }).ToArray();
            Bridge.SetFrame(0, items.ToDictionary(item => item.Id, _ => CelestialPhysicsContractTests.State(0)));
            return new CelestialSpawnManifest("PF_System", 123, null, items);
        }
        public void Apply()
        {
            var pending = Pipeline.PendingPhysicsOperationIds;
            var prepared = Bridge.PrepareInterval(new PhysicsIntervalRequest(Bridge.SessionId, Bridge.CommittedStamp.TopologyRevision,
                Bridge.CommittedStamp.PhysicalSeconds, Bridge.CommittedStamp.PhysicalSeconds, pending));
            Assert.Equal(PhysicsPreparationStatus.Prepared, prepared.Status);
            Bridge.CommitPreparedInterval(prepared.Token!.Value);
            Pipeline.Synchronize(Bridge.CommittedStamp);
        }
        private CelestialBodyTemplate Template()
        {
            var baseTemplate = CelestialSystemGenerationTests.Template();
            var descriptor = new CelestialCompDescriptor("motion", ExternalPhysicsResolvedProperties.Handler, 1,
                provides: new[] { "physics.binding" }, requires: new[] { "geometry" }, exclusiveSlot: "physics.binding");
            var binding = new CelestialCompDefinition(descriptor, new Factory(this, descriptor), _ => CelestialResult.Ok<ICelestialResolvedProperties>(
                new ExternalPhysicsResolvedProperties("motion", 20, new PhysicsInitialization(DeterministicTestPhysicsBridge.Id, "replay", 1, ReadOnlySpan<byte>.Empty))));
            return CelestialBodyTemplate.Create("PF_Body", "body", "Planet", Array.Empty<string>(), baseTemplate.Components.Append(binding)).Value;
        }
        private sealed class Factory(Fixture owner, CelestialCompDescriptor descriptor) : ICelestialCompFactory
        {
            public CelestialBodyComp Create(ICelestialResolvedProperties properties)
            {
                if (++owner.created == owner.ThrowOnCreateNumber) throw new InvalidOperationException("fixture factory failure");
                var component = new BindingComp(descriptor, owner);
                owner.Components.Add(component);
                return component;
            }
        }
    }
    internal sealed class BindingComp(CelestialCompDescriptor descriptor, Fixture owner) : CelestialBodyComp(descriptor), ICelestialPhysicsBinding
    {
        public int ReadyCount { get; private set; }
        public bool Released { get; private set; }
        protected override void OnAttached()
        {
            if (!Context.GetCapabilities<CelestialBodyComp>().Any(component => component.Descriptor.Provides.Contains("geometry")))
                throw new InvalidOperationException("Geometry dependency must be attached first.");
        }
        public override void OnReady() { ReadyCount++; if (owner.ThrowOnReady) throw new InvalidOperationException("fixture ready failure"); }
        protected override void OnRelease() => Released = true;
        public CelestialResult<CelestialBodyRegistration> CreateRegistration(CelestialBodyDescription description, CelestialInstanceId? parentId,
            IReadOnlyDictionary<string, CelestialInstanceId> initializationReferences) => CelestialResult.Ok(new CelestialBodyRegistration(
                description.DefinitionId, new CelestialPhysicsDescriptor(description.Id, description.DescriptorRevision, description.RadiusMeters, description.GravitationalParameter),
                ((ICelestialPhysicsBindingProperties)Context.Properties).Initialization, parentId, initializationReferences, description.Handle));
    }
}
