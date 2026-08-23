using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 完工火箭的舱内口袋地图 Comp。
    /// 在火箭完工/重建时生成或关联一张舱内地图；玩家可从火箭 Gizmo 进入，
    /// 在舱内装载人员、建造建筑，发射时该地图随火箭一起入轨。
    /// </summary>
    public class CompProperties_RocketPocketMap : CompProperties
    {
        public CompProperties_RocketPocketMap()
        {
            compClass = typeof(CompRocketPocketMap);
        }
    }

    public class CompRocketPocketMap : ThingComp
    {
        private int pocketMapId = -1;

        public int PocketMapId => pocketMapId;

        public bool HasPocketMap => GetPocketMap() != null;

        /// <summary>把一张已生成的口袋地图关联到本火箭。</summary>
        public void LinkMap(Map map)
        {
            pocketMapId = map != null ? map.uniqueID : -1;
        }

        /// <summary>获取本火箭关联的口袋地图；不存在返回 null。</summary>
        public Map GetPocketMap()
        {
            if (pocketMapId < 0)
                return null;
            return PioneerFramework.SpaceMapGenerator.FindPocketMap(pocketMapId);
        }

        /// <summary>进入本火箭的舱内口袋地图。</summary>
        public bool TryEnterPocketMap()
        {
            var map = GetPocketMap();
            if (map == null)
            {
                Messages.Message("火箭舱内地图不存在。", MessageTypeDefOf.RejectInput, false);
                return false;
            }

            PioneerFramework.SpaceMapGenerator.EnterPocketMap(map);
            return true;
        }

        /// <summary>把地面地图上的自由殖民者装入舱内口袋地图（调试用）。</summary>
        public int LoadColonistsFromGround()
        {
            Map source = parent?.Map;
            Map dest = GetPocketMap();
            if (source == null || dest == null || source == dest)
                return 0;

            int count = 0;
            var pawns = source.mapPawns?.FreeColonistsSpawned;
            if (pawns == null)
                return 0;

            foreach (Pawn pawn in pawns.ToList())
            {
                if (pawn == null || pawn.Dead || pawn.Destroyed || !pawn.Spawned)
                    continue;

                pawn.DeSpawn();
                GenSpawn.Spawn(pawn, FindSpawnCell(dest), dest, Rot4.North);
                count++;
            }

            return count;
        }

        /// <summary>把舱内口袋地图中的自由殖民者卸回地面地图（调试用）。</summary>
        public int UnloadColonistsToGround()
        {
            Map source = GetPocketMap();
            Map dest = parent?.Map;
            if (source == null || dest == null || source == dest)
                return 0;

            int count = 0;
            var pawns = source.mapPawns?.FreeColonistsSpawned;
            if (pawns == null)
                return 0;

            foreach (Pawn pawn in pawns.ToList())
            {
                if (pawn == null || pawn.Dead || pawn.Destroyed || !pawn.Spawned)
                    continue;

                pawn.DeSpawn();
                GenSpawn.Spawn(pawn, FindSpawnCell(dest), dest, Rot4.North);
                count++;
            }

            return count;
        }

        private static IntVec3 FindSpawnCell(Map map)
        {
            if (map == null)
                return IntVec3.Invalid;

            // 口袋地图由 PF_RoomPocket 生成：优先找可站立地板，中心兜底。
            foreach (IntVec3 cell in map.AllCells)
            {
                if (cell.InBounds(map) && cell.Standable(map))
                    return cell;
            }

            return map.Center;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref pocketMapId, "pocketMapId", -1);
        }

        public override string CompInspectStringExtra()
        {
            var map = GetPocketMap();
            return map != null
                ? "火箭舱内地图: " + map.uniqueID
                : "火箭舱内地图: 未生成";
        }
    }
}
