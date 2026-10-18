using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Megastructures
{
    public sealed class StructureMapSiteService
    {
        private readonly MegastructureManager manager;
        public StructureMapSiteService(MegastructureManager manager)=>this.manager=manager;
        public IReadOnlyList<StructureMapSiteState> Sites=>manager.Runtime.MapSiteState.Select(MegastructureCodec.Clone).ToArray();
        public FrameworkResult Access(StructureMapSiteAddress address,bool generate,int actor,IntVec3 size,out Map? map)
        {
            map=null;var instance=manager.GetSnapshot(address.Node.InstanceId);
            if(instance==null||instance.Lifecycle==StructureLifecycle.Retired||instance.Nodes.All(n=>n.Id!=address.Node.NodeId||!n.Enabled))return FrameworkResult.Reject("NodeUnavailable");
            if(!StructurePermissions.Allows(instance,address.Node.NodeId,actor,StructurePermission.View)||(generate&&!StructurePermissions.Allows(instance,address.Node.NodeId,actor,StructurePermission.Construct)))return FrameworkResult.Reject("PermissionDenied");
            if(address.Cell is not { } cell||cell.Surface.Node!=address.Node||StructureMapSiteAddress.ForCell(cell)!=address)return FrameworkResult.Reject("InvalidMapSite");
            if(!manager.Surfaces.TryResolve(cell.Surface,out var layer,out var surface))return FrameworkResult.Reject("SurfaceMissing");
            if(!surface.ModuleEnabled(SurfaceModules.Map))return FrameworkResult.Reject("CapabilityDisabled");
            var old=manager.Runtime.MapSiteState.FirstOrDefault(s=>s.Address==address);
            var parent=Find.WorldObjects.MapParentAt(new PlanetTile(cell.CellId,layer));
            if(old?.MapParentId>=0&&(parent==null||parent.ID!=old.MapParentId))return FrameworkResult.Reject("MapParentMissing");
            if(old?.MapId>=0&&parent?.Map==null)return FrameworkResult.Reject("MapRestoreRequired");
            if(!SettlementMapSiteProvider.TryGetOrGenerateMap(layer,cell.CellId,size,generate,out map,out string reason))return FrameworkResult.Reject("MapAccessFailed",reason);
            Register(address,map!.Parent.ID,map.uniqueID);return FrameworkResult.Ok(address.Node.InstanceId);
        }
        internal void Register(StructureMapSiteAddress address,int parentId,int mapId)
        {
            var site=manager.Runtime.MapSiteState.FirstOrDefault(s=>s.Address==address);
            if(site==null){site=new(){Address=address};manager.Runtime.MapSiteState.Add(site);}
            if(site.MapParentId>=0&&site.MapParentId!=parentId){site.BlockedReason="MapParentIdentityChanged";return;}
            if(mapId<0&&site.MapId>=0){site.BlockedReason="MapRestoreRequired";return;}
            site.MapParentId=parentId;site.MapId=mapId;site.BlockedReason="";
        }
        public void Reconcile()
        {
            if(Current.Game?.World==null||Find.WorldObjects==null)return;
            foreach(var surface in manager.Surfaces.ForInstanceIds())
            {
                if(!manager.Surfaces.TryResolve(surface,out var layer,out _))continue;
                foreach(var parent in Find.WorldObjects.AllWorldObjects.OfType<MapParent>().Where(p=>p.Tile.Layer==layer&&p.Faction==Faction.OfPlayer))
                    Register(StructureMapSiteAddress.ForCell(new(surface,parent.Tile.tileId)),parent.ID,parent.Map?.uniqueID??-1);
            }
            foreach(var site in manager.Runtime.MapSiteState)
                if(Find.WorldObjects.AllWorldObjects.All(p=>p.ID!=site.MapParentId))site.BlockedReason="MapParentMissing";
        }
        public static bool AccessNative(PlanetLayer layer,int cell,IntVec3 size,out Map? map,out string reason)
        {
            var manager=Current.Game?.GetComponent<MegastructureManager>();
            if(manager!=null&&ArtificialSurfaceLayers.TryDescribe(layer,out var surface))
            {
                var a=manager.Backends.Associations.FirstOrDefault(a=>!a.Released&&a.Backend==surface.Backend);
                if(a!=null)
                {
                    var result=manager.MapSites.Access(StructureMapSiteAddress.ForCell(new(new(a.Node,surface.SurfaceId),cell)),true,Faction.OfPlayer.loadID,size,out map);
                    reason=result.Success?"":result.Code+": "+result.Detail;return result.Success;
                }
            }
            return SettlementMapSiteProvider.TryGetOrGenerateMap(layer,cell,size,true,out map,out reason);
        }
    }
}
