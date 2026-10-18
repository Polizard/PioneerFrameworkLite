using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PioneerFramework.Megastructures
{
    public sealed partial class MegastructureRuntime
    {
        private FrameworkDocument document=new();
        private readonly Dictionary<string,StructureDefinition> definitions=new(StringComparer.Ordinal);
        private readonly Queue<Action> pendingCommands=new();
        private bool flushing;
        public long CurrentTick { get; set; }
        internal bool DeferCommands { get; set; }
        internal void FlushCommands()
        {
            if(flushing)return;flushing=true;
            try{int budget=4096;while(budget-->0&&pendingCommands.TryDequeue(out var command))command();}
            finally{flushing=false;}
        }
        public event Action<StructureEvent>? Changed;
        public Action<Exception>? CallbackError { get; set; }
        public Func<StructureState,string?>? ActivationGuard { get; set; }
        public Func<StructureState,string?>? RetirementGuard { get; set; }
        public Func<FrameworkDocument,string?>? DocumentGuard { get; set; }
        public Func<StructureState,StructureCommand,FrameworkResult>? CommandExecutor { get; set; }
        public Func<StructureState,StructureCommand,string?>? MutationGuard { get; set; }
        public Func<StructureState,string,CapabilityState,FrameworkResult>? CapabilityGuard { get; set; }
        public FrameworkResult CheckCapability(StructureState state,string nodeId,CapabilityState capability)=>!capability.Enabled?FrameworkResult.Reject("CapabilityDisabled"):CapabilityGuard?.Invoke(state,nodeId,capability)??(string.IsNullOrEmpty(capability.BlockedReason)?FrameworkResult.Ok():FrameworkResult.Reject(capability.BlockedReason));
        public IReadOnlyList<StructureState> Snapshots=>document.Instances.Select(MegastructureCodec.Clone).ToArray();
        internal IEnumerable<StructureState> LiveInstances=>document.Instances;
        internal ResourceDocument ResourceState=>document.Resources;
        internal BackendDocument BackendState=>document.Backends;
        internal List<StructureMapSiteState> MapSiteState=>document.MapSites;
        internal List<SurfaceBindingState> SurfaceState=>document.Surfaces;
        internal StructureDefinition? Definition(string id)=>definitions.GetValueOrDefault(id);
        internal TransportDocument TransportState=>document.Transport;
        public FrameworkResult? GetOperationResult(string id)=>document.Operations.FirstOrDefault(x=>x.Id==id)?.Result;
        internal StructureState? Find(string id)=>document.Instances.FirstOrDefault(x=>x.Id==id);
        public bool HasDefinition(string id)=>definitions.ContainsKey(id);
        public StructureState? GetSnapshot(string id)=>Find(id) is { } state?MegastructureCodec.Clone(state):null;
        public FrameworkDocument Export()=>MegastructureCodec.Clone(document);
        internal FrameworkResult PreviewCreate(CreateStructureRequest request)
        {
            var preview=new MegastructureRuntime{CurrentTick=CurrentTick};
            foreach(var definition in definitions.Values)preview.RegisterDefinition(definition);
            var restored=preview.Restore(Export());if(!restored.Success)return restored;
            preview.DocumentGuard=DocumentGuard;
            return preview.Create(request);
        }
        public FrameworkResult RegisterDefinition(StructureDefinition definition)
        {
            if(string.IsNullOrWhiteSpace(definition.Id)||definition.Nodes.Count==0||definition.Nodes.Any(x=>!FrameworkValidation.StableId(x.Id))||definition.Nodes.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=definition.Nodes.Count) return FrameworkResult.Reject("InvalidDefinition");
            if(definition.Capabilities.Concat(definition.Nodes.SelectMany(n=>n.Capabilities)).Any(c=>!FrameworkValidation.StableId(c.Id)))return FrameworkResult.Reject("InvalidCapability");
            if(definitions.ContainsKey(definition.Id))return FrameworkResult.Reject("DuplicateDefinition");
            definitions.Add(definition.Id,MegastructureCodec.Clone(definition));return FrameworkResult.Ok();
        }
        public FrameworkResult Restore(FrameworkDocument value)
        {
            if(value.SchemaVersion!=1)return FrameworkResult.Reject("UnsupportedSchema");
            if(value.Instances==null||value.Operations==null||value.Instances.Any(x=>x==null||string.IsNullOrWhiteSpace(x.Id)||x.Nodes==null||x.Nodes.Count==0||x.Nodes.Any(n=>n==null||string.IsNullOrWhiteSpace(n.Id))||x.Nodes.Select(n=>n.Id).Distinct().Count()!=x.Nodes.Count)||value.Instances.Select(x=>x.Id).Distinct().Count()!=value.Instances.Count||value.Operations.Any(x=>string.IsNullOrWhiteSpace(x.Id))||value.Operations.Select(x=>x.Id).Distinct().Count()!=value.Operations.Count)return FrameworkResult.Reject("InvalidDocument");
            string? shape=FrameworkValidation.Check(value);if(shape!=null)return FrameworkResult.Reject(shape);
            string? error=DocumentGuard?.Invoke(value);if(error!=null)return FrameworkResult.Reject(error);
            document=MegastructureCodec.Clone(value);return FrameworkResult.Ok();
        }
        public FrameworkResult Create(CreateStructureRequest request)
        {
            if(DeferCommands){var queued=MegastructureCodec.Clone(request);pendingCommands.Enqueue(()=>Create(queued));return new(true,"Queued",InstanceId:request.InstanceId);}
            var prior=Begin(request.OperationId,"create:"+MegastructureCodec.Encode(request),out var receipt);if(prior!=null)return prior;
            if(!FrameworkValidation.StableId(request.InstanceId)||!definitions.TryGetValue(request.DefinitionId,out var definition))return FrameworkResult.Reject("InvalidDefinition");
            if(Find(request.InstanceId)!=null)return FrameworkResult.Reject("DuplicateInstance");
            if(request.SpatialOverrides.Keys.Any(k=>definition.Nodes.All(n=>n.Id!=k)))return FrameworkResult.Reject("NodeMissing");
            var state=new StructureState {Id=request.InstanceId,DefinitionId=definition.Id,Access=MegastructureCodec.Clone(definition.Access),OwnerFactionId=request.OwnerFactionId,Connections=MegastructureCodec.Clone(definition.Connections),Capabilities=MegastructureCodec.Clone(definition.Capabilities)};
            foreach(var capability in state.Capabilities)capability.LastTick=CurrentTick;
            foreach(var template in definition.Nodes)
            {
                var node=new NodeState {Id=template.Id,Access=MegastructureCodec.Clone(template.Access),Spatial=MegastructureCodec.Clone(request.SpatialOverrides.TryGetValue(template.Id,out var binding)?binding:template.Spatial),Capabilities=MegastructureCodec.Clone(template.Capabilities)};
                if(node.Spatial.Mode==SpatialMode.NodeRelative&&string.IsNullOrEmpty(node.Spatial.Parent.InstanceId))node.Spatial.Parent=new(request.InstanceId,node.Spatial.Parent.NodeId);
                foreach(var capability in node.Capabilities)capability.LastTick=CurrentTick;
                state.Nodes.Add(node);
            }
            var candidate=Export();candidate.Instances.Add(state);string? error=DocumentGuard?.Invoke(candidate);if(error!=null)return FrameworkResult.Reject(error);
            document.Instances.Add(state);return Complete(receipt!,state,"Created");
        }
        public FrameworkResult Execute(StructureCommand command)
        {
            if(DeferCommands){var queued=MegastructureCodec.Clone(command);pendingCommands.Enqueue(()=>Execute(queued));return new(true,"Queued");}
            var prior=Begin(command.OperationId,"command:"+MegastructureCodec.Encode(command),out var receipt);if(prior!=null)return prior;
            var current=Find(command.InstanceId);if(current==null)return FrameworkResult.Reject("InstanceMissing");
            if(!StructurePermissions.Allows(current,command))return FrameworkResult.Reject("PermissionDenied");
            if(command.Kind is StructureCommandKind.ChangeOwner or StructureCommandKind.SetAccessPolicy&&current.OwnerFactionId!=command.ActorFactionId)return FrameworkResult.Reject("PermissionDenied");
            if(command.ExpectedRevision>=0&&command.ExpectedRevision!=current.Revision)return FrameworkResult.Reject("RevisionConflict");
            if(current.Lifecycle==StructureLifecycle.Retired)return FrameworkResult.Reject("Retired");
            if(current.Lifecycle==StructureLifecycle.Decommissioning&&command.Kind!=StructureCommandKind.FinishRetirement)return FrameworkResult.Reject("RetirementInProgress");
            var state=MegastructureCodec.Clone(current);var node=state.Nodes.FirstOrDefault(x=>x.Id==command.NodeId);
            string? error=MutationGuard?.Invoke(state,command);if(error!=null)return FrameworkResult.Reject(error);
            switch(command.Kind)
            {
                case StructureCommandKind.Activate:
                    if(state.Lifecycle is not (StructureLifecycle.Created or StructureLifecycle.Suspended))return FrameworkResult.Reject("InvalidTransition");
                    error=ActivationGuard?.Invoke(state);state.Lifecycle=StructureLifecycle.Active;foreach(var c in state.Capabilities.Concat(state.Nodes.SelectMany(n=>n.Capabilities)))c.LastTick=CurrentTick;break;
                case StructureCommandKind.Suspend:
                    if(state.Lifecycle!=StructureLifecycle.Active)return FrameworkResult.Reject("InvalidTransition");state.Lifecycle=StructureLifecycle.Suspended;break;
                case StructureCommandKind.BeginRetirement:
                    if(state.Lifecycle==StructureLifecycle.Decommissioning)return FrameworkResult.Reject("InvalidTransition");
                    error=CoreRetirementGuard(state)??RetirementGuard?.Invoke(state);state.Lifecycle=StructureLifecycle.Decommissioning;break;
                case StructureCommandKind.FinishRetirement:
                    if(state.Lifecycle!=StructureLifecycle.Decommissioning)return FrameworkResult.Reject("InvalidTransition");
                    error=CoreRetirementGuard(state)??RetirementGuard?.Invoke(state);state.Lifecycle=StructureLifecycle.Retired;break;
                case StructureCommandKind.ChangeOwner:state.OwnerFactionId=command.NewOwnerFactionId;state.Access=new();foreach(var n in state.Nodes)n.Access=new();break;
                case StructureCommandKind.BindNode:
                    if(node==null||command.Spatial==null)return FrameworkResult.Reject("NodeMissing");
                    if(node.Spatial.Mode is SpatialMode.PhysicalGroup or SpatialMode.Custom&&node.Spatial.Ownership!=BindingOwnership.Borrowed&&MegastructureCodec.Encode(node.Spatial)!=MegastructureCodec.Encode(command.Spatial))return FrameworkResult.Reject("OwnedBindingInUse");
                    node.Spatial=MegastructureCodec.Clone(command.Spatial);break;
                case StructureCommandKind.SetNodeEnabled:
                    if(node==null)return FrameworkResult.Reject("NodeMissing");node.Enabled=command.Enabled;break;
                case StructureCommandKind.AddCapability:
                    if(command.Capability==null||!FrameworkValidation.StableId(command.Capability.Id))return FrameworkResult.Reject("InvalidCapability");
                    var capabilities=string.IsNullOrEmpty(command.NodeId)?state.Capabilities:node?.Capabilities;
                    if(capabilities==null)return FrameworkResult.Reject("NodeMissing");if(capabilities.Any(c=>c.Id==command.Capability.Id))return FrameworkResult.Reject("DuplicateCapability");var added=MegastructureCodec.Clone(command.Capability);added.LastTick=CurrentTick;capabilities.Add(added);break;
                case StructureCommandKind.Connect:
                    var connection=command.Connection;
                    if(connection==null||string.IsNullOrWhiteSpace(connection.Id)||state.Connections.Any(x=>x.Id==connection.Id)||state.Nodes.All(n=>n.Id!=connection.SourceNode)||state.Nodes.All(n=>n.Id!=connection.TargetNode)||!double.IsFinite(connection.CapacityPerStep)||connection.CapacityPerStep<=0)return FrameworkResult.Reject("InvalidConnection");
                    state.Connections.Add(MegastructureCodec.Clone(connection));break;
                case StructureCommandKind.Disconnect:if(state.Connections.RemoveAll(x=>x.Id==command.ConnectionId)==0)return FrameworkResult.Reject("ConnectionMissing");break;
                case StructureCommandKind.Maneuver:break;
                case StructureCommandKind.AddNode:
                    if(command.NodeTemplate==null||!FrameworkValidation.StableId(command.NodeTemplate.Id)||state.Nodes.Any(n=>n.Id==command.NodeTemplate.Id)||command.NodeTemplate.Capabilities.Any(c=>!FrameworkValidation.StableId(c.Id)))return FrameworkResult.Reject("InvalidNode");
                    var template=command.NodeTemplate;var newNode=new NodeState {Id=template.Id,Access=MegastructureCodec.Clone(template.Access),Spatial=MegastructureCodec.Clone(template.Spatial),Capabilities=MegastructureCodec.Clone(template.Capabilities)};
                    if(newNode.Spatial.Mode==SpatialMode.NodeRelative&&string.IsNullOrEmpty(newNode.Spatial.Parent.InstanceId))newNode.Spatial.Parent=new(state.Id,newNode.Spatial.Parent.NodeId);
                    foreach(var capability in newNode.Capabilities)capability.LastTick=CurrentTick;state.Nodes.Add(newNode);break;
                case StructureCommandKind.RemoveNode:
                    if(node==null)return FrameworkResult.Reject("NodeMissing");if(state.Nodes.Count<=1)return FrameworkResult.Reject("LastNode");
                    if(document.Resources.Stores.Any(s=>s.Owner==new NodeAddress(state.Id,node.Id)&&(s.Balances.Any(b=>b.Amount>0)||s.Reservations.Count>0)))return FrameworkResult.Reject("StoredResourcesRemain");
                    if(document.Transport.Tickets.Any(t=>t.Status=="Reserved"&&((t.Request.Source.InstanceId==state.Id&&t.Request.Source.NodeId==node.Id)||(t.Request.Target.InstanceId==state.Id&&t.Request.Target.NodeId==node.Id))))return FrameworkResult.Reject("ActiveTransportRemain");
                    if(node.Bindings.Any(b=>b.Ownership!=BindingOwnership.Borrowed)||(node.Spatial.Mode is SpatialMode.PhysicalGroup or SpatialMode.Custom&&node.Spatial.Ownership!=BindingOwnership.Borrowed))return FrameworkResult.Reject("OwnedBindingInUse");
                    if(state.Connections.Any(c=>c.SourceNode==node.Id||c.TargetNode==node.Id))return FrameworkResult.Reject("NodeConnected");state.Nodes.Remove(node);break;
                case StructureCommandKind.AttachBackend:
                    if(node==null||command.Backend==null||string.IsNullOrWhiteSpace(command.Backend.Id)||string.IsNullOrWhiteSpace(command.Backend.Kind))return FrameworkResult.Reject("InvalidBinding");
                    if(node.Bindings.Any(b=>b.Kind==command.Backend.Kind&&b.Id==command.Backend.Id))return FrameworkResult.Reject("DuplicateBinding");node.Bindings.Add(MegastructureCodec.Clone(command.Backend));break;
                case StructureCommandKind.SupplyWork:
                    var project=node?.Capabilities.FirstOrDefault(c=>c.Id==command.CapabilityId&&c.HandlerId=="pfl.construction");
                    if(project==null||command.WorkAmount<=0)return FrameworkResult.Reject("InvalidWork");
                    var projectData=MegastructureCodec.Read<ConstructionCapabilityData>(project.Payload);projectData.AvailableWork+=command.WorkAmount;project.Payload=MegastructureCodec.Encode(projectData);break;
                case StructureCommandKind.SetCapabilityEnabled:
                    var selected=(node?.Capabilities??state.Capabilities).FirstOrDefault(c=>c.Id==command.CapabilityId);
                    if(selected==null)return FrameworkResult.Reject("CapabilityMissing");selected.Enabled=command.Enabled;selected.LastTick=CurrentTick;break;
                case StructureCommandKind.UpdateCapability:
                    var replacement=(node?.Capabilities??state.Capabilities).FirstOrDefault(c=>c.Id==command.CapabilityId);
                    if(replacement==null||command.Capability==null||replacement.HandlerId!=command.Capability.HandlerId||command.Capability.Payload.Length>1_048_576)return FrameworkResult.Reject("InvalidCapability");
                    replacement.Payload=command.Capability.Payload;replacement.Version=command.Capability.Version;replacement.BlockedReason="";break;
                case StructureCommandKind.SetCombatEnabled:break;
                case StructureCommandKind.SetAccessPolicy:
                    if(command.Access==null)return FrameworkResult.Reject("InvalidPolicy");
                    if(string.IsNullOrEmpty(command.NodeId))state.Access=MegastructureCodec.Clone(command.Access);
                    else if(node!=null)node.Access=MegastructureCodec.Clone(command.Access);else return FrameworkResult.Reject("NodeMissing");break;
                default:return FrameworkResult.Reject("UnsupportedCommand");
            }
            if(error!=null)return FrameworkResult.Reject(error);
            var candidate=Export();candidate.Instances[candidate.Instances.FindIndex(x=>x.Id==state.Id)]=state;error=DocumentGuard?.Invoke(candidate);if(error!=null)return FrameworkResult.Reject(error);
            if(command.Kind is StructureCommandKind.Maneuver or StructureCommandKind.FinishRetirement or StructureCommandKind.ChangeOwner or StructureCommandKind.SetCombatEnabled)
            {var execution=CommandExecutor?.Invoke(state,command)??(command.Kind==StructureCommandKind.Maneuver?FrameworkResult.Reject("UnsupportedCommand"):FrameworkResult.Ok());if(!execution.Success)return execution;}
            state.Revision++;document.Instances[document.Instances.IndexOf(current)]=state;return Complete(receipt!,state,command.Kind.ToString());
        }
        private FrameworkResult? Begin(string id,string payload,out OperationReceipt? receipt)
        {
            receipt=null;if(string.IsNullOrWhiteSpace(id))return FrameworkResult.Reject("InvalidOperationId");
            string hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            receipt=document.Operations.FirstOrDefault(x=>x.Id==id);
            if(receipt!=null)return receipt.Fingerprint!=hash?FrameworkResult.Reject("OperationConflict"):receipt.Result?.Success==true?receipt.Result:null;
            receipt=new OperationReceipt {Id=id,Fingerprint=hash};document.Operations.Add(receipt);return null;
        }
        private FrameworkResult Complete(OperationReceipt receipt,StructureState state,string kind)
        {
            var result=FrameworkResult.Ok(state.Id,state.Revision);receipt.Result=result;
            bool previous=DeferCommands;DeferCommands=true;
            try
            {
                foreach(var handler in Changed?.GetInvocationList()??Array.Empty<Delegate>())try {((Action<StructureEvent>)handler)(new(state.Id,kind,state.Revision));}catch(Exception e){try{CallbackError?.Invoke(e);}catch(Exception){}}
            }
            finally {DeferCommands=previous;}
            if(!previous)FlushCommands();
            return result;
        }
        private string? CoreRetirementGuard(StructureState state)
        {
            var stores=document.Resources.Stores.Where(s=>s.Owner.InstanceId==state.Id).ToArray();
            if(stores.Any(s=>s.Balances.Any(b=>b.Amount>0)||s.Reservations.Count>0))return "StoredResourcesRemain";
            var ids=stores.Select(s=>s.Id).ToHashSet(StringComparer.Ordinal);
            if(document.Resources.Transactions.Any(t=>t.StartedCommit&&!t.Committed&&t.Legs.Any(l=>ids.Contains(l.ProviderId))))return "TransactionPending";
            if(document.Transport.Tickets.Any(t=>t.Status=="Reserved"&&(t.Request.Source.InstanceId==state.Id||t.Request.Target.InstanceId==state.Id)))return "ActiveTransportRemain";
            if(document.Instances.Where(s=>s.Id!=state.Id&&s.Lifecycle!=StructureLifecycle.Retired).SelectMany(s=>s.Nodes).Any(n=>n.Spatial.Mode==SpatialMode.NodeRelative&&n.Spatial.Parent.InstanceId==state.Id))return "DependentNodesRemain";
            bool owned=state.Nodes.Any(n=>n.Bindings.Any(b=>b.Ownership!=BindingOwnership.Borrowed)||(n.Spatial.Ownership!=BindingOwnership.Borrowed&&n.Spatial.Mode is SpatialMode.PhysicalGroup or SpatialMode.Custom));
            return owned&&(RetirementGuard==null||CommandExecutor==null)?"RetirementBackendRequired":null;
        }
    }
}
