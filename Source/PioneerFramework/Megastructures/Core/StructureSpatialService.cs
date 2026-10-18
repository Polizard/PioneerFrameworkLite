using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Megastructures
{
    public readonly record struct SpatialState(int FrameId,SystemVec3 PositionSystem,SystemVec3 VelocitySystem,RotationData Rotation,DVector3 AngularVelocity,double Tick)
    {
        public SpatialState(int frameId,DVector3 position,DVector3 velocity,RotationData rotation,DVector3 angularVelocity,double tick):this(frameId,new SystemVec3(position),new SystemVec3(velocity),rotation,angularVelocity,tick) { }
    }
    public sealed record SpatialEvaluation(bool Success,string Code,SpatialState State)
    {public static SpatialEvaluation Reject(string code)=>new(false,code,default);}
    public interface IStructureSpatialHost
    {
        bool TryGetBody(int id,double tick,out SpatialState state);
        bool TryGetGroup(int id,double tick,out SpatialState state);
        FrameworkResult Maneuver(int groupId,DVector3 deltaV);
    }
    public interface ISpatialBindingProvider
    {
        string Id { get; }
        FrameworkResult Validate(SpatialBinding binding);
        SpatialEvaluation Evaluate(NodeAddress address,SpatialBinding binding,double tick);
        FrameworkResult CanRelease(NodeAddress address,SpatialBinding binding);
        FrameworkResult Release(NodeAddress address,SpatialBinding binding,string operationId);
    }
    public sealed class StructureSpatialService
    {
        private readonly MegastructureRuntime runtime;
        private readonly IStructureSpatialHost host;
        private readonly Dictionary<string,ISpatialBindingProvider> providers=new(StringComparer.Ordinal);
        private readonly Func<double> clock;
        public double CurrentTime=>clock();
        public StructureSpatialService(MegastructureRuntime runtime,IStructureSpatialHost host,Func<double>? clock=null){this.runtime=runtime;this.host=host;this.clock=clock??(()=>runtime.CurrentTick);}
        public FrameworkResult RegisterProvider(ISpatialBindingProvider provider)=>providers.TryAdd(provider.Id,provider)?FrameworkResult.Ok():FrameworkResult.Reject("DuplicateSpatialProvider");
        public ISpatialBindingProvider? FindProvider(string id)=>providers.TryGetValue(id,out var provider)?provider:null;
        public string? Validate(FrameworkDocument document)
        {
            var nodes=new Dictionary<NodeAddress,NodeState>();var groups=new HashSet<int>();
            foreach(var instance in document.Instances.Where(x=>x.Lifecycle!=StructureLifecycle.Retired))foreach(var node in instance.Nodes)
            {
                nodes.Add(new(instance.Id,node.Id),node);var b=node.Spatial;
                if(b==null||!Enum.IsDefined(b.Mode)||!b.Offset.IsFinite()||!b.Velocity.IsFinite()||!b.AngularVelocity.IsFinite()||!double.IsFinite(b.Epoch))return "InvalidSpatialBinding";
                var q=b.Rotation;double norm=q.X*q.X+q.Y*q.Y+q.Z*q.Z+q.W*q.W;
                if(!double.IsFinite(norm)||Math.Abs(norm-1)>1e-6)return "InvalidRotation";
                if(b.Mode==SpatialMode.PhysicalGroup&&(b.GroupId<=0||!groups.Add(b.GroupId)))return "DuplicatePhysicalAuthority";
                if(b.Mode==SpatialMode.BodyAnchored&&b.BodyId<0)return "InvalidBody";
                if(b.Mode==SpatialMode.Custom&&providers.TryGetValue(b.ProviderId,out var custom))
                {try{var valid=custom.Validate(MegastructureCodec.Clone(b));if(!valid.Success)return valid.Code;}catch(Exception){return "SpatialProviderError";}}
            }
            var complete=new HashSet<NodeAddress>();var visiting=new HashSet<NodeAddress>();
            string? Visit(NodeAddress address)
            {
                if(complete.Contains(address))return null;if(!visiting.Add(address))return "SpatialCycle";
                var b=nodes[address].Spatial;
                if(b.Mode==SpatialMode.NodeRelative)
                {if(!nodes.ContainsKey(b.Parent))return "AnchorMissing";string? error=Visit(b.Parent);if(error!=null)return error;}
                visiting.Remove(address);complete.Add(address);return null;
            }
            foreach(var address in nodes.Keys){string? error=Visit(address);if(error!=null)return error;}
            return null;
        }
        public SpatialEvaluation Evaluate(NodeAddress address,double tick)=>Evaluate(address,tick,new HashSet<NodeAddress>());
        private SpatialEvaluation Evaluate(NodeAddress address,double tick,HashSet<NodeAddress> visited)
        {
            if(!double.IsFinite(tick))return SpatialEvaluation.Reject("InvalidTime");
            if(!visited.Add(address))return SpatialEvaluation.Reject("SpatialCycle");
            var instance=runtime.Find(address.InstanceId);var node=instance?.Nodes.FirstOrDefault(x=>x.Id==address.NodeId);
            if(node==null||instance!.Lifecycle==StructureLifecycle.Retired)return SpatialEvaluation.Reject("NodeMissing");
            if(!node.Enabled)return SpatialEvaluation.Reject("NodeDisabled");
            var b=node.Spatial;SpatialState parent;
            switch(b.Mode)
            {
                case SpatialMode.PhysicalGroup:
                    if(!host.TryGetGroup(b.GroupId,tick,out parent))return SpatialEvaluation.Reject("GroupMissing");
                    return Math.Abs(parent.Tick-tick)<1e-6?new(true,"Ok",parent):SpatialEvaluation.Reject("SpatialSampleUnavailable");
                case SpatialMode.Custom:
                    if(!providers.TryGetValue(b.ProviderId,out var custom))return SpatialEvaluation.Reject("SpatialProviderMissing");
                    try{return custom.Evaluate(address,MegastructureCodec.Clone(b),tick);}catch(Exception){return SpatialEvaluation.Reject("SpatialProviderError");}
                case SpatialMode.BodyAnchored:if(!host.TryGetBody(b.BodyId,tick,out parent))return SpatialEvaluation.Reject("BodyMissing");break;
                case SpatialMode.NodeRelative:
                    var result=Evaluate(b.Parent,tick,visited);if(!result.Success)return result;parent=result.State;break;
                default:parent=new(b.FrameId,DVector3.zero,DVector3.zero,RotationData.Identity,DVector3.zero,tick);break;
            }
            double delta=tick-b.Epoch;
            var offset=parent.Rotation.Rotate(b.Offset+b.Velocity*delta);
            var localRotation=b.AngularVelocity.sqrMagnitude>0?RotationData.AxisAngle(b.AngularVelocity,b.AngularVelocity.magnitude*delta)*b.Rotation:b.Rotation;
            return new(true,"Ok",new(parent.FrameId,parent.PositionSystem.v+offset,parent.VelocitySystem.v+DVector3.Cross(parent.AngularVelocity,offset)+parent.Rotation.Rotate(b.Velocity),parent.Rotation*localRotation,parent.AngularVelocity+parent.Rotation.Rotate(b.AngularVelocity),tick));
        }
        public FrameworkResult Maneuver(StructureState instance,string nodeId,DVector3 deltaV)
        {
            var node=instance.Nodes.FirstOrDefault(n=>n.Id==nodeId);
            if(instance.Lifecycle!=StructureLifecycle.Active||node==null||!node.Enabled)return FrameworkResult.Reject("NodeUnavailable");
            var capability=node.Capabilities.FirstOrDefault(c=>c.HandlerId=="pfl.maneuver"&&c.Enabled);
            if(capability==null)return FrameworkResult.Reject("CapabilityMissing");
            var admission=runtime.CheckCapability(instance,nodeId,capability);if(!admission.Success)return admission;
            if(node.Spatial.Mode!=SpatialMode.PhysicalGroup||!deltaV.IsFinite()||deltaV.sqrMagnitude<=0)return FrameworkResult.Reject("InvalidManeuver");
            return host.Maneuver(node.Spatial.GroupId,deltaV);
        }
    }
}
