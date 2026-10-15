using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 地图浮雕分类的**置脏挂钩**（2026-10-15，"反射清理"批 A-8：替代原先挂在
    /// `TerrainGrid.SetTerrain` / `TerrainGrid.RemoveTopLayer` / `RoofGrid.SetRoof` /
    /// `Thing.SpawnSetup` / `Thing.DeSpawn` 上的 **5 处 Harmony 补丁**）。
    ///
    /// 机制：`Map.FillComponents` 会自动实例化**所有**非抽象 `MapComponent` 子类（新档 + 每次读档，
    /// 已存在则跳过、缺失则补齐 ⇒ 老存档也会自动获得本组件），构造时订阅 `MapEvents` 的
    /// 地形/屋顶/物生/物灭四事件——**事件源与原先补的那 5 个方法同点触发**
    ///（`TerrainGrid.DoTerrainChangedEffects` / `RoofGrid.SetRoof` / `Thing.SpawnSetup` / `Thing.DeSpawn` 尾部），
    /// 且两条"Unsafe"直写路径（`RemoveGravshipTerrainUnsafe` / `RemoveRoofUnsafe`）原本就不经过
    /// 被补的方法 ⇒ **覆盖等价**（2026-10-15 逐条核对）。
    ///
    /// 订阅位置照抄原版同类：`Verse.PathFinderMapData` 也是在**构造里**订阅 `map.events`（:43/:50）。
    ///
    /// 事件里**只置一个脏标记**（`SpaceMapComp.NotifyReliefDirty`）——重建推迟到 PFView 取用时做，
    /// 因为这些都是建造/物理流程上下文里的事件，绝不能在事件里扫整张图。
    /// </summary>
    public class MapComponent_ReliefDirtyWatcher : MapComponent
    {
        public MapComponent_ReliefDirtyWatcher(Map map) : base(map)
        {
            map.events.TerrainChanged += OnTerrainChanged;
            map.events.RoofChanged += OnRoofChanged;
            map.events.ThingSpawned += OnThingSpawned;
            map.events.ThingDespawned += OnThingDespawned;
        }

        // 建筑出现/消失 = 房间边界与"结构梁"格的变化源。
        // 只认 `is Building`（与非建筑无关；与旧补丁同一过滤条件）——非建筑事件只多一次类型判断。
        private void OnTerrainChanged(IntVec3 cell) => SpaceMapComp.NotifyReliefDirty(map);
        private void OnRoofChanged(IntVec3 cell) => SpaceMapComp.NotifyReliefDirty(map);

        private void OnThingSpawned(Thing thing)
        {
            if (thing is Building)
                SpaceMapComp.NotifyReliefDirty(map);
        }

        private void OnThingDespawned(Thing thing)
        {
            if (thing is Building)
                SpaceMapComp.NotifyReliefDirty(map);
        }
    }
}
