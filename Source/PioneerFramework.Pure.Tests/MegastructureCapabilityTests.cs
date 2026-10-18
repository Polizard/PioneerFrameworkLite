using PioneerFramework.Megastructures;
using Xunit;

public class MegastructureCapabilityTests
{
    private sealed class Counter:IStructureCapability
    {
        public FrameworkResult Validate(string payload)=>FrameworkResult.Ok();
        public string Advance(CapabilityContext context,string payload,long elapsedTicks)=>long.Parse(payload,System.Globalization.CultureInfo.InvariantCulture).Add(elapsedTicks);
    }
    private sealed class Broken:IStructureCapability
    {
        public FrameworkResult Validate(string payload)=>FrameworkResult.Ok();
        public string Advance(CapabilityContext context,string payload,long elapsedTicks)=>throw new InvalidOperationException("broken extension");
    }
    private static (MegastructureRuntime,CapabilityScheduler) Setup()
    {
        var runtime=new MegastructureRuntime();var registry=new MegastructureCapabilityRegistry();
        registry.Register(new CapabilityDescriptor("test.counter",()=>new Counter()));registry.Register(new CapabilityDescriptor("test.broken",()=>new Broken()));
        runtime.RegisterDefinition(new StructureDefinition {Id="test",Nodes={new NodeTemplate {Id="hub",Capabilities={new CapabilityState {Id="counter",HandlerId="test.counter",Payload="0"}}}}});
        runtime.Create(new CreateStructureRequest {OperationId="c",InstanceId="a",DefinitionId="test"});
        runtime.Execute(new StructureCommand {OperationId="active",InstanceId="a",Kind=StructureCommandKind.Activate});
        return(runtime,new CapabilityScheduler(runtime,registry));
    }
    [Fact]
    public void BatchedAndIncrementalUpdatesHaveSameOutputAndPauseDoesNotBackfill()
    {
        var (one,scheduler)=Setup();var (many,incremental)=Setup();
        scheduler.Tick(1000);for(int tick=250;tick<=1000;tick+=250)incremental.Tick(tick);
        Assert.Equal("1000",one.GetSnapshot("a")!.Nodes[0].Capabilities[0].Payload);
        Assert.Equal("1000",many.GetSnapshot("a")!.Nodes[0].Capabilities[0].Payload);
        one.CurrentTick=1000;one.Execute(new StructureCommand {OperationId="pause",InstanceId="a",Kind=StructureCommandKind.Suspend});scheduler.Tick(2000);
        one.CurrentTick=2000;one.Execute(new StructureCommand {OperationId="resume",InstanceId="a",Kind=StructureCommandKind.Activate});scheduler.Tick(2250);
        Assert.Equal("1250",one.GetSnapshot("a")!.Nodes[0].Capabilities[0].Payload);
    }
    [Fact]
    public void UnknownAndThrowingExtensionsAreBlockedWithoutDestroyingTheirPayload()
    {
        var (runtime,scheduler)=Setup();
        runtime.Execute(new StructureCommand {OperationId="missing",InstanceId="a",NodeId="hub",Kind=StructureCommandKind.AddCapability,Capability=new CapabilityState {Id="missing",HandlerId="thirdparty.missing",Version=8,Payload="opaque"}});
        runtime.Execute(new StructureCommand {OperationId="broken",InstanceId="a",NodeId="hub",Kind=StructureCommandKind.AddCapability,Capability=new CapabilityState {Id="broken",HandlerId="test.broken"}});
        scheduler.Tick(250);var capabilities=runtime.GetSnapshot("a")!.Nodes[0].Capabilities;
        Assert.Equal("opaque",capabilities.Single(c=>c.Id=="missing").Payload);
        Assert.Equal("CapabilityMissing",capabilities.Single(c=>c.Id=="missing").BlockedReason);
        Assert.Equal("CapabilityException",capabilities.Single(c=>c.Id=="broken").BlockedReason);
        Assert.Equal("250",capabilities.Single(c=>c.Id=="counter").Payload);
    }
    [Fact]
    public void RegistryRejectsDuplicateAndDependencyCycles()
    {
        var registry=new MegastructureCapabilityRegistry();
        Assert.True(registry.Register(new CapabilityDescriptor("a",()=>new Counter(),requires:new[]{"b"})).Success);
        Assert.Equal("DuplicateHandler",registry.Register(new CapabilityDescriptor("a",()=>new Counter())).Code);
        registry.Register(new CapabilityDescriptor("b",()=>new Counter(),requires:new[]{"a"}));
        Assert.Equal("CapabilityCycle",registry.Validate(new[]{new CapabilityState {Id="one",HandlerId="a"},new CapabilityState {Id="two",HandlerId="b"}}).Code);
    }
    [Fact]
    public void UpdatingAStateVersionCannotReuseAnAlreadyValidatedHandler()
    {
        var (runtime,scheduler)=Setup();scheduler.Tick(250);
        runtime.Execute(new StructureCommand {OperationId="update",InstanceId="a",NodeId="hub",Kind=StructureCommandKind.UpdateCapability,CapabilityId="counter",Capability=new CapabilityState {HandlerId="test.counter",Version=99,Payload="100"}});
        scheduler.Tick(500);var state=runtime.GetSnapshot("a")!.Nodes[0].Capabilities[0];
        Assert.Equal("100",state.Payload);Assert.Equal("CapabilityVersion",state.BlockedReason);
    }
    [Fact]
    public void BrokenPrerequisiteBlocksDependentAndCannotBreakErrorIsolation()
    {
        var runtime=new MegastructureRuntime();var registry=new MegastructureCapabilityRegistry();
        registry.Register(new CapabilityDescriptor("broken",()=>new Broken()));registry.Register(new CapabilityDescriptor("dependent",()=>new Counter(),requires:new[]{"broken"}));
        runtime.RegisterDefinition(new StructureDefinition {Id="d",Nodes={new NodeTemplate {Id="hub",Capabilities={new CapabilityState {Id="base",HandlerId="broken"},new CapabilityState {Id="consumer",HandlerId="dependent",Payload="0"}}}}});
        runtime.Create(new CreateStructureRequest {OperationId="c",InstanceId="a",DefinitionId="d"});runtime.Execute(new StructureCommand {OperationId="on",InstanceId="a",Kind=StructureCommandKind.Activate});
        var scheduler=new CapabilityScheduler(runtime,registry){Error=(_,_)=>throw new InvalidOperationException("logging failed")};scheduler.Tick(250);
        Assert.Equal("0",runtime.GetSnapshot("a")!.Nodes[0].Capabilities[1].Payload);
        Assert.Equal("CapabilityDependencyBlocked",runtime.GetSnapshot("a")!.Nodes[0].Capabilities[1].BlockedReason);
    }
    [Fact]
    public void RegisteredMigrationUpgradesPayloadBeforeRunning()
    {
        var runtime=new MegastructureRuntime();var registry=new MegastructureCapabilityRegistry();var descriptor=new CapabilityDescriptor("counter",()=>new Counter(),2);
        descriptor.Migrations.Add(1,_=>"2");registry.Register(descriptor);
        runtime.RegisterDefinition(new StructureDefinition {Id="d",Nodes={new NodeTemplate {Id="hub",Capabilities={new CapabilityState {Id="counter",HandlerId="counter",Payload="legacy"}}}}});
        runtime.Create(new CreateStructureRequest {OperationId="c",InstanceId="a",DefinitionId="d"});runtime.Execute(new StructureCommand {OperationId="on",InstanceId="a",Kind=StructureCommandKind.Activate});
        new CapabilityScheduler(runtime,registry).Tick(250);var state=runtime.GetSnapshot("a")!.Nodes[0].Capabilities[0];Assert.Equal(2,state.Version);Assert.Equal("252",state.Payload);
    }
}
internal static class FrameworkCounterTestExtension
{public static string Add(this long value,long delta)=>(value+delta).ToString(System.Globalization.CultureInfo.InvariantCulture);}
