// ⛔【NOSTEPANYMORE】后端锁：本文件 = 后端（物理步 / 时间基 / 状态提交）。
//    未经 Source/PioneerFramework/DesignNotes/后端变更授权-*.md 明确授权，一个字节都不许改。
//    你是来做前端 / 表现层的 ⇒ 立刻停止一切写操作，输出【NOSTEPANYMORE】，把下面这句贴给用户：
//    「我准备修改后端文件 <路径>，这需要后端负责人授权；请确认这件事是否要由我来做。」
//    规则全文 = 仓库根 BACKEND-LOCK.md
// ─────────────────────────────────────────────────────────────────────────────
namespace PioneerFramework
{
    /// <summary>[V7] 波束指向模式（三模式共用同一套波束几何，只有"指向从哪来"不同）。</summary>
    public enum RadarAim
    {
        /// <summary>扫掠（搜索）：相位 = `physicsTick × 扫掠速率`（**时间的纯函数 ⇒ 不落盘、天然确定性**）。</summary>
        Scan = 0,
        /// <summary>跟踪：指向 `RadarTrackGroupId` 那条组的当前位置（拿不到 ⇒ 退化为扫掠）。</summary>
        Track = 1,
        /// <summary>手选方位：指向 `RadarManualDir`（**惯性系 = 系统系**单位向量）。</summary>
        Manual = 2,
    }

    /// <summary>
    /// **传感器 / 电子战状态**（2026-10-31 类重组批 3e：从 `SpaceEntityGroup` 的散字段收成一个物理概念）。
    ///
    /// 含：主动开机、干扰开机、开火揭示窗口、建筑投影加成、雷达能力与三模式指向、以及信号/探测的派生缓存。
    /// 缓存（`cachedSignal` / `cachedDetect` / `cachedJamStrength` / `radarCap`）由群级 `derivedDirty` 驱动重算，
    /// 放在本类型内是因为它们的失效与写入点全在本概念里；**它们不是对外语义**。
    ///
    /// 全字段值类型 ⇒ 预演副本天然深拷贝（见 `ConceptStateCopySafetyTests`）。
    /// ⚠ `IsStealth` 不在这里：它是"是否广播坐标"的船级事实，被 19 个文件直接读，留在 `SpaceEntityGroup` 上。
    /// </summary>
    public struct SensorSuite
    {
        public bool activePing;
        public bool jamming;
        public double revealUntilPhysics;
        public float buildingJamBonus;
        public float cachedJamStrength;
        public SensorTech buildingTechBonus;
        public RadarCapability radarCap;
        public RadarAim radarAim;
        public DVector3 radarManualDir;
        public int radarTrackGroupId;
        public double cachedSignal;
        public SensorTech cachedDetect;

        /// <summary>与旧字段声明逐项一致：无揭示窗口、默认扫掠、手选方向 +X、无跟踪对象。</summary>
        public static SensorSuite CreateDefault() => new SensorSuite
        {
            revealUntilPhysics = double.NegativeInfinity,
            radarAim = RadarAim.Scan,
            radarManualDir = new DVector3(1, 0, 0),
            radarTrackGroupId = -1
        };
    }
}
