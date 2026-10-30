using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class CelestialSystemGenerationTests
{
    [Fact]
    public void PerParentExpandsIndependentlyAndSiblingOrderDoesNotChangeValues()
    {
        var entries = new[] { Entry("root", 1, surface: true), Entry("parents", 2, "root"), Entry("moons", 3, "parents") };
        var first = Expand(entries);
        var second = Expand(entries.Reverse());
        Assert.True(first.Success);
        Assert.True(second.Success);
        var moons = first.Value.Items.Where(item => item.EntryKey == "moons").ToArray();
        Assert.Equal(6, moons.Length);
        Assert.Equal(6, moons.Select(item => item.Id).Distinct().Count());
        Assert.All(moons.GroupBy(item => item.ParentId), group => Assert.Equal(3, group.Count()));
        Assert.Equal(first.Value.Items.Select(Signature), second.Value.Items.Select(Signature));
        Assert.Equal(1, first.Value.Items.Count(item => item.Id == first.Value.RootSurfaceId));
    }

    [Fact]
    public void EmptyParentProducesNoChildrenAndUnrelatedBranchStaysStable()
    {
        var first = Expand(new[] { Entry("root", 1, surface: true), Entry("empty", 0), Entry("child", 3, "empty") });
        var second = Expand(new[] { Entry("root", 1, surface: true) });
        Assert.Single(first.Value.Items);
        Assert.Equal(Signature(second.Value.Items[0]), Signature(first.Value.Items[0]));
    }

    [Theory]
    [InlineData("DuplicateEntryKey")]
    [InlineData("ParentEntryCycle")]
    [InlineData("MissingParentEntry")]
    [InlineData("MissingRootSurface")]
    [InlineData("AmbiguousRootSurface")]
    [InlineData("RootHasNoSurface")]
    [InlineData("InvalidSpawnCount")]
    public void InvalidManifestIsRejectedBeforePublication(string error)
    {
        var entries = error switch
        {
            "DuplicateEntryKey" => new[] { Entry("root", 1, surface: true), Entry("root", 1) },
            "ParentEntryCycle" => new[] { Entry("root", 1, "moon", true), Entry("moon", 1, "root") },
            "MissingParentEntry" => new[] { Entry("root", 1, "missing", true) },
            "MissingRootSurface" => new[] { Entry("other", 1, surface: true) },
            "AmbiguousRootSurface" => new[] { Entry("root", 2, surface: true) },
            "RootHasNoSurface" => new[] { Entry("root", 1) },
            _ => new[] { Entry("root", -1, surface: true) },
        };
        var result = Expand(entries);
        Assert.False(result.Success);
        Assert.Equal(error, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void AmbiguousReferenceRequiresIndexOrParentContext()
    {
        var result = Expand(new[] { Entry("root", 1, surface: true), Entry("parents", 2, "root"), Entry("moon", 3, "parents") });
        var context = new CelestialSpawnContext(result.Value.Items, result.Value.Items.First(item => item.EntryKey == "moon"));
        var ambiguous = CelestialSpawnReferences.Resolve(new CelestialSpawnReference("parents"), context);
        Assert.Equal("AmbiguousReference", Assert.Single(ambiguous.Errors).Code);
        var parent = CelestialSpawnReferences.Resolve(new CelestialSpawnReference("parents", Scope: CelestialSpawnReferenceScope.Parent), context);
        Assert.Equal(context.Current.ParentId, parent.Value);
        var sibling = CelestialSpawnReferences.Resolve(new CelestialSpawnReference("moon", 1, CelestialSpawnReferenceScope.Siblings), context);
        Assert.Equal(context.Current.ParentId, result.Value.Items.Single(item => item.Id == sibling.Value).ParentId);
    }

    [Fact]
    public void InitializationReferencesAreBoundAfterAllEntriesExist()
    {
        var root = Entry("root", 1, surface: true);
        var body = new CelestialSystemEntry("a", Template(), IntParameterSpec.Fixed(1), initializationReferences:
            new Dictionary<string, CelestialSpawnReference> { ["partner"] = new("z") });
        var result = Expand(new[] { body, root, Entry("z", 1) });
        Assert.Equal(result.Value.Items.Single(item => item.EntryKey == "z").Id,
            result.Value.Items.Single(item => item.EntryKey == "a").InitializationReferences["partner"]);
    }

    [Fact]
    public void TypedOverridesAreValidatedAndCrossComponentConstraintsRunAfterResolution()
    {
        var template = Template(true);
        var descriptor = template.Components[0].Descriptor;
        var replacement = new CelestialCompDefinition(descriptor, template.Components[0].Factory,
            _ => CelestialResult.Ok<ICelestialResolvedProperties>(new Geometry(42)));
        var changed = template.WithOverrides(new[] { replacement });
        Assert.Equal(42, ((ICelestialGeometryProperties)changed.Value.Resolve(new CelestialGenerationContext(1, "root")).Value[0].Properties).RadiusMeters);
        Assert.NotEqual(42, ((ICelestialGeometryProperties)template.Resolve(new CelestialGenerationContext(1, "root")).Value[0].Properties).RadiusMeters);
        var invalid = new CelestialCompDefinition(descriptor, template.Components[0].Factory,
            _ => CelestialResult.Ok<ICelestialResolvedProperties>(new InvalidGeometry()));
        var result = template.WithOverrides(new[] { invalid }).Value.Resolve(new CelestialGenerationContext(1, "root"));
        Assert.Equal("InconsistentParameters", Assert.Single(result.Errors).Code);
    }

    private static string Signature(CelestialSpawnItem item) => item.Id + ":" +
        ((ICelestialGeometryProperties)item.Components.First(component => component.Properties is ICelestialGeometryProperties).Properties).RadiusMeters;
    private static CelestialResult<CelestialSpawnManifest> Expand(IEnumerable<CelestialSystemEntry> entries)
        => CelestialSystemGenerator.Expand(new CelestialSystemTemplate("PF_TestSystem", "root", entries), 123);
    private static CelestialSystemEntry Entry(string key, int count, string? parent = null, bool surface = false)
        => new(key, Template(surface), IntParameterSpec.Fixed(count), parent);

    internal static CelestialBodyTemplate Template(bool surface = false)
    {
        var descriptor = new CelestialCompDescriptor("shape", "test.shape", 1, provides: surface ? new[] { "geometry", "surface" } : new[] { "geometry" });
        return CelestialBodyTemplate.Create("PF_TestBody", "body", "Moon", Array.Empty<string>(), new[] {
            new CelestialCompDefinition(descriptor, new Factory(descriptor), context =>
                CelestialResult.Ok<ICelestialResolvedProperties>(new Geometry(context.SampleDouble("radius", DoubleParameterSpec.Uniform(10, 100))))) }).Value;
    }

    private sealed record Geometry(double RadiusMeters) : ICelestialGeometryProperties
    {
        public string Key => "shape";
        public string HandlerId => "test.shape";
        public int Version => 1;
        public ReadOnlyMemory<byte> Serialize() => BitConverter.GetBytes(RadiusMeters);
    }
    private sealed record InvalidGeometry : ICelestialGeometryProperties, ICelestialResolvedConfigurationValidator
    {
        public string Key => "shape";
        public string HandlerId => "test.shape";
        public int Version => 1;
        public double RadiusMeters => 20;
        public ReadOnlyMemory<byte> Serialize() => BitConverter.GetBytes(RadiusMeters);
        public CelestialResult<bool> Validate(IReadOnlyList<ICelestialResolvedProperties> components)
            => CelestialResult.Reject<bool>(new CelestialError("InconsistentParameters"));
    }
    private sealed class Factory(CelestialCompDescriptor descriptor) : ICelestialCompFactory
    {
        public CelestialBodyComp Create(ICelestialResolvedProperties properties) => new Comp(descriptor);
    }
    private sealed class Comp(CelestialCompDescriptor descriptor) : CelestialBodyComp(descriptor), ICelestialGeometryCapability, ICelestialSurfaceCapability
    {
        public CelestialGeometryDescription GetDescription() => new("sphere", ((ICelestialGeometryProperties)Context.Properties).RadiusMeters, "test.fixed");
        public DVector3 FixedPosition(double latitudeRadians, double longitudeRadians)
            => new SphericalBodyResolvedProperties(Descriptor.Key, ((ICelestialGeometryProperties)Context.Properties).RadiusMeters).FixedPosition(latitudeRadians, longitudeRadians);
        public SurfaceResolvedProperties GetProperties() => new(Descriptor.Key, CelestialSurfaceGenerationMode.OnDemand, 0, 1);
    }
}
