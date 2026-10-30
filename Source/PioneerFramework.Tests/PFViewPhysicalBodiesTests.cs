using System;
using System.Collections.Generic;
using PioneerFramework.Celestial;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests;

public class PFViewPhysicalBodiesTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData("FocusBody")]
    [InlineData("DefaultBodyDistance")]
    [InlineData("UpdateCamera")]
    [InlineData("HandleInput")]
    public void EveryCelestialBodyUsesTheOrdinaryCameraPolicy(string name)
    {
        var members = Members(name);
        Assert.DoesNotContain(members, m => m.Name is "get_IsStarFocus" or "InitializeSunCameraPose"
            || m.DeclaringType?.Name == "PFViewSolarCore");
    }

    [Fact]
    public void AllBodiesAreDrawnAtEphemerisPositionsWithTheSharedSpherePipeline()
    {
        var members = Members("DrawCelestialBodiesCore");
        Assert.Contains(members, m => m.Name == "GetCelestialRenderPosition");
        var positions = Members("GetCelestialRenderPosition");
        Assert.Contains(positions, m => m.Name == "get_Motion");
        Assert.Contains(positions, m => m.Name == "get_Position");
        Assert.Contains(positions, m => m.Name == "GetBodyPositionAbs");
        Assert.Contains(Members("GetCelestialRenderBodies"), m => m.DeclaringType == typeof(Celestial.CelestialViewAdapter) && m.Name == "CapturePresentationBatch");
        Assert.Contains(members, m => m.Name == "AbsToView");
        Assert.Contains(members, m => m.Name == "DrawCelestialSphere");
        Assert.DoesNotContain(members, m => m.Name is "DrawSunDisc" or "TryDrawSolarPlasma" or "SunDiscDir");
        var render = Members("RenderScene");
        Assert.DoesNotContain(render, m => m.Name is "UpdateSunDisc" or "ProbeSunDisc");
    }

    [Fact]
    public void RetiredSolarViewAndShadersAreAbsentFromTheRuntime()
    {
        var assembly = typeof(MainTabWindow_PFView).Assembly;
        Assert.Null(assembly.GetType("PioneerFramework.PFViewSolarCore"));
        Assert.Null(typeof(MainTabWindow_PFView).GetMethod("TryDrawSolarPlasma", Flags));
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "Defs", "ShaderDefs", "PFViewSolar.xml")));
    }

    [Fact]
    public void StarfieldHasAnInertialOrientationIndependentOfSunlightAndCameraOrbit()
    {
        var members = Members("DrawStarfield");
        Assert.Contains(members, m => m.Name == "viewFrameRot");
        Assert.DoesNotContain(members, m => m.Name is "sunDirectionView" or "SunDiscDir");
    }

    [Fact]
    public void PlanetsKeepTheirOpaqueShaderAndStarsUseOnlyAVisualMaterialOverride()
    {
        var shader = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Defs", "ShaderDefs", "PFShaderDefs.xml"))
            .Root.Elements("ShaderDef").Single(e => (string)e.Element("defName") == "PFLit").Element("shaderProgram");
        Assert.Equal("Opaque", (string)shader.Element("blendMode"));
        Assert.NotEqual("false", (string)shader.Element("depthTest"));
        Assert.NotEqual("false", (string)shader.Element("depthWrite"));
        var members = Members("GetBaseSphereMaterial");
        Assert.Contains(members, m => m.Name == "CreateStellarSurfaceMaterial");
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(14600d)]
    [InlineData(58400d)]
    [InlineData(317900d)]
    [InlineData(-45000d)]
    [InlineData(123456789.25d)]
    public void ConfiguredPlanetsOrbitTheSunAndMoonsOrbitTheirParent(double tick)
    {
        var (manager, bodies) = SolarSystem();
        var sun = manager.GetBodyPositionAbs(0, tick);
        var earth = manager.GetBodyPositionAbs(1, tick);
        var moon = manager.GetBodyPositionAbs(2, tick);
        Assert.Equal(DVector3.zero, sun);
        Assert.Equal(bodies[1].orbitRadius, (earth - sun).magnitude, 5);
        Assert.Equal(bodies[2].orbitRadius, (moon - earth).magnitude, 5);
        Assert.True((earth - sun).magnitude > bodies[0].radius + bodies[1].radius);
        Assert.True((moon - earth).magnitude > bodies[1].radius + bodies[2].radius);
        Assert.True(Math.Abs(DVector3.Dot(earth - sun, bodies[1].OrbitNormalD.normalized)) < 1e-5);
        Assert.True(Math.Abs(DVector3.Dot(moon - earth, bodies[2].OrbitNormalD.normalized)) < 1e-5);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ConfiguredOrbitalPeriodsAgreeWithTheirParentsGravity(int id)
    {
        var (_, bodies) = SolarSystem();
        var body = bodies[id];
        double expected = 2 * Math.PI * Math.Sqrt(Math.Pow(body.orbitRadius, 3)
            / bodies[body.ParentBodyId].gravitationalParameter);
        Assert.InRange(Math.Abs(body.orbitPeriod / expected - 1), 0, 1e-4);
    }

    [Theory]
    [InlineData(1, 0d)]
    [InlineData(1, 12345.75d)]
    [InlineData(2, 0d)]
    [InlineData(2, 12345.75d)]
    public void ActualEphemerisVelocitiesMatchPositionMotion(int id, double tick)
    {
        var (manager, _) = SolarSystem();
        const double dt = .01;
        var difference = (manager.GetBodyPositionAbs(id, tick + dt) - manager.GetBodyPositionAbs(id, tick - dt)) / (2 * dt);
        var velocity = manager.GetBodyVelocityAbs(id, tick);
        Assert.True((difference - velocity).magnitude / velocity.magnitude < 1e-7);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void EachBodyReturnsToItsParentRelativePositionAfterOneOrbit(int id)
    {
        var (manager, bodies) = SolarSystem();
        var body = bodies[id];
        var start = manager.GetBodyPositionAbs(id, 0) - manager.GetBodyPositionAbs(body.ParentBodyId, 0);
        var end = manager.GetBodyPositionAbs(id, body.orbitPeriod) - manager.GetBodyPositionAbs(body.ParentBodyId, body.orbitPeriod);
        Assert.True((end - start).magnitude < 1e-5);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(12345.75d)]
    [InlineData(14600d)]
    public void EachBodyIsLitFromItsOwnPositionTowardTheActualSun(double tick)
    {
        var (manager, _) = SolarSystem();
        var light = typeof(MainTabWindow_PFView).Assembly.GetType("PioneerFramework.PFViewCelestialLighting");
        Assert.NotNull(light);
        var direction = light.GetMethod("Direction", Flags);
        Assert.NotNull(direction);
        var frame = PFViewCameraCore.OrbitRotation(37f, 19f, Quaternion.identity);
        var sun = manager.GetBodyPositionAbs(0, tick);
        foreach (int id in new[] { 1, 2 })
        {
            var position = manager.GetBodyPositionAbs(id, tick);
            var actual = (Vector3)direction.Invoke(null, new object[] { sun, position, frame });
            var expected = frame * (Vector3)(sun - position).normalized;
            Assert.True(Vector3.Dot(actual, expected) > .999999f);
        }
        if (tick == 0)
        {
            var earthLight = (Vector3)direction.Invoke(null, new object[] { sun, manager.GetBodyPositionAbs(1, tick), frame });
            var moonLight = (Vector3)direction.Invoke(null, new object[] { sun, manager.GetBodyPositionAbs(2, tick), frame });
            Assert.True(Vector3.Dot(earthLight, moonLight) < .95f, "The Moon must not reuse the Earth's lighting direction.");
        }
    }

    [Theory]
    [InlineData("DrawCloudLayer")]
    [InlineData("DrawSettlementOverlay")]
    public void SurfaceLayersReceiveTheirBodySpecificLightDirection(string name)
    {
        var method = typeof(MainTabWindow_PFView).GetMethod(name, Flags);
        Assert.Contains(method.GetParameters(), p => p.Name == "toSun" && p.ParameterType == typeof(Vector3));
        Assert.DoesNotContain(PFViewUiIntegrationTests.ReferencedMembers(method),
            m => m.Name is "sunDirectionView" or "get_sunLightForward");
    }

    [Theory]
    [InlineData("UpdateLighting")]
    [InlineData("DrawCelestialBodiesCore")]
    [InlineData("UpdateAtmosphereSky")]
    public void CameraBodiesAndAtmosphereUseGeometricLightDirections(string name)
        => Assert.Contains(Members(name), m => m.DeclaringType?.Name == "PFViewCelestialLighting" && m.Name == "Direction");

    private static MemberInfo[] Members(string name) => PFViewUiIntegrationTests.ReferencedMembers(
        typeof(MainTabWindow_PFView).GetMethod(name, Flags)).ToArray();

    private static (CelestialBodyManager, Dictionary<int, CelestialBodyInfo>) SolarSystem()
    {
        var root = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Defs", "CelestialBodies", "CelestialBodies_SolarSystem.xml")).Root;
        var system = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Defs", "CelestialFramework", "CelestialSystems.xml"))
            .Root.Elements().Single().Element("entries").Elements("li").ToDictionary(entry => (string)entry.Element("key"));
        string EntryPath(XElement entry) => entry.Element("parentEntry") is { } parent ? EntryPath(system[parent.Value]) + "/" + entry.Element("key").Value + "[0]"
            : entry.Element("key").Value + "[0]";
        var entries = system.Values.OrderBy(EntryPath, StringComparer.Ordinal).ToArray();
        var xmlBodies = root.Elements().ToDictionary(element => element.Element("defName").Value);
        var byName = entries.Select((entry, index) => {
            var e = xmlBodies[entry.Element("bodyDef").Value];
            var comps = e.Element("comps").Elements("li").ToArray();
            var geometry = comps.Single(comp => comp.Attribute("Class").Value.EndsWith("CompProperties_SphericalBody", StringComparison.Ordinal));
            var physics = comps.Single(comp => comp.Attribute("Class").Value.EndsWith("CompProperties_ExternalPhysicsBinding", StringComparison.Ordinal));
            var decoded = ExistingEphemerisParameters.Parse(new(physics.Element("providerId").Value, physics.Element("modeId").Value,
                (int?)physics.Element("initializationVersion") ?? 1, Convert.FromBase64String(physics.Element("initializationPayload").Value)));
            Assert.True(decoded.Success);
            var parameters = decoded.Value;
            return new CelestialBodyInfo {
                defName = e.Element("defName").Value, bodyId = index, bodyType = Enum.Parse<CelestialBodyType>(e.Element("category").Value),
                radius = (double)geometry.Element("radiusMeters").Element("value"),
                gravitationalParameter = (double)physics.Element("gravitationalParameter").Element("value"),
                orbitRadius = parameters.OrbitRadius, orbitPeriod = parameters.OrbitPeriod, initialPhase = parameters.InitialPhase,
                orbitNormal = (Vector3)parameters.OrbitNormal,
                parentBodyDefName = entry.Element("parentEntry") is { } parent ? system[parent.Value].Element("bodyDef").Value : null };
        }).ToDictionary(body => body.defName);
        foreach (var body in byName.Values)
            typeof(CelestialBodyInfo).GetProperty(nameof(CelestialBodyInfo.ParentBodyId)).SetValue(body,
                body.parentBodyDefName != null ? byName[body.parentBodyDefName].bodyId : -1);
        var bodies = byName.Values.ToDictionary(b => b.bodyId);
        var manager = new CelestialBodyManager(null);
        typeof(CelestialBodyManager).GetField("initialized", Flags).SetValue(manager, true);
        typeof(CelestialBodyManager).GetField("bodiesById", Flags).SetValue(manager, bodies);
        return (manager, bodies);
    }

}
