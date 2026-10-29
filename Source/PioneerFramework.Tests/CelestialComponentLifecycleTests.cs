using System;
using System.Linq;
using System.Collections.Generic;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialComponentLifecycleTests
{
    [Fact]
    public void RestoredNotificationRunsAfterNativeBindingsAndCommittedQueriesBecomeAvailable()
    {
        using var native = new CelestialMapIdentityTests.Fixture();
        var runtime = CelestialBenchFactory.Create(3, transform: template => CelestialBodyTemplate.Create(template.DefinitionId, template.Label,
            template.Category, template.Tags, template.Components.Append(new CelestialCompDefinition(new("lifecycle", "test.lifecycle", 1),
                new LifecycleCodec(), _ => CelestialResult.Ok<ICelestialResolvedProperties>(new LifecycleProperties())))).Value);
        var document = new CelestialSaveAdapter(runtime).CaptureCheckpoint().Value;
        var registry = CelestialBuiltinCompRegistry.Create(); var codec = new LifecycleCodec(true);
        Assert.True(registry.Register(new("test.lifecycle", 1, 1, codec, codec)).Success);
        var session = Guid.NewGuid(); var provider = new ExistingCelestialEphemerisBridge(session);
        var restore = new CelestialRestoreCoordinator(session, provider, registry, _ => throw new InvalidOperationException("No XML replay"));
        Assert.True(restore.RestoreIdentities(document).Success); Assert.True(restore.BindNativeObjects().Success);
        Assert.True(restore.CompleteRestore().Success);
        foreach (int index in Enumerable.Range(0, 3))
        {
            var id = CelestialBenchFactory.Id(index);
            Assert.Equal(CelestialLifecycle.Ready, restore.Runtime.Query(id).Value.Lifecycle);
            Assert.Equal(1, restore.Runtime.GetCapabilities<LifecycleProbe>(id).Single().RestoredCalls);
        }
    }
    [Fact]
    public void RestoredStateSkipsInitializationAndReadyAndNotifiesOnce()
    {
        var properties = new LifecycleProperties();
        var component = new LifecycleProbe(); component.Attach(new StubContext(properties)); component.OnReady(); component.Value = 7;
        Assert.Equal(1, component.Initializations); Assert.Equal(1, component.ReadyCalls);
        var registration = new LifecycleCodec(); var registry = new CelestialCompRegistry();
        Assert.True(registry.Register(new("test.lifecycle", 1, 1, registration, registration)).Success);
        var record = CelestialCompStateCodec.Encode(component, properties);
        var decoded = CelestialCompStateCodec.Decode(record, registry).Value;
        var restored = Assert.IsType<LifecycleProbe>(decoded.Comp);
        restored.Attach(new StubContext(decoded.Properties)); restored.NotifyRestored(); restored.NotifyRestored();
        Assert.Equal(7, restored.Value); Assert.Equal(0, restored.Initializations); Assert.Equal(0, restored.ReadyCalls);
        Assert.Equal(1, restored.RestoredCalls); Assert.Equal(CelestialAttachReason.Restored, restored.AttachReason);
    }
    private sealed class LifecycleProperties : ICelestialResolvedProperties
    {
        public string Key => "lifecycle"; public string HandlerId => "test.lifecycle"; public int Version => 1;
        public ReadOnlyMemory<byte> Serialize() => ReadOnlyMemory<byte>.Empty;
    }
    private sealed class LifecycleProbe() : CelestialBodyComp(new("lifecycle", "test.lifecycle", 1))
    {
        internal int Value, Initializations, ReadyCalls, RestoredCalls;
        internal bool RequireCommittedQuery;
        protected override void OnInitialized() { Initializations++; Value = 1; }
        public override void OnReady() => ReadyCalls++;
        public override void OnRestored()
        {
            RestoredCalls++;
            if (!RequireCommittedQuery) return;
            if (!Context.TryGetService<ICelestialQueryService>(out var service)) throw new InvalidOperationException("Query service missing");
            using var sample = service.Sample(new(service.WorldSessionId, service.CurrentStamp.TopologyRevision, new[] { Context.InstanceId })).Snapshot;
            if (sample == null) throw new InvalidOperationException("RestoreSampleNotReady");
        }
        public override ReadOnlyMemory<byte> CaptureState() => BitConverter.GetBytes(Value);
    }
    private sealed class LifecycleCodec(bool requireCommittedQuery = false) : ICelestialCompFactory, ICelestialCompStateHandler
    {
        public CelestialBodyComp Create(ICelestialResolvedProperties properties) => new LifecycleProbe { RequireCommittedQuery = requireCommittedQuery };
        public CelestialResult<ICelestialResolvedProperties> DecodeProperties(string key, int version, ReadOnlyMemory<byte> payload)
            => CelestialResult.Ok<ICelestialResolvedProperties>(new LifecycleProperties());
        public CelestialResult<bool> RestoreState(CelestialBodyComp component, int version, ReadOnlyMemory<byte> payload)
        { ((LifecycleProbe)component).Value = BitConverter.ToInt32(payload.Span); return CelestialResult.Ok(true); }
    }
    private sealed class StubContext(ICelestialResolvedProperties properties) : ICelestialCompContext
    {
        public CelestialInstanceId InstanceId => CelestialBenchFactory.Id(0);
        public Guid WorldSessionId => CelestialBenchFactory.Session;
        public long CatalogRevision => 1; public long DescriptorRevision => 1; public double PhysicalSeconds => 0;
        public ICelestialResolvedProperties Properties => properties;
        public bool TryGetCapability<T>(out T capability) where T : class { capability = null; return false; }
        public IReadOnlyList<T> GetCapabilities<T>() where T : class => Array.Empty<T>();
        public bool TryGetService<T>(out T service) where T : class { service = null; return false; }
        public CelestialResult<string> Submit(CelestialCommand command) => throw new InvalidOperationException();
    }
    [Fact]
    public void ThrowingReentrantReleaseDetachesOnceAndRemovesCapabilities()
    {
        var runtime = CelestialBenchFactory.Create(3, transform: template => {
            var descriptor = new CelestialCompDescriptor("release", "test.release", 1);
            return CelestialBodyTemplate.Create(template.DefinitionId, template.Label, template.Category, template.Tags,
                template.Components.Append(new CelestialCompDefinition(descriptor, new Factory(descriptor),
                    _ => CelestialResult.Ok<ICelestialResolvedProperties>(new Properties())))).Value;
        });
        var id = CelestialBenchFactory.Id(0);
        var component = runtime.GetCapabilities<ReleaseProbe>(id).Single();
        Assert.Throws<InvalidOperationException>(component.Release);
        Assert.Equal(1, component.Calls);
        Assert.False(component.IsAttached);
        Assert.Empty(runtime.GetCapabilities<ReleaseProbe>(id));
        Assert.Throws<InvalidOperationException>(() => component.ReadOwner());
        component.Release();
    }
    private sealed class Properties : ICelestialResolvedProperties
    {
        public string Key => "release"; public string HandlerId => "test.release"; public int Version => 1;
        public ReadOnlyMemory<byte> Serialize() => ReadOnlyMemory<byte>.Empty;
    }
    private sealed class Factory(CelestialCompDescriptor descriptor) : ICelestialCompFactory
    { public CelestialBodyComp Create(ICelestialResolvedProperties properties) => new ReleaseProbe(descriptor); }
    private sealed class ReleaseProbe(CelestialCompDescriptor descriptor) : CelestialBodyComp(descriptor)
    {
        public int Calls;
        public CelestialInstanceId ReadOwner() => Context.InstanceId;
        protected override void OnRelease() { if (++Calls == 1) Release(); throw new InvalidOperationException("release fixture"); }
    }
}
