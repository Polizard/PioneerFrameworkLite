using System;
using System.Linq;
using System.Reflection;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialPublicApiTests
{
    [Fact]
    public void SealedCapabilityContractsCannotChangeValidationForExistingComponents()
    {
        var registry = new CelestialCapabilityRegistry();
        Assert.True(registry.Register<ICelestialGeometryCapability>("geometry").Success);
        var seal = typeof(CelestialCapabilityRegistry).GetMethod("Seal");
        Assert.NotNull(seal);
        seal.Invoke(registry, null);
        Assert.Contains(registry.Register<ICelestialSurfaceCapability>("extension.late").Errors, error => error.Code == "RegistrySealed");
        Assert.Contains(registry.Validate(new FalseGeometry()).Errors, error => error.Code == "CapabilityContractViolation");
    }
    [Fact]
    public void FactoryBindsSuppliedConfigurationAndCapabilityMismatchIsRejected()
    {
        var properties = new SphericalBodyResolvedProperties("shape", 42);
        var definition = new CompProperties_SphericalBody { key = "shape" };
        var component = definition.Create(properties);
        definition.radiusMeters.value = 999;
        Assert.Same(properties, component.ResolvedProperties);
        Assert.Equal(42, Assert.IsType<SphericalBodyResolvedProperties>(component.ResolvedProperties).RadiusMeters);
        var violation = CelestialCapabilityRegistry.Global.Validate(new FalseGeometry());
        Assert.Equal("CapabilityContractViolation", Assert.Single(violation.Errors).Code);
    }
    private sealed class FalseGeometry() : CelestialBodyComp(new("shape", "extension.false", 1, provides: new[] { "geometry" }));
    [Fact]
    public void ExtensionsCannotAdvanceTheHostOrSupplyExecutableCreationManifests()
    {
        Assert.Null(typeof(CelestialRuntime).GetMethod("Advance", BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(typeof(CelestialRuntime).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        var queryMethods = typeof(ICelestialQueryService).GetMethods().Concat(typeof(ICelestialCapabilityResolver).GetMethods()).Select(method => method.Name).ToHashSet();
        Assert.Empty(typeof(CelestialRuntime).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName && !queryMethods.Contains(method.Name)));
        Assert.Null(typeof(CelestialBodyManager).GetMethod("TryGetCelestialRuntime", BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(typeof(CelestialBodyManager).GetMethod("AttachCelestialRuntime", BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(typeof(ICelestialBodyService).GetMethods().SelectMany(m => m.GetParameters()),
            parameter => parameter.ParameterType == typeof(CreateBatchRequest) || parameter.ParameterType == typeof(CreateBodyRequest));
        Assert.NotNull(typeof(CelestialRuntime).Assembly.GetType("PioneerFramework.Celestial.ICelestialQueryService"));
    }

    [Fact]
    public void DiagnosticsHaveActionableRetryAndOpenErrorCodes()
    {
        var error = new CelestialError("ProviderUnavailable");
        var property = typeof(CelestialError).GetProperty("RetryDisposition");
        Assert.NotNull(property);
        Assert.Equal("WhenProviderReady", property!.GetValue(error)!.ToString());
        Assert.Equal("extension.NewFailure", new CelestialError("extension.NewFailure").Code);
    }
    [Fact]
    public void DataOverrideForecastAndLocalisationExposeTheirRequiredPublicShape()
    {
        Assert.NotNull(typeof(CelestialCreateRequest).GetProperty("ParameterOverrides"));
        Assert.NotNull(typeof(CelestialForecastSource).GetProperty("PredictionSchemaVersion"));
        Assert.NotNull(typeof(CelestialError).GetProperty("LocalizationKey"));
    }
    [Theory]
    [InlineData("ProviderUnavailable", "PF_CelestialError_Availability")]
    [InlineData("OperationConflict", "PF_CelestialError_Conflict")]
    [InlineData("ComponentAdvanceFailed", "PF_CelestialError_Component")]
    [InlineData("ConsistencyFault", "PF_CelestialError_Consistency")]
    [InlineData("OperationExpired", "PF_CelestialError_Expired")]
    [InlineData("InvalidMapSize", "PF_CelestialError_Validation")]
    public void ErrorLocalisationUsesTheActionableCategoryKey(string code, string expected)
        => Assert.Equal(expected, new CelestialError(code).LocalizationKey);
}
