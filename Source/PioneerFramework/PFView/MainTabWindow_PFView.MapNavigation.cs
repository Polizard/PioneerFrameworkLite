using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>只收集当前可操作编队已有的主地图；入口与延迟菜单回调共用同一份校验。</summary>
    internal static class PFViewMapNavigation
    {
        internal static void CollectMaps(SpaceGameComp? space, int groupId, int playerFactionId,
            IReadOnlyList<Map> loadedMaps, List<Map> result)
        {
            result.Clear();
            var group = space?.GetGroupById(groupId);
            if (group == null || !group.IsActive || playerFactionId < 0 || group.FactionId != playerFactionId) return;

            foreach (int entityId in group.GetEntityIds())
            {
                var entity = space!.GetEntityById(entityId);
                if (entity == null || entity.GroupId != groupId || !entity.HasMap
                    || entity.IsMapArchived || entity.MapId < 0) continue;
                for (int i = 0; i < loadedMaps.Count; i++)
                {
                    var map = loadedMaps[i];
                    if (map.uniqueID != entity.MapId) continue;
                    if (!result.Contains(map)) result.Add(map);
                    break;
                }
            }
            result.Sort((a, b) => a.uniqueID.CompareTo(b.uniqueID));
        }
    }

    public partial class MainTabWindow_PFView
    {
        private readonly List<Map> navigableGroupMaps = new();

        private bool RefreshNavigableGroupMaps(int groupId)
        {
            PFViewMapNavigation.CollectMaps(Current.Game?.GetComponent<SpaceGameComp>(), groupId,
                Faction.OfPlayer?.loadID ?? -1, Find.Maps, navigableGroupMaps);
            return navigableGroupMaps.Count > 0;
        }

        private void ShowGroupMapMenu(int groupId)
        {
            var session = Current.Game;
            if (session == null || !RefreshNavigableGroupMaps(groupId))
            {
                QueueToast("PFV_MapUnavailable".Translate());
                return;
            }
            if (navigableGroupMaps.Count == 1)
            {
                TryEnterGroupMap(session, groupId, navigableGroupMaps[0].uniqueID);
                return;
            }

            var options = new List<FloatMenuOption>();
            foreach (var map in navigableGroupMaps)
            {
                int mapId = map.uniqueID;
                string label = "PFV_GroupMapChoice".Translate(map.Parent.LabelCap, mapId);
                options.Add(new FloatMenuOption(label, () => TryEnterGroupMap(session, groupId, mapId)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void TryEnterGroupMap(Game session, int groupId, int mapId)
        {
            // 菜单打开后编队可能解散、转移所属或卸载地图；只保留 id，并在点击时重新解析。
            if (!ReferenceEquals(session, Current.Game) || !RefreshNavigableGroupMaps(groupId))
            {
                QueueToast("PFV_MapUnavailable".Translate());
                return;
            }
            var map = navigableGroupMaps.Find(candidate => candidate.uniqueID == mapId);
            if (map == null)
            {
                QueueToast("PFV_MapUnavailable".Translate());
                return;
            }

            Find.MainTabsRoot.SetCurrentTab(null);
            SpaceMapGenerator.EnterPocketMap(map);
        }
    }
}
