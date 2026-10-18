using System;
using PioneerFramework.Megastructures;
using Verse;

namespace PioneerFramework.RingWorld
{
    public sealed class RingBackendProperties:MegastructureBackendProperties
    {
        public int columns=256;
        public int rows=3;
        public double radiusMultiplier=3.2;
        public int seedOffset=194913;
        public override string Encode()=>MegastructureCodec.Encode(new RingCreationDefaults{Columns=columns,Rows=rows,RadiusMultiplier=radiusMultiplier,SeedOffset=seedOffset});
    }
    public sealed class RingCreationDefaults
    {
        public int Columns {get;set;}=256;
        public int Rows {get;set;}=3;
        public double RadiusMultiplier {get;set;}=3.2;
        public int SeedOffset {get;set;}=194913;
    }
    public sealed class RingBackendConfiguration
    {
        public string RingId { get; set; }="";
        public int ParentBodyId { get; set; }
        public int Seed { get; set; }
        public int Columns { get; set; }=256;
        public int Rows { get; set; }=3;
        public double RadiusMeters { get; set; }
        public RingParameters ToParameters()=>new(RingId,ParentBodyId,Seed,Columns,Rows,RadiusMeters);
        public static RingBackendConfiguration From(RingParameters p)=>new(){RingId=p.RingId,ParentBodyId=p.ParentBodyId,Seed=p.Seed,Columns=p.Columns,Rows=p.Rows,RadiusMeters=p.RadiusMeters};
    }
    public sealed class RingBackendProvider:IStructureBackendProvider,IBackendRequestFactory
    {
        public const string ProviderId="pfl.ring";
        public string Id=>ProviderId;
        public FrameworkResult CreateDefault(CreateStructureRequest request,out StructureCreationRequest configured)
        {
            configured=null!;
            var definition=DefDatabase<MegastructureDef>.GetNamedSilentFail(request.DefinitionId);
            int parent=request.SpatialOverrides.TryGetValue("facility",out var spatial)?spatial.BodyId:definition?.nodes.Find(n=>n.key=="facility")?.bodyId??-1;
            if(parent<0)parent=ViewFrame.ViewedBodyId;
            var body=Current.Game?.GetComponent<CelestialBodyManager>()?.GetBodyById(parent);
            if(body==null)return FrameworkResult.Reject("BodyMissing");
            RingParameters parameters;
            try
            {
                var properties=definition?.backendProperties;
                var defaults=properties==null?new RingCreationDefaults():MegastructureCodec.Read<RingCreationDefaults>(properties.Encode());
                parameters=new RingParameters(request.InstanceId,parent,checked(defaults.SeedOffset+parent),defaults.Columns,defaults.Rows,body.radius*defaults.RadiusMultiplier);
            }
            catch(Exception e) when(e is ArgumentException or OverflowException or System.Text.Json.JsonException)
            {return FrameworkResult.Reject("InvalidBackendConfiguration",e.Message);}
            configured=Build(parameters,request.OwnerFactionId,request.InstanceId,false,request.OperationId);
            configured.Structure=MegastructureCodec.Clone(request);
            if(!configured.Structure.SpatialOverrides.ContainsKey("facility"))configured.Structure.SpatialOverrides["facility"]=new(){Mode=SpatialMode.BodyAnchored,BodyId=parent};
            return FrameworkResult.Ok();
        }
        public static StructureCreationRequest Build(RingParameters parameters,int actor,string instanceId,bool import,string? operationId=null)=>new()
        {
            OperationId=operationId??"ring-associate/"+instanceId,ActorFactionId=actor,ExistingOnly=import,
            Backend=new(ProviderId,parameters.RingId),NodeId="facility",Configuration=MegastructureCodec.Encode(RingBackendConfiguration.From(parameters)),
            Structure=new(){InstanceId=instanceId,DefinitionId="PF_Megastructure_RingWorld",OwnerFactionId=actor,SpatialOverrides={["facility"]=new(){Mode=SpatialMode.BodyAnchored,BodyId=parameters.ParentBodyId}}}
        };
        public FrameworkResult Validate(StructureCreationRequest request)
        {
            try
            {
                var p=MegastructureCodec.Read<RingBackendConfiguration>(request.Configuration).ToParameters();
                if(p.RingId!=request.Backend.StableId)return FrameworkResult.Reject("BackendIdentityMismatch");
                if(!request.Structure.SpatialOverrides.TryGetValue(request.NodeId,out var anchor)||anchor.Mode!=SpatialMode.BodyAnchored||anchor.BodyId!=p.ParentBodyId||anchor.Offset!=DVector3.zero||anchor.Velocity!=DVector3.zero||anchor.AngularVelocity!=DVector3.zero||anchor.Rotation!=RotationData.Identity)
                    return FrameworkResult.Reject("SurfaceAnchorMismatch");
                var existing=RingWorldService.FindById(p.RingId);
                if(existing!=null&&existing.Parameters!=p)return FrameworkResult.Reject("BackendConfigurationConflict");
                if(existing==null)
                {
                    if(request.ExistingOnly)return FrameworkResult.Reject("BackendMissing");
                    if(!ModsConfig.OdysseyActive)return FrameworkResult.Reject("OdysseyRequired");
                    var body=Current.Game?.GetComponent<CelestialBodyManager>()?.GetBodyById(p.ParentBodyId);
                    if(body==null||!body.hasSurface)return FrameworkResult.Reject("BodyMissing");
                    if(!RingWorldService.ValidateParent(p,body.radius,out string reason))return FrameworkResult.Reject("InvalidRingRadius",reason);
                    if(DefDatabase<RimWorld.PlanetLayerDef>.GetNamedSilentFail(RingWorldService.LayerDefName)==null)return FrameworkResult.Reject("DefinitionMissing");
                }
                return FrameworkResult.Ok();
            }
            catch(Exception e) when(e is ArgumentException or System.Text.Json.JsonException or NotSupportedException)
            {return FrameworkResult.Reject("InvalidBackendConfiguration",e.Message);}
        }
        public FrameworkResult Ensure(StructureCreationRequest request,out BackendCreationReceipt receipt)
        {
            receipt=new(){Backend=request.Backend};
            var p=MegastructureCodec.Read<RingBackendConfiguration>(request.Configuration).ToParameters();
            var existing=RingWorldService.FindById(p.RingId);
            if(request.ExistingOnly)return existing==null?FrameworkResult.Reject("BackendMissing"):FrameworkResult.Ok();
            if(!RingWorldNativeBackend.TryCreate(p,out _,out string reason))return FrameworkResult.Reject("BackendCreationFailed",reason);
            receipt.Created=existing==null;return FrameworkResult.Ok();
        }
        public bool IsAvailable(StructureBackendRef backend)=>RingWorldService.FindById(backend.StableId) is { } ring&&ring.Tiles.Count==ring.Topology.TileCount;
        public FrameworkResult CanDestroy(StructureBackendRef backend)
        {
            var ring=RingWorldService.FindById(backend.StableId);
            return ring==null||RuntimePlanetLayerService.CanRemove(ring,false,out _)?FrameworkResult.Ok():FrameworkResult.Reject("LayerContentsRemain");
        }
        public FrameworkResult Destroy(StructureBackendRef backend,string operationId)
        {
            var ring=RingWorldService.FindById(backend.StableId);
            return ring==null||RingWorldNativeBackend.TryRemove(ring,out _)?FrameworkResult.Ok():FrameworkResult.Reject("LayerRemovalFailed");
        }
    }
}
