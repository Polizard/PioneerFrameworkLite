using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialPhysicsPropertiesTests
{
    [Fact]
    public void NativeXmlResolvesOpaqueInitializationAndBindingRemainsIndependentOfXmlChanges()
    {
        using var scope = new CelestialXmlTestScope();
        string xml = CelestialDefinitionTests.BodyXml("<value>30</value>").Replace("</comps>",
            "<li Class=\"PioneerFramework.Celestial.CompProperties_ExternalPhysicsBinding\"><key>motion</key>" +
            "<providerId>test.provider</providerId><modeId>external.mode</modeId><initializationPayload>AQID</initializationPayload>" +
            "<gravitationalParameter><value>20</value></gravitationalParameter></li></comps>", StringComparison.Ordinal);
        var definition = scope.ParseBody(xml);
        var resolved = definition.ToTemplate().Value.Resolve(new CelestialGenerationContext(1, "earth")).Value;
        var binding = resolved.Single(component => component.Properties is ICelestialPhysicsBindingProperties);
        var properties = Assert.IsType<ExternalPhysicsResolvedProperties>(binding.Properties);
        var saved = ExternalPhysicsResolvedProperties.Deserialize(properties.Key, properties.Version, properties.Serialize());
        Assert.True(saved.Success);
        Assert.Equal(new byte[] { 1, 2, 3 }, saved.Value.Initialization.Payload.ToArray());
        ((CompProperties_ExternalPhysicsBinding)definition.comps[1]).providerId = "changed";
        var component = Assert.IsType<CompExternalPhysicsBinding>(binding.Factory.Create(binding.Properties));
        var id = CelestialInstanceId.Parse("instance");
        component.Attach(new Context(id, properties));
        var registration = component.CreateRegistration(new CelestialBodyDescription(id, "PF_Test", 0, 1, "Planet", 30, 20), null,
            new Dictionary<string, CelestialInstanceId>());
        Assert.True(registration.Success);
        Assert.Equal("test.provider", registration.Value.Initialization.ProviderId);
        Assert.Equal(20, registration.Value.Descriptor.GravitationalParameter);
    }

    [Fact]
    public void InvalidPayloadAndNegativePhysicalParametersAreRejected()
    {
        var definition = new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = "provider", modeId = "mode", initializationPayload = "invalid" };
        Assert.Contains("InvalidInitializationPayload", definition.ConfigErrors());
        definition.initializationPayload = "";
        definition.gravitationalParameter = new() { value = -1 };
        Assert.Contains("InvalidGravitationalParameter", definition.ConfigErrors());
    }

    private sealed class Context(CelestialInstanceId id, ICelestialResolvedProperties properties) : ICelestialCompContext
    {
        public CelestialInstanceId InstanceId => id;
        public Guid WorldSessionId => Guid.Empty;
        public long CatalogRevision => 0;
        public long DescriptorRevision => 1;
        public ICelestialResolvedProperties Properties => properties;
        public double PhysicalSeconds => 0;
        public bool TryGetCapability<T>(out T capability) where T : class { capability = null!; return false; }
        public IReadOnlyList<T> GetCapabilities<T>() where T : class => Array.Empty<T>();
        public bool TryGetService<T>(out T service) where T : class { service = null; return false; }
        public CelestialResult<string> Submit(CelestialCommand command) => throw new InvalidOperationException();
    }
}
