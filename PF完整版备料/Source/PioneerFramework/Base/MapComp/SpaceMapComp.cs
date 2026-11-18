using Verse;

namespace PioneerFramework
{
    public class SpaceMapComp : MapComponent
    {
        // 可扩展的持久化数据，例如空间站名称、内部建筑列表等
        private string stationName = "Unnamed Station";

        /// <summary>Map → Entity：此地图所属的 SpaceEntity ID。</summary>
        public int ownerEntityId = -1;

        /// <summary>Map → Part：此地图对应的 SpacePart.partId（在 ownerEntity 的 Parts 列表中）。</summary>
        public int linkedPartId = -1;

        public string StationName
        {
            get => stationName;
            set => stationName = value;
        }

        public SpaceMapComp(Map map) : base(map) { }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref stationName, "stationName", "Unnamed Station");
            Scribe_Values.Look(ref ownerEntityId, "ownerEntityId", -1);
            Scribe_Values.Look(ref linkedPartId, "linkedPartId", -1);
        }
    }
}