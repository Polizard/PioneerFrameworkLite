using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PioneerFramework.Megastructures
{
    /// <summary>Durable backend saga. Native providers must make Ensure/Destroy idempotent.
    /// Failed operations retain their receipt and resume; they never delete pre-existing content.</summary>
    public sealed class StructureBackendService
    {
        private readonly MegastructureRuntime runtime;
        private readonly Dictionary<string,IStructureBackendProvider> providers=new(StringComparer.Ordinal);
        private BackendDocument State=>runtime.BackendState;
        public StructureBackendService(MegastructureRuntime runtime)=>this.runtime=runtime;
        public IReadOnlyList<BackendAssociation> Associations=>State.Associations.Select(MegastructureCodec.Clone).ToArray();
        public void Register(IStructureBackendProvider provider)
        {if(string.IsNullOrWhiteSpace(provider.Id)||!providers.TryAdd(provider.Id,provider))throw new ArgumentException("Duplicate backend provider.");}
        public bool HasProvider(string id)=>providers.ContainsKey(id);
        public IStructureBackendProvider? FindProvider(string id)=>providers.GetValueOrDefault(id);
        public IReadOnlyList<BackendOperationState> Operations=>State.Operations.Select(MegastructureCodec.Clone).ToArray();
        public FrameworkResult Retry(string operationId,int actor)
        {
            var operation=State.Operations.FirstOrDefault(o=>o.OperationId==operationId);
            if(operation?.Request==null)return FrameworkResult.Reject("OperationMissing");
            if(operation.Request.ActorFactionId!=actor)return FrameworkResult.Reject("PermissionDenied");
            var instance=runtime.GetSnapshot(operation.InstanceId);
            if(instance!=null&&instance.OwnerFactionId!=actor)return FrameworkResult.Reject("PermissionDenied");
            return Create(MegastructureCodec.Clone(operation.Request));
        }
        public FrameworkResult Create(StructureCreationRequest request)
        {
            if(string.IsNullOrWhiteSpace(request.OperationId)||string.IsNullOrWhiteSpace(request.Backend.StableId))return FrameworkResult.Reject("InvalidOperationId");
            if(request.ActorFactionId!=request.Structure.OwnerFactionId)return FrameworkResult.Reject("PermissionDenied");
            string hash=Fingerprint(request);
            var operation=State.Operations.FirstOrDefault(o=>o.OperationId==request.OperationId);
            if(operation!=null&&operation.Fingerprint!=hash)return FrameworkResult.Reject("OperationConflict");
            if(operation?.Result?.Success==true)return operation.Result;
            if(!providers.TryGetValue(request.Backend.ProviderId,out var provider))return FrameworkResult.Reject("BackendProviderMissing");
            var definition=runtime.Definition(request.Structure.DefinitionId);
            if(definition==null||definition.Nodes.All(n=>n.Id!=request.NodeId)||(!string.IsNullOrEmpty(definition.BackendProviderId)&&definition.BackendProviderId!=provider.Id))return FrameworkResult.Reject("InvalidDefinition");
            var validation=provider.Validate(request);if(!validation.Success)return validation;
            if(operation==null||operation.Stage<BackendOperationStage.InstanceReady)
            {
                var preview=MegastructureCodec.Clone(request.Structure);preview.OperationId=request.OperationId+"/instance";
                var preflight=runtime.PreviewCreate(preview);if(!preflight.Success)return preflight;
            }
            var associated=State.Associations.FirstOrDefault(a=>a.Backend==request.Backend&&!a.Released&&runtime.GetSnapshot(a.Node.InstanceId)?.Lifecycle!=StructureLifecycle.Retired);
            if(associated!=null&&associated.Node.InstanceId!=request.Structure.InstanceId)return FrameworkResult.Reject("BackendAlreadyAssociated",associated.Node.InstanceId);
            if(operation==null)
            {
                if(runtime.GetSnapshot(request.Structure.InstanceId)!=null)return FrameworkResult.Reject("DuplicateInstance");
                operation=new(){OperationId=request.OperationId,Fingerprint=hash,InstanceId=request.Structure.InstanceId,Backend=request.Backend,Request=MegastructureCodec.Clone(request)};State.Operations.Add(operation);
            }
            if(operation.Stage<BackendOperationStage.BackendReady)
            {
                var result=provider.Ensure(request,out var receipt);if(!result.Success)return result;
                if(receipt.Backend!=request.Backend)return FrameworkResult.Reject("BackendIdentityMismatch");
                operation.Receipt=receipt;operation.Stage=BackendOperationStage.BackendReady;
            }
            if(!provider.IsAvailable(request.Backend))return FrameworkResult.Reject("BackendMissing");
            if(operation.Stage<BackendOperationStage.InstanceReady)
            {
                var core=MegastructureCodec.Clone(request.Structure);core.OperationId=request.OperationId+"/instance";
                var result=runtime.Create(core);if(!result.Success)return result;operation.Stage=BackendOperationStage.InstanceReady;
            }
            var ownership=operation.Receipt!.Created&&!request.ExistingOnly?BindingOwnership.Owned:BindingOwnership.Borrowed;
            if(operation.Stage<BackendOperationStage.Bound)
            {
                var result=runtime.Execute(new(){OperationId=request.OperationId+"/binding",InstanceId=request.Structure.InstanceId,NodeId=request.NodeId,ActorFactionId=request.ActorFactionId,Kind=StructureCommandKind.AttachBackend,Backend=new(){Kind=provider.Id,Id=request.Backend.StableId,Ownership=ownership}});
                if(!result.Success)return result;
                if(!State.Associations.Any(a=>a.Node==new NodeAddress(request.Structure.InstanceId,request.NodeId)&&a.Backend==request.Backend))
                    State.Associations.Add(new(){Node=new(request.Structure.InstanceId,request.NodeId),Backend=request.Backend,Ownership=ownership,Generation=1+State.Associations.Count(a=>a.Backend==request.Backend)});
                operation.Stage=BackendOperationStage.Bound;
            }
            operation.Stage=BackendOperationStage.Completed;
            return operation.Result=FrameworkResult.Ok(request.Structure.InstanceId,runtime.GetSnapshot(request.Structure.InstanceId)!.Revision);
        }
        public string? ActivationGuard(StructureState instance)
        {
            var definition=runtime.Definition(instance.DefinitionId);
            var bindings=State.Associations.Where(a=>a.Node.InstanceId==instance.Id&&!a.Released).ToArray();
            if(!string.IsNullOrEmpty(definition?.BackendProviderId)&&bindings.Length==0)return "BackendAssociationRequired";
            foreach(var binding in bindings)
            {
                if(!providers.TryGetValue(binding.Backend.ProviderId,out var provider))return "BackendProviderMissing";
                if(!provider.IsAvailable(binding.Backend))return "BackendMissing";
            }
            return null;
        }
        public void Reconcile()
        {
            foreach(var association in State.Associations.Where(a=>!a.Released))
                association.BlockedReason=!providers.TryGetValue(association.Backend.ProviderId,out var provider)?"BackendProviderMissing":provider.IsAvailable(association.Backend)?"":"BackendMissing";
        }
        public FrameworkResult PrepareDisposition(string operationId,string instanceId,int actor,BackendDisposition disposition)
        {
            var instance=runtime.GetSnapshot(instanceId);if(instance==null)return FrameworkResult.Reject("InstanceMissing");
            if(instance.OwnerFactionId!=actor)return FrameworkResult.Reject("PermissionDenied");
            if(instance.Lifecycle==StructureLifecycle.Retired)return FrameworkResult.Reject("Retired");
            if(disposition==BackendDisposition.DestroyOwned&&State.Associations.Any(a=>a.Node.InstanceId==instanceId&&!a.Released&&a.Ownership!=BindingOwnership.Owned))return FrameworkResult.Reject("BorrowedBackendProtected");
            string hash=Fingerprint(new{instanceId,actor,disposition});
            var prior=State.Operations.FirstOrDefault(o=>o.OperationId==operationId);
            if(prior!=null)return prior.Fingerprint==hash?prior.Result??FrameworkResult.Ok(instanceId):FrameworkResult.Reject("OperationConflict");
            if(string.IsNullOrWhiteSpace(operationId))return FrameworkResult.Reject("InvalidOperationId");
            foreach(var a in State.Associations.Where(a=>a.Node.InstanceId==instanceId&&!a.Released))a.Disposition=disposition;
            var result=FrameworkResult.Ok(instanceId);
            State.Operations.Add(new(){OperationId=operationId,Fingerprint=hash,InstanceId=instanceId,Disposition=disposition,Stage=BackendOperationStage.Completed,Result=result});return result;
        }
        public FrameworkResult CanRelease(StructureState instance)
        {
            foreach(var a in State.Associations.Where(a=>a.Node.InstanceId==instance.Id&&!a.Released&&a.Ownership==BindingOwnership.Owned&&a.Disposition==BackendDisposition.DestroyOwned))
            {
                if(!providers.TryGetValue(a.Backend.ProviderId,out var provider))return FrameworkResult.Reject("BackendProviderMissing");
                var result=provider.CanDestroy(a.Backend);if(!result.Success)return result;
            }
            return FrameworkResult.Ok();
        }
        public FrameworkResult Release(StructureState instance)
        {
            var check=CanRelease(instance);if(!check.Success)return check;
            foreach(var a in State.Associations.Where(a=>a.Node.InstanceId==instance.Id&&!a.Released))
            {
                if(a.Ownership==BindingOwnership.Owned&&a.Disposition==BackendDisposition.DestroyOwned)
                {var result=providers[a.Backend.ProviderId].Destroy(a.Backend,"retire/"+instance.Id+"/"+a.Generation);if(!result.Success)return result;}
                a.Released=true;
            }
            return FrameworkResult.Ok();
        }
        public FrameworkResult AdoptLegacy(NodeAddress node,StructureBackendRef backend,BindingOwnership ownership)
        {
            if(State.Associations.Any(a=>a.Node==node&&a.Backend==backend))return FrameworkResult.Ok(node.InstanceId);
            if(runtime.GetSnapshot(node.InstanceId)?.Nodes.All(n=>n.Id!=node.NodeId)!=false)return FrameworkResult.Reject("NodeMissing");
            State.Associations.Add(new(){Node=node,Backend=backend,Ownership=ownership,Generation=1+State.Associations.Count(a=>a.Backend==backend)});return FrameworkResult.Ok(node.InstanceId);
        }
        public static string AssociationInstanceId(StructureBackendRef backend,int generation)=>"surface:"+Convert.ToHexString(Encoding.UTF8.GetBytes(backend.ProviderId+":"+backend.StableId))+":"+generation;
        private static string Fingerprint<T>(T value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(MegastructureCodec.Encode(value))));
    }
}
