using PioneerFramework.Megastructures;
using Xunit;

public class MegastructureRuntimeTests
{
    private static MegastructureRuntime Runtime()
    {
        var runtime = new MegastructureRuntime();
        Assert.True(runtime.RegisterDefinition(new StructureDefinition { Id="test", Nodes={new NodeTemplate { Id="hub" }} }).Success);
        return runtime;
    }

    [Fact]
    public void CreationIsIdempotentAndConflictingPayloadCannotCreateAnotherInstance()
    {
        var runtime=Runtime();
        var request=new CreateStructureRequest { OperationId="create", InstanceId="alpha", DefinitionId="test", OwnerFactionId=7 };
        Assert.True(runtime.Create(request).Success);
        Assert.True(runtime.Create(request).Success);
        Assert.Single(runtime.Snapshots);
        request.InstanceId="beta";
        Assert.Equal("OperationConflict",runtime.Create(request).Code);
        Assert.Single(runtime.Snapshots);
    }

    [Fact]
    public void MaplessFacilityHasProtectedLifecycleAndSnapshotsCannotMutateRuntime()
    {
        var runtime=Runtime();
        Assert.True(runtime.Create(new CreateStructureRequest { OperationId="create",InstanceId="a",DefinitionId="test",OwnerFactionId=7 }).Success);
        runtime.GetSnapshot("a")!.Nodes.Clear();
        Assert.Single(runtime.GetSnapshot("a")!.Nodes);
        Assert.True(runtime.Execute(new StructureCommand { OperationId="activate",InstanceId="a",Kind=StructureCommandKind.Activate,ActorFactionId=7 }).Success);
        Assert.Equal("PermissionDenied",runtime.Execute(new StructureCommand { OperationId="foreign",InstanceId="a",Kind=StructureCommandKind.Suspend,ActorFactionId=3 }).Code);
        Assert.Equal("RevisionConflict",runtime.Execute(new StructureCommand { OperationId="stale",InstanceId="a",Kind=StructureCommandKind.Suspend,ActorFactionId=7,ExpectedRevision=0 }).Code);
        Assert.Equal(StructureLifecycle.Active,runtime.GetSnapshot("a")!.Lifecycle);
    }

    [Fact]
    public void UnknownCapabilityPayloadAndOperationReceiptsSurvivePersistence()
    {
        var runtime=Runtime();
        runtime.Create(new CreateStructureRequest { OperationId="create",InstanceId="a",DefinitionId="test" });
        var document=runtime.Export();
        document.Instances[0].Nodes[0].Capabilities.Add(new CapabilityState { Id="custom",HandlerId="external.missing",Version=9,Payload="<state amount='17'/>" });
        var restored=Runtime();
        Assert.True(restored.Restore(document).Success);
        var roundTrip=MegastructureCodec.Decode(MegastructureCodec.Encode(restored.Export()));
        Assert.Equal("<state amount='17'/>",roundTrip.Instances[0].Nodes[0].Capabilities[0].Payload);
        Assert.True(restored.Create(new CreateStructureRequest { OperationId="create",InstanceId="a",DefinitionId="test" }).Success);
        Assert.Single(restored.Snapshots);
    }

    [Fact]
    public void UnsupportedSchemaCannotReplaceLiveState()
    {
        var runtime=Runtime();
        runtime.Create(new CreateStructureRequest { OperationId="create",InstanceId="a",DefinitionId="test" });
        Assert.Equal("UnsupportedSchema",runtime.Restore(new FrameworkDocument { SchemaVersion=99 }).Code);
        Assert.Single(runtime.Snapshots);
    }
    [Fact]
    public void RetirementCannotBypassStoredResourcesWithoutAHostGuard()
    {
        var runtime=Runtime();runtime.Create(new CreateStructureRequest {OperationId="c",InstanceId="a",DefinitionId="test"});
        var document=runtime.Export();new StructureResourceService(document.Resources).AddStore("stock",new("a","hub"),"ore","item",10,3,true);runtime.Restore(document);
        Assert.Equal("StoredResourcesRemain",runtime.Execute(new StructureCommand {OperationId="retire",InstanceId="a",Kind=StructureCommandKind.BeginRetirement}).Code);
        Assert.Equal(StructureLifecycle.Created,runtime.GetSnapshot("a")!.Lifecycle);
    }
    [Fact]
    public void EventCommandsAreDeferredUntilEverySubscriberSeesCommittedState()
    {
        var runtime=Runtime();runtime.Create(new CreateStructureRequest {OperationId="c",InstanceId="a",DefinitionId="test"});
        StructureLifecycle observed=StructureLifecycle.Created;
        runtime.Changed+=e=>{if(e.Kind=="Activate")runtime.Execute(new StructureCommand {OperationId="pause",InstanceId="a",Kind=StructureCommandKind.Suspend});};
        runtime.Changed+=e=>{if(e.Kind=="Activate")observed=runtime.GetSnapshot("a")!.Lifecycle;};
        runtime.Execute(new StructureCommand {OperationId="active",InstanceId="a",Kind=StructureCommandKind.Activate});
        Assert.Equal(StructureLifecycle.Active,observed);
        Assert.Equal(StructureLifecycle.Suspended,runtime.GetSnapshot("a")!.Lifecycle);
    }
}
