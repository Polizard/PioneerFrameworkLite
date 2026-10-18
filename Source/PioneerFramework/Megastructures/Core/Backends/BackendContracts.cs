using System.Collections.Generic;

namespace PioneerFramework.Megastructures
{
    public readonly record struct StructureBackendRef(string ProviderId,string StableId);
    public enum BackendDisposition { DestroyOwned, DetachKeepBackend }
    public sealed class StructureCreationRequest
    {
        public string OperationId { get; set; }="";
        public int ActorFactionId { get; set; }=-1;
        public CreateStructureRequest Structure { get; set; }=new();
        public string NodeId { get; set; }="surface";
        public StructureBackendRef Backend { get; set; }
        public string Configuration { get; set; }="{}";
        public bool ExistingOnly { get; set; }
    }
    public sealed class BackendCreationReceipt
    {
        public StructureBackendRef Backend { get; set; }
        public bool Created { get; set; }
    }
    public interface IStructureBackendProvider
    {
        string Id { get; }
        FrameworkResult Validate(StructureCreationRequest request);
        FrameworkResult Ensure(StructureCreationRequest request,out BackendCreationReceipt receipt);
        bool IsAvailable(StructureBackendRef backend);
        FrameworkResult CanDestroy(StructureBackendRef backend);
        FrameworkResult Destroy(StructureBackendRef backend,string operationId);
    }
    public sealed class BackendAssociation
    {
        public NodeAddress Node { get; set; }
        public StructureBackendRef Backend { get; set; }
        public BindingOwnership Ownership { get; set; }
        public int Generation { get; set; }=1;
        public BackendDisposition Disposition { get; set; }
        public bool Released { get; set; }
        public string BlockedReason { get; set; }="";
    }
    public sealed class BackendDocument
    {
        public List<BackendOperationState> Operations { get; set; }=new();
        public List<BackendAssociation> Associations { get; set; }=new();
    }
}
