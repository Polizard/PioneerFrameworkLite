using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 航天器地图全局注册表：管理所有 SpacecraftMapParent，不依赖口袋地图列表。
    /// </summary>
    public static class SpacecraftMapRegistry
    {
        private static readonly List<SpacecraftMapParent> maps = new List<SpacecraftMapParent>();

        public static IReadOnlyList<SpacecraftMapParent> All => maps;

        public static void Register(SpacecraftMapParent map)
        {
            if (map == null || maps.Contains(map))
                return;
            maps.Add(map);
        }

        public static void Unregister(SpacecraftMapParent map)
        {
            if (map != null)
                maps.Remove(map);
        }

        public static SpacecraftMapParent GetByMapId(int mapId)
        {
            if (mapId < 0)
                return null;
            return maps.FirstOrDefault(m => m.Map != null && m.Map.uniqueID == mapId);
        }

        public static SpacecraftMapParent GetBySpacecraftId(string spacecraftId)
        {
            if (string.IsNullOrEmpty(spacecraftId))
                return null;
            return maps.FirstOrDefault(m => m.spacecraftId == spacecraftId);
        }

        public static List<SpacecraftMapParent> GetByState(SpacecraftState state)
        {
            return maps.Where(m => m.state == state).ToList();
        }

        public static void Destroy(SpacecraftMapParent map)
        {
            if (map == null)
                return;
            Unregister(map);
            if (!map.Destroyed)
                map.Destroy();
        }

        /// <summary>
        /// 从当前世界对象中重新构建注册表（读档/异常恢复用）。
        /// </summary>
        public static void Reconcile()
        {
            maps.Clear();
            if (Find.World == null || Find.WorldObjects == null)
                return;

            foreach (var obj in Find.WorldObjects.AllWorldObjects)
            {
                if (obj is SpacecraftMapParent spacecraft)
                    maps.Add(spacecraft);
            }
        }
    }
}
