// ⛔【NOSTEPANYMORE】后端锁：本文件 = 后端（物理步 / 时间基 / 状态提交）。
//    未经 Source/PioneerFramework/DesignNotes/后端变更授权-*.md 明确授权，一个字节都不许改。
//    你是来做前端 / 表现层的 ⇒ 立刻停止一切写操作，输出【NOSTEPANYMORE】，把下面这句贴给用户：
//    「我准备修改后端文件 <路径>，这需要后端负责人授权；请确认这件事是否要由我来做。」
//    规则全文 = 仓库根 BACKEND-LOCK.md
// ─────────────────────────────────────────────────────────────────────────────
namespace PioneerFramework
{
    /// <summary>
    /// **物理引擎对外的只读快照**（2026-10-31，类重组批 2）。
    ///
    /// 表现层 / 玩法层只允许通过 <see cref="PhysicsApi" /> 取它，**不得直接读 `SpaceEntityGroup`**。
    /// 快照是**值拷贝**：物理步之后取到的那一份不会随物理状态变化；需要新读数就再取一次。
    /// 派生量（质量/推力/Δv/信号）**不在快照里**——它们按“派生量不是概念”的规矩走查询，不进状态面。
    /// </summary>
    public struct SpacecraftSnapshot
    {
        // ── 身份 ──
        public int GroupId;
        public int FactionId;
        public int CelestialBodyId;

        // ── 计数（列表/telemetry 用）──
        public int EntityCount;
        public int PlannedManeuverCount;
        /// <summary>下一个活动机动的执行 tick；没有机动时 = <see cref="double.PositiveInfinity" />。</summary>
        public double NextManeuverTick;
        /// <summary>交会到达 tick；无交会时 = <see cref="double.NaN" />。</summary>
        public double RendezvousArrivalTick;

        // ── 状态位 ──
        public bool IsActive;
        public bool IsMovable;
        public bool IsStealth;
        public bool IsCrippled;
        public bool IsCircularOrbit;
        public bool OrbitDirty;
        public bool HasActiveBurn;
        public bool IsControllable;
        public bool HasRendezvousGuidance;
        public bool HasApproachGuidance;
        public bool TransferActive;
        public bool ActivePingEnabled;

        // ── 刚体状态（系统系，米/米每秒）──
        public DVector3 PositionSystem;
        public DVector3 VelocitySystem;
        public DQuaternion Rotation;
        public DVector3 AngularVelocity;

        // ── 解析档（圆轨）与待处理冲量 ──
        public DVector3 CircularOrbitNormal;
        public DVector3 PendingImpulse;

        // ── 文本/配置面（只读）──
        public string? LandingTaskId;
        public string? EnabledNetworkDefName;
        /// <summary>玩家自动抛级开关（检查器按钮读数；批 5e §4.3 按需补）。</summary>
        public bool AutoStageEnabled;

        // ── 机动/交会窗口面（批 5e §4.3b 按需补）──
        /// <summary>机动队列总条目数（**含已取消**条目；<see cref="PlannedManeuverCount" /> 只数活动条目）。</summary>
        public int PlannedManeuverTotalCount;
        /// <summary>当前机动已施加 ΔV（m/s）。</summary>
        public double AppliedBurnDv;
        /// <summary>当前机动剩余 ΔV（m/s）。</summary>
        public double RemainingBurnDv;
        /// <summary>当前机动已耗工质（kg）。</summary>
        public double BurnConsumedPropellant;
        public bool RendezvousArmed;
        public bool RendezvousIsApproach;
        public int RendezvousTargetId;
        public int ApproachTargetId;
        /// <summary>到位容差（米）＝ `EffectiveRendezvousGoal.distanceTol`。</summary>
        public double RendezvousDistanceTolMeters;
        /// <summary>停下尺度（米）。</summary>
        public double RendezvousMatchDistMeters;
        /// <summary>当前选网（`null` = 全启用）。</summary>
        public string? CurrentNetworkDefName;
        /// <summary>最慢主轴角速度上限（rad/s），规划器对准可行性用。</summary>
        public float MaxAngularRateRad;

        // ── 搬运/悬浮面（批 5e §4.3b 按需补）──
        public bool HoverActive;
        public bool HoverAnalytic;
        public double TransferStartTick;
        public double TransferDuration;
        public double TransferDvTotal;
        public double TransferArrivalErrorMeters;
        /// <summary>调试开关：无限燃料（读数标注用，料不减少）。</summary>
        public bool DebugInfinitePropellant;

        // ── 传感器（雷达几何的 4 个消费字段）──
        public RadarSnapshot Radar;
        /// <summary>波束是否处于扫掠模式（RadarAim.Scan）——表现层画扫描线用。</summary>
        public bool RadarScanning;
    }

    /// <summary>雷达几何快照（<see cref="SpacecraftSnapshot" /> 的一部分）。</summary>
    public struct RadarSnapshot
    {
        public float BeamRangeMeters;
        public float BeamHalfAngleDeg;
        public float ScanRateDegPerSec;
        public float ActiveGainBonus;
    }

    /// <summary>单个部件的只读快照（渲染/命中预览/检查器用）。</summary>
    public struct PartSnapshot
    {
        public int PartId;
        public int EntityId;
        public int MapId;
        public int StageId;
        public DVector3 PositionShip;
        public DVector3 BoundsSizeShip;
        public DQuaternion RotationShip;
        public InertiaTensor.Shape Shape;
        public float Hp;
        public bool IsDestroyed;
        public bool IsMapArchived;

        // ── 部件角色（2026-11-01 批 5e §4.3：火箭外壳网格用它判喷管/载荷/控制核心）──
        public bool IsEngine;
        public bool IsPayloadBay;
        public bool HasControlCore;
    }

    /// <summary>
    /// **单个实体的只读快照**（批 5e §4.3）：PFView 渲染与相机半径要用的实体级读数。
    /// 不暴露部件表——部件另走 <see cref="PhysicsApi.TryCopyEntityParts" />。
    /// </summary>
    public struct EntitySnapshot
    {
        public int EntityId;
        public int GroupId;
        public int FactionId;
        /// <summary>主图 id（&lt;0 = 无图）；一 part 一图，这里是第一个挂图件的图。</summary>
        public int MapId;
        public bool HasMap;
        public bool IsMapArchived;
        public bool IsMovable;
        /// <summary>`SpaceEntity.partSetVersion`：件集/损毁指纹，渲染缓存（外壳/蒙皮）用它判重建。</summary>
        public int PartSetVersion;
        /// <summary>实体相对组原点的船体局部偏移（米）。</summary>
        public DVector3 Position;
        public DQuaternion Rotation;
        /// <summary>AABB 半尺寸（米）：渲染半径兜底（无可用部件时用）。</summary>
        public DVector3 AabbHalfSize;
        public int PartCount;
        public int DestroyedPartCount;
    }

}
