using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using PioneerFramework.Components;

namespace PioneerFramework
{
    /// <summary>
    /// 轨道状态（位置+速度+朝向），用于缓存和计算。
    /// 位置/速度使用 DVector3（双精度）以避免累积误差。
    /// </summary>
    public struct OrbitState : IExposable
    {
        /// <summary>**系统系**位置（批6 S3-3c 第三片：类型承载系）—— 这是**权威字段**。
        /// ⚠ 读它要进裸三分量的地方（老的物理/渲染工具）写 `.v`：那是**显式跨界**，看得见、可 grep。</summary>
        public SystemVec3 positionSystem;
        /// <summary>**系统系**速度。语义同 <see cref="positionSystem" />。</summary>
        public SystemVec3 velocitySystem;
        /// <summary>姿态（B2：物理/姿态全程 double；Unity Quaternion 只在渲染/UI 边界转换）。</summary>
        public DQuaternion rotation;

        // 批6 S3-3c 第三片收尾：**过渡桥已删除**（同名 `position`/`velocity` 两个 DVector3 属性）。
        // 桥的出口条件是"未迁移读点 = 0"，2026-09-30 达成（109 → 0，逐片迁移 + 棘轮判据看着它降）；
        // 由 `tools/check-frame-crossing.ps1`（闸门第 11 步）的棘轮守着 —— 它现在只认**带系字段**。
        // 要取回那座桥：见 `_backups/20260930-1918-post-S3-orbitstate-bridge-310248792982ABA4/`。

        public void ExposeData()
        {
            SystemVec3.Scribe("position", ref positionSystem);
            SystemVec3.Scribe("velocity", ref velocitySystem);
            DQuaternion.Scribe("rotation", ref rotation);
        }
    }

    /// <summary>姿态请求用哪根船体轴（2026-10-27：每步只解析一个请求、只写一次角速度）。</summary>
    public enum AttitudeAxis : byte { MainThrust = 0, Forward = 1, Up = 2 }

    public partial class SpaceEntityGroup : ISpatialEntity, IExposable
    {
        public string LandingTaskId = string.Empty;
        public int GroupId;
        public int FactionId;

        public bool IsActive = true;
        public bool IsMovable = true;
        public bool IsCollidable = true;
        public bool HasGravityWell ;
        /// <summary>B5：关键件损毁 ⇒ 整船失能（不立即解体）：禁止新点火/自动机动；碰撞与漂移照常。</summary>
        public bool IsCrippled ;

        /// <summary>
        /// 匿踪闸门（[V1]）：false = **公开坐标**（应答机语义，对所有阵营可见、不做任何探测计算）；
        /// true = 走雷达探测，只有在某阵营的认知层里被判定可见才出现。
        ///
        /// **默认值按"是否自报坐标"定**（[N3] 裁定）：飞船 / 空间站 `false`（应答机广播位置），
        /// **导弹与弹头 `true`**（不广播，只能被探测到）——设置点在 `CombatManager` 的两个弹体工厂。
        /// 这不是"隐蔽"而是"是否广播"：不匿踪的船**开雷达也不会更暴露**（本来就公开），
        /// 而匿踪的船一旦主动辐射就立刻可被探测（辐射与暴露的对称性，[E1]）。
        ///
        /// 准入判据（§1.8 [O5]）：它是"关于这艘船的客观事实"（是否在广播）⇒ 属真值层。
        /// ⚠ **目前尚无消费方**——认知层 [V3] 才读它，这是地基步骤的正常状态。
        /// </summary>
        public bool IsStealth ;

        /// <summary>
        /// 侦测技术三分量（[V1] / W2）：灵敏度 / 抗压制 / 抗欺骗（见 `SensorTech`）。
        ///
        /// **字段名与类型名 `SensorTech` 刻意不同**——同名会让"类内引用该类型"变成字段引用
        /// （`SensorTech x = ...` 报 CS0119），是个会埋很久的坑。取 `DetectTech`（＝侦测技术）避开。
        ///
        /// **来源（[V7]，2026-09-24 起）**：**派生量** —— 由所属各实体的部件聚合
        /// （`ShipStats.sensorRange` / `ecmResist` / `eccmResist`）求和得到，与 <see cref="SignalStrength"/>
        /// **共用一个缓存、同一批失效点**（部件集变化 ⇒ `SpaceEntity.InvalidateStats` ⇒ 本组失效 ✓）。
        ///
        /// ⚠ **不落盘**（序列化已删）：派生量存进去只会在读档时带出一份陈旧副本，
        /// 而"陈旧"正是本库反复踩的坑（`SignalStrength` 那批失效点注释、AVM:824 的派生点清单）。
        /// 原来的"投影写入"形态（[N7] 的建筑级通道）留给 EW **建筑**；**部件级**不需要它——
        /// 部件聚合链是既有的、每帧不需要跑的那种（读触发重算）。
        ///
        /// 消费者：[V3] 认知层（`EffectiveDetectRange` / 抗压制 / 抗欺骗三个判据输入）；
        /// 探针：世界视图面板、`VisibilityProbe` 快照、`FrameworkSelfTest` 的"三分量"项。
        /// </summary>
        public SensorTech DetectTech
        {
            get
            {
                if (derivedDirty) RecomputeDerived();
                return cachedDetect;
            }
        }

        private bool activePing;
        /// <summary>
        /// 主动探测开关（[V1] / [E2]）：`true` = 雷达开机，`false`（默认）= 被动。
        ///
        /// **这是 W1 枢纽（辐射与暴露的对称性）在 Group 上的唯一开关**，两个后果同时发生：
        /// ①自己的 `SignalStrength` 升高 ⇒ **更早被别人发现**（开机即暴露）；
        /// ②有效探测距离提高 ⇒ 看得更远。EW 最经典的取舍。
        ///
        /// **刻意做成属性而非 public 字段**（与 `IsActive` / `IsCollidable` / `HasActiveBurn` 那批不同）：
        /// 它的翻转必须让派生信号失效，而属性 setter 是**唯一写入点**、不可能被绕过。
        /// （对照教训：`HasActiveBurn` 是 public 字段，失效钩子只能挂在 `StartBurn`/`EndBurn` 两处，
        ///  将来谁在别处直接赋值就会静默过期——新字段有机会做对就做对。）
        ///
        /// ⚠ 本步**不建 UI 开关**（Gizmo 属 [E2]，P4），所以除探针外永远为默认值。
        /// </summary>
        public bool ActivePingEnabled
        {
            get => activePing;
            set
            {
                if (activePing == value) return;
                activePing = value;
                derivedDirty = true;   // 派生量（信号 + DetectTech 视角）失效
            }
        }

        // ── [E3]/S4 干扰与开火揭示（2026-10-02）──
        private bool jamming;
        /// <summary>**干扰开机**（[E3]，S4）：与 `ActivePingEnabled` 同款属性（翻转即让派生信号失效）。
        /// 语义：干扰机必须辐射才能干扰 ⇒ 开机后自己也会变响（`SignalModel.JamMultiplier`）。</summary>
        public bool JammingEnabled
        {
            get => jamming;
            set
            {
                if (jamming == value) return;
                jamming = value;
                derivedDirty = true;
            }
        }

        /// <summary>**开火揭示窗口的截止时刻**（物理秒轴；`[N6]`/S4）。⚠ 它的翻转为**时间流逝**而非 setter
        /// ⇒ 不能只靠失效钩子：`SignalStrength` 每次读时检查窗口是否已过（[V8] 登记的 C3 教训，就在这里用上）。</summary>
        private double revealUntilPhysics = double.NegativeInfinity;
        /// <summary>此刻是否处于开火揭示窗口内（时间的函数）。
        /// ⚠ **只在窗口被激活过之后才读时钟**：`TimeScaleManager.GetPhysicsTickSafe()` 会进
        /// `Verse.Find.TickManager`，而那个 getter **无 Game 时直接 NRE**（与 `GetAabbSystem` 同一族）
        /// ⇒ 无条件读钟会让"离线读 `SignalStrength`/`DetectTech`"整条路炸掉（本轮集成测试实测）。
        /// 短路还顺手省掉常态下的时钟调用（绝大多数群从未开过火）。</summary>
        public bool RevealedByFire
            => revealUntilPhysics > 0.0 && TimeScaleManager.GetPhysicsTickSafe() < revealUntilPhysics;
        /// <summary>**开火揭示**（[N6]）：**非导弹**开火时由 `CombatManager.FireWeapon` 调用（导弹发射器开火不计暴露）。</summary>
        public void NotifyWeaponFire(double nowPhysics)
        {
            revealUntilPhysics = nowPhysics + SignalModel.RevealWindowSeconds;
            derivedDirty = true;
        }

        /// <summary>**建筑级干扰贡献**（[E3]，与 `BuildingTechBonus` 同一条投影链）：每物理步从地图投影写入，
        /// 已按毁损折算。与部件级相加。**变了才置脏**（投影每步都写）、**不落盘**（派生量）。</summary>
        private float buildingJamBonus;
        public float BuildingJamBonus
        {
            get => buildingJamBonus;
            set
            {
                if (value == buildingJamBonus) return;
                buildingJamBonus = value;
                derivedDirty = true;
            }
        }

        /// <summary>干扰效力（无量纲）：各实体部件聚合（`JammerComponentData`）之和 **+ 建筑级投影**。走失效钩子缓存。</summary>
        private float cachedJamStrength;
        public float JamStrength
        {
            get
            {
                if (derivedDirty) RecomputeDerived();
                return cachedJamStrength;
            }
        }

        // ── 建筑级侦测贡献（[V7]/[N7] 投影写入口，2026-10-01）──
        private SensorTech buildingTechBonus;

        /// <summary>
        /// **建筑级侦测贡献**（[V7]，[N7] 投影链）：由 `SpaceGameComp` **每物理步**从本组所属地图的
        /// `SpaceMapComp` 投影写入（= 传感器阵列建筑的合计，已按毁损折算；归属 = 地图所属的这条 group）。
        /// 与 <see cref="DetectTech"/> 的**部件级聚合**相加 —— 即下文那句"投影写入形态（[N7]）留给 EW 建筑"的落地。
        ///
        /// ⚠ 写入方**每物理步都写**（哪怕值没变）⇒ setter 必须"**变了才置脏**"，否则每步都让派生缓存失效。
        /// ⚠ 不落盘：派生量只由"部件聚合 + 本投影"两处决定，存下去只会带出陈旧副本（同 `DetectTech` 的理由）。
        /// </summary>
        public SensorTech BuildingTechBonus
        {
            get => buildingTechBonus;
            set
            {
                if (value.sensitivityMeters == buildingTechBonus.sensitivityMeters
                    && value.memorySeconds == buildingTechBonus.memorySeconds
                    && value.ecmResist == buildingTechBonus.ecmResist
                    && value.eccmResist == buildingTechBonus.eccmResist)
                    return;
                buildingTechBonus = value;
                derivedDirty = true;   // 与 ActivePingEnabled 同一条失效路（SignalStrength/DetectTech 共用缓存）
            }
        }

        // ── 主动雷达能力（[V7]/[E2] 投影写入口，2026-10-01）──
        private RadarCapability radarCap;

        /// <summary>
        /// **主动雷达能力**（[V7]/[E2]）：由 `SpaceGameComp` **每物理步**从本组所属地图的 `SpaceMapComp`
        /// 投影写入（= 主动雷达建筑的合计，已按毁损折算）。语义见 <see cref="RadarCapability" />：
        /// 增益/辐射可加、波束几何取最大；**开机开关是 <see cref="ActivePingEnabled" />**（W1 枢纽，不在本结构里）。
        ///
        /// ⚠ 与 `BuildingTechBonus` 同一纪律：**变了才置脏**（投影每步都写）；**不落盘**（派生量）。
        /// ⚠ **波束的指向**（三模式：跟踪 / 手选方位 / 扫掠）是**组级状态**，留给 [E2]/[V3]（本步只做能力面）。
        /// </summary>
        public RadarCapability RadarCap
        {
            get => radarCap;
            set
            {
                if (value.activeGainBonus == radarCap.activeGainBonus
                    && value.activeRadiation == radarCap.activeRadiation
                    && value.beamHalfAngleDeg == radarCap.beamHalfAngleDeg
                    && value.beamRangeMeters == radarCap.beamRangeMeters)
                    return;
                radarCap = value;
                derivedDirty = true;   // 与 BuildingTechBonus 同一条失效路
            }
        }

        // ── [V7] 波束指向（三模式，2026-10-01 用户裁定"三者我都要"）──
        /// <summary>[V7] 波束指向模式（三模式共用同一套波束几何，只有"指向从哪来"不同）。</summary>
        public enum RadarAim
        {
            /// <summary>扫掠（搜索）：相位 = `physicsTick × 扫掠速率`（**时间的纯函数 ⇒ 不落盘、天然确定性**）。</summary>
            Scan = 0,
            /// <summary>跟踪：指向 <see cref="RadarTrackGroupId" /> 那条组的当前位置（拿不到 ⇒ 退化为扫掠）。</summary>
            Track = 1,
            /// <summary>手选方位：指向 <see cref="RadarManualDir" />（**惯性系 = 系统系**单位向量）。</summary>
            Manual = 2,
        }

        private RadarAim radarAim = RadarAim.Scan;
        private DVector3 radarManualDir = new(1, 0, 0);
        private int radarTrackGroupId = -1;

        /// <summary>波束指向模式（默认**扫掠**；AI 一旦指定交战对象由 [V10] 转跟踪 —— 与"选择"管道同拍）。</summary>
        public RadarAim RadarAimMode { get => radarAim; set => radarAim = value; }
        /// <summary>手选方位（**惯性系 = 系统系**单位向量；设置时自动归一）。</summary>
        public DVector3 RadarManualDir { get => radarManualDir; set => radarManualDir = value.normalized; }
        /// <summary>跟踪对象（组 id；-1 = 无 ⇒ 退化为扫掠）。⚠ 接触层 [V3] 落地后改指**接触身份**。</summary>
        public int RadarTrackGroupId { get => radarTrackGroupId; set => radarTrackGroupId = value; }

        /// <summary>
        /// **当前波束指向**（惯性系 = 系统系单位向量）。三模式共用一个口：
        /// 扫掠 = 赤道面（pole +Y）内按相位转圈；跟踪 = 指向目标组当前位置；手选 = 存的那个方向。
        /// ⚠ 跟踪拿不到目标（无组 / 已毁 / 零距离）时**退化为扫掠** —— 绝不返回零向量（零向量在消费侧是无声的坑）。
        /// </summary>
        public DVector3 RadarBoresight(double physicsTick)
        {
            if (radarAim == RadarAim.Manual && radarManualDir.sqrMagnitude > 1e-12)
                return radarManualDir.normalized;
            if (radarAim == RadarAim.Track && radarTrackGroupId >= 0)
            {
                var sg = Current.Game?.GetComponent<SpaceGameComp>();
                var tgt = sg?.GetGroupById(radarTrackGroupId);
                DVector3 self = CurrentOrbit.positionSystem.v;     // OrbitState 是 struct ⇒ 不判空（零值由下面的 ≥1e-12 兜住）
                if (tgt != null)
                {
                    DVector3 d = tgt.CurrentOrbit.positionSystem.v - self;
                    if (d.sqrMagnitude > 1e-12) return d.normalized;
                }
            }
            return EquatorialDir(ScanPhaseRad(physicsTick));   // 扫掠 = 默认，也是跟踪失败时的退化
        }

        /// <summary>**扫掠相位**（rad）= `physicsTick × 扫掠速率` 取模 2π —— 时间的**纯函数**
        ///（所以相位不用落盘、天然确定性；"信息处理速度"以后就改速率那一格）。</summary>
        public double ScanPhaseRad(double physicsTick)
        {
            double rateDeg = radarCap.scanRateDegPerSec > 0f ? radarCap.scanRateDegPerSec : 6f;
            double p = physicsTick * rateDeg * (System.Math.PI / 180.0) % (2.0 * System.Math.PI);
            return p < 0.0 ? p + 2.0 * System.Math.PI : p;
        }

        /// <summary>赤道面（pole +Y）内的单位方向：相位从 +X 起、绕 +Y 顺行。</summary>
        private static DVector3 EquatorialDir(double phaseRad)
            => new(System.Math.Cos(phaseRad), 0.0, System.Math.Sin(phaseRad));

        /// <summary>
        /// **波束判据**（[V7]，消费者 = [V3] 认知层）：目标（系统系位置）是否落在**本组波束内**
        /// = 距离 ≤ `beamRangeMeters` **且** 与指向的夹角 ≤ `beamHalfAngleDeg`。
        /// ⚠ 纯几何谓词：**不判"发现"**（归 [V3]），也**不做距离粗筛**（那是 [V3] 的 broad-phase）。
        /// ⚠ 没装雷达 ⇒ `beamRangeMeters == 0` ⇒ 恒 false（"没雷达照不到"由构造保证，不用特判）。
        /// </summary>
        public bool RadarBeamContains(DVector3 targetPosSystem, double physicsTick, out double distMeters)
        {
            DVector3 self = CurrentOrbit.positionSystem.v;   // OrbitState 是 struct ⇒ 不判空
            DVector3 d = targetPosSystem - self;
            distMeters = d.magnitude;
            if (distMeters > radarCap.beamRangeMeters || distMeters < 1e-6) return false;
            double halfRad = radarCap.beamHalfAngleDeg * (System.Math.PI / 180.0);
            double cosAng = DVector3.Dot(d / distMeters, RadarBoresight(physicsTick));
            return cosAng >= System.Math.Cos(halfRad);
        }

        // ── 信号强度（[V8]；W1 枢纽的量）──
        // 形态：**带失效钩子的缓存值**。之所以不做"静态项缓存 + 开关项按步读"的拆分
        // （§1.8 [F] 的简化）：[L](b)(c)+[O1] 之后**没有任何东西每步读它**——探测只在
        // "读触发刷新"时读，所以既不需要每步重算，也不需要把算式拆成两半。
        private double cachedSignal;
        private SensorTech cachedDetect;
        private bool derivedDirty = true;   // 失效位**同时**管 cachedSignal 与 cachedDetect（同一批驱动点，见 InvalidateSignal）

        // ── §5 电/热闭环（2026-10-22 步 4/5）──
        // 快照输入：只在**派生量重算**时（部件集/实体集变化、点火翻转等）刷新，每步规则只读这些 float。
        // ⇒ 物理步里**不遍历组件**（§0-1 红线），每群每步 O(1)。
        private float cachedPowerGen;
        private float cachedPowerDrawPropulsion, cachedPowerDrawShield, cachedPowerDrawWeapon, cachedPowerDrawSensor;
        private float cachedPowerStorage, cachedPowerDischargeLimit, cachedPowerChargeLimit;
        private float cachedHeatProduction, cachedHeatDissipation;

        /// <summary>**电池当前存量**（J，持久化）。容量 = `AggregatedStats.powerStorage`（Σ 组件容量）。
        /// 放在群级而不是逐件：存量每步都变，逐件存就要每步扫组件（§0-1）。</summary>
        public float StoredEnergyJ;

        /// <summary>**电闭环总系数**（0–1；1 = 全供，0 = 完全饿死）。消费端只乘这个数（§5）。</summary>
        public float Brownout = 1f;
        public float BrownoutPropulsion = 1f;
        public float BrownoutShield = 1f;
        public float BrownoutWeapon = 1f;
        public float BrownoutSensor = 1f;

        /// <summary>**热闭环系数**（0–1；1 = 不超温）。默认公式见 `PowerThermalCore.OverheatFactor`。</summary>
        public float Overheat = 1f;

        /// <summary>上一步读数（W，不持久化；调试/验收读它）。</summary>
        public float PowerSupplyW, PowerDemandW, PowerUsedW;

        /// <summary>本群是否有任何电/热输入（重算时定）：false ⇒ 每步**跳过**规则（老船零成本路径）。</summary>
        internal bool HasPowerOrHeat;

