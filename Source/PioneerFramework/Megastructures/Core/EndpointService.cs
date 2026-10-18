using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Megastructures
{
    public readonly record struct EndpointAddress(string InstanceId,string NodeId,string CapabilityId,string EndpointId);
    public sealed class EndpointDefinition
    {
        public string Id { get; set; }="";public string Service { get; set; }="cargo";
        public string StoreProviderId { get; set; }="";public string MapCapabilityId { get; set; }="";
        public int Capacity { get; set; }=1;public DVector3 Offset { get; set; }public DVector3 Direction { get; set; }=new(0,1,0);
    }
    public sealed class TransportCapabilityData {public List<EndpointDefinition> Endpoints { get; set; }=new();}
    public sealed class TransportDocument {public List<TransportTicket> Tickets { get; set; }=new();}
    public sealed class TransportRequest
    {
        public string OperationId { get; set; }="";public EndpointAddress Source { get; set; }public EndpointAddress Target { get; set; }
        public int ActorFactionId { get; set; }=-1;public string ResourceId { get; set; }="";public decimal Amount { get; set; }
        public string Service { get; set; }="cargo";
    }
    public sealed class TransportTicket
    {
        public string SourceProviderId { get; set; }="";public string TargetProviderId { get; set; }="";
        public TransportRequest Request { get; set; }=new();public long SourceRevision { get; set; }public long TargetRevision { get; set; }
        public string Status { get; set; }="Requested";public string BlockedReason { get; set; }="";
    }
    public sealed record EndpointEvaluation(bool Success,string Code,SpatialState State,EndpointDefinition? Definition,long Revision);
    public interface ITransportEndpointProvider
    {EndpointDefinition? Resolve(EndpointAddress address,CapabilityState capability);}
    public sealed class StructureEndpointService
    {
        private readonly MegastructureRuntime runtime;private readonly StructureSpatialService spatial;private readonly StructureResourceService resources;private readonly TransportDocument document;
        private readonly Dictionary<string,ITransportEndpointProvider> providers=new(StringComparer.Ordinal);
        public StructureEndpointService(MegastructureRuntime runtime,StructureSpatialService spatial,StructureResourceService resources,TransportDocument document)
        {this.runtime=runtime;this.spatial=spatial;this.resources=resources;this.document=document;providers.Add("pfl.transport",new ConfiguredEndpoints());}
        public IReadOnlyList<TransportTicket> Tickets=>document.Tickets.Select(MegastructureCodec.Clone).ToArray();
        public FrameworkResult RegisterProvider(string handlerId,ITransportEndpointProvider provider)=>providers.TryAdd(handlerId,provider)?FrameworkResult.Ok():FrameworkResult.Reject("DuplicateEndpointProvider");
        public EndpointEvaluation Resolve(EndpointAddress address,double tick)
        {
            EndpointEvaluation Fail(string code)=>new(false,code,default,null,-1);
            var instance=runtime.Find(address.InstanceId);var node=instance?.Nodes.FirstOrDefault(n=>n.Id==address.NodeId);
            if(instance==null||instance.Lifecycle!=StructureLifecycle.Active||node==null||!node.Enabled)return Fail("EndpointUnavailable");
            var capability=node.Capabilities.FirstOrDefault(c=>c.Id==address.CapabilityId&&c.Enabled&&string.IsNullOrEmpty(c.BlockedReason));
            if(capability==null||!providers.TryGetValue(capability.HandlerId,out var provider))return Fail("EndpointProviderMissing");
            var admission=runtime.CheckCapability(instance,address.NodeId,capability);if(!admission.Success)return Fail(admission.Code);
            EndpointDefinition? definition;
            try{definition=provider.Resolve(address,MegastructureCodec.Clone(capability));}catch(Exception){return Fail("EndpointProviderError");}
            if(definition==null||definition.Capacity<1||!definition.Offset.IsFinite()||!definition.Direction.IsFinite())return Fail("InvalidEndpoint");
            var result=spatial.Evaluate(new(address.InstanceId,address.NodeId),tick);if(!result.Success)return Fail(result.Code);
            var state=result.State;var offset=state.Rotation.Rotate(definition.Offset);
            return new(true,"Ok",state with {PositionSystem=new SystemVec3(state.PositionSystem.v+offset),VelocitySystem=new SystemVec3(state.VelocitySystem.v+DVector3.Cross(state.AngularVelocity,offset))},definition,instance.Revision);
        }
        public FrameworkResult Reserve(TransportRequest request,double tick)
        {
            if(string.IsNullOrWhiteSpace(request.OperationId)||request.Amount<=0||request.Source==request.Target)return FrameworkResult.Reject("InvalidTransport");
            var ticket=document.Tickets.FirstOrDefault(t=>t.Request.OperationId==request.OperationId);
            if(ticket!=null)
            {
                if(MegastructureCodec.Encode(ticket.Request)!=MegastructureCodec.Encode(request))return FrameworkResult.Reject("OperationConflict");
                if(ticket.Status is "Reserved" or "Arrived")return FrameworkResult.Ok();if(ticket.Status=="Cancelled")return FrameworkResult.Reject("TransportCancelled");
            }
            else {ticket=new TransportTicket {Request=MegastructureCodec.Clone(request)};document.Tickets.Add(ticket);}
            var source=Resolve(request.Source,tick);var target=Resolve(request.Target,tick);
            if(!source.Success||!target.Success)return FrameworkResult.Reject(!source.Success?source.Code:target.Code);
            foreach(var address in new[]{request.Source,request.Target})
            {
                var instance=runtime.Find(address.InstanceId)!;
                if(!StructurePermissions.Allows(instance,address.NodeId,request.ActorFactionId,StructurePermission.Load))return FrameworkResult.Reject("PermissionDenied");
            }
            if(source.Definition!.Service!=request.Service||target.Definition!.Service!=request.Service)return FrameworkResult.Reject("EndpointServiceMismatch");
            bool Busy(EndpointAddress address,int capacity)=>document.Tickets.Count(t=>t.Status=="Reserved"&&(t.Request.Source==address||t.Request.Target==address))>=capacity;
            if(Busy(request.Source,source.Definition.Capacity)||Busy(request.Target,target.Definition.Capacity))return FrameworkResult.Reject("EndpointBusy");
            string Store(string id,EndpointAddress address)=>id.Replace("$instance",address.InstanceId,StringComparison.Ordinal).Replace("$node",address.NodeId,StringComparison.Ordinal);
            ticket.SourceProviderId=Store(source.Definition.StoreProviderId,request.Source);ticket.TargetProviderId=Store(target.Definition.StoreProviderId,request.Target);
            ticket.SourceRevision=source.Revision;ticket.TargetRevision=target.Revision;ticket.Status="Reserved";return FrameworkResult.Ok();
        }
        public FrameworkResult Arrive(string operationId,double tick)
        {
            var ticket=document.Tickets.FirstOrDefault(t=>t.Request.OperationId==operationId);if(ticket==null)return FrameworkResult.Reject("TransportMissing");
            if(ticket.Status=="Arrived")return FrameworkResult.Ok();if(ticket.Status!="Reserved")return FrameworkResult.Reject("TransportNotReserved");
            if(!resources.HasStarted("transport/"+operationId))
            {
                var source=Resolve(ticket.Request.Source,tick);var target=Resolve(ticket.Request.Target,tick);
                if(!source.Success||!target.Success)return FrameworkResult.Reject(!source.Success?source.Code:target.Code);
                if(source.Revision!=ticket.SourceRevision||target.Revision!=ticket.TargetRevision)return FrameworkResult.Reject("EndpointChanged");
            }
            if(ticket.Request.Service!="cargo")return FrameworkResult.Reject("ExternalArrivalRequired");
            resources.Tick=runtime.CurrentTick;
            var result=resources.Transfer("transport/"+operationId,ticket.SourceProviderId,ticket.TargetProviderId,ticket.Request.ResourceId,ticket.Request.Amount);
            ticket.BlockedReason=result.Success?"":result.Code;if(result.Success)ticket.Status="Arrived";return result;
        }
        public FrameworkResult Cancel(string id)
        {
            var ticket=document.Tickets.FirstOrDefault(t=>t.Request.OperationId==id);if(ticket==null)return FrameworkResult.Reject("TransportMissing");
            if(ticket.Status=="Arrived")return FrameworkResult.Reject("AlreadyDelivered");
            if(resources.HasStarted("transport/"+id))return FrameworkResult.Reject("TransactionPending");ticket.Status="Cancelled";return FrameworkResult.Ok();
        }
        private sealed class ConfiguredEndpoints:ITransportEndpointProvider
        {public EndpointDefinition? Resolve(EndpointAddress address,CapabilityState capability)=>MegastructureCodec.Read<TransportCapabilityData>(capability.Payload).Endpoints.FirstOrDefault(e=>e.Id==address.EndpointId);}
    }
}
