using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using PioneerFramework.Landing;
using RimWorld.Planet;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialSurfaceConsumerTests
{
    private static double currentSeconds;
    private static double? capturedImpact;
    private static bool destroyed;
    private static bool ReadSeconds(ref double __result) { __result = currentSeconds; return false; }
    private static bool CaptureTouchdown(double groundSpeed, ref bool __result)
    { capturedImpact = groundSpeed; __result = true; return false; }
    private static bool RejectDestruction() { destroyed = true; return false; }
    private static bool SkipNotice() => false;

    [Fact]
    public void RotationReadInPreparedWorkerDoesNotAcquireThePublicationLock()
    {
        var f = new CelestialViewAdapterTests.Fixture();
        var manager = new CelestialBodyManager(null);
        manager.AttachCelestialRuntime(f.Runtime);
        int handle = f.Runtime.Query(CelestialViewAdapterTests.Fixture.One).Value.Description.Handle;
        var prepared = f.Runtime.Coordinator.Prepare(new(f.Runtime.WorldSessionId, 1, 0, 1)).Value;
        using var scope = manager.UseCelestialInterval(prepared.Sampler);
        Task<double> worker = null;
        try
        {
            f.Runtime.WithPublication(() => {
                SpaceGameComp.ParallelPhysicsInProgress = true;
                try
                {
                    worker = Task.Run(() => Launch.GetSurfaceState.RotationOmega(manager, handle));
                    Assert.True(worker.Wait(TimeSpan.FromSeconds(2)), "Rotation read waited for the host publication lock.");
                    Assert.Equal(-2, worker.Result);
                }
                finally { SpaceGameComp.ParallelPhysicsInProgress = false; }
                return true;
            });
        }
        finally
        {
            worker?.Wait(TimeSpan.FromSeconds(3));
            f.Runtime.Coordinator.Abort(prepared);
        }
    }

    [Fact]
    public void ActualLandingQuoteUsesFractionalCommittedTimeAndFullSurfacePose()
    {
        using var f = new Fixture();
        var fixedPoint = Launch.GetSurfaceState.TileToFixedPosition(f.Layer, 0, f.Radius);
        var local = DVector3.Rotate(fixedPoint, f.Orientation);
        var offset = f.Manager.BodyFrameOffset(f.Handle, currentSeconds);
        var ship = new SpaceEntityGroup(910) { CelestialBodyId = f.Handle, IsActive = true, IsMovable = true,
            CurrentOrbit = new() { positionSystem = (SystemVec3)(offset.Position + local * 1.01),
                velocitySystem = (SystemVec3)(offset.Velocity + DVector3.Cross(f.AngularVelocity, local)) } };
        f.Space.TryQuoteLanding(ship, f.Handle, new PlanetTile(0, f.Layer), out var quote, out _, out _, out _, out _);
        Assert.True(quote.ok, quote.reason);
        Assert.Equal(offset.Position + local, quote.tilePos);
        Assert.Equal(offset.Velocity + DVector3.Cross(f.AngularVelocity, local), quote.tileVel);
        Assert.Equal(0, quote.deltaV);
        Assert.Equal(0, Launch.GetSurfaceState.EciToTile(local, f.Manager, f.Handle, currentSeconds));
        Assert.Equal(0.6, f.Runtime.CurrentStamp.PhysicalSeconds);
    }

    [Fact]
    public void ActualCollisionAndAirContextUseFullAngularVelocity()
    {
        using var f = new Fixture();
        var local = DVector3.Rotate(new DVector3(0, 0, 1 - f.Radius), f.Orientation);
        var velocity = DVector3.Cross(f.AngularVelocity, local);
        var offset = f.Manager.BodyFrameOffset(f.Handle, currentSeconds);
        var ship = new SpaceEntityGroup(911) { CelestialBodyId = f.Handle, IsActive = true, IsMovable = true,
            CurrentOrbit = new() { positionSystem = (SystemVec3)(offset.Position + local),
                velocitySystem = (SystemVec3)(offset.Velocity + velocity) } };
        var patch = new Harmony("pfl.tests.celestial.surface-collision");
        patch.Patch(AccessTools.Method(typeof(SurfaceTouchdownService), nameof(SurfaceTouchdownService.TryHandle)),
            prefix: new HarmonyMethod(typeof(CelestialSurfaceConsumerTests), nameof(CaptureTouchdown)));
        patch.Patch(AccessTools.Method(typeof(SpaceGameComp), nameof(SpaceGameComp.DestroyGroup)),
            prefix: new HarmonyMethod(typeof(CelestialSurfaceConsumerTests), nameof(RejectDestruction)));
        patch.Patch(AccessTools.Method(typeof(Messages), nameof(Messages.Message), new[] { typeof(string), typeof(MessageTypeDef), typeof(bool) }),
            prefix: new HarmonyMethod(typeof(CelestialSurfaceConsumerTests), nameof(SkipNotice)));
        capturedImpact = null; destroyed = false;
        try
        {
            AccessTools.Method(typeof(SpaceGameComp), "CheckPlanetCollision").Invoke(f.Space, new object[] { ship, currentSeconds });
            Assert.False(destroyed);
            Assert.True(capturedImpact.HasValue);
            Assert.Equal(0, capturedImpact.Value, 8);
            object[] arguments = { ship, f.Manager, offset.Position + local * 1.001,
                offset.Velocity + DVector3.Cross(f.AngularVelocity, local * 1.001), 0d, DVector3.zero, currentSeconds };
            Assert.True((bool)AccessTools.Method(typeof(SpaceGameComp), "TryGetAeroContext").Invoke(f.Space, arguments));
            Assert.True((double)arguments[4] > 0);
            Assert.True(((DVector3)arguments[5]).magnitude < 1e-8);
        }
        finally { patch.UnpatchAll(patch.Id); }
    }

    [Fact]
    public void ActualHoverLockUsesProviderFixedCoordinates()
    {
        using var f = new Fixture();
        var fixedPoint = SurfaceFrame.SurfaceState(f.Radius + 100, 0.2, 0.7, 0, 0).pos;
        var local = DVector3.Rotate(fixedPoint, f.Orientation);
        var offset = f.Manager.BodyFrameOffset(f.Handle, currentSeconds);
        var ship = new SpaceEntityGroup(912) { CelestialBodyId = f.Handle, IsActive = true, IsMovable = true,
            CurrentOrbit = new() { positionSystem = (SystemVec3)(offset.Position + local),
                velocitySystem = (SystemVec3)(offset.Velocity + DVector3.Cross(f.AngularVelocity, local)) } };
        try
        {
            Assert.True((bool)AccessTools.Method(typeof(SpaceGameComp), "SetHoverCore").Invoke(f.Space, new object[] { ship, true }));
            Assert.Equal(0.2, ship.hoverLatRad, 12);
            Assert.Equal(0.7, ship.hoverLonRad0, 12);
            Assert.Equal(100, ship.hoverAltitudeMeters, 8);
        }
        finally { AccessTools.Method(typeof(SpaceGameComp), "SetHoverCore").Invoke(f.Space, new object[] { ship, false }); }
    }

    [Fact]
    public void AnalyticHoverAndTransferWindowFollowTheSameProviderPose()
    {
        using var f = new Fixture();
        var fixedPoint = SurfaceFrame.SurfaceState(f.Radius + 100, 0.2, 0.7, 0, 0).pos;
        var local = DVector3.Rotate(fixedPoint, f.Orientation);
        var offset = f.Manager.BodyFrameOffset(f.Handle, currentSeconds);
        var ship = new SpaceEntityGroup(913) { CelestialBodyId = f.Handle, IsActive = true, IsMovable = true,
            hoverActive = true, hoverAnalytic = true, hoverLatRad = 0.2, hoverLonRad0 = 0.7, hoverAltitudeMeters = 100,
            CurrentOrbit = new() { positionSystem = (SystemVec3)(offset.Position + local),
                velocitySystem = (SystemVec3)(offset.Velocity + DVector3.Cross(f.AngularVelocity, local)) } };
        AccessTools.Method(typeof(SpaceGameComp), "EnsureGravityScratchCapacity").Invoke(f.Space, new object[] { 1 });
        var scratch = AccessTools.Method(typeof(SpaceGameComp), "GetGravityScratch").Invoke(f.Space, new object[] { 0 });
        var result = AccessTools.Method(typeof(SpaceGameComp), "ComputeGroupPhysics").Invoke(f.Space,
            new object[] { ship, 0.1, currentSeconds, f.Manager, scratch, 1, currentSeconds });
        Assert.True((bool)AccessTools.Field(result.GetType(), "NeedsUpdate").GetValue(result));
        Assert.True(((DVector3)AccessTools.Field(result.GetType(), "NewPosition").GetValue(result) - (offset.Position + local)).magnitude < 1e-8);
        Assert.True(((DVector3)AccessTools.Field(result.GetType(), "NewVelocity").GetValue(result) -
            (offset.Velocity + DVector3.Cross(f.AngularVelocity, local))).magnitude < 1e-8);

        var window = (Window_Transfer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Window_Transfer));
        // 批 5e §4.3b：窗口几何刷新改吃快照（与生产同一个取数口）；这里按船的当前态构造一份。
        var craft = new SpacecraftSnapshot
        {
            GroupId = ship.GroupId,
            CelestialBodyId = ship.CelestialBodyId,
            PositionSystem = ship.CurrentOrbit.positionSystem.v,
            VelocitySystem = ship.CurrentOrbit.velocitySystem.v,
        };
        AccessTools.Method(typeof(Window_Transfer), "RefreshGeometry").Invoke(window, new object[] { craft, f.Manager, null, currentSeconds });
        Assert.Equal(0.2 * 180 / Math.PI, (float)AccessTools.Field(typeof(Window_Transfer), "shipLatDeg").GetValue(window), 4);
        Assert.Equal(0.7 * 180 / Math.PI, (float)AccessTools.Field(typeof(Window_Transfer), "shipLonDeg").GetValue(window), 4);
        Assert.True((float)AccessTools.Field(typeof(Window_Transfer), "shipGroundSpeed").GetValue(window) < 1e-8);
        var rotation = (DQuaternion)PFViewBodyRotation.FixedToEciRotation(f.Handle);
        Assert.True((DVector3.Rotate(new DVector3(0, 0, -1), rotation) - DVector3.Rotate(new DVector3(0, 0, -1), f.Orientation)).magnitude < 1e-6);
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly CelestialWorldBootstrapTests.Fixture world = new();
        private readonly Harmony clock = new("pfl.tests.celestial.surface-clock");
        public CelestialWorldBootstrapScope Scope { get; }
        public CelestialRuntime Runtime => Scope.Runtime;
        public CelestialBodyManager Manager => world.Native.Game.GetComponent<CelestialBodyManager>();
        public SpaceGameComp Space { get; }
        public CelestialSurfaceLayer Layer { get; }
        public int Handle => Runtime.Query(Layer.Header.OwnerId).Value.Description.Handle;
        public double Radius => Layer.Header.PhysicalRadiusMeters;
        public DQuaternion Orientation { get; } = new(Math.Sqrt(0.5), 0, 0, Math.Sqrt(0.5));
        public DVector3 AngularVelocity { get; } = new(0.01, 0, 0);
        public Fixture()
        {
            currentSeconds = 0.6;
            clock.Patch(AccessTools.Method(typeof(TimeScaleManager), nameof(TimeScaleManager.GetPhysicsTickSafe)),
                prefix: new HarmonyMethod(typeof(CelestialSurfaceConsumerTests), nameof(ReadSeconds)));
            var atmosphere = new CompProperties_Atmosphere { key = "air", rho0 = new() { value = 1.2 },
                scaleHeight = new() { value = 8000 }, topAltitude = new() { value = 100000 } }.ToDefinition();
            var entries = world.System.Entries.Select(entry => new CelestialSystemEntry(entry.Key,
                CelestialBodyTemplate.Create(entry.Body.DefinitionId, entry.Body.Label, entry.Body.Category, entry.Body.Tags,
                    entry.Body.Components.Append(atmosphere)).Value, entry.Count, initializationReferences:
                    new Dictionary<string, CelestialSpawnReference> { ["frameOrigin"] = new("optional", 0) })).ToArray();
            var system = new CelestialSystemTemplate(world.System.DefinitionId, "chosen", entries);
            Scope = CelestialWorldBootstrap.Begin(system, 17, request => {
                var bridge = new DeterministicTestPhysicsBridge(request.WorldSessionId, request.InitialSeconds);
                var states = request.Manifest.Items.ToDictionary(item => item.Id, item => new CelestialMotionState(1,
                    item.EntryKey == "chosen" ? new DVector3(100, 200, 300) : DVector3.zero,
                    item.EntryKey == "chosen" ? new DVector3(1, 2, 3) : DVector3.zero, Orientation, AngularVelocity));
                bridge.SetFrame(0, states); bridge.SetFrame(currentSeconds, states); bridge.SetFrame(1, states);
                return CelestialResult.Ok(new CelestialPhysicsConnection(bridge, bridge.CommittedStamp));
            }).Value;
            world.Native.Grid.GenerateFreshWorld();
            Layer = (CelestialSurfaceLayer)world.Native.Grid.Surface;
            WorldGenerator.GeneratePlanetLayer(Layer, "surface-consumers", 1);
            Assert.True(Scope.Complete().Success);
            Assert.True(Runtime.Advance(new(Runtime.WorldSessionId, Runtime.CurrentStamp.TopologyRevision, 0, currentSeconds)).Success);
            Space = new SpaceGameComp(world.Native.Game);
            world.Native.Game.components.Add(Space);
        }
        public void Dispose() { clock.UnpatchAll(clock.Id); Scope.Dispose(); world.Dispose(); }
    }
}