        /// <summary>上一步的电/热输入快照（供 `CanSupplyPropulsionNow` 在**点火前**问"够不够"）。</summary>
        private PowerThermalCore.Inputs lastPowerInputs;

        /// <summary>本步**推进通道是否被全额供给**（欠电 ⇒ false ⇒ 推力门关闭）。
        /// 由 `StepPowerThermal` 每步写；并行区只读。</summary>
        public bool PropulsionPowered = true;

        /// <summary>最近一次点火被**欠电**拒绝（读数，不持久化）。用户 2026-10-26 裁定：欠电 ⇒ 拒绝点火。</summary>
        public bool BurnIgnitionBlockedByPower;

        /// <summary>最近一次燃烧因**欠电**被熄火（读数，不持久化；与"工质尽"同口径）。</summary>
        public bool BurnEndedByPower;

        // ── 控制核心（2026-10-26；用户口径 = KSP 命令舱/探测器核心）──
        /// <summary>本群**装过**控制核心（粘性；由各实体 OR 上来，重算时刷新）。</summary>
        public bool RequiresControlCore;

        /// <summary>本群当前还有**未损毁**的控制核心（任一实体有 ⇒ 有）。</summary>
        public bool HasIntactControlCore;

        /// <summary>可被操作：没装过核心 ⇒ 恒 true；装过 ⇒ 必须还有完好核心。</summary>
        public bool IsControllable => !RequiresControlCore || HasIntactControlCore;

        /// <summary>最近一次点火被**无控制核心**拒绝（读数，不持久化）。</summary>
        public bool BurnIgnitionBlockedNoControl;

        /// <summary>本群还有轮子角动量没卸完（2026-10-26：滑行时也要继续卸载；不落盘，读档后由下一次燃烧重建）。</summary>
        public bool WheelHasMomentum;

        /// <summary>取当前控制权（需要时先刷派生量；读档/部件变化后由 `InvalidateSignal` 置脏）。</summary>
        public bool IsControllableNow()
        {
            if (derivedDirty) RecomputeDerived();
            return IsControllable;
        }

        /// <summary>
        /// **点火前的"够不够"判断**：推进通道需求 &gt; 0 时，发电 + 电池可放能否全额覆盖
        /// （用户 2026-10-26 裁定：**欠电即拒绝点火**，不是按比例降推力）。
        /// 无推进功耗（旧船/目录没填）⇒ 恒 true ⇒ **零行为变化**。
        /// </summary>
        public bool CanSupplyPropulsionNow()
        {
            if (!(lastPowerInputs.nominalPropulsion > 0f)) return true;
            var i = lastPowerInputs;
            i.storedJoules = StoredEnergyJ;   // 存量是群级实时值（输入里那份可能是一步前的快照）
            i.activityPropulsion = 1f;        // 假设现在就点火
            return PowerThermalCore.CanSupplyChannel(i, lastPowerInputs.nominalPropulsion);
        }

        /// <summary>
        /// **一步电/热规则**（由 `SpaceGameComp` 在主线程每物理步调一次）。
        /// 纯 O(1)：输入已由 `RecomputeDerived` 把 Σ 聚合缓存好，这里只做分配算术。
        /// 无发电、无需求、无产热 ⇒ 直接回到"全供/不超温"（老船零成本路径）。
        /// </summary>
        public void StepPowerThermal(in PowerThermalCore.Inputs inputs)
        {
            lastPowerInputs = inputs;   // 供 `CanSupplyPropulsionNow`（点火前问"够不够"）

            if (inputs.genWatts <= 0f
                && inputs.nominalPropulsion <= 0f && inputs.nominalShield <= 0f
                && inputs.nominalWeapon <= 0f && inputs.nominalSensor <= 0f
                && inputs.heatProduction <= 0f)
            {
                Brownout = 1f;
                BrownoutPropulsion = 1f;
                BrownoutShield = 1f;
                BrownoutWeapon = 1f;
                BrownoutSensor = 1f;
                Overheat = 1f;
                PowerSupplyW = 0f;
                PowerDemandW = 0f;
                PowerUsedW = 0f;
                PropulsionPowered = true;
                return;   // 存量不动（没发电也没需求 ⇒ 充放都是 0）
            }

            // §5（用户 2026-10-26 裁定）：推进**欠电即拒绝点火** ⇒ 本步推进通道要么全额、要么不需求。
            // 不能"按比例给一点"：那会变成"点了火却推不动"，且白烧电池。所以先把活动位收掉再分配。
            var effective = inputs;
            bool canProp = CanSupplyPropulsionNow();
            if (effective.activityPropulsion > 0f && !canProp)
                effective.activityPropulsion = 0f;
            // 推力门：本步**本来在点火**才看供给；没点火时门恒开（避免把"没在烧"误判成"欠电"）
            PropulsionPowered = inputs.activityPropulsion <= 0f || canProp;

            var o = PowerThermalCore.Step(effective);
            StoredEnergyJ = o.storedJoules;
            Brownout = o.brownout;
            BrownoutPropulsion = o.brownoutPropulsion;
            BrownoutShield = o.brownoutShield;
            BrownoutWeapon = o.brownoutWeapon;
            BrownoutSensor = o.brownoutSensor;
            Overheat = o.overheat;
            PowerSupplyW = o.supplyWatts;
            PowerDemandW = o.demandWatts;
            PowerUsedW = o.usedWatts;
        }

        /// <summary>用缓存快照 + 群级活动开关组装本步输入（`dt` = 物理步秒数）。</summary>
        internal PowerThermalCore.Inputs BuildPowerInputs(float dtSeconds)
        {
            PowerThermalCore.Inputs i = default;
            i.genWatts = cachedPowerGen;
            i.dischargeLimitWatts = cachedPowerDischargeLimit;
            i.chargeLimitWatts = cachedPowerChargeLimit;
            i.capacityJoules = cachedPowerStorage;
            i.storedJoules = StoredEnergyJ;
            i.nominalPropulsion = cachedPowerDrawPropulsion;
            i.nominalShield = cachedPowerDrawShield;
            i.nominalWeapon = cachedPowerDrawWeapon;
            i.nominalSensor = cachedPowerDrawSensor;
            // 活动折算（§5：按活动状态折算，不是静态标称）：
            // · 推进：只有点火中才吃电（`HasActiveBurn` 是群级开关，读它 = O(1)）；
            // · 护盾/武器/传感器：本批**恒开**（护盾系统未落地；武器挂点数据层无"正在开火"开关；
            //   被动阵列本来就常开）——留 hook，等各自子系统给活动位（见交接 §4 挂账）。
            i.activityPropulsion = HasActiveBurn ? 1f : 0f;
            i.activityShield = 1f;
            i.activityWeapon = 1f;
            i.activitySensor = 1f;
            i.heatProduction = cachedHeatProduction;
            i.heatDissipation = cachedHeatDissipation;
            i.dtSeconds = dtSeconds;
            return i;
        }

        /// <summary>
        /// 信号强度（[V8]；无量纲，标称 1.0 = 一艘静默基准船）。
        ///
        /// **一个量、两侧共用**（W1）：目标侧"别人探测我时读它"；观测侧"我开机/干扰/转发时它升高"。
        /// 算式本体是纯核心 `SignalModel.Total`——**禁止在别处再写一份**（[C7] 的对称性不变式）。
        ///
        /// 失效由三处驱动：①部件集变化 → `SpaceEntity.InvalidateStats` 收口（唯一的部件变更入口）
        /// ②点火翻转 → `StartBurn` / `EndBurn` ③主动开关翻转 → `ActivePingEnabled` 的 setter。
        /// </summary>
        public double SignalStrength
        {
            get
            {
                if (derivedDirty) RecomputeDerived();
                else if (revealUntilPhysics > 0.0 && TimeScaleManager.GetPhysicsTickSafe() >= revealUntilPhysics)
                {
                    // 开火揭示窗口**靠时间流逝结束**（不是 setter ⇒ 没有可挂的失效钩子）⇒ 读时检查一次并重算。
                    // 代价 = 一次比较（绝大多数群从未开过火，第一个条件就是 false）。
                    revealUntilPhysics = double.NegativeInfinity;
                    RecomputeDerived();
                }
                return cachedSignal;
            }
        }

        /// <summary>
        /// **派生量失效**：一次置脏同时管 `SignalStrength` 与 `DetectTech`（两者同一批驱动点 —— 见 `SignalStrength` 文档的四处）。
        /// 名字保留 `InvalidateSignal`（既有调用点与测试都在用它）；语义已扩到"全部派生量"。
        /// </summary>
        public void InvalidateSignal() => derivedDirty = true;

        private void RecomputeDerived()
        {
            var x = new SignalInputs
            {
                burning = HasActiveBurn,
                activePing = ActivePingEnabled,
                jamming = jamming,                  // [E3]/S4：干扰开机（W1 第二个开关项）
                revealedByFire = RevealedByFire,    // [N6]/S4：读时算（窗口靠时间流逝结束）
                // sharingIntel（[E8] 情报转发）**尚未落地**：本版没有"转发节点"这个概念
                //（阵营内共享在 S2 里是隐式的）⇒ 它的暴露成本待 [E8] 与"转发节点"一起定。
            };

            var dt = new SensorTech();   // 编队级 = 各实体聚合的**求和**（与实体级聚合同口径）

            // §5 电/热快照输入：整趟重算先清零（与上面 `SignalInputs` 同一次遍历收集）。
            // ⚠ 注意 `cachedJamStrength` 没有清零（历史遗留，已登记为缺陷：每次重算都会累加 Σ干扰）；
            //   本批**不动它**（属 EW 平衡改动，需单独裁定），新字段按"重算 = 全量覆盖"的纪律写。
            cachedPowerGen = 0f;
            cachedPowerDrawPropulsion = 0f;
            cachedPowerDrawShield = 0f;
            cachedPowerDrawWeapon = 0f;
            cachedPowerDrawSensor = 0f;
            cachedPowerStorage = 0f;
            cachedPowerDischargeLimit = 0f;
            cachedPowerChargeLimit = 0f;
            cachedHeatProduction = 0f;
            cachedHeatDissipation = 0f;

            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg != null)
            {
                // 尺寸口径（B8 定稿）：**编队并集 AABB 的半尺寸模**（船体系，姿态无关），
                // 与碰撞的组并集盒同口径；不再“逐实体取最大半对角线”（多实体组会低估/口径不一）。
                double uMinX = double.MaxValue, uMinY = double.MaxValue, uMinZ = double.MaxValue;
                double uMaxX = double.MinValue, uMaxY = double.MinValue, uMaxZ = double.MinValue;
                bool hasGeo = false;
                bool hasControlCore = false;
                foreach (int eid in entityIds)
                {
                    var e = sg.GetEntityById(eid);
                    if (e == null) continue;
                    // 控制核心：装过就粘住；任一实体还有完好核心 ⇒ 本群可操作
                    if (e.RequiresControlCore) RequiresControlCore = true;
                    if (e.HasIntactControlCore) hasControlCore = true;
                    e.GetAabbSystem(out DVector3 eMin, out DVector3 eMax);
                    DVector3 p = e.Position;
                    uMinX = System.Math.Min(uMinX, p.x + eMin.x);
                    uMinY = System.Math.Min(uMinY, p.y + eMin.y);
                    uMinZ = System.Math.Min(uMinZ, p.z + eMin.z);
                    uMaxX = System.Math.Max(uMaxX, p.x + eMax.x);
                    uMaxY = System.Math.Max(uMaxY, p.y + eMax.y);
                    uMaxZ = System.Math.Max(uMaxZ, p.z + eMax.z);
                    hasGeo = true;
                    // 热/电项今天恒为 0（无组件写那两个槽），组件就位后自动生效（§3.2 的空槽）
                    var st = e.AggregatedStats;
                    x.heatWatts += st.heatProduction + st.heatDissipation;
                    x.powerWatts += st.powerGen + st.powerDraw;
                    // §5：电/热规则要的快照（每步只读这些缓存 float，不回头遍历组件）
                    cachedPowerGen += st.powerGen;
                    cachedPowerDrawPropulsion += st.powerDrawPropulsion;
                    cachedPowerDrawShield += st.powerDrawShield;
                    cachedPowerDrawWeapon += st.powerDrawWeapon;
                    cachedPowerDrawSensor += st.powerDrawSensor;
                    cachedPowerStorage += st.powerStorage;
                    cachedPowerDischargeLimit += st.powerDischargeLimit;
                    cachedPowerChargeLimit += st.powerChargeLimit;
                    cachedHeatProduction += st.heatProduction;
                    cachedHeatDissipation += st.heatDissipation;
                    x.activeRadiation += st.activeRadiation;   // [V7]/[E2] 部件级那一份（建筑级在循环之后加）
                    // [V7]：侦测技术（传感器组件 / 未来的 EW 建筑投影都进这里）
                    dt.sensitivityMeters += st.sensorRange;
                    dt.memorySeconds += st.memorySeconds;   // 记忆时长（[V3] 的 T；部件级那一份）
                    cachedJamStrength += st.jamStrength;    // [E3] 干扰效力（部件级聚合）
                    // （建筑级那一份在循环之后加，见下面 buildingJamBonus）
                    dt.ecmResist += st.ecmResist;
                    dt.eccmResist += st.eccmResist;
                }
                HasIntactControlCore = hasControlCore;
                if (hasGeo)
                {
                    double hx = (uMaxX - uMinX) * 0.5;
                    double hy = (uMaxY - uMinY) * 0.5;
                    double hz = (uMaxZ - uMinZ) * 0.5;
                    x.rcsRadiusMeters = new DVector3(hx, hy, hz).magnitude;
                }
            }

            // [V7]/[N7] **建筑级投影**（每物理步由 `SpaceGameComp` 写入；已按毁损折算）——与部件级**相加**。
            // 归属口径（用户 2026-10-01 裁定）：建筑所在地图所属的那条 group；空间站自己就是一条 group。
            cachedJamStrength += buildingJamBonus;                 // [E3] 建筑级那一份（已按毁损折算）
            dt.sensitivityMeters += buildingTechBonus.sensitivityMeters;
            dt.memorySeconds += buildingTechBonus.memorySeconds;   // 建筑级那一份
            dt.ecmResist += buildingTechBonus.ecmResist;
            dt.eccmResist += buildingTechBonus.eccmResist;
            // [V7]/[E2] 主动辐射的**建筑级**那一份：只在开机时才进 S（判定在 `SignalModel.Total` 的 `activePing`）
            x.activeRadiation += radarCap.activeRadiation;

            cachedSignal = SignalModel.Total(x);
            cachedDetect = dt;
            // §5：本群是否需要每步跑电/热规则。false ⇒ 规则整条跳过（并把系数归位到"全供/不超温"，
            // 防"电部件被拆光后 brownout 还停在 0"的陈旧读数）。
            HasPowerOrHeat = cachedPowerGen > 0f
                || cachedPowerDrawPropulsion > 0f || cachedPowerDrawShield > 0f
                || cachedPowerDrawWeapon > 0f || cachedPowerDrawSensor > 0f
                || cachedHeatProduction > 0f || cachedPowerStorage > 0f;
            if (!HasPowerOrHeat)
            {
                Brownout = 1f;
                BrownoutPropulsion = 1f;
                BrownoutShield = 1f;
                BrownoutWeapon = 1f;
                BrownoutSensor = 1f;
                Overheat = 1f;
                PowerSupplyW = 0f;
                PowerDemandW = 0f;
                PowerUsedW = 0f;
            }
            derivedDirty = false;
        }

        public OrbitState CurrentOrbit;
        public bool OrbitDirty = true;

        private HashSet<int> entityIds = new();

        /// <summary>实体集版本：AddEntity/RemoveEntity 实际变更时自增（不序列化）。
        /// 供群级推进源缓存判陈旧（见 SpaceGameComp.GetGroupPropulsionSource）。</summary>
        internal int entitySetVersion;

        // ── 群级推进源聚合器缓存（无 Map 多推进实体组，不序列化）──
        // 指纹 = entitySetVersion ⊕ 各推进实体 PropulsionVersion，两者之外无输入，
        // 故指纹匹配即可复用实例（聚合器 Networks 懒构建一次，此后只读）。
        internal GroupPropulsionAggregator? cachedPropulsionAggregator;
        internal int cachedPropulsionStamp;

        /// <summary>B6：多图/多源聚合器缓存（地图源 + 无图实体源；指纹含各图 NetworksVersion/开关）。</summary>
        internal CompositePropulsionSource? cachedMapPropulsionAggregator;
        internal int cachedMapPropulsionStamp;

        /// <summary>燃烧切片 scratch（第 3 条：每物理步复用，免每步 new List）。
        /// 物理并行区内由本组唯一迭代独占写入，主线程在同一步消费其引用快照后不再保留。</summary>
        internal readonly List<ThrustSlice> thrustSliceScratch = new();

        // ── 圆轨模式 ──
        public bool IsCircularOrbit ;
        public double circularRadius ;          // 轨道半径 (m)
        public double circularAngularVelocity ; // 角速度 (rad/s)
        public double circularStartAngle ;      // 角度偏移 (进入时 = 0，兼容旧存档保留)
        public double circularStartTick ;           // 进入圆轨时的物理 Tick (physicsTick)
        public DVector3 circularOrbitNormal = DVector3.up;  // 轨道平面法线 (序列化)
        public DVector3 circularRadialRef = DVector3.right; // 进入圆轨时的径向参考方向 (角度0)
        /// <summary>圆轨的中心天体 ID。-1 表示沿用 CelestialBodyId（旧存档兼容）。</summary>
        public int circularCentralBodyId = -1;

        // ── 圆轨进入的两段式机动状态（第一段 burn 完成后由 SpaceGameComp 结算第二段）──
        /// <summary>是否有等待 burn 完成后结算的圆轨进入请求（BeginCircularize 置位）。</summary>
        public bool HasPendingCircularize ;
        /// <summary>待结算圆轨的中心天体 ID。</summary>
        public int pendingCircularizeBodyId = -1;

        // ── ISpatialEntity（粗网格索引） ──
        int ISpatialEntity.EntityId => GroupId;
        int ISpatialEntity.GroupId => GroupId;
        int ISpatialEntity.FactionId => FactionId;
        /// <summary>⚠ `ISpatialEntity.Position` 是**未标类型的接口**（A3 的标本：本类实现的是**系统系**，
        /// 而 `SpaceEntity` 实现的是**船体局部**）⇒ 这里**显式跨界**。</summary>
        public DVector3 Position
        {
            get => CurrentOrbit.positionSystem.v;
            set => CurrentOrbit.positionSystem = (SystemVec3)value;
        }
        public (int cx, int cy, int cz) LastCellCoord { get; set; }
        public bool IsSpatialDirty { get; set; }

        // ── 归属天体 ──
        /// <summary>归属天体的 bodyId，-1 表示未归属（深空自由飞行）</summary>
        public int CelestialBodyId = -1;
        /// <summary>
        /// 豁免行星碰撞检查（火箭爬升段出生在大气层内 R+10km &lt; 安全半径 R+100km、
        /// 初速 ~524 m/s &gt; 安全着陆阈值 10 m/s，会被 CheckPlanetCollision 首步误毁）。
        /// 仅爬升段置 true，入轨时清除。
        /// </summary>
        public bool exemptPlanetCollision ;

        // ── **Landed = 纯解析档**（2026-10-28 用户裁定）──────────────────────────────────
        // 落地后位置由**天体固连坐标**给出（自转系里不变），每步只按自转解析成 ECI，
        // **不积分、不燃烧、不做碰撞**（省性能 + 不漂移）。
        /// <summary>已落地（解析档）。由 `SpaceGameComp.LandGroup` 置位。</summary>
        public bool landed;
        /// <summary>落点的**固连坐标**（自转系，米）；解析档每步用它算出 ECI 位置。</summary>
        public double landedFixedX, landedFixedY, landedFixedZ;

