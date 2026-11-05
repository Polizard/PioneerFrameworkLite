using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **船载建筑的玩家向 UI 公用口径**（[E2] 交互装门，2026-10-11）：归属链（建筑 → 所属编队）与
    /// "这是不是玩家的东西"的**唯一判据** —— 雷达开机/波束模式 Gizmo 与干扰开机 Gizmo 共用。
    ///
    /// ⚠⚠ **判"这东西属于谁"要看归属链（船/编队），别看被挂部件自己的 `Thing.Faction`**：
    /// 调试场景直生（`GenSpawn.Spawn`）的建筑**没有派系**（整条 spawn 路径都不设 Faction），
    /// 而真实玩法里建筑挂的也正是这张图的主人（空间站 / 带图船）—— 2026-10-07 大气线在操舵台 Gizmo 上
    /// 真踩过这个坑（玩家闸挂在 `parent.Faction != Faction.OfPlayer` 之后 ⇒ 调试场景里按钮永远不出现）。
    /// ⇒ 本类把那条修法收成一处：**船链优先；船链取不到才退回建筑自己的派系**（旧口径，保真）。
    /// </summary>
    public static class ShipBuildingUI
    {
        /// <summary>
        /// 建筑 → 所属编队（唯一归属链：地图 `SpaceMapComp.ownerEntityId` → 实体 → `entity.GroupId` → group）。
        /// 任一环缺失 ⇒ null（调用方自己决定"没有归属"时怎么办）。
        /// </summary>
        public static SpaceEntityGroup? GetOwnerGroup(Thing building)   // 契约改真（2026-10-15）：查不到返回 null（原 null! 是谎）
        {
            // ⚠ `null!`：返回类型保持非空（与全库同款写法，调用方自查）——"取不到 ⇒ null"是本方法的**正当结果**，
            //   不给它加可空标注（那会沿着调用链一路传染）。见 GameComponentRef / GroupPropulsionAggregator 的先例。
            if (building == null || building.Map == null) return null;
            var mapComp = building.Map.GetComponent<SpaceMapComp>();
            if (mapComp == null || mapComp.ownerEntityId < 0) return null;

            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) return null;
            var entity = sg.GetEntityById(mapComp.ownerEntityId);
            if (entity == null || entity.GroupId < 0) return null;
            return sg.GetGroupById(entity.GroupId);
        }

        /// <summary>这条编队是不是玩家的（按 `Faction.OfPlayer.loadID`；玩家派系解析不到 ⇒ false）。</summary>
        public static bool IsPlayerGroup(SpaceEntityGroup group)
        {
            var player = Faction.OfPlayer;
            return player != null && group != null && group.FactionId == player.loadID;
        }

        /// <summary>Gizmo 的玩家闸：**船链优先**（`ownerGroup` 由调用方取一次、可复用）；船链取不到 ⇒ 退回建筑自己的派系。</summary>
        public static bool IsPlayerOwned(Thing building, SpaceEntityGroup ownerGroup)
        {
            if (ownerGroup != null) return IsPlayerGroup(ownerGroup);
            return building != null && building.Faction == Faction.OfPlayer;
        }
    }
}
