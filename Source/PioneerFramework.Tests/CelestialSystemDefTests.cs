using System;
using System.Collections.Generic;
using System.Xml;
using PioneerFramework.Celestial;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialSystemDefTests
{
    [Fact]
    public void XmlScopeDoesNotLeakUnresolvedReferencesIntoNativeInitialization()
    {
        using var preferences = new CelestialXmlTestScope();
        DirectXmlCrossRefLoader.Clear();
        using (var scope = new CelestialXmlTestScope())
        {
            var document = new XmlDocument();
            document.LoadXml("<PioneerFramework.Celestial.CelestialSystemDef><entries><li><key>body</key><bodyDef>PF_UnknownProbe</bodyDef>" +
                "</li></entries></PioneerFramework.Celestial.CelestialSystemDef>");
            DirectXmlToObject.ObjectFromXml<CelestialSystemDef>(document.DocumentElement, false);
            Assert.True(DirectXmlCrossRefLoader.LoadingInProgress);
        }
        Assert.False(DirectXmlCrossRefLoader.LoadingInProgress);
    }

    [Fact]
    public void NativeXmlParsesEntryCountsAndPerParentReferences()
    {
        using var scope = new CelestialXmlTestScope();
        var document = new XmlDocument();
        document.LoadXml("<PioneerFramework.Celestial.CelestialSystemDef><defName>PF_ProbeSystem</defName>" +
            "<rootSurfaceEntry>earth</rootSurfaceEntry><entries><li><key>earth</key><bodyDef>PF_Earth</bodyDef>" +
            "<count><value>1</value></count></li><li><key>moon</key><bodyDef>PF_Moon</bodyDef>" +
            "<parentEntry>earth</parentEntry><count><min>1</min><max>3</max></count>" +
            "<initializationReferences><li><key>primary</key><entry>earth</entry><scope>Parent</scope></li>" +
            "</initializationReferences></li></entries></PioneerFramework.Celestial.CelestialSystemDef>");
        var definition = DirectXmlToObject.ObjectFromXml<CelestialSystemDef>(document.DocumentElement, false);
        // Resolve references explicitly: the native cross-reference loader completes these after parsing all Defs.
        var body = scope.ParseBody(CelestialDefinitionTests.BodyXml("<value>30</value>"));
        definition.entries[0].bodyDef = body;
        definition.entries[1].bodyDef = body;
        var template = definition.ToTemplate();
        Assert.True(template.Success);
        Assert.Equal("PF_ProbeSystem", template.Value.DefinitionId);
        Assert.Equal(3, template.Value.Entries[1].Count.Maximum);
        Assert.Equal("earth", template.Value.Entries[1].ParentEntry);
        Assert.Equal(CelestialSpawnReferenceScope.Parent, template.Value.Entries[1].InitializationReferences["primary"].Scope);
    }

    [Fact]
    public void MissingBodyAndInvalidCountAreConfigErrors()
    {
        var definition = new CelestialSystemDef { defName = "PF_Probe", rootSurfaceEntry = "root",
            entries = new List<CelestialSystemEntryXml> { new() { key = "root", count = new() { min = 3, max = 1 } } } };
        Assert.False(definition.ToTemplate().Success);
        Assert.NotEmpty(definition.ConfigErrors());
    }
}
