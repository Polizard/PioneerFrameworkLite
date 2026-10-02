using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **干扰机建筑 Comp 的配置（[E3] 建筑级）**。挂在建筑 ThingDef 的 comps 列表里。
    /// 量纲与部件级 `JammerComponentData` 同口径（无量纲干扰效力），建筑级一般**大一档**。
    /// </summary>
    public class CompProperties_Jammer : CompProperties
    {
        /// <summary>干扰效力（无量纲，≥0）：1.0 = 在 `DetectionCore.JamRefMeters` 处把有效信号砍半。
        /// 建筑级默认 2.0（部件级默认 1.0）。</summary>
        public float jamStrength = 2f;

        public CompProperties_Jammer()
        {
            compClass = typeof(CompJammer);
        }
    }

    /// <summary>
    /// **干扰机建筑（[E3] 建筑级，2026-10-02）** —— 与传感器阵列同模子，只做两件事：
    /// **登记**（`PostSpawnSetup` 把自己登记进所属地图的 `SpaceMapComp`；`PostDeSpawn` 注销）
    /// 与**被读取**（`SpaceMapComp.JammerStrength` 汇总 → `SpaceGameComp` 每**物理步**投影进所属 group）。
    ///
    /// ```
    /// 建筑 comp（本文件：只登记）→ SpaceMapComp（汇总，按 HP 折算）→ SpaceGameComp 每物理步投影 → Group.BuildingJamBonus
    /// ```
    /// ## 口径（与传感器阵列同纪律）
    /// · **归属** = 建筑所在地图所属的那条 group（空间站自己就是一条 group）；
    /// · **贡献随毁损而掉**——由投影侧按 `HitPoints / MaxHitPoints` 折算（本 comp 不记账）；
    /// · **开不开机是群级开关**（`SpaceEntityGroup.JammingEnabled`，与 `ActivePingEnabled` 同款）——
    ///   建筑只提供"能力"，开关与暴露代价留在群上（W1：干扰机必须辐射才能干扰）；
    /// · 本 comp **不 Tick、也不自己改 Group**（pocket map 隐藏时 `Thing.CompTick` 不可靠，设计稿 §5.4）。
    /// </summary>
    public class CompJammer : ThingComp
    {
        public CompProperties_Jammer Props => (CompProperties_Jammer)props;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            // 手写注册表（与传感器阵列/工质向量同纪律）：**不落盘**，由建筑 Spawn 时登记、读档后重建。
            parent.Map?.GetComponent<SpaceMapComp>()?.RegisterJammer(parent);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode)
        {
            base.PostDeSpawn(map, mode);
            map?.GetComponent<SpaceMapComp>()?.UnregisterJammer(parent);
        }
    }
}
