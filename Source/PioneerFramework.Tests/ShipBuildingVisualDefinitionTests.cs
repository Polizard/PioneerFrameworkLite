using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using PioneerFramework.PFView.ShipBuildingVisuals;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests;

public class ShipBuildingVisualDefinitionTests
{
    [Fact]
    public void RegistryRejectsDuplicatesAndLateRegistration()
    {
        var registry = new ShipBuildingVisualRegistry();
        registry.RegisterMeshProvider("Test.Mesh.v1", new PrimitiveVisualMeshProvider());
        Assert.Throws<InvalidOperationException>(() => registry.RegisterMeshProvider("Test.Mesh.v1", new PrimitiveVisualMeshProvider()));
        registry.Freeze();
        Assert.Throws<InvalidOperationException>(() => registry.RegisterMeshProvider("Test.Other.v1", new PrimitiveVisualMeshProvider()));
    }

    [Fact]
    public void BrokenProfileDoesNotPoisonValidProfileAndWarnsOnce()
    {
        var warnings = new List<string>();
        var resolver = new VisualProfileResolver(new ShipBuildingVisualRegistry(), warnings.Add);
        var broken = new ShipBuildingVisualDef { defName = "bad", nodes = new() { new() { id = "a", meshProvider = "Missing.Provider" } } };
        Assert.False(resolver.TryResolve(broken, out _));
        Assert.False(resolver.TryResolve(broken, out _));
        Assert.Single(warnings);
        var good = new ShipBuildingVisualDef { defName = "good", nodes = new() { new() { id = "a", mesh = "box" } } };
        Assert.True(resolver.TryResolve(good, out var result));
        Assert.Equal(1, result.Core.NodeCount);
    }

    [Fact]
    public void DefRejectsBadLodAndNonFiniteStyle()
    {
        var invalid = new ShipBuildingVisualDef { defName = "invalid", detailedPixels = 1, hiddenPixels = 2, nodes = new() { new() { id = "a", color = new Color(float.NaN, 0, 0) } } };
        Assert.NotEmpty(invalid.VisualErrors());
    }

    [Fact]
    public void BundleReferenceHasUnambiguousPackageAssetAndSubmesh()
    {
        var reference = VisualBundleReference.Parse("Example.Ships::assets/models/turret.asset#2");
        Assert.Equal("Example.Ships", reference.PackageId);
        Assert.Equal("assets/models/turret.asset", reference.AssetName);
        Assert.Equal(2, reference.Submesh);
        Assert.Throws<ArgumentException>(() => VisualBundleReference.Parse("../turret"));
        Assert.Throws<ArgumentException>(() => VisualBundleReference.Parse("Example.Ships::assets/turret.asset#-1"));
    }

    [Fact]
    public void ShippedProfilesDeserializeAndCompileUsingRealXmlFieldTypes()
    {
        using var scope = new CelestialXmlTestScope();
        var xml = new System.Xml.XmlDocument();
        xml.Load(Path.Combine(AppContext.BaseDirectory, "Defs", "PFView", "ShipBuildingVisuals.xml"));
        var resolver = new VisualProfileResolver(new ShipBuildingVisualRegistry(), error => throw new Exception(error));
        foreach (System.Xml.XmlNode element in xml.DocumentElement.ChildNodes)
        {
            if (element.NodeType != System.Xml.XmlNodeType.Element) continue;
            var def = Verse.DirectXmlToObject.ObjectFromXml<ShipBuildingVisualDef>(element, false);
            Assert.Equal(element.SelectNodes("nodes/li").Count, def.nodes.Count);
            Assert.True(resolver.TryResolve(def, out var profile));
            Assert.InRange(profile.Core.NodeCount, 1, 32);
        }
    }

    [Fact]
    public void NativePatchesReuseExistingExtensionListAndBindEveryBuilding()
    {
        using var scope = new CelestialXmlTestScope();
        var defs = new System.Xml.XmlDocument();
        defs.LoadXml("""
            <Defs>
              <ThingDef><defName>PF_Building_Turret_Cannon</defName><modExtensions><li Class="Example.Extension"/></modExtensions></ThingDef>
              <ThingDef><defName>PF_Building_Turret_Laser</defName></ThingDef>
              <ThingDef><defName>PF_Building_ActiveRadar</defName></ThingDef>
              <ThingDef><defName>PF_Building_SensorArray</defName></ThingDef>
            </Defs>
            """);
        var patch = new System.Xml.XmlDocument();
        patch.Load(Path.Combine(AppContext.BaseDirectory, "Patches", "PFViewShipBuildingVisuals.xml"));
        foreach (System.Xml.XmlNode operation in patch.SelectNodes("/Patch/Operation"))
            Assert.True(Verse.DirectXmlToObject.ObjectFromXml<Verse.PatchOperation>(operation, false).Apply(defs));

        foreach (System.Xml.XmlNode building in defs.SelectNodes("/Defs/ThingDef"))
        {
            Assert.Equal(1, building.SelectNodes("modExtensions").Count);
            Assert.Equal(1, building.SelectNodes($"modExtensions/li[@Class='{typeof(ShipBuildingVisualExtension).FullName}']").Count);
        }
        Assert.NotNull(defs.SelectSingleNode("//li[@Class='Example.Extension']"));
    }

    [Fact]
    public void ShippedBindingsIncludeTurretRadarAndThirdStaticBuilding()
    {
        var root = AppContext.BaseDirectory;
        var profiles = XDocument.Load(Path.Combine(root, "Defs", "PFView", "ShipBuildingVisuals.xml"));
        var patch = XDocument.Load(Path.Combine(root, "Patches", "PFViewShipBuildingVisuals.xml"));
        var names = profiles.Root.Elements().Select(e => e.Element("defName")?.Value).ToHashSet();
        foreach (var visual in patch.Descendants("visual")) Assert.Contains(visual.Value, names);
        string text = patch.ToString();
        Assert.Contains("PF_Building_ActiveRadar", text);
        Assert.Contains("PF_Building_SensorArray", text);
        Assert.Contains("PF_Building_Turret", text);
    }
}
