using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialBuiltinCompTableTests
{
    // 这些字符串进入存档与扩展契约：顺序、拼写、版本都必须冻结。
    private static readonly string[] ExpectedHandlers =
    {
        "PFL.Sphere", "PFL.Surface", "PFL.ExternalPhysicsBinding", "PFL.Atmosphere", "PFL.LightSource",
        "PFL.ExistingEnvironment", "PFL.MapGeneration", "PFL.Biomes",
        "PFL.Visual.Surface", "PFL.Visual.Atmosphere", "PFL.Visual.Clouds", "PFL.Visual.Stellar"
    };

    private static readonly string[] ExpectedCapabilities =
    {
        "geometry", "surface", "physics.binding", "atmosphere", "atmosphere.composition", "light.source",
        "environment.existing", "map.generation", "biomes",
        "visual.surface", "visual.atmosphere", "visual.clouds", "visual.stellar"
    };

    [Fact]
    public void TableDeclaresEveryBuiltinHandlerInStableOrderWithFrozenVersions()
    {
        Assert.Equal(ExpectedHandlers, CelestialBuiltinComps.All.Select(row => row.Handler).ToArray());
        var expectedVersions = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["PFL.Sphere"] = 1, ["PFL.Surface"] = 1, ["PFL.ExternalPhysicsBinding"] = 1, ["PFL.Atmosphere"] = 2,
            ["PFL.LightSource"] = 1, ["PFL.ExistingEnvironment"] = 2, ["PFL.MapGeneration"] = 3, ["PFL.Biomes"] = 2,
            ["PFL.Visual.Surface"] = 2, ["PFL.Visual.Atmosphere"] = 2, ["PFL.Visual.Clouds"] = 2, ["PFL.Visual.Stellar"] = 2
        };
        Assert.Equal(ExpectedHandlers.Length, expectedVersions.Count);
        foreach (var row in CelestialBuiltinComps.All)
        {
            Assert.Equal(expectedVersions[row.Handler], row.ConfigurationVersion);
            Assert.True(typeof(ICelestialResolvedProperties).IsAssignableFrom(row.PropertiesType), row.Handler);
            Assert.NotEmpty(row.Capabilities);
            Assert.All(row.Capabilities, capability =>
            {
                Assert.False(string.IsNullOrWhiteSpace(capability.Name));
                Assert.True(capability.Contract.IsInterface);
            });
        }
    }

    [Fact]
    public void EveryFrameworkCompPropertiesClassIsRegisteredInTheTable()
    {
        // 新增内置 comp 却忘记改表 = 存档恢复静默 HandlerMissing；这里把“框架程序集里的所有内置 comp props”也锁进表。
        var discovered = typeof(CelestialCompProperties).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && type.IsSubclassOf(typeof(CelestialCompProperties)))
            .Select(type => ((CelestialCompProperties)Activator.CreateInstance(type)!).HandlerId)
            .OrderBy(handler => handler, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ExpectedHandlers.OrderBy(handler => handler, StringComparer.Ordinal), discovered);
    }

    [Fact]
    public void TableCapabilitiesMatchTheActualCompPropertiesHandlersAndProvides()
    {
        foreach (var row in CelestialBuiltinComps.All)
        {
            var properties = BuiltinProperties(row.Handler);
            Assert.Equal(row.Handler, properties.HandlerId);
            var declared = row.Capabilities.Select(capability => capability.Name).ToArray();
            Assert.All(properties.Descriptor.Provides, name => Assert.Contains(name, declared));
            if (row.Handler != AtmosphereResolvedProperties.Handler)
                Assert.Equal(declared.OrderBy(name => name, StringComparer.Ordinal),
                    properties.Descriptor.Provides.OrderBy(name => name, StringComparer.Ordinal));
        }
        // 大气在压力模式下同时提供 composition；表声明的是该 handler 的提供全集。
        var pressureAtmosphere = new CompProperties_Atmosphere { key = "probe", pressurePascals = new() { value = 101325 } };
        Assert.Equal(2, pressureAtmosphere.ConfigurationVersion);
        Assert.Equal(
            CelestialBuiltinComps.All.Single(row => row.Handler == AtmosphereResolvedProperties.Handler)
                .Capabilities.Select(capability => capability.Name).OrderBy(name => name, StringComparer.Ordinal),
            pressureAtmosphere.Descriptor.Provides.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void TableCapabilitiesMatchTheCapabilityRegistryExactly()
    {
        var declared = CelestialBuiltinComps.All.SelectMany(row => row.Capabilities).ToArray();
        Assert.Equal(ExpectedCapabilities, declared.Select(capability => capability.Name).ToArray());
        Assert.Equal(declared.Length, CelestialCapabilityRegistry.Global.ContractCount);
        foreach (var (name, contract) in declared)
        {
            Assert.True(CelestialCapabilityRegistry.Global.TryGetContract(name, out var registered), name);
            Assert.Equal(contract, registered);
            Assert.True(registered.IsInterface);
        }
    }

    [Fact]
    public void RegistryCoversEveryTableRowAndNothingElse()
    {
        foreach (var registry in new[] { CelestialBuiltinCompRegistry.Global, CelestialBuiltinCompRegistry.Create() })
        {
            foreach (var row in CelestialBuiltinComps.All)
            {
                Assert.True(registry.TryGet(row.Handler, out var registration), row.Handler);
                Assert.Equal(row.ConfigurationVersion, registration.ConfigurationVersion);
                Assert.Equal(1, registration.SchemaVersion);
                Assert.NotNull(registration.Factory);
                Assert.NotNull(registration.Codec);
            }
            Assert.Equal(ExpectedHandlers.OrderBy(handler => handler, StringComparer.Ordinal),
                StoredHandlers(registry).OrderBy(handler => handler, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void MissingDecodeOrFactoryCoverageThrowsInsteadOfSilentlySkipping()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => { CelestialBuiltinCompRegistry.HandlerFor("PFL.NotABuiltinComp"); });
        Assert.Contains("MissingBuiltinHandlerCoverage", exception.Message);
    }

    [Fact]
    public void BuiltinTypeCheckFollowsTheTableWhileExtensionHandlersRemainUnchecked()
    {
        var sphere = new CompProperties_SphericalBody { key = "shape" };
        var wrongType = Assert.Throws<ArgumentException>(() => { sphere.Create(new WrongSphereProperties("shape")); });
        Assert.Contains("wrong type", wrongType.Message);
        var created = sphere.Create(new SphericalBodyResolvedProperties("shape", 42));
        Assert.Equal(42, Assert.IsType<SphericalBodyResolvedProperties>(created.ResolvedProperties).RadiusMeters);
        var extension = new ExtensionCompProperties { key = "probe" };
        var extensionComponent = extension.Create(new ExtensionResolvedProperties("probe"));
        Assert.Equal("test.extension.probe", extensionComponent.ResolvedProperties.HandlerId);
    }

    private static IEnumerable<string> StoredHandlers(CelestialCompRegistry registry)
    {
        var field = typeof(CelestialCompRegistry).GetField("handlers", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var stored = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, CelestialCompRegistration>>>(field!.GetValue(registry));
        return stored.Select(pair => pair.Key);
    }

    private static CelestialCompProperties BuiltinProperties(string handler) => handler switch
    {
        SphericalBodyResolvedProperties.Handler => new CompProperties_SphericalBody { key = "probe" },
        SurfaceResolvedProperties.Handler => new CompProperties_Surface { key = "probe" },
        ExternalPhysicsResolvedProperties.Handler => new CompProperties_ExternalPhysicsBinding { key = "probe" },
        AtmosphereResolvedProperties.Handler => new CompProperties_Atmosphere { key = "probe" },
        LightSourceResolvedProperties.Handler => new CompProperties_LightSource { key = "probe" },
        ExistingEnvironmentResolvedProperties.Handler => new CompProperties_ExistingEnvironment { key = "probe" },
        MapGenerationResolvedProperties.Handler => new CompProperties_MapGeneration { key = "probe" },
        BiomeGenerationResolvedProperties.Handler => new CompProperties_BiomeGeneration { key = "probe" },
        "PFL.Visual.Surface" => new CompProperties_CelestialVisual { key = "probe" },
        "PFL.Visual.Atmosphere" => new CompProperties_AtmosphereVisual { key = "probe" },
        "PFL.Visual.Clouds" => new CompProperties_CloudsVisual { key = "probe" },
        "PFL.Visual.Stellar" => new CompProperties_StellarVisual { key = "probe" },
        _ => throw new ArgumentOutOfRangeException(nameof(handler), handler, "No builtin comp properties for this handler.")
    };

    private sealed class WrongSphereProperties(string key) : ICelestialResolvedProperties
    {
        public string Key => key;
        public string HandlerId => SphericalBodyResolvedProperties.Handler;
        public int Version => 1;
        public ReadOnlyMemory<byte> Serialize() => ReadOnlyMemory<byte>.Empty;
    }

    private sealed class ExtensionCompProperties : CelestialCompProperties
    {
        public override string HandlerId => "test.extension.probe";
        public override CelestialBodyComp CreateComp() => new ProbeComp(Descriptor);
        public override CelestialResult<ICelestialResolvedProperties> Resolve(CelestialGenerationContext context) =>
            CelestialResult.Reject<ICelestialResolvedProperties>(new CelestialError("ExtensionNotResolved"));
    }

    private sealed class ExtensionResolvedProperties(string key) : ICelestialResolvedProperties
    {
        public string Key => key;
        public string HandlerId => "test.extension.probe";
        public int Version => 1;
        public ReadOnlyMemory<byte> Serialize() => ReadOnlyMemory<byte>.Empty;
    }

    private sealed class ProbeComp(CelestialCompDescriptor descriptor) : CelestialBodyComp(descriptor) { }
}
