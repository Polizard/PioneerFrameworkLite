using System;
using System.Linq;
using System.Xml;
using PioneerFramework.Celestial;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    public class CelestialConfigValidationTests
    {
        [Theory]
        [InlineData("<value>30</value><min>10</min><max>40</max>", "ParameterFormConflict")]
        [InlineData("<min>10</min>", "ParameterFormMissing")]
        [InlineData("<min>40</min><max>10</max>", "InvalidParameterRange")]
        [InlineData("<value>NaN</value>", "InvalidParameterRange")]
        [InlineData("<value>30</value><typo>1</typo>", "UnknownParameterField")]
        [InlineData("<value>30</value><value>40</value>", "DuplicateParameterField")]
        public void ConflictingOrInvalidNumericFormsAreRejected(string body, string expected)
        {
            var document = new XmlDocument();
            document.LoadXml("<radiusMeters>" + body + "</radiusMeters>");
            var parameter = DirectXmlToObject.ObjectFromXml<CelestialDoubleParameterXml>(document.DocumentElement, false);
            var result = parameter.ToSpec();
            Assert.False(result.Success);
            Assert.Equal(expected, Assert.Single(result.Errors).Code);
        }

        [Fact]
        public void BodyWithoutGeometryCannotBecomeARuntimeTemplate()
        {
            var definition = new CelestialBodyDef { defName = "PF_Empty", category = "Planet" };
            var result = definition.ToTemplate();
            Assert.False(result.Success);
            Assert.Equal("MissingGeometry", Assert.Single(result.Errors).Code);
        }

        [Fact]
        public void DuplicateComponentKeysDoNotLoadAsAnIncompleteBody()
        {
            var definition = new CelestialBodyDef { defName = "PF_Duplicate" };
            definition.comps.Add(new TestCelestialGeometryProperties { key = "shape" });
            definition.comps.Add(new TestCelestialGeometryProperties { key = "shape" });
            Assert.Equal("DuplicateCompKey", Assert.Single(definition.ToTemplate().Errors).Code);
        }

        [Fact]
        public void NativeXmlReportsUnknownComponentFields()
        {
            using var scope = new CelestialXmlTestScope();
            Assert.NotNull(scope.ParseBody(CelestialDefinitionTests.BodyXml("<value>30</value>")));
            Assert.ThrowsAny<Exception>(() => scope.ParseBody(CelestialDefinitionTests.BodyXml("<value>30</value>")
                .Replace("</li>", "<unknownField>1</unknownField></li>", StringComparison.Ordinal)));
        }
    }
}
