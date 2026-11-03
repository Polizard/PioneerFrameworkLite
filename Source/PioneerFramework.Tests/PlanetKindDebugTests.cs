using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PioneerFramework.Celestial;
using RimWorld;
using RimWorld.Planet;
using Xunit;

namespace PioneerFramework.Tests;

public class PlanetKindDebugTests
{
    [Fact]
    public void SixActualKindsGetSeparatedOrbitsAndKeepOriginalDefinitions()
    {
        using var content = new CelestialPlanetKindContentTests.Content();
        var before = content.Bodies.Values.SelectMany(body => body.comps.OfType<CompProperties_ExternalPhysicsBinding>()).Select(p => p.initializationPayload).ToArray();
        var root = CelestialInstanceId.Parse("root");
        var result = Build(content.Bodies.Values.ToArray(), "wave-1", root, root, 5000000);
        Assert.True(result.Success, string.Join(",", result.Errors.Select(e => e.Code)));
        Assert.Null(result.Value.RootSurfaceId);
        Assert.Equal(6, result.Value.Items.Count);
        Assert.Equal(6, result.Value.Items.Select(i => i.Id).Distinct().Count());
        var intervals = result.Value.Items.Select(item => {
            Assert.Equal(root, item.ParentId);
            Assert.Equal(root, item.InitializationReferences["frameOrigin"]);
            Assert.Equal(root, item.InitializationReferences["primaryLight"]);
            Assert.Contains("PFL.Debug.PlanetKinds", item.Template.Tags);
            var shape = item.Components.Select(c => c.Properties).OfType<ICelestialGeometryProperties>().Single();
            var physics = item.Components.Select(c => c.Properties).OfType<ICelestialPhysicsBindingProperties>().Single();
            var parsed = ExistingEphemerisParameters.Parse(physics.Initialization);
            Assert.True(parsed.Success);
            Assert.True(parsed.Value.OrbitPeriod > 0);
            var environment = item.Components.Select(c => c.Properties).OfType<ExistingEnvironmentResolvedProperties>().SingleOrDefault();
            if (environment != null) Assert.Equal(environment.Profile.DayLengthTicks, parsed.Value.DayLengthTicks);
            return (Inner: parsed.Value.OrbitRadius - shape.RadiusMeters, Outer: parsed.Value.OrbitRadius + shape.RadiusMeters);
        }).OrderBy(i => i.Inner).ToArray();
        Assert.True(intervals[0].Inner > 5000000);
        for (int i = 1; i < intervals.Length; i++) Assert.True(intervals[i].Inner > intervals[i - 1].Outer + 1000000);
        Assert.Equal(before, content.Bodies.Values.SelectMany(body => body.comps.OfType<CompProperties_ExternalPhysicsBinding>()).Select(p => p.initializationPayload));
        var second = Build(content.Bodies.Values.ToArray(), "wave-2", root, root, intervals.Max(i => i.Outer));
        Assert.True(second.Success);
        Assert.DoesNotContain(second.Value.Items, item => result.Value.Items.Any(old => old.Id == item.Id));
        Assert.All(second.Value.Items, item => {
            var properties = item.Components.Select(c => c.Properties).OfType<ICelestialPhysicsBindingProperties>().Single();
            var radius = item.Components.Select(c => c.Properties).OfType<ICelestialGeometryProperties>().Single().RadiusMeters;
            Assert.True(ExistingEphemerisParameters.Parse(properties.Initialization).Value.OrbitRadius - radius > intervals.Max(i => i.Outer));
        });
    }

    [Fact]
    public void InvalidKindOrMotionRejectsTheWholeScenario()
    {
        using var content = new CelestialPlanetKindContentTests.Content();
        var root = CelestialInstanceId.Parse("root");
        Assert.False(Build(Array.Empty<CelestialBodyDef>(), "empty", root, root, 5000000).Success);
        var body = content.Bodies["PF_BarrenPlanet"];
        body.comps.OfType<CompProperties_ExternalPhysicsBinding>().Single().initializationPayload = Convert.ToBase64String(new byte[] { 1, 2 });
        Assert.False(Build(content.Bodies.Values.ToArray(), "bad-motion", root, root, 5000000).Success);
    }

