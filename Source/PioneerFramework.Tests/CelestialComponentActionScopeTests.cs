using System;
using System.Linq;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialComponentActionScopeTests
{
    [Fact]
    public void ReadyComponentsRequireExplicitNewActionScopeIndependentOfTheirCreationTransaction()
    {
        var runtime = CelestialBenchFactory.Create(3, pending: true, transform: template => CelestialBodyTemplate.Create(template.DefinitionId, template.Label,
            template.Category, template.Tags, template.Components.Append(new CelestialCompDefinition(new("emitter", "test.emitter", 1), new Factory(),
                _ => CelestialResult.Ok<ICelestialResolvedProperties>(new Properties())))).Value);
        var handler = new Handler(); Assert.True(runtime.RegisterCommandHandler("test.action", handler).Success);
        CelestialBenchFactory.Advance(runtime, 0);
        var emitter = Assert.Single(runtime.GetCapabilities<Emitter>(CelestialBenchFactory.Id(0)));
        Assert.Equal("OperationScopeRequired", Assert.Single(emitter.Raw("new-action").Errors).Code);
        Assert.True(runtime.CloseEpoch(runtime.WorldSessionId, 0).Success);
        long actionEpoch = runtime.OpenEpoch(runtime.WorldSessionId).Value;
        Assert.NotNull(typeof(ICelestialCompContext).GetMethod("SubmitNewAction"));
        var fresh = emitter.Explicit(actionEpoch, "new-action"); Assert.True(fresh.Success);
        CelestialBenchFactory.Advance(runtime, 1);
        Assert.Equal(1, handler.Calls);
        Assert.True(runtime.CloseEpoch(runtime.WorldSessionId, actionEpoch).Success);
        var old = emitter.Explicit(actionEpoch, "new-action"); Assert.False(old.Success);
        Assert.Equal("OperationExpired", Assert.Single(old.Errors).Code);
        Assert.Equal(1, handler.Calls);
    }
    private sealed class Properties : ICelestialResolvedProperties
    {
        public string Key => "emitter"; public string HandlerId => "test.emitter"; public int Version => 1;
        public ReadOnlyMemory<byte> Serialize() => ReadOnlyMemory<byte>.Empty;
    }
    private sealed class Factory : ICelestialCompFactory
    { public CelestialBodyComp Create(ICelestialResolvedProperties properties) => new Emitter(); }
    private sealed class Emitter() : CelestialBodyComp(new("emitter", "test.emitter", 1))
    {
        private CelestialCommand Command(string id) => new("test.action", id, Context.InstanceId, Context.WorldSessionId, Context.DescriptorRevision);
        public CelestialResult<string> Raw(string id) => Context.Submit(Command(id));
        public CelestialResult<string> Explicit(long epoch, string id)
            => Context.SubmitNewAction(epoch, Command(id));
    }
    private sealed class Handler : ICelestialCommandHandler
    { public int Calls; public CelestialResult<bool> Execute(CelestialCommand command) { Calls++; return CelestialResult.Ok(true); } }
}
