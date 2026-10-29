using System;
using System.Linq;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialComponentFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionalFaultOnlyDisablesSafeDependencyClosure(bool criticalDependent)
    {
        var runtime = CelestialBenchFactory.Create(3, transform: template => {
            var failing = new CelestialCompDescriptor("optional", "test.optional", 1, provides: new[] { "test.feature" },
                updatePolicy: CelestialUpdatePolicy.Every(1), failurePolicy: CelestialCompFailurePolicy.DisableOptional);
            var dependent = new CelestialCompDescriptor("dependent", "test.optional", 1, requires: new[] { "test.feature" },
                failurePolicy: criticalDependent ? CelestialCompFailurePolicy.BlockBody : CelestialCompFailurePolicy.DisableOptional);
            return CelestialBodyTemplate.Create(template.DefinitionId, template.Label, template.Category, template.Tags,
                template.Components.Concat(new[] { Definition(failing, true), Definition(dependent, false) })).Value;
        });
        var id = CelestialBenchFactory.Id(0);
        var components = runtime.GetCapabilities<Probe>(id);
        CelestialBenchFactory.Advance(runtime, 1);
        Assert.Equal(criticalDependent ? CelestialLifecycle.Blocked : CelestialLifecycle.Ready, runtime.Query(id).Value.Lifecycle);
        Assert.Empty(runtime.GetCapabilities<Probe>(id));
        if (!criticalDependent) Assert.All(components, component => Assert.True(component.IsDisabled));
        Assert.Contains(runtime.Query(id).Value.Errors, error => error.Code == "ComponentAdvanceFailed");
    }
    private static CelestialCompDefinition Definition(CelestialCompDescriptor descriptor, bool fails) => new(descriptor, new Factory(descriptor, fails),
        _ => CelestialResult.Ok<ICelestialResolvedProperties>(new Properties(descriptor.Key)));
    private sealed record Properties(string Key) : ICelestialResolvedProperties
    {
        public string HandlerId => "test.optional"; public int Version => 1;
        public ReadOnlyMemory<byte> Serialize() => ReadOnlyMemory<byte>.Empty;
    }
    private sealed class Factory(CelestialCompDescriptor descriptor, bool fails) : ICelestialCompFactory
    { public CelestialBodyComp Create(ICelestialResolvedProperties properties) => new Probe(descriptor, fails); }
    private sealed class Probe(CelestialCompDescriptor descriptor, bool fails) : CelestialBodyComp(descriptor)
    { public override void Advance(CelestialUpdateContext update) { if (fails) throw new InvalidOperationException("optional fixture"); } }
}