    [Fact]
    public void RealRuntimeCommitsAllKindsRetainsReceiptsAcrossReopenAndClosesOnlyCompletedEpochs()
    {
        using var harness = new Harness();
        var session = PlanetKindDebugSession.For(harness.Runtime);
        var created = session.Create(harness.Content.Bodies.Values.ToArray());
        Assert.Equal(CelestialOperationStatus.Pending, created.Status);
        Assert.Equal(2, harness.Runtime.CaptureEpochUsage().Count);
        Assert.Same(session, PlanetKindDebugSession.For(harness.Runtime));
        Assert.False(session.Context().Success);
        Assert.Equal(2, harness.Runtime.CaptureEpochUsage().Count);
        harness.Advance();
        session.Refresh();
        Assert.Equal(CelestialOperationStatus.Applied, session.LastOperation.Status);
        Assert.Equal(0, session.PendingCount);
        Assert.Single(harness.Runtime.CaptureEpochUsage());
        Assert.Equal("OperationExpired", harness.Runtime.GetOperation(created.OperationId).Errors.Single().Code);
        Assert.Equal(CelestialOperationStatus.Applied, session.LastOperation.Status);
        Assert.Equal(7, harness.Runtime.CaptureCatalog().Bodies.Count);
        var removed = session.RemoveAll();
        Assert.Equal(6, removed.Count);
        harness.Advance();
        session.Refresh();
        Assert.Equal(Harness.Root, Assert.Single(harness.Runtime.CaptureCatalog().Bodies).Id);
        Assert.Single(harness.Runtime.CaptureEpochUsage());
    }

