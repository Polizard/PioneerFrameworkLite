// ⛔【NOSTEPANYMORE】后端锁：本文件 = 后端（物理步 / 时间基 / 状态提交）。
//    未经 Source/PioneerFramework/DesignNotes/后端变更授权-*.md 明确授权，一个字节都不许改。
//    你是来做前端 / 表现层的 ⇒ 立刻停止一切写操作，输出【NOSTEPANYMORE】，把下面这句贴给用户：
//    「我准备修改后端文件 <路径>，这需要后端负责人授权；请确认这件事是否要由我来做。」
//    规则全文 = 仓库根 BACKEND-LOCK.md
// ─────────────────────────────────────────────────────────────────────────────
using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// **推进网络选项快照**（2026-11-01 批 5e §4.2）：PFView 网络选择器一行。
    /// 口径与旧 `MainTabWindow_PFView.Inspector.DrawNetworkSelectorButton` 逐项一致：
    /// 只收 `ṁ &gt; 0` 的网、同工质去重（首见者胜）、`AvailableKg` 是**该网工质现存料**。
    /// </summary>
    public struct NetworkOptionSnapshot
    {
        /// <summary>工质 `defName`（玩家选择后回填 `SetBurnNetwork` 的键）。</summary>
        public string DefName;
        public string Label;
        public bool IsRcs;
        /// <summary>加权排气速度 ve（m/s）。</summary>
        public float Ve;
        /// <summary>总质量流率 ṁ（kg/s）。</summary>
        public float MassFlow;
        /// <summary>该网工质现存料（kg；质量一律 double，见 `IPropulsionSource` 裁定）。</summary>
        public double AvailableKg;
    }

    /// <summary>
    /// **舱室件快照**（批 5e §4.2）：检查器舱室列表一行；`PartId` 回填给 `PhysicsCommands.EnterRoomMap`。
    /// </summary>
    public struct RoomPartSnapshot
    {
        public int PartId;
        public int EntityId;
        /// <summary>舱室 `RoomTypeDef.label`；未配置/Def 缺失为 null（UI 自己兜底文案）。</summary>
        public string? RoomLabel;
    }

    /// <summary>编队成员遥测（批 5e §4.2）：一个实体一行。</summary>
    public struct TelemetryMemberSnapshot
    {
        public int EntityId;
        public int PartCount;
        public int DestroyedPartCount;
    }

    /// <summary>编队推进网遥测（批 5e §4.2）：一条网一行；只有成网（ṁ &gt; 0）的条目会出现。</summary>
    public struct TelemetryNetworkSnapshot
    {
        public string Name;
        public double MassKg;
        public bool EnvironmentDependent;
        public double EnduranceSeconds;
        /// <summary>质量流率 ṁ（kg/s）——搬运窗口读数用（批 5e §4.3b）。</summary>
        public double MassFlow;
    }

    /// <summary>
    /// **编队遥测快照**（批 5e §4.2）：`PhysicsQueries.TryCollectTelemetry` 的输出。
    ///
    /// 分工：组级状态/机动/交会仍走 <see cref="SpacecraftSnapshot" />；这里只放**聚合量**
    /// （部件健康、图装载情况、Δv/续航、成员与网络明细）——旧 `PFViewFleetTelemetry` 的数据部分逐项对照搬迁。
    /// **分配口径**：每次采集 new 一个（含成员/网络 List），只在 UI 刷新频率调用，不在物理步内；
    /// 列表对外只读，内部由查询填充。
    /// </summary>
    public sealed class TelemetrySnapshot
    {
        private readonly List<TelemetryMemberSnapshot> members = new();
        private readonly List<TelemetryNetworkSnapshot> networks = new();

        public int GroupId { get; internal set; }
        public IReadOnlyList<TelemetryMemberSnapshot> Members => members;
        public IReadOnlyList<TelemetryNetworkSnapshot> Networks => networks;
        public int MemberCount => members.Count;

        /// <summary>组分内实体数 &gt; 1 时 UI 才显示成员明细（与旧面板同一判据）。</summary>
        public bool HasPropulsion { get; internal set; }
        public double PropellantMass { get; internal set; }
        public double PropellantCapacity { get; internal set; }
        public float ReferenceDeltaV { get; internal set; }
        /// <summary>任一条网是环境相关（吸气/包线）⇒ UI 报续航而不是 Δv。</summary>
        public bool EnvironmentDependent { get; internal set; }
        /// <summary>环境相关网里最短的全推续航秒；无环境相关网时 = <see cref="double.PositiveInfinity" />。</summary>
        public double ShortestEndurance { get; internal set; } = double.PositiveInfinity;

        public int DamagedBuildings { get; internal set; }
        public int DestroyedParts { get; internal set; }
        /// <summary>未毁、非无敌、HP 有限的部件里最小的 HP；没有可用读数 = null。</summary>
        public float? MinimumPartHp { get; internal set; }

        /// <summary>有一张可进入的图（主图 = 某个实体的 `MapId`）。</summary>
        public bool HasMap { get; internal set; }
        /// <summary>有图归档/卸载（读档后图未加载）⇒ UI 提示未加载。</summary>
        public bool UnloadedMaps { get; internal set; }

        internal void AddMember(in TelemetryMemberSnapshot member) => members.Add(member);
        internal void AddNetwork(in TelemetryNetworkSnapshot network) => networks.Add(network);
        internal void SortMembers() => members.Sort((a, b) => a.EntityId.CompareTo(b.EntityId));
    }

    /// <summary>
    /// **机动队列条目快照**（批 5e §4.3b）：ManeuverQueueWindow 一行。
    /// 口径与旧窗口逐项一致：分量是规划值（m/s）、`ExecuteTick` 是燃烧中点、`UseNetworkDefNames` null = 全启用。
    /// </summary>
    public struct PlannedManeuverSnapshot
    {
        public int Index;
        public bool Active;
        public int ExecuteTick;
        public float RadialDv;
        public float TangentialDv;
        public float NormalDv;
        /// <summary>该机动启用的工质 `defName`；null = 全启用（与 `PlannedManeuver.useNetworks` 同义）。</summary>
        public string[]? UseNetworkDefNames;
        /// <summary>UI 直显文本：`全启用` 或 `A+B`。</summary>
        public string NetworkLabel;
        /// <summary>计划意图（目标条件）的可读描述；无意图 = null。</summary>
        public string? IntentDescription;
    }

    /// <summary>单网络燃烧估算（<see cref="BurnQuote" /> 的一行）。</summary>
    public struct BurnNetworkQuote
    {
        public string? PropellantLabel;
        public bool IsReserve;
        public bool Feasible;
        public double AvailableKg;
        public double RequiredKg;
    }

    /// <summary>
    /// **一次机动的燃烧估算**（批 5e §4.3b）：ManeuverQueueWindow 条目与规划器区制判断用。
    /// 由 `PhysicsQueries.TryBuildBurnQuote` 现算；分配口径同遥测（UI 刷新频率，不进物理步）。
    /// </summary>
    public sealed class BurnQuote
    {
        private readonly List<BurnNetworkQuote> networks = new();
        public IReadOnlyList<BurnNetworkQuote> Networks => networks;
        /// <summary>是否有可参与机动的网络（旧窗口用它区分"未启用任何工质网络"）。</summary>
        public bool HasNetworks;
        public bool Feasible;
        public float RequiredPropellant;
        public float BurnSeconds;
        public float MaxDeltaV;

        internal void AddNetwork(in BurnNetworkQuote network) => networks.Add(network);
    }
}
