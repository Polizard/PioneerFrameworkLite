using PioneerFramework;
using PioneerFramework.Megastructures;
using Xunit;

public class MegastructureSpatialTests
{
    private sealed class Host:IStructureSpatialHost
    {
        public bool TryGetBody(int id,double tick,out SpatialState state) {state=new(1,new DVector3(100,0,0),new DVector3(2,0,0),RotationData.Identity,new DVector3(0,0,1),tick);return id==2;}
        public bool TryGetGroup(int id,double tick,out SpatialState state) {state=new(1,new DVector3(500,0,0),new DVector3(0,3,0),RotationData.Identity,DVector3.zero,tick);return id==7;}
        public FrameworkResult Maneuver(int groupId,DVector3 deltaV)=>FrameworkResult.Ok();
    }
    [Fact]
    public void RelativeEndpointsIncludeAnchorTranslationAndRotationalVelocity()
    {
        var runtime=new MegastructureRuntime();var spatial=new StructureSpatialService(runtime,new Host());runtime.DocumentGuard=spatial.Validate;
        runtime.RegisterDefinition(new StructureDefinition {Id="test",Nodes={new NodeTemplate {Id="root",Spatial=new SpatialBinding {Mode=SpatialMode.BodyAnchored,BodyId=2,Offset=new DVector3(10,0,0)}},new NodeTemplate {Id="port",Spatial=new SpatialBinding {Mode=SpatialMode.NodeRelative,Parent=new("a","root"),Offset=new DVector3(0,5,0)}}}});
        Assert.True(runtime.Create(new CreateStructureRequest {OperationId="c",InstanceId="a",DefinitionId="test"}).Success);
        var result=spatial.Evaluate(new("a","port"),0);
        Assert.True(result.Success);Assert.Equal(new DVector3(110,5,0),result.State.PositionSystem.v);
        Assert.Equal(new DVector3(-3,10,0),result.State.VelocitySystem.v);
    }
    [Fact]
    public void CyclicDependenciesAndDuplicatePhysicalAuthorityAreRejectedBeforeMutation()
    {
        var runtime=new MegastructureRuntime();var spatial=new StructureSpatialService(runtime,new Host());runtime.DocumentGuard=spatial.Validate;
        runtime.RegisterDefinition(new StructureDefinition {Id="test",Nodes={new NodeTemplate {Id="one",Spatial=new SpatialBinding {Mode=SpatialMode.PhysicalGroup,GroupId=7}},new NodeTemplate {Id="two",Spatial=new SpatialBinding {Mode=SpatialMode.NodeRelative,Parent=new("a","one")}}}});
        Assert.True(runtime.Create(new CreateStructureRequest {OperationId="c",InstanceId="a",DefinitionId="test"}).Success);
        Assert.Equal("SpatialCycle",runtime.Execute(new StructureCommand {OperationId="cycle",InstanceId="a",NodeId="one",Kind=StructureCommandKind.BindNode,Spatial=new SpatialBinding {Mode=SpatialMode.NodeRelative,Parent=new("a","two")}}).Code);
        Assert.Equal("DuplicatePhysicalAuthority",runtime.Execute(new StructureCommand {OperationId="duplicate",InstanceId="a",NodeId="two",Kind=StructureCommandKind.BindNode,Spatial=new SpatialBinding {Mode=SpatialMode.PhysicalGroup,GroupId=7}}).Code);
        Assert.Equal(SpatialMode.PhysicalGroup,runtime.GetSnapshot("a")!.Nodes[0].Spatial.Mode);
    }
    [Fact]
    public void MissingBackendDoesNotInventPositionOrFreezeOtherNodes()
    {
        var runtime=new MegastructureRuntime();var spatial=new StructureSpatialService(runtime,new Host());runtime.DocumentGuard=spatial.Validate;
        runtime.RegisterDefinition(new StructureDefinition {Id="test",Nodes={new NodeTemplate {Id="missing",Spatial=new SpatialBinding {Mode=SpatialMode.BodyAnchored,BodyId=999}},new NodeTemplate {Id="moving",Spatial=new SpatialBinding {Mode=SpatialMode.PhysicalGroup,GroupId=7}}}});
        runtime.Create(new CreateStructureRequest {OperationId="c",InstanceId="a",DefinitionId="test"});
        Assert.Equal("BodyMissing",spatial.Evaluate(new("a","missing"),1).Code);
        Assert.Equal(new DVector3(500,0,0),spatial.Evaluate(new("a","moving"),1).State.PositionSystem.v);
    }
}
