using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Megastructures
{
    public enum StructurePermission { View,Operate,Construct,Load,Maneuver,Bind,Dismantle }
    public sealed class PermissionGrant
    {
        public int FactionId { get; set; }=-1;
        public List<StructurePermission> Permissions { get; set; }=new();
    }
    public sealed class StructureAccessPolicy
    {
        public bool Inherit { get; set; }=true;
        public List<PermissionGrant> Grants { get; set; }=new();
    }
    public static class StructurePermissions
    {
        public static bool Allows(StructureState instance,string nodeId,int actor,StructurePermission permission)
        {
            if(actor==instance.OwnerFactionId)return true;
            var node=instance.Nodes.FirstOrDefault(n=>n.Id==nodeId);
            bool Granted(StructureAccessPolicy policy)=>policy.Grants.Any(g=>(g.FactionId<0||g.FactionId==actor)&&g.Permissions.Contains(permission));
            if(node!=null&&Granted(node.Access))return true;
            if(node!=null&&!node.Access.Inherit)return false;
            return Granted(instance.Access)||(instance.Access.Inherit&&instance.OwnerFactionId<0);
        }
        public static StructurePermission For(StructureCommandKind kind)=>kind switch {
            StructureCommandKind.Maneuver=>StructurePermission.Maneuver,
            StructureCommandKind.BindNode or StructureCommandKind.AttachBackend=>StructurePermission.Bind,
            StructureCommandKind.AddNode or StructureCommandKind.AddCapability or StructureCommandKind.SupplyWork or StructureCommandKind.Connect=>StructurePermission.Construct,
            StructureCommandKind.RemoveNode or StructureCommandKind.Disconnect or StructureCommandKind.BeginRetirement or StructureCommandKind.FinishRetirement=>StructurePermission.Dismantle,
            _=>StructurePermission.Operate
        };
        public static bool Allows(StructureState instance,StructureCommand command)
        {
            string node=command.Kind is StructureCommandKind.Activate or StructureCommandKind.Suspend or StructureCommandKind.BeginRetirement or StructureCommandKind.FinishRetirement or StructureCommandKind.ChangeOwner or StructureCommandKind.AddNode?"":command.NodeId;
            if(!Allows(instance,node,command.ActorFactionId,For(command.Kind)))return false;
            if(command.Kind==StructureCommandKind.Connect&&command.Connection!=null)
                return Allows(instance,command.Connection.SourceNode,command.ActorFactionId,StructurePermission.Construct)&&Allows(instance,command.Connection.TargetNode,command.ActorFactionId,StructurePermission.Construct);
            return true;
        }
    }
}