    internal sealed class Harness : IDisposable, ICelestialRemovalResources
    {
        internal static readonly CelestialInstanceId Root = CelestialInstanceId.Parse("planet-debug-root");
        internal CelestialPlanetKindContentTests.Content Content { get; } = new();
        internal CelestialRuntime Runtime { get; }
        internal Harness(Action<CelestialRuntime> initialize = null)
        {
            var bridge = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
            Runtime = new(bridge, bridge.CommittedStamp, this, 42,
                id => Content.Bodies.TryGetValue(id, out var body) ? body.ToTemplate() : CelestialResult.Reject<CelestialBodyTemplate>(new CelestialError("DefinitionMissing")));
            initialize?.Invoke(Runtime);
            var definition = new CelestialBodyDef { defName = "PF_DebugTestRoot", label = "root", comps = new() {
                new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { value = 5000000 } },
                new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = ExistingCelestialEphemerisBridge.Id, modeId = "existing.circular.v1",
                    gravitationalParameter = new() { value = 2e14 }, initializationPayload = Convert.ToBase64String(CelestialExistingEphemerisTests.Registration(Root, 0).Initialization.Payload.Span) },
                new CompProperties_LightSource { key = "light" }, new CompProperties_Surface { key = "surface", subdivisions = new() { value = 0 } } } };
            var template = definition.ToTemplate().Value;
            var item = new CelestialSpawnItem(Root, "root", 0, "root", template, template.Resolve(new(42, "root")).Value, null,
                new Dictionary<string, CelestialInstanceId> { ["frameOrigin"] = Root, ["primaryLight"] = Root });
            Runtime.CreateBatch(new(new("setup", bridge.SessionId, 0), new("setup", 42, Root, new[] { item })));
            Advance();
            Assert.Equal(CelestialLifecycle.Ready, Runtime.Query(Root).Value.Lifecycle);
        }
        internal void Advance()
        {
            var stamp = Runtime.CurrentStamp;
            var result = Runtime.Advance(new(Runtime.WorldSessionId, stamp.TopologyRevision, stamp.PhysicalSeconds, stamp.PhysicalSeconds));
            Assert.True(result.Success, string.Join(",", result.Errors.Select(e => e.Code)));
        }
        public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id) => Array.Empty<CelestialError>();
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id) => CelestialResult.Ok(true);
        public void Dispose() => Content.Dispose();
    }

    [Fact]
    public void CleanupRetainsReferencedTestsAndNeverTargetsTheDefaultRoot()
    {
        using var harness = new Harness();
        var session = PlanetKindDebugSession.For(harness.Runtime);
        var created = session.Create(harness.Content.Bodies.Values.ToArray());
        harness.Advance();
        session.Refresh();
        var protectedId = created.InstanceIds.First();
        var reference = new CelestialReference("test-ship", protectedId, CelestialReferenceKind.Ship,
            "ship", harness.Runtime.WorldSessionId, harness.Runtime.CaptureCatalog().Revision);
        var lease = harness.Runtime.RegisterReference(reference);
        Assert.True(lease.Success);
        var cleanup = session.RemoveAll();
        Assert.Contains(cleanup, operation => operation.Status == CelestialOperationStatus.Rejected && operation.InstanceIds.Contains(protectedId));
        Assert.NotEmpty(session.Errors);
        session.Track(cleanup.Last());
        Assert.NotEmpty(session.Errors);
        harness.Advance();
        session.Refresh();
        Assert.Equal(2, harness.Runtime.CaptureCatalog().Bodies.Count);
        Assert.Equal(CelestialLifecycle.Ready, harness.Runtime.Query(Harness.Root).Value.Lifecycle);
        Assert.Equal(CelestialLifecycle.Ready, harness.Runtime.Query(protectedId).Value.Lifecycle);
        Assert.True(harness.Runtime.ReleaseReference(lease.Value).Success);
        session.RemoveAll();
        harness.Advance();
        session.Refresh();
        Assert.Equal(Harness.Root, Assert.Single(harness.Runtime.CaptureCatalog().Bodies).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReloadRecoversTestEpochsWithoutReplayingPendingOrCompletedCreation(bool completed)
    {
        using var harness = new Harness();
        using var native = new CelestialNativeWorldTestScope();
        native.AddDefs(new PlanetLayerDef { defName = "PF_CelestialSurface", layerType = typeof(CelestialSurfaceLayer),
            tileType = typeof(SurfaceTile), worldDrawLayers = new() });
        var session = PlanetKindDebugSession.For(harness.Runtime);
        var created = session.Create(harness.Content.Bodies.Values.ToArray());
        if (completed) harness.Advance();
        // Save with the panel closed: it does not poll or close the test epoch.
        var checkpoint = new CelestialSaveAdapter(harness.Runtime).CaptureCheckpoint();
        Assert.True(checkpoint.Success, string.Join(",", checkpoint.Errors.Select(e => e.Code)));
        var document = CelestialSaveCodec.Decode(CelestialSaveCodec.Encode(checkpoint.Value).Value).Value;
        var provider = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var restore = new CelestialRestoreCoordinator(provider.SessionId, provider, CelestialBuiltinCompRegistry.Create(),
            _ => throw new InvalidOperationException("Restore must use saved resolved configurations."));
        Assert.True(restore.RestoreIdentities(document).Success);
        var duringRestore = PlanetKindDebugSession.For(restore.Runtime);
        var blocked = duringRestore.Context();
        Assert.False(blocked.Success);
        Assert.Contains(blocked.Errors, error => error.Code == "RestorePending");
        Assert.Equal(2, restore.Runtime.CaptureEpochUsage().Count);
        Assert.True(restore.BindNativeObjects().Success);
        var ready = restore.CompleteRestore();
        Assert.True(ready.Success, string.Join(",", ready.Errors.Select(e => e.Code)));
        var runtime = restore.Runtime;
        var idsBefore = runtime.Catalog.CaptureInstances().Select(b => b.Id).ToArray();
        var reopened = PlanetKindDebugSession.For(runtime);
        Assert.NotNull(reopened.LastOperation);
        Assert.Equal(created.OperationId, reopened.LastOperation.OperationId);
        Assert.Equal(completed ? 0 : 1, reopened.PendingCount);
        reopened.Refresh();
        Assert.Equal(completed ? 1 : 2, runtime.CaptureEpochUsage().Count);
        if (!completed)
        {
            var stamp = runtime.CurrentStamp;
            Assert.True(runtime.Advance(new(runtime.WorldSessionId, stamp.TopologyRevision, stamp.PhysicalSeconds, stamp.PhysicalSeconds)).Success);
            reopened.Refresh();
        }
        Assert.Single(runtime.CaptureEpochUsage());
        Assert.Equal(CelestialOperationStatus.Applied, reopened.LastOperation.Status);
        Assert.Equal(idsBefore, runtime.Catalog.CaptureInstances().Select(b => b.Id));
        Assert.Equal(7, runtime.CaptureCatalog().Bodies.Count);
    }

    [Fact]
    public void RecoveryDoesNotCloseAnEpochSharedWithForeignActions()
    {
        using var harness = new Harness();
        var session = PlanetKindDebugSession.For(harness.Runtime);
        var operation = session.Create(harness.Content.Bodies.Values.ToArray());
        harness.Advance();
        Assert.True(CelestialOperationEpochs.TryGetEpoch(operation.OperationId, out long epoch));
        string foreign = CelestialOperationEpochs.Qualify(epoch, "another-module/action");
        var ticket = harness.Runtime.Operations.Begin(new(foreign, harness.Runtime.WorldSessionId, harness.Runtime.Catalog.Revision),
            new string('a', 64), harness.Runtime.Catalog.Revision, Array.Empty<CelestialInstanceId>());
        Assert.True(ticket.Success);
        harness.Runtime.Operations.Update(new(foreign, CelestialOperationStatus.Applied));
        var fresh = (PlanetKindDebugSession)Activator.CreateInstance(typeof(PlanetKindDebugSession), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { harness.Runtime }, null);
        fresh.Refresh();
        Assert.Contains(harness.Runtime.CaptureEpochUsage(), usage => usage.Epoch == epoch);
        Assert.Equal(CelestialOperationStatus.Applied, harness.Runtime.GetOperation(foreign).Status);
    }

    private static CelestialResult<CelestialSpawnManifest> Build(IReadOnlyList<CelestialBodyDef> definitions, string operation,
        CelestialInstanceId root, CelestialInstanceId light, double outerEdge)
    {
        var type = typeof(CelestialBodyDef).Assembly.GetType("PioneerFramework.PlanetKindDebugScenario");
        Assert.NotNull(type);
        var method = type.GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);
        return (CelestialResult<CelestialSpawnManifest>)method.Invoke(null, new object[] { definitions, 42UL, operation, root, light, 2e14, outerEdge });
    }
}
