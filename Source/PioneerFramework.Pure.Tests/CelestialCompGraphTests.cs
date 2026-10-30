using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class CelestialCompGraphTests
{
    [Fact]
    public void CapabilityDependencyPrecedesOrdinalKeyOrder()
    {
        var shape = Comp("zShape", new[] { "geometry" }, slot: "geometry");
        var surface = Comp("aSurface", new[] { "surface" }, new[] { "geometry" }, "surface");
        var result = CelestialCompGraph.Resolve(new[] { surface, shape });
        Assert.True(result.Success);
        Assert.Equal(new[] { "zShape", "aSurface" }, result.Value.Select(comp => comp.Key));
        Assert.Equal(result.Value.Select(comp => comp.Key),
            CelestialCompGraph.Resolve(new[] { shape, surface }).Value.Select(comp => comp.Key));
    }

    [Fact]
    public void MissingCapabilityIsRejectedWithoutAddingAComponent()
    {
        var result = CelestialCompGraph.Resolve(new[] { Comp("surface", new[] { "surface" }, new[] { "geometry" }) });
        Assert.False(result.Success);
        Assert.Equal("MissingCapability", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void DuplicateKeysAndExclusiveSlotsAreRejected()
    {
        var duplicate = CelestialCompGraph.Resolve(new[] { Comp("same", new[] { "a" }), Comp("same", new[] { "b" }) });
        Assert.Equal("DuplicateCompKey", Assert.Single(duplicate.Errors).Code);
        var conflict = CelestialCompGraph.Resolve(new[]
        {
            Comp("one", new[] { "surface" }, slot: "surface"),
            Comp("two", new[] { "surface" }, slot: "surface")
        });
        Assert.Equal("ExclusiveCapabilityConflict", Assert.Single(conflict.Errors).Code);
    }

    [Fact]
    public void CyclicCapabilitiesAreRejected()
    {
        var result = CelestialCompGraph.Resolve(new[]
        {
            Comp("one", new[] { "a" }, new[] { "b" }),
            Comp("two", new[] { "b" }, new[] { "a" })
        });
        Assert.Equal("CompDependencyCycle", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void ExplicitOrderTargetsMustExist()
    {
        var result = CelestialCompGraph.Resolve(new[]
        {
            new CelestialCompDescriptor("one", "sample.one", 1, after: new[] { "missing" })
        });
        Assert.Equal("MissingCompOrderTarget", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void CallerCannotChangeAnExistingDependencyGraph()
    {
        var dependencies = new List<string> { "geometry" };
        var surface = new CelestialCompDescriptor("surface", "sample.surface", 1, requires: dependencies);
        dependencies.Clear();
        Assert.False(CelestialCompGraph.Resolve(new[] { surface }).Success);
    }

    private static CelestialCompDescriptor Comp(string key, string[] provides, string[]? requires = null, string? slot = null)
        => new(key, "sample." + key, 1, provides, requires, slot);
}
