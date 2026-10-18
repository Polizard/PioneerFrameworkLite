using System.Linq;
using Verse;

namespace PioneerFramework.Megastructures
{
    /// <summary>Legacy slot projection of append-only stable site addresses; native map ordering is irrelevant.</summary>
    public sealed class SurfaceMapSlotProvider:IMapAccessProvider
    {
        private readonly MegastructureManager manager;
        public SurfaceMapSlotProvider(MegastructureManager manager)=>this.manager=manager;
        public FrameworkResult Access(NodeAddress address,CapabilityState capability,int slot,bool generate,out Map? map)
        {
            map=null;var surfaceId=MegastructureCodec.Read<SurfaceModuleData>(capability.Payload).SurfaceId;
            var sites=manager.MapSites.Sites.Where(s=>s.Address.Node==address&&s.Address.Cell?.Surface.SurfaceId==surfaceId).ToArray();
            if(slot<0||slot>=sites.Length)return FrameworkResult.Reject("MapSiteSelectionRequired");
            var owner=manager.GetSnapshot(address.InstanceId)!.OwnerFactionId;
            return manager.MapSites.Access(sites[slot].Address,generate,owner,new(250,1,250),out map);
        }
    }
}
