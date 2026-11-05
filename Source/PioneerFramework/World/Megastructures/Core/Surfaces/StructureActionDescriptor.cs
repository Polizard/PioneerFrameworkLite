namespace PioneerFramework.Megastructures
{
    public sealed record StructureActionDescriptor(string Key,string LabelKey,StructurePermission Permission,string ParameterType,bool Available,string BlockedReason="");
    public sealed class StructureSurfaceActionRequest
    {
        public string OperationId { get; set; }="";
        public string Key { get; set; }="";
        public SurfaceAddress Surface { get; set; }
        public int CellId { get; set; }=-1;
        public int ActorFactionId { get; set; }=-1;
        public long ExpectedRevision { get; set; }=-1;
        public int Width { get; set; }=250;
        public int Height { get; set; }=250;
    }
}
