using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace PioneerFramework.Megastructures
{
    public sealed partial class MegastructureManager:GameComponent,IServiceProvider,IMegastructureService
    {
        private MegastructureRuntime runtime=new();
        private string payload="";
        private bool initialized;
        private long lastTick=-1;
        private CapabilityScheduler? scheduler;
        private StructureResourceService? resources;
        private StructureSpatialService? spatial;
        private StructureEndpointService? endpoints;
        private StructureMapService? maps;
        private StructureSurfaceService? surfaces;
        private StructureMapSiteService? mapSites;
        private StructureActionService? actions;
        public StructureActionService Actions {get{EnsureInitialized();return actions!;}}
        public StructureMapSiteService MapSites {get{EnsureInitialized();return mapSites!;}}
        public StructureSurfaceService Surfaces {get{EnsureInitialized();return surfaces!;}}
        public string SelectedInstanceId { get; set; }="";
        public string LoadError { get; private set; }="";
        public MegastructureRuntime Runtime { get {EnsureInitialized();return runtime;} }
        public StructureResourceService Resources { get {EnsureInitialized();return resources!;} }
        public StructureSpatialService Spatial { get {EnsureInitialized();return spatial!;} }
        public StructureEndpointService Endpoints { get {EnsureInitialized();return endpoints!;} }
        public StructureMapService Maps { get {EnsureInitialized();return maps!;} }
        public IReadOnlyList<StructureState> Snapshots=>Runtime.Snapshots;
        public StructureState? GetSnapshot(string instanceId)=>Runtime.GetSnapshot(instanceId);
        public FrameworkResult Create(CreateStructureRequest request)
        {EnsureInitialized();if(!string.IsNullOrEmpty(LoadError))return FrameworkResult.Reject(LoadError);if(Scribe.mode!=LoadSaveMode.Inactive)return FrameworkResult.Reject("SaveInProgress");if(!string.IsNullOrEmpty(runtime.Definition(request.DefinitionId)?.BackendProviderId))
            {
                if(Backends.FindProvider(runtime.Definition(request.DefinitionId)!.BackendProviderId) is not IBackendRequestFactory factory)return FrameworkResult.Reject("BackendConfigurationRequired");
                var built=factory.CreateDefault(request,out var configured);return built.Success?Create(configured):built;
            }runtime.CurrentTick=Find.TickManager?.TicksGame??0;return runtime.Create(request);}
        public FrameworkResult Execute(StructureCommand command)
        {EnsureInitialized();if(!string.IsNullOrEmpty(LoadError))return FrameworkResult.Reject(LoadError);if(Scribe.mode!=LoadSaveMode.Inactive)return FrameworkResult.Reject("SaveInProgress");runtime.CurrentTick=Find.TickManager?.TicksGame??0;return runtime.Execute(command);}
        public MegastructureManager(Game game) { }
        public override void StartedNewGame()=>EnsureInitialized();
        public override void LoadedGame()=>EnsureInitialized();
        private void EnsureInitialized()
        {
            if(initialized)return;initialized=true;
            foreach(var def in DefDatabase<MegastructureDef>.AllDefsListForReading)
            {var result=runtime.RegisterDefinition(def.ToDefinition());if(!result.Success)Log.Error("[PFL Megastructures] "+def.defName+": "+result.Code);}
            RegisterCompatibilityDefinitions();
            if(!string.IsNullOrEmpty(payload))
            {
                try{var result=runtime.Restore(MegastructureCodec.Decode(payload));if(!result.Success)LoadError=result.Code;}
                catch(Exception e){LoadError="InvalidFrameworkSave";Log.Error("[PFL Megastructures] "+e);}
            }
            resources=new StructureResourceService(runtime.ResourceState);
            foreach(var store in thingStores){store.Clock=()=>resources.Tick;resources.RegisterProvider(store);}
            spatial=new StructureSpatialService(runtime,new GameStructureSpatialHost(),TimeScaleManager.GetPhysicsTickSafe);
            foreach(var factory in MegastructureRegistry.SpatialFactories)
                try {spatial.RegisterProvider(factory());}catch(Exception e){Log.Error("[PFL Megastructures] Spatial provider: "+e);}
            endpoints=new StructureEndpointService(runtime,spatial,resources,runtime.TransportState);
            maps=new StructureMapService(this);
            maps.RegisterProvider(SurfaceModules.Map,new SurfaceMapSlotProvider(this));
            surfaces=new StructureSurfaceService(this);
            mapSites=new StructureMapSiteService(this);
            actions=new StructureActionService(this);
            backends=new StructureBackendService(runtime);
            RegisterBuiltinBackends();
            foreach(var factory in MegastructureRegistry.BackendFactories)backends.Register(factory());
            MigrateBackendAssociations();
            runtime.Changed+=_=>surfaces.Reconcile();
            runtime.ActivationGuard=backends.ActivationGuard;
            runtime.DocumentGuard=spatial.Validate;
            runtime.RetirementGuard=CheckRetirement;
            runtime.CommandExecutor=ExecuteHostCommand;
            runtime.MutationGuard=HostMutationGuard;
            runtime.CapabilityGuard=MegastructureRegistry.Capabilities.Admit;
            runtime.CallbackError=e=>Log.Error("[PFL Megastructures] Callback: "+e);
            scheduler=new CapabilityScheduler(runtime,MegastructureRegistry.Capabilities){Services=this,Error=(id,e)=>Log.Error("[PFL Megastructures] "+id+": "+e)};
            MegastructureRegistry.Capabilities.Seal();
            if(string.IsNullOrEmpty(LoadError))LoadError=spatial.Validate(runtime.Export())??"";
            AttachHostEvents();
            surfaces.Reconcile();
        }
        public object? GetService(Type type)=>type==typeof(StructureResourceService)?Resources:type==typeof(StructureSpatialService)?Spatial:type==typeof(StructureEndpointService)?Endpoints:type==typeof(IResourceStoreFactory)?this:type==typeof(MegastructureManager)?this:null;
        private FrameworkResult ExecuteHostCommand(StructureState instance,StructureCommand command)
        {
            if(command.Kind==StructureCommandKind.Maneuver)
            {
                var node=instance.Nodes.FirstOrDefault(n=>n.Id==command.NodeId);var group=node==null?null:subscribed?.GetGroupById(node.Spatial.GroupId);
                if(group==null||group.FactionId!=instance.OwnerFactionId)return FrameworkResult.Reject("PermissionDenied");
                return Spatial.Maneuver(instance,command.NodeId,command.DeltaV);
            }
            if(command.Kind==StructureCommandKind.SetCombatEnabled)return SetHostCombat(instance,command);
            if(command.Kind==StructureCommandKind.FinishRetirement)return FinishHostRetirement(instance);
            if(command.Kind==StructureCommandKind.ChangeOwner)
                foreach(var node in instance.Nodes.Where(n=>n.Spatial.Mode==SpatialMode.PhysicalGroup&&n.Spatial.Ownership==BindingOwnership.Owned))
                {
                    var group=subscribed?.GetGroupById(node.Spatial.GroupId);if(group!=null){group.FactionId=instance.OwnerFactionId;foreach(int id in group.GetEntityIds()){var entity=subscribed!.GetEntityById(id);if(entity!=null)entity.FactionId=instance.OwnerFactionId;}}
                }
            return FrameworkResult.Ok();
        }
        public override void GameComponentTick()
        {
            EnsureInitialized();if(!string.IsNullOrEmpty(LoadError))return;
            long now=Find.TickManager.TicksGame;runtime.CurrentTick=now;
            if(lastTick==now||now%250!=0)return;lastTick=now;ReconcileHostBindings();scheduler!.Tick(now);
        }
        public override void ExposeData()
        {
            if(Scribe.mode==LoadSaveMode.Saving&&string.IsNullOrEmpty(LoadError))payload=MegastructureCodec.Encode(Runtime.Export());
            Scribe_Values.Look(ref payload,"pfMegastructures","");
            Scribe_Collections.Look(ref thingStores,"pfMegastructureThingStores",LookMode.Deep);
            thingStores??=new();
            if(Scribe.mode==LoadSaveMode.LoadingVars){initialized=false;runtime=new();}
        }
        partial void AttachHostEvents();
        partial void RegisterBuiltinBackends();
        partial void RegisterCompatibilityDefinitions();
        partial void MigrateBackendAssociations();
        partial void ReconcileHostBindings();
        private string? CheckRetirement(StructureState instance)=>HostRetirementGuard(instance);
        private partial string? HostRetirementGuard(StructureState instance);
    }
}