        // ── 地表锚点（星座组网：为地表据点合成的不动 group，见《星座组网系统设计-20260828.md》§5 D11）──
        /// <summary>
        /// 地表据点的合成锚点 group：仅作星座成员载体（成员类型统一为 groupId），不是飞船。
        /// 约束 IsActive=false / IsMovable=false / IsCollidable=false——三者共同保证它既不参与物理积分与碰撞，
        /// 也不出现在任何「遍历 groups 的飞船消费方」（渲染 / 索敌 / 目标列表）视野内；
        /// <see cref="SpaceGameComp.GetGridForGroup"/> 另有 IsSurfaceAnchor 短路，使其根本不进空间索引。
        /// </summary>
        public bool IsSurfaceAnchor ;
        /// <summary>锚点对应的原版世界物体 ID（Settlement.ID）；非锚点 = -1。据点被摧毁时据此惰性回收。</summary>
        public int anchorWorldObjectId = -1;

        /// <summary>
        /// 中心天体相对**位置**（**天体局部系**）：`CurrentOrbit.positionSystem − 中心天体系统系偏移`。
        ///
        /// ⚠⚠ **批6 S3-3d（2026-09-30）：从"步末写一次的滞后副本"改成"现算"** —— 原先这两个字段在
        /// `SpaceGameComp` 的物理步末写一次，而 `CurrentOrbit` 在步内会变（圆轨组按当前 tick 解析求值）
        /// ⇒ 读到的值可能**滞后一步**（实测：月球档 ≈1.8 km / 0.03 m/s，**与交会门同阶**）✗。
        /// 现在读到的值**与 `CurrentOrbit` 同刻**；唯一换算仍是 <see cref="FrameConvert.SystemToBody" />。
        /// 要取回那份副本：`_backups/20260930-1938-post-S3-migration-complete-119F88ADF53D0614/`。
        /// </summary>
        public DVector3 positionBody => GetBodyPosition(TimeScaleManager.GetPhysicsTickSafe()).v;

        /// <summary>中心天体相对**速度**（**天体局部系**）。语义同 <see cref="positionBody" />。</summary>
        public DVector3 velocityBody => GetBodyVelocity(TimeScaleManager.GetPhysicsTickSafe()).v;

        // ── 轨道保持 (OrbitKeeping) ──
        /// <summary>是否启用轨道保持模式（选择性抵消非白名单天体的引力，消耗 dV）。</summary>
        public bool HasOrbitKeeping ;
        /// <summary>不抵消的天体 ID 白名单。null 或空 = 未启用过滤。</summary>
        public HashSet<int>? orbitKeepingWhitelist;

        // ── 自动抛级（2026-10-28，落到组上；默认关 ⇒ 不改变既有飞行行为）──
        /// <summary>自动抛级开关（存档）。</summary>
        public bool autoStageEnabled;
        /// <summary>触发油比（0.02 = 剩余 2%）。</summary>
        public double autoStageFuelFraction = 0.02;
        /// <summary>计划最短运行时间（s）。</summary>
        public double autoStageMinSeconds = 20.0;

        // ── 引力掩码 + 单向闩锁（[A6]，2026-10-06，计划稿 §2.5/D6）──
        /// <summary>
        /// **不计算引力的天体 id**（组属性；null/空 = 今天行为）。大气档 = "只算中心天体"
        /// （地球系场 2 源 → 1 源）。**过滤在求和循环里做**（`NBodyIntegrator.FieldAccelJerk` 的掩码重载），
        /// 不建"过滤后的源表"。**反向（→忽略）只能由档位显式设**（见 <see cref="SetGravityMask"/>）。
        /// </summary>
        public List<int>? ignoredGravityBodyIds;

        /// <summary>
        /// **单向闩锁**（[A6]）：恢复过全场就**永不自动回忽略**（幂等；序列化）。
        /// 语义 = "只加精度"：恢复 → 只有置位这一条路；反向要显式重建（且被闩锁拒绝）。
        /// </summary>
        public bool fieldRestoreLatched;

        /// <summary>该天体的引力本组是否忽略（求和循环 / 保持修正共用这一个判定）。</summary>
        public bool IsGravityIgnored(int bodyId)
        {
            var m = ignoredGravityBodyIds;
            if (m == null) return false;
            for (int i = 0; i < m.Count; i++)
                if (m[i] == bodyId) return true;
            return false;
        }

        /// <summary>
        /// **进大气档**（档位显式设掩码；[A6] 三步交接之①）：置"不计算的天体"列表。
        /// ⚠ 闩锁置位后**拒绝**（不许二次翻转）；空列表 = 清掩码（等价于没设）。
        /// </summary>
        public bool SetGravityMask(List<int>? bodyIds)
        {
            if (fieldRestoreLatched) return false;
            ignoredGravityBodyIds = bodyIds != null && bodyIds.Count > 0 ? bodyIds : null;
            return ignoredGravityBodyIds != null;
        }

        /// <summary>
        /// **恢复全场**（[A6] 三步交接之②：自动、单向、幂等）：清掩码 + 置闩锁。
        /// 返回"本次是否真的发生了恢复"（供日志/验收读数用）。
        /// </summary>
        public bool RestoreFullGravityField()
        {
            bool had = ignoredGravityBodyIds != null;
            ignoredGravityBodyIds = null;
            fieldRestoreLatched = true;
            return had;
        }

        // ── 主动燃烧（缓释 dV） ──
        /// <summary>是否有正在执行的主动燃烧。</summary>
        public bool HasActiveBurn ;
        public long BurnCommandVersion;
        /// <summary>燃烧方向单位向量（**系统系**；S1 命名：系标记进名字，见计划稿 §11.2）。</summary>
        public DVector3 burnDirectionSystem;
        /// <summary>本次燃烧剩余 dV (m/s)。</summary>
        public double remainingBurnDV;
        /// <summary>本次 burn 已累计施加的 dV (m/s，诊断/日志用；StartBurn 清零)。</summary>
        public double appliedBurnDV;
        /// <summary>本次 burn 开始时的总质量（kg，首次 burn tick 记录，实际 ΔV 评估用）。</summary>
        public double burnStartMass;
        /// <summary>本次 burn 已实际消耗的工质（kg，主线程累计）。</summary>
        public double burnConsumedPropellant;
        /// <summary>
        /// 本次 burn 是否用 RCS（全矢量免对准）：交会/接近/细修 burn 置 true，主引擎大 burn 保持 false。
        /// **有图/无图同义**（2026-10-27）：true ⇒ 优先 RCS，RCS 干了回退主网。
        /// 瞬态标记，EndBurn 清零，不序列化。
        /// </summary>
        public bool burnUseRcs;

        /// <summary>
        /// 本次 burn 是否为引导修正 burn（交会/接近）：引导允许打断重烧的前提。
        /// 瞬态标记，EndBurn 清零，不序列化。
        /// </summary>
        public bool burnIsCorrection;

        /// <summary>
        /// 预对准目标（主推进轴应对准的世界方向；coasting 段由机动执行设置，
        /// 点火前提前转向 → burn 准时居中）。零 = 未启用。瞬态，不序列化。
        /// </summary>
        public DVector3 pendingAlignDir;

        /// <summary>
        /// **大气段顺气流指向**（[A6+]，2026-10-06）：置位时，物理步把**船体前轴（+z）**转到
        /// **空气相对速度方向**（D13："不做风标力矩 ⇒ 指向纪律由自动驾驶/制导承担"）。
        /// 由爬升控制器在"气动不可忽略"的空域置位、出区/结束即清；**瞬态、不序列化**
        /// （控制器每步重设 ⇒ 读档后一步内恢复）。与主推轴/矢量锥无关（全向船也能被指向）。
        /// </summary>
        public bool aeroAlignToAirflow;

        /// <summary>
        /// **[A5] 熄火读数的"只报一次"标志**（瞬态、不序列化）：吸气式网络因空气不足而本步不出力时，
        /// 只要还在烧就会**每步再判一次** ⇒ 用本标志把日志限成一次（每步一行的探针 = 散射探针，纪律不许）。
        /// 由 `EndBurn` 清 ⇒ 下次点火重新有机会报。
        /// </summary>
        public bool intakeFlameoutLogged;

        /// <summary>
        /// **[A4] 包线归零读数的"只报一次"标志**（瞬态、不序列化）：某网络因包线因子 0
        /// （典型 = 真空里的吸气式）本步不出力时，每步都会再判一次 ⇒ 用本标志把日志限成一次。
        /// 由 `EndBurn` 清。
        /// </summary>
        public bool envelopeRoutedOffLogged;

        // ── [A7] 悬浮（定点保持，计划稿 §2.6 / D10）────────────────────────────────
        /// <summary>
        /// **是否在悬浮**（定点保持）：控制器每物理步把船按在**固连定点**上（见 `hoverLatRad` 等），
        /// 姿态锁**本地铅垂**（D10），推力走**全矢量（RCS）网**。**Scribe**：悬浮是工作模式，读档该在。
        /// </summary>
        public bool hoverActive;

        /// <summary>
        /// **[A8] 悬浮的档位**：false = **物理档**（定点保持控制器 + 真实推力耗料，默认）；
        /// true = **解析档**（位置/速度由定点解析给出、不模拟受力，但**照扣料** —— D8 的等效计费）。
        /// 档位只管"受不受力"，**耗料是工作模式的属性** ⇒ 两档都烧料、账单应一致（判据见台架）。
        /// **Scribe**：与工作模式一样该跨读档。
        /// </summary>
        public bool hoverAnalytic;

        /// <summary>悬浮定点：**固连纬度**（rad，Scribe）。</summary>
        public double hoverLatRad;
        /// <summary>悬浮定点：**固连经度**（rad，Scribe）。</summary>
        public double hoverLonRad0;
        /// <summary>悬浮定点：**高度**（m，相对地表，Scribe）。</summary>
        public double hoverAltitudeMeters;

        /// <summary>
        /// **调试用：无限燃料**（Scribe）—— 2026-10-07 用户点名要"无限燃料的船"。
        /// 语义 = 唯一的工质扣减口（`SpaceGameComp.ConsumeGroupPropellant`）**报告"已扣完"但实际不动**
        /// ⇒ 所有下游（悬浮 / 解析档 / 搬运 / 料尽退出）看到的是"料一直在"，**不掺任何质量/推力改动**
        /// （把罐加大反而会把重量加上去、悬不住 —— 那正是要避开的失真）。
        /// </summary>
        public bool debugInfinitePropellant;

        /// <summary>
        /// **搬运起搬时若在悬浮 ⇒ 到点自动恢复悬浮**（Scribe）—— 2026-10-07 用户实测"一搬就坠毁"：
        /// 起搬会（按模式互斥）退出悬浮，而**大气层里没有悬浮就等于开始掉**（30 km 掉到地面几十秒，
        /// 撞地 339.7 m/s ⇒ 坠毁 + 地图销毁）。巡航的语义应是"**搬过去、然后继续悬停**"，
        /// 所以要记住"原来在悬着"并在到达点恢复（档位一起记）。
        /// </summary>
        public bool hoverResumeAfterTransfer;
        /// <summary>恢复时要回的档位（`hoverResumeAfterTransfer` 配套；起搬前是解析档就回解析档）。</summary>
        public bool hoverResumeAnalytic;

        // ── [A8] 点到点解析搬运单（解析档的"搬运/巡航"；Scribe）──────────────────────
        /// <summary>**是否在解析搬运中**（解析档：不模拟受力，但**按等效 dv 速率照扣料**）。</summary>
        public bool transferActive;
        /// <summary>搬运起点（系统系，起搬那一刻的位置/速度）。</summary>
        public DVector3 transferFromPos, transferFromVel;
        /// <summary>搬运终点（系统系，目标位置/速度——到达时**精确**落在这上面）。</summary>
        public DVector3 transferToPos, transferToVel;
        /// <summary>这次搬运要付的等效 Δv 总量（m/s；账 = `m·dv/ve`，与物理档同口径）。</summary>
        public double transferDvTotal;
        /// <summary>起搬的物理时刻（s；进度 = (now − start)/T）。</summary>
        public double transferStartTick;
        /// <summary>搬运时长（s，起搬时算好；到达判据用它）。</summary>
        public double transferDuration;
        /// <summary>
        /// **[A8] 到达时刻的落位误差（m）**：由 `ApplyAnalyticTransfer` 在**到达那一步**当场量下
        /// （它就在位置写完之后、同一步内）—— 判据只读这个数，**不在事后重建**。
        /// 起因：判据侧三次口径迭代（惯性快照 1053 m / ω 搬点 387.6 km / 固连经纬度 387.6 km）互相打架，
        /// 与其继续调"我算的那条线"，不如让**生产自己**把那一刻的量记下来（谁也没法比它更准）。
        /// </summary>
        public double transferArrivalErrorMeters;

        /// <summary>搬运**中点**读数"只报一次"标志（瞬态）。</summary>
        public bool transferMidLogged;
        /// <summary>
        /// 搬运**到达**读数"只报一次"标志（瞬态）。
        /// ⚠ **与中点是两个标志**（2026-10-07 读日志抓到）：原先共用一个 ⇒ 中点先把它置位 ⇒ **到达那行被吞**
        /// ⇒ 日志里只有"起搬/中点"、看着像"根本没到达"（我据此白怀疑了落位三轮）。
        /// **"只报一次"的标志只能管一件事**；要压制的永远是**最不重要**的那条。
        /// </summary>
        public bool transferArriveLogged;

        /// <summary>悬浮"推力不足"只报一次（瞬态）。</summary>
        public bool hoverShortfallLogged;

        // ── [A7] 悬浮逐步读数（瞬态；诊断/表现层用）──
        /// <summary>本步指令 Δv（m/s）。</summary>
        public double hoverLastDvCmd;
        /// <summary>本步实给 Δv（m/s；不足时 &lt; 指令）。</summary>
        public double hoverLastDvApplied;
        /// <summary>本步可用全矢量推力（N）。</summary>
        public double hoverLastThrustN;

        /// <summary>悬浮周期读数的累积物理秒（瞬态；到 10 s 打一行并清零 —— **按秒累积，不按 tick 差**）。</summary>
        public double hoverLogAccumSeconds;

        // ── [A7] 悬浮读数**区间统计**（瞬态；每次打行后清零）—— 单点读数分不清"真漂"与"相位锁定的振荡"：
        /// <summary>区间内高度差（径向）最小值/最大值/累加/采样数。</summary>
        public double hoverLogErrMin = double.MaxValue;
        public double hoverLogErrMax = double.MinValue;
        public double hoverLogErrSum;
        public int hoverLogCount;
        /// <summary>区间内径向速度累加（算均值用）。</summary>
        public double hoverLogVSum;
        /// <summary>开悬浮那一刻的船半径（对照"船半径有没有真的在掉"）。</summary>
        public double hoverLogRadius0;

        /// <summary>
        /// 本物理步累积的合冲量（速度 ΔV，m/s）。
        /// 并行物理区 / 战斗层只累积（每 group 独占写自己的字段），主线程在下一物理步
        /// 开头统一施加并清零。多来源（burn / 碰撞动量交换 / 爆炸 / 手动 ΔV）顺序无关地
        /// 累加，避免一步内多次直接改 CurrentOrbit.velocity 的顺序混乱与并行写冲突。
        /// 瞬态字段，不序列化（物理步间即清空）。
        /// </summary>
        public DVector3 pendingImpulse;
        /// <summary>
        /// 机动燃烧策略：启用的工质网络（键 = 工质 Def）。null/空 = 全启用。
        /// **只允许经 `ApplyNetworkSelection` / `SetBurnNetworkOverride` / `RestorePlayerNetworkSelection` 写**
        /// （2026-10-27 收敛：此前机动/引导/圆轨/窗口四个爹直接赋值，语义靠调用顺序）。
        /// </summary>
        public HashSet<PropellantDef>? burnEnabledNetworks { get; private set; }

        // ── 玩家选网（2026-10-27）：持久保存选择，运行时映射到 `burnEnabledNetworks` ──
        /// <summary>玩家选中的推进网络工质（defName；空 = 全部）。存档保存。</summary>
        public string enabledNetworkDefName = "";
        /// <summary>选中的网络是否 RCS 类（决定 `burnUseRcs`：RCS 网必须显式模式才出力）。</summary>
        public bool enabledNetworkRcsOnly;
        private HashSet<PropellantDef>? playerEnabledNetworks;

        /// <summary>玩家选择的网络集合（null = 全部）。机动/制导未显式指定网络时用它。</summary>
        public HashSet<PropellantDef>? PlayerEnabledNetworks => playerEnabledNetworks;

        /// <summary>应用玩家选网：`defName` 空 = 全部；否则只启用该工质，并按网络类属设 `burnUseRcs`。</summary>
        public void ApplyNetworkSelection(string? defName, bool rcsOnly)
        {
            enabledNetworkDefName = defName ?? "";
            enabledNetworkRcsOnly = rcsOnly;
            if (string.IsNullOrEmpty(enabledNetworkDefName))
            {
                playerEnabledNetworks = null;
            }
            else
            {
                var def = DefDatabase<PropellantDef>.GetNamedSilentFail(enabledNetworkDefName);
                playerEnabledNetworks = def != null ? new HashSet<PropellantDef> { def } : null;
            }
            RestorePlayerNetworkSelection();
        }

        /// <summary>当前燃烧**临时覆盖**网络/模式；`EndBurn` 会恢复到玩家选择。</summary>
        public void SetBurnNetworkOverride(HashSet<PropellantDef>? networks, bool rcsOnly)
        {
            burnEnabledNetworks = networks;
            burnUseRcs = rcsOnly;
            networkSelectionVersion++;
        }

        /// <summary>恢复玩家选网（null = 全部；`burnUseRcs` = 玩家是否选了 RCS 网）。</summary>
        public void RestorePlayerNetworkSelection()
        {
            burnEnabledNetworks = playerEnabledNetworks;
            burnUseRcs = enabledNetworkRcsOnly;
            networkSelectionVersion++;
        }

        // ── 姿态请求（2026-10-27）：各子系统只提交意图，`ComputeGroupPhysics` 每步解析一次 ──
        /// <summary>本步姿态目标（世界系方向）。</summary>
        public DVector3 attitudeTargetSystem;
        /// <summary>本步胜出请求的优先级；`int.MinValue` = 无请求。</summary>
        public int attitudePriority = int.MinValue;
        /// <summary>本步胜出请求作用的船体轴。</summary>
        public AttitudeAxis attitudeAxis;

        /// <summary>提交姿态请求：优先级高者胜（同级后来者胜）；**不直接写角速度**。</summary>
        public void RequestAttitude(AttitudeAxis axis, DVector3 targetSystem, int priority)
        {
            if (priority < attitudePriority) return;
            attitudePriority = priority;
            attitudeAxis = axis;
            attitudeTargetSystem = targetSystem;
        }

        public void ClearAttitudeRequest()
        {
            attitudePriority = int.MinValue;
            attitudeTargetSystem = DVector3.zero;
        }

        // ── 选网掩码（2026-10-27）：每步不再 `HashSet.Contains`，只在选择/网络列表变化时重建 ──
        private IPropulsionSource? maskSource;
        private int maskVersion = -1;
        private bool[]? networkMask;
        private int networkSelectionVersion;

        /// <summary>返回与 `src.Networks` 同序的启用掩码；缓存键 = (源实例, 选择版本)。
        /// 索引顺序固定 ⇒ 归约顺序确定。</summary>
        public bool[] GetNetworkMask(IPropulsionSource src)
        {
            if (networkMask != null && ReferenceEquals(maskSource, src) && maskVersion == networkSelectionVersion)
                return networkMask;

            maskSource = src;
            maskVersion = networkSelectionVersion;
            var nets = src.Networks;
            if (networkMask == null || networkMask.Length != nets.Count)
                networkMask = new bool[nets.Count];

            bool all = burnEnabledNetworks == null || burnEnabledNetworks.Count == 0;
            for (int i = 0; i < nets.Count; i++)
            {
                var p = nets[i].propellant;
                networkMask[i] = all || (p != null && burnEnabledNetworks!.Contains(p));
            }
            return networkMask;
        }

