using PioneerFramework;
using PioneerFramework.Megastructures;
using Xunit;

public class MegastructureSafetyTests
{
    private static MegastructureRuntime Runtime()
    {
        var runtime=new MegastructureRuntime();runtime.RegisterDefinition(new StructureDefinition {Id="d",Nodes={new NodeTemplate {Id="hub",Spatial=new SpatialBinding {FrameId=1}},new NodeTemplate {Id="other",Spatial=new SpatialBinding {FrameId=1}}}});
        runtime.Create(new CreateStructureRequest {OperationId="c",InstanceId="a",DefinitionId="d",OwnerFactionId=3});return runtime;
    }
    [Fact]
    public void InvalidConstructionBatchCannotConsumeMaterialsOrLeavePartialNodes()
    {
        var runtime=Runtime();var resources=new StructureResourceService(new ResourceDocument());resources.AddStore("steel",new("a","hub"),"steel","item",10,5,true);
        var spatial=new StructureSpatialService(runtime,new NoHost());runtime.DocumentGuard=spatial.Validate;
        var stage=new ConstructionStage {Costs={new ResourceLeg {ProviderId="steel",ResourceId="steel",Amount=3}},Actions={
            new StructureCommand {OperationId="one",InstanceId="a",ActorFactionId=3,Kind=StructureCommandKind.AddNode,NodeTemplate=new NodeTemplate {Id="valid",Spatial=new SpatialBinding {FrameId=1}}},
            new StructureCommand {OperationId="two",InstanceId="a",ActorFactionId=3,Kind=StructureCommandKind.AddNode,NodeTemplate=new NodeTemplate {Id="invalid",Spatial=new SpatialBinding {Mode=SpatialMode.NodeRelative,Parent=new("a","missing")}}}
        }};
        Assert.False(runtime.CommitStage("build",stage,resources).Success);Assert.Equal(5,resources.Query("steel","steel").Amount);Assert.Equal(2,runtime.GetSnapshot("a")!.Nodes.Count);
    }
    [Fact]
    public void SuccessfulConstructionBatchIsIdempotent()
    {
        var runtime=Runtime();var resources=new StructureResourceService(new ResourceDocument());resources.AddStore("s",new("a","hub"),"steel","item",10,5,true);
        var stage=new ConstructionStage {Costs={new ResourceLeg {ProviderId="s",ResourceId="steel",Amount=3}},Actions={new StructureCommand {OperationId="add",InstanceId="a",ActorFactionId=3,Kind=StructureCommandKind.AddNode,NodeTemplate=new NodeTemplate {Id="new",Capabilities={new CapabilityState {Id="optional",HandlerId="missing.optional",Enabled=false}}}}}};
        Assert.True(runtime.CommitStage("stage",stage,resources).Success);Assert.True(runtime.CommitStage("stage",stage,resources).Success);
        Assert.Equal(2,resources.Query("s","steel").Amount);Assert.Equal(3,runtime.GetSnapshot("a")!.Nodes.Count);
    }
    [Fact]
    public void NodePermissionGrantsDoNotAuthorizeOtherNodesOrOwnershipChanges()
    {
        var runtime=Runtime();var policy=new StructureAccessPolicy {Inherit=false,Grants={new PermissionGrant {FactionId=4,Permissions={StructurePermission.Operate}}}};
        Assert.True(runtime.Execute(new StructureCommand {OperationId="policy",InstanceId="a",NodeId="hub",ActorFactionId=3,Kind=StructureCommandKind.SetAccessPolicy,Access=policy}).Success);
        Assert.True(runtime.Execute(new StructureCommand {OperationId="hub",InstanceId="a",NodeId="hub",ActorFactionId=4,Kind=StructureCommandKind.SetNodeEnabled,Enabled=false}).Success);
        Assert.Equal("PermissionDenied",runtime.Execute(new StructureCommand {OperationId="other",InstanceId="a",NodeId="other",ActorFactionId=4,Kind=StructureCommandKind.SetNodeEnabled,Enabled=false}).Code);
        Assert.Equal("PermissionDenied",runtime.Execute(new StructureCommand {OperationId="take",InstanceId="a",NodeId="hub",ActorFactionId=4,Kind=StructureCommandKind.ChangeOwner,NewOwnerFactionId=4}).Code);
    }
    [Fact]
    public void OwnedCustomBindingCannotBeRemovedOrReplacedWithoutRetirement()
    {
        var runtime=Runtime();runtime.Execute(new StructureCommand {OperationId="bind",InstanceId="a",NodeId="hub",ActorFactionId=3,Kind=StructureCommandKind.BindNode,Spatial=new SpatialBinding {Mode=SpatialMode.Custom,ProviderId="custom",Ownership=BindingOwnership.Owned}});
        Assert.Equal("OwnedBindingInUse",runtime.Execute(new StructureCommand {OperationId="replace",InstanceId="a",NodeId="hub",ActorFactionId=3,Kind=StructureCommandKind.BindNode,Spatial=new SpatialBinding {FrameId=1}}).Code);
        Assert.Equal("OwnedBindingInUse",runtime.Execute(new StructureCommand {OperationId="remove",InstanceId="a",NodeId="hub",ActorFactionId=3,Kind=StructureCommandKind.RemoveNode}).Code);
    }
    [Fact]
    public void MixedStorageIsRejectedBeforeAnyStoreIsCreated()
    {
        var registry=new MegastructureCapabilityRegistry();StandardCapabilities.Register(registry);
        var handler=registry.Find("pfl.resource.storage")!.Factory();
        Assert.Equal("SeparateItemAndScalarStoresRequired",handler.Validate(MegastructureCodec.Encode(new StorageCapabilityData {Resources={new ResourceSpecification {Id="energy",Unit="kWd",Capacity=10},new ResourceSpecification {Id="steel",Unit="item",IsItem=true,Capacity=10}}})).Code);
    }
    [Fact]
    public void CorruptResourceEnvelopeIsRejectedWithoutReplacingLiveInstances()
    {
        var runtime=Runtime();var document=runtime.Export();
        document.Resources.Stores.Add(new ResourceStoreState {Id="broken",Balances={new ResourceBalance {ResourceId="ore",Unit="item",Amount=-1,Capacity=10}}});
        Assert.Equal("InvalidResourceDocument",runtime.Restore(document).Code);Assert.Equal(2,runtime.GetSnapshot("a")!.Nodes.Count);
    }
    [Fact]
    public void ScheduledConstructionBindsLocalActionsAndCommitsOnce()
    {
        var runtime=Runtime();var resources=new StructureResourceService(new ResourceDocument());resources.AddStore("a/hub/materials",new("a","hub"),"steel","item",10,5,true);
        var project=new ConstructionCapabilityData {Stages={new ConstructionStage {Id="frame",Costs={new ResourceLeg {ProviderId="$instance/$node/materials",ResourceId="steel",Amount=3}},Actions={new StructureCommand {Kind=StructureCommandKind.AddNode,NodeTemplate=new NodeTemplate {Id="built"}}}}}};
        runtime.Execute(new StructureCommand {OperationId="project",InstanceId="a",NodeId="hub",ActorFactionId=3,Kind=StructureCommandKind.AddCapability,Capability=new CapabilityState {Id="project",HandlerId="pfl.construction",Payload=MegastructureCodec.Encode(project)}});
        runtime.Execute(new StructureCommand {OperationId="on",InstanceId="a",ActorFactionId=3,Kind=StructureCommandKind.Activate});
        var registry=new MegastructureCapabilityRegistry();StandardCapabilities.Register(registry);var scheduler=new CapabilityScheduler(runtime,registry){Services=resources};
        scheduler.Tick(250);scheduler.Tick(500);
        Assert.Equal(3,runtime.GetSnapshot("a")!.Nodes.Count);Assert.Equal(2,resources.Query("a/hub/materials","steel").Amount);
        Assert.True(MegastructureCodec.Read<ConstructionCapabilityData>(runtime.GetSnapshot("a")!.Nodes[0].Capabilities[0].Payload).Stages[0].Completed);
    }
    private sealed class NoHost:IStructureSpatialHost
    {
        public bool TryGetBody(int id,double tick,out SpatialState state){state=default;return false;}
        public bool TryGetGroup(int id,double tick,out SpatialState state){state=default;return false;}
        public FrameworkResult Maneuver(int groupId,DVector3 deltaV)=>FrameworkResult.Reject("Unsupported");
    }
}
