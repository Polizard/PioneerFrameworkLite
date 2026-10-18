using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace PioneerFramework.Megastructures
{
    public sealed class MegastructureDef:Def
    {
        public List<MegastructureNodeConfig> nodes=new();
        public bool referenceOnly;
        public string backendProviderId="";
        public MegastructureBackendProperties? backendProperties;
        public List<MegastructureCapabilityConfig> capabilities=new();
        public AccessPolicyConfig access=new();
        public List<NodeConnectionConfig> connections=new();
        public StructureDefinition ToDefinition()=>new() {Id=defName,BackendProviderId=backendProviderId,Access=access.ToPolicy(),Nodes=nodes.Select(n=>n.ToTemplate()).ToList(),Connections=connections.Select(c=>c.ToConnection()).ToList(),Capabilities=capabilities.Select(c=>c.ToState()).ToList()};
        public override IEnumerable<string> ConfigErrors()
        {
            foreach(string error in base.ConfigErrors())yield return error;
            if(nodes.Count==0)yield return "A megastructure requires at least one node.";
            if(nodes.Any(n=>string.IsNullOrWhiteSpace(n.key))||nodes.Select(n=>n.key).Distinct().Count()!=nodes.Count)yield return "Node keys must be nonempty and unique.";
            foreach(var node in nodes)foreach(var capability in node.capabilities)
                if(string.IsNullOrWhiteSpace(capability.key)||string.IsNullOrWhiteSpace(capability.handlerId))yield return "Capabilities require stable keys and handler IDs.";
        }
    }
    public abstract class MegastructureBackendProperties
    {
        public abstract string Encode();
    }
    public sealed class MegastructureNodeConfig
    {
        public string key="node";
        public SpatialMode spatialMode;
        public int bodyId=-1;
        public int frameId=-1;
        public string parentNode="";
        public Vector3Data offset=new();
        public List<MegastructureCapabilityConfig> capabilities=new();
        public AccessPolicyConfig access=new();
        public NodeTemplate ToTemplate()=>new() {Id=key,Access=access.ToPolicy(),Spatial=new SpatialBinding {Mode=spatialMode,BodyId=bodyId,FrameId=frameId,Parent=new("",parentNode),Offset=offset.ToVector()},Capabilities=capabilities.Select(c=>c.ToState()).ToList()};
    }
    public sealed class AccessPolicyConfig
    {
        public bool inherit=true;public List<PermissionGrantConfig> grants=new();
        public StructureAccessPolicy ToPolicy()=>new() {Inherit=inherit,Grants=grants.Select(g=>new PermissionGrant {FactionId=g.factionId,Permissions=g.permissions.ToList()}).ToList()};
    }
    public sealed class PermissionGrantConfig {public int factionId=-1;public List<StructurePermission> permissions=new();}
    public sealed class NodeConnectionConfig
    {
        public string key="";public string sourceNode="";public string targetNode="";public string resourceId="";public double capacityPerStep=1;
        public NodeConnection ToConnection()=>new() {Id=key,SourceNode=sourceNode,TargetNode=targetNode,ResourceId=resourceId,CapacityPerStep=capacityPerStep};
    }
    public sealed class Vector3Data
    {public double x;public double y;public double z;public DVector3 ToVector()=>new(x,y,z);}
    public sealed class MegastructureCapabilityConfig
    {
        public string key="";
        public string handlerId="";
        public int version=1;
        public string payload="{}";
        public MegastructureCapabilityProperties? properties;
        public CapabilityState ToState()=>new() {Id=key,HandlerId=handlerId,Version=version,Payload=properties?.Encode()??payload};
    }
    public abstract class MegastructureCapabilityProperties {public abstract string Encode();}
    public sealed class StorageCapabilityProperties:MegastructureCapabilityProperties
    {
        public List<ResourceSpecificationConfig> resources=new();
        public override string Encode()=>MegastructureCodec.Encode(new StorageCapabilityData {Resources=resources.Select(r=>new ResourceSpecification {Id=r.resourceId,Unit=r.unit,IsItem=r.isItem,Capacity=r.capacity,Initial=r.initial}).ToList()});
    }
    public sealed class ResourceSpecificationConfig
    {public string resourceId="";public string unit="";public bool isItem;public int capacity;public int initial;}
    public static class MegastructureRegistry
    {
        public static MegastructureCapabilityRegistry Capabilities { get; }=Create();
        private static readonly Dictionary<string,Func<ISpatialBindingProvider>> spatialFactories=new(StringComparer.Ordinal);
        private static readonly Dictionary<string,Func<IStructureBackendProvider>> backendFactories=new(StringComparer.Ordinal);
        internal static IEnumerable<Func<IStructureBackendProvider>> BackendFactories=>backendFactories.Values;
        public static FrameworkResult RegisterBackendProvider(string id,Func<IStructureBackendProvider> factory)
        {
            if(Capabilities.IsSealed)return FrameworkResult.Reject("RegistrySealed");
            if(string.IsNullOrWhiteSpace(id)||factory==null)return FrameworkResult.Reject("InvalidBackendProvider");
            return backendFactories.TryAdd(id,factory)?FrameworkResult.Ok():FrameworkResult.Reject("DuplicateBackendProvider");
        }
        internal static IEnumerable<Func<ISpatialBindingProvider>> SpatialFactories=>spatialFactories.Values;
        private static MegastructureCapabilityRegistry Create(){var result=new MegastructureCapabilityRegistry();StandardCapabilities.Register(result);SurfaceModules.Register(result);return result;}
        public static FrameworkResult RegisterCapability(CapabilityDescriptor descriptor)=>Capabilities.Register(descriptor);
        public static FrameworkResult RegisterSpatialProvider(string id,Func<ISpatialBindingProvider> factory)
        {
            if(Capabilities.IsSealed)return FrameworkResult.Reject("RegistrySealed");
            if(string.IsNullOrWhiteSpace(id)||factory==null)return FrameworkResult.Reject("InvalidSpatialProvider");
            return spatialFactories.TryAdd(id,factory)?FrameworkResult.Ok():FrameworkResult.Reject("DuplicateSpatialProvider");
        }
    }
}
