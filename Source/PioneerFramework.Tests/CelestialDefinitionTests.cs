using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml;
using HarmonyLib;
using PioneerFramework.Celestial;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    public class CelestialDefinitionTests
    {
        [Fact]
        public void NativeXmlLoadsFixedAndRangeParametersWithoutImplicitSurface()
        {
            using var scope = new CelestialXmlTestScope();
            var fixedBody = scope.ParseBody(BodyXml("<value>1500000</value>"));
            var rangeBody = scope.ParseBody(BodyXml("<min>1000000</min><max>1800000</max>"));
            var fixedTemplate = fixedBody.ToTemplate();
            var rangeTemplate = rangeBody.ToTemplate();
            Assert.True(fixedTemplate.Success);
            Assert.True(rangeTemplate.Success);
            Assert.False(fixedTemplate.Value.HasCapability("surface"));
            var fixedResolved = fixedTemplate.Value.Resolve(new CelestialGenerationContext(1, "moon[0]"));
            var rangeResolved = rangeTemplate.Value.Resolve(new CelestialGenerationContext(1, "moon[1]"));
            Assert.Equal(1500000, Assert.IsType<TestGeometryProperties>(Assert.Single(fixedResolved.Value).Properties).RadiusMeters);
            Assert.InRange(Assert.IsType<TestGeometryProperties>(Assert.Single(rangeResolved.Value).Properties).RadiusMeters, 1000000, 1800000);
        }

        [Fact]
        public void NativeAbstractInheritanceProducesAReusableBodyTemplate()
        {
            using var scope = new CelestialXmlTestScope();
            var document = new XmlDocument();
            document.LoadXml("<Defs><PioneerFramework.CelestialBodyDef Name=\"CelestialProbeBase\" Abstract=\"True\">" +
                "<category>Moon</category><comps><li Class=\"PioneerFramework.Tests.TestCelestialGeometryProperties\">" +
                "<key>shape</key><radiusMeters><value>30</value></radiusMeters></li></comps></PioneerFramework.CelestialBodyDef>" +
                "<PioneerFramework.CelestialBodyDef ParentName=\"CelestialProbeBase\"><defName>PF_ProbeChild</defName>" +
                "<label>child</label></PioneerFramework.CelestialBodyDef></Defs>");
            foreach (XmlNode node in document.DocumentElement.ChildNodes) XmlInheritance.TryRegister(node, null);
            scope.InheritanceUsed = true;
            XmlInheritance.Resolve();
            var inherited = DirectXmlToObject.ObjectFromXml<CelestialBodyDef>(document.DocumentElement.LastChild, false);
            var result = inherited.ToTemplate();
            Assert.True(result.Success);
            Assert.Equal("Moon", result.Value.Category);
            Assert.True(result.Value.HasCapability("geometry"));
            Assert.False(result.Value.HasCapability("surface"));
        }

        [Fact]
        public void ExistingResolvedValuesRemainIndependentOfLaterDefinitionChanges()
        {
            using var scope = new CelestialXmlTestScope();
            var definition = scope.ParseBody(BodyXml("<value>30</value>"));
            definition.label = " intentional spacing ";
            var template = definition.ToTemplate().Value;
            var resolved = template.Resolve(new CelestialGenerationContext(1, "moon")).Value;
            ((TestCelestialGeometryProperties)definition.comps[0]).radiusMeters.value = 99;
            Assert.Equal(30, Assert.IsType<TestGeometryProperties>(resolved[0].Properties).RadiusMeters);
            Assert.Equal(" intentional spacing ", template.Label);
        }

        internal static string BodyXml(string parameter) =>
            "<PioneerFramework.CelestialBodyDef><defName>PF_XmlProbe</defName><label>probe</label><category>Planet</category>" +
            "<comps><li Class=\"PioneerFramework.Tests.TestCelestialGeometryProperties\"><key>shape</key><radiusMeters>" +
            parameter + "</radiusMeters></li></comps></PioneerFramework.CelestialBodyDef>";
    }

    public sealed class TestCelestialGeometryProperties : CelestialCompProperties
    {
        public CelestialDoubleParameterXml radiusMeters = new() { value = 30 };
        public override string HandlerId => "test.geometry";
        protected override IEnumerable<string> Provides => new[] { "geometry" };
        protected override string ExclusiveSlot => "geometry";
        public override CelestialBodyComp CreateComp() => new TestGeometryComp(Descriptor);
        public override CelestialResult<ICelestialResolvedProperties> Resolve(CelestialGenerationContext context)
        {
            var specification = radiusMeters.ToSpec();
            return specification.Success
                ? CelestialResult.Ok<ICelestialResolvedProperties>(new TestGeometryProperties(key, context.SampleDouble("radiusMeters", specification.Value)))
                : CelestialResult.Reject<ICelestialResolvedProperties>(specification.Errors.ToArray());
        }
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            foreach (var error in radiusMeters.ToSpec().Errors) yield return error.Code;
        }
    }

    public sealed record TestGeometryProperties(string Key, double RadiusMeters) : ICelestialGeometryProperties
    {
        public string HandlerId => "test.geometry";
        public int Version => 1;
        public ReadOnlyMemory<byte> Serialize() => BitConverter.GetBytes(RadiusMeters);
    }

    public sealed class TestGeometryComp : CelestialBodyComp, ICelestialGeometryCapability
    {
        public TestGeometryComp(CelestialCompDescriptor descriptor) : base(descriptor) { }
        public CelestialGeometryDescription GetDescription() => new("sphere", ((ICelestialGeometryProperties)Context.Properties).RadiusMeters, "test.fixed");
        public DVector3 FixedPosition(double latitudeRadians, double longitudeRadians)
            => new SphericalBodyResolvedProperties(Descriptor.Key, ((ICelestialGeometryProperties)Context.Properties).RadiusMeters).FixedPosition(latitudeRadians, longitudeRadians);
    }

    internal sealed class CelestialXmlTestScope : IDisposable
    {
        private readonly Harmony patch = new("PFL.Tests.CelestialXml");
        private readonly System.Reflection.FieldInfo prefs = AccessTools.Field(typeof(Prefs), "data");
        private readonly object previous;
        private readonly List<(System.Reflection.FieldInfo Field, object Value)> previousReferences = new();
        public bool InheritanceUsed { get; set; }
        public CelestialXmlTestScope()
        {
            previous = prefs.GetValue(null);
            if (previous == null) prefs.SetValue(null, RuntimeHelpers.GetUninitializedObject(prefs.FieldType));
            // XML 解析器的交叉引用队列属于全局状态；每个夹具独立使用并原样恢复之前的队列。
            foreach (string name in new[] { "wantedRefs", "wantedListDictRefs" })
            {
                var field = AccessTools.Field(typeof(DirectXmlCrossRefLoader), name);
                previousReferences.Add((field, field.GetValue(null)));
                field.SetValue(null, Activator.CreateInstance(field.FieldType));
            }
            patch.Patch(AccessTools.Method(typeof(GenTypes), nameof(GenTypes.GetTypeInAnyAssembly), new[] { typeof(string), typeof(string) }),
                prefix: new HarmonyMethod(typeof(CelestialXmlTestScope), nameof(ResolveType)));
            patch.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(CelestialXmlTestScope), nameof(ThrowXmlError)));
        }
        public CelestialBodyDef ParseBody(string xml)
        {
            var document = new XmlDocument();
            document.LoadXml(xml);
            return DirectXmlToObject.ObjectFromXml<CelestialBodyDef>(document.DocumentElement, false);
        }
        private static bool ResolveType(string typeName, ref Type __result)
        {
            __result = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(typeName)).FirstOrDefault(type => type != null);
            return __result == null;
        }
        internal static Func<string, bool> ExpectedError;
        private static bool ThrowXmlError(string text)
        {
            if (ExpectedError?.Invoke(text) == true) return false;
            throw new InvalidOperationException(text);
        }
        public void Dispose()
        {
            if (InheritanceUsed) XmlInheritance.Clear();
            foreach (var reference in previousReferences) reference.Field.SetValue(null, reference.Value);
            patch.UnpatchAll(patch.Id);
            prefs.SetValue(null, previous);
        }
    }
}
