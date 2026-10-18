using System;
using System.Globalization;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Megastructures
{
    public sealed partial class MegastructureManager
    {
        private SpaceGameComp? subscribed;
        partial void AttachHostEvents()
        {
            var simulation=Current.Game?.GetComponent<SpaceGameComp>();if(ReferenceEquals(simulation,subscribed))return;
            if(subscribed!=null){subscribed.GroupDestroyed-=OnGroupLost;subscribed.GroupLanded-=OnGroupLost;}
            subscribed=simulation;if(simulation!=null){simulation.GroupDestroyed+=OnGroupLost;simulation.GroupLanded+=OnGroupLost;}
        }
        private void OnGroupLost(int groupId)
        {
            foreach(var instance in runtime.LiveInstances)foreach(var node in instance.Nodes)
                if(node.Spatial.Mode==SpatialMode.PhysicalGroup&&node.Spatial.GroupId==groupId){node.BlockedReason="GroupMissing";node.Enabled=false;instance.Revision++;}
        }
        private string? HostMutationGuard(StructureState instance,StructureCommand command)
        {
            if(command.Kind!=StructureCommandKind.RemoveNode)return null;
            var address=new NodeAddress(instance.Id,command.NodeId);
            if(thingStores.Any(s=>s.Owner==address&&(s.Count>0||s.HasReservations)))return "StoredResourcesRemain";
            return null;
        }
        private int ResolveBoundMapId(NodeState node,BackendBinding binding)
        {
            if(binding.LinkedEntityId>=0)return subscribed?.GetEntityById(binding.LinkedEntityId)?.MapId??-1;
            if(!int.TryParse(binding.Id,NumberStyles.Integer,CultureInfo.InvariantCulture,out int mapId))return -1;
            foreach(var capability in node.Capabilities.Where(c=>c.HandlerId=="pfl.map"))
            {
                try
                {
                    var data=MegastructureCodec.Read<MapCapabilityData>(capability.Payload);int slot=data.MapIds.IndexOf(mapId);
                    if(slot>=0&&data.EntityIds.TryGetValue(slot,out int entityId))return subscribed?.GetEntityById(entityId)?.MapId??mapId;
                }
                catch(System.Text.Json.JsonException){}
            }
            return mapId;
        }
        partial void ReconcileHostBindings()
        {
            AttachHostEvents();
            foreach(var instance in runtime.LiveInstances)
            {
                if(instance.Lifecycle==StructureLifecycle.Retired)continue;
                instance.BlockedReason=runtime.HasDefinition(instance.DefinitionId)||instance.DefinitionId=="PF_ExistingRing"?"":"DefinitionMissing";
                foreach(var node in instance.Nodes)
                {
                    if(node.Spatial.Mode==SpatialMode.PhysicalGroup&&subscribed?.GetGroupById(node.Spatial.GroupId)==null){node.BlockedReason="GroupMissing";node.Enabled=false;}
                    else if(node.Enabled){var result=Spatial.Evaluate(new(instance.Id,node.Id),Spatial.CurrentTime);node.BlockedReason=result.Success?"":result.Code;}
                }
            }
        }
        private partial string? HostRetirementGuard(StructureState instance)
        {
            if(thingStores.Any(s=>s.Owner.InstanceId==instance.Id&&(s.Count>0||s.HasReservations)))return "StoredResourcesRemain";
            if(Endpoints.Tickets.Any(t=>t.Status=="Reserved"&&(t.Request.Source.InstanceId==instance.Id||t.Request.Target.InstanceId==instance.Id)))return "ActiveTransportRemain";
            if(Resources.Stores.Any(s=>s.Owner.InstanceId==instance.Id&&(s.Balances.Any(b=>b.Amount>0)||s.Reservations.Count>0)))return "StoredResourcesRemain";
            foreach(var other in runtime.LiveInstances.Where(s=>s.Id!=instance.Id&&s.Lifecycle!=StructureLifecycle.Retired))
                if(other.Nodes.Any(n=>n.Spatial.Mode==SpatialMode.NodeRelative&&n.Spatial.Parent.InstanceId==instance.Id))return "DependentNodesRemain";
            foreach(var node in instance.Nodes)
            {
                if(node.Spatial.Mode==SpatialMode.Custom&&node.Spatial.Ownership!=BindingOwnership.Borrowed)
                {
                    var provider=Spatial.FindProvider(node.Spatial.ProviderId);if(provider==null)return "SpatialProviderMissing";
                    var result=provider.CanRelease(new(instance.Id,node.Id),MegastructureCodec.Clone(node.Spatial));if(!result.Success)return result.Code;
                }
                foreach(var binding in node.Bindings.Where(b=>b.Ownership==BindingOwnership.Owned&&b.Kind=="map"))
                {
                    int mapId=ResolveBoundMapId(node,binding);var map=Find.Maps?.FirstOrDefault(m=>m.uniqueID==mapId);
                    if(subscribed?.GetAllEntities().Any(e=>e.MapId==mapId&&e.IsMapArchived)==true)return "MapRestoreRequired";
                    if(map!=null&&map.listerThings.AllThings.Count>0)return "MapContentsRemain";
                }
                if(node.Spatial.Mode==SpatialMode.PhysicalGroup&&node.Spatial.Ownership!=BindingOwnership.Borrowed)
                {
                    var group=subscribed?.GetGroupById(node.Spatial.GroupId);if(group==null)continue;
                    if(group.HasActiveBurn||!string.IsNullOrEmpty(group.LandingTaskId))return "GroupBusy";
                    foreach(int entityId in group.GetEntityIds())
                    {
                        int mapId=subscribed!.GetEntityById(entityId)?.MapId??-1;if(mapId<0)continue;
                        bool owned=instance.Nodes.Any(n=>n.Bindings.Any(b=>b.Kind=="map"&&ResolveBoundMapId(n,b)==mapId&&b.Ownership==BindingOwnership.Owned));
                        if(!owned)return "BorrowedMapProtected";
                        var map=Find.Maps?.FirstOrDefault(m=>m.uniqueID==mapId);if(map==null||map.listerThings.AllThings.Count>0)return "MapContentsRemain";
                    }
                }
                if(node.Bindings.Any(b=>b.Ownership==BindingOwnership.Owned&&b.Kind is not ("map" or "ring")))return "UnsupportedOwnedBinding";
                foreach(var binding in node.Bindings.Where(b=>b.Ownership==BindingOwnership.Owned&&b.Kind=="ring"))
                {var ring=RingWorld.RingWorldService.FindById(binding.Id);if(ring!=null&&!RuntimePlanetLayerService.CanRemove(ring,false,out _))return "LayerContentsRemain";}
            }
            return null;
        }
        private FrameworkResult FinishHostRetirement(StructureState instance)
        {
            string? error=HostRetirementGuard(instance);if(error!=null)return FrameworkResult.Reject(error);
            foreach(var node in instance.Nodes)
            {
                if(node.Spatial.Mode==SpatialMode.Custom&&node.Spatial.Ownership!=BindingOwnership.Borrowed)
                {
                    var result=Spatial.FindProvider(node.Spatial.ProviderId)!.Release(new(instance.Id,node.Id),MegastructureCodec.Clone(node.Spatial),"retire/"+instance.Id+"/"+node.Id);
                    if(!result.Success)return result;
                }
                if(node.Spatial.Mode==SpatialMode.PhysicalGroup&&node.Spatial.Ownership!=BindingOwnership.Borrowed)subscribed?.DestroyGroup(node.Spatial.GroupId,"Megastructure retirement");
                foreach(var binding in node.Bindings.Where(b=>b.Ownership==BindingOwnership.Owned))
                {
                    if(binding.Kind=="map")
                    {
                        int mapId=ResolveBoundMapId(node,binding);var map=Find.Maps?.FirstOrDefault(m=>m.uniqueID==mapId);
                        if(map!=null){var parent=map.Parent;Current.Game.DeinitAndRemoveMap(map,false);parent.Destroy();}
                    }
                    else if(binding.Kind=="ring")
                    {var ring=RingWorld.RingWorldService.FindById(binding.Id);if(ring!=null&&!RingWorld.RingWorldService.TryRemove(ring,out string reason))return FrameworkResult.Reject("LayerRemovalFailed",reason);}
                }
            }
            return FrameworkResult.Ok();
        }
        public FrameworkResult ImportRing(RingWorld.RingSurfaceLayer ring,int ownerFactionId)
        {
            EnsureInitialized();const string definitionId="PF_ExistingRing";
            runtime.RegisterDefinition(new StructureDefinition {Id=definitionId,Nodes={new NodeTemplate {Id="facility"}}});
            string id="ring:"+ring.Parameters.RingId;
            var result=runtime.Create(new CreateStructureRequest {OperationId="import/"+id,InstanceId=id,DefinitionId=definitionId,OwnerFactionId=ownerFactionId,SpatialOverrides={ ["facility"]=new SpatialBinding {Mode=SpatialMode.BodyAnchored,BodyId=ring.Parameters.ParentBodyId} }});
            if(!result.Success)return result;
            result=runtime.Execute(new StructureCommand {OperationId="import-binding/"+id,InstanceId=id,ActorFactionId=ownerFactionId,NodeId="facility",Kind=StructureCommandKind.AttachBackend,Backend=new BackendBinding {Kind="ring",Id=ring.Parameters.RingId,Ownership=BindingOwnership.Borrowed}});
            if(!result.Success)return result;
            var mapIds=(Find.Maps??new System.Collections.Generic.List<Map>()).Where(m=>m.Tile.Layer==ring).Select(m=>m.uniqueID).OrderBy(x=>x).ToList();
            var existing=runtime.GetSnapshot(id)!.Nodes[0].Capabilities.FirstOrDefault(c=>c.Id=="existingMaps");
            var maps=new CapabilityState {Id="existingMaps",HandlerId="pfl.map",Payload=MegastructureCodec.Encode(new MapCapabilityData {MapIds=mapIds})};
            return runtime.Execute(new StructureCommand {OperationId="import-maps/"+id+"/"+string.Join("-",mapIds),InstanceId=id,ActorFactionId=ownerFactionId,NodeId="facility",Kind=existing==null?StructureCommandKind.AddCapability:StructureCommandKind.UpdateCapability,CapabilityId="existingMaps",Capability=maps});
        }
    }
}