        /// <summary>
        /// 空间站「当前网络」（单选）：此后加入的机动都继承此单一网络（null = 全启用）。
        /// 在机动队列窗口顶部切换。
        /// </summary>
        public PropellantDef currentNetwork = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        // 机动规划队列（顺序执行）
        public List<PlannedManeuver> plannedManeuvers = new();

        // 战斗模式缓存（用于插值）
        public PhysicsStateCache CombatCache = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        // ── Combat 插值渲染位置（仅渲染层读取；物理检测一律用 CurrentOrbit.position）──
        private DVector3 renderedPosition;
        private bool hasRenderedPosition;

        /// <summary>
        /// 渲染用位置：Combat 模式返回插值平滑位置；圆轨非战斗返回解析精确位置；其余返回物理位置。
        /// ⚠️ 物理/碰撞/射弹检测严禁使用此属性——物理状态统一在 CurrentOrbit（physicsTick 时间轴），
        /// 插值仅用于显示层平滑，二者分离保证"万物统一物理时间步长"。
        /// </summary>
        public DVector3 RenderPosition
        {
            get
            {
                var tsm = Current.Game?.GetComponent<TimeScaleManager>();
                if (tsm != null && tsm.IsCombatMode && hasRenderedPosition)
                    return renderedPosition;
                if (IsCircularOrbit)
                    return GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe());
                return CurrentOrbit.positionSystem.v;
            }
        }

        /// <summary>写入 Combat 插值渲染位置（仅由 UpdateCombatInterpolation 调用）。</summary>
        internal void SetRenderedPosition(DVector3 pos)
        {
            renderedPosition = pos;
            hasRenderedPosition = true;
        }

        // ── 圆轨解析结果懒缓存（per-physicsTick，不序列化）──
        // 圆轨位置/速度是 physicsTick 的纯解析函数（同一 tick 内无人修改轨道参数），
        // 战斗循环同一 tick 内会被查询 4~6 次，用 tick 值做键缓存，其余调用自动命中。
        private double cachedPosTick = double.NaN;
        private DVector3 cachedPos;
        private double cachedVelTick = double.NaN;
        private DVector3 cachedVel;

#if DEBUG
        // 缓存命中/未命中统计（调试验证：每 tick 每组应只 miss 1 次）
        internal long debugPosCacheHits;
        internal long debugPosCacheMisses;
        internal long debugVelCacheHits;
        internal long debugVelCacheMisses;
