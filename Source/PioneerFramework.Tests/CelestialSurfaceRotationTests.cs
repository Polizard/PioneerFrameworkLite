using System;
using System.Collections.Generic;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

/// <summary>
/// 2026-11-11：曲面位姿改走提供方四元数（<c>UsesProviderPose</c> 不再排除既有星历桥）后的两类探针。
///
/// 1. **倾斜自转轴**：固定在地表的本初子午线赤道点，其经纬度必须随时间不变。
///    旧实现（桥被排除，只按 `−AngularVelocity.y` 做 Ry）对倾斜轴会把纬经度算漂——这条用例先自证。
/// 2. **轴=+Y 回归**：共转速度必须与旧标量算式 `SurfaceFrame.SurfaceVelocity(p, ω)` 同值，
///    保证这次改动没有动既有四种天体的行为。
/// </summary>
public class CelestialSurfaceRotationTests
{
    private const double Radius = 1000.0;
    private static readonly double[] Times = { 0.0, 2.5, 10.0, 17.25, 39.75, 100.5 };

    private static (CelestialRuntime Runtime, CelestialBodyManager Manager, int Handle) Probe(double axisX, double axisY, double axisZ)
    {
        var id = CelestialInstanceId.Parse("tilt:body");
        var definition = new CelestialBodyDef {
            defName = "TiltProbeBody", comps = new() {
                new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { value = Radius } },
                new CompProperties_ExternalPhysicsBinding {
                    key = "motion", providerId = ExistingCelestialEphemerisBridge.Id,
                    motion = new() {
                        orbitRadiusMeters = 0, orbitPeriodSeconds = 0, bodyType = "Planet", frameOriginReference = "frameOrigin",
                        rotation = new() { axisX = axisX, axisY = axisY, axisZ = axisZ, periodSeconds = 40 } } } } };
        var template = definition.ToTemplate().Value;
        var item = new CelestialSpawnItem(id, id.Value, 0, id.Value, template, template.Resolve(new(7, id.Value)).Value);
        var provider = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var runtime = new CelestialRuntime(provider, provider.CommittedStamp, new EmptyResources(), 7, _ => throw new InvalidOperationException());
        runtime.CreateBatch(new(new("tilt", runtime.WorldSessionId, 0), new("tilt", 7, null, new[] { item })));
        Assert.True(runtime.Advance(new(runtime.WorldSessionId, 0, 0, 0)).Success);
        var manager = new CelestialBodyManager(null);
        manager.AttachCelestialRuntime(runtime);
        return (runtime, manager, runtime.Query(id).Value.Description.Handle);
    }

    private static void AdvanceTo(CelestialRuntime runtime, double seconds)
    {
        var committed = runtime.CurrentStamp;
        var result = runtime.Advance(new(runtime.WorldSessionId, committed.TopologyRevision, committed.PhysicalSeconds, seconds));
        Assert.True(result.Success, $"advance to {seconds} failed: " +
            string.Join(",", System.Linq.Enumerable.Select(result.Errors, error => error.Code + ":" + error.Detail)));
    }

    [Theory]
    [InlineData(0.5, 0.8660254037844386, 0.0)]   // 30° 倾斜
    [InlineData(1.0, 0.0, 0.0)]                  // 90° 倾斜（极轴在赤道面内）
    public void TiltedSpinKeepsFixedSurfacePointLatLonInvariant(double axisX, double axisY, double axisZ)
    {
        var (runtime, manager, handle) = Probe(axisX, axisY, axisZ);
        var fixedPoint = new DVector3(0, 0, -Radius);   // 本初子午线 × 赤道（SurfaceFrame: λ=0 ⇒ −Z）
        foreach (double seconds in Times)
        {
            AdvanceTo(runtime, seconds);
            double stamp = runtime.CurrentStamp.PhysicalSeconds;
            var motion = manager.GetBodyMotion(handle, stamp);
            var bodyPosition = DVector3.Rotate(fixedPoint, motion.Orientation);
            var (latitude, longitude) = Launch.GetSurfaceState.BodyLongLat(manager, handle, bodyPosition, stamp);
            Assert.True(Math.Abs(latitude) < 1e-9, $"t={seconds}: latitude={latitude}");
            Assert.True(Math.Abs(Math.IEEERemainder(longitude, 2 * Math.PI)) < 1e-9, $"t={seconds}: longitude={longitude}");
        }
    }

    [Fact]
    public void YAxisCoRotationMatchesLegacyScalarFormula()
    {
        var (runtime, manager, handle) = Probe(0, 1, 0);
        var fixedPoint = new DVector3(0, 0, -Radius);
        foreach (double seconds in Times)
        {
            AdvanceTo(runtime, seconds);
            double stamp = runtime.CurrentStamp.PhysicalSeconds;
            var motion = manager.GetBodyMotion(handle, stamp);
            var bodyPosition = DVector3.Rotate(fixedPoint, motion.Orientation);
            var provided = Launch.GetSurfaceState.CoRotationVelocity(manager, handle, bodyPosition, stamp);
            var legacy = SurfaceFrame.SurfaceVelocity(bodyPosition, Launch.GetSurfaceState.RotationOmega(manager, handle));
            Assert.True((provided - legacy).magnitude < 1e-12, $"t={seconds}: {provided} vs {legacy}");
        }
    }

    private sealed class EmptyResources : ICelestialRemovalResources
    {
        public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id) => Array.Empty<CelestialError>();
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id) => CelestialResult.Ok(true);
    }
}
