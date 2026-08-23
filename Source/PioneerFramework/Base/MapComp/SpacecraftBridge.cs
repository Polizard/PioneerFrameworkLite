using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 地面与航天器地图之间的桥：只保存映射数据，不承载地图。
    /// </summary>
    public class SpacecraftBridge : IExposable
    {
        public int groundMapId = -1;
        public IntVec3 groundCell;
        public int spacecraftMapId = -1;
        public IntVec3 spacecraftCell;

        public bool Valid => groundMapId >= 0 && spacecraftMapId >= 0;

        public void ExposeData()
        {
            Scribe_Values.Look(ref groundMapId, "groundMapId", -1);
            Scribe_Values.Look(ref groundCell, "groundCell");
            Scribe_Values.Look(ref spacecraftMapId, "spacecraftMapId", -1);
            Scribe_Values.Look(ref spacecraftCell, "spacecraftCell");
        }
    }
}
