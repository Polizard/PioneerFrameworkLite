using System.Linq;
using RimWorld;
using Verse;

namespace PioneerFramework.Megastructures
{
    public sealed partial class MegastructureManager
    {
        partial void RegisterBuiltinBackends()=>backends!.Register(new RingWorld.RingBackendProvider());
        partial void RegisterCompatibilityDefinitions()
        {
            runtime.RegisterDefinition(new(){Id="PF_ExistingRing",Nodes={new(){Id="facility"}}});
        }
        partial void MigrateBackendAssociations()
        {
            SurfaceBindingMigration.AdoptLegacyBackend(runtime,Backends,"ring",RingWorld.RingBackendProvider.ProviderId);
        }
        public FrameworkResult ImportRing(RingWorld.RingSurfaceLayer ring,int ownerFactionId)
        {
            if(ownerFactionId!=Faction.OfPlayer.loadID)return FrameworkResult.Reject("PermissionDenied");
            return ImportSurface(ring,ownerFactionId);
        }
        public FrameworkResult CreateRing(RingWorld.RingParameters parameters,bool import)
        {
            EnsureInitialized();
            var backend=new StructureBackendRef(RingWorld.RingBackendProvider.ProviderId,parameters.RingId);
            var active=Backends.Associations.FirstOrDefault(a=>a.Backend==backend&&!a.Released&&GetSnapshot(a.Node.InstanceId)?.Lifecycle!=StructureLifecycle.Retired);
            if(active!=null)
            {
                var ring=RingWorld.RingWorldService.FindById(parameters.RingId);
                return ring?.Parameters==parameters?FrameworkResult.Ok(active.Node.InstanceId):FrameworkResult.Reject("BackendConfigurationConflict");
            }
            int generation=1+Backends.Associations.Count(a=>a.Backend==backend);
            string id=StructureBackendService.AssociationInstanceId(backend,generation);
            return Create(RingWorld.RingBackendProvider.Build(parameters,Faction.OfPlayer.loadID,id,import));
        }
    }
}
