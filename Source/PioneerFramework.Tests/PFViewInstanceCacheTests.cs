using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests;

public class PFViewInstanceCacheTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly string[] BodyCaches = { "baseSphereMaterials", "flatColorTextures", "patchMaterials", "patchMeshes", "shellMaterials", "shellNightMaterials", "stellarHaloMaterials" };

    [Fact]
    public void ActualRefreshReleasesChangedOwnerAndPreservesOtherBodyResources()
    {
        using var native = new CelestialNativeWorldTestScope();
        var runtime = Runtime();
        var manager = native.Game.GetComponent<CelestialBodyManager>();
        Assert.True(manager.AttachCelestialRuntime(runtime).Success);
        var window = Window();
        Refresh(window, manager);
        int a = Handle(runtime, 1), b = Handle(runtime, 2);
        Seed(window, a, b);
        var otherKey = Keys(window)[b];
        Set(window, "stellarShadersChecked", true);
        Assert.IsType<MutableVisual>(runtime.GetCapabilities<ICelestialVisualCapability>(CelestialBenchFactory.Id(1)).Single()).Revision++;
        Refresh(window, manager);
        AssertOwnerAbsent(window, a);
        AssertOwnerPresent(window, b, otherKey);
        Assert.True((bool)Get(window, "stellarShadersChecked"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActualVisualRefreshPreservesOnlyCopyUntilBodyOrSurfaceRetires(bool retireBody)
    {
        using var fixture = new CelestialMapIdentityTests.Fixture(AddMutableVisual);
        var runtime = fixture.Scope.Runtime;
        var manager = fixture.Native.Game.GetComponent<CelestialBodyManager>();
        var ids = fixture.Scope.Manifest.Items.Where(item => item.Id != fixture.Root.Header.OwnerId).Select(item => item.Id).ToArray();
        var layers = ids.Select((id, index) => runtime.RequestSurface(new(new("unique-patch-layer-" + index, runtime.WorldSessionId,
            runtime.Catalog.Revision), id)).Handle).ToArray();
        int a = runtime.Query(ids[0]).Value.Description.Handle, b = runtime.Query(ids[1]).Value.Description.Handle;
        var window = Window(); Refresh(window, manager);
        string pathA = BakePath(fixture.Native.World.info.Seed, a), pathB = BakePath(fixture.Native.World.info.Seed, b);
        Assert.False(System.IO.File.Exists(pathA)); Assert.False(System.IO.File.Exists(pathB));
        PublishPatch(a, pathA); PublishPatch(b, pathB);
        var patchA = BakeCache("baked")[a]; var patchB = BakeCache("baked")[b];
        int revisionA = PFViewSurfaceBake.GetRevision(a), revisionB = PFViewSurfaceBake.GetRevision(b);
        Seed(window, a, b);
        var keyB = Keys(window)[b];
        Assert.IsType<MutableVisual>(runtime.GetCapabilities<ICelestialVisualCapability>(ids[0]).Single()).Revision++;
        Refresh(window, manager);
        AssertOwnerAbsent(window, a); AssertOwnerPresent(window, b, keyB);
        Assert.True(BakeCache("baked").Contains(a), "Ordinary visual revision discarded the only surface patch copy");
        Assert.Equal(patchA, BakeCache("baked")[a]); Assert.Equal(patchB, BakeCache("baked")[b]);
        Assert.Equal(revisionA, PFViewSurfaceBake.GetRevision(a)); Assert.Equal(revisionB, PFViewSurfaceBake.GetRevision(b));
        Assert.True(PFViewSurfaceBake.EnsureReady(a, fixture.Native.Grid.PlanetLayers[layers[0].LayerId]));
        if (retireBody)
        {
            Assert.Equal(CelestialOperationStatus.Pending, runtime.Remove(new(new("retire-unique-patch", runtime.WorldSessionId, runtime.Catalog.Revision), ids[0])).Status);
            var stamp = runtime.CurrentStamp;
            Assert.True(runtime.Advance(new(runtime.WorldSessionId, stamp.TopologyRevision, stamp.PhysicalSeconds, stamp.PhysicalSeconds)).Success);
            Assert.Equal(CelestialOperationStatus.Applied, runtime.GetOperation("retire-unique-patch").Status);
        }
        else
        {
            Assert.True(runtime.Surfaces.ReleaseEmptySurface(ids[0]).Success);
            Assert.False(runtime.Surfaces.Find(ids[0]).Success);
        }
        Refresh(window, manager);
        Assert.False(BakeCache("baked").Contains(a)); Assert.True(BakeCache("baked").Contains(b));
        PFViewSurfaceBake.Clear();
    }

    [Fact]
    public void ActualVisualRefreshCanUnloadSidecarBackedPatchWithoutTouchingOtherOwner()
    {
        using var fixture = new CelestialMapIdentityTests.Fixture(AddMutableVisual);
        fixture.Native.World.info.seedString = "cache-sidecar-" + Guid.NewGuid().ToString("N");
        var runtime = fixture.Scope.Runtime;
        var manager = fixture.Native.Game.GetComponent<CelestialBodyManager>();
        var ids = fixture.Scope.Manifest.Items.Where(item => item.Id != fixture.Root.Header.OwnerId).Select(item => item.Id).ToArray();
        foreach (var id in ids) Assert.Equal(CelestialOperationStatus.Applied, runtime.RequestSurface(new(new("sidecar-layer-" + id.Value,
            runtime.WorldSessionId, runtime.Catalog.Revision), id)).Operation.Status);
        int a = runtime.Query(ids[0]).Value.Description.Handle, b = runtime.Query(ids[1]).Value.Description.Handle;
        var window = Window(); Refresh(window, manager);
        string pathA = BakePath(fixture.Native.World.info.Seed, a), pathB = BakePath(fixture.Native.World.info.Seed, b);
        Assert.False(System.IO.File.Exists(pathA)); Assert.False(System.IO.File.Exists(pathB));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(pathA));
        System.IO.File.WriteAllBytes(pathA, new byte[] { 1 });
        PublishPatch(a, pathA); PublishPatch(b, pathB);
        var patchB = BakeCache("baked")[b];
        int revisionA = PFViewSurfaceBake.GetRevision(a), revisionB = PFViewSurfaceBake.GetRevision(b);
        Seed(window, a, b); var keyB = Keys(window)[b];
        Assert.IsType<MutableVisual>(runtime.GetCapabilities<ICelestialVisualCapability>(ids[0]).Single()).Revision++;
        Refresh(window, manager);
        AssertOwnerAbsent(window, a); AssertOwnerPresent(window, b, keyB);
        Assert.False(BakeCache("baked").Contains(a)); Assert.DoesNotContain(a, BakeSet("loadTried"));
        Assert.True(PFViewSurfaceBake.GetRevision(a) > revisionA);
        Assert.Equal(patchB, BakeCache("baked")[b]); Assert.Equal(revisionB, PFViewSurfaceBake.GetRevision(b));
        PFViewSurfaceBake.Clear();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActualVisualRefreshRejectsMemoryOnlyPatchWithInvalidGeometryOrOldDiskIdentity(bool invalidGeometry)
    {
        using var fixture = new CelestialMapIdentityTests.Fixture(AddMutableVisual);
        var runtime = fixture.Scope.Runtime;
        var manager = fixture.Native.Game.GetComponent<CelestialBodyManager>();
        var id = fixture.Scope.Manifest.Items.First(item => item.Id != fixture.Root.Header.OwnerId).Id;
        var surface = runtime.RequestSurface(new(new("invalid-patch-layer", runtime.WorldSessionId, runtime.Catalog.Revision), id));
        Assert.Equal(CelestialOperationStatus.Applied, surface.Operation.Status);
        var layer = Assert.IsType<CelestialSurfaceLayer>(fixture.Native.Grid.PlanetLayers[surface.Handle.LayerId]);
        var window = Window(); Refresh(window, manager);
        int handle = runtime.Query(id).Value.Description.Handle;
        string path = BakePath(fixture.Native.World.info.Seed, handle);
        Assert.False(System.IO.File.Exists(path));
        PublishPatch(handle, invalidGeometry ? path : path + ".obsolete");
        if (invalidGeometry)
            typeof(CelestialSurfaceHeader).GetField("physicalRadius", Flags).SetValue(layer.Header, layer.Header.PhysicalRadiusMeters + 1);
        Assert.IsType<MutableVisual>(runtime.GetCapabilities<ICelestialVisualCapability>(id).Single()).Revision++;
        Refresh(window, manager);
        Assert.False(BakeCache("baked").Contains(handle));
        PFViewSurfaceBake.Clear();
    }

    private static CelestialBodyTemplate AddMutableVisual(CelestialBodyTemplate template)
    {
        var visual = new CompProperties_CelestialVisual { key = "visual", profile = new CelestialVisualProfileDef { defName = "PF_MemoryOnlyPatchVisual" } }.ToDefinition();
        return CelestialBodyTemplate.Create(template.DefinitionId, template.Label, template.Category, template.Tags,
            template.Components.Append(new CelestialCompDefinition(visual.Descriptor, new VisualFactory(visual.Descriptor),
                context => CelestialResult.Ok(visual.Resolve(context).Value.Properties)))).Value;
    }

    [Fact]
    public void ActualRetirementReleasesOnlyRetiredInstance()
    {
        using var native = new CelestialNativeWorldTestScope();
        var runtime = Runtime();
        var manager = native.Game.GetComponent<CelestialBodyManager>();
        Assert.True(manager.AttachCelestialRuntime(runtime).Success);
        var window = Window();
        Refresh(window, manager);
        int a = Handle(runtime, 1), b = Handle(runtime, 2);
        Seed(window, a, b);
        var key = Keys(window)[b];
        var retiredKey = Keys(window)[a];
        Assert.Equal(CelestialOperationStatus.Pending, runtime.Remove(new(new("retire-cache", runtime.WorldSessionId, runtime.Catalog.Revision), CelestialBenchFactory.Id(1))).Status);
        CelestialBenchFactory.Advance(runtime, 0);
        Assert.Equal(CelestialOperationStatus.Applied, runtime.GetOperation("retire-cache").Status);
        Refresh(window, manager);
        AssertOwnerAbsent(window, a);
        Assert.False(Cache(window, "celestialCloudMaterials").Contains(retiredKey));
        AssertOwnerPresent(window, b, key);
    }

    [Fact]
    public void GlobalWorldSwitchReleasesBothEvenWhenNumericHandlesAreReused()
    {
        using var native = new CelestialNativeWorldTestScope();
        var first = new CelestialViewAdapterTests.Fixture();
        var manager = native.Game.GetComponent<CelestialBodyManager>();
        Assert.True(manager.AttachCelestialRuntime(first.Runtime).Success);
        var window = Window();
        Refresh(window, manager);
        int a = first.Runtime.Query(CelestialViewAdapterTests.Fixture.One).Value.Description.Handle;
        int b = first.Runtime.Query(CelestialViewAdapterTests.Fixture.Two).Value.Description.Handle;
        Seed(window, a, b);
        var oldKey = Keys(window)[a];
        var second = new CelestialViewAdapterTests.Fixture();
        manager = new CelestialBodyManager(native.Game);
        native.Game.components.Clear(); native.Game.components.Add(manager);
        Assert.True(manager.AttachCelestialRuntime(second.Runtime).Success);
        Refresh(window, manager);
        AssertOwnerAbsent(window, a);
        AssertOwnerAbsent(window, b);
        Assert.Equal(a, second.Runtime.Query(CelestialViewAdapterTests.Fixture.One).Value.Description.Handle);
        Assert.NotEqual(oldKey.WorldSessionId, Keys(window)[a].WorldSessionId);
    }

    [Fact]
    public void BakeForgetInvalidatesOnlyItsPatchAndGlobalClearInvalidatesBoth()
    {
        using var native = new CelestialNativeWorldTestScope();
        PFViewSurfaceBake.Clear();
        var window = Window();
        const int a = 11, b = 22;
        Synchronize(window, a); Synchronize(window, b);
        Cache(window, "patchMaterials")[a] = null; Cache(window, "patchMeshes")[a] = null;
        Cache(window, "patchMaterials")[b] = null; Cache(window, "patchMeshes")[b] = null;
        PFViewSurfaceBake.ForgetMemory(a);
        Synchronize(window, a); Synchronize(window, b);
        Assert.False(Cache(window, "patchMaterials").Contains(a));
        Assert.True(Cache(window, "patchMaterials").Contains(b));
        Assert.True(Cache(window, "patchMeshes").Contains(b));
        PFViewSurfaceBake.Clear();
        Synchronize(window, a); Synchronize(window, b);
        Assert.Empty(Cache(window, "patchMaterials"));
        Assert.Empty(Cache(window, "patchMeshes"));
    }

    [Fact]
    public void OptionalVisualFaultRemovesItsResourcesWithoutReleasingRebuiltUnchangedEntries()
    {
        using var native = new CelestialNativeWorldTestScope();
        var runtime = Runtime(optional: true);
        var manager = native.Game.GetComponent<CelestialBodyManager>();
        Assert.True(manager.AttachCelestialRuntime(runtime).Success);
        var window = Window(); Refresh(window, manager);
        int a = Handle(runtime, 1), b = Handle(runtime, 2);
        Seed(window, a, b);
        var aKey = Keys(window)[a]; var bKey = Keys(window)[b];
        var oldB = Cache(window, "celestialRenderConfigurations")[b];
        var oldProjectionB = Cache(window, "celestialRenderProjections")[b];
        var id = CelestialBenchFactory.Id(1);
        var component = Assert.IsType<MutableVisual>(runtime.GetCapabilities<ICelestialVisualCapability>(id).Single());
        Assert.True(runtime.Catalog.TryGetInstance(id, out var instance));
        runtime.WithPublication(() => {
            runtime.Creation.HandleComponentFault(instance, component, "ComponentEventFailed", new InvalidOperationException("Optional test visual disabled"));
            return true;
        });
        Refresh(window, manager);
        AssertOwnerAbsent(window, a);
        Assert.False(Cache(window, "celestialCloudMaterials").Contains(aKey));
        AssertOwnerPresent(window, b, bKey);
        Assert.NotSame(oldB, Cache(window, "celestialRenderConfigurations")[b]);
        Assert.Same(oldProjectionB, Cache(window, "celestialRenderProjections")[b]);
        Assert.Equal(bKey, Keys(window)[b]);
        Assert.False(Cache(window, "celestialRenderProjections").Contains(a));
    }

    [Fact]
    public void ActualBakePublicationAndReloadableUnloadPreserveTheOtherPatch()
    {
        using var native = new CelestialNativeWorldTestScope();
        PFViewSurfaceBake.Clear();
        const int a = 11, b = 22;
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pfl-cache-" + Guid.NewGuid().ToString("N") + ".png");
        System.IO.File.WriteAllBytes(path, new byte[] { 1 });
        PublishPatch(a, path); PublishPatch(b, path + ".missing");
        var window = Window(); Synchronize(window, a); Synchronize(window, b);
        Cache(window, "patchMaterials")[a] = null; Cache(window, "patchMeshes")[a] = null;
        Cache(window, "patchMaterials")[b] = null; Cache(window, "patchMeshes")[b] = null;
        PublishPatch(a, path);
        Synchronize(window, a); Synchronize(window, b);
        Assert.False(Cache(window, "patchMaterials").Contains(a));
        Assert.True(Cache(window, "patchMaterials").Contains(b));
        Assert.Contains(a, BakeSet("loadTried"));
        PFViewSurfaceBake.TrimUnused(System.Diagnostics.Stopwatch.GetTimestamp() + 31L * System.Diagnostics.Stopwatch.Frequency);
        Assert.False(BakeCache("baked").Contains(a));
        Assert.DoesNotContain(a, BakeSet("loadTried"));
        Assert.True(BakeCache("baked").Contains(b));
        Synchronize(window, b);
        Assert.True(Cache(window, "patchMaterials").Contains(b));
        PFViewSurfaceBake.ForgetMemory(b);
        Assert.False(BakeCache("baked").Contains(b));
        Assert.DoesNotContain(b, BakeSet("loadTried"));
        PFViewSurfaceBake.Clear();
    }

    [Fact]
    public void RealWindowCleanupAndNoRuntimeFallbackReleaseAllVisualOwners()
    {
        using var native = new CelestialNativeWorldTestScope();
        var runtime = Runtime();
        var manager = native.Game.GetComponent<CelestialBodyManager>();
        Assert.True(manager.AttachCelestialRuntime(runtime).Success);
        var window = Window(); Refresh(window, manager);
        int a = Handle(runtime, 1), b = Handle(runtime, 2);
        Seed(window, a, b);
        Invoke(window, "CleanupResources");
        AssertOwnerAbsent(window, a); AssertOwnerAbsent(window, b);
        Seed(window, a, b);
        var fallback = new CelestialBodyManager(null);
        typeof(CelestialBodyManager).GetField("initialized", Flags).SetValue(fallback, true);
        Refresh(window, fallback);
        foreach (string cache in BodyCaches) Assert.Empty(Cache(window, cache));
        Assert.Empty(Cache(window, "celestialCloudMaterials"));
    }

    [Fact]
    public void ClosingPfViewReleasesItsMaterialsAndRetainsTheSharedSurfaceTexture()
    {
        using var native = new CelestialNativeWorldTestScope();
        PFViewSurfaceBake.Clear();
        const int handle = 19;
        string path = System.IO.Path.GetTempFileName();
        try
        {
            PublishPatch(handle, path);
            int revision = PFViewSurfaceBake.GetRevision(handle);
            var window = Window();
            Cache(window, "patchMaterials")[handle] = null;
            Invoke(window, "CleanupResources");
            Assert.Empty(Cache(window, "patchMaterials"));
            Assert.True(BakeCache("baked").Contains(handle));
            Assert.Equal(revision, PFViewSurfaceBake.GetRevision(handle));
        }
        finally
        {
            PFViewSurfaceBake.Clear();
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void MissingSurfaceSidecarIsResolvedByTheBackgroundQueueOnlyOnce()
    {
        using var native = new CelestialNativeWorldTestScope();
        const int handle = 34129;
        PFViewSurfaceBake.Clear();
        try
        {
            Assert.False(PFViewSurfaceBake.EnsureReady(handle, null));
            Assert.True(PFViewSurfaceBake.IsSidecarPending(handle));
            Assert.True(System.Threading.SpinWait.SpinUntil(() =>
            {
                PFViewSurfaceBake.Update();
                return !PFViewSurfaceBake.IsSidecarPending(handle);
            }, TimeSpan.FromSeconds(5)));
            Assert.False(PFViewSurfaceBake.EnsureReady(handle, null));
            Assert.False(PFViewSurfaceBake.IsSidecarPending(handle));
        }
        finally
        {
            PFViewSurfaceBake.Clear();
        }
    }

    [Fact]
    public void SidecarDecodeRetainsTopDownRgbaAndBuildsTheMipChain()
    {
        // Red, green on the top row, blue, yellow on the bottom row.
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAHUlEQVR4AQESAO3/AP8AAP8A/wD/AAAA/////wD/R8oI+COQcloAAAAASUVORK5CYII=";
        string path = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllBytes(path, Convert.FromBase64String(png));
            var decoded = typeof(PFViewSurfaceBake).GetMethod("DecodeSidecar", Flags)!.Invoke(null, new object[] { path });
            Assert.NotNull(decoded);
            var levels = (byte[][])decoded.GetType().GetProperty("Levels")!.GetValue(decoded)!;
            Assert.Equal(2, levels.Length);
            Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 0, 255 }, levels[0]);
            Assert.Equal(4, levels[1].Length);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void PlanetTileSurfaceYieldsToTheFrameSchedulerAfterEachChunk()
    {
        using var fixture = new CelestialMapIdentityTests.Fixture();
        _ = Window();
        var type = typeof(MainTabWindow_PFView).GetNestedType("TileSurfaceBuild", Flags)!;
        var build = Activator.CreateInstance(type)!;
        type.GetField("phase", Flags)!.SetValue(build, 4);
        type.GetField("chunkPlans", Flags)!.SetValue(build, new List<int[]> { Array.Empty<int>(), Array.Empty<int>() });
        var buildChunk = typeof(MainTabWindow_PFView).GetMethod("BuildTileSurfaceChunks", Flags)!;
        var cursor = type.GetField("chunkCursor", Flags)!;

        buildChunk.Invoke(null, new[] { build, fixture.Root });
        Assert.Equal(1, cursor.GetValue(build));
        Assert.Equal(4, type.GetField("phase", Flags)!.GetValue(build));

        buildChunk.Invoke(null, new[] { build, fixture.Root });
        Assert.Equal(2, cursor.GetValue(build));
        Assert.Equal(5, type.GetField("phase", Flags)!.GetValue(build));
    }

    [Fact]
    public void WorldPreviewRequestReturnsBeforeRenderingAndCancelsOnSurfaceInvalidation()
    {
        using var fixture = new CelestialMapIdentityTests.Fixture();
        var root = fixture.Root;
        int handle = fixture.Scope.Runtime.Query(root.Header.OwnerId).Value.Description.Handle;
        PFViewSurfaceBake.Clear();
        try
        {
            PFViewSurfaceBake.PrepareWorldPreview(handle, root);
            Assert.True(PFViewSurfaceBake.IsWorldPreviewPending(handle, root));
            Assert.False(PFViewSurfaceBake.TryGetWorldPreview(handle, root, out _));
            PFViewSurfaceBake.ForgetMemory(handle, root.LayerID);
            Assert.False(PFViewSurfaceBake.IsWorldPreviewPending(handle, root));
        }
        finally
        {
            PFViewSurfaceBake.Clear();
        }
    }

    [Fact]
    public void BakeMemoryRejectsNewRuntimeWithReusedHandleButRetainsStableSidecarIdentity()
    {
        using var native = new CelestialNativeWorldTestScope();
        var first = new CelestialViewAdapterTests.Fixture();
        var manager = native.Game.GetComponent<CelestialBodyManager>();
        Assert.True(manager.AttachCelestialRuntime(first.Runtime).Success);
        int a = first.Runtime.Query(CelestialViewAdapterTests.Fixture.One).Value.Description.Handle;
        int seed = native.World.info.Seed;
        string oldPath = BakePath(seed, a);
        PublishPatch(a, oldPath);
        Assert.True(BakeCache("baked").Contains(a));
        var second = new CelestialViewAdapterTests.Fixture();
        manager = new CelestialBodyManager(native.Game);
        native.Game.components.Clear(); native.Game.components.Add(manager);
        Assert.True(manager.AttachCelestialRuntime(second.Runtime).Success);
        Assert.Equal(a, second.Runtime.Query(CelestialViewAdapterTests.Fixture.One).Value.Description.Handle);
        var window = Window(); Synchronize(window, a);
        Assert.False(BakeCache("baked").Contains(a));
        Assert.DoesNotContain(a, BakeSet("loadTried"));
        Assert.Equal(oldPath, BakePath(seed, a));
    }

    [Fact]
    public void ForgetAllowsMissingSidecarToBeTriedAgainWithoutTryingOtherBodiesAgain()
    {
        using var native = new CelestialNativeWorldTestScope();
        PFViewSurfaceBake.Clear();
        const int a = 31711, b = 31722;
        Assert.False(PFViewSurfaceBake.EnsureReady(a, null));
        Assert.False(PFViewSurfaceBake.EnsureReady(b, null));
        Assert.Contains(a, BakeSet("loadTried")); Assert.Contains(b, BakeSet("loadTried"));
        PFViewSurfaceBake.ForgetMemory(a);
        Assert.DoesNotContain(a, BakeSet("loadTried")); Assert.Contains(b, BakeSet("loadTried"));
        Assert.False(PFViewSurfaceBake.EnsureReady(a, null));
        Assert.Contains(a, BakeSet("loadTried")); Assert.Contains(b, BakeSet("loadTried"));
        PFViewSurfaceBake.Clear();
    }

    [Fact]
    public void BaseTextureLookupRejectsReusedLayerReferenceAndForgetsOnlyOwnersLayer()
    {
        using var native = new CelestialNativeWorldTestScope();
        PFViewSurfaceBake.Clear();
        Synchronize(Window(), 0);
        var first = (RimWorld.Planet.PlanetLayer)RuntimeHelpers.GetUninitializedObject(typeof(RimWorld.SurfaceLayer));
        var replacement = (RimWorld.Planet.PlanetLayer)RuntimeHelpers.GetUninitializedObject(typeof(RimWorld.SurfaceLayer));
        Assert.Equal(first.LayerID, replacement.LayerID);
        int layerId = first.LayerID;
        BakeCache("baseTexCache")[layerId] = null; BakeCache("baseTexLabel")[layerId] = "old-instance"; BakeCache("baseTexLayers")[layerId] = first;
        Assert.Null(PFViewSurfaceBake.ResolveBaseTexture(first, out string firstLabel));
        Assert.Equal("old-instance", firstLabel);
        Assert.Null(PFViewSurfaceBake.ResolveBaseTexture(replacement, out string replacementLabel));
        Assert.NotEqual("old-instance", replacementLabel);
        BakeCache("baseTexCache")[layerId + 1] = null; BakeCache("baseTexLabel")[layerId + 1] = "other"; BakeCache("baseTexLayers")[layerId + 1] = first;
        PFViewSurfaceBake.ForgetMemory(11, layerId);
        Assert.False(BakeCache("baseTexCache").Contains(layerId));
        Assert.True(BakeCache("baseTexCache").Contains(layerId + 1));
        PFViewSurfaceBake.Clear();
    }

    [Fact]
    public void CompiledRefreshBakeAndDrawEntriesUseTheTestedOwnershipOperations()
    {
        AssertCall(typeof(MainTabWindow_PFView), "GetCelestialRenderBodies", "InvalidateCelestialRenderResources");
        foreach (string release in new[] { "DestroySurfaceResourcesForBody", "DestroyCloudResourcesForBody", "DestroyStellarVisualResourcesForBody", "DestroyAtmosphereShellMaterialsForBody" })
        {
            AssertCall(typeof(MainTabWindow_PFView), "InvalidateCelestialRenderResources", release);
            AssertCall(typeof(MainTabWindow_PFView), release, "DestroyOwnedResource");
        }
        AssertCall(typeof(MainTabWindow_PFView), "DestroyOwnedResource", "Destroy");
        AssertCall(typeof(MainTabWindow_PFView), "InvalidateCelestialRenderResources", "InvalidateVisualMemory");
        AssertCall(typeof(PFViewSurfaceBake), "InvalidateVisualMemory", "MatchesCelestialOwner");
        AssertCall(typeof(PFViewSurfaceBake), "InvalidateVisualMemory", "TryGetCurrentPatch");
        AssertCall(typeof(PFViewSurfaceBake), "InvalidateVisualMemory", "ForgetMemory");
        AssertCall(typeof(MainTabWindow_PFView), "DrawCelestialBodiesCore", "TryGetSurfacePatch");
        AssertCall(typeof(MainTabWindow_PFView), "TryGetSurfacePatch", "RefreshSurfacePatch");
        AssertCall(typeof(MainTabWindow_PFView), "RefreshSurfacePatch", "GetRevision");
        AssertCall(typeof(PFViewSurfaceBake), "TryBake", "StorePatch");
        AssertCall(typeof(PFViewSurfaceBake), "CompleteSidecar", "StorePatch");
        AssertCall(typeof(PFViewSurfaceBake), "Update", "CompleteSidecar");
        AssertCall(typeof(PFViewSurfaceBake), "StorePatch", "Touch");
        AssertCall(typeof(MainTabWindow_PFView), "DrawCloudLayer", "GetCelestialCloudMaterial");
        AssertCall(typeof(MainTabWindow_PFView), "GetBaseSphereMaterial", "get_baseSphereColor");
        AssertCall(typeof(MainTabWindow_PFView), "CleanupResources", "DestroySurfaceResources");
    }

    private static void AssertCall(Type type, string method, string target) => Assert.Contains(
        PFViewUiIntegrationTests.ReferencedMembers(type.GetMethod(method, Flags)), member => member.Name == target);
    private static string BakePath(int seed, int handle) => (string)typeof(PFViewSurfaceBake).GetMethod("PathFor", Flags).Invoke(null, new object[] { seed, handle });

    private static IDictionary BakeCache(string name) => (IDictionary)typeof(PFViewSurfaceBake).GetField(name, Flags).GetValue(null);
    private static HashSet<int> BakeSet(string name) => (HashSet<int>)typeof(PFViewSurfaceBake).GetField(name, Flags).GetValue(null);
    private static void PublishPatch(int handle, string path)
    {
        var patchType = typeof(PFViewSurfaceBake).GetNestedType("Patch", Flags);
        var patch = Activator.CreateInstance(patchType);
        patchType.GetField("path", Flags).SetValue(patch, path);
        var method = typeof(PFViewSurfaceBake).GetMethod("StorePatch", Flags);
        Assert.NotNull(method);
        method.Invoke(null, new[] { (object)handle, patch });
    }
    private static void Invoke(object window, string method) => typeof(MainTabWindow_PFView).GetMethod(method, Flags).Invoke(window, null);

    private static void Synchronize(object window, int handle)
    {
        var method = typeof(MainTabWindow_PFView).GetMethod("RefreshSurfacePatch", Flags);
        Assert.NotNull(method);
        method.Invoke(window, new object[] { handle });
    }
    private static CelestialRuntime Runtime(bool optional = false) => CelestialBenchFactory.Create(3, visuals: true, transform: template => {
        var visual = template.Components.Single(component => component.Descriptor.Key == "visual");
        var original = visual.Descriptor;
        var descriptor = optional ? new CelestialCompDescriptor(original.Key, original.HandlerId, original.SchemaVersion, original.Provides, original.Requires,
            original.ExclusiveSlot, original.Before, original.After, original.UpdatePolicy, CelestialCompFailurePolicy.DisableOptional) : original;
        return template.WithOverrides(new[] { new CelestialCompDefinition(descriptor, new VisualFactory(descriptor), context => CelestialResult.Ok(visual.Resolve(context).Value.Properties)) }).Value;
    });
    private sealed class VisualFactory(CelestialCompDescriptor descriptor) : ICelestialCompFactory
    { public CelestialBodyComp Create(ICelestialResolvedProperties properties) => new MutableVisual(descriptor); }
    private sealed class MutableVisual(CelestialCompDescriptor descriptor) : CelestialBodyComp(descriptor), ICelestialVisualCapability
    {
        internal long Revision;
        public long VisualRevision => Revision;
        public CelestialVisualDescription GetVisualDescription() => new(Descriptor.Key, CelestialVisualKind.Surface, ((VisualResolvedProperties)Context.Properties).Profile);
    }
    private static int Handle(CelestialRuntime runtime, int index) => runtime.Query(CelestialBenchFactory.Id(index)).Value.Description.Handle;
    private static bool SkipGpuInitialization() => false;
    private static object Window()
    {
        // The native Vulkan initializer is outside this headless lifecycle fixture.
        // No cache, refresh, ownership or destruction method is patched.
        var patch = new HarmonyLib.Harmony("PFL.Tests.InstanceCache.NoGpu");
        patch.Patch(typeof(MainTabWindow_PFView).TypeInitializer, prefix: new HarmonyLib.HarmonyMethod(typeof(PFViewInstanceCacheTests).GetMethod(nameof(SkipGpuInitialization), Flags)));
        object window;
        try { window = RuntimeHelpers.GetUninitializedObject(typeof(MainTabWindow_PFView)); }
        finally { patch.UnpatchAll(patch.Id); }
        foreach (var field in typeof(MainTabWindow_PFView).GetFields(Flags).Where(f => !f.IsStatic && f.FieldType.IsGenericType &&
            (f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>) || f.FieldType.GetGenericTypeDefinition() == typeof(List<>) || f.FieldType.GetGenericTypeDefinition() == typeof(HashSet<>))))
            field.SetValue(window, Activator.CreateInstance(field.FieldType));
        return window;
    }
    private static void Refresh(object window, CelestialBodyManager manager) => typeof(MainTabWindow_PFView).GetMethod("GetCelestialRenderBodies", Flags).Invoke(window, new object[] { manager, 0d });
    private static object Get(object window, string name) => typeof(MainTabWindow_PFView).GetField(name, Flags).GetValue(window);
    private static void Set(object window, string name, object value) => typeof(MainTabWindow_PFView).GetField(name, Flags).SetValue(window, value);
    private static IDictionary Cache(object window, string name) => (IDictionary)Get(window, name);
    private static Dictionary<int, CelestialViewCacheKey> Keys(object window) => (Dictionary<int, CelestialViewCacheKey>)Get(window, "celestialRenderKeys");
    private static void Seed(object window, int a, int b)
    {
        foreach (string cache in BodyCaches) { Cache(window, cache)[a] = null; Cache(window, cache)[b] = null; }
        Cache(window, "celestialCloudMaterials")[Keys(window)[a]] = null;
        Cache(window, "celestialCloudMaterials")[Keys(window)[a] with { ConfigurationHash = "stale-owner-configuration" }] = null;
        Cache(window, "celestialCloudMaterials")[Keys(window)[b]] = null;
        var draws = (List<(int Handle, Mesh Mesh, Matrix4x4 Matrix, Material Material)>)Get(window, "stellarHaloDraws");
        draws.Add((a, null, default, null)); draws.Add((b, null, default, null));
    }
    private static void AssertOwnerAbsent(object window, int owner)
    {
        foreach (string cache in BodyCaches) Assert.False(Cache(window, cache).Contains(owner), cache + " retained affected owner");
        Assert.DoesNotContain(Cache(window, "celestialCloudMaterials").Keys.Cast<CelestialViewCacheKey>(), key => Keys(window).TryGetValue(owner, out var current) && key.InstanceId == current.InstanceId);
        Assert.DoesNotContain((List<(int Handle, Mesh Mesh, Matrix4x4 Matrix, Material Material)>)Get(window, "stellarHaloDraws"), draw => draw.Handle == owner);
    }
    private static void AssertOwnerPresent(object window, int owner, CelestialViewCacheKey key)
    {
        foreach (string cache in BodyCaches) Assert.True(Cache(window, cache).Contains(owner), cache + " released unrelated owner");
        Assert.True(Cache(window, "celestialCloudMaterials").Contains(key));
        Assert.Contains((List<(int Handle, Mesh Mesh, Matrix4x4 Matrix, Material Material)>)Get(window, "stellarHaloDraws"), draw => draw.Handle == owner);
    }
}
