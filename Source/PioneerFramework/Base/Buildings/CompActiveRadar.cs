using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 主动雷达建筑 Comp 的配置（**[V7]/[E2]**）。挂在建筑 ThingDef 的 comps 列表里。
    /// 与传感器阵列同量纲，但多了**主动**那一半：增益加成 + 开机时的信号增量 + **波束几何**（半锥角/距离上限）。
    /// </summary>
    public class CompProperties_ActiveRadar : CompProperties
    {
        /// <summary>主动增益**加成**（≥0）：有效探测距离倍率 = `1 + 本值`。</summary>
        public float activeGainBonus = 2f;

        /// <summary>主动开机时的信号增量（S_REF 倍数；加在 `SignalModel.ActivePingMultiplier` 基准之上）。</summary>
        public float activeRadiation = 4f;

        /// <summary>波束半锥角（度）——[V7] 三模式（跟踪/手选/扫掠）共用的几何。</summary>
        public float beamHalfAngleDeg = 15f;

        /// <summary>波束距离上限（m）。</summary>
        public float beamRangeMeters = 20_000_000f;

        /// <summary>扫掠角速度（deg/s，physicsTick 轴）：扫掠模式下相位 = `physicsTick × 本值`。
        /// ⚠ 它就是"信息处理速度"在雷达上的化身（以后要改成有限处理速度时，旋钮在这里）。</summary>
        public float scanRateDegPerSec = 6f;

        public CompProperties_ActiveRadar()
        {
            compClass = typeof(CompActiveRadar);
        }
    }

    /// <summary>
    /// **主动雷达建筑（[V7]/[E2]，2026-10-01）** —— 与 `CompSensorArray` **同一个模子**：
    /// **登记**（`PostSpawnSetup` → 所属地图的 `SpaceMapComp`；`PostDeSpawn` 注销）与**被读取**
    /// （`SpaceMapComp.RadarCapability()` 汇总 → `SpaceGameComp` 每**物理步**投影进 `Group.RadarCap`）。
    ///
    /// ⚠ **空壳**：不 Tick、不自己改 Group（pocket map 隐藏时 `Thing.CompTick` 不可靠）。
    /// ⚠ **开机开关不在本 comp 上**——W1 枢纽的**唯一开关**是 `SpaceEntityGroup.ActivePingEnabled`
    ///   （设计稿 §11.2；没装雷达的组也能开，但只有装了才"看得更远"，见 `RadarCapability`）。
    /// ⚠ 波束的**指向**（三模式：跟踪 / 手选方位 / 扫掠）是**组级**状态（多台雷达共用一个外沿），
    ///   本 comp 只提供几何参数；指向状态见 `SpaceEntityGroup` 的雷达节（[E2]/[V3] 落地时接）。
    /// ⚠ 贡献**随毁损而掉**：由投影侧按 `HitPoints / MaxHitPoints` 折算（用户 2026-10-01 裁定）。
    /// </summary>
    public class CompActiveRadar : ThingComp
    {
        public CompProperties_ActiveRadar Props => (CompProperties_ActiveRadar)props;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            parent.Map?.GetComponent<SpaceMapComp>()?.RegisterActiveRadar(parent);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode)
        {
            base.PostDeSpawn(map, mode);
            map?.GetComponent<SpaceMapComp>()?.UnregisterActiveRadar(parent);
        }
    }
}
