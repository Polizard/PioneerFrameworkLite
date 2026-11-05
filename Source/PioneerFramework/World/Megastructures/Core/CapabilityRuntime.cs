using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Megastructures
{
    public interface IStructureCapability
    {
        FrameworkResult Validate(string payload);
        string Advance(CapabilityContext context,string payload,long elapsedTicks);
    }
    public sealed class CapabilityContext
    {
        public NodeAddress Address { get; }
        public string CapabilityId { get; }
        public long Tick { get; }
        public IServiceProvider? Services { get; }
        private readonly MegastructureRuntime runtime;
        internal CapabilityContext(MegastructureRuntime runtime,NodeAddress address,string capabilityId,long tick,IServiceProvider? services)
        {this.runtime=runtime;Address=address;CapabilityId=capabilityId;Tick=tick;Services=services;}
        public StructureState? GetSnapshot()=>runtime.GetSnapshot(Address.InstanceId);
        public FrameworkResult Submit(StructureCommand command)=>runtime.Execute(command);
        public FrameworkResult? GetOperationResult(string id)=>runtime.GetOperationResult(id);
        public FrameworkResult CommitStage(string id,ConstructionStage stage,StructureResourceService resources)
        {
            var pending=MegastructureCodec.Clone(stage);var instance=runtime.GetSnapshot(Address.InstanceId)!;
            for(int index=0;index<pending.Actions.Count;index++)
            {
                var command=pending.Actions[index];
                if(!string.IsNullOrEmpty(command.InstanceId)&&command.InstanceId!=Address.InstanceId)return FrameworkResult.Reject("CrossInstanceStageAction");
                command.InstanceId=Address.InstanceId;command.ActorFactionId=instance.OwnerFactionId;
                command.OperationId=id+"/action/"+index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            foreach(var cost in pending.Costs)cost.ProviderId=cost.ProviderId.Replace("$instance",Address.InstanceId,StringComparison.Ordinal).Replace("$node",Address.NodeId,StringComparison.Ordinal);
            return runtime.CommitStage(id,pending,resources);
        }
    }
    public sealed class CapabilityDescriptor
    {
        public string HandlerId { get; }
        public Func<IStructureCapability> Factory { get; }
        public int StateVersion { get; }
        public int ApiMajor { get; init; }=1;
        public int IntervalTicks { get; init; }=250;
        public bool AllowInstance { get; init; }=true;
        public bool AllowNode { get; init; }=true;
        public string[] Requires { get; }
        public string[] Conflicts { get; init; }=Array.Empty<string>();
        public Dictionary<int,Func<string,string>> Migrations { get; }=new();
        public CapabilityDescriptor(string handlerId,Func<IStructureCapability> factory,int version=1,string[]? requires=null)
        {HandlerId=handlerId;Factory=factory;StateVersion=version;Requires=requires??Array.Empty<string>();}
    }
    public sealed class MegastructureCapabilityRegistry
    {
        private readonly Dictionary<string,CapabilityDescriptor> descriptors=new(StringComparer.Ordinal);
        public bool IsSealed { get; private set; }
        public void Seal()=>IsSealed=true;
        public FrameworkResult Register(CapabilityDescriptor descriptor)
        {
            if(IsSealed)return FrameworkResult.Reject("RegistrySealed");
            if(string.IsNullOrWhiteSpace(descriptor.HandlerId)||descriptor.Factory==null||descriptor.StateVersion<1||descriptor.IntervalTicks<1||descriptor.ApiMajor!=1)return FrameworkResult.Reject("InvalidHandler");
            if(!descriptors.TryAdd(descriptor.HandlerId,descriptor))return FrameworkResult.Reject("DuplicateHandler",descriptor.HandlerId);
            return FrameworkResult.Ok();
        }
        public CapabilityDescriptor? Find(string handlerId)=>descriptors.TryGetValue(handlerId,out var result)?result:null;
        public FrameworkResult Validate(IEnumerable<CapabilityState> values,bool instance=false)
        {
            var states=values.Where(c=>c.Enabled).ToArray();
            if(states.Any(c=>string.IsNullOrWhiteSpace(c.Id))||states.Select(c=>c.Id).Distinct().Count()!=states.Length)return FrameworkResult.Reject("DuplicateCapability");
            var ids=states.Select(c=>c.HandlerId).ToHashSet(StringComparer.Ordinal);
            foreach(var state in states)
            {
                var d=Find(state.HandlerId);if(d==null)return FrameworkResult.Reject("CapabilityMissing",state.HandlerId);
                if(instance?!d.AllowInstance:!d.AllowNode)return FrameworkResult.Reject("CapabilityScope");
                if(d.Requires.Any(r=>!ids.Contains(r)))return FrameworkResult.Reject("CapabilityDependency");
                if(d.Conflicts.Any(ids.Contains))return FrameworkResult.Reject("CapabilityConflict");
            }
            var visiting=new HashSet<string>();var done=new HashSet<string>();
            bool Visit(string id){if(done.Contains(id))return true;if(!visiting.Add(id))return false;foreach(string dependency in descriptors[id].Requires)if(!Visit(dependency))return false;visiting.Remove(id);done.Add(id);return true;}
            return ids.All(Visit)?FrameworkResult.Ok():FrameworkResult.Reject("CapabilityCycle");
        }
        internal int Rank(string id)
        {
            var seen=new HashSet<string>();
            int Visit(string key){if(!seen.Add(key)||!descriptors.TryGetValue(key,out var value))return 0;return value.Requires.Length==0?0:1+value.Requires.Max(Visit);}
            return Visit(id);
        }
        internal FrameworkResult ValidateFor(CapabilityState target,CapabilityState[] all,bool root)
        {
            var closure=new HashSet<string>();
            void Visit(string id){if(!closure.Add(id))return;var d=Find(id);if(d!=null)foreach(var dependency in d.Requires)Visit(dependency);}
            Visit(target.HandlerId);
            var descriptor=Find(target.HandlerId);
            if(descriptor!=null&&descriptor.Conflicts.Any(id=>all.Any(c=>c.Enabled&&c.HandlerId==id)))return FrameworkResult.Reject("CapabilityConflict");
            return Validate(all.Where(c=>closure.Contains(c.HandlerId)),root);
        }
        public FrameworkResult Admit(StructureState instance,string nodeId,CapabilityState capability)
        {
            var all=string.IsNullOrEmpty(nodeId)?instance.Capabilities:instance.Nodes.FirstOrDefault(n=>n.Id==nodeId)?.Capabilities;
            if(all==null||!capability.Enabled)return FrameworkResult.Reject("CapabilityUnavailable");
            var valid=ValidateFor(capability,all.ToArray(),string.IsNullOrEmpty(nodeId));if(!valid.Success)return valid;
            var descriptor=Find(capability.HandlerId);if(descriptor==null)return FrameworkResult.Reject("CapabilityMissing");
            if(capability.Version!=descriptor.StateVersion)return FrameworkResult.Reject("CapabilityVersion");
            if(capability.BlockedReason is "CapabilityException" or "StateMigrationFailed")return FrameworkResult.Reject(capability.BlockedReason);
            try{return descriptor.Factory().Validate(capability.Payload);}catch(Exception){return FrameworkResult.Reject("InvalidPayload");}
        }
    }
    public sealed class CapabilityScheduler
    {
        private readonly MegastructureRuntime runtime;
        private readonly MegastructureCapabilityRegistry registry;
        private readonly Dictionary<string,IStructureCapability> handlers=new(StringComparer.Ordinal);
        public IServiceProvider? Services { get; set; }
        public Action<string,Exception>? Error { get; set; }
        public int LastStepCount { get; private set; }
        public CapabilityScheduler(MegastructureRuntime runtime,MegastructureCapabilityRegistry registry)
        {
            this.runtime=runtime;this.registry=registry;
            runtime.CapabilityGuard=registry.Admit;
            runtime.Changed+=change=>{foreach(string key in handlers.Keys.Where(k=>k.StartsWith(change.InstanceId+"/",StringComparison.Ordinal)).ToArray())handlers.Remove(key);};
        }
        public void Tick(long now,int budget=4096)
        {
            runtime.CurrentTick=now;LastStepCount=0;
            var due=new PriorityQueue<(StructureState Instance,string Node,CapabilityState State,CapabilityDescriptor Descriptor,string Key),(long,int,string)>();
            foreach(var instance in runtime.LiveInstances.OrderBy(x=>x.Id,StringComparer.Ordinal))
            {
                bool active=instance.Lifecycle==StructureLifecycle.Active&&string.IsNullOrEmpty(instance.BlockedReason);
                Add(instance,"",instance.Capabilities,active,true);
                foreach(var node in instance.Nodes)Add(instance,node.Id,node.Capabilities,active&&node.Enabled&&string.IsNullOrEmpty(node.BlockedReason),false);
            }
            void Add(StructureState instance,string node,IEnumerable<CapabilityState> values,bool active,bool root)
            {
                var states=values.ToArray();
                foreach(var state in states)
                {
                    if(!active||!state.Enabled){state.LastTick=now;continue;}
                    var descriptor=registry.Find(state.HandlerId);
                    if(descriptor==null){state.BlockedReason="CapabilityMissing";continue;}
                    var valid=registry.ValidateFor(state,states,root);
                    if(!valid.Success){state.BlockedReason=valid.Code;continue;}
                    if(state.BlockedReason is "CapabilityException" or "StateMigrationFailed")continue;
                    string key=instance.Id+"/"+node+"/"+state.Id;
                    if(!handlers.TryGetValue(key,out var handler))
                    {
                        try
                        {
                            handler=descriptor.Factory();string payload=state.Payload;int version=state.Version;
                            while(version<descriptor.StateVersion){if(!descriptor.Migrations.TryGetValue(version,out var migrate))throw new InvalidOperationException("Missing state migration.");payload=migrate(payload);version++;}
                            if(version!=descriptor.StateVersion){state.BlockedReason="CapabilityVersion";continue;}
                            var validation=handler.Validate(payload);if(!validation.Success){state.BlockedReason=validation.Code;continue;}
                            state.Payload=payload;state.Version=version;handlers.Add(key,handler);
                        }
                        catch(Exception e){state.BlockedReason="StateMigrationFailed";Report(key,e);continue;}
                    }
                    state.BlockedReason="";
                    if(state.LastTick>now){state.BlockedReason="FutureCapabilityClock";continue;}
                    if(now-state.LastTick>=descriptor.IntervalTicks)due.Enqueue((instance,node,state,descriptor,key),(state.LastTick+descriptor.IntervalTicks,registry.Rank(state.HandlerId),key));
                }
            }
            runtime.DeferCommands=true;
            try
            {
                while(due.TryDequeue(out var work,out var priority)&&LastStepCount<budget)
                {
                    var peers=string.IsNullOrEmpty(work.Node)?work.Instance.Capabilities:work.Instance.Nodes.First(n=>n.Id==work.Node).Capabilities;
                    if(work.Descriptor.Requires.Any(id=>!peers.Any(c=>c.HandlerId==id&&c.Enabled&&string.IsNullOrEmpty(c.BlockedReason)))){work.State.BlockedReason="CapabilityDependencyBlocked";continue;}
                    try
                    {
                        string payload=handlers[work.Key].Advance(new CapabilityContext(runtime,new(work.Instance.Id,work.Node),work.State.Id,priority.Item1,Services),work.State.Payload,work.Descriptor.IntervalTicks);
                        if(payload.Length>1_048_576)throw new InvalidOperationException("Capability payload exceeds 1 MiB.");
                        work.State.Payload=payload;work.State.LastTick=priority.Item1;LastStepCount++;
                        if(now-work.State.LastTick>=work.Descriptor.IntervalTicks)due.Enqueue(work,(work.State.LastTick+work.Descriptor.IntervalTicks,registry.Rank(work.State.HandlerId),work.Key));
                    }
                    catch(Exception e){work.State.BlockedReason="CapabilityException";Report(work.Key,e);}
                }
            }
            finally {runtime.DeferCommands=false;runtime.FlushCommands();}
        }
        private void Report(string id,Exception error){try{Error?.Invoke(id,error);}catch(Exception){}}
    }
}
