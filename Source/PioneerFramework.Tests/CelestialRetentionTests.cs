using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialRetentionTests
{
    [Fact]
    public void ResourceInspectionFailureReturnsAStructuredRejectionDuringDiscard()
    {
        var provider = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var runtime = new CelestialRuntime(provider, provider.CommittedStamp, new ThrowingResources(), 1, _ => throw new Exception("No creation"));
        Assert.Equal("ResourceCheckFailed", Assert.Single(runtime.DiscardFailedCreation(provider.SessionId, CelestialInstanceId.Parse("failed")).Errors).Code);
    }
    private sealed class ThrowingResources : ICelestialRemovalResources
    {
        public System.Collections.Generic.IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id) => throw new InvalidOperationException("inspection fixture");
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id) => throw new Exception("Must not release");
    }
    [Fact]
    public void DefaultProviderAcknowledgesTerminalTokensAndClosedReceipts()
    {
        var runtime = CelestialBenchFactory.Create(3);
        for (int step = 1; step <= 1000; step++) CelestialBenchFactory.Advance(runtime, step);
        var tokens = (IDictionary)typeof(ExistingCelestialEphemerisBridge).GetField("tokens", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime.Physics)!;
        Assert.Equal(0, tokens.Count);
        Assert.True(runtime.CloseEpoch(runtime.WorldSessionId, 0).Success);
        var operations = (IDictionary)typeof(ExistingCelestialEphemerisBridge).GetField("operations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime.Physics)!;
        Assert.Equal(0, operations.Count);
        var expired = runtime.Physics.GetOperation("physics/create/" + Uri.EscapeDataString("bench/bootstrap"));
        Assert.Equal("OperationExpired", Assert.Single(expired.Errors).Code);
    }
    [Fact]
    public void ExplicitDiscardRetainsIdentityAndNeverDiscardsOrdinaryBlockedBodies()
    {
        var good = new CelestialBodyDef { defName = "PF_DiscardProbe", comps = new() {
            new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { value = 10 } } } }.ToTemplate().Value;
        var descriptor = new CelestialCompDescriptor("failure", "test.factory-failure", 1);
        var bad = CelestialBodyTemplate.Create(good.DefinitionId, good.Label, good.Category, good.Tags, good.Components.Append(new CelestialCompDefinition(descriptor,
            new ThrowingFactory(), _ => CelestialResult.Ok<ICelestialResolvedProperties>(new Properties())))).Value;
        var provider = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var runtime = new CelestialRuntime(provider, provider.CommittedStamp, new Resources(), 1, _ => CelestialResult.Ok(bad));
        var failed = runtime.Create(new(new("failed", runtime.WorldSessionId, 0), good.DefinitionId, "body"));
        Assert.Equal(CelestialOperationStatus.Rejected, failed.Status);
        var id = Assert.Single(failed.InstanceIds); int handle = runtime.Query(id).Value.Description.Handle;
        Assert.True(runtime.DiscardFailedCreation(runtime.WorldSessionId, id).Success);
        var tombstone = runtime.Query(id).Value;
        Assert.Equal(CelestialLifecycle.Removed, tombstone.Lifecycle); Assert.Empty(tombstone.ComponentKeys);
        Assert.Equal(handle, tombstone.Description.Handle);
        Assert.Equal(CelestialOperationStatus.Rejected, runtime.Create(new(new("failed", runtime.WorldSessionId, 0), good.DefinitionId, "body")).Status);
        var healthy = CelestialBenchFactory.Create(3); healthy.Catalog.TryGetInstance(CelestialBenchFactory.Id(1), out var body);
        body.Lifecycle = CelestialLifecycle.Blocked;
        Assert.Equal("NotFailedCreation", Assert.Single(healthy.DiscardFailedCreation(healthy.WorldSessionId, body.Id).Errors).Code);
        Assert.NotEmpty(body.CompStateRecords);
    }
    private sealed class ThrowingFactory : ICelestialCompFactory
    { public CelestialBodyComp Create(ICelestialResolvedProperties properties) => throw new InvalidOperationException("factory fixture"); }
    private sealed class Properties : ICelestialResolvedProperties
    {
        public string Key => "failure"; public string HandlerId => "test.factory-failure"; public int Version => 1;
        public ReadOnlyMemory<byte> Serialize() => ReadOnlyMemory<byte>.Empty;
    }
    private sealed class Resources : ICelestialRemovalResources
    {
        public System.Collections.Generic.IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id) => Array.Empty<CelestialError>();
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id) => CelestialResult.Ok(true);
    }
}
