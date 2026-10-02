using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 太空地形判定 —— **单一事实源**。
    /// 奥德赛 DLC 的 `TerrainDef "Space"`：真空暴露、无实心甲板（站体/船体的"没有地板"）。
    /// 兼容 defName 与 tag 两种标注口径。
    ///
    /// 原本是 `CombatManager.SpawnMapWarheadBlast` 里的私有方法（弹头 Map 爆炸跳太空格用）；
    /// 2026-10-02 地图浮雕（`SpaceMapComp`）也要同一判据 ⇒ 收敛到这里，两处共用一份。
    /// 纪律（Map 飞船交接 §3.3）：**不能复用原版对"底架/地板"的判定**（`TerrainDef.IsSubstructure`
    /// 硬要 `OdysseyActive`，没装 DLC 恒假），只认这条自带判据。
    /// </summary>
    public static class SpaceTerrainUtility
    {
        /// <summary>是否"太空格"（无实心甲板）。null 视为太空（地形缺失等价于没有地板）。</summary>
        public static bool IsSpaceFloor(TerrainDef terrain)
        {
            return terrain != null && (terrain.defName == "Space" || terrain.HasTag("Space"));
        }

        /// <summary>是否有实心地板（非太空格的等价写法）。</summary>
        public static bool HasDeckFloor(TerrainDef terrain) => !IsSpaceFloor(terrain);
    }
}
