using System;
using System.Linq;
using PioneerFramework.Megastructures;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PFL.ArtificialSurfaceSample
{
    [StaticConstructorOnStartup]
    public static class SampleRegistration
    {
        static SampleRegistration()=>MegastructureRegistry.RegisterBackendProvider(RectangularBackend.ProviderId,()=>new RectangularBackend());
    }
    public sealed class RectangularBackend:IStructureBackendProvider,IBackendRequestFactory
    {
        public const string ProviderId="sample.rectangle";
        public string Id=>ProviderId;
        private static RectangularLayer? FindLayer(string id)=>Current.Game?.World==null?null:Find.WorldGrid.PlanetLayers.Values.OfType<RectangularLayer>().FirstOrDefault(l=>l.Surface.Backend.StableId==id);
        public static StructureCreationRequest Request(string id,int parent,string instance,int actor,bool import)=>new()
        {
            OperationId="sample-associate/"+instance,ActorFactionId=actor,Backend=new(ProviderId,id),ExistingOnly=import,Configuration=parent.ToString(System.Globalization.CultureInfo.InvariantCulture),NodeId="surface",
            Structure=new(){InstanceId=instance,DefinitionId="PF_RectangularSample",OwnerFactionId=actor,SpatialOverrides={["surface"]=new(){Mode=SpatialMode.BodyAnchored,BodyId=parent}}}
        };
        public FrameworkResult CreateDefault(CreateStructureRequest request,out StructureCreationRequest configured)
        {configured=Request(request.InstanceId,request.SpatialOverrides["surface"].BodyId,request.InstanceId,request.OwnerFactionId,false);configured.OperationId=request.OperationId;return FrameworkResult.Ok();}
        public FrameworkResult Validate(StructureCreationRequest request)=>int.TryParse(request.Configuration,out int body)&&body>=0?FrameworkResult.Ok():FrameworkResult.Reject("InvalidBody");
        public bool IsAvailable(StructureBackendRef backend)=>FindLayer(backend.StableId) is { } layer&&layer.Tiles.Count==6;
        public FrameworkResult Ensure(StructureCreationRequest request,out BackendCreationReceipt receipt)
        {
            receipt=new(){Backend=request.Backend};var existing=FindLayer(request.Backend.StableId);
            if(existing!=null)return FrameworkResult.Ok();
            if(request.ExistingOnly)return FrameworkResult.Reject("BackendMissing");
            if(Current.ProgramState!=ProgramState.Playing||Current.Game?.World==null||!ModsConfig.OdysseyActive||Scribe.mode!=LoadSaveMode.Inactive)return FrameworkResult.Reject("GameNotReady");
            var def=DefDatabase<PlanetLayerDef>.GetNamedSilentFail("PF_RectangularSurfaceSample");if(def==null)return FrameworkResult.Reject("DefinitionMissing");
            RectangularLayer? created=null;
            try
            {
                created=(RectangularLayer)Find.WorldGrid.RegisterPlanetLayer(def,radius:20,subdivisions:0,viewAngle:180);
                created.Configure(request.Backend.StableId,int.Parse(request.Configuration,System.Globalization.CultureInfo.InvariantCulture),3,2);
                created.GenerateHabitatTiles();Find.WorldPathGrid.RecalculateLayerPerceivedPathCosts(created);Find.WorldReachability.ClearCache();created.SetAllLayersDirty();
                receipt.Created=true;return FrameworkResult.Ok();
            }
            catch(Exception e)
            {if(created!=null&&created.Tiles.Count==0)Find.WorldGrid.RemovePlanetLayer(created);return FrameworkResult.Reject("BackendCreationFailed",e.Message);}
        }
        public FrameworkResult CanDestroy(StructureBackendRef backend)
        {var layer=FindLayer(backend.StableId);return layer==null||PioneerFramework.RuntimePlanetLayerService.CanRemove(layer,false,out _)?FrameworkResult.Ok():FrameworkResult.Reject("LayerContentsRemain");}
        public FrameworkResult Destroy(StructureBackendRef backend,string operationId)
        {var layer=FindLayer(backend.StableId);return layer==null||PioneerFramework.RuntimePlanetLayerService.TryRemove(layer,false,out _)?FrameworkResult.Ok():FrameworkResult.Reject("LayerRemovalFailed");}
    }
}