#endif

        private void InvalidateOrbitCache()
        {
            cachedPosTick = double.NaN;
            cachedVelTick = double.NaN;
        }

        public class PhysicsStateCache
        {
            public int baseTick;
            public DVector3 startPos;
            public DVector3 startVel;
            public DVector3 acc;
            public DVector3 jerk;
            public double dt;
            public double offset;

            public DVector3 InterpolatePosition(double t)
            {
                NBodyIntegrator.TaylorInterpolate(startPos, startVel, acc, jerk, t, out var p, out _);
                return p;
            }

            public DVector3 InterpolateVelocity(double t)
            {
                NBodyIntegrator.TaylorInterpolate(startPos, startVel, acc, jerk, t, out _, out var v);
                return v;
            }
        }

        /// <summary>
        /// 机动规划结构体。序列化 + 预览缓存。
        /// </summary>
        public struct PlannedManeuver : IExposable
        {
            public float radialDV;
            public float tangentialDV;
            public float normalDV;
            /// <summary>节点时刻（冲量模型的瞬间，即燃烧中点；physicsTick 基准，与 SpaceGameComp 点火检查一致；Fast 档 TicksGame 会失真）。
            /// 燃烧居中：实际点火时刻 = executeTick − 半程燃烧时长。</summary>
            public int executeTick;
            public bool active;

            /// <summary>
            /// 本次机动使用的工质网络（键 = 工质 Def）。null/空 = 全启用。
            /// 规划机动时选择，执行 burn 只烧这些网络。
            /// </summary>
            public HashSet<PropellantDef>? useNetworks;

            /// <summary>规划时刻算好的**天体参考系（体心）Δv 向量**（节点帧）。
            /// 与世界系只差平移 ⇒ 方向相同；执行时直接用作方向，避免在点火时刻用实际状态换算出帧旋转误差
            /// （MechJeb 同款）。零 = 未设置（退回分量换算）。</summary>
            public DVector3 plannedDvBody;

            /// <summary>
            /// ★★ **本机动的参考轨道**（2026-09-21）——"这一发的 Δv 是在哪条轨道上算的"。
            /// 执行器的靶 = `参考轨道@节点UT 的速度 + plannedDvBody`（见 `PlanNode.refOrbit` 的完整说明）。
            /// ⚠ `false` ⇒ 退回旧口径（点火前轨道@UT + Δv）⇒ 既有条目行为逐位不变。
            /// </summary>
            public bool hasResolvedRefOrbit;
            public OrbitView resolvedRefOrbit;

            /// <summary>本条目所属的**阶梯计划代号**（见 `SpaceEntityGroup.ladderPlanEpoch`）。
            /// 0 = 非阶梯产出（手动/引导/微调）⇒ 永远不被 `InvalidateStaleLadderNodes` 清掉。</summary>
            public int ladderEpoch;

            /// <summary>
            /// **本机动的意图（目标条件）**——《大修计划》§2.5 / B1.2。null = 纯冻结向量（玩家手动节点 / 旧计划）。
            /// 执行层接上活重解（P2 下一步）后，用它按活轨道重算 Δv 与方向；现在它至少是**可读可存**的。
            /// </summary>
            public PlanIntent? intent;

            /// <summary>
            /// 是否**已按活轨道重解过一次**（P2）。进预点火窗口时解一次就置位——解失败也置位，
            /// 免得每 tick 反复重试同一条解不出来的意图。重解结果会**写回本条机动的分量与 plannedDvBody**
            /// ⇒ 下游（预对准方向 / 可行性估算 / 点火）自动用活值，不需要各自改。
            /// </summary>
            public bool intentResolved;

            /// <summary>
            /// **本条机动的归属**（§2.4 / B1.1）：`Manual` = 玩家排的（夺走引导）、`Guidance` = 引导/规划器排的。
            /// ⚠ 枚举零值是 `Manual` ⇒ `default(PlannedManeuver)` 视为手动（保守，与旧行为一致）。
            /// 规则本体在 `PlanOwnerRules`，**这里只存不判**。
            /// </summary>
            public PlanOwner owner;

            /// <summary>计划自报的燃烧时长（s，0 = 未声明）。执行层用它做"燃烧居中"，不再自己估（B2.2）。</summary>
            public double burnSecondsPlanned;

            /// <summary>本条机动点火时激活群组上已登记的"待激活交会引导"（精确交会：出发段执行后才开始引导）。
            /// 出发段被阻止/取消时引导自动作废。</summary>
            public bool armRendezvousGuidance;

            // 预览缓存 (不序列化，确认机动时由 Planner 填充)
            public List<Vector3> previewCurrentOrbit;
            public DVector3 previewNodePosition;
            public List<Vector3> previewPostOrbit;

            public void ExposeData()
            {
                Scribe_Values.Look(ref radialDV, "radialDV", 0f);
                Scribe_Values.Look(ref tangentialDV, "tangentialDV", 0f);
                Scribe_Values.Look(ref normalDV, "normalDV", 0f);
                Scribe_Values.Look(ref executeTick, "executeTick", 0);
                Scribe_Values.Look(ref active, "active", false);
                Scribe_Values.Look(ref armRendezvousGuidance, "armRendezvousGuidance", false);
                DVector3.Scribe("plannedDvBody", ref plannedDvBody);
                Scribe_Collections.Look(ref useNetworks, "useNetworks", LookMode.Def);

                // 意图（目标条件）：逐字段自己 Scribe（`PlanIntent` 是纯类型、不认识 Verse）
                string? intentName = intent?.operationName;
                List<double>? intentValues = intent == null ? null : new List<double>(intent.paramValues);
                List<string>? intentLabels = intent == null ? null : new List<string>(intent.paramLabels);
                int intentTimeRef = intent?.timeRefIndex ?? -1;
                double intentLead = intent?.leadTime ?? 0.0;
                double intentAlt = intent?.circularizeAltitude ?? 0.0;
                int intentTarget = intent?.targetGroupId ?? -1;
                double intentTick = intent?.absoluteTick ?? -1.0;
                Scribe_Values.Look(ref intentName, "intentOperation");
                Scribe_Collections.Look(ref intentValues, "intentValues", LookMode.Value);
                Scribe_Collections.Look(ref intentLabels, "intentLabels", LookMode.Value);
                Scribe_Values.Look(ref intentTimeRef, "intentTimeRef", -1);
                Scribe_Values.Look(ref intentLead, "intentLead", 0.0);
                Scribe_Values.Look(ref intentAlt, "intentAlt", 0.0);
                Scribe_Values.Look(ref intentTarget, "intentTarget", -1);
                Scribe_Values.Look(ref intentTick, "intentTick", -1.0);
                Scribe_Values.Look(ref intentResolved, "intentResolved", false);
                Scribe_Values.Look(ref owner, "owner", PlanOwner.Manual);
                Scribe_Values.Look(ref burnSecondsPlanned, "burnSecondsPlanned", 0.0);
                if (Scribe.mode == LoadSaveMode.LoadingVars || Scribe.mode == LoadSaveMode.PostLoadInit)
                {
                    intent = string.IsNullOrEmpty(intentName)
                        ? null
                        : new PlanIntent { operationName = intentName!,
                            paramValues = intentValues != null ? intentValues.ToArray() : [],
                            paramLabels = intentLabels != null ? intentLabels.ToArray() : [],
                            timeRefIndex = intentTimeRef,
                            leadTime = intentLead,
                            circularizeAltitude = intentAlt,
                            targetGroupId = intentTarget,
                            absoluteTick = intentTick,
                        };
                }
                if (Scribe.mode == LoadSaveMode.PostLoadInit && useNetworks != null && useNetworks.Count == 0)
                    useNetworks = null; // 空 = 全启用
                // 预览缓存不序列化
            }
        }

        public SpaceEntityGroup() { }
        public SpaceEntityGroup(int groupId, int factionId = 0)
        {
            GroupId = groupId;
            FactionId = factionId;
        }

        public void AddEntity(int entityId)
        {
            if (entityIds.Add(entityId)) entitySetVersion++;
        }

        public void RemoveEntity(int entityId)
        {
            if (entityIds.Remove(entityId)) entitySetVersion++;
        }
        public bool ContainsEntity(int entityId) => entityIds.Contains(entityId);
        /// <summary>
        /// 本组实体 id（**调用方只读**：增删一律走 <see cref="AddEntity"/>/<see cref="RemoveEntity"/>
        /// ——它们会推 <c>entitySetVersion</c>；直接改这一个集合会让版本号静默过期）。
        ///
        /// ⚠ **返回具体类型 `HashSet&lt;int&gt;` 而不是 `IReadOnlyCollection&lt;int&gt;`**（P45，2026-09-27）：
        /// `foreach` 走接口时每次都要**装箱一个 `HashSet&lt;int&gt;.Enumerator`** ⇒ 每群每步一次分配；
        /// 而本方法有 **60 处**调用（其中**并行体内**就有 `GetLinkedMapComp`/`GetGroupMass`/
        /// `GetGroupPropulsionSource` 三处）⇒ 那正是台账第 22 条「并行区零分配」要消的东西。
        /// 具体类型让 `foreach` 用**结构体枚举器**、`Count`/`Contains` 也走非虚路径 —— **零调用点改动、零语义变化**。
        /// </summary>
        public HashSet<int> GetEntityIds() => entityIds;

        /// <summary>取 group 任一实体所属地图的 SpaceMapComp（无 Map 实体 / 未关联时返回 null）。</summary>
        public SpaceMapComp? GetLinkedMapComp()
        {
            // R5（2026-09-26）：改用缓存引用 —— 本方法在相位里被 `GetMainThrustAxisSystem`/
            // `GetGroupPropulsionSource` 调用（每组每步 ≥1 次），原来每次都做一次组件表线性扫描。
            var sg = SpaceGameComp.GetCached();
            if (sg == null) return null;
            foreach (int eid in GetEntityIds())
            {
                var e = sg.GetEntityById(eid);
                if (e == null || e.MapId < 0)
                    continue;

                var mc = sg.GetMapComponent<SpaceMapComp>(e.MapId);
                if (mc != null)
                    return mc;

                // mapComponents 不随存档恢复；兜底直接从地图取 SpaceMapComp。
                Map map = SpaceMapGenerator.FindPocketMap(e.MapId);
                if (map != null)
                {
                    mc = map.GetComponent<SpaceMapComp>();
                    if (mc != null)
                        return mc;
                }
            }
            return null;
        }

        // ── 机动队列管理 ──

        /// <summary>
        /// 向队列末尾添加一个机动规划。新机动自动继承空间站「当前网络」配置
        /// （null/空 = 全启用）。
        /// </summary>
        public void QueueManeuver(PlannedManeuver maneuver)
        {
            maneuver.useNetworks = InheritNetworks();
            // §2.4 / B1.1：**显式归属**取代原来的"无条件清引导"。
            // 旧行为是"任何排队都杀引导"，于是引导自己派出的出发段也会把自己的引导清掉，
            // 只能靠"先排队后登记"的调用顺序 + `armRendezvousGuidance` 再带回来 ✗（脆弱且静默）。
            // 现在只有**手动**条目会夺走引导；引导自己的条目不清（规则见 `PlanOwnerRules`）。
            if (PlanOwnerRules.TakesOverGuidance(maneuver.owner))
                ClearRendezvousGuidance(); // 玩家手动接管：交会/接近引导让位（接近是其特化，一并清除）
            plannedManeuvers.Add(maneuver);
        }

        /// <summary>
        /// 添加一个机动规划（指定 dV 和相对时间）。同样继承「当前网络」。
        /// 注意：executeTick 使用 physicsTick 基准（与 ManeuverPlanner 一致）；
        /// 检查/执行在 SpaceGameComp 中用 physicsTick 判定，两基准错位会导致 Fast 档到期失真。
        /// </summary>
        public void QueueManeuver(float radDV, float tanDV, float normDV, int deltaTicks,
            PlanOwner owner = PlanOwner.Manual)
            => QueueManeuver(radDV, tanDV, normDV, deltaTicks, owner, DVector3.zero);

        /// <summary>
        /// 同上，但**带计划的惯性（world）向量**。
        /// ★★ 为什么必须有（2026-09-21 游戏内实测的真 bug）：点火门的判据是
        /// `MainAxisBurnAngleDeg(group, group.burnDirectionSystem)`，而 `burnDirectionSystem` 要到 `StartBurn`
        /// （**门的后面**）才被设 ⇒ 门比的是**上一次燃烧**留下的旧方向；同时 `pendingAlignDir` 的
        /// 预对准瞄的是**队列里下一条**机动 ⇒ 船被转到那个方向、停进 1.5° 死区，
        /// 而门要 2.0° ⇒ **船永远点不着火**（实测：`节点 T+19s` 之后计数器一路走负到 `T--6104s`，
        /// 船在"等对准"里卡死 1.7 小时、`夹角=0.0° |ω|=0` 与 `主轴还差 2.9°` 自相矛盾 ✗✗）。
        /// ⇒ 计划向量是"本条机动朝哪"的**唯一权威**，必须在排队时带上（与 MJ 的
        /// "节点自己携带 `node.patch`/`nextPatch`" 同构）。
        /// </summary>
        public void QueueManeuver(float radDV, float tanDV, float normDV, int deltaTicks,
            PlanOwner owner, DVector3 plannedDvBody)
        {
            int execTick = (int)System.Math.Round(TimeScaleManager.GetPhysicsTickSafe()) + deltaTicks;
            // 归属规则（`PlanOwnerRules`，唯一权威）：**只有手动**条目才夺走引导。
            // 旧写法是无条件 `ClearRendezvousGuidance()`，B2.2-b 的微调（`Executor`）走这里时
            // 会把自己的交会引导清掉 ⇒ 改成按规则判。
            if (PlanOwnerRules.TakesOverGuidance(owner))
                ClearRendezvousGuidance(); // 玩家手动机动 = 接管，交会/接近引导让位
            plannedManeuvers.Add(new PlannedManeuver
            {
                radialDV = radDV,
                tangentialDV = tanDV,
                normalDV = normDV,
                executeTick = execTick,
                active = true,
                owner = owner,
                useNetworks = InheritNetworks(),
                plannedDvBody = plannedDvBody
            });
        }

        /// <summary>
        /// 同上，另带**参考轨道**（2026-09-21，两冲量转移的第二发用）。
        /// 见 `PlanNode.refOrbit` 与 `PlannedManeuver.resolvedRefOrbit` 的说明：
        /// 执行器的靶 = `参考轨道@节点 + Δv`；不带参考轨道时退回"点火前轨道@节点 + Δv"。
        /// </summary>
        public void QueueManeuver(float radDV, float tanDV, float normDV, int deltaTicks,
            PlanOwner owner, DVector3 plannedDvBody, bool hasRefOrbit, OrbitView refOrbit)
        {
            int execTick = (int)System.Math.Round(TimeScaleManager.GetPhysicsTickSafe()) + deltaTicks;
            if (PlanOwnerRules.TakesOverGuidance(owner))
                ClearRendezvousGuidance();
            plannedManeuvers.Add(new PlannedManeuver
            {
                radialDV = radDV,
                tangentialDV = tanDV,
                normalDV = normDV,
                executeTick = execTick,
                active = true,
                owner = owner,
                useNetworks = InheritNetworks(),
                plannedDvBody = plannedDvBody,
                hasResolvedRefOrbit = hasRefOrbit,
                resolvedRefOrbit = refOrbit,
                ladderEpoch = ladderPlanEpoch
            });
        }

        /// <summary>阶梯新计划开始：**推进代号**（本代号 = 即将排入的那一对）。调用方随后用
        /// <see cref="InvalidateStaleLadderNodes" /> 清掉上一代的残留，或直接让下一次入队自动带新代号。</summary>
        public void BeginLadderPlanEpoch() => ladderPlanEpoch++;

        /// <summary>把「当前网络」继承到新机动；null = 全启用。</summary>
        private HashSet<PropellantDef>? InheritNetworks()
        {
            if (currentNetwork == null)
                return null;
            return new HashSet<PropellantDef> { currentNetwork };
        }

        /// <summary>
        /// 清除所有机动规划。
        /// **自动对准目标被清 ⇒ 消旋**（2026-10-17 用户实测：预对准转到一半清队列/手动接管，
        /// 魔法动量轮没人写 ω ⇒ 船永久自转）。手动输入随后会覆盖 ω；手动键松开仍保留自由自转。
        /// </summary>
        public void ClearManeuvers()
        {
            bool hadAutoAlign = pendingAlignDir.sqrMagnitude > 1e-12;
            plannedManeuvers.Clear();
            ClearRendezvousGuidance();
            pendingAlignDir = DVector3.zero; // 取消预对准（手动接管）
            if (hadAutoAlign) angularVelocity = DVector3.zero;
        }

        /// <summary>
        /// 取消从指定索引（含）到末尾的所有机动规划（保留前面的）。
        /// 从 0 取消 = 连预对准目标一起取消 ⇒ 消旋；从后面取消保留前面的预对准。
        /// </summary>
        public void CancelManeuversFrom(int index)
        {
            if (index < 0 || index >= plannedManeuvers.Count) return;
            bool hadAutoAlign = index == 0 && pendingAlignDir.sqrMagnitude > 1e-12;
            plannedManeuvers.RemoveRange(index, plannedManeuvers.Count - index);
            // 从 0 取消：旧引导的整代节点已无意义 ⇒ 撤回；从后面取消：保留前面的阶梯节点
            ClearRendezvousGuidance(invalidateLadderNodes: index == 0);
            if (index == 0)
            {
                pendingAlignDir = DVector3.zero;
                if (hadAutoAlign) angularVelocity = DVector3.zero;
            }
        }

        /// <summary>
        /// 获取队列中第一个启用的机动（不判断是否到期；点火时机由执行循环按"燃烧居中"计算）。
        /// </summary>
        public PlannedManeuver? PeekNextManeuver()
        {
            if (plannedManeuvers.Count == 0) return null;
            var next = plannedManeuvers[0];
            if (!next.active) return null;
            return next;
        }

        /// <summary>
        /// 弹出（移除）第一个已执行的机动。
        /// </summary>
        public void PopExecutedManeuver()
        {
            if (plannedManeuvers.Count > 0)
                plannedManeuvers.RemoveAt(0);
        }

        /// <summary>
        /// ★★ **阶梯计划的代号**（2026-09-21）。阶梯一次规划产出**一对**冲量（起飞 + 到达刹车），
        /// 两发都盖同一个代号。<see cref="InvalidateStaleLadderNodes" /> 用它把**上一条计划**
        /// 尚未执行的节点清掉。
        ///
        /// ## 为什么必须清（这就是"一抽一抽"的来路）
        /// 起飞执行后出队，**刹车还挂在队列里**（它排在 100 秒外）。而阶梯每 30 秒重规划一次，
        /// 新计划从**活状态**另算一对 ⇒ **旧计划那条刹车仍然会执行**，且它是按
        /// "船应该在**旧计划**的到达点" 施加的 ⇒ 一记**按过期计划**打的错向冲量
        /// （实测：一步从 1.5 km 甩到 11.2 km）。
        /// ⇔ 与 MJ 的"有节点就只执行、不立新节点"是同一条纪律的另一半：
        /// **我们的承诺是两发，那么撤回也必须是两发**。
        /// </summary>
        public int ladderPlanEpoch;

        /// <summary>
        /// 阶梯开始新一轮规划时调用：把**上一代号**残留的节点全部移除（本代号 = 即将排入的那一对）。
        /// ⚠ **手动条目不动**（`owner` 判定），玩家的手动机动不该被自动规划擦掉。
        /// ⚠ 正在执行的那一条**已经出队**（`PopExecutedManeuver` 在 `StartBurn` 时调用）⇒ 这里清的是"未来的"。
        /// </summary>
        public int InvalidateStaleLadderNodes()
        {
            int removed = 0;
            for (int i = plannedManeuvers.Count - 1; i >= 0; i--)
            {
                var m = plannedManeuvers[i];
                if (m.owner == PlanOwner.Manual) continue;      // 玩家的，不动
                if (m.ladderEpoch >= ladderPlanEpoch) continue; // 本代或更新 ⇒ 保留
                plannedManeuvers.RemoveAt(i);
                removed++;
            }
            return removed;
        }

        /// <summary>
        /// 队列里**有没有手动条目**（§2.4：引导只在有手动条目时让位；只有引导自己的条目则不让位）。
        /// 扫描本身写在这里（无分配），**判据**统一走 `PlanOwnerRules.GuidanceMustYieldTo`。
        /// </summary>
        public bool HasManualManeuver()
        {
            for (int i = 0; i < plannedManeuvers.Count; i++)
                if (PlanOwnerRules.GuidanceMustYieldTo(plannedManeuvers[i].owner)) return true;
            return false;
        }


        // ── 主动燃烧 (Active Burn) ──

        // ── 姿态控制（魔法动量轮）──
        /// <summary>角速度（rad/s，魔法动量轮直设；每物理步积分到 CurrentOrbit.rotation）。</summary>
        public DVector3 angularVelocity;

        /// <summary>调试/演示用：物理步按**无外力矩 Euler 方程**积分姿态（批2物理档前哨；默认 false）。
        /// 开启后 `ComputeGroupPhysics` 用本组多part 惯量张量演化 ω 与 rotation，演示贾尼别科夫效应；
        /// 不参与生产的魔法档。仅在演示组置 true，不入存档。</summary>
        public bool torqueFreePhysics;
        /// <summary>上次诊断的本地状态 + tick + 剩余 Δv（用于把"真正施加的冲量"从状态里扣出来）。</summary>
        /// <summary>⚠ 存的是**适配层系**的状态（与 `Shepperd` 同系 ⇒ 消除换系机会）。</summary>
        public DVector3 burnDiagR, burnDiagV;
        public double burnDiagTick = -1.0;
        public double burnDiagRemaining = -1.0;
        /// <summary>本步燃烧的**输入快照**（只作读数，不参与任何算式）：ΣF（N）、Σṁ（kg/s）、本步用的质量（kg）。
        /// 为什么要它们（2026-09-27 实测）：逐步指纹定位到"某一步、某一个组的 `remainingBurnDV` 差了
        /// 3.3e-6 m/s"（相对 ~2e-7 ≈ **1–2 个 float ULP**）⇒ 那是**输入里有一次浮点舍入不同**。
        /// 把这三个输入也进字段表 ⇒ 定位会直接说出**是哪一个输入**差的，不必再去 dump。</summary>
        public double burnDbgF, burnDbgMf, burnDbgMass;
        /// <summary>本段燃烧里**这个组被诊断过几次**（2026-09-26 加）。
        /// 为什么要它：`burnDiagTick` 是**绝对时刻**（不能进指纹：逐轮必然不同 ⇒ 第二例"绝对量进哈希"陷阱），
        /// 而"被诊断到几次"是**无量纲计数** ⇒ 既能进指纹，又能当 R3 的**直接判据**
        /// （诊断块原来挂在并行体里 + 全局静态节流 ⇒ 实测 64 个燃烧组每窗口只有 **1 个**被诊断，
        ///  其余 63 个静默跳过 ⇒ 这个计数在改前是"1 个组=1、63 个组=0"）。</summary>
        public int burnDiagRuns;

        /// <summary>燃烧弧预测的输入（**两系同轴 ⇒ 就是系统系**；批6 S2 起写入点直传 `dV`，
        /// 跨系只经 `OrbitView` 的进/出两个口；点火时暂存，供"预测 vs 实测"并排读数用；运行期，不序列化）。</summary>
        public DVector3 burnArcDvAdapter;
        public double burnArcNodeUt = -1.0;
        public double burnArcSeconds;

        /// <summary>诊断：活差额律因**差额反向**（超调）而收工的次数。超调在正常情况下不该发生
        /// （差额是每拍重测的）⇒ 非零就是在提示"某处的 Δv 记账与活状态不一致"，必须查。</summary>
        public int burnReversedStopCount;

        /// <summary>诊断：因**节点窗口已过**而停火的次数（见 `SpaceGameComp.BurnNodeWindowPostSeconds`）。
        /// **正常燃烧恒为 0**：燃烧以节点为中心、收手远早于窗口截止 ⇒ 非零就是在提示
        /// "某一发追着过期靶在烧"（2026-09-21 那个 4412 m/s / 9080s 的形态）——这是一条**故障计数**。</summary>
        public int burnNodeWindowAborts;

        /// <summary>诊断：因**差额小到没意义**（&lt; `BurnDeficitIgnoreFloor`）而停手的次数
        /// （见 `SpaceGameComp.BurnDeficitIgnoreFloor`）。收尾段为正常值；**非零很多**说明收尾在反复磨。</summary>
        public int burnDeficitIgnoredCount;

        /// <summary>
        /// ★★ **节点的目标速度**（**游戏系**惯性向量；点火时由计划算出，整段燃烧**不变**）——
        /// **这是 MJ 的靶**（`MechJebModuleNodeExecutor.NextDirection` 里 `node.GetBurnVector(Orbit)`：
        /// 靶 = 结果轨道的**速度**，活的是 `Orbit`）⇒ 每拍 `dv_rem = v_target(UT) − v_live_prop(UT)`。
        /// ⚠ 它**没有几何分支**（靶是固定向量，任何活状态都能算差额）⇒ 不存在"靶跟着本点跑"那一类失效。
        /// （我们一度换用过"意图轨道 (a,h向量) + 出范围政策"当靶：台架 e 好两个数量级，但**真实几何上
        ///   出范围支是自证式的**，游戏实测 430.01 m/s 只交付 164.9 ✗ —— 用户裁定"暂时全盘照抄 MJ"
        ///   ⇒ 换回本靶；精度由收口微调负责，见 `trimTarget*` 那组字段。）
        /// = `GameFrameStateAtUT(点火前轨道, 节点UT).v + 计划 Δv`。
        /// ⚠ 未设（微调条目、无计划向量的燃烧）⇒ 退回 RTN 分量律（见 `burnRadial` 字段）。
        /// </summary>
        public DVector3 burnTargetV;
        public bool burnHasTargetV;
        /// <summary>B6/ArchTest：活差额律的靶已达成（用了靶速度 ⇒ 允许 BurnStep 结束燃烧）。
        /// 未达成时即使本步“计划预算”烧完也继续，下一步从活状态重算差额（有限燃烧的重力项）。</summary>
        public bool burnTargetReached;
        /// <summary>诊断：活差额律**最后一步**看到的剩余 Δv 与它在节点处的活速度（不落盘）。</summary>
        public double burnLastDeficit;
        public DVector3 burnLastVLiveUt;
        public DVector3 burnLastDeficitDir;
        public double burnLastAppliedDv;

        /// <summary>B2.2 诊断：实际燃烧的起止 tick（-1 = 未记录）。用于打印"实际燃烧时长"。</summary>
        public double burnActualStartTick = -1.0;
        public double burnActualEndTick = -1.0;

        /// <summary>
        /// B2.3：点火时刻的 RTN 分量（"提交的解"，方向刷新时只换框架、不换解）。
        /// ⚠ **本轮起降级为退路**：有计划向量的燃烧走 `burnTargetV` 的活差额律（更精确，见那个字段）；
        /// 只有拿不到目标速度的燃烧（微调条目、引导逐帧指令）才用这条 RTN 律。
        /// ⚠ **分解必须用正本**（`CurrentOrbit` 的位置/速度），所以它**不在 `StartBurn` 里算** ——
        /// 那里只有 `positionBody/velocityBody`（**副本**，与正本差一个帧速 ≈14–16 m/s ⇒ 方向偏 ~0.13°）。
        /// 改由刷新回路在**首次刷新时**用正本分解一次（`burnComponentsInit`），见 `SpaceGameComp`。
        /// </summary>
        public double burnRadial, burnTangential, burnNormal;
        public bool burnComponentsInit;

        /// <summary>
        /// 用**计划自带的解**（节点时刻的 RTN 分量）设定本次燃烧的方向锚点。
        /// ⚠ 为什么必须走这条路（2026-09-19 实测）：`StartBurn` 只拿到**世界向量**，若在**点火帧**重新分解，
        /// 锚点就从节点漂到点火时刻（相差 ω·49s ≈ **2.44°**）——实测「模型方向 vs 执行方向」恒为 2.23–2.36° ✓
        /// 与之吻合，而 e 因此停在 7.5e-3（台架同口径 7.465e-3）。**计划里本来就有正确的分量**
        /// （`OrbitAdapter.TryQueuePlan` 用**节点时刻**位姿分解 ⇒ `PlannedManeuver.radialDV/…`），直接用即可。
        /// 分量全 0（老档/引导逐帧指令没有节点解）⇒ 保持未初始化，由刷新回路退回"点火帧分解"（对那类燃烧是对的）。
        /// </summary>
        public void SetBurnSolution(double rad, double tan, double nor)
        {
            if (rad == 0.0 && tan == 0.0 && nor == 0.0) return;
            burnRadial = rad;
            burnTangential = tan;
            burnNormal = nor;
            burnComponentsInit = true;
        }

        /// <summary>
        /// B2.2：**点火前**的轨道 —— 前向模型（`BurnArc`）的基准。
        /// ⚠ 必须是**点火前**的（模型自己会推进到点火时刻）；喂"当前（已烧过的）轨道"会让预测
        /// 随**已烧累计 Δv** 漂移（2026-09-19 台架实测：t=100s 时差 694 m/s ≈ 已烧 Δv 本身）。
        /// </summary>
        public OrbitView burnPreBurnOrbit;
        public bool burnHasPreBurnOrbit;

        /// <summary>B2.2 诊断：执行器**实际所用**网络的 ΣF·dt 与 Σṁ·dt（⇒ 真实 ve = F/ṁ 的唯一来源）。
        /// 合并口径（全部网络）会把未点火的 RCS 网算进来 —— 那正是"白送 Δv"假警报的来源。</summary>
        public double burnThrustDt, burnFlowDt;

        /// <summary>
        /// **本次燃烧的命令加速度**（m/s²，0 = 未设 ⇒ 退回全油门）。点火那一步锁存 = `F参与/m₀`
        /// （用户 2026-09-19 裁定："机动 = Δv + 加速度，油门随时适配质量"）。
        /// 整段按它走 ⇒ Δv 均匀摊在居中窗口里 ⇒ 冲量质心落在节点上（全油门会因料耗让 a 上升 ⇒ 质心偏晚 ~3.5 s）。
        /// ⚠ **0 有两种含义**，由 <see cref="burnThrottleCap" /> 区分（2026-09-21）：
        ///   · `burnThrottleCap ≤ 0` ⇒ **真的没设**（RTN 退路律）⇒ 全油门 + 逐拍递减记账；
        ///   · `burnThrottleCap > 0` ⇒ **有节点靶**：不用常量加速度，油门由**剩余 Δv** 推（见那个字段）。
        /// </summary>
        public double burnCommandAccel;

        /// <summary>
        /// ★★ **本轮燃烧"还差多少 m/s"**（`&gt; 0` ⇒ 走**需求推油门**，MJ 的 `ThrustForDv` 口径；2026-09-21）。
        ///
        /// ## 为什么要把这个数单列出来（读 MJ 本体之后改的）
        /// MJ：`ThrustForDv(_dvLeft, τ)` ⇒ `desiredAccel = _dvLeft/τ` ⇒ `throttle = desiredAccel/MaxAccel`
        /// ⇒ 展开就是 **`throttle × MaxAccel × τ = _dvLeft`** —— **油门与"还差多少"是同一件事**，
        /// 而且 `_dvLeft` 每帧从活轨道重算 ⇒ **没有任何一帧可以被锁死**。
        /// 我们原来：点火锁 `burnCommandAccel = Δv/t_b` 常量化 ⇒ `BurnStep.Compute` 按它烧满整段，
        /// **发动机实际给不出那个加速度时，整段燃烧都按错的加速度走，没有任何一帧会去纠正它** ✗。
        ///
        /// ⇒ 现在（仅有节点靶的燃烧）**不用常量加速度**，每步把"还差多少"喂进来：
        ///   `dv本步 = min(a_可用·dt, burnThrottleCap)` ⇒ 自然就是**需求推油门**，
        ///   且需求（差额）与可得（`a_可用·dt`）**用同一把尺子量**，两边不可能分家。
        ///
        /// 认一个账：供/需仍可差一个数量级（船就是给不出），但那时燃烧会**因为差额真的减小而收手**，
        /// 而不是像记账式那样"我欠 431.7、永远还不完"。
        /// </summary>
        public double burnThrottleCap;

        /// <summary>本次机动是否已做过**点火相位扫描**（B2.2 弧补偿）：等待点火的每个物理步都扫一遍
        /// 会白烧 CPU + 刷屏 ⇒ 每次机动只扫一次（`StartBurn` 复位）。</summary>
        public bool burnPhaseSwept;

        /// <summary>头步定点读数计时（只打头 6 步；`StartBurn` 复位）。</summary>
        public int burnHeadStepsLogged;

        /// <summary>
        /// **姿态角速度倍率**（默认 1.0；2026-10-28 用户裁定）。
        /// 发射爬升时由 `RocketLaunchController` 置 3.0：大惯量火箭在一级燃烧期间靠惯量限速
        /// 只能维持 ~24° 偏差（贴着 25° 点火锥上限，随时可能熄火），
        /// 发射段给姿态环节一个明确的倍率，出段即复位。
        /// </summary>
        public double attitudeRateScale = 1.0;

        /// <summary>
        /// **"零 Δv"步计数**（2026-10-28 临时探针）：燃烧中网络有推力、油也还在，却算出 `burnDv = 0`
        /// ⇒ `BurnExhausted` 被置位、质量冻结（用户实报"烧到 219 t 还剩 155 t 油就不掉了"）。
        /// `StartBurn` 复位；探针只在前 5 次与每 50 次打一行。
        /// </summary>
        public int burnZeroDvSteps;

        /// <summary>**"零扣料"步计数**（2026-10-28 探针）：燃烧中 `PropellantConsumed ≤ 0` 的连续步数。</summary>
        public int burnDrySteps;

        /// <summary>
        /// **冲量探针已打印步数**（2026-10-29）。**不随 `StartBurn` 复位** ——
        /// 发射链每步重开燃烧，用 `burnStepAuditCount` 当闸门会每步清零 ⇒ 20 发并发时每秒 4 万行日志、
        /// 进程被 I/O 拖死（实测：5 秒写 12 MB、00:38:25 起日志停止、游戏卡住）。
        /// </summary>
        public int impulseProbeSteps;

        // ── 意图轨道靶（B2.2-b 的收口微调 + "MJ 优先"第 5 件起的**整段燃烧**共用）─────────────
        // 一个靶，两种时机（收口时调一次 = 微调；逐拍调 = 燃烧律）⇒ **只允许一份算式算它**（J2）。
        /// <summary>本次机动的**意图轨道**：计划在节点处的**脉冲**结果 `(a, |h|, ĥ)`。
        /// ⚠ 必须是"脉冲结果"而不是"模型预测结果" —— 意图 = 计划想做的事，不是模型猜我会做成什么。
        /// ⚠ `trimTargetHVec`（**游戏系**角动量向量）不是可选项：**纯面外变轨不改 a 也不改 |h|**
        ///   ⇒ 只有标量靶时整段平面改变会被判"无差额"而**静默跳过**（台架实测：一步不烧 ✗）。</summary>
        public double trimTargetA, trimTargetH;
        public DVector3 trimTargetHVec;
        public bool trimHasTarget;
        /// <summary>本机动是否允许收口微调。★ **默认 false**（2026-09-21 用户裁定：“老老实实移植融合 MJ”）。
        /// **微调是我们自己的东西，上游没有** ⇒ 它属于“最后总修缮”阶段的补充，
        /// 在移植验收完成前**不该在流程里**（否则又是“两套机制互相插队” ✗）。
        /// 对照实验/将来接回时显式置 true ✓。</summary>
        public bool trimEnabled ;
        /// <summary>本次机动是否已经微调过（**每次机动最多一次**：二次微调要靠它收敛 ⇒ 先不做，避免自激）。</summary>
        public bool trimFired;
        /// <summary>上次微调的 Δv 大小（诊断；`StartBurn` 复位）。</summary>
        public double trimLastDv;
        /// <summary>**主燃**的累计 Δv（微调排队那一刻抓走 —— 微调自己 `StartBurn` 会把 `appliedBurnDV` 清零，
        /// 不抓的话验收读数只剩微调那 1.3 m/s）。0 = 本机动没走微调。</summary>
        public double trimMainBurnDv;

        // ── 对照验收开关（2026-09-19）：**per-group**（几条船会同时燃烧，全局静态会互相污染 ✗）
        //    默认 false ⇒ 生产行为逐位不变。见 `BurnABAcceptanceRunner`。
        /// <summary>冻结燃烧方向（关掉"按当前 RTN 重取"）。</summary>
        public bool abFreezeDirection;
        /// <summary>用全油门（关掉"命令恒定加速度"政策）。</summary>
        public bool abFullThrottle;
        /// <summary>不做预对准（测点火瞬间姿态是否参与）。</summary>
        public bool abNoPreAlign;
        /// <summary>**强制点火相位**（对照实验用；NaN = 走自动扫描）。单位 s，正 = 推迟点火。</summary>
        public double abForcePhaseShift = double.NaN;
        /// <summary>**单脉冲执行**（对照 F）：到节点把计划 Δv 一次施加（不走有限燃烧）——
        /// 判据的"期望"算的正是这一件事 ⇒ 它直接把"计划→空间的映射"与"有限燃烧的执行"分开。</summary>
        public bool abOneShotImpulse;
        public double abNodeTick = -1.0;

        /// <summary>逐步对账（2026-09-19）：`burnLastCmdDv` = 本步命令的 Δv，`appliedDvAudit` = 累计实收。</summary>
        public double burnLastCmdDv;
        public double appliedDvAudit;
        public int burnStepAuditCount;

        /// <summary>
        /// 相位扫描选出的**点火偏移**（s）。⚠ 必须存在群上：它是**跨物理步**的结果
        /// （扫描在等待期某一步做一次、点火在之后某一步才发生）。
        /// **实测教训（2026-09-19）**：第一版把它写成 `ignitionTick` 计算处的**局部变量** ✗ ⇒
        /// 每一步都被重置为 0 ⇒ 扫描白扫（点火仍在 −53、e 与未扫描的第⑤轮**逐位相同** 1.108e-3 ✓ 反证）。
        /// 与"a_cmd 锁存"同一个坑：**跨步状态不能放局部**。
        /// </summary>
        public double burnPhaseShift;

        /// <summary>
        /// 魔法动量轮的**锚点**限速（rad/s）：默认无Map船（5000 kg 结构干质量 / 40×20×80 盒体）的
        /// **最慢主轴**。批 1 起它只是锚点——实际限速按 τ_ref/I_轴 现算（见 <see cref="MaxAngularRate"/>）。
        /// </summary>
        public const float DefaultMaxAngularRate = 5f * Mathf.Deg2Rad;
        /// <summary>
        /// 实际限速（rad/s）。&lt;=0 = 未覆盖（走 I 加权公式；旧存档无感）。
        /// 导弹等敏捷自主体出生时按 MissileDef.maxAngularRateDegrees 覆盖（D15 主推对准收敛速度）。
        /// </summary>
        public float maxAngularRate = -1f;
        /// <summary>
        /// 实际限速（rad/s，**最慢轴**口径）：def 覆盖优先；否则 `clamp(τ_ref / I_最慢主轴, 0.2, 30)°/s`
        /// （批 1，2026-10-17）。无惯量数据（headless / 无部件）⇒ 回落 <see cref="DefaultMaxAngularRate"/>。
        /// 具体某根轴（滚/偏/俯）的限速见 `MaxAngularRateAboutBodyAxis`——大船往往先被各轴差异卡住。
        /// </summary>
        public float MaxAngularRate
        {
            get
            {
                if (maxAngularRate > 0f) return maxAngularRate;
                var r = InertiaResult;
                if (!r.HasData || !(r.p1 > 0.0)) return DefaultMaxAngularRate;
                return (float)InertiaTensor.RateForMoment(r.p1);
            }
        }

        /// <summary>
        /// 船体局部轴向 → 世界方向（当前姿态 rotation 旋转）。
        /// 方向感知推进 / 转向机动的唯一换算入口。
        /// </summary>
        public DVector3 GetThrustAxisSystem(ThrustAxis axis)
        {
            return DVector3.Rotate(axis.ShipVector(), CurrentOrbit.rotation);
        }

        /// <summary>
        /// **原地更新燃烧指令**（2026-10-27，制导重构第 1 步）：不重开状态机、不清点火质量/实际起始 tick、
        /// 不置脏派生量。每步只把"这一步该交付的 Δv"与对应命令加速度写进去。
        /// 未在燃烧时退化为 `StartBurn`（只发生一次）。
        /// </summary>
        public void SetBurnCommand(DVector3 dV, double commandAccel)
        {
            if (!HasActiveBurn)
            {
                StartBurn(dV);
                burnCommandAccel = commandAccel > 0.0 ? commandAccel : 0.0;
                return;
            }
            if (dV.sqrMagnitude > 1e-12)
                burnDirectionSystem = dV.normalized;
            remainingBurnDV = dV.magnitude;
            appliedBurnDV = 0.0;          // 本步指令的交付从零计
            burnCommandAccel = commandAccel > 0.0 ? commandAccel : 0.0;
            BurnCommandVersion++;
        }

        /// <summary>
        /// 开始一次缓释燃烧。
        /// </summary>
        /// <param name="dV">本次燃烧的总 dV 向量 (m/s)</param>
        /// <summary>
        /// **点火唯一入口**（2026-10-27）：所有"给船一段 Δv"都构造 <see cref="ManeuverPlan" /> 走这里。
        /// 只负责把计划变成燃烧状态（选网/模式/命令加速度/方向锚点/标志），不做安全校验与排队。
        /// 返回 false = 被拒（损毁/控制核心/欠电/零 Δv）。
        /// </summary>
        public bool BeginBurn(in ManeuverPlan plan)
        {
            if (IsCrippled) return false;
            if (plan.dv.sqrMagnitude <= 1e-12) return false;

            switch (plan.networks)
            {
                case ManeuverNetworkMode.InheritPlayer:
                    SetBurnNetworkOverride(PlayerEnabledNetworks, enabledNetworkRcsOnly);
                    break;
                case ManeuverNetworkMode.All:
                    SetBurnNetworkOverride(null, plan.useRcs);
                    break;
                default:
                    SetBurnNetworkOverride(plan.useNetworks, plan.useRcs);
                    break;
            }

            if (plan.clearCircularOrbit && IsCircularOrbit)
                ExitCircularOrbit();

            StartBurn(plan.dv);
            if (!HasActiveBurn) return false;

            burnCommandAccel = plan.burnSeconds > 0.0
                ? plan.dv.magnitude / plan.burnSeconds
                : plan.commandAccel;
            if (plan.hasSolution)
                SetBurnSolution(plan.radialDV, plan.tangentialDV, plan.normalDV);
            if (plan.isCorrection) burnIsCorrection = true;
            if (plan.armRendezvousGuidance) ArmRendezvousGuidance();
            if (plan.nodeTick >= 0 && abOneShotImpulse) abNodeTick = plan.nodeTick;
            GetLinkedMapComp()?.SetThrustersBurning(true);
            return true;
        }

        public void StartBurn(DVector3 dV)
        {
            // B5：关键件损毁 ⇒ 失能，不再接受新点火（已有点火由 MarkPartDestroyed 的 EndBurn 熄掉）。
            if (IsCrippled) return;
            // 控制核心（2026-10-26）：**装过核心的舰失去核心 ⇒ 拒绝点火**；没装过的舰不受影响。
            if (!IsControllableNow())
            {
                BurnIgnitionBlockedNoControl = true;
                return;
            }
            BurnIgnitionBlockedNoControl = false;
            // §5（用户 2026-10-26 裁定）：**欠电 ⇒ 拒绝点火**（不是按比例降推力）。
            // 判据 = 发电 + 电池可放能否全额覆盖推进通道；无推进功耗的旧船恒通过（零行为变化）。
            if (!CanSupplyPropulsionNow())
            {
                BurnIgnitionBlockedByPower = true;
                return;
            }
            BurnIgnitionBlockedByPower = false;
            BurnEndedByPower = false;
            BurnCommandVersion++;
            HasActiveBurn = true;
            burnTargetReached = false;
            // ⚠ **信号失效点之一**：点火会让 SignalStrength 跃升（[V8] 的 burning 项）——
            // 这正是"冷发射信号 profile"的来源（巡航低信号 / 末段高信号）。
            // `HasActiveBurn` 是 public 字段，钩子只能挂在本方法与 EndBurn 两处；
            // **任何别处直接赋 `HasActiveBurn = true` 都会让派生信号静默过期**（与 AVM:824 的"派生点"同类陷阱）。
            derivedDirty = true;
            burnDirectionSystem = dV.normalized;
            remainingBurnDV = dV.magnitude;
            appliedBurnDV = 0.0;
            burnStartMass = 0.0;      // 首次 burn tick 时记录（此时质量已知）
            burnDiagRuns = 0;         // R3 的直接判据面：新一段燃烧重新计数
            burnActualStartTick = -1.0; // 诊断：实际点火 tick（B2.2 的"预测 vs 实测"要用）
            burnActualEndTick = -1.0;
            burnConsumedPropellant = 0.0;
            burnThrustDt = 0.0;
            burnFlowDt = 0.0;
            burnCommandAccel = 0.0;        // 由点火块按 `Δv/t_b` 设定（见执行器注释）
            burnThrottleCap = 0.0;         // 0 = 未启用"需求推油门"（RTN 退路律走记账式）；>0 见该字段说明
            burnPhaseSwept = false;        // 新机动 ⇒ 允许再扫一次点火相位
            burnPhaseShift = 0.0;          // …且偏移归零（扫描结果在 StartBurn 之前已用完）
            burnHeadStepsLogged = 0;       // 头步读数重新开始
            burnZeroDvSteps = 0;           // 零 Δv 步计数重新开始（2026-10-28 探针）
            burnDrySteps = 0;              // 零扣料步计数重新开始（2026-10-28 探针）
            burnLastCmdDv = 0.0;
            appliedDvAudit = 0.0;
            burnStepAuditCount = 0;
            burnHasPreBurnOrbit = false;   // 点火 tick 由执行侧填（那时才有"点火前的轨道"）
            burnHasTargetV = false;        // 目标速度同上（**必须复位**：不复位会继承上一段的残留靶 ⇒ 静默烧错方向）
            // ⚠ **等对准计时也必须复位**（2026-09-21 实测抓到的真 bug）：我上一版加了"没对准就不点火"
            //    的门却没复位这个时间戳 ⇒ 第二次以后的机动一算就"等超时" ⇒ **门形同虚设** ✗✗
            //    （日志证据：`带角度点火 4 次`，角度 93.4° / 74.0°，其中 93° 根本进不了 10° 主推锥）。
            burnIgnitionAlignWaitTick = -1.0;
            burnComponentsInit = false;    // RTN 分量由刷新回路用**正本**分解一次（见字段注释）
            trimHasTarget = false;         // 意图轨道由点火块按计划（脉冲结果）算出
            trimFired = false;             // 每次机动最多微调一次

            // ── B2.3 燃烧弧方向律（§2.2-2：`burnDirectionSystem` 原来整段燃烧期冻结）──
            // 解 = 点火时刻的 **RTN 分量**（在 `SpaceGameComp` 的刷新回路由**正本**分解一次），
            // 燃烧中**每步**用**当前 RTN** 重新合成世界方向 ⇒ 解不动、方向按时间取。
            //
            // ⚠ 2026-09-19：原来按"扫过 2°"的节拍刷新（`BurnArcPolicy`）。台架（`BurnArcExecutorBench`）
            //   实测该量化**吃掉 4.8 倍残差**（e 3.76e-3 vs 每步连续 7.79e-4；不变量 0.9897 vs 0.9975，
            //   而游戏实测 0.9897 —— 逐位对上 ⇒ 量化就是那个差）。
            //   政策的理由（"每 tick **重解**太贵"）不适用：这里只是把**同一个解**在当前 RTN 里重新表达
            //   （~15 次浮点 + 一次归一化），没有求根也没有搜索 ⇒ 节拍机制整体删除。
            //
            // ⚠ **也不能在这里分解**：本方法的入参只有 `positionBody/velocityBody`（**副本**，
            //   与正本差一个帧速 ≈14–16 m/s ⇒ 方向偏 ~0.13°）。这正是 604 行那段注释说的同一个 trap 的
            //   另一半 —— 2026-09-19 修刷新时漏了这处分解。
        }

        /// <summary>
        /// 立即结束燃烧（剩余 dV 丢弃）。
        /// </summary>
        public void EndBurn()
        {
            BurnCommandVersion++;
            HasActiveBurn = false;
            derivedDirty = true;   // 失效点之一（熄火 ⇒ burning 项归零），与 StartBurn 成对
            remainingBurnDV = 0.0;
            RestorePlayerNetworkSelection();   // 2026-10-27：机动覆盖结束 ⇒ 回玩家选网（不再硬置 false）
            burnIsCorrection = false;
            intakeFlameoutLogged = false;   // [A5] 熄火读数复位（下次点火重新有机会报一次）
            envelopeRoutedOffLogged = false; // [A4] 包线归零读数复位（同上）
        }


        // ── 轨道保持 (OrbitKeeping) ──

        /// <summary>
        /// 进入轨道保持模式。
        /// </summary>
        /// <param name="whitelistBodyIds">不抵消的天体 ID 白名单。白名单内的天体的引力正常参与轨迹积分，
        /// 白名单外的引力被抵消并消耗 dV。</param>
        public void EnterOrbitKeeping(HashSet<int> whitelistBodyIds)
        {
            HasOrbitKeeping = true;
            orbitKeepingWhitelist = whitelistBodyIds ?? new HashSet<int>();
        }

        /// <summary>
        /// 退出轨道保持模式。清空白名单。
        /// </summary>
        public void ExitOrbitKeeping()
        {
            HasOrbitKeeping = false;
            if (orbitKeepingWhitelist != null)
            {
                orbitKeepingWhitelist.Clear();
                orbitKeepingWhitelist = null;
            }
        }


        // ── 交会引导 (Rendezvous Guidance) ──

        /// <summary>交会引导状态已登记（精确交会模式）。<see cref="rendezvousArmed"/> 为 false 时表示等待出发段执行。</summary>
        public bool HasRendezvousGuidance ;
        /// <summary>出发段已点火执行，引导生效（闭环修正开始）。false = 等待出发段。</summary>
        public bool rendezvousArmed ;
        /// <summary>引导为"近距离接近"模式（动态到达时刻、无出发段）。true 时绕开出发段与固定到达时刻逻辑。</summary>
        public bool rendezvousIsApproach ;
        /// <summary>目标群 ID。</summary>
        public int rendezvousTargetId = -1;
        /// <summary>
        /// 目标局部系瞄准偏移（m，序列化）：瞄准 targetGroupPos + 目标实体偏移 + 姿态旋转(offset)。
        /// 零 = 瞄准 group 中心（原行为）。跳帮落点用：落点格子 → 目标空间站局部系偏移。
        /// </summary>
        public DVector3 rendezvousTargetOffset;
        /// <summary>到达时刻（physicsTick 基准）。</summary>
        public double rendezvousArrivalTick;
        /// <summary>转移方向（ShortWay/LongWay）。</summary>
        public bool rendezvousPrograde = true;
        /// <summary>引导修正烧使用的工质网络（继承出发机动；null = 全启用）。</summary>
        public HashSet<PropellantDef>? rendezvousUseNetworks;
        /// <summary>上次引导检查的 physicsTick（节流用，不序列化；载入后首次检查立即进行即可）。</summary>
        public double rendezvousLastCheckTick = double.NegativeInfinity;

        // ── 交会精度（**2026-09-22 用户裁定：`100 m` 太近，阈值可以换 `1 km` / `10 km`**）──
        /// <summary>交会**完成**的距离判据（m）= **1000（用户裁定放宽）**。
        /// ⚠ 演进史（留档，别再来回搬）：300 m（测试）→ 2000 m（2026-09-20"最后几米太贵"，实测一轮 1284 发微修正）
        /// → 100 m（2026-09-21 跟上游 MJ）→ **1000 m（2026-09-22 用户裁定：100 m 太近）**。
        /// ⚠⚠ **`100 m` 的实际代价（低推力档实测，这是本次放宽的直接依据）**：船每次都贴到 **246 m**、
        /// 残余相对速度 2.5 m/s，而 100 m 的门把它挡在外面 ⇒ 一路磨到燃料被判不可行
        /// （`BLOCKED maxDV=3361.9`）。**246 m 在任何实用意义上就是"到了"**——
        /// 而这个数是**引擎推力**决定的（低推力船做不到更细），不是规划错。
        /// ⇒ **判据不该严到"只有高推力船才够得着"**。要更严/更松请用 <see cref="rendezvousGoal" /> 的各预设：
        ///   要贴近用 `Precise(100, 1)`、要更松用 `Loose(10000)`、战斗用 `Combat()`。
        /// </summary>
        public double rendezvousArriveDist = 2_000.0;   // 2026-10-01 用户裁定：**2 km**
        // ⚠ 与速度侧**成对**看（见下面 `rendezvousArriveRelVel` 的注释：只放距离不放速度 ⇒ 船还在动就"到"了 ✗）。
        // ★ **"这条线是不是太低"的实测账（2026-10-01）**：
        //   · **月球档**做到过 **972.9 m / 5.7 m/s** ⇒ 满足 (2 km, 10 m/s) ⇒ 这一档这条线**可达** ✓；
        //   · **地球·低推力档**：逐发成对读数里"速度配平"的那几发落在 **5.5 / 6.7 / 15.6 / 21.5 km** ✗
        //     ⇒ 该档**同时**满足 (≤2 km 且 ≤10 m/s) 的最近记录 ≈ **5.5–6.7 km** ⇒ **2 km 对这一档偏低**。
        //     但**不是物理极限**：那是末端"位置+速度联合"没做完的**能力缺口** ⇒ 修终端能抬上去。
        //   ⇒ 要按档放松就用**既有预设** `Loose(10000)`（10 km / 10 m/s，成对 ✓）/ `Combat()` / `Precise(100,1)`；
        //     ⚠ **别改这里的全局默认** —— 那会连带改月球档与跳帮那些的验收 ✗。
        /// <summary>★ **"停下/收口"的距离尺度**（m）——**与完成容差分开**是纪律（哪怕它们取同一个数）。
        /// 上游 MJ 只有一个 `desiredDistance = 100` 同时当两者用，之所以不矛盾是**因为它足够小**：
        /// 接近级按 `dist/100`（≤ `maxClosingSpeed` 100 m/s）闭合 ⇒ 一次闭合就能把 20 km 收到 ~100 m
        /// ⇒ **停下门比接近级的结果还严 ⇒ 永不打架** ✓。我们一度把完成容差放宽到 2 km 并照抄同一个数
        /// ⇒ 门比接近尺度松 20 倍 ⇒ **2 km 就"停"而接近级还能干 100 m/s ⇒ 停-接近打架 = 过冲** ✗✗
        /// （实测：`Intercept`×58 ⇄ `MatchAtClosestApproach`×27、总 Δv 8438 vs 估计 642）。
        /// ⇒ 收口/清队列/在最近点停下这一族判据一律用它（= 上游的 100 ✓）。
        /// ⚠ **两个字段保留**（不合并）：数值允许不同（例如日后想把完成容差放宽），但"停下"永远必须小 ✓。</summary>
        public double rendezvousMatchDist = 100.0;
        /// <summary>★ **本机动是否允许收口微调**（由阶梯按**选中的级**置位）。
        /// 微调的语义是“把 `(a,|h|)` 残差收干净”（它自己的类注释）⇒ **只有“立轨道”那几级才该用**：
        /// `MatchPlanes` / `Hohmann` / `PhasingOrbit` / `Circularize`；
        /// 而 `Intercept` / `MatchVelocitiesNow` / `MatchAtClosestApproach` 的使命是“**到达**”，
        /// 它们的“意图轨道”是“过到达点的那条轨道”、**不是目标船的轨道**
        /// ⇒ 微调会每轮打一发径向烧把船往那条轨道上拽、**跟拦截的闭合对抗** ✗✗
        /// （实测：42 轮×(拦截+微调) ≈ 2500–3000 m/s；一轮只收 332 m 而计划该收 ~9.5 km）。</summary>
        public bool trimAllowedByRung = true;
        /// <summary>点火前“等对准”的起算 tick（-1 = 本机动还没开始等）。见 `IgnitionAlignGateDeg`：到了点但主推轴还没对准 ⇒ **等**（上游纪律）。</summary>
        public double burnIgnitionAlignWaitTick = -1.0;
        /// <summary>交会**完成**的相对速度判据（m/s）= **10.0（2026-09-22 用户裁定时随距离一起放宽）**。
        /// 演进：2.0 → 5.0（配 2 km 站位）→ 1.0（2026-09-21 跟上游 MJ）→ **10.0**。
        /// ⚠ **必须与 <see cref="rendezvousArriveDist" /> 一起调**（只放距离不放速度 ⇒ 距离够了但船还在
        /// 10 m/s 上飘 = **假完成**）；本库 2026-09-22 的取值对是 **1000 m / 10 m/s**（约"半个接近段的量级"）。
        /// ⚠ 留档的实测教训（当时是对的，现在仍成立）：**若速度判据比距离判据更宽，距离再窄也停不下来**
        /// ——那时卡住的正是相对速度。⇒ 放宽可以，但两者要**成对**放，不能只放一个。
        /// 要更严：`RendezvousGoal.Precise(100, 1)`（上游口径）。</summary>
        public double rendezvousArriveRelVel = 10.0;

        /// <summary>★★ **交会的目的**（2026-09-21）。默认 = 由 <see cref="rendezvousArriveDist" /> /
        /// <see cref="rendezvousArriveRelVel" /> 组装出的**精确贴靠** ⇒ **既有行为逐位不变**。
        /// 要"为战斗而交会"等其它目的时设它（见 <see cref="RendezvousGoal" />）。
        /// ⚠ 设了它就**以它为准**：上面那两个字段只作为 <see cref="Kind.Precise"/> 的入参来源。</summary>
        public RendezvousGoal rendezvousGoal;

        /// <summary>**当前生效的到达判据**（唯一权威）。未显式设过 <see cref="rendezvousGoal" /> 时
        /// 用两个旧字段组装精确贴靠 ⇒ 兼容既有全部调用点。</summary>
        public RendezvousGoal EffectiveRendezvousGoal
            => rendezvousGoal.distanceTol > 0.0
                ? rendezvousGoal
                : RendezvousGoal.Precise(rendezvousArriveDist, rendezvousArriveRelVel);

        /// <summary>
        /// ★★ **到达判据的统一求值口**（2026-09-22）——把"目的需要哪些量"这件事**收在一处**：
        /// 距离/速度目的只用 `positionBody`/`velocityBody`；**相位目的还要目标的位置**（算有符号相位差），
        /// 且它的"到位门"读的是**半长轴差**（见 `RendezvousGoal.IsMet` 三参版）。
        ///
        /// ⚠ 为什么不让调用方自己拼：`SpaceGameComp` 那边已经在用它做完成判据与捷径，两处**必须同一个口径**
        /// （本会话"改一处忘一处"吃过太多次）。**这里返回的 `met` 就是"到达"的唯一答案。**
        ///
        /// ⚠ 半长轴差用 `SemiMajorAxis(r, v, gm)` 现成式，**不另写**；`gm` = 中心天体 GM
        /// （拿不到时只对相位目的失效：保守返回 false，不让它冒充达成）。
        /// </summary>
        /// <param name="target">目标编队（须同中心天体；不同则相位目的保守判 false）</param>
        /// <param name="gm">中心天体 GM（m³/s²）</param>
        /// <param name="phaseErrDeg">输出的**有符号相位差**（度；非相位目的为 NaN）</param>
        public bool IsRendezvousGoalMet(SpaceEntityGroup? target, double gm, out double phaseErrDeg)
        {
            phaseErrDeg = double.NaN;
            var goal = EffectiveRendezvousGoal;

            DVector3 rSelf = positionBody, vSelf = velocityBody;
            double dist = double.NaN, relVel = double.NaN;
            if (target != null)
            {
                DVector3 rTgt = target.positionBody, vTgt = target.velocityBody;
                dist = (rTgt - rSelf).magnitude;
                relVel = (vTgt - vSelf).magnitude;
                if (goal.kind == RendezvousGoal.Kind.PhaseSynced)
                    phaseErrDeg = RendezvousGoal.SignedPhaseDeg(rSelf, vSelf, rTgt);
            }

            switch (goal.kind)
            {
                case RendezvousGoal.Kind.PhaseSynced:
                    if (target == null || double.IsNaN(phaseErrDeg) || !(gm > 0.0)
                        || target.CelestialBodyId != CelestialBodyId)
                        return false;   // 缺量 ⇒ 保守不判达成（不冒充）
                    double aSelf = OrbitMath.SemiMajorAxis(rSelf, vSelf, gm);
                    double aTgt = OrbitMath.SemiMajorAxis(target.positionBody, target.velocityBody, gm);
                    return goal.IsMet(System.Math.Abs(aSelf - aTgt), relVel, phaseErrDeg);
                default:
                    return goal.IsMet(dist, relVel);
            }
        }
        /// <summary>**终端 CW 的交班窗口**（占轨道半径的比例）。**默认保持原值 0.05** —— 它是**收敛性**参数
        /// （远场律在近距离能否收住是几十轮调出来的），2026-09-20 只把它**提成可调**，**不改默认行为** ✓。
        /// 若去掉"磨"之后仍卡，再考虑动它（那时要用"交会耗时 + 收敛"两个判据一起看）。</summary>
        public double rendezvousTerminalFraction = 0.05;

        // ── 区制分派（B2.1，2026-09-20）：准脉冲走阶梯，连续走连续律 ──────────────
        /// <summary>允许本船用**交会阶梯**（准脉冲区）。默认开；对照实验可关掉 ⇒ 生产行为可二分。</summary>
        public bool ladderEnabled = true;
        /// <summary>上次**阶梯**规划的时刻（物理 tick = 秒）。**最小重规划间隔**靠它：
        /// 没有它会出现"每步重规划"（阶梯给出小修正 ⇒ 一步打完 ⇒ 队列又空 ⇒ 又问……）。</summary>
        public double ladderLastPlanTick = double.NegativeInfinity;
        /// <summary>最小重规划间隔（秒）。与"点火死区/控制周期"同族：防"律在算、白算"。</summary>
        public double ladderMinReplanSeconds = 30.0;
        /// <summary>
        /// **远场归属**：本船当前的连续机动由**阶梯**负责（true）还是**连续律**（false）。
        /// ⚠ 必须是**粘滞**的（2026-09-20 游戏内实测）——第一版把归属只放在"规划的拍子"上，
        ///   两次规划之间的空档就落回了连续律：日志里于是交替出现
        ///   `[阶梯] … 排入 2 发` 与 `[Guidance] … correction burn started (new-law) dv=0.4 m/s miss=1e7 m` ✗。
        ///   这是 `PlanLeg` 那条"**模式必须按段决定、一段内固定**"教训的**上一层**版本
        ///   （那里是形状/相位两通道互相抵消，这里是两套机制互相插队）。
        /// </summary>
        public bool ladderOwnsFarField ;
        /// <summary>上次引导修正烧开始的 physicsTick（冷却用，不序列化）。</summary>
        public double rendezvousLastBurnTick = double.NegativeInfinity;

        /// <summary>
        /// **近场终端律开关**（2026-10-01）：开 = 相对状态进入粗门（`TerminalRendezvousLaw.GateMet`）后
        /// **由终端律接管**（位置+速度联合；抢占阶梯、粘滞、带迟滞）；关 = 回退到阶梯（A/B 对照用）。
        /// </summary>
        public bool terminalLawEnabled = true;
        /// <summary>终端律**是否已接管**（运行期、**粘滞**；进入/退出都在引导里判定并打日志）。</summary>
        public bool terminalMode ;
        /// <summary>终端律的下一次重算式 physicsTick —— 终端要的是**密**闭环，不受 `ladderMinReplanSeconds` 节流。</summary>
        public double terminalNextTick = double.NegativeInfinity;

        // 批5.B（2026-09-28，用户："相位让我们的 MJ 接管"）：此处原有**旧律的 8 个跨周期状态字段**
        // （`rendezvousEccRelay` / `rendezvousLaw*` / `rendezvousBestDistM` / `rendezvousNextLogTick`）
        // 与它们的 `ResetRendezvousLawState()`。旧入口撤掉后**外部用户全为 0**（只剩每次登记时把它们
        // 复位成初值）⇒ 连同面板那两行"变轨段/相位段"“本段剩 X/Y h”读数一起退役。
        // 相位与分段一律由**阶梯那一侧**提供（面板的"沿轨相位差"读 `RendezvousGoal.SignedPhaseDeg`）。
        // 要回看：git 历史 / `_backups/20260928-2347-post-legacy-rendezvous-removal-16c22d39b7117707/`。

        /// <summary>
        /// 登记交会引导（精确交会）。出发段机动需带 <see cref="PlannedManeuver.armRendezvousGuidance"/>，
        /// 其点火时调用 <see cref="ArmRendezvousGuidance"/> 使引导生效；出发段被阻止/取消则引导自动作废。
        /// </summary>
        /// <param name="useNetworks">限定使用的工质网络；**null = 全启用**（这是正常用法，不是缺省容错）。</param>
        public void RegisterRendezvousGuidance(int targetId, double arrivalTick, bool prograde, HashSet<PropellantDef>? useNetworks)
        {
            ClearApproachGuidance(); // 互斥：规划交会即取消接近
            HasRendezvousGuidance = true;
            rendezvousArmed = false;
            rendezvousTargetId = targetId;
            rendezvousArrivalTick = arrivalTick;
            rendezvousPrograde = prograde;
            rendezvousUseNetworks = useNetworks != null ? new HashSet<PropellantDef>(useNetworks) : null;
            rendezvousLastCheckTick = double.NegativeInfinity;
            rendezvousLastBurnTick = double.NegativeInfinity;
        }

        /// <summary>出发段点火执行，引导生效。</summary>
        public void ArmRendezvousGuidance()
        {
            rendezvousArmed = true;
        }

        /// <summary>清除交会引导（玩家手动接管 / 出发段失败 / 交会完成）。
        /// <paramref name="invalidateLadderNodes"/> = true（默认）时**撤回该代未执行的阶梯节点**
        /// （2026-10-17：用户实测"断开引导后旧目标的机动作仍会点火"）；手动条目不动。</summary>
        public void ClearRendezvousGuidance(bool invalidateLadderNodes = true)
        {
            bool hadGuidance = HasRendezvousGuidance;
            HasRendezvousGuidance = false;
            rendezvousArmed = false;
            rendezvousTargetId = -1;
            rendezvousArrivalTick = 0.0;
            rendezvousPrograde = true;
            rendezvousUseNetworks = null;
            rendezvousTargetOffset = DVector3.zero;
            terminalMode = false;                                        // 2026-10-01：换目标/引导结束 ⇒ 终端接管失效
            terminalNextTick = double.NegativeInfinity;

            if (hadGuidance && invalidateLadderNodes)
            {
                // 旧引导排下的阶梯节点全部作废；提升代号后 InvalidateStaleLadderNodes 只留手动条目。
                BeginLadderPlanEpoch();
                InvalidateStaleLadderNodes();
            }
        }


        // ── 近距离接近 (Close-Range Approach) ──

        /// <summary>是否处于近距离接近模式（交会引导的特化：动态到达时刻、无出发段）。</summary>
        public bool HasApproachGuidance => HasRendezvousGuidance && rendezvousIsApproach;

        /// <summary>接近目标群 ID（等价 rendezvousTargetId）。</summary>
        public int ApproachTargetId => rendezvousTargetId;

        /// <summary>
        /// 登记近距离接近引导：复用交会引导引擎（Lambert 拦截 + 终端消速），但到达时刻不固定，
        /// 每检查周期按当前距离动态重算（tof = dist / 限速）。无出发段，登记即生效。
        /// 由玩家手动触发（空间信息面板「近距离接近」按钮选择目标）。
        /// </summary>
        public void RegisterApproachGuidance(int targetId, HashSet<PropellantDef>? useNetworks)
        {
            RegisterApproachGuidance(targetId, useNetworks, DVector3.zero);
        }

        /// <summary>
        /// 带瞄准偏移的接近引导：瞄准 targetGroupPos + 目标实体偏移 + 姿态旋转(targetOffset)。
        /// 跳帮落点用（落点 = 目标空间站局部系偏移）；零偏移 = 瞄准 group 中心（原行为）。
        /// </summary>
        public void RegisterApproachGuidance(int targetId, HashSet<PropellantDef>? useNetworks, DVector3 targetOffset)
        {
            ClearRendezvousGuidance(); // 互斥：清除旧的交会/接近引导
            HasRendezvousGuidance = true;
            rendezvousIsApproach = true;
            rendezvousArmed = true;      // 接近无出发段，登记即生效
            rendezvousTargetId = targetId;
            rendezvousArrivalTick = 0.0; // 动态重算，占位
            rendezvousPrograde = true;
            rendezvousUseNetworks = useNetworks != null ? new HashSet<PropellantDef>(useNetworks) : null;
            rendezvousTargetOffset = targetOffset;
            rendezvousLastCheckTick = double.NegativeInfinity;
            rendezvousLastBurnTick = double.NegativeInfinity;
        }

        /// <summary>清除接近引导（等价清除交会引导）。</summary>
        public void ClearApproachGuidance() => ClearRendezvousGuidance();


        /// <summary>
        /// 获取中心天体在**系统系**中的位置偏移（批6 S3-3c：返回类型改成 <see cref="SystemVec3" />，
        /// 于是"拿它去和天体局部量做运算"这种混系会**编译不过**）。
        /// 如果中心天体是行星（地球自身，即系统系原点），返回零向量。
        /// </summary>
        public SystemVec3 GetCentralBodyOffset(int currentTick)
        {
            return GetCentralBodyOffset((double)currentTick);
        }

        public SystemVec3 GetCentralBodyOffset(double currentTick)
        {
            if (CelestialBodyId < 0) return SystemVec3.zero;
            // R5（2026-09-26）：缓存引用（本方法由 `OrbitAdapter.TryToOrbitView` 每组每步调用）。
            var celestialMgr = CelestialBodyManager.GetCached();
            if (celestialMgr == null) return SystemVec3.zero;
            var centralDef = celestialMgr.GetBodyById(CelestialBodyId);
            if (centralDef == null || centralDef.bodyType == CelestialBodyType.Planet)
                return SystemVec3.zero;
            return new SystemVec3(celestialMgr.GetBodyPositionParent(CelestialBodyId, currentTick));
        }

        /// <summary>
        /// 获取中心天体在**系统系**中的速度（类型同 <see cref="GetCentralBodyOffset(int)" />）。
        /// 如果中心天体是行星（地球自身），返回零向量。
        /// </summary>
        public SystemVec3 GetCentralBodyVelocity(int currentTick)
        {
            if (CelestialBodyId < 0) return SystemVec3.zero;
            // R5（2026-09-26）：缓存引用（同 `GetCentralBodyOffset`）。
            var celestialMgr = CelestialBodyManager.GetCached();
            if (celestialMgr == null) return SystemVec3.zero;
            var centralDef = celestialMgr.GetBodyById(CelestialBodyId);
            if (centralDef == null || centralDef.bodyType == CelestialBodyType.Planet)
                return SystemVec3.zero;
            return new SystemVec3(celestialMgr.GetBodyVelocityParent(CelestialBodyId, currentTick));
        }

        /// <summary>
        /// **本群组在"天体局部系"的位置**（批6 S3-3c）：`CurrentOrbit.position − 中心天体系统系偏移`，
        /// **唯一换算**是 <see cref="FrameConvert.SystemToBody" />（别再在调用点手写这个减法）。
        ///
        /// ⚠ 与 `positionBody` 字段的区别：那个是**步末写一次的滞后副本**（实测滞后 ≤ 一步、月球档 ≈1.8 km，
        /// **与交会门同阶**）；本方法是**现算**，与 `CurrentOrbit` 同刻。新代码一律用本方法
        /// （R5 的处方："构造点只从 `CurrentOrbit` 取真值"）。
        /// </summary>
        public BodyVec3 GetBodyPosition(double tick) =>
            FrameConvert.SystemToBody(CurrentOrbit.positionSystem, GetCentralBodyOffset(tick));

        /// <summary>**本群组在"天体局部系"的速度**。语义与 <see cref="GetBodyPosition" /> 相同。</summary>
        public BodyVec3 GetBodyVelocity(double tick) =>
            FrameConvert.SystemToBodyVelocity(CurrentOrbit.velocitySystem, GetCentralBodyVelocity((int)System.Math.Round(tick)));

        /// <summary>
        /// **本群组的系统系状态 → 天体局部系状态**（同一 tick 取两次 ⇒ 一定同一时刻；
        /// 需要同时要位置与速度时用这个，别分别调两次以免取到不同刻）。
        /// </summary>
        public (BodyVec3 pos, BodyVec3 vel) GetBodyState(double tick)
        {
            int t = (int)System.Math.Round(tick);
            return (FrameConvert.SystemToBody(CurrentOrbit.positionSystem, GetCentralBodyOffset(t)),
                    FrameConvert.SystemToBodyVelocity(CurrentOrbit.velocitySystem, GetCentralBodyVelocity(t)));
        }

        /// <summary>
        /// 进入圆轨模式（自动获取 physicsTick）。
        /// 兼容旧调用，内部会从 TimeScaleManager 获取 physicsTick。
        /// </summary>
        public void EnterCircularOrbit(int realTick, int centralBodyId)
        {
            var tsm = Current.Game?.GetComponent<TimeScaleManager>();
            double physicsTick = tsm != null ? tsm.GetPhysicsTick() : (double)Find.TickManager.TicksGame;
            EnterCircularOrbit(realTick, physicsTick, centralBodyId);
        }

        /// <summary>
        /// 进入圆轨模式。
        /// 自动开启轨道保持（白名单 = {centralBodyId}），仅保留中心天体的引力。
        /// </summary>
        /// <param name="realTick">当前真实游戏 tick（用于天体位置查询）</param>
        /// <param name="physicsTick">当前物理 tick（用于圆轨起始时间，受时间挡位影响）</param>
        /// <param name="centralBodyId">环绕的中心天体 ID</param>
        public void EnterCircularOrbit(int realTick, double physicsTick, int centralBodyId)
        {
            DVector3 pos = CurrentOrbit.positionSystem.v;
            DVector3 vel = CurrentOrbit.velocitySystem.v;

            // 如果中心天体不是行星（如月球），将 pos/vel 转为相对中心天体
            // 采样用 physicsTick：CurrentOrbit 是 physicsTick 时间轴上的状态，
            // 与后续 GetCircularPositionAtTick / N 体积分同基准（realTick 在倍率≠1 时错位）
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (celestialMgr != null)
            {
                var centralDef = celestialMgr.GetBodyById(centralBodyId);
                if (centralDef != null && centralDef.bodyType != CelestialBodyType.Planet)
                {
                    pos -= celestialMgr.GetBodyPositionParent(centralBodyId, physicsTick);
                    vel -= celestialMgr.GetBodyVelocityParent(centralBodyId, physicsTick);
                }
            }

            double r = pos.magnitude;
            if (r < 0.01)
            {
                Log.Warning($"SpaceEntityGroup {GroupId}: Cannot enter circular orbit at near-zero radius.");
                return;
            }

            // 最小轨道半径 ≥ 天体半径：低于表面强行入轨 = 圆轨解析位置在地表以下，必坠毁
            var centralDefForRadius = celestialMgr?.GetBodyById(centralBodyId);
            if (centralDefForRadius != null && centralDefForRadius.radius > 0.0 && r < centralDefForRadius.radius)
            {
                Log.Warning($"SpaceEntityGroup {GroupId}: Cannot enter circular orbit around body {centralBodyId}: orbit radius {r / 1000.0:F0} km < body radius {centralDefForRadius.radius / 1000.0:F0} km.");
                return;
            }

            // ── 新规则（[A6]，2026-10-06）：**解析圆轨与气动互斥** ─────────────────────
            // 解析轨不积分（**忽略阻力**）⇒ 只要点还在大气壳内（ρ > 0）就走不得（结果必错）⇒ 拒绝进入。
            // ⚠ 判据 = `IsInsideAtmosphere`（ρ 严格为零才是解析轨的合法域），**不是**闩锁恢复那条
            //   "ρ/ρ₀ ≤ e^−k"（那条管"气动可忽略到什么程度"；两条语义不同，别合并）。
            // ⚠ 现有调用点全部在 500 km 以上 ⇒ 本守卫在 ρ 数值未定期间是休眠的（零行为变化）。
            if (centralDefForRadius != null
                && centralDefForRadius.Atmosphere.IsInsideAtmosphere(r - centralDefForRadius.radius))
            {
                Log.Warning($"SpaceEntityGroup {GroupId}: Refusing analytic circular orbit around body {centralBodyId} at r={r / 1000.0:F0} km — inside the atmosphere (analytic rail ignores drag). [A6]");
                return;
            }

            DVector3 radialDir = pos.normalized;
            // 不校验切向速度：调用方（空间站/飞船部署等）可能只提供位置、速度为零。
            // 下方会强制用中心天体 GM 重算圆轨速度 sqrt(mu/r)，零速度也能入轨。

            // 用中心天体的 GM 重新计算正确的环轨速度，避免入轨速度用了错误 GM
            double mu = SpaceConstants.PrimaryGravitationalParameter;
            if (celestialMgr != null)
            {
                var centralDef = celestialMgr.GetBodyById(centralBodyId);
                if (centralDef != null && centralDef.gravitationalParameter > 0.0)
                    mu = centralDef.gravitationalParameter;
            }
            double v_tan = System.Math.Sqrt(mu / r);
            double omega = v_tan / r;

            // 轨道平面法线 = pos × vel 的方向；速度为零 / 纯径向时退化为默认平面（up）
            DVector3 normal = DVector3.Cross(pos, vel).normalized;
            if (normal.sqrMagnitude < 0.01)
                normal = DVector3.up; // 退化情况（纯径向速度 / 零速度）

            IsCircularOrbit = true;
            circularCentralBodyId = centralBodyId;
            circularRadius = r;
            circularAngularVelocity = omega;
            circularStartAngle = 0.0;
            circularStartTick = physicsTick;
            circularOrbitNormal = normal;
            circularRadialRef = radialDir;
            InvalidateOrbitCache();

            // 将 CurrentOrbit.velocity 同步为圆轨切向速度
            DVector3 tangentDir = DVector3.Cross(normal, radialDir).normalized;
            if (tangentDir.sqrMagnitude < 0.01)
            {
                // 极罕见：radialDir 与轨道平面法线平行（位置沿法线方向且速度为零）
                DVector3 refVec = System.Math.Abs(radialDir.y) < 0.9 ? DVector3.up : DVector3.forward;
                tangentDir = DVector3.Cross(refVec, radialDir).normalized;
            }
            DVector3 circularVel = tangentDir * v_tan;

            // 批6 S3-3d：`positionBody`/`velocityBody` 现在是**现算属性**（见其文档）⇒ 不再在这里写副本
            Log.Message($"[Orbit] Group {GroupId} EnterCircularOrbit: body={centralBodyId} r={r / 1000.0:F0}km |localPos|={positionBody.magnitude / 1000.0:F0}km |localVel|={velocityBody.magnitude:F1} m/s");

            // 如果中心天体不是行星，加回其轨道速度（转回地球参考系）
            if (celestialMgr != null)
            {
                var centralDefInner = celestialMgr.GetBodyById(centralBodyId);
                if (centralDefInner != null && centralDefInner.bodyType != CelestialBodyType.Planet)
                {
                    circularVel += celestialMgr.GetBodyVelocityParent(centralBodyId, physicsTick);
                }
            }
            CurrentOrbit.velocitySystem = (SystemVec3)circularVel;
            CelestialBodyId = centralBodyId;

            // 自动开启轨道保持：白名单只保留中心天体，抵消其他摄动
            EnterOrbitKeeping(new HashSet<int> { centralBodyId });
        }

        /// <summary>
        /// 退出圆轨模式，根据当前角度计算切向速度和位置，更新 CurrentOrbit。
        /// 使用记录的轨道平面法线和径向参考方向，支持倾斜轨道。
        /// </summary>
        public void ExitCircularOrbit()
        {
            if (!IsCircularOrbit) return;

            var timeScaleManager = Current.Game?.GetComponent<TimeScaleManager>();
            double physicsTick = timeScaleManager != null ? timeScaleManager.GetPhysicsTick() : (double)Find.TickManager.TicksGame;

            DVector3 pos = GetCircularPositionAtTick(physicsTick);
            // 系统系 → 天体局部系：**唯一换算** `FrameConvert`（只要方向 ⇒ 取 `.v` 再归一化）
            DVector3 radialDir = FrameConvert.SystemToBody(pos, GetCentralBodyOffset(physicsTick)).normalized.v;

            // 切向方向：在轨道平面内垂直于径向
            DVector3 tangentDir;
            if (circularOrbitNormal.sqrMagnitude > 0.001)
            {
                tangentDir = DVector3.Cross(circularOrbitNormal, radialDir).normalized;
            }
            else
            {
                // 旧存档兼容：XZ 平面
                tangentDir = new DVector3(-radialDir.z, 0, radialDir.x).normalized;
            }

            DVector3 newVel = tangentDir * (circularAngularVelocity * circularRadius);

            // 批6 S3-3d：`positionBody`/`velocityBody` 现在是**现算属性** ⇒ 不再写副本（原两行删除）

            CurrentOrbit.velocitySystem = (SystemVec3)newVel;

            // 如果中心天体不是行星，速度要加回中心天体的轨道速度
            if (circularCentralBodyId >= 0)
            {
                var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
                if (celestialMgr != null)
                {
                    var centralDef = celestialMgr.GetBodyById(circularCentralBodyId);
                    if (centralDef != null && centralDef.bodyType != CelestialBodyType.Planet)
                    {
                        // 同系相加（中心天体的轨道速度也是系统系）⇒ 显式跨界，不再手写裸名
                        CurrentOrbit.velocitySystem = CurrentOrbit.velocitySystem
                            + new SystemVec3(celestialMgr.GetBodyVelocityParent(circularCentralBodyId, physicsTick));
                    }
                }
            }

            CurrentOrbit.positionSystem = (SystemVec3)pos;

            IsCircularOrbit = false;
            circularCentralBodyId = -1;
            circularRadius = 0.0;
            circularAngularVelocity = 0.0;
            circularStartAngle = 0.0;
            circularStartTick = 0;
            circularOrbitNormal = DVector3.up;
            circularRadialRef = DVector3.right;
            InvalidateOrbitCache();

            // 退出轨道保持
            ExitOrbitKeeping();
        }

        /// <summary>
        /// 获取圆轨模式下当前时刻的 3D 位置（使用轨道平面法线）。
        /// 支持倾斜轨道。旧存档（无法线数据）回退到 XZ 平面。
        /// 注意：currentTick 应为 physicsTick（受时间挡位影响的虚拟tick）。
        /// </summary>
        public DVector3 GetCircularPositionAtTick(int currentTick)
        {
            return GetCircularPositionAtTick((double)currentTick);
        }

        /// <summary>
        /// 获取圆轨模式下当前时刻的 3D 位置（支持子tick，用于战斗模式）。
        /// currentTick 应为 physicsTick（受时间挡位影响的虚拟tick）。
        /// </summary>
        public DVector3 GetCircularPositionAtTick(double currentTick)
        {
            if (!IsCircularOrbit) return DVector3.zero;

            // 同一 physicsTick 下圆轨位置是纯解析函数 → 懒缓存，其余调用直接命中
            if (currentTick == cachedPosTick)
            {
#if DEBUG
                debugPosCacheHits++;
#endif
                return cachedPos;
            }
#if DEBUG
            debugPosCacheMisses++;
#endif

            // circularAngularVelocity 是 rad/秒；physicsTick 是游戏世界秒（1 tick = 1 秒），
            // 与 N 体积分 / 天体公转 / 机动规划使用同一时间基准，无需额外换算。
            double deltaAngle = circularAngularVelocity * (currentTick - circularStartTick);
            double angle = circularStartAngle + deltaAngle;
            double cosA = System.Math.Cos(angle);
            double sinA = System.Math.Sin(angle);

            DVector3 localPos;
            if (circularOrbitNormal.sqrMagnitude > 0.001 && circularRadialRef.sqrMagnitude > 0.001)
            {
                // 使用轨道平面：pos = r * (radialRef * cos(θ) + yDir * sin(θ))
                DVector3 yDir = DVector3.Cross(circularOrbitNormal, circularRadialRef).normalized;
                localPos = circularRadius * (circularRadialRef * cosA + yDir * sinA);
            }
            else
            {
                // 旧存档兼容：XZ 平面
                localPos = new DVector3(System.Math.Cos(angle) * circularRadius, 0, System.Math.Sin(angle) * circularRadius);
            }

            // 如果中心天体不是行星，将局部坐标转回系统系
            // 注意：使用 currentTick（physicsTick），与发动机积分器保持一致
            if (circularCentralBodyId >= 0)
            {
                // 天体局部系 → 系统系：**唯一换算** `FrameConvert.BodyToSystem`
                localPos = FrameConvert.BodyToSystem(new BodyVec3(localPos), GetCentralBodyOffset(currentTick));
            }

            cachedPosTick = currentTick;
            cachedPos = localPos;
            return localPos;
        }

        /// <summary>
        /// 获取圆轨模式下当前时刻的解析速度（世界系，含中心天体公转速度），支持子 tick。
        /// 与 <see cref="GetCircularPositionAtTick"/> 相同采用 per-physicsTick 懒缓存。
        /// 非圆轨模式返回零向量（调用方应先判断 IsCircularOrbit）。
        /// </summary>
        public DVector3 GetCircularVelocityAtTick(double currentTick)
        {
            if (!IsCircularOrbit) return DVector3.zero;

            if (currentTick == cachedVelTick)
            {
#if DEBUG
                debugVelCacheHits++;
#endif
                return cachedVel;
            }
#if DEBUG
            debugVelCacheMisses++;
#endif

            DVector3 pos = GetCircularPositionAtTick(currentTick); // 命中位置缓存，不重复解析
            DVector3 radialDir = FrameConvert.SystemToBody(pos, GetCentralBodyOffset(currentTick)).normalized.v;
            DVector3 tangentDir;
            if (circularOrbitNormal.sqrMagnitude > 0.001)
                tangentDir = DVector3.Cross(circularOrbitNormal, radialDir).normalized;
            else
                tangentDir = new DVector3(-radialDir.z, 0, radialDir.x).normalized;
            DVector3 vel = tangentDir * (circularAngularVelocity * circularRadius);
            // 绕卫星（月球）时：世界速度 = 圆轨切向 + 中心天体公转速度（行星中心体为世界原点，速度为零）
            if (circularCentralBodyId >= 0)
            {
                var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
                if (celestialMgr != null)
                {
                    var centralDef = celestialMgr.GetBodyById(circularCentralBodyId);
                    if (centralDef != null && centralDef.bodyType != CelestialBodyType.Planet)
                        vel += celestialMgr.GetBodyVelocityParent(circularCentralBodyId, currentTick);
                }
            }

            cachedVelTick = currentTick;
            cachedVel = vel;
            return vel;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref LandingTaskId, "landingTaskId", string.Empty);
            Scribe_Values.Look(ref GroupId, "groupId");
            Scribe_Values.Look(ref FactionId, "factionId");
            Scribe_Values.Look(ref BurnCommandVersion, "burnCommandVersion");
            Scribe_Values.Look(ref IsActive, "isActive", true);
            Scribe_Values.Look(ref IsMovable, "isMovable", true);
            Scribe_Values.Look(ref IsCollidable, "isCollidable", true);
            Scribe_Values.Look(ref HasGravityWell, "hasGravityWell", false);
            Scribe_Values.Look(ref IsCrippled, "isCrippled", false);
            // §5 电闭环：电池存量（群级状态；容量/充放上限从部件聚合来，不落这里）
            Scribe_Values.Look(ref StoredEnergyJ, "powerStoredJ", 0f);
            // 匿踪闸门（[V1]）：旧存档无此节点 → 默认 false（公开坐标），与"飞船/站不广播"一致
            Scribe_Values.Look(ref IsStealth, "isStealth", false);
            // 主动探测开关（[V1]）：旧存档无节点 → false（被动）
            Scribe_Values.Look(ref activePing, "activePing", false);
            // [V7] 波束指向三模式的状态（2026-10-01）：模式 / 手选方位 / 跟踪对象**落盘**；
            //   扫掠相位**不落盘**——它是 `physicsTick` 的纯函数（见 `ScanPhaseRad`）。
            Scribe_Values.Look(ref radarAim, "radarAim", RadarAim.Scan);
            DVector3.Scribe("radarManualDir", ref radarManualDir);
            Scribe_Values.Look(ref radarTrackGroupId, "radarTrackGroupId", -1);
            Scribe_Deep.Look(ref CurrentOrbit, "currentOrbit");
            DVector3.Scribe("angularVelocity", ref angularVelocity);
            Scribe_Values.Look(ref torqueFreePhysics, "torqueFreePhysics", false); // 演示/物理档前哨；默认 false
            Scribe_Values.Look(ref maxAngularRate, "maxAngularRate", -1f); // -1 = 默认常量（旧存档兼容）
            Scribe_Values.Look(ref OrbitDirty, "orbitDirty", true);
            Scribe_Collections.Look(ref entityIds, "entityIds", LookMode.Value);
            Scribe_Values.Look(ref CelestialBodyId, "celestialBodyId", -1);
            Scribe_Values.Look(ref exemptPlanetCollision, "exemptPlanetCollision", false);
            // 落地解析档（2026-10-28）：旧档无此节点 ⇒ 默认在飞
            Scribe_Values.Look(ref landed, "landed", false);
            Scribe_Values.Look(ref landedFixedX, "landedFixedX", 0.0);
            Scribe_Values.Look(ref landedFixedY, "landedFixedY", 0.0);
            Scribe_Values.Look(ref landedFixedZ, "landedFixedZ", 0.0);
            // 地表锚点（星座成员载体）：旧存档无此节点 → 默认非锚点
            Scribe_Values.Look(ref IsSurfaceAnchor, "isSurfaceAnchor", false);
            Scribe_Values.Look(ref anchorWorldObjectId, "anchorWorldObjectId", -1);
            // 圆轨模式数据序列化
            Scribe_Values.Look(ref IsCircularOrbit, "isCircularOrbit", false);
            Scribe_Values.Look(ref circularRadius, "circularRadius", 0.0);
            Scribe_Values.Look(ref circularAngularVelocity, "circularAngularVelocity", 0.0);
            Scribe_Values.Look(ref circularStartAngle, "circularStartAngle", 0.0);
            Scribe_Values.Look(ref circularStartTick, "circularStartTick", 0.0);
            Scribe_Values.Look(ref circularCentralBodyId, "circularCentralBodyId", -1);
            DVector3.Scribe("circularOrbitNormal", ref circularOrbitNormal);
            DVector3.Scribe("circularRadialRef", ref circularRadialRef);

            // 机动规划队列序列化
            Scribe_Collections.Look(ref plannedManeuvers, "plannedManeuvers", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && plannedManeuvers == null)
                plannedManeuvers = new List<PlannedManeuver>();

            // 空间站「当前网络」（单选，新机动继承）序列化
            Scribe_Defs.Look(ref currentNetwork, "currentNetwork");

            // 轨道保持序列化
            Scribe_Values.Look(ref HasOrbitKeeping, "hasOrbitKeeping", false);
            Scribe_Values.Look(ref autoStageEnabled, "autoStageEnabled", false);
            Scribe_Values.Look(ref autoStageFuelFraction, "autoStageFuelFraction", 0.02);
            Scribe_Values.Look(ref autoStageMinSeconds, "autoStageMinSeconds", 20.0);
            Scribe_Values.Look(ref enabledNetworkDefName, "enabledNetworkDef", "");
            Scribe_Values.Look(ref enabledNetworkRcsOnly, "enabledNetworkRcsOnly", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                ApplyNetworkSelection(enabledNetworkDefName, enabledNetworkRcsOnly);
            if (Scribe.mode == LoadSaveMode.LoadingVars && HasOrbitKeeping && orbitKeepingWhitelist == null)
                orbitKeepingWhitelist = new HashSet<int>();
            // Scribe 不支持直接序列化 HashSet<int>，转 List<int>
            List<int>? whitelistList = null;
            if (Scribe.mode == LoadSaveMode.Saving)
                whitelistList = orbitKeepingWhitelist != null ? new List<int>(orbitKeepingWhitelist) : new List<int>();
            Scribe_Collections.Look(ref whitelistList, "orbitKeepingWhitelist", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars && whitelistList != null)
            {
                if (orbitKeepingWhitelist == null)
                    orbitKeepingWhitelist = new HashSet<int>(whitelistList);
                else
                {
                    orbitKeepingWhitelist.Clear();
                    orbitKeepingWhitelist.UnionWith(whitelistList);
                }
            }

            // 引力掩码 + 单向闩锁（[A6]，2026-10-06）：默认 null/false（旧档读出来就是今天行为）
            Scribe_Collections.Look(ref ignoredGravityBodyIds, "ignoredGravityBodyIds", LookMode.Value);
            Scribe_Values.Look(ref fieldRestoreLatched, "fieldRestoreLatched", false);

            // 悬浮（[A7]）：定点 + 开关都进档（工作模式该跨读档；`hoverActiveCount` 由 PostLoad 重数）
            Scribe_Values.Look(ref hoverActive, "hoverActive", false);
            Scribe_Values.Look(ref hoverAnalytic, "hoverAnalytic", false);   // [A8] 档位（默认物理档）
            // [A8] 解析搬运单（工作模式该跨读档）
            Scribe_Values.Look(ref transferActive, "transferActive", false);
            Scribe_Values.Look(ref transferFromPos, "transferFromPos");
            Scribe_Values.Look(ref transferFromVel, "transferFromVel");
            Scribe_Values.Look(ref transferToPos, "transferToPos");
            Scribe_Values.Look(ref transferToVel, "transferToVel");
            Scribe_Values.Look(ref transferDvTotal, "transferDvTotal", 0.0);
            Scribe_Values.Look(ref transferStartTick, "transferStartTick", 0.0);
            Scribe_Values.Look(ref transferDuration, "transferDuration", 0.0);
            Scribe_Values.Look(ref hoverLatRad, "hoverLatRad", 0.0);
            Scribe_Values.Look(ref hoverLonRad0, "hoverLonRad0", 0.0);
            Scribe_Values.Look(ref hoverAltitudeMeters, "hoverAltitudeMeters", 0.0);
            Scribe_Values.Look(ref debugInfinitePropellant, "debugInfinitePropellant", false);   // 调试船跨读档
            Scribe_Values.Look(ref hoverResumeAfterTransfer, "hoverResumeAfterTransfer", false);  // 搬运到点恢复悬浮
            Scribe_Values.Look(ref hoverResumeAnalytic, "hoverResumeAnalytic", false);

            // 交会引导序列化
            Scribe_Values.Look(ref HasRendezvousGuidance, "hasRendezvousGuidance", false);
            Scribe_Values.Look(ref rendezvousArmed, "rendezvousArmed", false);
            Scribe_Values.Look(ref rendezvousIsApproach, "rendezvousIsApproach", false);
            Scribe_Values.Look(ref rendezvousTargetId, "rendezvousTargetId", -1);
            DVector3.Scribe("rendezvousTargetOffset", ref rendezvousTargetOffset);
            Scribe_Values.Look(ref rendezvousArrivalTick, "rendezvousArrivalTick", 0.0);
            Scribe_Values.Look(ref rendezvousPrograde, "rendezvousPrograde", true);
            Scribe_Collections.Look(ref rendezvousUseNetworks, "rendezvousUseNetworks", LookMode.Def);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && !HasRendezvousGuidance)
            {
                // 旧存档 / 无效状态兜底：清空残留引导字段
                rendezvousArmed = false;
                rendezvousIsApproach = false;
                rendezvousTargetId = -1;
                rendezvousUseNetworks = null;
            }
        }
    }
}
