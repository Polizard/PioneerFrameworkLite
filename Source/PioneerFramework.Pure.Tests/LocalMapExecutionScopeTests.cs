using System;
using System.Collections.Generic;
using PioneerFramework.MapGeneration;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class LocalMapExecutionScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scope_wraps_factory_execution_validation_and_same_instance_postinit(bool fail)
    {
        var calls = new List<string>();
        var registry = new MapGenerationRegistry();
        registry.Register(new("scope", 1, new[] { 1 }, _ => { }, _ => {
            calls.Add("factory"); return new Module(calls, fail);
        }));
        var spec = new LocalMapGenerationSpec(new LocalMapRecipe("test", new[] {
            new MapGenerationStepSpec("step", "scope", 1, 1, MapGenerationStage.Fields, MapGenerationDomain.Natural) }));
        var plan = MapGenerationCompiler.Compile(spec, MapGenerationPurpose.Colony, registry).Plan!;
        var executor = new MapGenerationExecutor(plan, new("owner", 1, 0, 50, 50, "biome"), new Scopes(calls));
        Assert.Equal(fail ? MapGenerationState.Failed : MapGenerationState.Generated, executor.Execute().State);
        if (fail) Assert.Equal(new[] { "enter:False", "factory", "generate", "leave" }, calls);
        else
        {
            Assert.Equal(MapGenerationState.Ready, executor.PostInitialize().State);
            Assert.Equal(new[] { "enter:False", "factory", "generate", "leave", "enter:True", "post", "leave" }, calls);
        }
    }
    private sealed class Module(List<string> calls, bool fail) : IMapGenerationModule, IMapGenerationPostInitializer
    {
        private bool generated;
        public void Execute(MapGenerationContext context) { calls.Add("generate"); if (fail) throw new InvalidOperationException("stop"); generated = true; }
        public void PostInitialize(MapGenerationContext context) { Assert.True(generated); calls.Add("post"); }
    }
    private sealed class Scopes(List<string> calls) : IServiceProvider, IMapGenerationExecutionScopeFactory
    {
        public object? GetService(Type type) => type == typeof(IMapGenerationExecutionScopeFactory) ? this : null;
        public IDisposable Enter(MapGenerationContext context, bool postInitialize) { calls.Add("enter:" + postInitialize); return new Scope(calls); }
    }
    private sealed class Scope(List<string> calls) : IDisposable { public void Dispose() => calls.Add("leave"); }
}
