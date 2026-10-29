using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using PioneerFramework.Celestial;
using PFL.CelestialCompSample;
using Xunit;
using Verse;

namespace PioneerFramework.Tests;

public class CelestialExternalCompTests
{
    [Fact]
    public void ShippedSystemPatchAppliesThroughNativePatchOperation()
    {
        using var scope = new CelestialXmlTestScope();
        var system = new XmlDocument();
        system.Load(CelestialDefaultContentTests.DataPath("CelestialFramework/CelestialSystems.xml"));
        var patch = new XmlDocument();
        patch.Load(Path.Combine(AppContext.BaseDirectory, "CelestialCompSample", "Patches", "SampleSystem.xml"));
        var operation = DirectXmlToObject.ObjectFromXml<PatchOperation>(patch.DocumentElement.FirstChild, false);
        Assert.True(operation.Apply(system));
        Assert.Equal(5, system.SelectNodes("Defs/PioneerFramework.Celestial.CelestialSystemDef/entries/li").Count);
        Assert.NotNull(system.SelectSingleNode("//entries/li[key='z_counterFixed' and bodyDef='PF_CounterSampleFixed']"));
        Assert.NotNull(system.SelectSingleNode("//entries/li[key='z_counterRanged' and bodyDef='PF_CounterSampleRanged']"));
    }

    [Fact]
    public void ShippedXmlLoadsNativeInheritanceAndBothPublicComponentConfigurations()
    {
        using var scope = new CelestialXmlTestScope();
        var xml = new XmlDocument();
        xml.Load(Path.Combine(AppContext.BaseDirectory, "CelestialCompSample", "Defs", "SampleCelestialBodies.xml"));
        foreach (XmlNode node in xml.DocumentElement.ChildNodes) XmlInheritance.TryRegister(node, null);
        scope.InheritanceUsed = true;
        XmlInheritance.Resolve();
        var visual = DirectXmlToObject.ObjectFromXml<CelestialVisualProfileDef>(xml.DocumentElement.FirstChild, false);
        var templates = new List<CelestialBodyTemplate>();
        foreach (XmlNode node in xml.DocumentElement.ChildNodes)
        {
            if (node["defName"]?.InnerText is not ("PF_CounterSampleFixed" or "PF_CounterSampleRanged")) continue;
            var definition = DirectXmlToObject.ObjectFromXml<CelestialBodyDef>(node, false);
            Assert.Equal(visual.defName, XmlInheritance.GetResolvedNodeFor(node).SelectSingleNode("comps/li[key='visual']/profile").InnerText);
            foreach (var component in definition.comps.OfType<CelestialVisualCompProperties>()) component.profile = visual;
            var template = definition.ToTemplate();
            Assert.True(template.Success, string.Join(",", definition.ConfigErrors()));
            templates.Add(template.Value);
            Assert.Single(definition.comps.OfType<CompProperties_SampleCounter>());
        }
        Assert.Equal(2, templates.Count);
        var fixedProperties = templates.Single(t => t.DefinitionId == "PF_CounterSampleFixed").Resolve(new(1, "fixed")).Value;
        Assert.Equal(3, fixedProperties.Select(c => c.Properties).OfType<SampleCounterProperties>().Single().InitialCount);
        var ranged = templates.Single(t => t.DefinitionId == "PF_CounterSampleRanged").Resolve(new(1, "ranged")).Value;
        Assert.InRange(ranged.Select(c => c.Properties).OfType<ICelestialGeometryProperties>().Single().RadiusMeters, 250000, 650000);
    }

    [Fact]
    public void IndependentAssemblyCountersKeepTheirOwnStateAcrossCodecRestoreAndUnknownModules()
    {
        Assert.NotEqual(typeof(CelestialBodyComp).Assembly, typeof(SampleCounterComp).Assembly);
        var definition = new CompProperties_SampleCounter { key = "counter", initialCount = new() { value = 3 } };
        var firstProperties = Assert.IsType<SampleCounterProperties>(definition.Resolve(new(1, "first", "counter")).Value);
        var secondProperties = Assert.IsType<SampleCounterProperties>(definition.Resolve(new(1, "second", "counter")).Value);
        var first = Assert.IsType<SampleCounterComp>(definition.Create(firstProperties));
        var second = Assert.IsType<SampleCounterComp>(definition.Create(secondProperties));
        first.Attach(new Context("first", firstProperties)); second.Attach(new Context("second", secondProperties));
        first.Handle(new(SampleCounterComp.IncrementEvent));
        Assert.Equal(4, first.Counter); Assert.Equal(3, second.Counter);
        first.Advance(new CelestialUpdateContext(0, 100, 100));
        Assert.Equal(4, first.Counter);
        var records = new[] { CelestialCompStateCodec.Encode(first, firstProperties), CelestialCompStateCodec.Encode(second, secondProperties) };
        var registry = new CelestialCompRegistry();
        Assert.True(SampleCounterRegistration.Register(registry).Success);
        foreach (var pair in records.Select((record, index) => (record, index)))
        {
            var restored = CelestialCompStateCodec.Decode(pair.record, registry).Value;
            Assert.True(restored.IsReady);
            var counter = Assert.IsType<SampleCounterComp>(restored.Comp);
            counter.Attach(new Context("restored-" + pair.index, restored.Properties));
            Assert.Equal(pair.index == 0 ? 4 : 3, counter.Counter);
            var unknown = CelestialCompStateCodec.Decode(pair.record, new CelestialCompRegistry()).Value;
            Assert.False(unknown.IsReady);
            Assert.Equal(pair.record.ConfigurationPayload.ToArray(), unknown.OriginalRecord.ConfigurationPayload.ToArray());
            Assert.Equal(pair.record.StatePayload.ToArray(), unknown.OriginalRecord.StatePayload.ToArray());
        }
    }

    [Fact]
    public void FixedAndClosedRangeInputsAreFrozenPerInstanceAndRejectInvalidForms()
    {
        var properties = new CompProperties_SampleCounter { key = "counter", initialCount = new() { min = 3, max = 9 } };
        var values = Enumerable.Range(0, 256).Select(seed => Assert.IsType<SampleCounterProperties>(properties.Resolve(new((ulong)seed, "range", "counter")).Value).InitialCount).ToArray();
        Assert.All(values, value => Assert.InRange(value, 3, 9));
        Assert.Contains(3, values); Assert.Contains(9, values);
        var resolved = Assert.IsType<SampleCounterProperties>(properties.Resolve(new(1, "freeze", "counter")).Value);
        int original = resolved.InitialCount;
        properties.initialCount.min = 100;
        Assert.Equal(original, resolved.InitialCount);
        Assert.False(properties.Resolve(new(1, "invalid", "counter")).Success);
        properties.initialCount = new() { value = -1 };
        Assert.False(properties.Resolve(new(1, "negative", "counter")).Success);
    }

    private sealed class Context(string id, ICelestialResolvedProperties properties) : ICelestialCompContext
    {
        public CelestialInstanceId InstanceId => CelestialInstanceId.Parse(id);
        public Guid WorldSessionId { get; } = Guid.NewGuid();
        public long CatalogRevision => 1;
        public long DescriptorRevision => 1;
        public ICelestialResolvedProperties Properties => properties;
        public double PhysicalSeconds => 0;
        public bool TryGetCapability<T>(out T capability) where T : class { capability = null; return false; }
        public IReadOnlyList<T> GetCapabilities<T>() where T : class => Array.Empty<T>();
        public bool TryGetService<T>(out T service) where T : class { service = null; return false; }
        public CelestialResult<string> Submit(CelestialCommand command) => throw new InvalidOperationException();
    }
}
