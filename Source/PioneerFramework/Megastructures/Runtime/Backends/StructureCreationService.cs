using System.Linq;
using RimWorld;
using Verse;

namespace PioneerFramework.Megastructures
{
    public sealed partial class MegastructureManager
    {
        private StructureBackendService? backends;
        public StructureBackendService Backends {get{EnsureInitialized();return backends!;}}
        public FrameworkResult RetryBackend(string operationId,int actor)
        {
            EnsureInitialized();if(Scribe.mode!=LoadSaveMode.Inactive||Current.ProgramState!=ProgramState.Playing)return FrameworkResult.Reject("SaveInProgress");
            var result=Backends.Retry(operationId,actor);if(result.Success)Surfaces.Reconcile();return result;
        }
        public FrameworkResult Create(StructureCreationRequest request)
        {
            EnsureInitialized();
            if(!string.IsNullOrEmpty(LoadError))return FrameworkResult.Reject(LoadError);
            if(Scribe.mode!=LoadSaveMode.Inactive||Current.ProgramState!=ProgramState.Playing)return FrameworkResult.Reject("SaveInProgress");
            runtime.CurrentTick=Find.TickManager?.TicksGame??0;
            var result=Backends.Create(request);if(result.Success){Surfaces.Reconcile();if(Find.WorldObjects!=null)MapSites.Reconcile();}return result;
        }
        public FrameworkResult RetireBackend(string operationId,string instanceId,int actor,BackendDisposition disposition)
        {
            EnsureInitialized();if(Scribe.mode!=LoadSaveMode.Inactive)return FrameworkResult.Reject("SaveInProgress");
            var result=Backends.PrepareDisposition(operationId,instanceId,actor,disposition);if(!result.Success)return result;
            var instance=GetSnapshot(instanceId)!;
            if(instance.Lifecycle!=StructureLifecycle.Decommissioning)
            {result=Execute(new(){OperationId=operationId+"/begin",InstanceId=instanceId,ActorFactionId=actor,Kind=StructureCommandKind.BeginRetirement});if(!result.Success)return result;}
            return Execute(new(){OperationId=operationId+"/finish",InstanceId=instanceId,ActorFactionId=actor,Kind=StructureCommandKind.FinishRetirement});
        }
    }
}
