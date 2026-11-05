using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using PioneerFramework.Combat;

namespace PioneerFramework.Megastructures
{
    public interface IMapAccessProvider
    {FrameworkResult Access(NodeAddress address,CapabilityState capability,int slot,bool generate,out Map? map);}
    public sealed class StructureMapService
    {
        private readonly MegastructureManager manager;
        private readonly Dictionary<string,IMapAccessProvider> providers=new(StringComparer.Ordinal);
        public StructureMapService(MegastructureManager manager){this.manager=manager;providers.Add("pfl.map",new DefaultMapProvider(manager));}
        public FrameworkResult RegisterProvider(string handlerId,IMapAccessProvider provider)=>providers.TryAdd(handlerId,provider)?FrameworkResult.Ok():FrameworkResult.Reject("DuplicateMapProvider");
        public FrameworkResult Access(NodeAddress address,string capabilityId,int slot,bool generate,int actorFactionId,out Map? map)
        {
            map=null;var instance=manager.GetSnapshot(address.InstanceId);var node=instance?.Nodes.FirstOrDefault(n=>n.Id==address.NodeId);
            if(instance==null||instance.Lifecycle==StructureLifecycle.Retired||node==null||!node.Enabled)return FrameworkResult.Reject("NodeUnavailable");
            if(!StructurePermissions.Allows(instance,address.NodeId,actorFactionId,StructurePermission.View)||(generate&&!StructurePermissions.Allows(instance,address.NodeId,actorFactionId,StructurePermission.Construct)))return FrameworkResult.Reject("PermissionDenied");
            var capability=node.Capabilities.FirstOrDefault(c=>c.Id==capabilityId&&c.Enabled&&string.IsNullOrEmpty(c.BlockedReason));
            if(capability==null||!providers.TryGetValue(capability.HandlerId,out var provider))return FrameworkResult.Reject("MapProviderMissing");
            var admission=manager.Runtime.CheckCapability(instance,address.NodeId,capability);if(!admission.Success)return admission;
            try{return provider.Access(address,capability,slot,generate,out map);}
            catch(Exception error){return FrameworkResult.Reject("MapProviderError",error.Message);}
        }
        private sealed class DefaultMapProvider:IMapAccessProvider
        {
            private readonly MegastructureManager manager;
            public DefaultMapProvider(MegastructureManager manager){this.manager=manager;}
            public FrameworkResult Access(NodeAddress address,CapabilityState capability,int slot,bool generate,out Map? map)
            {
                map=null;var config=MegastructureCodec.Read<MapCapabilityData>(capability.Payload);
                if(slot<0)return FrameworkResult.Reject("InvalidMapSlot");
                if(slot<config.MapIds.Count)
                {
                    var simulation=Current.Game?.GetComponent<SpaceGameComp>();
                    var entity=config.EntityIds.TryGetValue(slot,out int entityId)?simulation?.GetEntityById(entityId):simulation?.GetAllEntities().FirstOrDefault(e=>e.MapId==config.MapIds[slot]);
                    if(entity!=null&&!config.EntityIds.ContainsKey(slot))
                    {
                        config.EntityIds[slot]=entity.EntityId;
                        capability.Payload=MegastructureCodec.Encode(config);int actor=manager.GetSnapshot(address.InstanceId)!.OwnerFactionId;
                        var saved=manager.Execute(new StructureCommand {OperationId="map-entity/"+address.InstanceId+"/"+address.NodeId+"/"+capability.Id+"/"+slot,InstanceId=address.InstanceId,NodeId=address.NodeId,ActorFactionId=actor,Kind=StructureCommandKind.UpdateCapability,CapabilityId=capability.Id,Capability=capability});
                        if(!saved.Success)return saved;
                    }
                    int mapId=entity!=null&&!entity.IsMapArchived?entity.MapId:config.MapIds[slot];
                    map=Find.Maps.FirstOrDefault(m=>m.uniqueID==mapId);if(map!=null)return FrameworkResult.Ok();
                    if(entity?.IsMapArchived!=true)return FrameworkResult.Reject("MapMissing");
                    if(Scribe.mode!=LoadSaveMode.Inactive)return FrameworkResult.Reject("SaveInProgress");
                    if(simulation!.ActiveRestoreOperation?.TargetEntity==entity)return FrameworkResult.Reject("MapRestoring");
                    if(simulation.ActiveRestoreOperation!=null||simulation.ActiveArchiveOperation!=null)return FrameworkResult.Reject("MapArchiveBusy");
                    return FrameworkResult.Reject(simulation.BeginRestoreMap(entity)?"MapRestoring":"MapRestoreFailed");
                }
                if(!generate||!config.AllowGenerate||slot!=config.MapIds.Count)return FrameworkResult.Reject("MapMissing");
                if(!ModsConfig.OdysseyActive)return FrameworkResult.Reject("OdysseyRequired");
                if(Scribe.mode!=LoadSaveMode.Inactive||config.Width<50||config.Height<50||config.Width>400||config.Height>400)return FrameworkResult.Reject("InvalidMapSize");
                Map? created=null;
                try
                {
                    created=SpaceMapGenerator.GeneratePocketMap(config.Width,config.Height,"Megastructure_"+address.InstanceId+"_"+address.NodeId+"_"+slot);
                    if(created==null)return FrameworkResult.Reject("MapGenerationFailed");
                    int actor=manager.GetSnapshot(address.InstanceId)!.OwnerFactionId;
                    config.MapIds.Add(created.uniqueID);capability.Payload=MegastructureCodec.Encode(config);
                    var stage=new ConstructionStage {Actions={
                        new StructureCommand {OperationId="map-bind/"+created.uniqueID,InstanceId=address.InstanceId,NodeId=address.NodeId,ActorFactionId=actor,Kind=StructureCommandKind.AttachBackend,Backend=new BackendBinding {Kind="map",Id=created.uniqueID.ToString(System.Globalization.CultureInfo.InvariantCulture),Ownership=BindingOwnership.Owned}},
                        new StructureCommand {OperationId="map-capability/"+created.uniqueID,InstanceId=address.InstanceId,NodeId=address.NodeId,ActorFactionId=actor,Kind=StructureCommandKind.UpdateCapability,CapabilityId=capability.Id,Capability=capability}
                    }};
                    var update=manager.Runtime.CommitStage("map-create/"+created.uniqueID,stage,manager.Resources);
                    if(!update.Success)throw new InvalidOperationException(update.Code);
                    map=created;return FrameworkResult.Ok();
                }
                catch(Exception e)
                {if(created!=null){var parent=created.Parent;Current.Game.DeinitAndRemoveMap(created,false);parent.Destroy();}return FrameworkResult.Reject("MapGenerationFailed",e.Message);}
            }
        }
    }
    public sealed partial class MegastructureManager
    {
        private FrameworkResult SetHostCombat(StructureState instance,StructureCommand command)
        {
            var node=instance.Nodes.FirstOrDefault(n=>n.Id==command.NodeId);
            var capability=node?.Capabilities.FirstOrDefault(c=>c.HandlerId=="pfl.combat"&&c.Enabled);
            if(node==null||capability==null||node.Spatial.Mode!=SpatialMode.PhysicalGroup)return FrameworkResult.Reject("CapabilityMissing");
            var admission=runtime.CheckCapability(instance,node.Id,capability);if(!admission.Success)return admission;
            var group=subscribed?.GetGroupById(node.Spatial.GroupId);if(group==null||group.FactionId!=instance.OwnerFactionId)return FrameworkResult.Reject("PermissionDenied");
            var grid=Current.Game?.GetComponent<CombatGrid>();if(grid==null)return FrameworkResult.Reject("CombatUnavailable");
            grid.RemoveShip(group);group.IsCollidable=command.Enabled;if(command.Enabled)grid.AddShip(group);return FrameworkResult.Ok();
        }
        public FrameworkResult StartSupportedLanding(NodeAddress address,SpaceEntityGroup incoming,int mapSlot,string mapCapabilityId,int actorFactionId)
        {
            if(incoming==null||incoming.FactionId!=actorFactionId)return FrameworkResult.Reject("PermissionDenied");
            var result=Maps.Access(address,mapCapabilityId,mapSlot,false,actorFactionId,out var map);if(!result.Success)return result;
            var bodies=Current.Game.GetComponent<CelestialBodyManager>();int bodyId=Launch.GetSurfaceState.GetBodyForLayer(bodies,map!.Tile.Layer);
            if(bodyId<0)return FrameworkResult.Reject("UnsupportedLandingEndpoint");
            return subscribed?.TryLandAtTile(incoming,bodyId,map.Tile,out _) ==true?FrameworkResult.Ok():FrameworkResult.Reject("LandingUnavailable");
        }
    }
}
