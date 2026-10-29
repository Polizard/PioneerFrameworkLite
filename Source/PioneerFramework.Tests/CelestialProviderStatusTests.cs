using System;
using System.Collections.Generic;
using System.Reflection;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialProviderStatusTests
{
    [Theory]
    [InlineData(PhysicsBridgeStatus.Unavailable, "ProviderUnavailable")]
    [InlineData(PhysicsBridgeStatus.Restoring, "ProviderRestoring")]
    [InlineData(PhysicsBridgeStatus.Faulted, "ProviderFaulted")]
    public void PublicSamplingRequiresReadyProviderButDescriptionsRemainReadable(PhysicsBridgeStatus status, string error)
    {
        StatusProxy control = null;
        var runtime = CelestialBenchFactory.Create(3, bridgeTransform: inner => {
            var proxy = DispatchProxy.Create<ICelestialPhysicsBridge, StatusProxy>();
            control = (StatusProxy)(object)proxy; control.Inner = inner; control.Status = PhysicsBridgeStatus.Ready; return proxy;
        });
        control.Status = status;
        var result = runtime.Sample(new(runtime.WorldSessionId, runtime.CurrentStamp.TopologyRevision, new[] { CelestialBenchFactory.Id(0) }));
        if (result.Success) result.Snapshot.Dispose();
        Assert.False(result.Success);
        Assert.Equal(error, Assert.Single(result.Errors).Code);
        Assert.True(runtime.Query(CelestialBenchFactory.Id(0)).Success);
    }

    [Fact]
    public void BuiltinBiomeDeclarationRequiresItsActualTypedInterface()
    { Assert.False(CelestialCapabilityRegistry.Global.Validate(new FalseBiome()).Success); }
    private sealed class FalseBiome() : CelestialBodyComp(new("biomes", "test.false.biomes", 1, provides: new[] { "biomes" })) { }
    [Theory]
    [InlineData(PhysicsBridgeStatus.Ready, true)]
    [InlineData(PhysicsBridgeStatus.Unavailable, false)]
    [InlineData(PhysicsBridgeStatus.Restoring, false)]
    [InlineData(PhysicsBridgeStatus.Faulted, false)]
    public void InitialConnectionRequiresReadyProvider(PhysicsBridgeStatus status, bool accepted)
    {
        var bridge = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var proxy = DispatchProxy.Create<ICelestialPhysicsBridge, StatusProxy>();
        ((StatusProxy)(object)proxy).Inner = bridge; ((StatusProxy)(object)proxy).Status = status;
        var registry = new CelestialPhysicsProviderRegistry();
        Assert.True(registry.Register(bridge.ProviderId, _ => CelestialResult.Ok(new CelestialPhysicsConnection(proxy, bridge.CommittedStamp))).Success);
        var result = registry.Connect(bridge.ProviderId, new(bridge.CommittedStamp.WorldSessionId, 1, new("test", 1, null, Array.Empty<CelestialSpawnItem>())));
        Assert.Equal(accepted, result.Success);
        if (!accepted) Assert.Equal("ProviderContractViolation", Assert.Single(result.Errors).Code);
    }
    public class StatusProxy : DispatchProxy
    {
        public ICelestialPhysicsBridge Inner;
        public PhysicsBridgeStatus Status;
        protected override object Invoke(MethodInfo method, object[] args) => method.Name == "get_Status" ? Status : method.Invoke(Inner, args);
    }
    [Fact]
    public void InitialEpochAndForecastProvenanceAreValidated()
    {
        var runtime = CelestialBenchFactory.Create(3);
        var services = new CelestialWorldServices(runtime);
        var rejected = services.Bodies.Create(new(new("old", runtime.WorldSessionId, runtime.Catalog.Revision, 1), "PF_BenchBody", "body"));
        Assert.Equal("InvalidOperationEpoch", Assert.Single(rejected.Errors).Code);
        var created = services.Bodies.Create(new(new("valid", runtime.WorldSessionId, runtime.Catalog.Revision), "PF_BenchBody", "body",
            InitializationReferences: new Dictionary<string, CelestialInstanceId> { ["frameOrigin"] = CelestialBenchFactory.Id(0) }));
        Assert.Equal(CelestialOperationStatus.Pending, created.Status);
        var provider = Assert.IsAssignableFrom<ICelestialPhysicsForecastProvider>(runtime.Physics);
        var forecast = provider.Forecast(new(runtime.WorldSessionId, runtime.CurrentStamp.TopologyRevision, CelestialBenchFactory.Id(0), 1, 100));
        Assert.True(forecast.Success);
        Assert.Equal(new CelestialForecastSource(runtime.Provider.ProviderId, runtime.Provider.ContractVersion), forecast.Value.Source);
    }
}
