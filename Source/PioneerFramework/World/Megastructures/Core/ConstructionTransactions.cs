using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Megastructures
{
    public sealed partial class MegastructureRuntime
    {
        // Construction actions are declarative state edits. Native movement, ownership transfer,
        // map generation and retirement have their own host transactions and cannot be staged here.
        private static bool IsStageAction(StructureCommandKind kind)=>kind is
            StructureCommandKind.AddNode or StructureCommandKind.AddCapability or StructureCommandKind.BindNode or
            StructureCommandKind.SetNodeEnabled or StructureCommandKind.SetCapabilityEnabled or
            StructureCommandKind.AttachBackend or StructureCommandKind.Connect or StructureCommandKind.Disconnect or StructureCommandKind.UpdateCapability;

        public FrameworkResult PreflightStage(IEnumerable<StructureCommand> actions)
        {
            var preview=new MegastructureRuntime {document=Export(),CurrentTick=CurrentTick,DocumentGuard=DocumentGuard,MutationGuard=MutationGuard,CapabilityGuard=CapabilityGuard};
            foreach(var entry in definitions)preview.definitions.Add(entry.Key,entry.Value);
            var commands=actions.ToArray();
            foreach(var action in commands)
            {
                if(!IsStageAction(action.Kind))return FrameworkResult.Reject("UnsupportedStageAction");
                var result=preview.Execute(action);if(!result.Success)return result;
            }
            foreach(var action in commands)
            {
                var candidate=preview.Find(action.InstanceId)!;
                if(action.Kind==StructureCommandKind.AddNode)
                {
                    var created=candidate.Nodes.First(n=>n.Id==action.NodeTemplate!.Id);
                    foreach(var state in created.Capabilities.Where(c=>c.Enabled)){var admission=preview.CheckCapability(candidate,created.Id,state);if(!admission.Success)return admission;}
                }
                if(action.Capability!=null)
                {
                    var capability=(string.IsNullOrEmpty(action.NodeId)?candidate.Capabilities:candidate.Nodes.First(n=>n.Id==action.NodeId).Capabilities).FirstOrDefault(c=>c.Id==(action.Kind==StructureCommandKind.UpdateCapability?action.CapabilityId:action.Capability.Id));
                    if(capability?.Enabled==true){var result=preview.CheckCapability(candidate,action.NodeId,capability);if(!result.Success)return result;}
                }
            }
            return FrameworkResult.Ok();
        }
        public FrameworkResult CommitStage(string operationId,ConstructionStage stage,StructureResourceService resources)
        {
            if(GetOperationResult(operationId)?.Success==true)
                return Begin(operationId,"stage:"+MegastructureCodec.Encode(stage.Actions)+MegastructureCodec.Encode(stage.Costs),out _)!;
            if(DeferCommands){var pending=MegastructureCodec.Clone(stage);pendingCommands.Enqueue(()=>CommitStage(operationId,pending,resources));return new(true,"Queued");}
            var prior=Begin(operationId,"stage:"+MegastructureCodec.Encode(stage.Actions)+MegastructureCodec.Encode(stage.Costs),out var receipt);if(prior!=null)return prior;
            var validation=PreflightStage(stage.Actions);if(!validation.Success){receipt!.Result=validation;return validation;}
            var payment=stage.Costs.Count==0?FrameworkResult.Ok():resources.Commit(operationId+"/materials",stage.Costs);
            if(!payment.Success){receipt!.Result=payment;return payment;}
            bool previous=DeferCommands;bool wasFlushing=flushing;flushing=true;
            try
            {
                // Single synchronous commit; callbacks remain deferred until every action has applied.
                foreach(var action in stage.Actions)
                {
                    DeferCommands=false;var result=Execute(action);DeferCommands=true;
                    if(!result.Success){receipt!.Result=result;return result;}
                }
                return receipt!.Result=FrameworkResult.Ok();
            }
            finally{DeferCommands=previous;flushing=wasFlushing;if(!previous)FlushCommands();}
        }
    }
}
