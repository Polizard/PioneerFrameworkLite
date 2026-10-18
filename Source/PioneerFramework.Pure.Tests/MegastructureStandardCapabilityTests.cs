using PioneerFramework.Megastructures;
using Xunit;

public class MegastructureStandardCapabilityTests
{
    [Theory]
    [InlineData(600,3000,5)]
    [InlineData(100,1000,10)]
    public void ProductionPreservesRemainderAndProcessesShortPeriods(long period,long now,long expected)
    {
        var registry=new MegastructureCapabilityRegistry();StandardCapabilities.Register(registry);
        var runtime=new MegastructureRuntime();
        runtime.RegisterDefinition(new StructureDefinition {Id="d",Nodes={new NodeTemplate {Id="n",Capabilities={
            new CapabilityState {Id="storage",HandlerId="pfl.resource.storage",Payload=MegastructureCodec.Encode(new StorageCapabilityData {Resources={new ResourceSpecification {Id="energy",Unit="kWd",Capacity=100}}})},
            new CapabilityState {Id="p",HandlerId="pfl.production",Payload=MegastructureCodec.Encode(new ProductionCapabilityData {PeriodTicks=period,Outputs={new RecipeAmount {ResourceId="energy",Amount=1}}})}
        }}}});
        runtime.Create(new CreateStructureRequest {OperationId="c",InstanceId="a",DefinitionId="d"});runtime.Execute(new StructureCommand {OperationId="on",InstanceId="a",Kind=StructureCommandKind.Activate});
        var resources=new StructureResourceService(new ResourceDocument());new CapabilityScheduler(runtime,registry){Services=resources}.Tick(now);
        Assert.Equal(expected,resources.Query("a/n/storage","energy").Amount);
    }
    [Fact]
    public void ProductionConsumesInputsAndFullOutputStopsWithoutEatingMoreMaterials()
    {
        var runtime=new MegastructureRuntime();var registry=new MegastructureCapabilityRegistry();StandardCapabilities.Register(registry);
        var storage=new StorageCapabilityData {Resources={new ResourceSpecification {Id="ore",Unit="item",IsItem=true,Capacity=10,Initial=5},new ResourceSpecification {Id="plate",Unit="item",IsItem=true,Capacity=2}}};
        var production=new ProductionCapabilityData {PeriodTicks=250,Inputs={new RecipeAmount {ResourceId="ore",Amount=1}},Outputs={new RecipeAmount {ResourceId="plate",Amount=1}}};
        runtime.RegisterDefinition(new StructureDefinition {Id="factory",Nodes={new NodeTemplate {Id="hub",Capabilities={new CapabilityState {Id="storage",HandlerId="pfl.resource.storage",Payload=MegastructureCodec.Encode(storage)},new CapabilityState {Id="factory",HandlerId="pfl.production",Payload=MegastructureCodec.Encode(production)}}}}});
        runtime.Create(new CreateStructureRequest {OperationId="c",InstanceId="a",DefinitionId="factory"});runtime.Execute(new StructureCommand {OperationId="activate",InstanceId="a",Kind=StructureCommandKind.Activate});
        var resources=new StructureResourceService(new ResourceDocument());var scheduler=new CapabilityScheduler(runtime,registry){Services=resources};scheduler.Tick(1000);
        Assert.Equal(2,resources.Query("a/hub/storage","plate").Amount);Assert.Equal(3,resources.Query("a/hub/storage","ore").Amount);
    }
    [Fact]
    public void ConstructionNeedsExplicitWorkAndAStageCannotChargeTwice()
    {
        var resources=new StructureResourceService(new ResourceDocument());resources.AddStore("materials",new("a","hub"),"steel","item",10,5,true);
        var project=new ConstructionCapabilityData {AvailableWork=9,Stages={new ConstructionStage {Id="frame",WorkRequired=10,Costs={new ResourceLeg {ProviderId="materials",ResourceId="steel",Amount=3}}}}};
        var construction=new StructureConstructionService(resources);
        Assert.False(construction.Advance("project",project));Assert.Equal(5,resources.Query("materials","steel").Amount);
        project.AvailableWork=1;
        Assert.True(construction.Advance("project",project));Assert.Equal(2,resources.Query("materials","steel").Amount);
        Assert.True(construction.Advance("project",project));Assert.Equal(2,resources.Query("materials","steel").Amount);
    }
    [Fact]
    public void ScalarNetworkDoesNotBounceFreshCreditsThroughACycleInOneStep()
    {
        var resources=new StructureResourceService(new ResourceDocument());resources.Tick=250;
        resources.AddStore("a/one/storage",new("a","one"),"energy","kWd",10,4);
        resources.AddStore("a/two/storage",new("a","two"),"energy","kWd",10);
        var connections=new[]{new NodeConnection {Id="forward",SourceNode="one",TargetNode="two",ResourceId="energy",CapacityPerStep=4},new NodeConnection {Id="back",SourceNode="two",TargetNode="one",ResourceId="energy",CapacityPerStep=4}};
        var result=StructureConnectionService.BuildLegs("a",connections,resources,250,(_,_)=>true);
        Assert.Equal(2,result.Count);
        Assert.True(resources.Commit("network",result).Success);
        Assert.Equal(0,resources.Query("a/one/storage","energy").Amount);
        Assert.Equal(4,resources.Query("a/two/storage","energy").Amount);
        Assert.Equal(0,resources.Query("a/two/storage","energy").Available);
    }
}
