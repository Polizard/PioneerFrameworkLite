using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using PioneerFramework.Landing;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Xunit;
namespace PioneerFramework.Tests;
public class LandingDefContractTests
{
    private static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "1.7", "Defs")) && Directory.Exists(Path.Combine(d.FullName, "Source"))) return d.FullName;
        throw new DirectoryNotFoundException("Repository root");
    }
    [Fact]
    public void TranslationsAreSymmetricAndUnique()
    {
        var root = Root();
        var en = XDocument.Load(Path.Combine(root, "1.7/Languages/English/Keyed/Landing.xml")).Root.Elements().ToList();
        var zh = XDocument.Load(Path.Combine(root, "1.7/Languages/ChineseSimplified/Keyed/Landing.xml")).Root.Elements().ToList();
        Assert.Equal(en.Count, en.Select(x => x.Name.LocalName).Distinct().Count());
        Assert.Equal(zh.Count, zh.Select(x => x.Name.LocalName).Distinct().Count());
        Assert.Equal(en.Select(x => x.Name.LocalName).OrderBy(x => x), zh.Select(x => x.Name.LocalName).OrderBy(x => x));
    }
    [Fact]
    public void NewDefFieldsAndTexturePathsMatchRuntimeContracts()
    {
        var root = Root();
        string[] files = { "WorldObjectDefs/WorldObject_LandingTransit.xml", "WorldObjectDefs/WorldObject_LandedShipSite.xml", "WorldObjectDefs/WorldObject_LandedGroup.xml", "ThingDefs/Thing_RecoveredSpacecraft.xml", "Misc/LandingRecoveryDefs.xml" };
        foreach (string file in files)
        {
            var def = XDocument.Load(Path.Combine(root, "1.7/Defs", file)).Root.Elements().Single();
            Type type = def.Name.LocalName == "WorldObjectDef" ? typeof(WorldObjectDef) : def.Name.LocalName == "ThingDef" ? typeof(ThingDef) : typeof(LandingRecoveryDef);
            foreach (var field in def.Elements()) Assert.True(type.GetField(field.Name.LocalName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null, file + ": " + field.Name.LocalName);
            // Core textures are packed assets. Both paths were verified against 1.7 Core's TravellingTransporters Def.
            string[] coreTextures = { "World/WorldObjects/TravelingTransportPods", "World/WorldObjects/Expanding/TravelingTransportPods" };
            var textures = def.Descendants().Where(e => e.Name.LocalName is "texture" or "texturePath" or "texPath" or "expandingIconTexture");
            foreach (var texture in textures.Select(e => e.Value))
                Assert.True(File.Exists(Path.Combine(root, "1.7/Textures", texture + ".png")) || coreTextures.Contains(texture), texture);
        }
    }
    [Fact]
    public void RecoveredItemMassUsesPayloadThroughRealStatPart()
    {
        var item = new RecoveredSpacecraft { Payload = new LandingEntityPayload { DryMass = 100, FuelMass = 20 } };
        float mass = 1;
        new StatPart_RecoveredSpacecraftMass().TransformValue(StatRequest.For(item), ref mass);
        Assert.Equal(120, mass);
    }
}
