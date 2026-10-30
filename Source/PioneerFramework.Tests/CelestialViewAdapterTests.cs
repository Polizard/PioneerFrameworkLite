using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialViewAdapterTests
{
    [Fact]
    public void PhysicalGeometryAndAuthoritativeOrientationRemainIndependentOfNativeDisplayRadius()
    {
        var f = new Fixture();
        var a = f.Adapter.Capture(Fixture.One, f.Runtime.CurrentStamp).Value;
        var b = f.Adapter.Capture(Fixture.Two, f.Runtime.CurrentStamp).Value;
        Assert.Equal(100, a.NativeDisplayRadius);
        Assert.Equal(100, b.NativeDisplayRadius);
        Assert.Equal(1000, a.Description.RadiusMeters);
        Assert.Equal(2000, b.Description.RadiusMeters);
        var point = new DVector3(1000, 0, 0);
        var system = a.FixedPointToSystem(point);
        Assert.Equal(new DVector3(-992, 16, 32), system);
        Assert.Equal(point, a.SystemPointToFixed(system));
        Assert.Equal(new DVector3(5, 6, 2007), a.FixedPointVelocity(point));
        var state = Launch.GetSurfaceState.SystemSurfaceState(a, point);
        Assert.Equal(system, state.pos); Assert.Equal(new DVector3(5, 6, 2007), state.vel);
        Assert.Equal(new Vector3(100, 0, 0), ViewFrame.ToViewUnity(system, a));
        Assert.Empty(f.Runtime.Creation.GetProperties(Fixture.StarOne).OfType<SurfaceResolvedProperties>());
        Assert.Null(f.Runtime.Maps);
        Assert.Null(f.Runtime.Surfaces);
    }
    [Fact]
    public void InstanceAndSessionOwnCachesWhileCommittedMotionRefreshesWithoutRebaking()
    {
        var f = new Fixture();
        var stamp = f.Runtime.CurrentStamp;
        var first = f.Adapter.Capture(Fixture.One, stamp).Value;
        var second = f.Adapter.Capture(Fixture.Two, stamp).Value;
        Assert.NotEqual(first.CacheKey, second.CacheKey);
        Assert.True(f.Runtime.Advance(new(f.Runtime.WorldSessionId, stamp.TopologyRevision, 0, 1)).Success);
        var moved = f.Adapter.Capture(Fixture.One, f.Runtime.CurrentStamp).Value;
        Assert.Equal(first.CacheKey, moved.CacheKey);
        Assert.NotEqual(first.Motion.Position, moved.Motion.Position);
        Assert.False(f.Adapter.Capture(Fixture.One, stamp).Success);
        var nextWorld = new Fixture();
        Assert.False(nextWorld.Adapter.Capture(Fixture.One, f.Runtime.CurrentStamp).Success);
        Assert.NotEqual(first.CacheKey, nextWorld.Adapter.Capture(Fixture.One, nextWorld.Runtime.CurrentStamp).Value.CacheKey);
    }
    [Fact]
    public void PresentationUsesTheRequestedCommittedHistoryTimeAndNeverExtrapolates()
    {
        var f = new Fixture();
        Assert.True(f.Runtime.Advance(new(f.Runtime.WorldSessionId, f.Runtime.CurrentStamp.TopologyRevision, 0, 1)).Success);
        var sampled = f.Adapter.CapturePresentation(Fixture.One, 0.5);
        Assert.True(sampled.Success, string.Join(",", sampled.Errors.Select(error => error.Code)));
        var middle = sampled.Value;
        Assert.Equal(0.5, middle.Stamp.PhysicalSeconds);
        Assert.Equal(new DVector3(8.5, 16, 32), middle.Motion.Position);
        Assert.False(f.Adapter.CapturePresentation(Fixture.One, 1.1).Success);
    }
    [Fact]
    public void NativeDisplayConstantCannotOverwriteTheSelectedRootPhysicalRadius()
    {
        double previousRadius = SpaceConstants.PlanetRadiusMeters;
        double previousGm = SpaceConstants.PrimaryGravitationalParameter;
        double previousAtmosphere = SpaceConstants.AtmosphereThicknessMeters;
        try
        {
            using var fixture = new CelestialMapIdentityTests.Fixture();
            SpaceConstants.SyncWithWorldGrid();
            Assert.Equal(1500000, SpaceConstants.PlanetRadiusMeters);
            Assert.Equal(0, SpaceConstants.PrimaryGravitationalParameter);
            Assert.Equal(0, SpaceConstants.AtmosphereThicknessMeters);
        }
        finally
        {
            SpaceConstants.PlanetRadiusMeters = previousRadius;
            SpaceConstants.PrimaryGravitationalParameter = previousGm;
            SpaceConstants.AtmosphereThicknessMeters = previousAtmosphere;
        }
    }
    [Fact]
    public void NativeLayerMappingUsesSavedOwnerInsteadOfSharedDefinitionNames()
    {
        using var fixture = new CelestialMapIdentityTests.Fixture();
        var runtime = fixture.Scope.Runtime;
        var manager = fixture.Native.Game.GetComponent<CelestialBodyManager>();
        var owners = fixture.Scope.Manifest.Items.Where(item => item.Id != fixture.Root.Header.OwnerId).Select(item => item.Id).ToArray();
        var layers = owners.Select((owner, index) => runtime.RequestSurface(new(new("surface-" + index, runtime.WorldSessionId, runtime.Catalog.Revision), owner)).Handle).ToArray();
        Assert.Equal(2, layers.Length);
        Assert.NotEqual(layers[0].LayerId, layers[1].LayerId);
        for (int i = 0; i < owners.Length; i++)
        {
            var layer = fixture.Native.Grid.PlanetLayers[layers[i].LayerId];
            int handle = runtime.Query(owners[i]).Value.Description.Handle;
            Assert.Same(layer, Launch.GetSurfaceState.GetLayerForBody(manager, handle));
            Assert.Equal(handle, Launch.GetSurfaceState.GetBodyForLayer(manager, layer));
            int otherHandle = runtime.Query(owners[1 - i]).Value.Description.Handle;
            Assert.False(PFViewSurfaceBake.TryLoadSaved(1, otherHandle, layer, out string message));
            Assert.Equal("SurfaceIdentityMismatch", message);
        }
    }
    internal sealed class Fixture
    {
        public static readonly CelestialInstanceId One = CelestialInstanceId.Parse("view:one"), Two = CelestialInstanceId.Parse("view:two"),
            StarOne = CelestialInstanceId.Parse("view:star-a"), StarTwo = CelestialInstanceId.Parse("view:star-b");
        public CelestialRuntime Runtime { get; }
        public CelestialViewAdapter Adapter { get; }
        public Fixture()
        {
            var profile = new CelestialVisualProfileDef { defName = "PF_ProbeVisual", color = new Color(0.2f, 0.5f, 0.8f, 1), shellHeightMeters = 10000 };
            CelestialSpawnItem Spawn(CelestialInstanceId id, double radius, double scale, string category, bool clouds, bool stellar)
            {
                var comps = new List<CelestialCompProperties> {
                    new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { value = radius } },
                    new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = DeterministicTestPhysicsBridge.Id, modeId = "replay" },
                    new CompProperties_CelestialVisual { key = "base", profile = profile } };
                if (!stellar) comps.Add(new CompProperties_Surface { key = "surface", metersPerLayerUnit = new() { value = scale }, subdivisions = new() { value = 0 } });
                if (clouds) comps.Add(new CompProperties_CloudsVisual { key = "clouds", profile = profile });
                if (stellar) comps.Add(new CompProperties_StellarVisual { key = "stellar", profile = profile });
                var template = new CelestialBodyDef { defName = stellar ? "SharedStar" : "SharedPlanet", category = category, comps = comps }.ToTemplate().Value;
                return new(id, id.Value, 0, id.Value, template, template.Resolve(new(1, id.Value)).Value);
            }
            var items = new[] { Spawn(One, 1000, 10, "Rock", true, false), Spawn(Two, 2000, 20, "Earth", false, false),
                Spawn(StarOne, 3000, 50, "Rock", false, true), Spawn(StarTwo, 4000, 50, "Moon", false, true) };
            var bridge = new DeterministicTestPhysicsBridge(Guid.NewGuid());
            Dictionary<CelestialInstanceId, CelestialMotionState> States(double seconds) => items.ToDictionary(item => item.Id, item => new CelestialMotionState(1,
                new DVector3(8 + seconds, 16, 32), new(5, 6, 7), new(0, 1, 0, 0), new(0, 2, 0)));
            bridge.SetFrame(0, States(0)); bridge.SetFrame(0.5, States(0.5)); bridge.SetFrame(1, States(1));
            Runtime = new(bridge, bridge.CommittedStamp, new EmptyResources(), 1, _ => throw new Exception("No implicit creation"));
            Assert.Equal(CelestialOperationStatus.Pending, Runtime.CreateBatch(new(new("view", Runtime.WorldSessionId, 0), new("view", 1, null, items))).Status);
            Assert.True(Runtime.Advance(new(Runtime.WorldSessionId, 0, 0, 0)).Success);
            Adapter = new(Runtime);
        }
    }
    private sealed class EmptyResources : ICelestialRemovalResources
    {
        public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id) => Array.Empty<CelestialError>();
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id) => CelestialResult.Ok(true);
    }
}
