using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialPresentationFollowupTests
{
    [Fact]
    public void EachPresentationBatchSamplesOnceAndStillRefreshesRequestedMotion()
    {
        SampleProbe probe = null;
        var runtime = CelestialBenchFactory.Create(3, visuals: true, bridgeTransform: bridge => probe = new SampleProbe(bridge));
        CelestialBenchFactory.Advance(runtime, 1);
        var adapter = new CelestialViewAdapter(runtime);
        var ids = Enumerable.Range(0, 3).Select(CelestialBenchFactory.Id).ToArray();
        int before = probe.Samples;
        var first = adapter.CapturePresentationBatch(ids, runtime.CurrentStamp, 0.25);
        var next = adapter.CapturePresentationBatch(ids, runtime.CurrentStamp, 0.75);
        Assert.Equal(2, probe.Samples - before);
        Assert.All(first, result => Assert.True(result.Success));
        Assert.All(next, result => Assert.True(result.Success));
        for (int index = 0; index < ids.Length; index++)
        {
            Assert.Equal(0.25, first[index].Value.Stamp.PhysicalSeconds);
            Assert.Equal(0.75, next[index].Value.Stamp.PhysicalSeconds);
            Assert.NotEqual(first[index].Value.Motion.Orientation, next[index].Value.Motion.Orientation);
            Assert.Equal(first[index].Value.CacheKey, next[index].Value.CacheKey);
            Assert.Same(first[index].Value.Visuals, next[index].Value.Visuals);
        }
        Assert.Equal(1, runtime.CurrentStamp.PhysicalSeconds);
    }

    [Fact]
    public void WarmBatchDoesNotCopyTheSameBodyComponentMetadataTwice()
    {
        // A large immutable component-key list makes a duplicate body query observable without timing or GPU assumptions.
        var runtime = CelestialBenchFactory.Create(1, visuals: true, transform: template => {
            var extra = Enumerable.Range(0, 512).Select(index => {
                string key = "metadata-" + index;
                var descriptor = new CelestialCompDescriptor(key, MetadataProperties.Handler, 1);
                return new CelestialCompDefinition(descriptor, new MetadataFactory(descriptor),
                    _ => CelestialResult.Ok<ICelestialResolvedProperties>(new MetadataProperties(key)));
            });
            return CelestialBodyTemplate.Create(template.DefinitionId, template.Label, template.Category, template.Tags,
                template.Components.Concat(extra)).Value;
        });
        var adapter = new CelestialViewAdapter(runtime);
        var ids = new[] { CelestialBenchFactory.Id(0) };
        var stamp = runtime.CurrentStamp;
        Action individual = () => GC.KeepAlive(adapter.CapturePresentation(ids[0], 0));
        Action batch = () => GC.KeepAlive(adapter.CapturePresentationBatch(ids, stamp, 0));
        for (int warmup = 0; warmup < 16; warmup++) { individual(); batch(); }
        const int iterations = 16;
        long singleBytes = Allocated(individual, iterations);
        long batchBytes = Allocated(batch, iterations);
        Assert.True(batchBytes <= singleBytes + iterations * 2048,
            $"Warm one-body batch allocated {batchBytes / iterations} B/capture versus {singleBytes / iterations} B/individual; " +
            "batch orchestration must not copy the 515 component keys a second time.");
    }

    [Theory]
    [InlineData("Moon")]
    [InlineData("Planet")]
    [InlineData("Star")]
    [InlineData("GasGiant")]
    [InlineData("star")]
    [InlineData("99")]
    public void ExistingPhysicsMoonRoleDoesNotDependOnOpenCategoryMetadata(string category)
    {
        var runtime = CreateExistingMoon(category);
        var manager = new CelestialBodyManager(null);
        manager.AttachCelestialRuntime(runtime);
        int root = runtime.Query(Root).Value.Description.Handle;
        int moon = runtime.Query(Satellite).Value.Description.Handle;
        Assert.Equal(category, runtime.Query(Satellite).Value.Description.Category);
        Assert.Equal(CelestialBodyType.Moon, manager.GetBodyById(moon).bodyType);
        Assert.Equal(moon, Assert.Single(manager.GetMoonsOf(root)).bodyId);
        Assert.DoesNotContain(manager.GetPlanets(), body => body.bodyId == moon);
    }

    [Fact]
    public void OtherProviderCompatibilityRolesFollowVisualsAndParentRelationsInsteadOfCategory()
    {
        var bridge = new DeterministicTestPhysicsBridge(Guid.NewGuid());
        CelestialSpawnItem Item(CelestialInstanceId id, string category, CelestialInstanceId? parent, bool stellar)
        {
            var properties = new List<CelestialCompProperties> {
                new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { value = 1000 } },
                new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = DeterministicTestPhysicsBridge.Id, modeId = "replay" },
                new CompProperties_CelestialVisual { key = "visual", profile = new CelestialVisualProfileDef { defName = "PF_MetadataVisual" } }
            };
            if (stellar) properties.Add(new CompProperties_StellarVisual { key = "stellar", profile = new CelestialVisualProfileDef { defName = "PF_MetadataStellar" } });
            else properties.Add(new CompProperties_Surface { key = "surface", subdivisions = new() { value = 0 }, metersPerLayerUnit = new() { value = 10 } });
            var template = new CelestialBodyDef { defName = "PF_MetadataBody", category = category, comps = properties }.ToTemplate().Value;
            return new(id, id.Value, 0, id.Value, template, template.Resolve(new(7, id.Value)).Value, parent,
                new Dictionary<string, CelestialInstanceId> { ["frameOrigin"] = stellar ? Stellar : Root });
        }
        var items = new[] { Item(Stellar, "Moon", null, true), Item(Root, "Star", Stellar, false), Item(Satellite, "Planet", Root, false) };
        bridge.SetFrame(0, items.ToDictionary(item => item.Id, _ => new CelestialMotionState(1, DVector3.zero, DVector3.zero, new(0, 0, 0, 1), DVector3.zero)));
        var runtime = new CelestialRuntime(bridge, bridge.CommittedStamp, new Resources(), 7, _ => throw new InvalidOperationException("Explicit test items only."));
        Assert.Equal(CelestialOperationStatus.Pending, runtime.CreateBatch(new(new("metadata-hierarchy", runtime.WorldSessionId, 0), new("metadata-hierarchy", 7, Root, items))).Status);
        Assert.True(runtime.Advance(new(runtime.WorldSessionId, 0, 0, 0)).Success);
        var manager = new CelestialBodyManager(null);
        manager.AttachCelestialRuntime(runtime);
        int star = runtime.Query(Stellar).Value.Description.Handle;
        int root = runtime.Query(Root).Value.Description.Handle;
        int satellite = runtime.Query(Satellite).Value.Description.Handle;
        Assert.Equal(CelestialBodyType.Star, manager.GetBodyById(star).bodyType);
        Assert.Equal(CelestialBodyType.Planet, manager.GetBodyById(root).bodyType);
        Assert.Equal(CelestialBodyType.Moon, manager.GetBodyById(satellite).bodyType);
        Assert.Equal(satellite, Assert.Single(manager.GetMoonsOf(root)).bodyId);
    }

    [Fact]
    public void StaticPresentationCacheDropsAnOptionalVisualWhenItsFaultRevisionChanges()
    {
        var runtime = CelestialBenchFactory.Create(1, visuals: true, transform: template => {
            var visual = template.Components.Single(component => component.Descriptor.Key == "visual");
            var d = visual.Descriptor;
            var optional = new CelestialCompDescriptor(d.Key, d.HandlerId, d.SchemaVersion, d.Provides, d.Requires, d.ExclusiveSlot,
                d.Before, d.After, d.UpdatePolicy, CelestialCompFailurePolicy.DisableOptional);
            return template.WithOverrides(new[] { new CelestialCompDefinition(optional, visual.Factory,
                context => CelestialResult.Ok(visual.Resolve(context).Value.Properties)) }).Value;
        });
        var id = CelestialBenchFactory.Id(0);
        var adapter = new CelestialViewAdapter(runtime);
        var first = adapter.CapturePresentation(id, 0).Value;
        Assert.Single(first.Visuals);
        Assert.True(adapter.IsConfigurationCurrent());
        var component = Assert.IsAssignableFrom<CelestialBodyComp>(Assert.Single(runtime.GetCapabilities<ICelestialVisualCapability>(id)));
        Assert.True(runtime.Catalog.TryGetInstance(id, out var instance));
        runtime.WithPublication(() => {
            runtime.Creation.HandleComponentFault(instance, component, "ComponentEventFailed", new InvalidOperationException("Optional visual failed."));
            return true;
        });
        Assert.False(adapter.IsConfigurationCurrent());
        var next = adapter.CapturePresentation(id, 0);
        Assert.True(next.Success);
        Assert.Empty(next.Value.Visuals);
        Assert.True(adapter.IsConfigurationCurrent());
        Assert.NotEqual(first.CacheKey, next.Value.CacheKey);
    }

    [Fact]
    public void StaticPresentationCacheChangesItsOwnerLayerKeyWithoutChangingPhysicalScale()
    {
        var fixture = new CelestialViewAdapterTests.Fixture();
        var description = fixture.Runtime.Query(CelestialViewAdapterTests.Fixture.One).Value.Description;
        var cache = new CelestialVisualDescriptorCache(fixture.Runtime);
        var first = cache.Get(description, 11);
        Assert.Same(first, cache.Get(description, 11));
        var replacement = cache.Get(description, 12);
        Assert.NotSame(first, replacement);
        Assert.Equal(11, first.Key.LayerId);
        Assert.Equal(12, replacement.Key.LayerId);
        Assert.Equal(100, first.NativeRadius);
        Assert.Equal(100, replacement.NativeRadius);
        Assert.Equal(1000, description.RadiusMeters);
    }

    private static readonly CelestialInstanceId Root = CelestialInstanceId.Parse("presentation:root");
    private static readonly CelestialInstanceId Satellite = CelestialInstanceId.Parse("presentation:satellite");
    private static readonly CelestialInstanceId Stellar = CelestialInstanceId.Parse("presentation:star");

    private static CelestialRuntime CreateExistingMoon(string category)
    {
        CelestialSpawnItem Item(CelestialInstanceId id, string metadata, string role, CelestialInstanceId? parent)
        {
            var original = CelestialBenchFactory.Template(1000, false, visuals: true);
            var motion = new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = ExistingCelestialEphemerisBridge.Id,
                modeId = "existing.circular.v1", gravitationalParameter = new() { value = 1e8 },
                initializationPayload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new {
                    orbitRadius = parent.HasValue ? 10000d : 0d, orbitPeriod = parent.HasValue ? 1000d : 0d,
                    initialPhase = 0d, orbitNormal = new[] { 0, 1, 0 }, dayLengthTicks = 60000d,
                    bodyType = role, frameOriginReference = "frameOrigin" })) };
            var replaced = original.WithOverrides(new[] { motion.ToDefinition() }).Value;
            var template = CelestialBodyTemplate.Create(replaced.DefinitionId, replaced.Label, metadata, replaced.Tags, replaced.Components).Value;
            return new(id, id.Value, 0, id.Value, template, template.Resolve(new(7, id.Value)).Value, parent,
                new Dictionary<string, CelestialInstanceId> { ["frameOrigin"] = Root });
        }
        var bridge = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var runtime = new CelestialRuntime(bridge, bridge.CommittedStamp, new Resources(), 7, _ => throw new InvalidOperationException("Explicit test items only."));
        var items = new[] { Item(Root, "Planet", "Planet", null), Item(Satellite, category, "Moon", Root) };
        Assert.Equal(CelestialOperationStatus.Pending, runtime.CreateBatch(new(new("metadata-roles", runtime.WorldSessionId, 0), new("metadata-roles", 7, null, items))).Status);
        Assert.True(runtime.Advance(new(runtime.WorldSessionId, 0, 0, 0)).Success);
        return runtime;
    }

    private static long Allocated(Action action, int iterations)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
    private sealed class MetadataProperties(string key) : ICelestialResolvedProperties
    {
        internal const string Handler = "PFL.Tests.PresentationMetadata";
        public string Key => key;
        public string HandlerId => Handler;
        public int Version => 1;
        public ReadOnlyMemory<byte> Serialize() => new byte[] { 1 };
    }
    private sealed class MetadataFactory(CelestialCompDescriptor descriptor) : ICelestialCompFactory
    { public CelestialBodyComp Create(ICelestialResolvedProperties properties) => new MetadataComp(descriptor); }
    private sealed class MetadataComp(CelestialCompDescriptor descriptor) : CelestialBodyComp(descriptor) { }
    private sealed class Resources : ICelestialRemovalResources
    {
        public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id) => Array.Empty<CelestialError>();
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id) => CelestialResult.Ok(true);
    }
    private sealed class SampleProbe(ICelestialPhysicsBridge inner) : ICelestialPhysicsBridge
    {
        internal int Samples;
        public string ProviderId => inner.ProviderId;
        public PhysicsContractVersion ContractVersion => inner.ContractVersion;
        public PhysicsBridgeStatus Status => inner.Status;
        public PhysicsReferenceFrame ReferenceFrame => inner.ReferenceFrame;
        public PhysicsOperation Submit(PhysicsChangeSet request) => inner.Submit(request);
        public PhysicsOperation GetOperation(string operationId) => inner.GetOperation(operationId);
        public PhysicsIntervalPreparation PrepareInterval(PhysicsIntervalRequest request) => inner.PrepareInterval(request);
        public void CommitPreparedInterval(PhysicsIntervalToken token) => inner.CommitPreparedInterval(token);
        public void AbortPreparedInterval(PhysicsIntervalToken token) => inner.AbortPreparedInterval(token);
        public PhysicsSnapshotResult Sample(PhysicsSampleRequest request) { Samples++; return inner.Sample(request); }
        public PhysicsCheckpoint CaptureCheckpoint() => inner.CaptureCheckpoint();
        public PhysicsRestoreResult RestoreCheckpoint(PhysicsCheckpoint checkpoint, IReadOnlyList<CelestialBodyRegistration> committedBodies) => inner.RestoreCheckpoint(checkpoint, committedBodies);
    }
}
