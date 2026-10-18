using PioneerFramework.Megastructures;
using Xunit;

public class MegastructureResourceTests
{
    private sealed class InterruptedProvider(IResourceProvider inner):IResourceProvider
    {
        public string Id=>inner.Id;
        public bool PersistentReceipts=>true;
        public ResourceSnapshot Query(string resource)=>inner.Query(resource);
        public FrameworkResult Reserve(string id,ResourceLeg leg)=>inner.Reserve(id,leg);
        public FrameworkResult Commit(string id,ResourceLeg leg)=>throw new InvalidOperationException("interrupted handoff");
        public void Cancel(string id)=>inner.Cancel(id);
        public bool HasReceipt(string id)=>inner.HasReceipt(id);
    }
    [Fact]
    public void TransfersConserveStockAndRetryDoesNotDebitAgain()
    {
        var data=new ResourceDocument();var resources=new StructureResourceService(data);
        resources.AddStore("source",new("a","one"),"ore","item",20,10,true);
        resources.AddStore("target",new("a","two"),"ore","item",20,0,true);
        Assert.True(resources.Transfer("ship-1","source","target","ore",4).Success);
        Assert.True(resources.Transfer("ship-1","source","target","ore",4).Success);
        Assert.Equal(6,resources.Query("source","ore").Amount);Assert.Equal(4,resources.Query("target","ore").Amount);
        Assert.Equal("OperationConflict",resources.Transfer("ship-1","source","target","ore",5).Code);
        var restored=new StructureResourceService(MegastructureCodec.Read<ResourceDocument>(MegastructureCodec.Encode(data)));
        Assert.True(restored.Transfer("ship-1","source","target","ore",4).Success);
        Assert.Equal(4,restored.Query("target","ore").Amount);
    }
    [Fact]
    public void FullTargetAndFractionalItemsAreRejectedWithoutLosingSourceStock()
    {
        var resources=new StructureResourceService(new ResourceDocument());
        resources.AddStore("s",new("a","one"),"ore","item",20,10,true);resources.AddStore("t",new("a","two"),"ore","item",3,3,true);
        Assert.False(resources.Transfer("full","s","t","ore",4).Success);
        Assert.Equal(10,resources.Query("s","ore").Amount);
        Assert.Equal("InvalidQuantity",resources.Transfer("fraction","s","t","ore",.5m).Code);
    }
    [Fact]
    public void ProductionCreditsCannotBeConsumedInTheSameSettlementStep()
    {
        var resources=new StructureResourceService(new ResourceDocument());resources.Tick=250;
        resources.AddStore("s",new("a","one"),"energy","kWd",20,0,false);
        Assert.True(resources.Commit("generate",new[]{new ResourceLeg {ProviderId="s",ResourceId="energy",Amount=3,Incoming=true,AvailableAtTick=500}}).Success);
        Assert.Equal(3,resources.Query("s","energy").Amount);Assert.Equal(0,resources.Query("s","energy").Available);
        Assert.False(resources.Commit("consume",new[]{new ResourceLeg {ProviderId="s",ResourceId="energy",Amount=1}}).Success);
        resources.Tick=500;
        Assert.True(resources.Commit("consume",new[]{new ResourceLeg {ProviderId="s",ResourceId="energy",Amount=1}}).Success);
        Assert.Equal(2,resources.Query("s","energy").Amount);
    }
    [Fact]
    public void CommittedLegIsNotRepeatedWhenNextProviderTemporarilyDisappears()
    {
        var data=new ResourceDocument();var resources=new StructureResourceService(data);
        resources.AddStore("s",new("a","one"),"ore","item",20,10,true);resources.AddStore("t",new("a","two"),"ore","item",20,0,true);
        resources.RegisterProvider(new InterruptedProvider(resources.FindProvider("t")!),replace:true);
        Assert.Equal("TransactionPending",resources.Transfer("handoff","s","t","ore",4).Code);
        Assert.Equal(6,resources.Query("s","ore").Amount);
        var restored=new StructureResourceService(MegastructureCodec.Read<ResourceDocument>(MegastructureCodec.Encode(data)));
        Assert.True(restored.Transfer("handoff","s","t","ore",4).Success);
        Assert.Equal(6,restored.Query("s","ore").Amount);Assert.Equal(4,restored.Query("t","ore").Amount);
    }
}
