using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Megastructures
{
    public sealed class ResourceDocument
    {
        public List<ResourceStoreState> Stores { get; set; }=new();
        public List<ResourceTransaction> Transactions { get; set; }=new();
    }
    public sealed class ResourceStoreState
    {
        public string Id { get; set; }="";
        public NodeAddress Owner { get; set; }
        public List<ResourceBalance> Balances { get; set; }=new();
        public List<ResourceReservation> Reservations { get; set; }=new();
        public List<string> Receipts { get; set; }=new();
    }
    public sealed class ResourceBalance
    {
        public string ResourceId { get; set; }="";
        public string Unit { get; set; }="";
        public bool IsItem { get; set; }
        public decimal Capacity { get; set; }
        public decimal Amount { get; set; }
        public List<ResourceCredit> PendingCredits { get; set; }=new();
    }
    public sealed record ResourceCredit(decimal Amount,long AvailableAtTick);
    public sealed class ResourceLeg
    {
        public string ProviderId { get; set; }="";
        public string ResourceId { get; set; }="";
        public decimal Amount { get; set; }
        public bool Incoming { get; set; }
        public long AvailableAtTick { get; set; }
    }
    public sealed class ResourceReservation
    {
        public string Id { get; set; }="";
        public ResourceLeg Leg { get; set; }=new();
    }
    public sealed class ResourceTransaction
    {
        public string Id { get; set; }="";
        public List<ResourceLeg> Legs { get; set; }=new();
        public bool Committed { get; set; }
        public bool StartedCommit { get; set; }
        public string BlockedReason { get; set; }="";
    }
    public sealed record ResourceSnapshot(bool Exists,string Unit,bool IsItem,decimal Amount,decimal Available,decimal Capacity);
    public interface IResourceProvider
    {
        string Id { get; }
        bool PersistentReceipts { get; }
        ResourceSnapshot Query(string resourceId);
        FrameworkResult Reserve(string operationId,ResourceLeg leg);
        FrameworkResult Commit(string operationId,ResourceLeg leg);
        void Cancel(string operationId);
        bool HasReceipt(string operationId);
    }
    public interface IResourceStoreFactory
    {FrameworkResult EnsureStore(string id,NodeAddress owner,ResourceSpecification specification);}
    public interface IItemTransferProvider:IResourceProvider
    {FrameworkResult TransferItems(string operationId,IItemTransferProvider target,string resourceId,decimal amount);}
    public sealed class StructureResourceService:IServiceProvider
    {
        private readonly ResourceDocument document;
        private readonly Dictionary<string,IResourceProvider> providers=new(StringComparer.Ordinal);
        public long Tick { get; set; }
        public StructureResourceService(ResourceDocument document)
        {this.document=document;foreach(var store in document.Stores)providers.Add(store.Id,new MemoryResourceProvider(store,()=>Tick));}
        public object? GetService(Type serviceType)=>serviceType==typeof(StructureResourceService)?this:null;
        public IReadOnlyList<ResourceStoreState> Stores=>document.Stores.Select(MegastructureCodec.Clone).ToArray();
        public IResourceProvider? FindProvider(string id)=>providers.TryGetValue(id,out var provider)?provider:null;
        public bool HasStarted(string id)=>document.Transactions.Any(t=>t.Id==id&&t.StartedCommit);
        public string? FindStore(NodeAddress owner,string resource)=>document.Stores.FirstOrDefault(s=>s.Owner==owner&&s.Balances.Any(b=>b.ResourceId==resource))?.Id;
        public FrameworkResult RegisterProvider(IResourceProvider provider,bool replace=false)
        {if(providers.ContainsKey(provider.Id)&&!replace)return FrameworkResult.Reject("DuplicateProvider");providers[provider.Id]=provider;return FrameworkResult.Ok();}
        public FrameworkResult AddStore(string id,NodeAddress owner,string resource,string unit,decimal capacity,decimal initial=0,bool item=false)
        {
            if(string.IsNullOrWhiteSpace(id)||string.IsNullOrWhiteSpace(resource)||string.IsNullOrWhiteSpace(unit)||capacity<0||initial<0||initial>capacity||(item&&(decimal.Truncate(initial)!=initial||decimal.Truncate(capacity)!=capacity)))return FrameworkResult.Reject("InvalidStore");
            var store=document.Stores.FirstOrDefault(x=>x.Id==id);
            if(store==null){if(providers.ContainsKey(id))return FrameworkResult.Reject("DuplicateProvider");store=new ResourceStoreState {Id=id,Owner=owner};document.Stores.Add(store);providers.Add(id,new MemoryResourceProvider(store,()=>Tick));}
            if(store.Owner!=owner)return FrameworkResult.Reject("StoreOwnerConflict");
            var old=store.Balances.FirstOrDefault(x=>x.ResourceId==resource);
            if(old!=null)return old.Unit==unit&&old.IsItem==item&&old.Capacity==capacity?FrameworkResult.Ok():FrameworkResult.Reject("StoreConfigurationConflict");
            store.Balances.Add(new ResourceBalance {ResourceId=resource,Unit=unit,IsItem=item,Capacity=capacity,Amount=initial});return FrameworkResult.Ok();
        }
        public ResourceSnapshot Query(string provider,string resource)=>FindProvider(provider)?.Query(resource)??new(false,"",false,0,0,0);
        public FrameworkResult Transfer(string id,string source,string target,string resource,decimal amount)
        {
            var legs=new[]{new ResourceLeg {ProviderId=source,ResourceId=resource,Amount=amount},new ResourceLeg {ProviderId=target,ResourceId=resource,Amount=amount,Incoming=true}};
            var from=FindProvider(source);var to=FindProvider(target);
            if(from is not IItemTransferProvider&&to is not IItemTransferProvider)return Commit(id,legs);
            if(from is not IItemTransferProvider items||to is not IItemTransferProvider receiver)return FrameworkResult.Reject("PhysicalItemTransferProviderRequired");
            if(string.IsNullOrWhiteSpace(id)||amount<=0||decimal.Truncate(amount)!=amount||source==target)return FrameworkResult.Reject("InvalidQuantity");
            var transaction=document.Transactions.FirstOrDefault(t=>t.Id==id);
            if(transaction!=null&&MegastructureCodec.Encode(transaction.Legs)!=MegastructureCodec.Encode(legs))return FrameworkResult.Reject("OperationConflict");
            if(transaction?.Committed==true)return FrameworkResult.Ok();
            if(transaction==null){transaction=new ResourceTransaction {Id=id,Legs=legs.ToList()};document.Transactions.Add(transaction);}
            try
            {
                var result=items.TransferItems(id,receiver,resource,amount);
                transaction.StartedCommit=result.Success;transaction.Committed=result.Success;transaction.BlockedReason=result.Success?"":result.Code;return result;
            }
            catch(Exception error){transaction.StartedCommit=true;transaction.BlockedReason=error.GetType().Name;return FrameworkResult.Reject("TransactionPending",error.Message);}
        }
        public FrameworkResult Commit(string id,IEnumerable<ResourceLeg> requested)
        {
            var legs=requested.ToList();if(string.IsNullOrWhiteSpace(id)||legs.Count==0||legs.Any(x=>x.Amount<=0))return FrameworkResult.Reject("InvalidQuantity");
            var transaction=document.Transactions.FirstOrDefault(x=>x.Id==id);
            if(transaction!=null&&MegastructureCodec.Encode(transaction.Legs)!=MegastructureCodec.Encode(legs))return FrameworkResult.Reject("OperationConflict");
            if(transaction?.Committed==true)return FrameworkResult.Ok();
            foreach(var group in legs.GroupBy(x=>x.ResourceId,StringComparer.Ordinal))
            {
                ResourceSnapshot? first=null;
                foreach(var leg in group)
                {
                    var provider=FindProvider(leg.ProviderId);if(provider==null)return FrameworkResult.Reject("ResourceProviderMissing");
                    if(!provider.PersistentReceipts)return FrameworkResult.Reject("NonPersistentProvider");
                    var snapshot=provider.Query(leg.ResourceId);if(!snapshot.Exists)return FrameworkResult.Reject("ResourceMissing");
                    if(snapshot.IsItem&&decimal.Truncate(leg.Amount)!=leg.Amount)return FrameworkResult.Reject("InvalidQuantity");
                    if(first!=null&&(first.Unit!=snapshot.Unit||first.IsItem!=snapshot.IsItem))return FrameworkResult.Reject("ResourceUnitConflict");first=snapshot;
                }
            }
            if(transaction==null){transaction=new ResourceTransaction {Id=id,Legs=MegastructureCodec.Clone(legs)};document.Transactions.Add(transaction);}
            for(int index=0;index<legs.Count;index++)
            {
                var leg=legs[index];var provider=providers[leg.ProviderId];string legId=id+"/"+index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if(provider.HasReceipt(legId))continue;
                var result=provider.Reserve(legId,leg);
                if(!result.Success)
                {
                    transaction.BlockedReason=result.Code;
                    if(!transaction.StartedCommit)for(int j=0;j<legs.Count;j++)providers[legs[j].ProviderId].Cancel(id+"/"+j.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return transaction.StartedCommit?FrameworkResult.Reject("TransactionPending",result.Code):result;
                }
            }
            transaction.StartedCommit=true;
            try
            {
                for(int index=0;index<legs.Count;index++)
                {
                    var leg=legs[index];var provider=providers[leg.ProviderId];string legId=id+"/"+index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if(provider.HasReceipt(legId))continue;
                    var result=provider.Commit(legId,leg);if(!result.Success){transaction.BlockedReason=result.Code;return FrameworkResult.Reject("TransactionPending",result.Code);}
                }
                transaction.Committed=true;transaction.BlockedReason="";return FrameworkResult.Ok();
            }
            catch(Exception e){transaction.BlockedReason=e.GetType().Name;return FrameworkResult.Reject("TransactionPending",e.Message);}
        }
    }
    internal sealed class MemoryResourceProvider:IResourceProvider
    {
        private readonly ResourceStoreState store;private readonly Func<long> clock;
        public string Id=>store.Id;
        public bool PersistentReceipts=>true;
        public MemoryResourceProvider(ResourceStoreState store,Func<long> clock){this.store=store;this.clock=clock;}
        public bool HasReceipt(string id)=>store.Receipts.Contains(id,StringComparer.Ordinal);
        public ResourceSnapshot Query(string resource)
        {
            var balance=store.Balances.FirstOrDefault(x=>x.ResourceId==resource);if(balance==null)return new(false,"",false,0,0,0);
            decimal held=store.Reservations.Where(r=>r.Leg.ResourceId==resource&&!r.Leg.Incoming).Sum(r=>r.Leg.Amount);
            return new(true,balance.Unit,balance.IsItem,balance.Amount,balance.Amount-balance.PendingCredits.Where(x=>x.AvailableAtTick>clock()).Sum(x=>x.Amount)-held,balance.Capacity);
        }
        public FrameworkResult Reserve(string id,ResourceLeg leg)
        {
            if(HasReceipt(id))return FrameworkResult.Ok();var old=store.Reservations.FirstOrDefault(x=>x.Id==id);
            if(old!=null)return MegastructureCodec.Encode(old.Leg)==MegastructureCodec.Encode(leg)?FrameworkResult.Ok():FrameworkResult.Reject("OperationConflict");
            var snapshot=Query(leg.ResourceId);if(!snapshot.Exists)return FrameworkResult.Reject("ResourceMissing");
            if(!leg.Incoming&&snapshot.Available<leg.Amount)return FrameworkResult.Reject("InsufficientResources");
            decimal incoming=store.Reservations.Where(x=>x.Leg.Incoming&&x.Leg.ResourceId==leg.ResourceId).Sum(x=>x.Leg.Amount);
            if(leg.Incoming&&snapshot.Capacity-snapshot.Amount-incoming<leg.Amount)return FrameworkResult.Reject("StorageFull");
            store.Reservations.Add(new ResourceReservation {Id=id,Leg=MegastructureCodec.Clone(leg)});return FrameworkResult.Ok();
        }
        public FrameworkResult Commit(string id,ResourceLeg leg)
        {
            if(HasReceipt(id))return FrameworkResult.Ok();if(!store.Reservations.Any(r=>r.Id==id))return FrameworkResult.Reject("ReservationMissing");
            var balance=store.Balances.First(x=>x.ResourceId==leg.ResourceId);
            balance.PendingCredits.RemoveAll(x=>x.AvailableAtTick<=clock());
            if(leg.Incoming&&balance.Amount+leg.Amount>balance.Capacity)return FrameworkResult.Reject("StorageFull");
            if(!leg.Incoming&&balance.Amount<leg.Amount)return FrameworkResult.Reject("InsufficientResources");
            balance.Amount+=leg.Incoming?leg.Amount:-leg.Amount;
            if(leg.Incoming&&leg.AvailableAtTick>clock())balance.PendingCredits.Add(new(leg.Amount,leg.AvailableAtTick));
            store.Reservations.RemoveAll(r=>r.Id==id);store.Receipts.Add(id);return FrameworkResult.Ok();
        }
        public void Cancel(string id){if(!HasReceipt(id))store.Reservations.RemoveAll(r=>r.Id==id);}
    }
}
