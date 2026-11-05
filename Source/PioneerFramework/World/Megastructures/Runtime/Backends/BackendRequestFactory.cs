using RimWorld.Planet;

namespace PioneerFramework.Megastructures
{
    public interface IBackendRequestFactory
    {
        FrameworkResult CreateDefault(CreateStructureRequest request,out StructureCreationRequest configured);
    }
    public sealed partial class MegastructureManager
    {
        public FrameworkResult ImportSurface(PlanetLayer layer,int actor)
        {
            if(!ArtificialSurfaceLayers.TryDescribe(layer,out var surface)||surface.ImportRequestFactory==null)return FrameworkResult.Reject("BackendImportUnsupported");
            var active=Backends.Associations;
            foreach(var a in active)if(a.Backend==surface.Backend&&!a.Released&&GetSnapshot(a.Node.InstanceId)?.Lifecycle!=StructureLifecycle.Retired)return FrameworkResult.Ok(a.Node.InstanceId);
            int generation=1;foreach(var a in active)if(a.Backend==surface.Backend)generation++;
            string id=StructureBackendService.AssociationInstanceId(surface.Backend,generation);
            return Create(surface.ImportRequestFactory(actor,id));
        }
    }
}
