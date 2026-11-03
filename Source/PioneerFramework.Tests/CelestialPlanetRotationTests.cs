using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;
public class CelestialPlanetRotationTests
{
    private static readonly CelestialInstanceId Center = CelestialInstanceId.Parse("center");
    private static readonly CelestialInstanceId Planet = CelestialInstanceId.Parse("planet");
    private static readonly CelestialInstanceId Sibling = CelestialInstanceId.Parse("sibling");
    private static readonly string[] InitialOperation = { "initial" };
    private static readonly string[] WrongOperation = { "wrong" };
    private static CompProperties_ExternalPhysicsBinding Readable(double axisX = 0, bool retrograde = false, double phase = 0, string target = "")
    {
        object motion = CelestialPlanetParameterTests.New("CelestialMotionXml");
        object rotation = CelestialPlanetParameterTests.New("CelestialRotationXml");
        CelestialPlanetParameterTests.Set(rotation, "axisX", axisX);
        CelestialPlanetParameterTests.Set(rotation, "axisY", axisX == 0 ? 1.0 : 0.0);
        CelestialPlanetParameterTests.Set(rotation, "phaseRadians", phase);
        CelestialPlanetParameterTests.Set(rotation, "retrograde", retrograde);
        CelestialPlanetParameterTests.Set(rotation, "synchronousTargetReference", target);
        if (target.Length == 0) CelestialPlanetParameterTests.Set(rotation, "periodSeconds", (double?)20.0);
        CelestialPlanetParameterTests.Set(motion, "rotation", rotation);
        return CelestialPlanetParameterTests.Set(new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = ExistingCelestialEphemerisBridge.Id }, "motion", motion);
    }
    private static PhysicsInitialization Initialization(CompProperties_ExternalPhysicsBinding props)
    {
        var result = CelestialPlanetParameterTests.Template(CelestialPlanetParameterTests.Shape(30), props).Resolve(new(1, "p"));
        Assert.True(result.Success, string.Join(",", result.Errors.Select(e => e.Code + ":" + e.Detail)));
        return ((ICelestialPhysicsBindingProperties)result.Value.Single(c => c.Descriptor.Key == "motion").Properties).Initialization;
    }
    private static CelestialBodyRegistration Registration(CelestialInstanceId id, PhysicsInitialization init, CelestialInstanceId? parent = null, IReadOnlyDictionary<string, CelestialInstanceId> references = null)
        => new("probe", new(id, 1, 30, 42), init, parent, references);
    private static ExistingCelestialEphemerisBridge Ready(params CelestialBodyRegistration[] bodies)
    {
        var bridge = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        bridge.Submit(new("initial", bridge.SessionId, 0, bodies.Select(PhysicsBodyChange.Register)));
        var prepared = bridge.PrepareInterval(new(bridge.SessionId, 0, 0, 0, InitialOperation));
        Assert.Equal(PhysicsPreparationStatus.Prepared, prepared.Status);
        bridge.CommitPreparedInterval(prepared.Token.Value); return bridge;
    }
    [Theory]
    [InlineData(1e-6, 0, 0)]
    [InlineData(1e-6, 0, .25)]
    [InlineData(1e-9, 0, 0)]
    [InlineData(1e-9, 0, 2.1)]
    [InlineData(0, -1e-9, .25)]
    [InlineData(1e-9, -2e-9, 2.1)]
    [InlineData(0, 0, .25)]
    public void NearNegativeYAxisPreservesDeclaredNorthAcrossAuthorityAndRestore(double axisX, double axisZ, double phase)
    {
        var props = Readable(axisX, phase: phase);
        props.motion.rotation.axisY = -1;
        props.motion.rotation.axisZ = axisZ;
        var declaredNorth = new DVector3(axisX, -1, axisZ).normalized;
        var registration = Registration(Planet, Initialization(props));
        var bridge = Ready(registration);
        var interval = bridge.PrepareInterval(new(bridge.SessionId, 1, 0, 17.25));
        Assert.Equal(PhysicsPreparationStatus.Prepared, interval.Status);
        foreach (double time in new[] { 0, .125, 5, 17.25 })
        {
            var sampled = interval.Sampler.Sample(time, new[] { Planet });
            Assert.True(sampled.Success, string.Join(",", sampled.Errors.Select(e => e.Code)));
            using var snapshot = sampled.Snapshot;
            Assert.True(snapshot.TryGetBodyState(Planet, out var state));
            Assert.True((DVector3.Rotate(DVector3.up, state.Orientation) - declaredNorth).magnitude < 1e-15);
            var forecast = bridge.Forecast(new(bridge.SessionId, 1, Planet, 1, time));
            Assert.True(forecast.Success, string.Join(",", forecast.Errors.Select(e => e.Code)));
            Assert.Equal(state, forecast.Value.Motion);
        }
        bridge.CommitPreparedInterval(interval.Token.Value);
        var restored = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var restore = restored.RestoreCheckpoint(bridge.CaptureCheckpoint(), new[] { registration });
        Assert.True(restore.Success, string.Join(",", restore.Errors.Select(e => e.Code)));
        var committed = restored.Sample(new(restored.SessionId, 1, new[] { Planet }));
        Assert.True(committed.Success);
        using var committedSnapshot = committed.Snapshot;
        Assert.True(committedSnapshot.TryGetBodyState(Planet, out var restoredState));
        Assert.True((DVector3.Rotate(DVector3.up, restoredState.Orientation) - declaredNorth).magnitude < 1e-15);
        foreach (double time in new[] { 0, .125, 5, 17.25, 100 })
        {
            var forecast = restored.Forecast(new(restored.SessionId, 1, Planet, 1, time));
            Assert.True(forecast.Success);
            Assert.True((DVector3.Rotate(DVector3.up, forecast.Value.Motion.Orientation) - declaredNorth).magnitude < 1e-15);
            Assert.Equal(bridge.Forecast(new(bridge.SessionId, 1, Planet, 1, time)).Value.Motion, forecast.Value.Motion);
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AxisPhaseAndRetrogradeUseAuthoritativeQuaternion(bool retrograde)
    {
        var bridge = Ready(Registration(Planet, Initialization(Readable(1, retrograde, .25))));
        var forecast = bridge.Forecast(new(bridge.SessionId, 1, Planet, 1, 5)).Value;
        var north = DVector3.Rotate(DVector3.up, forecast.Motion.Orientation);
        Assert.True((north - new DVector3(1, 0, 0)).magnitude < 1e-12);
        Assert.True((forecast.Motion.AngularVelocity - new DVector3((retrograde ? 1 : -1) * 2 * Math.PI / 20, 0, 0)).magnitude < 1e-12);
        var at0 = bridge.Forecast(new(bridge.SessionId, 1, Planet, 1, 0)).Value.Motion;
        var phaseReference = DQuaternion.AngleAxis(.25, new DVector3(1, 0, 0)) * DQuaternion.AngleAxis(-Math.PI / 2, new DVector3(0, 0, 1));
        Assert.True((DVector3.Rotate(DVector3.forward, phaseReference) - DVector3.Rotate(DVector3.forward, at0.Orientation)).magnitude < 1e-12);
        var expected = DQuaternion.AngleAxis(new DVector3(1,0,0).x * ((retrograde ? 1 : -1) * Math.PI / 2), new DVector3(1,0,0));
        var reference = DVector3.Rotate(DVector3.forward, expected * at0.Orientation);
        Assert.True((reference - DVector3.Rotate(DVector3.forward, forecast.Motion.Orientation)).magnitude < 1e-12);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SynchronousTargetFacesParentOrSiblingAcrossSamplingForecastAndRestore(bool sibling)
    {
        var root = CelestialExistingEphemerisTests.Registration(Center, 0);
        var other = CelestialExistingEphemerisTests.Registration(Sibling, 1, Center, 100, 20, .3);
        var props = Readable(target: "lock");
        object readable = props.GetType().GetField("motion")!.GetValue(props)!;
        CelestialPlanetParameterTests.Set(readable, "orbitRadiusMeters", 50.0);
        CelestialPlanetParameterTests.Set(readable, "orbitPeriodSeconds", 40.0);
        var target = sibling ? Sibling : Center;
        var planet = Registration(Planet, Initialization(props), Center, new Dictionary<string, CelestialInstanceId> { ["lock"] = target });
        var registrations = new[] { root, other, planet }; var bridge = Ready(registrations);
        var interval = bridge.PrepareInterval(new(bridge.SessionId, 1, 0, 5));
        using var snapshot = interval.Sampler.Sample(5, new[] { Planet, target }).Snapshot;
        snapshot.TryGetBodyState(Planet, out var p); snapshot.TryGetBodyState(target, out var t);
        var facing = DVector3.Rotate(new DVector3(0, 0, -1), p.Orientation);
        Assert.True((facing - (t.Position - p.Position).normalized).magnitude < 1e-12);
        Assert.Equal(p, bridge.Forecast(new(bridge.SessionId, 1, Planet, 1, 5)).Value.Motion);
        bridge.CommitPreparedInterval(interval.Token.Value);
        var restored = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        Assert.True(restored.RestoreCheckpoint(bridge.CaptureCheckpoint(), registrations).Success);
        Assert.Equal(p, restored.Forecast(new(restored.SessionId, 1, Planet, 1, 5)).Value.Motion);
    }
    [Fact]
    public void WrongSynchronousTargetAndReadableOpaqueConflictReject()
    {
        var props = Readable(); props.initializationPayload = "AQID"; Assert.NotEmpty(props.ConfigErrors());
        var wrong = Registration(Planet, Initialization(Readable(target: "missing")));
        var bridge = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        bridge.Submit(new("wrong", bridge.SessionId, 0, new[] { PhysicsBodyChange.Register(wrong) }));
        Assert.Equal(PhysicsPreparationStatus.Rejected, bridge.PrepareInterval(new(bridge.SessionId, 0, 0, 0, WrongOperation)).Status);
    }
    [Fact]
    public void LegacyRotationRetainsExactFormulaAndCheckpoint()
    {
        var registration = CelestialExistingEphemerisTests.Registration(Planet, 3, day: 317900);
        var bridge = Ready(registration);
        var state = bridge.Forecast(new(bridge.SessionId, 1, Planet, 1, 123.5)).Value.Motion;
        double omega = 2.0 * Math.PI / 317900; double half = -omega * 123.5 * .5;
        Assert.Equal(new DQuaternion(0, Math.Sin(half), 0, Math.Cos(half)), state.Orientation);
        var restored = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        Assert.True(restored.RestoreCheckpoint(bridge.CaptureCheckpoint(), new[] { registration }).Success);
        Assert.Equal(state, restored.Forecast(new(restored.SessionId, 1, Planet, 1, 123.5)).Value.Motion);
    }
    [Fact]
    public void ReadableXmlAndVersionTwoPayloadAreStrictAndMetadataRemainsOpen()
    {
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml("<motion><bodyType>Extension.Custom</bodyType><rotation><periodSeconds>20</periodSeconds><phaseRadians>0.25</phaseRadians></rotation></motion>");
        var motion = new CelestialMotionXml(); motion.LoadDataFromXmlCustom(doc.DocumentElement);
        var initialization = motion.Compile(); var parsed = ExistingEphemerisParameters.Parse(initialization);
        Assert.True(parsed.Success); Assert.Equal("Extension.Custom", parsed.Value.BodyType);
        string json = System.Text.Encoding.UTF8.GetString(initialization.Payload.Span);
        foreach (string invalid in new[] { json.Replace("\"Retrograde\":false", "\"Retrograde\":false,\"unexpected\":0", StringComparison.Ordinal),
            json.Replace("\"RotationAxis\":[0,1,0]", "\"RotationAxis\":[0,0,0]", StringComparison.Ordinal),
            json.Replace("\"RotationPeriodSeconds\":20", "\"RotationPeriodSeconds\":0", StringComparison.Ordinal) })
            Assert.False(ExistingEphemerisParameters.Parse(new(initialization.ProviderId, initialization.ModeId, 2, System.Text.Encoding.UTF8.GetBytes(invalid))).Success);
        doc.LoadXml("<motion><rotation><periodSeconds>20</periodSeconds><periodSeconds>30</periodSeconds></rotation><unknown>1</unknown></motion>");
        motion.LoadDataFromXmlCustom(doc.DocumentElement); Assert.NotEmpty(motion.ConfigErrors());
    }
}
