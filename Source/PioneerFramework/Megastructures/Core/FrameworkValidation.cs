using System;
using System.Linq;

namespace PioneerFramework.Megastructures
{
    internal static class FrameworkValidation
    {
        internal static bool StableId(string id)=>!string.IsNullOrWhiteSpace(id)&&!id.Contains('/');
        internal static string? Check(FrameworkDocument document)
        {
            try
            {
                bool Capabilities(System.Collections.Generic.List<CapabilityState> values)=>values!=null&&values.All(c=>c!=null&&StableId(c.Id)&&!string.IsNullOrWhiteSpace(c.HandlerId)&&c.Version>0&&c.Payload!=null&&c.Payload.Length<=1_048_576)&&values.Select(c=>c.Id).Distinct().Count()==values.Count;
                bool Access(StructureAccessPolicy access)=>access!=null&&access.Grants!=null&&access.Grants.All(g=>g!=null&&g.Permissions!=null&&g.Permissions.All(Enum.IsDefined));
                if(document.Resources==null||document.Transport==null||document.Resources.Stores==null||document.Resources.Transactions==null||document.Transport.Tickets==null)return "InvalidDocument";
                if(document.Backends==null||document.Backends.Associations==null||document.Backends.Operations==null||document.Surfaces==null||document.MapSites==null)return "InvalidSurfaceDocument";
                bool Backend(StructureBackendRef b)=>StableId(b.ProviderId)&&!string.IsNullOrWhiteSpace(b.StableId);
                bool Node(NodeAddress n)=>document.Instances.Any(s=>s.Id==n.InstanceId&&s.Nodes.Any(x=>x.Id==n.NodeId));
                if(document.Backends.Associations.Any(a=>a==null||!Node(a.Node)||!Backend(a.Backend)||a.Generation<1||!Enum.IsDefined(a.Ownership)||!Enum.IsDefined(a.Disposition))||document.Backends.Associations.Select(a=>(a.Node,a.Backend)).Distinct().Count()!=document.Backends.Associations.Count)return "InvalidBackendDocument";
                var active=document.Backends.Associations.Where(a=>!a.Released).ToArray();
                if(active.Select(a=>a.Backend).Distinct().Count()!=active.Length)return "DuplicateBackendAuthority";
                if(document.Backends.Operations.Any(o=>o==null||string.IsNullOrWhiteSpace(o.OperationId)||string.IsNullOrWhiteSpace(o.Fingerprint)||!Enum.IsDefined(o.Stage)||!Enum.IsDefined(o.Disposition))||document.Backends.Operations.Select(o=>o.OperationId).Distinct().Count()!=document.Backends.Operations.Count)return "InvalidBackendDocument";
                if(document.Surfaces.Any(s=>s==null||!Node(s.Address.Node)||!StableId(s.Address.SurfaceId)||!Backend(s.Backend)||s.Version<1||s.AssociationGeneration<1)||document.Surfaces.Select(s=>s.Address).Distinct().Count()!=document.Surfaces.Count)return "InvalidSurfaceDocument";
                if(document.MapSites.Any(s=>s==null||!Node(s.Address.Node)||string.IsNullOrWhiteSpace(s.Address.SiteKey)||s.MapParentId< -1||s.MapId< -1||s.Address.Cell is { } c&&(c.Surface.Node!=s.Address.Node||c.CellId<0||!StableId(c.Surface.SurfaceId)))||document.MapSites.Select(s=>s.Address).Distinct().Count()!=document.MapSites.Count)return "InvalidMapSiteDocument";
                foreach(var instance in document.Instances)
                {
                    if(!StableId(instance.Id)||!Enum.IsDefined(instance.Lifecycle)||instance.Revision<0||!Access(instance.Access)||!Capabilities(instance.Capabilities)||instance.Connections==null)return "InvalidDocument";
                    foreach(var node in instance.Nodes)if(!StableId(node.Id)||node.Spatial==null||!Enum.IsDefined(node.Spatial.Ownership)||node.Bindings==null||node.Bindings.Any(b=>b==null||string.IsNullOrWhiteSpace(b.Id)||string.IsNullOrWhiteSpace(b.Kind)||!Enum.IsDefined(b.Ownership))||!Access(node.Access)||!Capabilities(node.Capabilities))return "InvalidDocument";
                    if(instance.Connections.Select(c=>c.Id).Distinct().Count()!=instance.Connections.Count||instance.Connections.Any(c=>!StableId(c.Id)||!double.IsFinite(c.CapacityPerStep)||c.CapacityPerStep<=0||instance.Nodes.All(n=>n.Id!=c.SourceNode)||instance.Nodes.All(n=>n.Id!=c.TargetNode)))return "InvalidConnection";
                }
                var stores=document.Resources.Stores;
                if(stores.Select(s=>s.Id).Distinct().Count()!=stores.Count)return "InvalidResourceDocument";
                foreach(var store in stores)
                {
                    if(string.IsNullOrWhiteSpace(store.Id)||store.Balances==null||store.Reservations==null||store.Receipts==null||store.Balances.Select(b=>b.ResourceId).Distinct().Count()!=store.Balances.Count)return "InvalidResourceDocument";
                    foreach(var balance in store.Balances)
                        if(string.IsNullOrWhiteSpace(balance.ResourceId)||string.IsNullOrWhiteSpace(balance.Unit)||balance.Amount<0||balance.Capacity<balance.Amount||balance.PendingCredits==null||balance.PendingCredits.Any(c=>c.Amount<=0)||(balance.IsItem&&(decimal.Truncate(balance.Amount)!=balance.Amount||decimal.Truncate(balance.Capacity)!=balance.Capacity)))return "InvalidResourceDocument";
                    if(store.Reservations.Any(r=>string.IsNullOrWhiteSpace(r.Id)||r.Leg==null||r.Leg.Amount<=0))return "InvalidResourceDocument";
                }
                if(document.Resources.Transactions.Select(t=>t.Id).Distinct().Count()!=document.Resources.Transactions.Count||document.Resources.Transactions.Any(t=>string.IsNullOrWhiteSpace(t.Id)||t.Legs==null||t.Legs.Any(l=>l==null||l.Amount<=0||string.IsNullOrWhiteSpace(l.ProviderId)||string.IsNullOrWhiteSpace(l.ResourceId))))return "InvalidResourceDocument";
                if(document.Transport.Tickets.Any(t=>t==null||t.Request==null||string.IsNullOrWhiteSpace(t.Request.OperationId)||t.Request.Amount<=0))return "InvalidTransportDocument";
                return null;
            }
            catch(Exception error) when(error is NullReferenceException or ArgumentException or OverflowException){return "InvalidDocument";}
        }
    }
}
