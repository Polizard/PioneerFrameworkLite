using System;
using System.Collections.Generic;

namespace PioneerFramework.Megastructures
{
    public enum StructureLifecycle { Created, Active, Suspended, Decommissioning, Retired }
    public enum StructureCommandKind { Activate, Suspend, BeginRetirement, FinishRetirement, ChangeOwner, BindNode, SetNodeEnabled, AddCapability, Connect, Disconnect, Maneuver, AddNode, RemoveNode, AttachBackend, SupplyWork, SetCapabilityEnabled, UpdateCapability, SetCombatEnabled, SetAccessPolicy }
    public enum SpatialMode { FrameFixed, BodyAnchored, PhysicalGroup, NodeRelative, Custom }
    public enum BindingOwnership { Borrowed, Owned, Projection }
    public readonly record struct NodeAddress(string InstanceId, string NodeId);
    public sealed record FrameworkResult(bool Success, string Code, string Detail="", string InstanceId="", long Revision=0)
    {
        public static FrameworkResult Ok(string id="",long revision=0)=>new(true,"Ok","",id,revision);
        public static FrameworkResult Reject(string code,string detail="")=>new(false,code,detail);
    }
    public sealed class StructureDefinition
    {
        public StructureAccessPolicy Access { get; set; }=new();
        public string Id { get; set; }="";
        public List<NodeTemplate> Nodes { get; set; }=new();
        public List<NodeConnection> Connections { get; set; }=new();
        public List<CapabilityState> Capabilities { get; set; }=new();
    }
    public sealed class NodeTemplate
    {
        public StructureAccessPolicy Access { get; set; }=new();
        public string Id { get; set; }="";
        public SpatialBinding Spatial { get; set; }=new();
        public List<CapabilityState> Capabilities { get; set; }=new();
    }
    public sealed class SpatialBinding
    {
        public SpatialMode Mode { get; set; }
        public int FrameId { get; set; }=-1;
        public int BodyId { get; set; }=-1;
        public int GroupId { get; set; }=-1;
        public NodeAddress Parent { get; set; }
        public DVector3 Offset { get; set; }
        public DVector3 Velocity { get; set; }
        public DVector3 AngularVelocity { get; set; }
        public RotationData Rotation { get; set; }=RotationData.Identity;
        public BindingOwnership Ownership { get; set; }=BindingOwnership.Borrowed;
        public double Epoch { get; set; }
        public string ProviderId { get; set; }="";
        public string ProviderPayload { get; set; }="{}";
    }
    public sealed class BackendBinding
    {
        public int LinkedEntityId { get; set; }=-1;
        public string Kind { get; set; }="";
        public string Id { get; set; }="";
        public BindingOwnership Ownership { get; set; }
    }
    public sealed class CapabilityState
    {
        public string Id { get; set; }="";
        public string HandlerId { get; set; }="";
        public int Version { get; set; }=1;
        public string Payload { get; set; }="{}";
        public long LastTick { get; set; }
        public bool Enabled { get; set; }=true;
        public string BlockedReason { get; set; }="";
    }
    public sealed class NodeState
    {
        public StructureAccessPolicy Access { get; set; }=new();
        public string Id { get; set; }="";
        public bool Enabled { get; set; }=true;
        public string BlockedReason { get; set; }="";
        public SpatialBinding Spatial { get; set; }=new();
        public List<BackendBinding> Bindings { get; set; }=new();
        public List<CapabilityState> Capabilities { get; set; }=new();
    }
    public sealed class StructureState
    {
        public StructureAccessPolicy Access { get; set; }=new();
        public string Id { get; set; }="";
        public string DefinitionId { get; set; }="";
        public int OwnerFactionId { get; set; }=-1;
        public long Revision { get; set; }
        public StructureLifecycle Lifecycle { get; set; }
        public string BlockedReason { get; set; }="";
        public List<NodeState> Nodes { get; set; }=new();
        public List<CapabilityState> Capabilities { get; set; }=new();
        public List<NodeConnection> Connections { get; set; }=new();
    }
    public sealed class NodeConnection
    {
        public string Id { get; set; }="";
        public string SourceNode { get; set; }="";
        public string TargetNode { get; set; }="";
        public string ResourceId { get; set; }="";
        public double CapacityPerStep { get; set; }
        public bool Enabled { get; set; }=true;
    }
    public sealed class CreateStructureRequest
    {
        public string OperationId { get; set; }="";
        public string InstanceId { get; set; }="";
        public string DefinitionId { get; set; }="";
        public int OwnerFactionId { get; set; }=-1;
        public Dictionary<string,SpatialBinding> SpatialOverrides { get; set; }=new();
    }
    public sealed class StructureCommand
    {
        public StructureAccessPolicy? Access { get; set; }
        public string OperationId { get; set; }="";
        public string InstanceId { get; set; }="";
        public string NodeId { get; set; }="";
        public StructureCommandKind Kind { get; set; }
        public int ActorFactionId { get; set; }=-1;
        public long ExpectedRevision { get; set; }=-1;
        public int NewOwnerFactionId { get; set; }=-1;
        public bool Enabled { get; set; }=true;
        public SpatialBinding? Spatial { get; set; }
        public CapabilityState? Capability { get; set; }
        public NodeConnection? Connection { get; set; }
        public string ConnectionId { get; set; }="";
        public DVector3 DeltaV { get; set; }
        public NodeTemplate? NodeTemplate { get; set; }
        public BackendBinding? Backend { get; set; }
        public string CapabilityId { get; set; }="";
        public decimal WorkAmount { get; set; }
    }
    public sealed class OperationReceipt
    {
        public string Id { get; set; }="";
        public string Fingerprint { get; set; }="";
        public FrameworkResult? Result { get; set; }
    }
    public sealed class FrameworkDocument
    {
        public int SchemaVersion { get; set; }=1;
        public List<StructureState> Instances { get; set; }=new();
        public List<OperationReceipt> Operations { get; set; }=new();
        public ResourceDocument Resources { get; set; }=new();
        public TransportDocument Transport { get; set; }=new();
    }
    public sealed record StructureEvent(string InstanceId,string Kind,long Revision);
}
