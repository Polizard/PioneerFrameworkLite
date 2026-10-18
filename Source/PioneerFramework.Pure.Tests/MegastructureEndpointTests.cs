using PioneerFramework;
using PioneerFramework.Megastructures;
using Xunit;

public class MegastructureEndpointTests
{
    private sealed class Host:IStructureSpatialHost
    {
        public bool TryGetBody(int id,double tick,out SpatialState state){state=default;return false;}
        public bool TryGetGroup(int id,double tick,out SpatialState state){state=default;return false;}
        public FrameworkResult Maneuver(int groupId,DVector3 deltaV)=>FrameworkResult.Reject("Unsupported");
    }
    private static (MegastructureRuntime,StructureEndpointService,StructureResourceService,ResourceDocument) Setup()
    {
        var runtime=new MegastructureRuntime();
        var port=MegastructureCodec.Encode(new TransportCapabilityData {Endpoints={new EndpointDefinition {Id="cargo",StoreProviderId="$instance/$node/storage",Capacity=1}}});
        runtime.RegisterDefinition(new StructureDefinition {Id="test",Nodes={new NodeTemplate {Id="one",Spatial=new SpatialBinding {FrameId=1},Capabilities={new CapabilityState {Id="port",HandlerId="pfl.transport",Payload=port}}},new NodeTemplate {Id="two",Spatial=new SpatialBinding {FrameId=1,Offset=new DVector3(10,0,0)},Capabilities={new CapabilityState {Id="port",HandlerId="pfl.transport",Payload=port}}}}});
        runtime.Create(new CreateStructureRequest {OperationId="c",InstanceId="a",DefinitionId="test"});runtime.Execute(new StructureCommand {OperationId="active",InstanceId="a",Kind=StructureCommandKind.Activate});
        var data=new ResourceDocument();var resources=new StructureResourceService(data);resources.AddStore("a/one/storage",new("a","one"),"ore","item",20,10,true);resources.AddStore("a/two/storage",new("a","two"),"ore","item",20,0,true);
        return(runtime,new StructureEndpointService(runtime,new StructureSpatialService(runtime,new Host()),resources,new TransportDocument()),resources,data);
    }
    private static TransportRequest Request(string id)=>new() {OperationId=id,Source=new("a","one","port","cargo"),Target=new("a","two","port","cargo"),ResourceId="ore",Amount=2};
    [Fact]
    public void EndpointReservationsPreventDoubleBookingAndCancellationReleasesSlot()
    {
        var (_,ports,_,_)=Setup();Assert.True(ports.Reserve(Request("first"),0).Success);
        Assert.Equal("EndpointBusy",ports.Reserve(Request("second"),0).Code);
        Assert.True(ports.Cancel("first").Success);Assert.True(ports.Reserve(Request("second"),0).Success);
    }
    [Fact]
    public void ChangingNodeRevisionInvalidatesArrivalBeforeAnyCargoDebit()
    {
        var (runtime,ports,_,_)=Setup();Assert.True(ports.Reserve(Request("first"),0).Success);
        runtime.Execute(new StructureCommand {OperationId="move",InstanceId="a",NodeId="two",Kind=StructureCommandKind.BindNode,Spatial=new SpatialBinding {FrameId=1,Offset=new DVector3(100,0,0)}});
        Assert.Equal("EndpointChanged",ports.Arrive("first",250).Code);
        Assert.Equal(new DVector3(100,0,0),ports.Resolve(new("a","two","port","cargo"),250).State.PositionSystem.v);
    }
    [Fact]
    public void PartiallyCommittedArrivalCanFinishAfterRevisionChangeAndReload()
    {
        var (runtime,ports,resources,data)=Setup();Assert.True(ports.Reserve(Request("first"),0).Success);
        resources.RegisterProvider(new Interrupted(resources.FindProvider("a/two/storage")!),true);
        Assert.Equal("TransactionPending",ports.Arrive("first",250).Code);Assert.Equal(8,resources.Query("a/one/storage","ore").Amount);
        runtime.Execute(new StructureCommand {OperationId="change",InstanceId="a",NodeId="two",Kind=StructureCommandKind.SetNodeEnabled,Enabled=false});
        var restored=new StructureResourceService(MegastructureCodec.Read<ResourceDocument>(MegastructureCodec.Encode(data)));
        var tickets=new TransportDocument {Tickets=ports.Tickets.ToList()};
        var resumed=new StructureEndpointService(runtime,new StructureSpatialService(runtime,new Host()),restored,tickets);
        Assert.True(resumed.Arrive("first",500).Success);Assert.Equal(8,restored.Query("a/one/storage","ore").Amount);Assert.Equal(2,restored.Query("a/two/storage","ore").Amount);
    }
    private sealed class Interrupted(IResourceProvider inner):IResourceProvider
    {
        public string Id=>inner.Id;public bool PersistentReceipts=>true;
        public ResourceSnapshot Query(string resource)=>inner.Query(resource);
        public FrameworkResult Reserve(string id,ResourceLeg leg)=>inner.Reserve(id,leg);
        public FrameworkResult Commit(string id,ResourceLeg leg)=>throw new InvalidOperationException("temporary failure");
        public void Cancel(string id)=>inner.Cancel(id);public bool HasReceipt(string id)=>inner.HasReceipt(id);
    }
}
