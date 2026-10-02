using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 传感器阵列建筑 Comp 的配置（**[V7] 建筑级**）。挂在建筑 ThingDef 的 comps 列表里。
    /// 量纲与部件级 `SensorArrayComponentData` 同口径（`SensorTech` 三分量），只是建筑级一般**大一档**。
    /// </summary>
    public class CompProperties_SensorArray : CompProperties
    {
        /// <summary>被动灵敏度（m）。建筑级默认 5000 km（部件级默认 500 km）。</summary>
        public float passiveRangeMeters = 5_000_000f;

        /// <summary>记忆时长（s，物理秒轴）：观测者的记忆窗口 T（[V3]）。0 = 未设 ⇒ 用 `DetectionCore.DefaultMemorySeconds`。</summary>
        public float memorySeconds = 0f;

        /// <summary>抗压制（无量纲，≥0）。</summary>
        public float ecmResist = 0f;

        /// <summary>抗欺骗（无量纲，≥0）。</summary>
        public float eccmResist = 0f;

        public CompProperties_SensorArray()
        {
            compClass = typeof(CompSensorArray);
        }
    }

    /// <summary>
    /// **传感器阵列建筑（[V7] 建筑级，2026-10-01）** —— 只做两件事：
    /// **登记**（`PostSpawnSetup` 把自己登记进所属地图的 `SpaceMapComp`；`PostDeSpawn` 注销）
    /// 与**被读取**（`SpaceMapComp.SensorArrayTech` 汇总 → `SpaceGameComp` 每**物理步**投影进所属 group）。
    ///
    /// ## 为什么本 comp 不 Tick、也不自己改 Group（设计稿 §5.4 的脊柱）
    /// EW 建筑住在 pocket map，而 **pocket map 隐藏时 `Thing.CompTick` 不可靠**（2026-08-12 实证）。
    /// ⇒ 一切"推动"都放在 `SpaceGameComp` 的物理步里（[N7] 投影链）：
    /// ```
    /// 建筑 comp（本文件：只登记）→ SpaceMapComp（汇总）→ SpaceGameComp 每物理步投影 → Group.BuildingTechBonus
    /// ```
    /// ## 口径（用户 2026-10-01 裁定）
    /// · **归属** = 建筑所在地图所属的那条 group（空间站自己就是一条 group）；
    /// · **贡献随毁损而掉**——由投影侧按 `HitPoints / MaxHitPoints` 折算（本 comp 不记账）；
    /// · 时间量一律走 `physicsTick` 轴（本 comp 不涉及时间，故无字段）。
    /// </summary>
    public class CompSensorArray : ThingComp
    {
        public CompProperties_SensorArray Props => (CompProperties_SensorArray)props;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            // 手写注册表（与工质向量/推进器同纪律）：**不落盘**，由建筑 Spawn 时登记、读档后重建。
            parent.Map?.GetComponent<SpaceMapComp>()?.RegisterSensorArray(parent);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode)
        {
            base.PostDeSpawn(map, mode);
            map?.GetComponent<SpaceMapComp>()?.UnregisterSensorArray(parent);
        }
    }
}
