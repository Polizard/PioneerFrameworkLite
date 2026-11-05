using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace PioneerFramework.Megastructures
{
    public sealed class ThingResourceContainer:IItemTransferProvider,IExposable,IThingHolder
    {
        private ThingOwner<Thing> contents;
        private string id="";private string instanceId="";private string nodeId="";private string statePayload="";
        private ResourceStoreState state=new();
        internal Func<long> Clock { get; set; }=()=>Current.Game==null?0:Find.TickManager?.TicksGame??0;
        public string Id=>id;
        public NodeAddress Owner=>new(instanceId,nodeId);
        public bool PersistentReceipts=>true;
        public IThingHolder? ParentHolder=>null;
        public int Count=>contents.Sum(t=>t.stackCount);
        public bool HasReservations=>state.Reservations.Count>0;
        public ThingResourceContainer(){contents=new ThingOwner<Thing>(this,false,LookMode.Deep);}
        public ThingResourceContainer(string id,NodeAddress owner):this(){this.id=id;instanceId=owner.InstanceId;nodeId=owner.NodeId;}
        public ThingOwner GetDirectlyHeldThings()=>contents;
        public void GetChildHolders(List<IThingHolder> outChildren)=>ThingOwnerUtility.AppendThingHoldersFromThings(outChildren,contents);
        public FrameworkResult Configure(ResourceSpecification specification)
        {
            if(!specification.IsItem||specification.Unit!="item"||specification.Capacity<0||decimal.Truncate(specification.Capacity)!=specification.Capacity||specification.Capacity>int.MaxValue)return FrameworkResult.Reject("InvalidItemStore");
            var previous=state.Balances.FirstOrDefault(b=>b.ResourceId==specification.Id);
            if(previous!=null)return previous.Capacity==specification.Capacity?FrameworkResult.Ok():FrameworkResult.Reject("StoreConfigurationConflict");
            if(specification.Initial!=0)return FrameworkResult.Reject("InitialItemsRequireDelivery");
            state.Balances.Add(new ResourceBalance {ResourceId=specification.Id,Unit="item",IsItem=true,Capacity=specification.Capacity});return FrameworkResult.Ok();
        }
        public FrameworkResult Deposit(Thing thing)
        {
            if(thing==null||thing.Spawned||thing.def.stackLimit<=1||thing.def.MadeFromStuff||!thing.def.comps.NullOrEmpty())return FrameworkResult.Reject("UnsupportedItemState");
            var snapshot=Query(thing.def.defName);if(!snapshot.Exists||snapshot.Capacity-snapshot.Amount<thing.stackCount)return FrameworkResult.Reject("StorageFull");
            int expected=thing.stackCount;int added=contents.TryAddOrTransfer(thing,expected);return added==expected?FrameworkResult.Ok():FrameworkResult.Reject("ItemTransferFailed");
        }
        public bool HasReceipt(string operationId)=>state.Receipts.Contains(operationId,StringComparer.Ordinal);
        public ResourceSnapshot Query(string resource)
        {
            var balance=state.Balances.FirstOrDefault(b=>b.ResourceId==resource);if(balance==null)return new(false,"item",true,0,0,0);
            decimal amount=contents.Where(t=>t.def.defName==resource).Sum(t=>t.stackCount);
            decimal held=state.Reservations.Where(r=>r.Leg.ResourceId==resource&&!r.Leg.Incoming).Sum(r=>r.Leg.Amount);
            return new(true,"item",true,amount,amount-held-balance.PendingCredits.Where(c=>c.AvailableAtTick>Clock()).Sum(c=>c.Amount),balance.Capacity);
        }
        public FrameworkResult Reserve(string operationId,ResourceLeg leg)
        {
            if(HasReceipt(operationId))return FrameworkResult.Ok();var old=state.Reservations.FirstOrDefault(r=>r.Id==operationId);
            if(old!=null)return MegastructureCodec.Encode(old.Leg)==MegastructureCodec.Encode(leg)?FrameworkResult.Ok():FrameworkResult.Reject("OperationConflict");
            var snapshot=Query(leg.ResourceId);
            if(!snapshot.Exists||leg.Amount<=0||decimal.Truncate(leg.Amount)!=leg.Amount||leg.Amount>int.MaxValue)return FrameworkResult.Reject("InvalidQuantity");
            if(!leg.Incoming&&snapshot.Available<leg.Amount)return FrameworkResult.Reject("InsufficientResources");
            decimal incoming=state.Reservations.Where(r=>r.Leg.Incoming&&r.Leg.ResourceId==leg.ResourceId).Sum(r=>r.Leg.Amount);
            if(leg.Incoming&&snapshot.Capacity-snapshot.Amount-incoming<leg.Amount)return FrameworkResult.Reject("StorageFull");
            if(leg.Incoming)
            {
                var def=DefDatabase<ThingDef>.GetNamedSilentFail(leg.ResourceId);
                if(def==null||def.stackLimit<=1||def.MadeFromStuff||!def.comps.NullOrEmpty())return FrameworkResult.Reject("UnsupportedItemState");
            }
            state.Reservations.Add(new ResourceReservation {Id=operationId,Leg=MegastructureCodec.Clone(leg)});return FrameworkResult.Ok();
        }
        public FrameworkResult Commit(string operationId,ResourceLeg leg)
        {
            if(HasReceipt(operationId))return FrameworkResult.Ok();if(!state.Reservations.Any(r=>r.Id==operationId))return FrameworkResult.Reject("ReservationMissing");
            int amount=checked((int)leg.Amount);
            if(leg.Incoming)
            {
                var def=DefDatabase<ThingDef>.GetNamedSilentFail(leg.ResourceId);if(def==null)return FrameworkResult.Reject("ResourceMissing");
                if(Query(leg.ResourceId).Amount+amount>Query(leg.ResourceId).Capacity)return FrameworkResult.Reject("StorageFull");
                var created=new List<Thing>();var added=new List<Thing>();
                try
                {
                    while(amount>0){int count=Math.Min(amount,def.stackLimit);var thing=ThingMaker.MakeThing(def);thing.stackCount=count;created.Add(thing);amount-=count;}
                    foreach(var thing in created){if(!contents.TryAdd(thing,false))throw new InvalidOperationException("Item storage rejected a committed credit.");added.Add(thing);}
                }
                catch {foreach(var thing in added)contents.Remove(thing);foreach(var thing in created)if(!thing.Destroyed)thing.Destroy(DestroyMode.Vanish);throw;}
                var balance=state.Balances.First(b=>b.ResourceId==leg.ResourceId);balance.PendingCredits.RemoveAll(c=>c.AvailableAtTick<=Clock());
                if(leg.AvailableAtTick>Clock())balance.PendingCredits.Add(new(leg.Amount,leg.AvailableAtTick));
            }
            else
            {
                if(Query(leg.ResourceId).Amount<amount)return FrameworkResult.Reject("InsufficientResources");
                foreach(var thing in contents.Where(t=>t.def.defName==leg.ResourceId).ToArray())
                {int count=Math.Min(amount,thing.stackCount);if(count==thing.stackCount){contents.Remove(thing);thing.Destroy(DestroyMode.Vanish);}else{thing.stackCount-=count;}amount-=count;if(amount==0)break;}
            }
            state.Reservations.RemoveAll(r=>r.Id==operationId);state.Receipts.Add(operationId);return FrameworkResult.Ok();
        }
        public void Cancel(string operationId){if(!HasReceipt(operationId))state.Reservations.RemoveAll(r=>r.Id==operationId);}
        public FrameworkResult TransferItems(string operationId,IItemTransferProvider destination,string resourceId,decimal requested)
        {
            if(destination is not ThingResourceContainer target||ReferenceEquals(this,target))return FrameworkResult.Reject("PhysicalItemTransferProviderRequired");
            if(HasReceipt(operationId)&&target.HasReceipt(operationId))return FrameworkResult.Ok();
            if(HasReceipt(operationId)||target.HasReceipt(operationId))return FrameworkResult.Reject("ItemReceiptMismatch");
            if(requested<=0||decimal.Truncate(requested)!=requested||requested>int.MaxValue)return FrameworkResult.Reject("InvalidQuantity");
            var from=Query(resourceId);var to=target.Query(resourceId);
            if(!from.Exists||!to.Exists)return FrameworkResult.Reject("ResourceMissing");
            if(from.Available<requested)return FrameworkResult.Reject("InsufficientResources");
            decimal held=target.state.Reservations.Where(r=>r.Leg.Incoming&&r.Leg.ResourceId==resourceId).Sum(r=>r.Leg.Amount);
            if(to.Capacity-to.Amount-held<requested)return FrameworkResult.Reject("StorageFull");
            // All work is synchronous on the game thread. No save or external callback occurs
            // between the move and both durable receipts. Failed moves roll the same Things back.
            var moved=new List<Thing>();int remaining=(int)requested;
            try
            {
                foreach(var original in contents.Where(t=>t.def.defName==resourceId).ToArray())
                {
                    int count=Math.Min(remaining,original.stackCount);Thing cargo;
                    if(count==original.stackCount){contents.Remove(original);cargo=original;}
                    else cargo=original.SplitOff(count);
                    moved.Add(cargo);
                    if(!target.contents.TryAdd(cargo,false))throw new InvalidOperationException("Item destination rejected cargo.");
                    remaining-=count;if(remaining==0)break;
                }
                if(remaining!=0)throw new InvalidOperationException("Item stock changed during transfer.");
            }
            catch
            {
                foreach(var cargo in moved){if(cargo.holdingOwner==target.contents)target.contents.Remove(cargo);if(cargo.holdingOwner!=contents&&!contents.TryAdd(cargo,false))throw new InvalidOperationException("Item transfer rollback failed.");}
                return FrameworkResult.Reject("ItemTransferFailed");
            }
            state.Receipts.Add(operationId);target.state.Receipts.Add(operationId);return FrameworkResult.Ok();
        }
        public void ExposeData()
        {
            Scribe_Values.Look(ref id,"id","");Scribe_Values.Look(ref instanceId,"instance","");Scribe_Values.Look(ref nodeId,"node","");
            if(Scribe.mode==LoadSaveMode.Saving)statePayload=MegastructureCodec.Encode(state);
            Scribe_Values.Look(ref statePayload,"resourceState","");Scribe_Deep.Look(ref contents,"contents",this);
            if(Scribe.mode==LoadSaveMode.LoadingVars&&!string.IsNullOrEmpty(statePayload))state=MegastructureCodec.Read<ResourceStoreState>(statePayload);
        }
    }
    public sealed partial class MegastructureManager:IResourceStoreFactory
    {
        private List<ThingResourceContainer> thingStores=new();
        public IReadOnlyList<ThingResourceContainer> ThingStores=>thingStores.AsReadOnly();
        public FrameworkResult EnsureStore(string id,NodeAddress owner,ResourceSpecification specification)
        {
            if(!specification.IsItem)return thingStores.Any(s=>s.Id==id)?FrameworkResult.Reject("SeparateItemAndScalarStoresRequired"):Resources.AddStore(id,owner,specification.Id,specification.Unit,specification.Capacity,specification.Initial);
            if(Resources.Stores.Any(s=>s.Id==id))return FrameworkResult.Reject("SeparateItemAndScalarStoresRequired");
            var store=thingStores.FirstOrDefault(s=>s.Id==id);
            if(store==null){store=new ThingResourceContainer(id,owner){Clock=()=>Resources.Tick};var registered=Resources.RegisterProvider(store);if(!registered.Success)return registered;thingStores.Add(store);}
            if(store.Owner!=owner)return FrameworkResult.Reject("StoreOwnerConflict");return store.Configure(specification);
        }
    }
}
