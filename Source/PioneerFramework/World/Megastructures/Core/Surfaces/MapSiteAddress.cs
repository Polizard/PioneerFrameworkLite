namespace PioneerFramework.Megastructures
{
    public readonly record struct StructureMapSiteAddress(NodeAddress Node,string SiteKey,SurfaceCellAddress? Cell)
    {
        public static StructureMapSiteAddress ForCell(SurfaceCellAddress cell)=>new(cell.Surface.Node,"cell:"+cell.Surface.SurfaceId+":"+cell.CellId.ToString(System.Globalization.CultureInfo.InvariantCulture),cell);
    }
    public sealed class StructureMapSiteState
    {
        public StructureMapSiteAddress Address { get; set; }
        public int MapParentId { get; set; }=-1;
        public int MapId { get; set; }=-1;
        public string BlockedReason { get; set; }="";
    }
}
