using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 舱室内部生成步骤：全图铺设地板 + 地图边界一圈墙 + 全图解锁迷雾。
    /// 纯壳结构，无屋顶/照明/默认设施（v1），地图尺寸由舱室部件物理尺寸决定。
    /// </summary>
    public class GenStep_RoomShell : GenStep
    {
        public override int SeedPart => 812341;

        public override void Generate(Map map, GenStepParams parms)
        {
            // 1. 地板：金属地板（回退混凝土）
            var floor = DefDatabase<TerrainDef>.GetNamedSilentFail("MetalTile")
                        ?? TerrainDefOf.Concrete;
            foreach (var cell in map.AllCells)
                map.terrainGrid.SetTerrain(cell, floor);

            // 2. 边界墙：一圈钢制墙
            var steel = ThingDefOf.Steel;
            foreach (var cell in CellRect.WholeMap(map).EdgeCells)
            {
                var wall = ThingMaker.MakeThing(ThingDefOf.Wall, steel);
                GenSpawn.Spawn(wall, cell, map, Rot4.North);
            }

            // 3. 全图解锁迷雾（小舱室直接可见）
            map.fogGrid.ClearAllFog();
        }
    }
}
