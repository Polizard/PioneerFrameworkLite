using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **Map 质量的置脏挂钩**（2026-10-17 用户裁定：建筑生成/拆除、工质加注/消耗、屋顶覆盖/拆除
    /// 都要反映到 Map 质量上；未知质量按 1 kg）。
    ///
    /// 事件源与原版 `MapEvents` 同点（`ThingSpawned` / `ThingDespawned` / `RoofChanged`），
    /// 与 `MapComponent_ReliefDirtyWatcher` 并列：本组件只做**增量记账**（O(1)/事件），
    /// 全量重算只在读档/首次绑定（`SpaceMapComp.RecomputeStructureMass`）发生。
    /// 工质部分由 `SpaceMapComp.TotalPropellantMass` 直接带出，无需事件。
    /// </summary>
    public class MapComponent_MapMassWatcher : MapComponent
    {
        public MapComponent_MapMassWatcher(Map map) : base(map)
        {
            map.events.ThingSpawned += OnThingSpawned;
            map.events.ThingDespawned += OnThingDespawned;
            map.events.RoofChanged += OnRoofChanged;
        }

        private void OnThingSpawned(Thing thing) => SpaceMapComp.NotifyBuildingSpawned(map, thing);
        private void OnThingDespawned(Thing thing) => SpaceMapComp.NotifyBuildingDespawned(map, thing);
        private void OnRoofChanged(IntVec3 cell) => SpaceMapComp.NotifyRoofChanged(map, cell);
    }
}
