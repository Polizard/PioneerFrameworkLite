using System;
using System.Linq;
using System.Runtime.CompilerServices;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialConsumerIntegrationTests
{
    [Fact]
    public void PhysicsWorkersCanSamplePreparedSubstepsWithoutPublishingOrTakingTheHostLock()
    {
        var f = new CelestialViewAdapterTests.Fixture();
        var manager = new CelestialBodyManager(null);
        manager.AttachCelestialRuntime(f.Runtime);
        int handle = f.Runtime.Query(CelestialViewAdapterTests.Fixture.One).Value.Description.Handle;
        var prepared = f.Runtime.Coordinator.Prepare(new(f.Runtime.WorldSessionId, 1, 0, 1)).Value;
        try
        {
            using (manager.UseCelestialInterval(prepared.Sampler))
            {
                f.Runtime.WithPublication(() => {
                    SpaceGameComp.ParallelPhysicsInProgress = true;
                    try
                    {
                        var work = System.Threading.Tasks.Task.Run(() => manager.GetBodyPositionAbs(handle, 0.5));
                        Assert.True(work.Wait(TimeSpan.FromSeconds(3)), "Physics worker tried to acquire the main publication lock.");
                        Assert.Equal(new DVector3(8.5, 16, 32), work.Result);
                    }
                    finally { SpaceGameComp.ParallelPhysicsInProgress = false; }
                    return true;
                });
            }
            Assert.Equal(0, f.Runtime.CurrentStamp.PhysicalSeconds);
            Assert.Throws<InvalidOperationException>(() => manager.GetBodyPositionAbs(handle, 0.5));
        }
        finally { f.Runtime.Coordinator.Abort(prepared); }
    }

    [Fact]
    public void QueuedMegastructureActionsRecheckSessionAndStructureRevision()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var core = new Megastructures.MegastructureRuntime();
        var state = new Megastructures.StructureState { Id = "queued", Revision = 1, Lifecycle = Megastructures.StructureLifecycle.Active,
            Nodes = new() { new() { Id = "base", Spatial = new() { Mode = Megastructures.SpatialMode.BodyAnchored,
                BodyId = f.Scope.Runtime.Query(f.Root.Header.OwnerId).Value.Description.Handle } } } };
        Assert.True(core.Restore(new Megastructures.FrameworkDocument { Instances = new() { state } }).Success);
        var manager = new Megastructures.MegastructureManager(f.Native.Game);
        HarmonyLib.AccessTools.Field(typeof(Megastructures.MegastructureManager), "runtime").SetValue(manager, core);
        HarmonyLib.AccessTools.Field(typeof(Megastructures.MegastructureManager), "initialized").SetValue(manager, true);
        f.Native.Game.components.Add(manager);
        var validate = Megastructures.MegastructureDeferredGuard.Capture(manager, state);
        Assert.True(validate().Success);
        var originalGame = Verse.Current.Game;
        try
        {
            Verse.Current.Game = null;
            Assert.Equal("StaleSession", validate().Code);
        }
        finally { Verse.Current.Game = originalGame; }
        state.Revision = 2;
        Assert.True(core.Restore(new Megastructures.FrameworkDocument { Instances = new() { state } }).Success);
        Assert.Equal("RevisionConflict", validate().Code);
    }

    [Fact]
    public void AbsentGravityDoesNotInheritTheDefaultWorldGravity()
    {
        var f = new CelestialViewAdapterTests.Fixture();
        var manager = new CelestialBodyManager(null);
        manager.AttachCelestialRuntime(f.Runtime);
        int handle = f.Runtime.Query(CelestialViewAdapterTests.Fixture.One).Value.Description.Handle;
        var sources = new System.Collections.Generic.List<GravitySource>();
        manager.CollectSystemSources(handle, 0, sources);
        Assert.Equal(0, Assert.Single(sources).mu);
    }

    [Fact]
    public void SystemMembershipIncludesAllInstancesBeyondTheOldFixedLimit()
    {
        var f = new CelestialViewAdapterTests.Fixture();
        var root = CelestialViewAdapterTests.Fixture.One;
        var template = new CelestialBodyDef { defName = "PF_ManyBodies", comps = new() {
            new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { value = 10 } },
            new CompProperties_ExternalPhysicsBinding { key = "physics", providerId = "PFL.Tests.Replay", modeId = "replay" } } }.ToTemplate().Value;
        var children = Enumerable.Range(0, 80).Select(i => {
            string key = "child" + i;
            return new CelestialSpawnItem(CelestialInstanceId.Parse(key), key, 0, key, template, template.Resolve(new(1, key)).Value, root);
        }).ToArray();
        var states = f.Runtime.CaptureCatalog().Bodies.Select(body => body.Id).Concat(children.Select(child => child.Id)).ToDictionary(id => id,
            _ => new CelestialMotionState(1, DVector3.zero, DVector3.zero, DQuaternion.identity, DVector3.zero));
        ((PioneerFramework.TestSupport.Celestial.DeterministicTestPhysicsBridge)f.Runtime.Physics).SetFrame(0, states);
        f.Runtime.CreateBatch(new(new("many", f.Runtime.WorldSessionId, f.Runtime.CaptureCatalog().Revision), new("many", 1, null, children)));
        Assert.True(f.Runtime.Advance(new(f.Runtime.WorldSessionId, 1, 0, 0)).Success);
        var manager = new CelestialBodyManager(null);
        manager.AttachCelestialRuntime(f.Runtime);
        Assert.Equal(81, manager.GetSystemMembers(f.Runtime.Query(root).Value.Description.Handle).Count);
    }

    [Fact]
    public void NativeLandingAndExistingMegastructureAnchorsProtectOnlyTheirBoundInstance()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var game = f.Native.Game;
        var root = f.Root.Header.OwnerId;
        var other = f.Scope.Runtime.CaptureCatalog().Bodies.First(body => body.Id != root);
        int rootHandle = f.Scope.Runtime.Query(root).Value.Description.Handle;
        var lander = new Landing.LandingTransitManager(game);
        var task = new Landing.LandingTaskData { TargetBodyId = rootHandle, Status = Landing.LandingTransitStatus.InFlight };
        lander.Tasks.Add(task); game.components.Add(lander);
        var core = new Megastructures.MegastructureRuntime();
        Assert.True(core.Restore(new Megastructures.FrameworkDocument { Instances = new() { new() { Id = "anchor", Lifecycle = Megastructures.StructureLifecycle.Active,
            Nodes = new() { new() { Id = "base", Spatial = new() { Mode = Megastructures.SpatialMode.BodyAnchored, BodyId = rootHandle } } } } } }).Success);
        var mega = new Megastructures.MegastructureManager(game);
        HarmonyLib.AccessTools.Field(typeof(Megastructures.MegastructureManager), "runtime").SetValue(mega, core);
        HarmonyLib.AccessTools.Field(typeof(Megastructures.MegastructureManager), "initialized").SetValue(mega, true);
        game.components.Add(mega);
        Assert.Contains(f.Scope.Runtime.CanRemove(root).Blockers, error => error.Code == "TransportActive" && error.InstanceId == root);
        Assert.Contains(f.Scope.Runtime.CanRemove(root).Blockers, error => error.Code == "MegastructureAttached" && error.InstanceId == root);
        Assert.DoesNotContain(f.Scope.Runtime.CanRemove(other.Id).Blockers, error => error.Code is "TransportActive" or "MegastructureAttached");
        task.Status = Landing.LandingTransitStatus.Completed;
        Assert.True(core.Restore(new Megastructures.FrameworkDocument()).Success);
        Assert.DoesNotContain(f.Scope.Runtime.CanRemove(root).Blockers, error => error.Code is "TransportActive" or "MegastructureAttached");
    }

    [Fact]
    public void LandingUsesTheOwnedMapServiceAndRejectsAnotherInstanceAtTheSameNativeTile()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var created = f.Request("initial-map", CelestialMapAction.Generate);
        Assert.True(f.Service.TryGetNativeMap(created.Handle.MapId, out var existing));
        int handle = f.Scope.Runtime.Query(f.Root.Header.OwnerId).Value.Description.Handle;
        var other = f.Scope.Runtime.CaptureCatalog().Bodies.First(body => body.Handle != handle);
        var task = new Landing.LandingTaskData { TargetTile = new RimWorld.Planet.PlanetTile(0, f.Root), TargetBodyId = other.Handle };
        Assert.Throws<InvalidOperationException>(() => Landing.LandingDestinationRuntime.Generate(task, new(250, 250)));
        task.TargetBodyId = handle;
        Assert.Same(existing, Landing.LandingDestinationRuntime.Generate(task, new(250, 250)));
        Assert.Equal(existing.Parent.ID, task.DestinationParentId);
        Assert.Single(f.Scope.Runtime.Maps.CaptureSites());
    }

    [Fact]
    public void DeferredTargetRejectsAnotherWorldAndRemovingBodies()
    {
        var first = new CelestialViewAdapterTests.Fixture();
        var manager = new CelestialBodyManager(null);
        manager.AttachCelestialRuntime(first.Runtime);
        var id = CelestialViewAdapterTests.Fixture.One;
        int handle = first.Runtime.Query(id).Value.Description.Handle;
        var token = manager.CaptureTarget(handle).Value;
        Assert.Equal(id, token.InstanceId);
        Assert.True(manager.ValidateTarget(token).Success);
        var second = new CelestialViewAdapterTests.Fixture();
        var other = new CelestialBodyManager(null);
        other.AttachCelestialRuntime(second.Runtime);
        Assert.Equal("StaleSession", Assert.Single(other.ValidateTarget(token).Errors).Code);
        Assert.Equal(CelestialOperationStatus.Pending, first.Runtime.Remove(new(new("remove-target", first.Runtime.WorldSessionId, first.Runtime.CaptureCatalog().Revision), id)).Status);
        Assert.Equal("BodyNotReady", Assert.Single(manager.ValidateTarget(token).Errors).Code);
    }

    [Fact]
    public void TemplateHasNoRuntimeScalarFieldsAndConsumersProtectTheirActualTarget()
    {
        Assert.Null(typeof(CelestialBodyDef).GetField("bodyId"));
        Assert.Null(typeof(CelestialBodyDef).GetField("radius"));
        Assert.Null(typeof(CelestialBodyDef).GetField("moonBodies"));
        var f = new CelestialViewAdapterTests.Fixture();
        var manager = new CelestialBodyManager(null);
        manager.AttachCelestialRuntime(f.Runtime);
        var one = CelestialViewAdapterTests.Fixture.One;
        var two = CelestialViewAdapterTests.Fixture.Two;
        int handle = f.Runtime.Query(one).Value.Description.Handle;
        manager.RegisterGroupToBody(300, handle);
        Assert.Contains(f.Runtime.CanRemove(one).Blockers, error => error.Code == "ShipAttached" && error.InstanceId == one);
        Assert.DoesNotContain(f.Runtime.CanRemove(two).Blockers, error => error.Code == "ShipAttached");
        manager.UnregisterGroupFromBody(300);
        Assert.DoesNotContain(f.Runtime.CanRemove(one).Blockers, error => error.Code == "ShipAttached");
    }

    [Fact]
    public void ExistingMegastructureSpatialAdapterUsesProviderAttitudeAndAngularVelocity()
    {
        using var host = new SpaceAiRuntimeTestHost();
        var f = new CelestialViewAdapterTests.Fixture();
        var manager = host.Game.GetComponent<CelestialBodyManager>();
        manager.AttachCelestialRuntime(f.Runtime);
        int handle = f.Runtime.Query(CelestialViewAdapterTests.Fixture.One).Value.Description.Handle;
        Assert.True(new Megastructures.GameStructureSpatialHost().TryGetBody(handle, 0, out var state));
        Assert.Equal(new Megastructures.RotationData(0, 1, 0, 0), state.Rotation);
        Assert.Equal(new DVector3(0, 2, 0), state.AngularVelocity);
        var surface = Launch.GetSurfaceState.FixedSurfaceState(manager, handle, new DVector3(10, 0, 0), 0);
        Assert.Equal(new DVector3(-10, 0, 0), surface.pos);
        Assert.Equal(new DVector3(0, 0, 20), surface.vel);
    }

    [Fact]
    public void ManagerAndOrbitAdapterReturnImmutableDistinctInstanceFacts()
    {
        var f = new CelestialViewAdapterTests.Fixture();
        var manager = new CelestialBodyManager(null);
        Assert.True(manager.AttachCelestialRuntime(f.Runtime).Success);
        var a = manager.GetBodyById(f.Runtime.Query(CelestialViewAdapterTests.Fixture.One).Value.Description.Handle);
        var b = manager.GetBodyById(f.Runtime.Query(CelestialViewAdapterTests.Fixture.Two).Value.Description.Handle);
        Assert.IsType<CelestialBodyInfo>(a);
        Assert.Equal(CelestialViewAdapterTests.Fixture.One, a.InstanceId);
        Assert.Equal(CelestialViewAdapterTests.Fixture.Two, b.InstanceId);
        Assert.Equal(a.defName, b.defName);
        Assert.Equal(1000, a.radius); Assert.Equal(2000, b.radius);
        Assert.Equal(1000, OrbitAdapter.ToBodyView(a, manager).radius);
        Assert.Equal(2000, OrbitAdapter.ToBodyView(b, manager).radius);
        var setter = typeof(CelestialBodyInfo).GetProperty(nameof(CelestialBodyInfo.radius)).SetMethod;
        Assert.Contains(typeof(IsExternalInit), setter.ReturnParameter.GetRequiredCustomModifiers());
        Assert.False(typeof(Verse.Def).IsAssignableFrom(typeof(CelestialBodyInfo)));
    }

    [Fact]
    public void ManagerReadsProviderMotionAndCannotDeriveStateFromCategoryOrSharedDefinition()
    {
        var f = new CelestialViewAdapterTests.Fixture();
        var manager = new CelestialBodyManager(null);
        manager.AttachCelestialRuntime(f.Runtime);
        int handle = f.Runtime.Query(CelestialViewAdapterTests.Fixture.One).Value.Description.Handle;
        Assert.Equal(new DVector3(8, 16, 32), manager.GetBodyPositionAbs(handle, 0));
        Assert.Equal(new DVector3(5, 6, 7), manager.GetBodyVelocityAbs(handle, 0));
        Assert.Equal(new DVector3(8, 16, 32), manager.GetBodyPositionParent(handle, 0));
        Assert.Equal(new DVector3(5, 6, 7), manager.GetBodyVelocityParent(handle, 0));
        Assert.Equal(0, f.Runtime.CurrentStamp.PhysicalSeconds);
        Assert.Throws<InvalidOperationException>(() => manager.GetBodyPositionAbs(handle, 1));
        Assert.Equal(0, f.Runtime.CurrentStamp.PhysicalSeconds);
    }
}
