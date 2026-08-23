using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace PioneerFramework
{
    /// <summary>
    /// 火箭舱内 50x50 地图布局：7x4 房间、围墙、屋顶、宽段中央舱门（PocketMapExit）。
    /// </summary>
    public class GenStep_RocketCabinLayout : GenStep
    {
        public override int SeedPart => 421331;

        // 50x50 地图内的房间与门坐标。
        public static readonly IntVec3 CabinRoomMin = new IntVec3(22, 0, 23);
        public static readonly IntVec3 CabinRoomMax = new IntVec3(28, 0, 26);
        public static readonly IntVec3 CabinDoorCell = new IntVec3(25, 0, 22);

        public override void Generate(Map map, GenStepParams parms)
        {
            if (map == null)
                return;

            TerrainDef floor = DefDatabase<TerrainDef>.GetNamedSilentFail("MetalTile") ?? TerrainDefOf.Concrete;
            for (int x = CabinRoomMin.x; x <= CabinRoomMax.x; x++)
            {
                for (int z = CabinRoomMin.z; z <= CabinRoomMax.z; z++)
                {
                    IntVec3 cell = new IntVec3(x, 0, z);
                    if (cell.InBounds(map))
                        map.terrainGrid.SetTerrain(cell, floor);
                }
            }

            IntVec3 outerMin = new IntVec3(CabinRoomMin.x - 1, 0, CabinRoomMin.z - 1);
            IntVec3 outerMax = new IntVec3(CabinRoomMax.x + 1, 0, CabinRoomMax.z + 1);

            foreach (IntVec3 cell in CellRect.FromLimits(outerMin, outerMax).Cells)
            {
                bool onWall = cell.x == outerMin.x || cell.x == outerMax.x
                    || cell.z == outerMin.z || cell.z == outerMax.z;
                if (!onWall || cell == CabinDoorCell || !cell.InBounds(map))
                    continue;

                Thing wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel);
                GenSpawn.Spawn(wall, cell, map, Rot4.North);
            }

            RoofDef roof = DefDatabase<RoofDef>.GetNamedSilentFail("RoofConstructed")
                ?? DefDatabase<RoofDef>.GetNamedSilentFail("RoofRock");
            if (roof != null)
            {
                for (int x = CabinRoomMin.x; x <= CabinRoomMax.x; x++)
                {
                    for (int z = CabinRoomMin.z; z <= CabinRoomMax.z; z++)
                    {
                        IntVec3 cell = new IntVec3(x, 0, z);
                        if (cell.InBounds(map))
                            map.roofGrid.SetRoof(cell, roof);
                    }
                }

                if (CabinDoorCell.InBounds(map))
                    map.roofGrid.SetRoof(CabinDoorCell, roof);
            }

            ThingDef exitDef = PocketMapUtility.currentlyGeneratingPortal?.def?.portal?.exitDef
                ?? DefDatabase<ThingDef>.GetNamedSilentFail("PF_Building_Rocket_CabinExit");
            if (exitDef == null)
                return;

            if (!CabinDoorCell.InBounds(map))
                return;

            Building exit = (Building)ThingMaker.MakeThing(exitDef);
            exit.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(exit, CabinDoorCell, map, Rot4.North);

            MapGenerator.PlayerStartSpot = new IntVec3(CabinRoomMin.x + 3, 0, CabinRoomMin.z);
        }
    }
}
