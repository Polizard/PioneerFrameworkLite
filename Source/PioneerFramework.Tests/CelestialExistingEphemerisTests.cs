using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialExistingEphemerisTests
{
    [Fact]
    public void ForecastUsesCommittedModelVersionWithoutPublishingFutureOrPendingBodies()
    {
        var bridge = Ready();
        var stamp = bridge.CommittedStamp;
        var forecast = ((ICelestialPhysicsForecastProvider)bridge).Forecast(new(bridge.SessionId, 1, Moon, 1, 10000));
        Assert.True(forecast.Success);
        Assert.Equal(stamp, forecast.Value.BasedOn);
        Assert.Equal(10000, forecast.Value.PhysicalSeconds);
        var local = OriginalCircular(8e7, 317900, 1.5, 10000);
        var parent = OriginalCircular(1.2e8, 58400, 0, 10000);
        Assert.Equal(local.pos, forecast.Value.PositionRelativeToParent);
        Assert.Equal(local.vel, forecast.Value.VelocityRelativeToParent);
        Assert.Equal(local.pos + parent.pos, forecast.Value.Motion.Position);
        Assert.Equal(stamp, bridge.CommittedStamp);
        Assert.Equal("TimeUnavailable", Error(bridge.Sample(new(bridge.SessionId, 1, new[] { Moon }, physicalSeconds: 10000))));
        Assert.Equal("DescriptorRevisionConflict", Assert.Single(bridge.Forecast(new(bridge.SessionId, 1, Moon, 2, 10000)).Errors).Code);
        Assert.Equal("StaleSession", Assert.Single(bridge.Forecast(new(Guid.NewGuid(), 1, Moon, 1, 10000)).Errors).Code);
        var next = CelestialInstanceId.Parse("pending-forecast");
        bridge.Submit(new("pending-forecast", bridge.SessionId, 1, new[] { PhysicsBodyChange.Register(Registration(next, 3)) }));
        Assert.Equal("BodyMissing", Assert.Single(bridge.Forecast(new(bridge.SessionId, 1, next, 1, 10000)).Errors).Code);
    }
    private static readonly CelestialInstanceId Sun = CelestialInstanceId.Parse("sun");
    private static readonly CelestialInstanceId Earth = CelestialInstanceId.Parse("earth");
    private static readonly CelestialInstanceId Moon = CelestialInstanceId.Parse("moon");

    [Fact]
    public void ProductionAdapterPreservesExistingParentCompositionAndSubstepFormula()
    {
        var bridge = Ready();
        var prepared = bridge.PrepareInterval(new(bridge.SessionId, 1, 0, 7.25));
        Assert.Equal(PhysicsPreparationStatus.Prepared, prepared.Status);
        foreach (double t in new[] { 0, 0.125, 3.75, 7.25 })
        {
            using var snapshot = prepared.Sampler.Sample(t, new[] { Sun, Earth, Moon }).Snapshot;
            Assert.Equal(PhysicsSampleKind.AuthoritativeInterval, snapshot.Stamp.SampleKind);
            snapshot.TryGetBodyState(Earth, out var earth);
            snapshot.TryGetBodyState(Moon, out var moon);
            var earthBaseline = OriginalCircular(1.2e8, 58400, 0, t);
            var moonBaseline = OriginalCircular(8e7, 317900, 1.5, t);
            var local = bridge.SampleParent(Moon, t, 1, prepared.Sampler).Value;
            Assert.Equal(moonBaseline.pos, local.Position);
            Assert.Equal(moonBaseline.vel, local.Velocity);
            Assert.Equal(earthBaseline.pos, earth.Position);
            Assert.Equal(earthBaseline.vel, earth.LinearVelocity);
            Assert.Equal(moonBaseline.pos + earthBaseline.pos, moon.Position);
            Assert.Equal(moonBaseline.vel + earthBaseline.vel, moon.LinearVelocity);
            Assert.Equal(1, moon.DescriptorRevision);
            Assert.Equal(new DVector3(0, -2 * Math.PI / 317900, 0), moon.AngularVelocity);
            var point = new DVector3(17, 5, -31);
            Assert.True((DVector3.Rotate(point, moon.Orientation) - SurfaceFrame.FixedToEci(point, 2 * Math.PI / 317900, t)).magnitude < 1e-12);
        }
        Assert.Equal(0, bridge.CommittedStamp.PhysicalSeconds);
        Assert.Equal("TimeUnavailable", Error(bridge.Sample(new(bridge.SessionId, 1, new[] { Earth }, physicalSeconds: 1))));
        bridge.CommitPreparedInterval(prepared.Token.Value);
        using var historical = bridge.Sample(new(bridge.SessionId, 1, new[] { Earth }, PhysicsSamplePurpose.Presentation, 3.75)).Snapshot;
        Assert.Equal(3.75, historical.Stamp.PhysicalSeconds);
        Assert.Equal(OriginalCircular(1.2e8, 58400, 0, 3.75).pos, State(historical, Earth).Position);
        Assert.Equal("TimeUnavailable", Error(bridge.Sample(new(bridge.SessionId, 1, new[] { Earth }, PhysicsSamplePurpose.Presentation, 7.5))));
    }

    [Fact]
    public void MultipleStarsUseExplicitMotionPayloadAndUpdatesKeepOldSnapshots()
    {
        var bridge = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var other = CelestialInstanceId.Parse("other-star");
        var registrations = new[] { Registration(Sun, 0), Registration(other, 1, Sun, 100, 20, 0.3, type: "Star") };
        Apply(bridge, "stars", registrations.Select(PhysicsBodyChange.Register).ToArray());
        using var old = bridge.Sample(new(bridge.SessionId, 1, new[] { other })).Snapshot;
        Assert.Equal(OriginalCircular(100, 20, 0.3, 0).pos, State(old, other).Position);
        var update = Registration(other, 1, Sun, 100, 20, 0.3, type: "Star", revision: 2, radius: 500);
        Apply(bridge, "update", new[] { PhysicsBodyChange.Update(update) });
        using var current = bridge.Sample(new(bridge.SessionId, 2, new[] { other })).Snapshot;
        Assert.Equal(1, State(old, other).DescriptorRevision);
        Assert.Equal(2, State(current, other).DescriptorRevision);
        current.TryGetBodyDescriptor(other, out var descriptor);
        Assert.Equal(500, descriptor.RadiusMeters);
        Assert.Equal(descriptor.DescriptorRevision, State(current, other).DescriptorRevision);
    }

    [Fact]
    public void AbortExpiresSamplerAndLateRequestsSurviveCommitWithIdempotentReceipts()
    {
        var bridge = Ready();
        var first = bridge.PrepareInterval(new(bridge.SessionId, 1, 0, 1));
        bridge.AbortPreparedInterval(first.Token.Value);
        Assert.Equal("PreparedIntervalExpired", Error(first.Sampler.Sample(0, new[] { Earth })));
        Assert.Equal(0, bridge.CommittedStamp.PhysicalSeconds);
        var second = bridge.PrepareInterval(new(bridge.SessionId, 1, 0, 1));
        var remove = new PhysicsChangeSet("remove", bridge.SessionId, 1, new[] { PhysicsBodyChange.Unregister(Moon) });
        bridge.Submit(remove);
        bridge.CommitPreparedInterval(second.Token.Value);
        bridge.CommitPreparedInterval(second.Token.Value);
        Assert.Equal(PhysicsOperationStatus.Pending, bridge.GetOperation("remove").Status);
        var next = bridge.PrepareInterval(new(bridge.SessionId, 1, 1, 1, new[] { "remove" }));
        bridge.CommitPreparedInterval(next.Token.Value);
        Assert.Equal(PhysicsOperationStatus.Applied, bridge.Submit(remove).Status);
        Assert.Equal("OperationConflict", Assert.Single(bridge.Submit(new("remove", bridge.SessionId, 2, new[] { PhysicsBodyChange.Unregister(Earth) })).Errors).Code);
        Assert.Equal("StaleSession", Assert.Single(bridge.Submit(new("remove", Guid.NewGuid(), 2, remove.Changes)).Errors).Code);
    }

    [Fact]
    public void CheckpointRestoresExactParametersAndPendingRequestsInNewSession()
    {
        var bridge = Ready();
        var interval = bridge.PrepareInterval(new(bridge.SessionId, 1, 0, 13));
        bridge.CommitPreparedInterval(interval.Token.Value);
        var remove = new PhysicsChangeSet("remove", bridge.SessionId, 1, new[] { PhysicsBodyChange.Unregister(Moon) });
        bridge.Submit(remove);
        var checkpoint = bridge.CaptureCheckpoint();
        var restored = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var result = restored.RestoreCheckpoint(checkpoint, Defaults());
        Assert.True(result.Success, string.Join(",", result.Errors.Select(e => e.Code)));
        Assert.Equal(13, restored.CommittedStamp.PhysicalSeconds);
        Assert.NotEqual(bridge.SessionId, restored.SessionId);
        using var a = bridge.Sample(new(bridge.SessionId, 1, new[] { Moon })).Snapshot;
        using var b = restored.Sample(new(restored.SessionId, 1, new[] { Moon })).Snapshot;
        Assert.Equal(State(a, Moon), State(b, Moon));
        Assert.Equal("RestorePendingResubmission", Assert.Single(restored.PrepareInterval(new(restored.SessionId, 1, 13, 13, new[] { "remove" })).Errors).Code);
        Assert.Equal(PhysicsOperationStatus.Pending, restored.Submit(new("remove", restored.SessionId, 1, remove.Changes)).Status);
        var prepared = restored.PrepareInterval(new(restored.SessionId, 1, 13, 13, new[] { "remove" }));
        restored.CommitPreparedInterval(prepared.Token.Value);
        Assert.Equal(PhysicsOperationStatus.Applied, restored.GetOperation("remove").Status);
        Assert.Equal("BodyMissing", Error(restored.Sample(new(restored.SessionId, 2, new[] { Moon }))));
    }

    [Theory]
    [InlineData("{\"orbitRadius\":100,\"orbitPeriod\":0,\"orbitNormal\":[0,1,0],\"initialPhase\":0,\"dayLengthTicks\":60000}")]
    [InlineData("{\"orbitRadius\":100,\"orbitPeriod\":20,\"orbitNormal\":[0,0,0],\"initialPhase\":0,\"dayLengthTicks\":60000}")]
    [InlineData("{\"orbitRadius\":0,\"orbitPeriod\":0,\"orbitNormal\":[0,1,0],\"initialPhase\":0,\"dayLengthTicks\":60000,\"typo\":2}")]
    public void InvalidInitializationCannotPartiallyPublish(string payload)
    {
        var bridge = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var valid = Registration(Sun, 0);
        var invalid = new CelestialBodyRegistration("test", new(Earth, 1, 30), new(bridge.ProviderId, "existing.circular.v1", 1, Encoding.UTF8.GetBytes(payload)));
        bridge.Submit(new("invalid", bridge.SessionId, 0, new[] { PhysicsBodyChange.Register(valid), PhysicsBodyChange.Register(invalid) }));
        Assert.Equal(PhysicsPreparationStatus.Rejected, bridge.PrepareInterval(new(bridge.SessionId, 0, 0, 0, new[] { "invalid" })).Status);
        Assert.Equal(0, bridge.CommittedStamp.TopologyRevision);
        Assert.Equal("BodyMissing", Error(bridge.Sample(new(bridge.SessionId, 0, new[] { Sun }))));
    }

    [Fact]
    public void ParentCyclesAndRemovingLiveParentsAreRejected()
    {
        var bridge = Ready();
        bridge.Submit(new("parent", bridge.SessionId, 1, new[] { PhysicsBodyChange.Unregister(Earth) }));
        Assert.Equal(PhysicsPreparationStatus.Rejected, bridge.PrepareInterval(new(bridge.SessionId, 1, 0, 0, new[] { "parent" })).Status);
        Assert.Equal(1, bridge.CommittedStamp.TopologyRevision);
        var cyclic = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        cyclic.Submit(new("cycle", cyclic.SessionId, 0, new[] { PhysicsBodyChange.Register(Registration(Earth, 1, Moon)), PhysicsBodyChange.Register(Registration(Moon, 2, Earth)) }));
        Assert.Equal(PhysicsPreparationStatus.Rejected, cyclic.PrepareInterval(new(cyclic.SessionId, 0, 0, 0, new[] { "cycle" })).Status);
    }

    private static ExistingCelestialEphemerisBridge Ready()
    {
        var bridge = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        Apply(bridge, "initial", Defaults().Select(PhysicsBodyChange.Register).ToArray());
        return bridge;
    }
    private static CelestialBodyRegistration[] Defaults() => new[] { Registration(Sun, 0), Registration(Earth, 1, Sun, 1.2e8, 58400), Registration(Moon, 2, Earth, 8e7, 317900, 1.5, day: 317900) };
    private static void Apply(ExistingCelestialEphemerisBridge bridge, string operation, PhysicsBodyChange[] changes)
    {
        var stamp = bridge.CommittedStamp;
        bridge.Submit(new(operation, bridge.SessionId, stamp.TopologyRevision, changes));
        var prepared = bridge.PrepareInterval(new(bridge.SessionId, stamp.TopologyRevision, stamp.PhysicalSeconds, stamp.PhysicalSeconds, new[] { operation }));
        Assert.Equal(PhysicsPreparationStatus.Prepared, prepared.Status);
        bridge.CommitPreparedInterval(prepared.Token.Value);
    }
    internal static CelestialBodyRegistration Registration(CelestialInstanceId id, int handle, CelestialInstanceId? parent = null,
        double orbit = 0, double period = 0, double phase = 0, double day = 60000, string type = "Planet", long revision = 1, double radius = 30)
        => new("shared-template", new(id, revision, radius, 20), new("PFL.ExistingEphemeris", "existing.circular.v1", 1,
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { orbitRadius = orbit, orbitPeriod = period, initialPhase = phase,
                orbitNormal = new[] { 0, 1, 0 }, dayLengthTicks = day, bodyType = type, frameOriginReference = "frameOrigin" })), parent,
            compatibilityHandle: handle);
    // 冻结的原 CelestialBodyManager XZ 圆轨公式；保留计算顺序作为迁移基线。
    private static (DVector3 pos, DVector3 vel) OriginalCircular(double r, double period, double phase0, double t)
    {
        double omega = 2.0 * Math.PI / period;
        double phase = phase0 + omega * t;
        double cos = Math.Cos(phase), sin = Math.Sin(phase);
        var x = new DVector3(1, 0, 0); var y = new DVector3(0, 0, -1);
        return (r * (cos * x + sin * y), (r * omega) * (-sin * x + cos * y));
    }
    private static CelestialMotionState State(ICelestialPhysicsSnapshot snapshot, CelestialInstanceId id)
    { Assert.True(snapshot.TryGetBodyState(id, out var state)); return state; }
    private static string Error(PhysicsSnapshotResult result) => Assert.Single(result.Errors).Code;
}
