namespace PioneerFramework.Megastructures
{
    public enum BackendOperationStage { Requested, BackendReady, InstanceReady, Bound, Completed }
    public sealed class BackendOperationState
    {
        public string OperationId { get; set; }="";
        public string Fingerprint { get; set; }="";
        public string InstanceId { get; set; }="";
        public StructureBackendRef Backend { get; set; }
        public BackendOperationStage Stage { get; set; }
        public BackendCreationReceipt? Receipt { get; set; }
        public StructureCreationRequest? Request { get; set; }
        public BackendDisposition Disposition { get; set; }
        public FrameworkResult? Result { get; set; }
    }
}
