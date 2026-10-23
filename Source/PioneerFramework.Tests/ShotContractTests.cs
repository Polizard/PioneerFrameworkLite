using System;
using System.IO;
using System.Xml.Linq;
using PioneerFramework.Combat;
using Xunit;

namespace PioneerFramework.Tests
{
    public class ShotContractTests
    {
        [Fact]
        public void PublicShotContractSupportsXmlComponentFactories()
        {
            var assembly = typeof(WeaponDef).Assembly;
            var definition = assembly.GetType("PioneerFramework.Combat.ShotDef");
            Assert.NotNull(definition);
            Assert.True(typeof(Verse.Def).IsAssignableFrom(definition));
            var properties = assembly.GetType("PioneerFramework.Combat.ShotCompProperties");
            Assert.NotNull(properties);
            Assert.NotNull(properties.GetMethod("CreateComp"));
            Assert.NotNull(definition.GetField("comps"));
        }

        [Fact]
        public void CannonAndLaserDeclareXmlShotDefinitions()
        {
            var xml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Defs", "Combat", "WeaponDefs.xml"));
            foreach (string weapon in new[] { "PF_Weapon_ProjectileCannon", "PF_Weapon_Laser" })
            {
                var element = Assert.Single(xml.Root.Elements(), x => (string)x.Element("defName") == weapon);
                Assert.False(string.IsNullOrEmpty((string)element.Element("defaultShot")));
                Assert.NotEmpty(element.Element("allowedShots").Elements("li"));
            }
        }
    }
}
