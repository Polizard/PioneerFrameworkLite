using System.Collections.Generic;

namespace PioneerFramework.Megastructures
{
    public sealed class MapCapabilityData
    {
        public List<int> MapIds { get; set; }=new();
        public Dictionary<int,int> EntityIds { get; set; }=new();
        public bool AllowGenerate { get; set; }
        public int Width { get; set; }=120;
        public int Height { get; set; }=120;
    }
}
