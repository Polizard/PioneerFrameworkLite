using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using RimWorld.Planet;
using RimWorld;
using System.Reflection;
using PioneerFramework.Combat;
using MechJebLib.Lambert;

namespace PioneerFramework
{
    public class SpaceGameComp : GameComponent
    {
        // ── 轨道实体/群组 ──
        private Dictionary<int, SpaceEntity> entities = new Dictionary<int, SpaceEntity>();
        private Dictionary<int, SpaceEntityGroup> groups = new Dictionary<int, SpaceEntityGroup>();

        // ── id 分配：持久化单调计数器（A5a）。≤0 = 未初始化 → 首次分配时一次性播种 ──
        private int nextGroupId;
        private int nextEntityId;

        // ── 参考系网格（根行星/恒星ID → 粗网格）──
        private Dictionary<int, SpatialGrid3D<ISpatialEntity>> planetGrids = new Dictionary<int, SpatialGrid3D<ISpatialEntity>>();

        /// <summary>
        /// 粗网格中 group 上次同步的位置（每 2 tick SyncGroupToCoarseGrid 维护）。
        /// 圆轨船位置是解析函数（GetCircularPositionAtTick），粗网格若停在入轨点会永久过期，
        /// 定向能 raycast 的 8 大区块粗筛就会漏检已绕出去的船 → 必须定期同步保鲜。
        /// </summary>
        private Dictionary<int, DVector3> groupLastCoarsePos = new Dictionary<int, DVector3>();

        private Dictionary<int, object> mapComponents = new Dictionary<int, object>();

        // 时间规则：1 tick = 1 秒，每 2 tick 更新一次物理
        internal const int UPDATE_INTERVAL_TICKS = 2;
        private int lastUpdateTick = 0;

        // PERF-5：物理并行下限——编组数低于此值时顺序执行（线程调度 + 并发容器分配 > 顺序开销）
        private const int PARALLEL_PHYSICS_MIN_GROUPS = 32;

        // ── 每物理步 scratch 复用（2026-09-10 性能第 2 条：消除每步固定容器分配）──
        // 仅主线程持有与消费；物理并行区只按索引写 physicsResultsScratch（组数与顺序步内稳定）。
        private readonly List<SpaceEntityGroup> movableGroupsScratch = new List<SpaceEntityGroup>();
        private GroupPhysicsResult[] physicsResultsScratch = new GroupPhysicsResult[0];
        private readonly List<int> groupsToCheckScratch = new List<int>();
        private readonly List<ThrustSlice> steeringSliceScratch = new List<ThrustSlice>();

        // ── 组件引用缓存（2026-09-10 性能第 5 条）：GameComponentTick / 每物理步都要查的组件 ──
        private readonly GameComponentRef<TimeScaleManager> timeScaleRef = new GameComponentRef<TimeScaleManager>();
        private readonly GameComponentRef<CombatManager> combatMgrRef = new GameComponentRef<CombatManager>();
        private readonly GameComponentRef<AutonomousVehicleManager> avmRef = new GameComponentRef<AutonomousVehicleManager>();
        private readonly GameComponentRef<CelestialBodyManager> celestialRef = new GameComponentRef<CelestialBodyManager>();

        /// <summary>
        /// 方向感知推进的默认矢量锥半角余弦（cos 10° ≈ 0.9848）。
        /// burn / 手动平移只使用「轴向世界方向与目标方向夹角 ≤ 锥角」的引擎。
        /// </summary>
        public const float DefaultThrustConeCos = 0.984807753f;
        /// <summary>姿态"对准完成"阈值（≈1.5° 死区）：主推轴收心到正中才点火/消旋（2026-09-06 收心语义）。
        /// 与 DefaultThrustConeCos（10° 引擎允许窗口）区分：窗口内可出力、收心到死区才点火。</summary>
        public const float DefaultThrustAlignCos = 0.999657325f;

        /// <summary>操舵/自动对准详细日志开关（验证用，默认关；Debug 菜单「PFL 测试」页签切换）。</summary>
        public static bool DebugDetailedSteering = false;
        /// <summary>无 Map 主推方向型机动详细日志（D15 新增改动验证）：每物理步输出网选择/对准角/RCS/燃料/燃尽抑制。</summary>
        public static bool DebugDetailedThrust = false;

        // 自动对准 / 手动操舵详细日志状态（主线程）
        private readonly Dictionary<int, bool> alignStateByGroup = new Dictionary<int, bool>();
        private int nextAlignLogTick = 0;
        private int nextSteerLogTick = 0;

        /// <summary>机动预点火窗口（physicsTick 秒）：进入节点前提前这么久开始处理机动
        /// （估燃烧时长 → 等待点火时刻），需覆盖最长燃烧的半程，保证"燃烧居中"生效。</summary>
        private const double ManeuverPreBurnLookaheadTicks = 600;

        /// <summary>活差额律的收工地板（m/s）：`|v_target(UT) − v_live(UT)|` 小于它即认为达标。
        /// 取 1e-3 而**不是** 0 —— 差额是每拍重测的，末步冲量与测量之间隔着一拍框架（重力少积一拍），
        /// 永远差一个 ~1e-3 量级的残数；地板比它大一点点，既不空转也不会把残差留在轨道上
        /// （1e-3/7500 ≈ 1.3e-7，比原来那笔 5.96e-4 的弧误差小三个数量级）。</summary>
        private const double BurnTargetVFloor = 1e-3;

        /// <summary>
        /// ★★ **"差额小到没意义"的地板**（m/s；2026-09-21，用户观察"一抽一抽"+"贴到很近但不结束"）。
        ///
        /// 活差额降到这个量级以下时，**方向已经落在噪声里**——实测收尾段每一发的 R/T/N 都与上一发反号，
        /// 而每一轮掉头要 36 秒（5°/s）、节点只给 13–15 秒 ⇒ 转不完就点火 ⇒ 打出完全歪的冲量
        /// ⇒ 把刚建立的接近又破坏掉 ⇒ 往复不止。**停手**比追噪声好。
        ///
        /// ⚠⚠ **取值：1.0 m/s**（2026-09-22 第二次更正；原 `5.0` 实测把**真活儿**也砍了）。
        ///
        /// ## 为什么从 5.0 降到 1.0（低推力档实测）
        /// ```
        /// [燃烧·收尾] G4 差额只剩 4.29 m/s（< 5.0 地板）⇒ 不再发指令。已交付 0.0 m/s
        /// [阶梯] ⇒ Intercept｜很近（1199 m）… 残余相对速度 12 m/s
        /// ```
        /// 那一发**要消 13.65 m/s**，地板在 5 m/s 处停手 ⇒ 残余 12 m/s 留着 ⇒ 而完成判据要 ≤10
        /// ⇒ **差 2 m/s 永远卡住**。**4.29 m/s 不是噪声**——该档 `a_full=1.13 m/s²`、一步只能给
        /// **2.27 m/s** ⇒ 4.29 是"**两步的活**"。
        ///
        /// ## 判据的正确形式
        /// 地板应当**只顾"方向噪声"**（差额小到"一次冲量就过冲/反号"），**不该吃掉"引擎真能交付的活"**。
        /// 1.0 这个常数下界的作用只剩：别为"已经远小于完成容差"的残差再发指令。
        /// ⇒ **1.0 m/s**：远小于本库任何完成容差的**速度**门（新默认 10 m/s；上游口径 1 m/s 时它正好 1 倍），
        ///   又远大于 `BurnTargetVFloor`（1e-3，那是**数值**地板，别混）。
        /// ⚠ 若将来把完成容差的速度门收到比 1 m/s 还严，这个数要跟着降——**它必须 < 速度容差**。
        /// **回撤**：改回 0 ⇒ 恢复"只要有差额就追"。
        /// </summary>
        private const double BurnDeficitIgnoreFloor = 1.0;

        /// <summary>**节点窗口**：燃烧弧的截止时刻 = `节点 UT + 名义燃时/2 + 本余量`，过了就**停火**。
        ///
        /// 为什么必须有（2026-09-21 游戏内实证的真 bug）：活差额律的两条收手判据**只有**
        /// "差额落到 <see cref="BurnTargetVFloor" /> 以下"与"差额反向"（`BurnStep.Deficit`），
        /// 而两者都是**关于差额本身的**、没有一条**关于节点时刻**。当节点时刻已经过去，
        /// 差额描述的已不是"这个节点还差多少" ⇒ 两条都可能**永远不触发** ⇒ 无界燃烧：
        /// `[Burn] 实际时长=9080s appliedDv=4412.3 m/s`（计划 431.7 ⇒ **10 倍**）、
        /// `剩余=1032.3 一直不掉`、轨道被毁（ApR=−47663 km）⇒ 燃料烧光 ⇒ 交会死 ✗✗。
        ///
        /// 上游为什么不会这样：`ShouldTerminateStock()` 的 `AngleFromNode() >= π/2` —— 它的差额取的是
        /// **节点本体在 UT 处的燃烧向量**，节点作废后那条向量会翻转 ⇒ 收手。我们的差额靶是
        /// **一个固定速度向量**（`burnTargetV`）⇒ 必须显式给出"这一发已经作废"的判据。
        ///
        /// ⚠ 与"点火对准无超时"（见 `IgnitionAlignGateDeg` 处的上游纪律）**不冲突**：
        /// 那条管的是**点火前等对准**（宁可烧晚也不打偏）；本条管的是**点火后的收手**
        /// （节点窗口过完就别再追）。上游两者都有：等对准 + `AngleFromNode` 收手。
        ///
        /// ⚠ 余量取 420 s（远大于物理步 2 s × 3 挡位）：正常燃烧远早于此收手 ⇒ 这条闸对正常燃烧
        /// **不可达**。取值依据（实测）：同轨 30° 快例的调相发名义燃时 ≈124–150 s ⇒ 截止在 UT+470 s 开外；
        /// 而现场那发烧了 **9329 s**（20 倍）—— 既不误杀也不漏抓。
        /// 算式在 <see cref="BurnStep.NodeWindowDeadline" />（唯一一份）。
        /// </summary>
        private const double BurnNodeWindowPostSeconds = 420.0;

        /// <summary>交会阶梯的"一圈最多收多少相位"（度）。LEO 实测 ≈20°/圈（受大气层边缘限）。
        /// ⚠ 换天体要重取（地板随大气层厚度变）—— 这也是它没被塞进 `ManeuverRegime` 常量里的原因。</summary>
        /// <summary>点火前要求的对准精度（度）。
        ///
        /// ★★ **它必须 = 主推锥窗（`DefaultThrustConeCos`，10°），不能更严**（2026-09-21 用户指出）：
        ///   本库的**主推是带矢量角的**——`SpaceMapComp.GetThrustAlong` 逐引擎判
        ///   `dot(该引擎的轴, 指令方向) ≥ coneCos`，凡落在**指令方向**±10° 锥内的轴**都按指令方向出力**；
        ///   而冲量是沿 `burnDirection` 累积的（`pendingImpulse += burnDirection * dv`）⇒
        ///   **只要还有一根轴在锥内，出力方向就是对的**。这正是"快对准了差一点点时，朝正确方向点火"的机制。
        ///   ⇒ 门外不可能出力（推力恒 0，白等）；门内出力方向本来就对 ⇒ **门 = 锥窗**是唯一自洽的取值。
        ///   ⚠ 历史上取 2°（照抄上游 `AlignedToleranceDegrees = 1`）是**过度严格**：上游是单喷管靠姿态对准，
        ///   我们是多轴矢量 ⇒ 用它的数就把"矢量角"这份余量白白丢掉了（实测代价：晚点火 85 s、
        ///   节点落进过去、级联到霍曼那一级卡死）。
        /// ⚠ 仍须**严格宽于**收心死区（`DefaultThrustAlignCos`≈1.5°），否则船收心后仍在门外 ⇒ 死锁
        ///   （静态构造里 `Log.Error` 钉住）。10° ≫ 1.5° ✓。
        /// </summary>
        private static readonly double IgnitionAlignGateDeg =
            Math.Acos(Math.Min(1.0, DefaultThrustConeCos)) * Mathf.Rad2Deg;

        /// <summary>阶梯归属的**退出**阈值（rad）：进用 `ManeuverRegime.ThetaThreshold`（0.2），
        /// 出要超过它 —— 迟滞带防在阈值上翻（实测这批场景 Θ 正好压在 0.187~0.200）。</summary>
        private const double LadderExitTheta = 0.3;

        /// <summary>交会引导：修正烧死区（修正量低于此不点火，m/s；终端消速用）。</summary>
        private const double GuidanceBurnDeadband = 2.0;
        /// <summary>交会引导：进入终端模式（消相对速度）的剩余时间窗口（physicsTick 秒）。
        /// Lambert 在 tof→0 时数值不稳，接近终点改用直接消相对速度；到达点过后立即结束引导。</summary>
        private const double GuidanceTerminalWindow = 60.0;
        /// <summary>交会引导：巡航检查间隔（physicsTick 秒）。未到间隔不重算（省算力、防高频点火）。</summary>
        private const double GuidanceCheckInterval = 30.0;
        /// <summary>交会引导：终端闭环反馈间隔（physicsTick 秒）。消速段更密的重算（needDv = 目标速度−当前速度），
        /// 逐步收敛、达到死区即停，避免一次巨烧过量。</summary>
        private const double GuidanceTerminalCheckInterval = 10.0;
        /// <summary>交会引导：修正烧后冷却（physicsTick 秒）。让轨迹稳定再评估，避免追逐残差。</summary>
        private const double GuidanceBurnCooldown = 60.0;
        /// <summary>交会引导：预测脱靶量门控（米）。传播当前状态到到达时刻，脱靶低于此视为 on-course（零烧）。</summary>
        private const double GuidanceMissThreshold = 2000.0; // 2 km

        /// <summary>接近引导：限速（m/s）。动态到达时刻 tof = dist / v_close，保证修正量有界、轨道扰动小。</summary>
        private const double ApproachMaxClosingSpeed = 50.0;
        /// <summary>接近引导：终端收拢时间常数 τ（s）。消相对速度的同时以 ≤ dist/τ 温柔贴近（仅近距离生效）。</summary>
        private const double ApproachTimeConstant = 300.0;
        /// <summary>接近引导：进入终端收拢的距离阈值（m）。低于此距离不再切回 Lambert 巡航，
        /// 避免 tof=dist/限速 在终端窗口边界反复横跳造成极限环振荡。</summary>
        private const double ApproachTerminalDistance = 5000.0; // 5 km
        /// <summary>接近引导：完成判定——相对距离（m）。</summary>
        private const double ApproachCompletionDistance = 300.0;
        /// <summary>接近引导：完成判定——相对速度（m/s，取 ≥ 引导死区以保证可达成）。</summary>
        private const double ApproachCompletionRelVel = 2.0;
        /// <summary>接近引导：允许启动的最大相对距离（m，太远建议用精确交会）。</summary>
        public const double ApproachMaxStartDistance = 1000000.0; // 1000 km

        // ── 新远场律（RendezvousGuidanceLaw）接线参数（2026-09-17）──────────────────
        //   律本身全在纯核心（Base/Orbital/RendezvousGuidanceLaw.cs，与纯测试台架共用）。
        //   这里只是**调用方的政策**：分腿长度、终端交班、控制周期——与台架 pilot 同口径。
        /// <summary>
        /// 新远场律开关（**默认开**：2026-09-17 起玩家路径只走新律）。
        /// 关掉它 = 回退旧范式（Lambert 规划 + 出发段 + 巡航修正）；旧代码完整保留，
        /// 只从调试页签进入，用于对照与回退——等游戏内验收通过后再谈删除。
        /// 只影响 <b>精确交会</b>（<c>rendezvousIsApproach=false</c>）；近距离接近保持原引擎
        /// （它的目标是"追上并停下"、限速更温柔，同时还是本律的降级出口）。
        /// </summary>
        public static bool UseNewRendezvousLaw = true;
        /// <summary>
        /// 新律：腿长的**额外下限**（s）。默认路径**不用它**——腿长的硬下限是
        /// **目标轨道的一个周期**（见 <see cref="RendezvousGuidanceLaw.PlanLeg"/> 的 periodTgt）：
        /// 交会的自然时间尺度就是一圈，S 形式"段多长就等多久" ⇒ 要一圈交会就得把段设成一圈。
        /// 这个常数只用于旧路径回退时写的 <c>rendezvousArrivalTick</c> 占位与场景显式传参。
        /// </summary>
        private const double NewLawLegSeconds = 60000.0;
        /// <summary>新律：控制周期（s）= 远场一次评估要覆盖的时长（Δv = 加速度 × 此值）。
        /// ⚠ 必须与"烧在周期内完成"自洽：律给出的 |Δv| ≤ aMax×dt ⇒ 满推力烧得完（见 RunNewRendezvousLaw）。
        /// ⚠ 也决定**每次点火的冲量大小** `aMax×dt`：取 10s（≈纯台架的 dt=5s 同量级）。
        ///   取大（如 30s）会让单次冲量超过"该轨道能安全吸收的 Δv"（LEO 500km ≈100 m/s 级），
        ///   于是每轮都被撞行星保护裁剪 ⇒ 律的模型（它以为自己交付了 aRef×dt）与真实交付不符。
        ///   该不符**只会让收敛变慢、不会发散**（环增益被削小 = 更阻尼），但没必要主动制造它。</summary>
        private const double NewLawControlDt = 10.0;
        /// <summary>新律：终端交班距离（占轨道半径比例）。**必须小**——终端用 CW 线性化，
        /// 而 CW 耦合项 1.5n·Δr 在 5% 半径处已不可忽略（台架实测 25% 处交班会发散）。</summary>
        // （原 `NewLawTerminalFraction = 0.05` 已提升为 per-group 字段 `rendezvousTerminalFraction`
        //   —— 2026-09-20 提成可调，默认行为不变；常量留着会变成第二个默认值 ⇒ 删。）
        /// <summary>
        /// <summary>新律：终端 CW 视界（占轨道周期比例）。**偏向短视界**：滚动执行下长视界
        /// "计划便宜、闭环执行贵"（台架实测 T5 终端 430→1455 m/s）。</summary>
        private const double NewLawTerminalHorizonFrac = 0.15;
        /// <summary>新律：兜底交班的**外圈**比例。远场若在外圈内收不到底（不是停滞，是缓慢收敛），
        /// 也给终端接手——否则永远到不了 5% 交班线（台架实测 T5@96000 卡在 8.8%）。</summary>
        private const double NewLawTerminalLateFraction = 0.10;
        /// <summary>新律：兜底交班的**机会倍数**（已给远场这么多倍腿长之后才允许外圈接手）。
        /// ⚠ 判据不能用"停滞"：远场是一路推进只是收不到底，停滞计时器会被推进不断重置。</summary>
        private const double NewLawTerminalLateBudgets = 2.0;
        /// <summary>新律：点火死区 = 本周期**最大可交付 Δv**（aMax×dt）乘以此比例。
        ///
        /// ⚠ 这个数取大了会把律**憋死**，而且症状是"律在算、船一动不动、日志全绿"：
        ///   S 形式的指令在**非饱和区**是 $a_t=	frac{r}{3}kS$（$k=1/(fT^2)$，**与 aMax 无关**）
        ///   —— 腿越长它起步越温柔。2026-09-17 游戏内实测（同轨 LEO、相位差 90°、腿长 25056s）：
        ///     指令 = (1/(0.05·T²))·1.57·5.5e6/3 ≈ 0.0915 m/s² ⇒ 每周期 **0.92 m/s**；
        ///     而当时死区 = 0.02·aMax·dt = **1.57 m/s** ⇒ **每轮都判"可忽略" ⇒ 3 次登记 0 次点火**。
        ///   0.001（= 最大可交付量的 0.1%）⇒ 调试船 0.079 m/s，远低于起步指令；纯台架 T14 两种取值都钉住。
        /// ⚠ 台架原先**不建模死区** ⇒ 这类故障离线测不出来；现已补上（`ActuatorDeadband`）。</summary>
        private const double NewLawDeadbandFraction = 0.001;
        // 形状/相位段门槛、腿长系数、腿长安全系数：**都定义在纯核心** RendezvousGuidanceLaw
        // （ShapeGateMps / ShapeExitMps / ShapeLegFactor / LegSafety）——游戏与台架共用同一份政策，
        // 避免"两份相似的政策"这种最坏的复制：改一处忘一处 = 台架验过的不是游戏在跑的。

        // ── 地图封存/取档异步操作 ──
        public MapRestoreOperation ActiveRestoreOperation { get; private set; }
        public MapArchiveOperation ActiveArchiveOperation { get; private set; }

        // ── 时间流速管理 ──
        /// <summary>用户期望的时间流速（不受机动自动降速影响）。</summary>
        public TimeScaleLevel DesiredTimeScale { get; set; } = TimeScaleLevel.Normal;

        /// <summary>收尾降档的距离门（m）与状态（见 `TryForceTerminalPrecision`）。</summary>
        private const double TerminalPrecisionDist = 2000.0;
        private bool terminalPrecisionForced;
        private bool terminalPrecisionLogged;

        /// <summary>
        /// ★★ **交会收尾自动降档**（2026-09-21，用户："最后一公里时自动切换到 1/60x 试试"）。
        ///
        /// ## 为什么
        /// 1× 档下物理步 = 2 s ⇒ **一步能给的 Δv ≈ 88 m/s**，而收尾修正量只有 15–100 m/s
        /// ⇒ 一次修正 ≈ 一个物理步的粒度（没有余量）；"一拍延迟"2 s 又对应约 1.5 km 位置误差。
        /// 1/60 档把物理步压到 0.033 s ⇒ 每步 1.5 m/s、一拍延迟 ≈ 25 m ⇒ 最后收口才有分辨率。
        ///
        /// ## 边界（三条，都是防"打架"）
        /// · **只降档、不覆写 `DesiredTimeScale`**（玩家偏好是他的）；本函数只认 `terminalPrecisionForced`
        ///   这个"是我把它按下去的"标记，退出时只回自己按的那一下。
        /// · **与 `trackedManeuvers` 那条冲突**：那条在有排队机动时把档位拉回 Normal ⇒ 本判定必须
        ///   **排在它前面**，且命中期间不让它恢复（见调用处的分支顺序）。
        /// · **返回 true 表示"收尾期，别动档位"**；<paramref name="forcedNow"/> 是"本次刚按下"（仅用于日志）。
        ///
        /// ## 回撤
        /// 调用处删掉本分支即恢复原状（档位完全由玩家/`trackedManeuvers` 决定）。
        /// 判据：**若 1/60 档下能收进 `Done`（dist&lt;2 km 且 relVel&lt;1）⇒ 降档是对的**；
        /// 若收不进 ⇒ 分辨率不是瓶颈，删掉本机制、回规划量级那条查。
        /// </summary>
        private bool TryForceTerminalPrecision(TimeScaleManager tsm, out bool forcedNow)
        {
            forcedNow = false;
            if (tsm == null) return false;

            // 找一条正在收尾的交会船（有引导 + 距离进入门内）
            bool inTerminal = false;
            foreach (var g in groups.Values)
            {
                if (!g.HasRendezvousGuidance) continue;
                if (!groups.TryGetValue(g.rendezvousTargetId, out var tgt)) continue;   // 目标不在场（已销毁）⇒ 跳过
                double d = (g.localPosition - tgt.localPosition).magnitude;
                if (d < TerminalPrecisionDist) { inTerminal = true; break; }
            }

            if (!inTerminal)
            {
                // 退出：只回"我自己按下去的那一下"
                if (terminalPrecisionForced)
                {
                    terminalPrecisionForced = false;
                    terminalPrecisionLogged = false;
                    if (tsm.CurrentLevel == TimeScaleLevel.Combat && DesiredTimeScale != TimeScaleLevel.Combat)
                    {
                        if (DesiredTimeScale == TimeScaleLevel.Fast) tsm.QueueCommand(TimeScaleCommand.ToFast());
                        else tsm.QueueCommand(TimeScaleCommand.ToNormal());
                    }
                    Log.Message("[交会·收尾] 退出收尾降档：恢复玩家档位 "
                        + (DesiredTimeScale == TimeScaleLevel.Fast ? "Fast" : "Normal"));
                }
                return false;
            }

            // 进入/维持收尾：1/60 档
            if (!terminalPrecisionForced && tsm.CurrentLevel != TimeScaleLevel.Combat)
            {
                forcedNow = true;
                terminalPrecisionForced = true;
                if (!terminalPrecisionLogged)
                {
                    terminalPrecisionLogged = true;
                    Log.Message(string.Format(
                        "[交会·收尾] 距离 < {0:F0} m ⇒ **自动降档到 1/60×**（物理步 {1:F3} s，"
                        + "每步可给 Δv 约 1.5 m/s；1× 下是 2 s / 88 m/s ⇒ 最后收口没有分辨率）",
                        TerminalPrecisionDist, UPDATE_INTERVAL_TICKS / 60.0));
                }
                tsm.QueueCommand(TimeScaleCommand.ToCombat());
            }
            return true;
        }

        /// <summary>机动追踪表：groupId → 机动状态。半事件驱动，每16 display-tick检查。</summary>
        private struct ManeuverTracker
        {
            public double ExecutePhysicsTick;   // 机动执行的 physicsTick
            public bool IsActive;               // 是否已开始推进
        }
        private Dictionary<int, ManeuverTracker> trackedManeuvers = new();
        /// <summary>上次流速检查时的 physicsTick，每 16tick 检查一次。</summary>
        private int lastManeuverCheckRealTick = -240;

        public SpaceGameComp(Game game) { }

        static SpaceGameComp()
        {
            // ★ **不变量自检**（2026-09-21）：点火门的容差必须**严格宽于**收心死区。
            //   否则船会收心停在死区里、而门够不着 ⇒ 永远点不着火（实测症状：
            //   `预对准·诊断 夹角=1.2°` 与 `主轴还差 2.9°` 并存、计数器一路走负到 `T--6104s`）。
            //   两个常数分别在两处定义（`DefaultThrustAlignCos` 是"姿态收心"、`IgnitionAlignGateDeg`
            //   是"允不允许点火"）⇒ 靠人记住它们的关系不可靠 ⇒ 这里开机断言。
            double deadzoneDeg = Math.Acos(Math.Min(1.0, DefaultThrustAlignCos)) * Mathf.Rad2Deg;
            if (!(IgnitionAlignGateDeg > deadzoneDeg))
                Log.Error(string.Format(
                    "[自检] 点火门({0:F2}°)未严格宽于收心死区({1:F2}°) ⇒ **船会永远点不着火**。" +
                    "调大 `IgnitionAlignGateDeg` 或调小死区（见 AlignMainAxis）。",
                    IgnitionAlignGateDeg, deadzoneDeg));
            // ★ 门的**上限**也必须自检（2026-09-21 用户指出"主推带矢量角"）：
            //   门比主推锥窗（`DefaultThrustConeCos`）更宽 ⇒ 门内仍可能一根轴都不在锥内 ⇒ 出力恒 0 ⇒ 白等。
            //   本库取"门 ≡ 锥窗"（门由锥窗推导）⇒ 这条断言是防将来有人把它改宽。
            double coneWindowDeg = Math.Acos(Math.Min(1.0, DefaultThrustConeCos)) * Mathf.Rad2Deg;
            if (IgnitionAlignGateDeg > coneWindowDeg + 1e-9)
                Log.Error(string.Format(
                    "[自检] 点火门({0:F2}°)比主推锥窗({1:F2}°)更宽 ⇒ 门内可能一根主推轴都不在锥内、" +
                    "出力恒 0 而船在白等。门应 ≤ 锥窗（见 `GetThrustAlong` 的逐引擎判据）。",
                    IgnitionAlignGateDeg, coneWindowDeg));
        }

        #region 地图控件
        public void EnterHiddenMap(int mapId)
        {
            var map = SpaceMapGenerator.FindPocketMap(mapId);
            SpaceMapGenerator.EnterPocketMap(map);
        }
        #endregion

        // ── 网格路由辅助 ──

        /// <summary>获取指定 Group 所属参考系的粗网格。</summary>
        private SpatialGrid3D<ISpatialEntity> GetGridForGroup(int groupId)
        {
            if (groups.TryGetValue(groupId, out var group) && group.CelestialBodyId >= 0)
            {
                // 地表锚点（星座成员载体）不进任何空间索引：粗网格的唯一消费者是
                // 定向能粗筛 / GetGroupsInRange 的候选预过滤，锚点没有参与资格。
                // 在本方法短路 = RegisterGroup / SyncGroupToCoarseGrid / DestroyGroup 三处一并覆盖。
                if (group.IsSurfaceAnchor) return null;
                var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
                if (mgr != null)
                {
                    int frameId = mgr.GetReferenceFrameId(group.CelestialBodyId);
                    if (frameId >= 0)
                    {
                        if (!planetGrids.TryGetValue(frameId, out var pg))
                        {
                            pg = new SpatialGrid3D<ISpatialEntity>();
                            planetGrids[frameId] = pg;
                        }
                        return pg;
                    }
                }
                return null;
            }
            return null;
        }

        /// <summary>获取所有参考系网格。</summary>
        private IEnumerable<SpatialGrid3D<ISpatialEntity>> GetAllGrids()
        {
            foreach (var pg in planetGrids.Values)
                yield return pg;
        }

        /// <summary>
        /// 粗网格位置同步（定向能 raycast 8 大区块粗筛的保鲜前提）：
        /// 用新鲜位置更新 group 在粗网格的区块。大区块 1000km、2 tick 移动 ~5km，
        /// 跨格极罕见，Update 内部新旧格相同 O(1) 返回，成本可忽略。
        /// 统一入口：RegisterGroup（初始）/ UpdateGroupOrbit（轨道变更）/ SyncAllShipsToGrid（每 2 tick）都走这里，
        /// 保证 groupLastCoarsePos 缓存与粗网格实际格坐标一致，不会用错误的旧位置移除导致泄漏。
        /// </summary>
        public void SyncGroupToCoarseGrid(int groupId, DVector3 newPos)
        {
            if (!groups.TryGetValue(groupId, out var group)) return;
            var targetGrid = GetGridForGroup(groupId);
            if (targetGrid == null) return;

            if (groupLastCoarsePos.TryGetValue(groupId, out var last))
            {
                targetGrid.Update(group, last, newPos);
            }
            else
            {
                // 防御分支：正常流程 RegisterGroup 已初始化缓存，这里仅兜底
                targetGrid.Add(group, newPos);
            }
            groupLastCoarsePos[groupId] = newPos;
        }

        /// <summary>获取参考系网格字典（只读），外部用。</summary>
        public IReadOnlyDictionary<int, SpatialGrid3D<ISpatialEntity>> GetPlanetGrids() => planetGrids;

        /// <summary>计算参考系 ID：行星/卫星群组 → 根行星 ID，恒星群组 → 恒星 ID，深空 → -1。</summary>
        private int GetReferenceFrameId(int celestialBodyId)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            return mgr?.GetReferenceFrameId(celestialBodyId) ?? -1;
        }

        /// <summary>公开的参考系 ID 查询，供 CombatManager 等外部使用。</summary>
        public int ResolveReferenceFrameId(int celestialBodyId)
        {
            return GetReferenceFrameId(celestialBodyId);
        }

        /// <summary>将射弹注册到粗网格。</summary>
        public void RegisterProjectileToGrid(ProjectileInstance proj)
        {
            int frameId = GetReferenceFrameId(proj.systemBodyId);
            if (frameId >= 0)
            {
                if (!planetGrids.TryGetValue(frameId, out var grid))
                {
                    grid = new SpatialGrid3D<ISpatialEntity>();
                    planetGrids[frameId] = grid;
                }
                grid.Add(proj, proj.Position);
            }
        }

        /// <summary>从粗网格移除射弹。</summary>
        public void UnregisterProjectileFromGrid(ProjectileInstance proj)
        {
            int frameId = GetReferenceFrameId(proj.systemBodyId);
            if (frameId >= 0 && planetGrids.TryGetValue(frameId, out var grid))
                grid.Remove(proj);
        }

        #region 注册/注销

        public void RegisterEntity(SpaceEntity entity)
        {
            if (entity == null) return;
            if (entities.ContainsKey(entity.EntityId))
            {
                Log.Warning(string.Format("SpaceEntity {0} already registered.", entity.EntityId));
                return;
            }

            if (entity.GroupId == 0)
            {
                Log.Error(string.Format("Entity {0} has GroupId 0. It must belong to a group before registration.", entity.EntityId));
                return;
            }

            if (!groups.TryGetValue(entity.GroupId, out var group))
            {
                Log.Error(string.Format("Group {0} not found for entity {1}. Registration failed.", entity.GroupId, entity.EntityId));
                return;
            }

            if (!group.ContainsEntity(entity.EntityId))
                group.AddEntity(entity.EntityId);

            entities[entity.EntityId] = entity;

            if (group.CelestialBodyId >= 0)
            {
                var celestialMgr = Current.Game.GetComponent<CelestialBodyManager>();
                if (celestialMgr != null)
                {
                    celestialMgr.RegisterEntityToBody(entity.EntityId, group.CelestialBodyId);
                    celestialMgr.RegisterGroupToBody(group.GroupId, group.CelestialBodyId);
                }
            }
        }

        public void UnregisterEntity(SpaceEntity entity)
        {
            if (entity == null) return;
            if (!entities.Remove(entity.EntityId)) return;

            var celestialMgr = Current.Game.GetComponent<CelestialBodyManager>();
            if (celestialMgr != null)
                celestialMgr.UnregisterEntityFromBody(entity.EntityId);
        }

        public void RegisterGroup(SpaceEntityGroup group)
        {
            if (group == null) return;
            if (groups.ContainsKey(group.GroupId))
            {
                Log.Warning(string.Format("SpaceEntityGroup {0} already registered.", group.GroupId));
                return;
            }
            groups[group.GroupId] = group;

            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            int frameId = mgr?.GetReferenceFrameId(group.CelestialBodyId) ?? -1;

            // 粗网格：注册群组（同步初始化位置缓存，保证 SyncGroupToCoarseGrid 走 Update 路径）
            var targetGrid = GetGridForGroup(group.GroupId);
            if (targetGrid != null)
            {
                targetGrid.Add(group, group.Position);
                groupLastCoarsePos[group.GroupId] = group.Position;
            }

            // 打印实际结果而非"registered"：地表锚点按设计不入索引（GetGridForGroup 短路），
            // 若这里仍报 registered，会与星座自检「空间索引隔离」的结论互相矛盾，误导排查。
            Log.Message(string.Format("[Grid] Group {0} registered: bodyId={1} frameId={2} 粗网格={3}",
                group.GroupId, group.CelestialBodyId, frameId,
                targetGrid != null ? "已入" : group.IsSurfaceAnchor ? "锚点不入" : "无参考系"));

            // 细网格：注册群组到战斗网格
            var combatGrid = Current.Game?.GetComponent<CombatGrid>();
            if (combatGrid != null && group.IsCollidable)
                combatGrid.AddShip(group);

            if (group.CelestialBodyId >= 0)
            {
                var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>(); // 与全库其它调用点同款判空
                if (celestialMgr != null)
                    celestialMgr.RegisterGroupToBody(group.GroupId, group.CelestialBodyId);
            }
        }

        public void UnregisterGroup(SpaceEntityGroup group)
        {
            if (group == null) return;
            if (!groups.Remove(group.GroupId)) return;

            var celestialMgr = Current.Game.GetComponent<CelestialBodyManager>();
            if (celestialMgr != null)
                celestialMgr.UnregisterGroupFromBody(group.GroupId);

            foreach (var entityId in group.GetEntityIds())
            {
                if (entities.TryGetValue(entityId, out var entity))
                {
                    entity.GroupId = 0;
                    if (celestialMgr != null)
                        celestialMgr.UnregisterEntityFromBody(entityId);
                }
            }
        }

        /// <summary>
        /// 群组销毁事件（唯一收束点：<see cref="DestroyGroup"/> 内 fire，与 OnGroupDestroyed→CombatManager 同点）。
        /// 卫星与地表锚点的销毁都汇聚到 DestroyGroup，订阅方（星座收尸等）不必到处挂点。
        /// **注意**：fire 时刻群组仍在 groups 字典中（注销是第 4 步）→ 订阅者可读 group 自身身份字段。
        /// </summary>
        public event Action<int> GroupDestroyed;

        /// <summary>
        /// 销毁群组：从粗细网格移除 → 清理战斗缓存 → 注销实体 → 注销群组。
        /// </summary>
        public void DestroyGroup(int groupId)
        {
            if (!groups.TryGetValue(groupId, out var group))
                return;

            // 1. 细网格移除（此时 group 状态仍有效）
            var combatGrid = Current.Game?.GetComponent<CombatGrid>();
            combatGrid?.RemoveShip(group);

            // 2. 战斗缓存清理
            var combatMgr = Current.Game?.GetComponent<CombatManager>();
            combatMgr?.OnGroupDestroyed(groupId);

            // 2.7 收尸事件（星座等组织层订阅）：在粗网格/实体注销之前 fire，
            //     此刻 group 仍在 groups 中，订阅者能读到 IsSurfaceAnchor / anchorWorldObjectId。
            GroupDestroyed?.Invoke(groupId);

            // 2.5 粗网格移除（此时 group 仍在 groups 字典中，GetGridForGroup 可用）
            var targetGrid = GetGridForGroup(groupId);
            if (targetGrid != null)
                targetGrid.Remove(group);
            groupLastCoarsePos.Remove(groupId);

            // 3. 注销实体
            foreach (int entityId in group.GetEntityIds().ToList())
            {
                if (entities.TryGetValue(entityId, out var entity))
                    UnregisterEntity(entity);
            }

            // 4. 注销群组
            UnregisterGroup(group);

            // 5. 网格清理
            foreach (var grid in GetAllGrids())
                grid.ApplyChanges();
        }

        public void RegisterMapComponent(int mapId, object mapComp) => mapComponents[mapId] = mapComp;
        public void UnregisterMapComponent(int mapId) => mapComponents.Remove(mapId);
        public T GetMapComponent<T>(int mapId) where T : class => mapComponents.TryGetValue(mapId, out var comp) ? comp as T : null;

        /// <summary>
        /// 为群组添加机动规划并记录到追踪表（供自动降速使用）。
        /// </summary>
        public void QueueGroupManeuver(int groupId, SpaceEntityGroup.PlannedManeuver maneuver)
        {
            if (groups.TryGetValue(groupId, out var group))
            {
                // §2.4：这条入口是**引导/规划器**用的（`ManeuverPlanner`/`OrbitTransferPlanner`/
                // `RendezvousPlanner`/本类自己的引导都走它）⇒ 显式打上 Guidance。
                // 玩家手动路径走规划器窗口 → `OrbitAdapter.TryQueuePlan`（`PlanNode.owner = Manual`）。
                maneuver.owner = PlanOwner.Guidance;
                group.QueueManeuver(maneuver);
                trackedManeuvers[groupId] = new ManeuverTracker
                {
                    ExecutePhysicsTick = maneuver.executeTick,
                    IsActive = false
                };
            }
        }

        #endregion

        #region 空间站创建与管理

        /// <summary>
        /// id 分配 = **持久化单调计数器**（A5a 根治）。
        ///
        /// 旧实现是"扫 max+1"这种**派生状态**：销毁当前最大 id 的组之后，下一个新组会**复用**那个 id。
        /// 后果（2026-09-14 实测：一轮验收里 id 复用 1–21 次）：
        ///   · 弹头的 `sourceGroupId` 友军免疫判定、AVM 的 `targetGroupId` 追踪会认错组（静默错靶/穿模）；
        ///   · 探针按 groupId 记忆的状态（切片 scratch / 聚合器指纹）会误判"同 id 不同组"。
        /// 计数器随档序列化；计数缺失/异常时**一次性播种**为 max+1（自愈，不依赖旧档迁移）。
        /// </summary>
        public int AllocGroupId()
        {
            if (nextGroupId <= 0) nextGroupId = MaxLiveId(groups.Keys) + 1;
            return nextGroupId++;
        }

        /// <summary>分配下一个 entity id（持久化单调计数器，同上）。</summary>
        public int AllocEntityId()
        {
            if (nextEntityId <= 0) nextEntityId = MaxLiveId(entities.Keys) + 1;
            return nextEntityId++;
        }

        /// <summary>
        /// 注册表一致性探针（A5b，每 600 tick 一次，仅在 PerfProbe.Enabled 时算）：
        /// `entities` 应恰好等于"各存活组 entityIds 的并集"；多出来的是**离群未注销**的泄漏实体。
        /// 之所以要"每 N tick 查"而不是只在验收开头查：泄漏是**战斗中累积**的，开头查永远是 0。
        /// </summary>
        private void ProbeEntityRegistryConsistency()
        {
            if (!PerfProbe.Enabled) return;
            int now = Find.TickManager?.TicksGame ?? 0;
            if (now < nextEntityRegistryProbeTick) return;
            nextEntityRegistryProbeTick = now + 600;

            var inGroups = new HashSet<int>();
            foreach (var g in groups.Values)
                foreach (int eid in g.GetEntityIds())
                    inGroups.Add(eid);

            var leaked = new List<int>();
            foreach (var kv in entities)
                if (!inGroups.Contains(kv.Key)) leaked.Add(kv.Key);

            PerfProbe.EntityRegistry(leaked.Count, leaked.Count > 0
                ? string.Join(",", leaked.GetRange(0, Math.Min(8, leaked.Count)).ConvertAll(i => i.ToString()).ToArray())
                : null);
        }

        private int nextEntityRegistryProbeTick;

        private static int MaxLiveId(Dictionary<int, SpaceEntityGroup>.KeyCollection keys)
        {
            int max = 0;
            foreach (var k in keys) if (k > max) max = k;
            return max;
        }

        private static int MaxLiveId(Dictionary<int, SpaceEntity>.KeyCollection keys)
        {
            int max = 0;
            foreach (var k in keys) if (k > max) max = k;
            return max;
        }

        public SpaceEntity CreateStation(int entityId, int groupId, int factionId,
            DVector3 initialPosition, int mapWidth = 60, int mapHeight = 60, string stationName = null)
        {
            var group = new SpaceEntityGroup(groupId, factionId)
            {
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = 1   // EARTH_BODY_ID，确保空间站放入地球参考系网格
            };
            group.CurrentOrbit = new OrbitState { position = initialPosition, velocity = DVector3.zero, rotation = Quaternion.identity };
            RegisterGroup(group);

            // 用 Part 尺寸创建实体：mapWidth × 50m(厚度) × mapHeight
            var entity = SpaceMapGenerator.CreateStationEntity(entityId, factionId,
                initialPosition, mapWidth, mapHeight);
            entity.GroupId = groupId;
            // entity.Position/Velocity 为相对 group 原点的局部偏移（实体在 group 原点 → zero）
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(Quaternion.identity);

            // 生成地图并用双向链接绑定
            var map = SpaceMapGenerator.GeneratePocketMap(mapWidth, mapHeight, "TestStation_" + entityId);
            SpaceMapGenerator.LinkEntityToMap(entity, map);

            RegisterEntity(entity);
            group.AddEntity(entityId);

            // 语义确认日志：entity 局部偏移应为 zero（实体在 group 原点），AABB 中心 = groupPos
            Log.Message(string.Format(
                "[创建] 空间站 Entity {0} Group {1} groupPos=({2:F0},{3:F0},{4:F0})km entityLocal=({5:F0},{6:F0},{7:F0})km AABB=({8:F0}x{9:F0}x{10:F0})m",
                entity.EntityId, groupId,
                initialPosition.x / 1000.0, initialPosition.y / 1000.0, initialPosition.z / 1000.0,
                entity.Position.x / 1000.0, entity.Position.y / 1000.0, entity.Position.z / 1000.0,
                entity.BoundsSize.x, entity.BoundsSize.y, entity.BoundsSize.z));

            return entity;
        }

        #endregion

        #region 地图封存/取档

        /// <summary>开始封存操作（异步多 tick）。</summary>
        public bool BeginArchiveMap(SpaceEntity entity)
        {
            if (ActiveArchiveOperation != null)
            {
                Messages.Message("已有地图正在封存中，请等待完成。", MessageTypeDefOf.RejectInput, false);
                return false;
            }
            if (ActiveRestoreOperation != null)
            {
                Messages.Message("已有地图正在取档中，请等待完成。", MessageTypeDefOf.RejectInput, false);
                return false;
            }

            var map = SpaceMapGenerator.FindPocketMap(entity.MapId);
            if (map == null) return false;

            var op = MapArchiveManager.BeginArchiveMap(map, entity);
            if (op == null) return false;

            ActiveArchiveOperation = op;
            Messages.Message("开始封存地图...", MessageTypeDefOf.SilentInput, false);
            return true;
        }

        /// <summary>开始取档操作（异步多 tick）。</summary>
        public bool BeginRestoreMap(SpaceEntity entity)
        {
            if (ActiveRestoreOperation != null)
            {
                Messages.Message("已有地图正在取档中，请等待完成。", MessageTypeDefOf.RejectInput, false);
                return false;
            }
            if (ActiveArchiveOperation != null)
            {
                Messages.Message("已有地图正在封存中，请等待完成。", MessageTypeDefOf.RejectInput, false);
                return false;
            }

            var op = MapArchiveManager.BeginRestoreMap(entity);
            if (op == null) return false;

            ActiveRestoreOperation = op;
            Messages.Message("开始取出地图...", MessageTypeDefOf.SilentInput, false);
            return true;
        }

        /// <summary>每 tick 处理封存批次。</summary>
        private void ProcessArchiveOperation()
        {
            if (ActiveArchiveOperation == null) return;

            bool done = MapArchiveManager.ProcessArchiveBatch(
                ActiveArchiveOperation, MapArchiveOperation.BATCH_SIZE);

            if (done)
            {
                FinalizeArchiveOperation();
            }
        }

        /// <summary>每 tick 处理取档批次。</summary>
        private void ProcessRestoreOperation()
        {
            if (ActiveRestoreOperation == null) return;

            bool done = MapArchiveManager.ProcessBuildingBatch(
                ActiveRestoreOperation, MapRestoreOperation.BATCH_SIZE);

            if (done)
            {
                FinalizeRestoreOperation();
            }
        }

        private void FinalizeArchiveOperation()
        {
            var op = ActiveArchiveOperation;
            if (op == null) return;

            MapArchiveManager.FinalizeArchive(op);
            ActiveArchiveOperation = null;
        }

        private void FinalizeRestoreOperation()
        {
            var op = ActiveRestoreOperation;
            if (op == null) return;

            var entity = op.TargetEntity;
            entity.MapId = op.NewMap.uniqueID;
            entity.HasMap = true;
            entity.IsMapArchived = false;

            // 恢复双向映射
            SpaceMapGenerator.LinkEntityToMap(entity, op.NewMap);

            // 删除封存文件
            if (op.ArchiveFilePath != null && File.Exists(op.ArchiveFilePath))
                File.Delete(op.ArchiveFilePath);

            Log.Message(string.Format("[MapArchive] Restore complete: map {0} for entity {1}",
                op.NewMap.uniqueID, entity.EntityId));
            Messages.Message("地图取档完成。", MessageTypeDefOf.SilentInput, false);

            ActiveRestoreOperation = null;
        }

        /// <summary>取消正在进行的取档操作。</summary>
        public void CancelRestoreOperation()
        {
            if (ActiveRestoreOperation == null) return;

            // 销毁已创建的地图
            if (ActiveRestoreOperation.NewMap != null)
                PocketMapUtility.DestroyPocketMap(ActiveRestoreOperation.NewMap);

            ActiveRestoreOperation = null;
            Log.Message("[MapArchive] Restore operation cancelled.");
        }

        #endregion

        #region 查询

        public SpaceEntity GetEntityById(int id) => entities.TryGetValue(id, out var e) ? e : null;
        public SpaceEntityGroup GetGroupById(int id) => groups.TryGetValue(id, out var g) ? g : null;
        public IEnumerable<SpaceEntity> GetAllEntities() => entities.Values;
        public IEnumerable<SpaceEntityGroup> GetAllGroups() => groups.Values;

        /// <summary>
        /// 获取指定范围内所有空间群组（遍历所有参考系网格）。
        /// </summary>
        public IEnumerable<SpaceEntityGroup> GetGroupsInRange(DVector3 centerMeters, double radiusMeters, int factionFilter = -1, Func<SpaceEntityGroup, bool> extraFilter = null)
        {
            foreach (var pg in planetGrids.Values)
            {
                foreach (var entry in pg.GetNearby(centerMeters, radiusMeters))
                {
                    if (entry is SpaceEntityGroup group)
                    {
                        if (factionFilter != -1 && group.FactionId != factionFilter) continue;
                        if (extraFilter != null && !extraFilter(group)) continue;
                        yield return group;
                    }
                }
            }
        }

        #endregion

        #region 集群同步

        /// <summary>
        /// 更新集群轨道，同步所有成员实体位置，并更新粗网格。
        /// </summary>
        public void UpdateGroupOrbit(int groupId, OrbitState newOrbit)
        {
            if (!groups.TryGetValue(groupId, out var group))
                return;

            if (group.CurrentOrbit.position == newOrbit.position &&
                group.CurrentOrbit.velocity == newOrbit.velocity &&
                group.CurrentOrbit.rotation == newOrbit.rotation)
                return;

            group.CurrentOrbit = newOrbit;
            group.OrbitDirty = false;

            // 姿态同步：group 旋转 = 全实体旋转（单 part 化，实体无独立姿态）。
            // 碰撞 AABB（GetWorldAabb）与射弹/光束命中（局部系变换）都消费 entity.Rotation，
            // 必须跟随 group 姿态，否则船转了碰撞盒/命中判定不转。
            foreach (int eid in group.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e != null && e.Rotation != newOrbit.rotation)
                    e.SetRotation(newOrbit.rotation);
            }

            // 更新粗网格中的群组位置（统一走 SyncGroupToCoarseGrid，与每 2 tick 同步共用位置追踪，
            // 缓存 last 即粗网格中 group 当前所在格的位置，不会用错误的旧位置移除导致泄漏）
            SyncGroupToCoarseGrid(groupId, newOrbit.position);

            // 注意：entity.Position 是 Entity 在 Group 内的局部偏移量，
            // Group 移动时实体偏移量不变，不要累加 deltaPos。
        }

        /// <summary>
        /// 直接设置实体的位置/速度/旋转。不涉及网格更新（实体不入网格）。
        /// </summary>
        public void MoveEntity(SpaceEntity entity, DVector3 newPositionMeters, DVector3 newVelocityMeters, Quaternion newRotation)
        {
            if (entity == null) return;
            entity.Position = newPositionMeters;
            entity.Velocity = newVelocityMeters;
            entity.Rotation = newRotation;

            if (entity.GroupId != 0 && groups.TryGetValue(entity.GroupId, out var group))
                group.OrbitDirty = true;
        }

        #endregion

        #region Tick 更新（物理积分）

        public override void GameComponentTick()
        {
            base.GameComponentTick();

            // ── 地图封存/取档异步处理（每次 tick 推进一批）──
            if (ActiveRestoreOperation != null)
            {
                ProcessRestoreOperation();
            }
            if (ActiveArchiveOperation != null)
            {
                ProcessArchiveOperation();
            }

            var timeScaleManager = timeScaleRef.Get();
            int currentTick = Find.TickManager.TicksGame;
            
            if (currentTick - lastUpdateTick >= UPDATE_INTERVAL_TICKS)
            {
                lastUpdateTick = currentTick;
                // 第 25 条墙钟：整步 = 一次 UpdateAllMovements（操舵 + 冲量 + 群组物理 + 耗散 + **交会引导**）。
                // ★ 2026-09-20 改为**常驻测量**（原先只在优化探针开着时测）：成本 = 两次 Stopwatch 读（纳秒级），
                //   换来"随时按需出一份平均/最慢"（`PerfProbe.StepClockReport()`）。
                //   动机：优化探针的开关会**连带打开影子校验**（额外工作 ✗）⇒ 用它测耗时会污染计时
                //   （观测者效应）。常驻测量则与那些校验**完全独立** ✓。
                long stepClock = PerfProbe.StepClockStart();
                UpdateAllMovements(timeScaleManager);
                PerfProbe.StepClockEnd(stepClock);
                PerfProbe.StepClockMaybeAutoLog();      // 开发者模式下自动出一行（不依赖用户按按钮）

                // 真飞验收的**自动检验**（用户 2026-09-19 要求）：布置过的场景在"烧完"后自动出结论。
                // 空闲时第一行就返回（静态 pending 标志）⇒ 零成本；与手动「检查」共用同一套判据。
                LiveBurnAcceptanceRunner.Tick();
                BurnABAcceptanceRunner.Tick();   // 对照验收（一轮做完二分）：空闲时第一行返回
                BurnPhaseAcceptanceRunner.Tick(); // 相位三点实验：空闲时第一行返回
                // 每物理步消费者统一在推进完成后驱动（2026-09-07）：GameComponent tick 顺序按类型
                // 全名字母序（AutonomousVehicleManager → 本组件 → CombatManager），若制导/碰撞各自
                // %2 tick 会有一方读到推进前的旧位置 → 同 tick 位置错位 2s ≈ 5km（导弹制导报 0.7km
                // 命中、碰撞实测 2.6km 的日志实证）。此处推进完毕 = 所有消费者同 tick 同位。
                combatMgrRef.Get()?.OnPhysicsStepCompleted();
                avmRef.Get()?.OnPhysicsStepCompleted();
            }
            
            if (timeScaleManager != null && timeScaleManager.IsCombatMode)
            {
                UpdateCombatInterpolation(currentTick, timeScaleManager);
            }
            
            foreach (var pg in planetGrids.Values)
            {
                pg.ApplyChanges();
                // 粗网格空格回收（TTL 600 tick）：本方法此前**没有任何调用点**（同名 CombatGrid.DrainEmptyCells
                // 才有），于是空格与其 emptyQueue 条目永不回收——历史上被访问过的每个 1000km 格都常驻内存。
                // 2026-09-10 游戏内实证：空闲场景跑一次星座自检（造/销一枚圆轨探针）后，[网格状态] 恒为
                // 「网格数=1 格数=1 实体=0 空队=1」，tick 240/360/480 无回落。
                // 安全性：只回收"当前无实体"的格，查询侧（GetNearby→GetGroupsInRange）语义不变；
                // 被回收格的 LastCellCoord 只可能属于已 Remove 离开网格的实体，不会再走 Update。
                // 成本：每 tick 最多摊销 emptyQueue.Count/20（空队时零迭代）。
                pg.DrainEmptyCells();
            }

            // 空间站地图为 pocket map，Thing 的 CompTick 在隐藏时不可靠：
            // 统一在此驱动储罐差值同步（burn 扣向量后储罐本地逐 tick 追平）。
            foreach (var mc in mapComponents.Values)
            {
                if (mc is SpaceMapComp smc)
                    smc.TickPropellantTankSync();
            }

            // 游戏内架构验证测试（burn 差值同步/合冲量/推进器状态）——空闲时零开销
            BurnArchitectureTest.Tick();

            // 性能优化验收探针汇总（2026-09-10；开关关闭时仅一次静态 bool 读，内部限频）
            PerfProbe.MaybeLogSummary();

            // 实体注册表一致性（A5b；独立限频 600 tick，不受汇总频率影响）
            ProbeEntityRegistryConsistency();

            // 游戏内一键验收状态机（未运行时立即返回，空闲零开销）
            PerfAcceptanceRunner.Tick();

            // 调试场景驱动（原 DebugShipSpawner.CompTick：场景自动下令 + 碰撞距离探针）
            DebugShipSpawner.Tick();
        }

        private struct GroupPhysicsResult
        {
            public int GroupId;
            public bool NeedsUpdate;
            public DVector3 NewPosition;
            public DVector3 NewVelocity;
            /// <summary>本步是否发生姿态变化（角速度积分产生新 rotation）。</summary>
            public bool HasRotationChange;
            /// <summary>姿态积分后的新 rotation（并行区只算不写，主线程统一施加）。</summary>
            public Quaternion NewRotation;
            public bool HasActiveBurn;
            public bool BurnExhausted;
            /// <summary>RCS 修正燃烧耗尽时不终结 burn（主网仍有料、可能仍在等对准），避免误终止（D15 弱 RCS）。</summary>
            public bool SuppressAutoBurnEnd;
            // ── D15 无 Map 主推/RCS 详细日志快照（并行区只写，主线程打日志）──
            public bool NoMapThrust;
            public bool UseMainSel;
            public bool UseRcsSel;
            public bool AligningMain;      // 主推型且本步未对准（主网等待）
            public bool RcsFallbackSel;    // 主推已对准但主网尽 → RCS 兜底
            public double MainDot;         // mainAxisWorld·burnDir（已归一；全向=1）
            public double AppliedDv;       // 本步实际 ΔV（m/s）
            /// <summary>本次 tick 应消耗的工质总量（kg），主线程按各网络切片流率比例分配扣除。</summary>
            public double PropellantConsumed;
            /// <summary>本次 burn 参与推进的方向切片（并行阶段构建的活动网络快照，含锥内推力/流率）。</summary>
            public List<ThrustSlice> ActiveSlices;
            public bool HasOrbitKeeping;
            public double OrbitKeepingDvCost;
            public DVector3 SyncBodyPos;
            public DVector3 SyncBodyVel;
        }

        /// <summary>
        /// 引力场求解的复用容器：**每个群组一份**——物理推进跑在 Parallel.For 里，
        /// 共享容器即数据竞争（同 PerfProbe 的 SliceBufferShared 所警戒的那类）。
        /// all = 该系统全部天体；kept = 其中的非白名单子集（轨道保持用）。
        /// </summary>
        private sealed class GravityScratch
        {
            public readonly List<GravitySource> all = new List<GravitySource>();
            public readonly List<GravitySource> kept = new List<GravitySource>();
        }

        private GravityScratch[] gravityScratchByIndex = new GravityScratch[0];

        /// <summary>主线程路径（战斗插值缓存重建）专用容器：不在并行区内，独占即可。</summary>
        private GravityScratch gravityScratchMain = new GravityScratch();

        private GravityScratch GetGravityScratch(int index)
        {
            var scratch = gravityScratchByIndex[index];
            if (scratch == null)
            {
                scratch = new GravityScratch();
                gravityScratchByIndex[index] = scratch;
            }
            return scratch;
        }

        private void EnsureGravityScratchCapacity(int count)
        {
            if (gravityScratchByIndex.Length >= count) return;

            // 与 physicsResultsScratch 同规则：翻倍留余量，避免组数增长时每步重分配
            int cap = Math.Max(count, gravityScratchByIndex.Length * 2);
            var grown = new GravityScratch[cap];
            Array.Copy(gravityScratchByIndex, grown, gravityScratchByIndex.Length);
            gravityScratchByIndex = grown;
        }

        /// <summary>
        /// 轨道保持的抵消量（A0-7）：要减掉的加速度 = **场(当前位置) − 计划轨道(当前位置)**，
        /// 即"非白名单天体相对计划轨道中心的那部分贡献"。
        ///   · 抵消它 ⇒ 实际轨迹 = 计划轨道 + 白名单天体的扰动（与"白名单=不抵消"的旧语义一致）；
        ///   · 其模长 = 维持计划所需 dV 速率（旧实现算的正是这个量）。
        /// 根域（计划中心 = 被钉住的根）退化为"非白名单天体的全量项"——与旧数字逐位一致；
        /// 子天体域（如月球）则是**潮汐项**（旧实现此处为零，属本次修正）。
        /// 注意：`EnterOrbitKeeping(null)` 会落成**空集**（= 抵消全部扰动、保住计划），
        /// 与旧实现"空集 ⇒ 零引力积分"不同；现有调用点均传 `{自身中心天体}`，不受影响。
        /// </summary>
        private void ComputeOrbitKeepingCorrection(SpaceEntityGroup group, CelestialBodyManager mgr,
            GravityScratch scratch, out DVector3 corrAcc, out DVector3 corrJerk)
        {
            corrAcc = DVector3.zero;
            corrJerk = DVector3.zero;

            int centerBodyId = group.CelestialBodyId;
            var whitelist = group.orbitKeepingWhitelist;

            // ① 非白名单天体（除计划中心本身）在当前位的全量项
            scratch.kept.Clear();
            for (int i = 0; i < scratch.all.Count; i++)
            {
                var s = scratch.all[i];
                if (s.bodyId == centerBodyId) continue;
                if (whitelist != null && whitelist.Contains(s.bodyId)) continue;
                scratch.kept.Add(s);
            }

            if (scratch.kept.Count > 0)
            {
                NBodyIntegrator.FieldAccelJerk(group.CurrentOrbit.position, group.CurrentOrbit.velocity,
                    scratch.kept, out corrAcc, out corrJerk);
            }

            // ② 计划中心的受迫（自由落体）加速度里、由非白名单父天体贡献的那部分：
            //    逐级取「父对子的拉力，评估在子处」——链到系统外的父天体即止（根域为空）。
            int childId = centerBodyId;
            for (int guard = 0; guard < 8; guard++)
            {
                var childBody = mgr.GetBodyById(childId);
                if (childBody == null) break;
                int parentId = childBody.ParentBodyId;
                if (parentId < 0) break;

                if (!NBodyIntegrator.TryFindSource(scratch.all, parentId, out var parentSrc)) break;

                if (whitelist == null || !whitelist.Contains(parentId))
                {
                    if (NBodyIntegrator.TryFindSource(scratch.all, childId, out var childSrc))
                    {
                        NBodyIntegrator.ComputeBodyAccelJerk(
                            childSrc.position, childSrc.velocity,
                            parentSrc.position, parentSrc.velocity, parentSrc.mu,
                            out var a, out var j);
                        corrAcc -= a;
                        corrJerk -= j;
                    }
                }

                childId = parentId;
            }
        }

        /// <summary>
        /// 群组所受的引力加速度/急动度（系统系内，含轨道保持抵消）。
        /// **物理推进与战斗渲染插值共用本方法**——同一份算式，避免两处各写一遍导致参考系/口径漂移。
        /// keepAcc/keepJerk = 本步被轨道保持抵消掉的那部分（其模长即 dV 速率）。
        /// </summary>
        private void ComputeGroupFieldAccelJerk(SpaceEntityGroup group, CelestialBodyManager mgr, double tick,
            GravityScratch scratch, out DVector3 acc, out DVector3 jerk,
            out DVector3 keepAcc, out DVector3 keepJerk, bool timeSegment = false)
        {
            acc = DVector3.zero;
            jerk = DVector3.zero;
            keepAcc = DVector3.zero;
            keepJerk = DVector3.zero;

            // 第 25 条墙钟：只有**物理步**里的调用点传 timeSegment=true。战斗插值重建
            // （RebuildCombatCache）走同一方法但不在物理步节奏上，计入会污染"µs/步"口径。
            // ★ 2026-09-20：引力场段计时改为**常驻**（原随优化探针开关）—— 理由同"步钟"：
            //   优化探针会连带打开影子校验（观测者效应 ✗），而"引导之外那 6.6 ms 尖峰是不是引力场"
            //   只能靠这条读数回答。成本 = 每组每子步两次 Stopwatch 读（纳秒级；组数是个位数）。
            bool timing = timeSegment;
            long clock = timing ? PerfProbe.GravityClockStart() : 0L;

            mgr.CollectSystemSources(group.CelestialBodyId, tick, scratch.all);
            if (scratch.all.Count == 0)
            {
                if (timing) PerfProbe.GravityClockEnd(clock);
                return;
            }

            NBodyIntegrator.FieldAccelJerk(group.CurrentOrbit.position, group.CurrentOrbit.velocity,
                scratch.all, out acc, out jerk);

            if (timing) PerfProbe.GravityClockEnd(clock);

            if (PerfProbe.Enabled) ProbeGravityField(group, mgr, scratch, acc);

            if (!group.HasOrbitKeeping) return;

            ComputeOrbitKeepingCorrection(group, mgr, scratch, out keepAcc, out keepJerk);
            acc -= keepAcc;
            jerk -= keepJerk;
        }

        /// <summary>
        /// 引力场探针（A0，§8.4 第 1/2 项；限频，只在 PerfProbe.Enabled 时跑）：
        ///  · **根域影子**：场(全部后代) vs 场(根 + 直接子天体)。当前数据无孙天体，两者必须逐位一致
        ///    → mismatch 恒为 0 是"层级枚举与位置累加没错"的守卫；将来加了孙天体则应显式分化（勿盲改）。
        ///  · **子天体域差分**：报告「差分(潮汐)模长 / 局部引力模长」。这是月球系修正是否生效的直接读数——
        ///    ≈0 = 还是旧行为（只算中心天体）、0.1%–2% = 正确潮汐、≈7% = 走了"全量求和"的错路。
        /// </summary>
        private void ProbeGravityField(SpaceEntityGroup group, CelestialBodyManager mgr, GravityScratch scratch, DVector3 acc)
        {
            bool isSubBodyDomain = mgr.GetReferenceFrameId(group.CelestialBodyId) != group.CelestialBodyId;

            // 根域：热路径（地球系群组多）→ 限频；子天体域：群组稀少（月球系）→ 逐次采样，
            // 保证验收阶段一定拿得到证据，且 min/max 区间更密。
            if (!isSubBodyDomain && !PerfProbe.ShouldProbeGravity()) return;

            if (!isSubBodyDomain)
            {
                // 根域：与「根 + 直接子天体」子集比较
                scratch.kept.Clear();
                for (int i = 0; i < scratch.all.Count; i++)
                {
                    var s = scratch.all[i];
                    bool directChild = s.bodyId == group.CelestialBodyId
                        || mgr.GetBodyById(s.bodyId)?.ParentBodyId == group.CelestialBodyId;
                    if (directChild) scratch.kept.Add(s);
                }

                NBodyIntegrator.FieldAccelJerk(group.CurrentOrbit.position, group.CurrentOrbit.velocity,
                    scratch.kept, out var subsetAcc, out _);

                double diff = (acc - subsetAcc).magnitude;
                PerfProbe.GravityRootDomain(diff < 1e-9,
                    string.Format("G{0} 全域与根+直接子天体不一致 Δ={1:E3} m/s²（天体枚举/位置累加错？）",
                        group.GroupId, diff));
                return;
            }

            // 子天体域：差分 = 场 − 计划（唯一口径见 CelestialBodyManager.ComputeFieldMinusPlan）。
            // 注意不能写成"场 − 中心天体单独在场"——那是外面天体的**全量**项（实测 6.86%），
            // 不是潮汐（0.3%–0.6%）。2026-09-14 首轮验收正是这样误报的。
            mgr.ComputeFieldMinusPlan(group.CurrentOrbit.position, group.CurrentOrbit.velocity,
                group.CelestialBodyId, scratch.all, out var diffAcc, out _);

            scratch.kept.Clear();
            for (int i = 0; i < scratch.all.Count; i++)
            {
                if (scratch.all[i].bodyId == group.CelestialBodyId) scratch.kept.Add(scratch.all[i]);
            }
            NBodyIntegrator.FieldAccelJerk(group.CurrentOrbit.position, group.CurrentOrbit.velocity,
                scratch.kept, out var localAcc, out _);

            PerfProbe.GravitySubBodyDomain(diffAcc.magnitude, localAcc.magnitude);
        }

        private GroupPhysicsResult ComputeGroupPhysics(SpaceEntityGroup group, double dtSeconds, double physicsTick, CelestialBodyManager celestialMgr, GravityScratch gravityScratch, int subSteps = 1)
        {
            var result = new GroupPhysicsResult { GroupId = group.GroupId };

            if (group.HasActiveBurn)
            {
                // ── 方向与剩余量：**MJ 节点执行器的活差额律**（"MJ 优先"第 5 件，2026-09-20）──
                // 逐拍：把**活轨道**推到节点 `UT`，与**目标速度**（`burnTargetV`）做差 ⇒ 差额既给方向又给剩余量。
                // 史（三层，每层都被实测钉过）：
                //   ① 整段惯性冻结 ⇒ e=8.6e-3（台架）；② 按 2° 节拍刷新（已删）⇒ e=3.8e-3，且**不变量 0.9897
                //   与游戏实测逐位吻合** ⇒ 量化就是那 1% 的来源；③ 每步把"同一个解"在当前 RTN 里重表达 ⇒ e=7.8e-4；
                //   ④ **本轮 = 不再重表达，而是每步重测差额**（解本身随活状态更新）—— 这是 MJ 的做法，
                //   差的那一项正是"重力在燃烧窗口内的贡献"（= 弧补偿一直在追、追不干净的那笔）。
                // **只换方向，不动 remainingBurnDV 记账**。
                if (physicsTick >= BurnDiagNextTick)
                {
                    BurnDiagNextTick = physicsTick + 10.0;
                    // ★★ 全候选一次测完（环境已证纯二体 ⇒ 可用"引力只推进"把**真正施加的冲量**从状态里扣出来）：
                    //   扣出来的 Δv 一步就判掉四件事：**框架**（方向对不对）、**幅值**、**方向**、**时机**。
                    bool haveOv = OrbitAdapter.TryToOrbitView(group, celestialMgr, physicsTick, out OrbitView ovNow);
                    double elapsedBurn = group.burnActualStartTick >= 0.0 ? physicsTick - group.burnActualStartTick : 0.0;

                    string predTxt = "预测=?";
                    string dirTxt = "模型方向=?";
                    if (group.burnArcSeconds > 0.0 && haveOv)
                    {
                        // ⚠ **基准轨道必须是点火前的**：`BurnArc` 自己会把基准推进到点火时刻，
                        //   所以喂"当前（已烧过的）轨道"= 让模型**重烧一遍已经烧掉的那部分** ⇒
                        //   预测随已烧累计 Δv 漂移（2026-09-19 台架：t=100s 差 694 m/s ≈ 已烧 Δv 本身；
                        //   游戏日志同款：实测 5347.3 vs 预测 4652.9）。**这是月牙差 20 倍的真因，不是执行器。**
                        OrbitView arcBase = group.burnHasPreBurnOrbit ? group.burnPreBurnOrbit : ovNow;
                        // ⚠ **同口径**：诊断在本步累加**之前**采样 ⇒ 模型也只积到"上一步末"（elapsed − 一步），
                        //   否则两边的 Δv 恒差一步（实测 14.5 m/s ✓ 量等于 2 s × 7.4671）。这条口径修好之前，
                        //   "预测 vs 实测"这个仪器会系统性骗人 —— 我为此白跑了两轮 ✗。
                        if (BurnArc.TryPredictPartial(arcBase, group.burnArcNodeUt, group.burnArcDv, group.burnArcSeconds,
                                elapsedBurn - UPDATE_INTERVAL_TICKS,
                                group.burnPhaseShift + BurnArc.ExecutionDelaySeconds,
                                out BurnArc.Result pre, out string preWhy))
                        {
                            predTxt = string.Format("预测 |r|={0:F0}m |v|={1:F3}m/s", pre.achieved.radius,
                                pre.achieved.RightHandedOrbitalVelocityAtUT(pre.achieved.epoch).magnitude);
                            if (pre.finalDir.sqrMagnitude > 0.0)
                                dirTxt = string.Format("模型方向 vs 执行方向={0:F2}°",
                                    Math.Acos(Math.Max(-1.0, Math.Min(1.0, MechJebLib.Primitives.V3.Dot(pre.finalDir,
                                        OrbitView.GameWorldToAdapter(OrbitView.ToV3(group.burnDirection)))))) * Mathf.Rad2Deg);
                        }
                        else predTxt = "预测不可用(" + preWhy + ")";
                        // ⚠ **本轮起这是"冻结模型"的预测**（活差额律下它与执行器不再是同一件事）⇒ 读数里明说，
                        //   否则下一次读日志的人会拿它当"执行器的预期"用（正是本轮之前那套仪器的口径）。
                        if (group.burnHasTargetV && !group.abFreezeDirection) predTxt = "（冻结模型）" + predTxt;
                    }

                    // ② 真正施加的冲量 = 实测状态 − "只用引力推进"的预测（dt = 距上次诊断）
                    string impulseTxt = "实交冲量=?";
                    if (haveOv && group.burnDiagTick > 0.0)
                    {
                        double dt = physicsTick - group.burnDiagTick;
                        if (dt > 0.0)
                        {
                            // "只用引力推进"的预测：走**已验证精确**的那条路（适配层 Shepperd）。
                            // 两个状态都换到适配层系里做差 ⇒ 框架一致（游戏系与适配层只差一个旋转）。
                            // ⚠ 全在**适配层系**里算：存的是适配层状态、推的是适配层系、现在也换成适配层系
                            //   （上一版把游戏系向量直接喂给 Shepperd ⇒ 少换一次系 ⇒ 两个读数全废 ✗ 第 9 次仪器事故）
                            (MechJebLib.Primitives.V3 rP, MechJebLib.Primitives.V3 vP) = MechJebLib.TwoBody.Shepperd.Solve(
                                ovNow.mu, dt, group.burnDiagR, group.burnDiagV);
                            (MechJebLib.Primitives.V3 rN, MechJebLib.Primitives.V3 vN) = ovNow.RightHandedStateVectorsAtUT(physicsTick);
                            DVector3 dvApplied = OrbitAdapter.ToD(vN - vP);
                            _ = rP; _ = rN;
                            double dvCmd = Math.Max(0.0, group.burnDiagRemaining - group.remainingBurnDV);
                            // ⚠ **必须把指令也换到适配层系**再比：`dvApplied` 已是适配层系，
                            //   而 `group.burnDirection` 是游戏系 —— 两者差一次 90° 旋转 ⇒ 不换系就恒读 ~88° ✗
                            //   （同一行里那个"模型方向 vs 执行方向"换过系 ⇒ 读 2° ✓，这就是铁证）
                            DVector3 cmdInAdapter = OrbitAdapter.ToD(OrbitView.GameWorldToAdapter(OrbitView.ToV3(group.burnDirection)));
                            double ang = DVector3.Dot(dvApplied.normalized, cmdInAdapter.normalized);
                            // ★ **冲量的 R/T/N 分解**（2026-09-19 通道扫描的结论）：径向一阶不改 ε/|h|
                            //   （R±10 m/s 都测不出 ✓），**切向灵敏**（T+5 → Δε −0.73%、Δa +14.8 km）。
                            //   游戏残差 = T ≈ +2.8 m/s（≡ 方向朝顺行侧转 ~0.7°）⇒ 必须**逐窗**看 T 的实测 vs 指令，
                            //   才能定位它是"每窗都少一点"还是"某几窗少"。
                            (DVector3 rImp, DVector3 vImp) = OrbitAdapter.StateAtUT(ovNow, physicsTick);
                            DVector3 rhatI = rImp.normalized;
                            DVector3 hhatI = DVector3.Cross(rImp, vImp).normalized;
                            DVector3 thatI = DVector3.Cross(hhatI, rhatI);
                            // ⚠ **必须把冲量也换到游戏系**再点乘：`dvApplied` 在**适配层系**，
                            //   而 `StateAtUT` 给的是**游戏系**（它内部做了 AdapterToGameWorld）。
                            //   混系的后果 = 恒读 ~87°（正是"实测 R/T/N 几乎全在法向上"那个假象 ——
                            //   我据此一度以为冲量打到面外去了，而 i/LAN 恰恰是**精确 0** ⇒ 自相矛盾）。
                            //   第 10 次仪器事故：**同一行里两个向量必须来自同一个系**。
                            DVector3 dvAppliedG = OrbitAdapter.ToD(OrbitView.AdapterToGameWorld(OrbitView.ToV3(dvApplied)));
                            double impR = DVector3.Dot(dvAppliedG, rhatI);
                            double impT = DVector3.Dot(dvAppliedG, thatI);
                            double impN = DVector3.Dot(dvAppliedG, hhatI);
                            DVector3 cmdWorldI = group.burnDirection * dvCmd;
                            double cmdR = DVector3.Dot(cmdWorldI, rhatI);
                            double cmdT = DVector3.Dot(cmdWorldI, thatI);
                            double cmdN = DVector3.Dot(cmdWorldI, hhatI);
                            impulseTxt = string.Format("实交 |Δv|={0:F3}（指令 {1:F3}，比值 {2:F3}）｜施加方向 vs 指令={3:F1}°"
                                + "｜R/T/N 实测 {4:F2}/{5:F2}/{6:F2} vs 指令 {7:F2}/{8:F2}/{9:F2}（ΔT={10:F3}）",
                                dvApplied.magnitude, dvCmd, dvCmd > 1e-9 ? dvApplied.magnitude / dvCmd : double.NaN,
                                Math.Acos(Math.Max(-1.0, Math.Min(1.0, ang))) * Mathf.Rad2Deg,
                                impR, impT, impN, cmdR, cmdT, cmdN, impT - cmdT);
                        }
                    }

                    // ★ 最后一块拼图：**副本 vs 正本**。探针读 `localVelocity`（每步从 CurrentOrbit 同步的副本），
                    //   而 `e` 来自适配层（读 CurrentOrbit 正本）⇒ 若两者不等，"幅值/方向"测的就是副本 ✗
                    //   （与今天开头"改了 localVelocity 却没人读"是同一个坑的两面）。
                    double vLocal = group.localVelocity.magnitude;
                    double vCanon = haveOv ? ovNow.RightHandedOrbitalVelocityAtUT(physicsTick).magnitude : double.NaN;
                    double rLocal = group.localPosition.magnitude;
                    double rCanon = haveOv ? ovNow.RightHandedBCIPositionAtUT(physicsTick).magnitude : double.NaN;

                    // ★ 加上"当前质量"：修复（燃耗逐步入账）后它该随工质**下降**；
                    //   若它整段燃烧期不动 ⇒ 燃耗没进到质量里 ⇒ `a` 偏大 ⇒ 白送 Δv（2026-09-19 的结论）。
                    double dbgMNow = GetGroupMass(group, GetGroupPropulsionSource(group));
                    // ★ 本轮起**哪条律在跑**必须自证（否则读日志的人分不清"活差额生效了"与"退路律在跑"）——
                    //   这正是游戏内验收要看的第一件事。
                    string lawTxt = group.abFreezeDirection ? "冻结(对照)"
                        : (group.burnHasTargetV ? "活差额(MJ靶=UT速度)" : "RTN退路");
                    Log.Message(string.Format(
                        "[燃烧·诊断] G{0} 已烧={1:F0}s tick={2:F0} 剩余={3:F1}｜律={4}｜质量={5:F1}kg（点火时 {6:F1}）｜{7}",
                        group.GroupId, elapsedBurn, physicsTick, group.remainingBurnDV, lawTxt,
                        dbgMNow, group.burnStartMass, predTxt));
                    Log.Message(string.Format(
                        "[燃烧·诊断] G{0} 副本 vs 正本：|v| {1:F6} vs {2:F6}（差 {3:E3}）｜|r| {4:F3} vs {5:F3}（差 {6:E3}）",
                        group.GroupId, vLocal, vCanon, System.Math.Abs(vLocal - vCanon),
                        rLocal, rCanon, System.Math.Abs(rLocal - rCanon)));
                    // ★★ **绝对探针**（2026-09-19）：执行器施加的**世界系**方向 vs **计划的世界系向量**。
                    //   两者都是惯性系 ⇒ **不受"执行器 vs 自己的输入"那种共模抵消** ⇒ 这才是唯一能看见
                    //   "定常面内偏置（≈0.23~0.5°）"的读数（终端真值已从状态侧证实它存在）。
                    string absTxt = "绝对=?";
                    if (group.burnArcDv.sqrMagnitude > 0.0)
                    {
                        // ⚠ `pm` 不在此作用域 ⇒ 用群里存的 **计划 Δv（适配层系）**，只换系一次（与本文件既有约定一致）
                        DVector3 planWorld = OrbitAdapter.ToD(OrbitView.AdapterToGameWorld(group.burnArcDv));
                        double absAng = Math.Acos(Math.Max(-1.0, Math.Min(1.0,
                            DVector3.Dot(group.burnDirection.normalized, planWorld.normalized)))) * Mathf.Rad2Deg;
                        absTxt = string.Format("★绝对：执行方向 vs 计划world向量={0:F3}°", absAng);
                    }
                    Log.Message(string.Format("[燃烧·诊断] G{0} {1}｜{2}｜{3}｜主推轴 vs 指令={4:F1}°",
                        group.GroupId, impulseTxt, dirTxt, absTxt,
                        group.GroupId, impulseTxt, dirTxt, MainAxisBurnAngleDeg(group, group.burnDirection)));

                    if (haveOv) (group.burnDiagR, group.burnDiagV) = ovNow.RightHandedStateVectorsAtUT(physicsTick);
                    group.burnDiagTick = physicsTick;
                    group.burnDiagRemaining = group.remainingBurnDV;
                }

                // ★ **每步**重取方向（2026-09-19 台架定量：按 2° 节拍刷新的量化 = e 3.76e-3 vs 每步
                //   7.79e-4，不变量 0.9897 vs 0.9975 —— 游戏实测 0.9897 与之逐位吻合 ⇒ 量化就是残差源）。
                //   这里只是把**同一个解**在当前 RTN 里重新表达，不是重解 ⇒ 没有理由省。
                {
                    // ★★ 2026-09-19 真凶：这里原来用 `localPosition/localVelocity`（**副本**），
                    //   而副本的参考系与"正本 `CurrentOrbit`"**差一个帧速**（实测 14–16 m/s，缓慢变化 ✗）——
                    //   适配层的 `GetCentralBodyOffset/Velocity` 对行星返回 0 ✓，而 `SyncBodyVel` 不是 0 ✗。
                    //   冲量写进的是**正本**（`CurrentOrbit.velocity` ✓）⇒ 用副本的系去解正本系的 Δv = 系统性方向偏差 ✓
                    //   （这也解释了上一次"改点火处分解"为何无效：**主导全程方向的刷新**用的还是副本 ✗）。
                    // ★★ 2026-09-20：**用冲量**落位**那一拍的框架**（= `physicsTick + dtSeconds`），
                    //   不是本拍的。理由（本轮终端真值 + 逐窗 R/T/N 定量〕：冲量在本拍累积、
                    //   **下一物理步首才并入速度**（见 `pendingImpulse` 处注释），而这里原来用的是
                    //   **本拍**的 RTN ⇒ 冲量永远"用上一拍的方向"打在下一拍的位置上 ⇒ **整整一拍的框架滞后**。
                    //   代价：台架同仪器对照 —— 理想执行器滚转 −0.174°，**加一拍滞后 = −0.2647°**，
                    //   游戏实测 **−0.266°**（差 0.001°）；逐窗读数也对上：切向每窗少 0.10~0.115 m/s
                    //   ⇔ 方向偏 0.095°，而一拍的框架转角 `ω·dt = 0.0998°` ✓。
                    //   ⚠ **旧结论"滞后/时机零效应"要从一台空开关撤回**：台架那个 `applyLagOneStep`
                    //     当时只写在签名里、循环里没人用 ⇒ 它测不出任何东西 ✗（第 11 次仪器事故）。
                    //   ⚠ 台架默认口径**本来就是**"落位那一拍的框架" ⇒ 本修完，模型 ≡ 执行器继续成立。
                    DVector3 arcPos = group.localPosition, arcVel = group.localVelocity;
                    bool haveArc = OrbitAdapter.TryToOrbitView(group, celestialMgr, physicsTick, out OrbitView ovArc);
                    if (haveArc)
                        (arcPos, arcVel) = OrbitAdapter.StateAtUT(ovArc, physicsTick + dtSeconds);   // 正本系，落位拍
                    // ★★ **MJ 的活差额律**（"暂时全盘照抄 MJ"，2026-09-20 用户裁定后换回）—— MJ 的**架构**+MJ 的**靶**：
                    //   `MechJebModuleNodeExecutor.NextDirection()` = 把**活轨道**推进到节点 `UT`、
                    //   与**目标速度**（一个**固定向量**，无几何分支）做差 ⇒ 差额**既是方向也是剩余量**；
                    //   终止是几何的（差额反向 ⇒ 超调 ⇒ 收手，= MJ 的 `AngleFromNode() ≥ 90°`）。
                    //   ⚠ **靶不再用 (a,h向量)**（我一度换过：台架 e 好两个数量级，但真实几何上它的
                    //     出范围支是自证式的 ⇒ 游戏实测 430.01 m/s 只交付 164.9 ✗ ✗）⇒
                    //     **MJ 的形式没有几何分支，任何活状态都能算差额** —— 简单即鲁棒。
                    //   ⚠ 用**落位拍**的状态（`physicsTick + dtSeconds`，与上面方向刷新同口径）：
                    //     冲量下一物理步首才并入速度（见 `pendingImpulse` 处注释）⇒ 用本拍状态会差一拍框架。
                    if (group.burnHasTargetV && !group.abFreezeDirection && haveArc)
                    {
                        // ★★ **节点窗口闸**（2026-09-21）——见 `BurnNodeWindowPostSeconds` 的说明。
                        //   活差额律的两条收手判据都只看"差额本身"，节点作废后两条都可能永不触发 ⇒ 无界燃烧。
                        //   这里给出第三条判据：**关于时刻的**。算式在纯层（`BurnStep.NodeWindowExpired`），
                        //   纯测试用**同一份**（唯一算式纪律）。
                        if (BurnStep.NodeWindowExpired(physicsTick, group.burnArcNodeUt, group.burnArcSeconds,
                                BurnNodeWindowPostSeconds)
                            && group.remainingBurnDV > 0.0)
                        {
                            group.remainingBurnDV = 0.0;      // ⇒ 下方 BurnStep 报 exhausted ⇒ EndBurn()
                            group.burnNodeWindowAborts++;
                            Log.Message(string.Format(
                                "[燃烧·窗口] G{0} 节点窗口已过（tick={1:F0} > 截止 {2:F0}）⇒ **停火**。" +
                                "原因：这一发已作废（节点 UT={3:F0}、名义燃时={4:F0}s、已烧 {5:F0}s、" +
                                "已交付 {6:F1} m/s）—— 继续追一个过期靶会无界燃烧（2026-09-21 实测烧掉 4412 m/s / 9080s）",
                                group.GroupId, physicsTick,
                                BurnStep.NodeWindowDeadline(group.burnArcNodeUt, group.burnArcSeconds,
                                    BurnNodeWindowPostSeconds),
                                group.burnArcNodeUt, group.burnArcSeconds,
                                group.burnActualStartTick >= 0.0 ? physicsTick - group.burnActualStartTick : 0.0,
                                group.appliedBurnDV));
                            // 本拍**不再刷新方向**（`remainingBurnDV = 0` 已使本拍的
                            // `BurnStep.Compute(aCmd, accel, mfTotal, dt, 0)` 直接返回 exhausted ⇒
                            // 下面统一走 `EndBurn()`，与"烧完"是同一条退场路径）
                        }
                        else
                        {
                        (DVector3 rLiveUt, DVector3 vLiveUt) = OrbitAdapter.StateAtUT(ovArc, group.burnArcNodeUt);
                        _ = rLiveUt;
                        // 算式与判据都在纯层（`BurnStep`），台架用**同一份**（唯一算式纪律）
                        BurnStep.DeficitToTargetV(vLiveUt, group.burnTargetV, group.burnDirection,
                            BurnTargetVFloor, out DVector3 defDir, out double defRem, out bool defRev);
                        // ★★ **差额小到"追它不如停手"⇒ 不发指令**（2026-09-21 定，同日两次更正）
                        //
                        // ## 为什么（"抽"的本体）
                        // 修正量掉到接近噪声时**方向没有定义**：实测收尾每一发都与上一发**反号**，
                        // 而每次掉头要 **36 秒**（180°/5°·s⁻¹）、节点只给 13–15 秒 ⇒ 转不完就点火
                        // ⇒ 打出 `径向 22.81 / 切向 0.54` 的歪冲量 ⇒ 把刚建立的接近又破坏掉。
                        // ⇔ 上游 `NextDirection()`：`_dvLeft < MaxThrustAcceleration ⇒ return _direction`。
                        //
                        // ## ⚠⚠ 判据**不能是固定 5 m/s**（第一版的错，实测代价：把真活儿也砍了）
                        // 第一版取常数 5 m/s ⇒ 日志出现
                        //   `差额只剩 4.74 m/s（< 5.0 地板）⇒ 不再发指令。已交付 16.9 m/s`
                        // ——那一发**只差 4.7 m/s 就能到位**，却被地板停住 ⇒ **残差留在轨道上、
                        // 一轮一轮累积**（4.74 + 4.66 + …）⇒ 交会永远差一口气。
                        //
                        // ⇒ 正确的判据是**"这一步能不能把这差额烧掉"**：
                        //   能 ⇒ 照烧（它是真活儿）；**不能** ⇒ 烧了也只会过冲/反号 ⇒ 才停手。
                        //   即：`差额 < 本步全油门能给的 Δv` ⇒ 停手（一步之内干不了，追它就是抽）。
                        //   本步全油门 Δv 的算式与执行器**同一份**（`BurnStep.Compute` 的 `aFull·dt`）——
                        //   这里用 `_burnStepDvEstimate` 那一份，别另写。
                        if (defRem > 0.0 && defRem < BurnDeficitIgnoreFloor)
                        {
                            group.remainingBurnDV = 0.0;   // 收工（下方 BurnStep 报 exhausted ⇒ EndBurn）
                            group.burnThrottleCap = 0.0;
                            group.burnDeficitIgnoredCount++;
                            Log.Message(string.Format(
                                "[燃烧·收尾] G{0} 差额只剩 {1:F2} m/s（< {2:F1} m/s 地板）⇒ **不再发指令**"
                                + "（方向已进入噪声：追它只会导致 180° 掉头）。已交付 {3:F1} m/s；完成判据接管",
                                group.GroupId, defRem, BurnDeficitIgnoreFloor, group.appliedBurnDV));
                            defRem = 0.0;   // 让下面的分支走"收工"那一支（不再刷新方向）
                        }
                            if (defRem > 0.0)
                            {
                                group.burnDirection = defDir;
                                group.remainingBurnDV = defRem;   // 剩余量**由活状态重算**，不再逐拍递减
                                group.burnComponentsInit = true;   // 靶律直接给方向 ⇒ 不需要 RTN 分解
                                // ★★ **需求推油门**（2026-09-21，读 MJ `ThrustForDv` 之后）：
                                //   MJ 的 `throttle × MaxAccel × τ = _dvLeft` ⇒ **油门与"还差多少"是同一件事**，
                                //   且 `_dvLeft` 每帧从活轨道重算 ⇒ 没有一帧可以被锁死。
                                //   我们原来点火锁 `burnCommandAccel = Δv/t_b` 常量化 ⇒ 实际可用加速度与它不符时，
                                //   **整段燃烧都按错的加速度走**（没有任何一帧会纠正）⇒ 交付率与记账分家 ✗。
                                //   ⇒ 有节点靶的燃烧**不用常量加速度**：`cmd=0`（全油门为上限）+ 把差额当油门帽，
                                //     于是本步 Δv = min(a_可用·dt, 差额) —— 需求与可得**同尺**。
                                group.burnCommandAccel = 0.0;
                                group.burnThrottleCap = defRem;
                            }
                            else
                            {
                                group.remainingBurnDV = 0.0;   // 达标或反向 ⇒ 收工（下方 BurnStep 会报 exhausted）
                                group.burnThrottleCap = 0.0;
                                if (defRev) group.burnReversedStopCount++;
                            }
                        }
                    }
                    // ── 退路（**拿不到意图轨道**的燃烧：微调条目、引导逐帧指令）：旧的 RTN 分量律 ──
                    else
                    {
                        // ★ 首次刷新：把**计划的解**（= 当前 `burnDirection`，`StartBurn` 设的世界方向）在
                        //   这一帧的**正本** RTN 里分解一次。此后整段燃烧只换框架、不换解。
                        //   ⚠ 必须在下面那句**之前**做，且不能在 `StartBurn` 里做 —— 那里拿不到正本
                        //   （副本差一个帧速 ⇒ ~0.13° 方向偏差），而这里若晚一步就会用**上一次燃烧的残留分量**
                        //   去合成方向（= 把刚设好的正确方向覆盖掉）。
                        if (!group.burnComponentsInit)
                        {
                            OrbitMath.WorldDeltaVToComponents(arcPos, arcVel, group.burnDirection,
                                out group.burnRadial, out group.burnTangential, out group.burnNormal);
                            group.burnComponentsInit = true;
                        }

                        DVector3 arcDir = OrbitMath.GetWorldDeltaV(arcPos, arcVel,
                            group.burnRadial, group.burnTangential, group.burnNormal);
                        if (arcDir.sqrMagnitude > 1e-12 && !group.abFreezeDirection)
                            group.burnDirection = arcDir.normalized;
                    }
                }

                // 多工质网络并行烧：F = Σ(ve·ṁ)，a = F/m_now，各网络按 ṁ 比例消耗各自工质
                // （质量随工质消耗减小，加速度自然增大，无需对数记账）
                var burnSrc = GetGroupPropulsionSource(group);

                float fTotal = 0f, mfTotal = 0f;
                // 第 3 条：组级 scratch 复用（本组本步独占；下方非 Map 分支是 Add 累加，必须先清空）
                var activeSlices = group.thrustSliceScratch;
                activeSlices.Clear();
                // 探针：校验"每组独占一个 scratch 且身份恒定"（并行区两组共用会互相覆盖）
                if (PerfProbe.Enabled) PerfProbe.SliceBuffer(group, activeSlices);

                if (burnSrc is SpaceMapComp mapComp)
                {
                    if (group.burnUseRcs && mapComp.HasRcs)
                    {
                        // ── RCS 全矢量（微调 burn）：任意方向出力，免对准/免锥 ──
                        // 交会/接近修正 burn 走此分支：无转向死区，修正即时。
                        mapComp.GetRcsThrustAlong(group.burnEnabledNetworks, activeSlices, out fTotal, out mfTotal);
                        if (fTotal <= 0f)
                        {
                            // RCS 工质尽 → 回退主引擎（对准，现状）
                            mapComp.GetThrustAlong(group.burnDirection, group.CurrentOrbit.rotation, DefaultThrustConeCos,
                                group.burnEnabledNetworks, activeSlices, out fTotal, out mfTotal);
                            AlignMainAxis(group, group.burnDirection, dtSeconds);
                            if (fTotal <= 0f && mapComp.TotalThrust <= 0f)
                                result.BurnExhausted = true; // 完全没有引擎 → 无法推进，终止
                        }
                        // RCS 有出力：不跑 AlignMainAxis（全矢量无需对准，船保持当前姿态）
                    }
                    else
                    {
                        if (!mapComp.HasDirectionalEngines)
                        {
                            // 纯全矢量船（仅虚拟推进/RCS，无 per-axis 引擎）：任意方向出力，免对准
                            mapComp.GetRcsThrustAlong(group.burnEnabledNetworks, activeSlices, out fTotal, out mfTotal);
                            if (fTotal <= 0f && mapComp.TotalThrust <= 0f)
                                result.BurnExhausted = true; // 完全没有引擎 → 无法推进，终止
                        }
                        else
                        {
                            // ── 主引擎方向感知：矢量锥内轴向出力 + 自动对准兜底 ──
                            // GetThrustAlong 只取「轴向落在 burnDirection 矢量锥内」的引擎；
                            // 未对准（锥内无推力）→ 本步只旋转对准（Align 控制器），不推力不耗工质。
                            mapComp.GetThrustAlong(group.burnDirection, group.CurrentOrbit.rotation, DefaultThrustConeCos,
                                group.burnEnabledNetworks, activeSlices, out fTotal, out mfTotal);
                            // 已对准 → 消旋；未对准 → 转向（顺带零掉残余角速度）
                            AlignMainAxis(group, group.burnDirection, dtSeconds);
                            if (fTotal <= 0f && mapComp.TotalThrust <= 0f)
                                result.BurnExhausted = true; // 完全没有引擎 → 无法推进，终止
                        }
                    }
                }
                else
                {
                    // ── 无 Map 源：主推方向型（D15）+ 可选弱 RCS 副网，或纯全向 ──
                    // 主网（PropulsionData.isVectoredRcs=false）：主推型沿 mainAxisLocal 锥内出力；
                    // RCS 网（isVectoredRcs=true）：全矢量小推力——主推未对准时仍可出力修正、主网尽时兜底。
                    DVector3 mainAxisWorld = GetMainThrustWorldAxis(group);
                    bool isMainThrust = mainAxisWorld.sqrMagnitude > 1e-12;
                    double axisDot = 1.0;
                    if (isMainThrust)
                    {
                        // 姿态控制独立于燃料（魔动量轮不限料）：本步开头执行——
                        // 已收心（axis·burn ≥ cos~1.5°）→ 立即消旋，杜绝残余 ω 让弹体在主推燃烧时漂出；
                        // 未收心 → 全速转向（剩余夹角小于一步转角则降速收敛）。
                        axisDot = DVector3.Dot(mainAxisWorld.normalized, group.burnDirection.normalized);
                        AlignMainAxis(group, group.burnDirection, dtSeconds);
                    }
                    // ★ 点火门 = **锥角 10°**（`DefaultThrustConeCos` —— 本文件 65 行的**文档口径**就是
                    //   "窗口内可出力、收心到死区才点火"，`DefaultThrustAlignCos`(1.5°) 只用于消旋/精修）。
                    //   ⚠ 2026-09-19 实测（**代码与自己的文档矛盾**）：这里原来用 1.5° 死区当点火门 ⇒
                    //   质心在死区附近摆动时**整步空转**（不推力不耗料）⇒ 燃烧被拉长（98s→106s）、
                    //   Δv 质心后移。台架定量：起步空转 2 步就吃掉不变量 4.4%（0.9975 → **0.956**），
                    //   与实测 0.9808 同档 ⇒ **这就是"幅值方向都对、轨道就是偏"的最后一环**。
                    bool mainAligned = !isMainThrust || axisDot >= DefaultThrustConeCos;

                    // pass1：各类网络可用性（启用 + ṁ>0 + 有料）+ RCS 存量/流率（预判本步是否耗尽）
                    bool hasMainFuel = false, hasRcsFuel = false;
                    double rcsAvailKg = 0.0, rcsFlowKgS = 0.0;
                    if (burnSrc != null)
                    {
                        foreach (var n in burnSrc.Networks)
                        {
                            if (n.MassFlow <= 0f) continue;
                            if (group.burnEnabledNetworks != null && !group.burnEnabledNetworks.Contains(n.propellant)) continue;
                            double avail = n.AvailableMass(burnSrc);
                            if (avail <= 0f) continue;
                            if (n.HasRcsEngines)
                            {
                                hasRcsFuel = true;
                                rcsAvailKg += avail;
                                rcsFlowKgS += n.MassFlow;
                            }
                            else hasMainFuel = true;
                        }
                    }
                    if (!hasMainFuel && !hasRcsFuel)
                    {
                        result.BurnExhausted = true; // 全工质尽 → 终止 burn
                    }
                    else
                    {
                        // 选网策略：
                        //  全向            → 主网 + RCS 全部出力；
                        //  主推已对准       → 主网出力（RCS 保留修正/兜底）；主网尽 → RCS 兜底；
                        //  主推未对准       → 主网等待（只转），RCS 小推力修正；无 RCS → 纯转向等对准。
                        bool useMain = false, useRcs = false;
                        // ★★ **细修走 RCS**（2026-09-21，用户："我们是不是用主推干了本来应该用 RCS 细修的事情"）
                        //
                        // ## 为什么（实测）
                        // 收尾段修正量是 **1–4 m/s**（`dv=1.5/2.3/2.6/3.4/3.7`），而原策略是
                        // **"主推已对准 ⇒ 主网出力"**（RCS 只在未对准/主网尽时上场）⇒ 细修全落给主推：
                        // `a_cmd≈12.5 m/s² × dt 2 s = **25 m/s 一步**`，**比要做的活大 16 倍** ✗✗
                        // ⇒ 方向差一点就把刚建立的接近打散；叠上"差额小 ⇒ 方向是噪声"⇒ 每轮 180° 掉头
                        // （掉头 36 s、节点只给 13–15 s ⇒ 转不完就点火 ⇒ 歪冲量）。RCS 全矢量免对准
                        // ⇒ 掉头需求消失，0.5–0.7 m/s² × 几秒 = 精确小冲量。
                        //
                        // ## 判据：**RCS 一个物理步烧得完的活，才交给 RCS** —— `aRcs·dt ≥ |Δv|`
                        //   ⚠ **不能用旧制导那句 `aRcs×15s`**：那会把 10 m/s 也判给 RCS，而 RCS 一步只给
                        //     ~1.3 m/s ⇒ 要烧 8 步 ⇒ 拖长燃烧、错过节点。本判据下：1.5 m/s（1 步）⇒ RCS ✓；
                        //     6 m/s（5 步）⇒ 主推 ✓；200 m/s ⇒ 主推 ✓。
                        // ⚠ 只改**主推已对准**那一支（未对准那支本来就是 RCS，不动）。
                        if (!isMainThrust)
                        {
                            useMain = true;
                            useRcs = true;
                        }
                        else if (mainAligned)
                        {
                            if (hasMainFuel) useMain = true;
                            else useRcs = hasRcsFuel; // 主网尽 → RCS 兜底
                        }
                        else
                        {
                            useRcs = hasRcsFuel; // 未对准：主网等待（姿态已在上方 Align 转向）
                        }

                        // RCS 修正燃烧中若本步将耗尽、而主网仍有料 → 不终结 burn（主线程据 SuppressAutoBurnEnd 跳过 EndBurn），
                        // 让姿态继续转向直到对准主网接管；未到耗尽预判的普通修正步不刷 Suppress。
                        bool rcsMayExhaust = rcsFlowKgS > 0.0 && rcsAvailKg <= rcsFlowKgS * dtSeconds;
                        if (useRcs && !useMain && hasMainFuel && rcsMayExhaust)
                            result.SuppressAutoBurnEnd = true;

                        // ── D15 详细日志快照（主线程打 [Thrust]）──
                        result.NoMapThrust = true;
                        result.UseMainSel = useMain;
                        result.UseRcsSel = useRcs;
                        result.AligningMain = isMainThrust && !mainAligned;
                        result.RcsFallbackSel = isMainThrust && mainAligned && !hasMainFuel && useRcs;
                        result.MainDot = isMainThrust
                            ? Math.Max(-1.0, Math.Min(1.0, axisDot))
                            : 1.0;

                        if (burnSrc != null && (useMain || useRcs))
                        {
                            foreach (var n in burnSrc.Networks)
                            {
                                if (n.MassFlow <= 0f) continue;
                                if (group.burnEnabledNetworks != null && !group.burnEnabledNetworks.Contains(n.propellant)) continue;
                                if (n.AvailableMass(burnSrc) <= 0f) continue;
                                bool isRcs = n.HasRcsEngines;
                                if ((isRcs && !useRcs) || (!isRcs && !useMain)) continue;
                                activeSlices.Add(new ThrustSlice { network = n, thrust = n.Thrust, massFlow = n.MassFlow });
                                fTotal += n.Thrust;
                                mfTotal += n.MassFlow;
                            }
                        }
                        // 主推未对准且无 RCS（主网有料）：0 推力 0 耗料，burn 保持激活等对准（不标 exhausted）。
                        // RCS 修正/兜底燃烧时主网未动 → 主网不耗料；RCS 烧尽不误判整 burn 结束（主料仍可用）。
                    }
                }

                // ★ 对照 F：**单脉冲** —— 节点前**一点不烧**（只是活着等），到节点把全部 Δv 一次施加。
                //   ⚠ 绝不能落到普通燃烧分支 ✗（否则节点前就烧掉一部分，F 行就不再是"纯映射"判据了）。
                if (group.abOneShotImpulse)
                {
                    if (group.abNodeTick > 0.0 && physicsTick >= group.abNodeTick && group.remainingBurnDV > 0.0)
                    {
                        double dvAll = group.remainingBurnDV;
                        group.pendingImpulse += group.burnDirection * dvAll;   // 节点处 burnDirection = 计划向量 ✓
                        group.burnLastCmdDv = dvAll;
                        group.remainingBurnDV = 0.0;
                        group.appliedBurnDV += dvAll;
                        result.HasActiveBurn = true;
                        result.BurnExhausted = true;
                        result.PropellantConsumed = 0.0;   // F 行不记账工质（只判几何/映射）
                        result.AppliedDv = dvAll;
                        Log.Message(string.Format("[对照·F] G{0} 单脉冲施加 |Δv|={1:F2} m/s @tick={2:F0}（节点 {3:F0}）",
                            group.GroupId, dvAll, physicsTick, group.abNodeTick));
                    }
                    else
                    {
                        result.HasActiveBurn = true;          // 节点前：保持活跃、不烧、不耗料
                    }
                }
                else if (fTotal > 0f)
                {
                    double groupMass = GetGroupMass(group, burnSrc);
                    if (group.burnStartMass <= 0.0)
                        group.burnStartMass = groupMass; // burn 起始质量（实际 ΔV 评估基准）
                    double accel = fTotal / Math.Max(groupMass, 1.0); // m/s²（全油门）
                    // ★ **命令恒定加速度**（用户 2026-09-19 裁定）：机动 = Δv + 加速度，油门随时适配质量。
                    //   点火那一步的全油门值 = `F参与 / m₀`（可行域里最紧的恒定加速度）⇒ 整段燃烧按它走，
                    //   于是 Δv **均匀**地摊在居中的窗口里（旧的全油门照"可用上限"烧，上限随料耗上升 ⇒
                    //   交付率 76→90/10s；摊平后恒为 74.671 ✓）。
                    //   工质总量不变（火箭方程只依赖总 Δv）—— 只是烧久一点（本场景 98 s ⇒ ~108 s）。
                    //   ⚠ 命令值**只由计划给**（点火块里设 = Δv/t_b），**绝不在燃烧中锁存"当前那一步的全油门"** ✗：
                    //   首个燃烧步可能正在烧 **RCS**（未出锥、导弹起步、主网没料）⇒ 锁存到 RCS 的小加速度
                    //   （实测档 0.4 vs 主网 7.47 m/s²）⇒ **整段燃烧被压成 1/20 速度**（自审发现，未上线）。
                    //   未设（引导逐帧指令那类没有 t_b 的燃烧）⇒ 退回全油门，与改动前逐位相同。
                    double aCmd = (group.abFullThrottle || group.burnCommandAccel <= 0.0) ? accel : group.burnCommandAccel;
                    // 油门份额：命令加速度 / 当前可用全油门（燃料在烧 ⇒ 可用 a 只升，正常 ≤1；
                    // 若某步换网（主网耗尽转 RCS）导致可用 a 掉下来，则按可用的烧并留一行诊断）
                    if (aCmd > accel)
                    {
                        if (PerfProbe.Enabled || group.burnCommandAccel > accel * 1.02)
                            Log.Message(string.Format(
                                "[燃烧] G{0} 命令加速度 {1:F3} 超过可用 {2:F3} m/s²（换网？）⇒ 本步按可用烧",
                                group.GroupId, aCmd, accel));
                        aCmd = accel;
                    }
                    // ★ 头 6 步定点读数（2026-09-19）：实测"执行器头 ~20 s 少交付 ~1.7 m/s，之后追平"
                    //   （模型 vs 实测的曲线在样本 2→3 之间跳 +1.74 ✓）⇒ 只有逐步读数能看见它。
                    //   每轮燃烧只打 6 行，不是散射探针。
                    if (group.burnHeadStepsLogged < 6)
                    {
                        group.burnHeadStepsLogged++;
                        Log.Message(string.Format(
                            "[燃烧·头步] G{0} 第{1}步 t={2:F0}：aCmd={3:F4}｜可用a={4:F4}｜份额={5:F4}｜mass={6:F1}｜fTotal={7:F0}｜网络={8}",
                            group.GroupId, group.burnHeadStepsLogged, physicsTick, aCmd, accel,
                            dvFullSafe(aCmd, accel), groupMass, fTotal, activeSlices.Count));
                    }
                    // ★ **唯一算式**（`BurnStep.Compute`）：执行器与台架/纯测试共用同一份 ⇒
                    //   "台架 vs 游戏"的差只可能来自环境，不可能来自模型（本会话最大的教训）。
                    // ★★ **需求推油门**（2026-09-21）：有节点靶时 `burnThrottleCap` = **本步还差多少**
                    //   （= `DeficitToTargetV` 每拍从活状态重算的差额）⇒ 本步 Δv = min(a_可用·dt, 差额)。
                    //   需求与可得**同尺**：发动机给不出就以可用的量慢慢烧，差额一直在减小 ⇒ 必然收手。
                    //   ⚠ 差额本来就是从活轨道算的，故 `remainingBurnDV` 也应是它 —— 本行保证两者同源。
                    double remainingForStep = group.burnThrottleCap > 0.0
                        ? Math.Min(group.remainingBurnDV, group.burnThrottleCap)
                        : group.remainingBurnDV;
                    BurnStep.Result bs = BurnStep.Compute(aCmd, accel, mfTotal, dtSeconds, remainingForStep);
                    double burnDv = bs.Dv;
                    if (burnDv > 0.0)
                    {
                        result.HasActiveBurn = true;
                        result.BurnExhausted = bs.Exhausted;
                        result.PropellantConsumed = bs.Propellant;
                        result.ActiveSlices = activeSlices; // 引用快照，主线程按切片流率比例分配扣除
                        if (PerfProbe.Enabled) // 探针：快照必须是本组自己的 scratch（否则并行区会被别的组改写）
                            PerfProbe.SliceAlias(ReferenceEquals(activeSlices, group.thrustSliceScratch), $"G{group.GroupId}");
                        // 冲量累积：并行区只写本 group 独占的累积器，主线程在下一物理步首统一施加
                        // （避免并行区直接改 CurrentOrbit.velocity，消除多冲量顺序依赖）
                        group.burnLastCmdDv = burnDv;   // 逐步对账：本步命令的量
                        group.pendingImpulse += group.burnDirection * burnDv;
                        group.remainingBurnDV -= burnDv;
                        group.appliedBurnDV += burnDv; // 累计已施加 dV（诊断/日志）
                        result.AppliedDv = burnDv;
                    }
                    else
                    {
                        result.BurnExhausted = true;
                    }
                }
                else if (!result.BurnExhausted)
                {
                    // 自动对准中（Map 源，引擎存在但未对准）：保持 burn 激活，本步无推力无耗料，继续旋转
                    result.HasActiveBurn = true;
                }
            }

            // ── 预对准（前瞻）：coasting 段按待点火机动方向提前转向主推进轴 ──
            // 机动点火时已对准 → burn 准时居中（消除"先对准再烧"导致的初始 miss）。
            // 有 burn 时不干预（burn 内部自带的 AlignMainAxis 已接管）。
            if (!group.HasActiveBurn && group.pendingAlignDir.sqrMagnitude > 1e-12)
                AlignMainAxis(group, group.pendingAlignDir, dtSeconds);

            // 引力场采样：非圆轨需要它做积分；圆轨（解析推进）只有在轨道保持需要结算 dV 时才需要。
            // 保持旧成本画像——圆轨且无轨道保持的群组不做任何引力计算。
            var scratch = gravityScratch;
            bool needField = !group.IsCircularOrbit || group.HasOrbitKeeping;
            DVector3 fieldAcc = DVector3.zero, fieldJerk = DVector3.zero;
            DVector3 keepAcc = DVector3.zero, keepJerk = DVector3.zero;

            if (needField)
            {
                ComputeGroupFieldAccelJerk(group, celestialMgr, physicsTick, scratch,
                    out fieldAcc, out fieldJerk, out keepAcc, out keepJerk, timeSegment: true);
            }

            // 轨道保持的 dV 结算：**两条推进路径共用**（圆轨走解析推进，但其"抵消非白名单天体扰动"
            // 的燃料开销与积分路径同源；旧实现同样对圆轨结算，勿因重构而漏掉）
            if (group.HasOrbitKeeping && scratch.all.Count > 0)
            {
                result.HasOrbitKeeping = true;
                result.OrbitKeepingDvCost = keepAcc.magnitude * dtSeconds;
            }

            if (group.IsCircularOrbit)
            {
                DVector3 newPos = group.GetCircularPositionAtTick(physicsTick);
                // 圆轨下速度是随相位旋转的解析函数：同步 CurrentOrbit.velocity，
                // 使预览/机动规划/交会等所有读 velocity 的消费方得到当前相位正确值
                // （否则它们会拿到入轨瞬间的初始切线速度，导致轨道被算成漂移的椭圆）。
                DVector3 newVel = group.GetCircularVelocityAtTick(physicsTick);
                if (newPos != group.CurrentOrbit.position)
                {
                    result.NeedsUpdate = true;
                    result.NewPosition = newPos;
                    result.NewVelocity = newVel;
                }
            }
            else if (scratch.all.Count > 0)
            {
                // ── 引力场（A0）：系统系内**全部天体**全量求和，无"环绕天体"参量 ──
                // 坐标 = CurrentOrbit.position（群组系 = 根行星中心，与场同系）→ 不再做任何帧转换。
                // 子天体为中心时（如月球），由场自动带上根天体的贡献（等价于"局部系 + 潮汐项"），
                // 不再需要旧的"减天体位置 / 加回天体位置"两段式。
                DVector3 pos = group.CurrentOrbit.position;
                DVector3 vel = group.CurrentOrbit.velocity;

                if (subSteps <= 1)
                {
                    NBodyIntegrator.ThirdOrderTaylorStep(pos, vel, fieldAcc, fieldJerk, dtSeconds,
                        out var newPos, out var newVel);

                    result.NeedsUpdate = true;
                    result.NewPosition = newPos;
                    result.NewVelocity = newVel;
                }
                else
                {
                    // 分子步：每子步用子步时刻重采天体位置（天体解析位置随时间连续，避免大步长冻结天体）
                    double subDt = dtSeconds / subSteps;
                    for (int i = 0; i < subSteps; i++)
                    {
                        ComputeGroupFieldAccelJerk(group, celestialMgr, physicsTick + i * subDt, scratch,
                            out var acc, out var jerk, out _, out _, timeSegment: true);
                        NBodyIntegrator.ThirdOrderTaylorStep(pos, vel, acc, jerk, subDt, out pos, out vel);
                    }

                    result.NeedsUpdate = true;
                    result.NewPosition = pos;
                    result.NewVelocity = vel;
                }
            }

            if (group.HasOrbitKeeping && !result.HasOrbitKeeping)
            {
                // 无引力场（天体缺失）时仍标记模式，但不产生 dV（无处可抵消）
                result.HasOrbitKeeping = true;
            }

            // ── 姿态积分（魔法动量轮）──
            // 角速度 → rotation 增量；大步长（物理步 2s）下按角度限幅分子步，避免肉眼可见跳变。
            // 并行区只计算新姿态，主线程在结果应用时统一写 CurrentOrbit.rotation。
            if (group.angularVelocity.sqrMagnitude > 1e-12)
            {
                double w = group.angularVelocity.magnitude;
                DVector3 axis = group.angularVelocity / w;
                double totalAngle = w * dtSeconds;
                int steps = Math.Max(1, (int)Math.Ceiling(totalAngle / (10f * Mathf.Deg2Rad)));
                steps = Math.Min(steps, 32);
                DQuaternion rot = DQuaternion.FromQuaternion(group.CurrentOrbit.rotation);
                DQuaternion stepRot = DQuaternion.AngleAxis(totalAngle / steps, axis);
                for (int i = 0; i < steps; i++)
                    rot = stepRot * rot;
                result.HasRotationChange = true;
                result.NewRotation = rot.ToQuaternion();
                result.NeedsUpdate = true;
            }

            result.SyncBodyPos = GetBodyWorldOffset(group.CelestialBodyId, (int)System.Math.Round(physicsTick));
            result.SyncBodyVel = GetBodyWorldVelOffset(group.CelestialBodyId, (int)System.Math.Round(physicsTick));

            return result;
        }

        /// <summary>
        /// 主推轴世界方向（D15）：Map = mapComp.MainThrustAxis 随 rotation 旋转；
        /// 无 Map 主推方向型 = entity.mainAxisLocal 随 rotation 旋转；全向类（无主推轴）= zero。
        /// 并行区只读调用（与 GetGroupPropulsionSource 同纪律）。
        /// 只读公开给表现层（PFView 朝向指示）——**唯一出处**，视图不再复制一份"Map/无 Map"解析分支。
        /// </summary>
        public DVector3 GetMainThrustWorldAxis(SpaceEntityGroup group)
        {
            var mapComp = group.GetLinkedMapComp();
            if (mapComp != null)
                return group.GetWorldAxis(mapComp.MainThrustAxis);
            // 无 Map：任一带主推轴的实体（导弹弹群成员同构同姿态；2026-09-08 思路 A——多实体组推进源
            // 是群级聚合器，不再 as SpaceEntity 取轴，改扫实体；单实体组结果与原来逐字节一致）
            foreach (int eid in group.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e != null && e.HasMainAxis)
                    return DVector3.Rotate(e.mainAxisLocal.Value.normalized, group.CurrentOrbit.rotation);
            }
            return DVector3.zero;
        }

        /// <summary>
        /// 自动对准（SAS 兜底）：把主推进轴转到目标世界方向（收心正中，死区 ~1.5°），向量角速度（动量轮限速）。
        /// 已收心 → 零角速度（消旋）。未收心 → 按剩余夹角比例调速，一步转不完则满速。
        /// 主推轴来源：Map = mapComp.MainThrustAxis；无 Map 主推型 = entity.mainAxisLocal（D15）；
        /// 全向类（无主推轴）→ 直接返回（无对准语义）。
        /// 在并行物理区调用（只写本 group 独占的 angularVelocity，与 pendingImpulse 同纪律）。
        /// </summary>
        private void AlignMainAxis(SpaceEntityGroup group, DVector3 targetDir, double dtSeconds)
        {
            DVector3 mainAxisWorld = GetMainThrustWorldAxis(group);
            if (mainAxisWorld.sqrMagnitude < 1e-12)
                return; // 无主推轴（全向类）：无姿态对准
            DVector3 targetN = targetDir.normalized;
            if (targetN.sqrMagnitude < 1e-12)
            {
                group.angularVelocity = DVector3.zero;
                return;
            }
            double dot = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(mainAxisWorld, targetN)));
            if (dot >= DefaultThrustAlignCos)
            {
                group.angularVelocity = DVector3.zero; // 已收心到正中（≤~1.5°）：消旋，防 burn 期间漂移
                return;
            }
            double error = Math.Acos(dot);
            // 最短旋转轴 = mainAxis × target（右手系）；反平行时叉积退化 → 任取垂直轴
            DVector3 rotAxis = DVector3.Cross(mainAxisWorld, targetN);
            if (rotAxis.sqrMagnitude < 1e-12)
                rotAxis = DVector3.Cross(mainAxisWorld, Math.Abs(mainAxisWorld.y) < 0.9 ? DVector3.up : DVector3.forward);
            DVector3 axisN = rotAxis.normalized;
            // 剩余夹角小于一步最大转角 → 降速收敛（避免越过目标震荡）
            double rate = Math.Min(group.MaxAngularRate, error / Math.Max(dtSeconds, 1e-9));
            group.angularVelocity = axisN * rate;
        }

        /// <summary>
        /// 手动操舵应用（每物理步）：平移冲量 + 旋转角速度 + SAS 对准 + 输入打断清队列（D10）。
        /// 在 UpdateAllMovements 步首调用（冲量随本步 ApplyPendingImpulses 立即生效）。
        /// 平移不走 burn 状态机（避免自动对准把船转向），方向感知推力逐引擎过滤动作组。
        /// </summary>
        private static double PreAlignDiagNextTick = double.NegativeInfinity;
        private static double BurnDiagNextTick = double.NegativeInfinity;

        private void ApplySteering(double dtSeconds)
        {
            if (!SteeringController.IsOpen) return;
            var group = GetGroupById(SteeringController.GroupId);
            if (group == null || !group.IsActive || !group.IsMovable)
            {
                SteeringController.Close();
                return;
            }
            var mapComp = group.GetLinkedMapComp();
            if (mapComp == null) return; // 无 Map 船暂不支持操舵

            bool anyInput = SteeringController.AnyTranslation || SteeringController.AnyRotation
                || SteeringController.SasKind != SasTargetKind.None;

            // D10：任何操舵输入 → 手动接管：中止 burn、清空机动队列与引导
            if (anyInput)
            {
                bool hadAuto = group.HasActiveBurn || group.plannedManeuvers.Count > 0 || group.HasRendezvousGuidance;
                if (group.HasActiveBurn)
                    group.EndBurn();
                if (group.plannedManeuvers.Count > 0 || group.HasRendezvousGuidance)
                {
                    group.ClearManeuvers();
                    trackedManeuvers.Remove(group.GroupId);
                }
                if (hadAuto)
                {
                    mapComp.SetThrustersBurning(false);
                    Log.Message(string.Format("[Steer] Group {0} 手动接管：清空机动队列/引导", group.GroupId));
                }
            }

            // ① 手动平移：方向感知直施冲量 + 扣工质
            DVector3 transDir = SteeringController.GetTranslationWorldDir(group);
            if (transDir.sqrMagnitude > 1e-9)
            {
                // 第 3 条：主线程 scratch 复用（本方法是单组逐步调用，无重入；显式清空不依赖被调方语义）
                var slices = steeringSliceScratch;
                slices.Clear();
                mapComp.GetManualThrustAlong(transDir, group.CurrentOrbit.rotation, DefaultThrustConeCos,
                    group.burnEnabledNetworks, mapComp.armedGroupsMask, slices, out float f, out float mf);
                bool burning = false;
                if (f > 0f && mf > 0f)
                {
                    double mass = GetGroupMass(group, mapComp);
                    double dv = f / Math.Max(mass, 1.0) * dtSeconds;
                    group.pendingImpulse += transDir.normalized * dv;
                    float mfTotal = 0f;
                    for (int i = 0; i < slices.Count; i++) mfTotal += slices[i].massFlow;
                    for (int i = 0; i < slices.Count; i++)
                    {
                        float share = mfTotal > 0f ? mf * (float)dtSeconds * (slices[i].massFlow / mfTotal) : 0f;
                        ConsumeGroupPropellant(group, slices[i].network.propellant, share);
                    }
                    burning = true;
                }
                mapComp.SetThrustersBurning(burning);
                if (DebugDetailedSteering && Find.TickManager.TicksGame >= nextSteerLogTick)
                {
                    DVector3 dirN = transDir.normalized;
                    Log.Message(string.Format("[Steer] Group {0} 平移 dir=({1:F2},{2:F2},{3:F2}) 推力={4:F1}kN 流率={5:F1}kg/s rcs={6} 实际推进={7}",
                        group.GroupId, dirN.x, dirN.y, dirN.z, f / 1000f, mf, mapComp.HasRcs, burning));
                    nextSteerLogTick = Find.TickManager.TicksGame + 10;
                }
            }
            else if (!SteeringController.AnyRotation && SteeringController.SasKind == SasTargetKind.None)
            {
                // 无任何操舵输入：熄火表现（清掉上一步手动平移留下的喷焰）
                mapComp.SetThrustersBurning(false);
            }

            // ② 手动旋转：动量轮直设角速度（无输入不动 angularVelocity → 自由自转）
            if (SteeringController.AnyRotation)
                group.angularVelocity = SteeringController.GetRotationWorldAngularVelocity(group);

            // ③ 手动 SAS：主推进轴对准目标（复用 AlignMainAxis；对准后消旋保持姿态）
            if (SteeringController.SasKind != SasTargetKind.None)
            {
                DVector3 target = SteeringController.GetSasTarget(group);
                if (target.sqrMagnitude > 1e-9)
                    AlignMainAxis(group, target, dtSeconds);
            }

            // 详细日志：旋转 / SAS（与平移共用节流）
            if (DebugDetailedSteering && Find.TickManager.TicksGame >= nextSteerLogTick)
            {
                if (SteeringController.AnyRotation)
                {
                    DVector3 w = group.angularVelocity;
                    Log.Message(string.Format("[Steer] Group {0} 旋转 ω=({1:F2},{2:F2},{3:F2})°/s 幅值={4:F2}°/s",
                        group.GroupId, w.x * Mathf.Rad2Deg, w.y * Mathf.Rad2Deg, w.z * Mathf.Rad2Deg,
                        (float)w.magnitude * Mathf.Rad2Deg));
                }
                if (SteeringController.SasKind != SasTargetKind.None)
                {
                    DVector3 t = SteeringController.GetSasTarget(group);
                    Log.Message(string.Format("[Steer] Group {0} SAS→{1} target=({2:F2},{3:F2},{4:F2})",
                        group.GroupId, SteeringController.SasKind, t.x, t.y, t.z));
                }
                nextSteerLogTick = Find.TickManager.TicksGame + 10;
            }
        }

        /// <summary>
        /// 物理步首：把各 group 累积的合冲量施加到速度并清零。
        /// 圆轨 group 先退出圆轨（解析位置/速度会被覆盖）；施加后标记轨道脏。
        /// </summary>
        private void ApplyPendingImpulses()
        {
            foreach (var group in groups.Values)
            {
                if (!group.IsMovable) continue;
                if (group.pendingImpulse.sqrMagnitude <= 0.0) continue;
                if (group.IsCircularOrbit)
                    group.ExitCircularOrbit();
                // ★ **逐步对账**（2026-09-19）：命令说这一步该给 aCmd·dt，这一行是**状态实际吃到的量**。
                //   悬案：交付端逐项验证精确（每步命令 / 工质 / 方向），但有效能量系统性少 0.34%
                //   （Δa = +8.46 km，而台架同政策全族只有 +0.9~+1.9 km）⇒ 这一步必须逐笔对。
                //   只在开发者模式打，每轮燃烧最多 60 行。
                if (Prefs.DevMode && group.burnStepAuditCount < 60)
                {
                    group.burnStepAuditCount++;
                    double got = group.pendingImpulse.magnitude;
                    group.appliedDvAudit += got;   // ⚠ 先累加**再**打印（上一版打的是"加本笔之前"的值 ⇒ 我据此误判过一次 ✗）
                    Log.Message(string.Format(
                        "[燃烧·对账] G{0} 第{1}笔：命令={2:F5}｜实收={3:F5}｜差={4:F2}%｜**含本笔**累计实收={5:F3} m/s",
                        group.GroupId, group.burnStepAuditCount, group.burnLastCmdDv, got,
                        group.burnLastCmdDv > 1e-9 ? (got - group.burnLastCmdDv) / group.burnLastCmdDv * 100.0 : 0.0,
                        group.appliedDvAudit));
                }
                group.CurrentOrbit.velocity += group.pendingImpulse;
                group.pendingImpulse = DVector3.zero;
                group.OrbitDirty = true;
            }
        }

        protected virtual void UpdateAllMovements(TimeScaleManager timeScaleManager)
        {
            double dtSeconds = UPDATE_INTERVAL_TICKS;
            int subSteps = 1;
            double physicsTick = Find.TickManager.TicksGame;
            
            if (timeScaleManager != null)
            {
                float physicsSpeed = timeScaleManager.PhysicsSpeedMultiplier;
                dtSeconds = UPDATE_INTERVAL_TICKS * physicsSpeed;
                subSteps = timeScaleManager.SubSteps;
                physicsTick = timeScaleManager.GetPhysicsTick();
            }
            
            var celestialMgr = celestialRef.Get();
            if (celestialMgr == null) return;

            // ── 手动操舵（每物理步）：平移/旋转/SAS 输入应用 + 输入打断清队列（D10）──
            ApplySteering(dtSeconds);

            // ── 物理步首：统一施加上一步累积的合冲量（burn/碰撞动量交换/爆炸/手动 ΔV），
            //    然后清零。并行区此后只累积新冲量，不直接改速度。──
            ApplyPendingImpulses();

            // PERF-5：小规模回退顺序——预分配数组存结果，编组数低于阈值走顺序循环，避免并发容器分配
            var movableGroups = movableGroupsScratch;
            movableGroups.Clear();
            foreach (var group in groups.Values)
            {
                if (group.IsMovable)
                    movableGroups.Add(group);
            }

            if (physicsResultsScratch.Length < movableGroups.Count)
            {
                // 扩容留余量（翻倍）：组数持续增长时避免每步重新分配
                // （2026-09-10 探针实测：按精确尺寸扩容会在弹群解离/场景加船的步骤上反复重分配）
                int cap = Math.Max(movableGroups.Count, physicsResultsScratch.Length * 2);
                physicsResultsScratch = new GroupPhysicsResult[cap];
            }
            var physicsResults = physicsResultsScratch;
            EnsureGravityScratchCapacity(movableGroups.Count);

            // 探针（第 2 条）：容器身份稳定（确在复用）+ 复用数组尾部陈旧槽统计（下方按 Count 上界跳过）
            if (PerfProbe.Enabled) PerfProbe.StepScratch(movableGroups, physicsResults, movableGroups.Count);

            if (movableGroups.Count < PARALLEL_PHYSICS_MIN_GROUPS)
            {
                for (int i = 0; i < movableGroups.Count; i++)
                    physicsResults[i] = ComputeGroupPhysics(movableGroups[i], dtSeconds, physicsTick, celestialMgr, GetGravityScratch(i), subSteps);
            }
            else
            {
                Parallel.For(0, movableGroups.Count, i =>
                {
                    physicsResults[i] = ComputeGroupPhysics(movableGroups[i], dtSeconds, physicsTick, celestialMgr, GetGravityScratch(i), subSteps);
                });
            }

            var groupsToCheck = groupsToCheckScratch;
            if (PerfProbe.Enabled) PerfProbe.GroupsToCheckLeftover(groupsToCheck.Count); // 探针：上一步遗留项（应被清空）
            groupsToCheck.Clear();

            // 只遍历本步有效的前 Count 项：复用数组时尾部仍是上一步的结果，不得参与本步
            for (int ri = 0; ri < movableGroups.Count; ri++)
            {
                var result = physicsResults[ri];
                if (!groups.TryGetValue(result.GroupId, out var group)) continue;

                bool burnEndedThisStep = false;

                if (result.HasActiveBurn)
                {
                    // 多网络按切片流率比例分配消耗并扣各自工质；全部网络工质尽 → 燃烧终止
                    float totalConsumed = 0f;
                    int activeCount = result.ActiveSlices != null ? result.ActiveSlices.Count : 0;
                    int exhaustedCount = 0;
                    if (activeCount > 0)
                    {
                        float mfTotal = 0f;
                        for (int i = 0; i < activeCount; i++)
                            mfTotal += result.ActiveSlices[i].massFlow;
                        // ★ 执行器**实际所用**网络的 ΣF·dt 与 Σṁ·dt ⇒ 真实 ve = ΣF/Σṁ 的唯一来源。
                        //   ⚠ 用"全部网络合并"的 `EffectiveVe` 会**白送 Δv 假警报**：2026-09-19 实测
                        //   过推档船（主网 ve=4000/ṁ=140 + RCS 网 ve=1500/ṁ=20）= 合并 ve 3687.5，
                        //   而一次只烧主网 ⇒ 808.5 施加 vs "749.6 的质量口径" 被误读成"白送 58 m/s"。
                        double fSum = 0.0, mfSum = 0.0;
                        for (int i = 0; i < activeCount; i++)
                        {
                            fSum += result.ActiveSlices[i].thrust * dtSeconds;
                            mfSum += result.ActiveSlices[i].massFlow * dtSeconds;
                        }
                        group.burnThrustDt += fSum;
                        group.burnFlowDt += mfSum;
                        for (int i = 0; i < activeCount; i++)
                        {
                            var slice = result.ActiveSlices[i];
                            float share = mfTotal > 0f ? (float)result.PropellantConsumed * (slice.massFlow / mfTotal) : 0f;
                            float actual = ConsumeGroupPropellant(group, slice.network.propellant, share);
                            totalConsumed += actual;
                            if (actual < share - 0.001f)
                                exhaustedCount++;
                        }
                    }
                    group.burnConsumedPropellant += totalConsumed;

                    bool allNetworksExhausted = activeCount > 0 && exhaustedCount >= activeCount;
                    if ((allNetworksExhausted || result.BurnExhausted) && !result.SuppressAutoBurnEnd)
                    {
                        // 实际 ΔV 评估（质量口径）：veBurn·ln(m0/m1)，验证 ṁ/推力模型与火箭方程一致性。
                        // `veBurn` = **本次燃烧真实用到的那些网络**的 ΣF/Σṁ（按步累积），不是"全部网络合并"。
                        float veBurn = group.burnFlowDt > 0.0 ? (float)(group.burnThrustDt / group.burnFlowDt) : 0f;
                        float veMerged = GetGroupPropulsionSource(group)?.EffectiveVe ?? 0f;
                        double actualDv = veBurn > 0f && group.burnStartMass > 0.0 && group.burnConsumedPropellant > 0.0
                            ? veBurn * Math.Log(group.burnStartMass / Math.Max(group.burnStartMass - group.burnConsumedPropellant, 1.0))
                            : 0.0;
                        group.EndBurn();
                        // burn 结束：推进器退出燃烧状态（表现层）
                        group.GetLinkedMapComp()?.SetThrustersBurning(false);
                        trackedManeuvers.Remove(group.GroupId);
                        alignStateByGroup.Remove(group.GroupId);
                        Log.Message(string.Format(
                            "[Burn] Group {0} burn completed: 实际时长={6:F0}s appliedDv={1:F1} m/s actualDv(mass,veBurn={5:F0})={2:F1} m/s（比值 {7:F4}）propellant={3:F1} kg (propellantOut={4})｜全部网络合并 ve={8:F0}（**非执行器所用**，仅参考）",
                            group.GroupId, group.appliedBurnDV, actualDv, group.burnConsumedPropellant, allNetworksExhausted, veBurn,
                            group.burnActualStartTick >= 0.0 ? physicsTick - group.burnActualStartTick : 0.0,
                            group.appliedBurnDV > 1e-9 ? actualDv / group.appliedBurnDV : 0.0, veMerged));
                        group.burnActualEndTick = physicsTick;
                        burnEndedThisStep = true;
                    }
                }
                else if (result.BurnExhausted && !result.SuppressAutoBurnEnd)
                {
                    group.EndBurn();
                    // burn 结束：推进器退出燃烧状态（表现层）
                    group.GetLinkedMapComp()?.SetThrustersBurning(false);
                    trackedManeuvers.Remove(group.GroupId);
                    alignStateByGroup.Remove(group.GroupId);
                    burnEndedThisStep = true;
                }

                // ── 无 Map 主推/RCS 机动详细日志（DebugDetailedThrust，D15 改动验证）──
                // 每物理步：选网（主/RCS/兜底）、主推轴-目标夹角、Δv/余 dv、双罐燃料、燃尽抑制、转向角速度。
                if (DebugDetailedThrust && result.NoMapThrust)
                {
                    double angleDeg = Mathf.Rad2Deg * Mathf.Acos((float)Math.Max(-1.0, Math.Min(1.0, result.MainDot)));
                    string sel = result.UseMainSel && result.UseRcsSel ? "主+RCS"
                        : result.UseRcsSel ? "RCS" : result.UseMainSel ? "主" : "无";
                    if (result.RcsFallbackSel) sel += "(兜底)";
                    string alignState = result.AligningMain ? "对准中" : (result.UseMainSel ? "已对准" : "全向");
                    string note = result.SuppressAutoBurnEnd ? "  [!RCS尽主料存→不终burn]" : "";
                    Log.Message(string.Format(
                        "[Thrust] G{0} tick={1} {2} | 用网={3} | axis·burn={4:F3}({5:F1}°) | Δv={6:F1} m/s 余dv={7:F1} | 耗={8:F2}kg | {9}{10} | ω={11:F1}°/s",
                        group.GroupId, Find.TickManager.TicksGame, alignState, sel,
                        result.MainDot, angleDeg, result.AppliedDv, group.remainingBurnDV,
                        result.PropellantConsumed, NoMapFuelSummary(group), note,
                        (float)group.angularVelocity.magnitude * Mathf.Rad2Deg));
                }

                // ── 自动对准详细日志（DebugDetailedSteering）：对准开始/完成/进度 ──
                if (DebugDetailedSteering && result.HasActiveBurn)
                {
                    bool aligningNow = result.ActiveSlices == null || result.ActiveSlices.Count == 0;
                    bool wasAligning = alignStateByGroup.TryGetValue(group.GroupId, out var prevAlign) && prevAlign;
                    if (aligningNow && !wasAligning)
                    {
                        DVector3 mainAxisWorld = GetMainThrustWorldAxis(group);
                        Log.Message(string.Format(
                            "[Align] Group {0} 自动对准开始：burnDir=({1:F2},{2:F2},{3:F2}) 主轴世界=({4:F2},{5:F2},{6:F2})",
                            group.GroupId, group.burnDirection.x, group.burnDirection.y, group.burnDirection.z,
                            mainAxisWorld.x, mainAxisWorld.y, mainAxisWorld.z));
                    }
                    else if (!aligningNow && wasAligning)
                    {
                        Log.Message(string.Format("[Align] Group {0} 对准完成 → 进入 burn（主轴进入 {1}° 矢量锥）",
                            group.GroupId, Mathf.Rad2Deg * Mathf.Acos(Mathf.Clamp(SpaceGameComp.DefaultThrustConeCos, -1f, 1f))));
                    }
                    else if (aligningNow && Find.TickManager.TicksGame >= nextAlignLogTick)
                    {
                        DVector3 mainAxisWorld = GetMainThrustWorldAxis(group);
                        double dot = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(mainAxisWorld, group.burnDirection.normalized)));
                        Log.Message(string.Format(
                            "[Align] Group {0} 对准中：主轴=({1:F2},{2:F2},{3:F2}) vs burnDir 夹角 {4:F1}° 角速度={5:F2}°/s",
                            group.GroupId, mainAxisWorld.x, mainAxisWorld.y, mainAxisWorld.z,
                            Mathf.Rad2Deg * Mathf.Acos((float)dot), (float)group.angularVelocity.magnitude * Mathf.Rad2Deg));
                        nextAlignLogTick = Find.TickManager.TicksGame + 30;
                    }
                    alignStateByGroup[group.GroupId] = aligningNow;
                }

                if (result.NeedsUpdate)
                {
                    var newOrbit = new OrbitState
                    {
                        position = result.NewPosition,
                        velocity = result.NewVelocity,
                        rotation = result.HasRotationChange ? result.NewRotation : group.CurrentOrbit.rotation
                    };
                    UpdateGroupOrbit(group.GroupId, newOrbit);
                }

                // 圆轨两段式机动：仅当本步 burn 结束 → 轨道已落位（NeedsUpdate），再按当前轨道二次计算并结算入轨
                if (burnEndedThisStep && group.HasPendingCircularize)
                    TryCompleteCircularize(group, group.pendingCircularizeBodyId);

                // ★ B2.2-b **闭环微调**：主燃烧刚结束、且**后面没有别的机动**时，按「现在这一刻的轨道 vs
                //   意图轨道」算一次小 Δv 收口（`TrimBurn`）。为什么在这里：① 主燃烧结束时离目标半径最近
                //   （Δa 残差 = |r − a_t| 最小）；② 再晚就会被后面的机动搅在一起。
                //   ⚠ 每次机动最多一次（`trimFired`）：二次微调要靠它自己收敛 ⇒ 先不做，避免自激振荡。
                // ★ “**本机动允不允许微调**”由阶梯按选中的级置位（`trimAllowedByRung`）：
                //   微调的语义是“收 `(a,|h|)` 残差” ⇒ 只适合“立轨道”那几级；
                //   拦截/匹配速度/最近点 几级的使命是“到达”，微调会跟它们对抗 ✗。
                if (burnEndedThisStep && group.trimEnabled && group.trimAllowedByRung && group.trimHasTarget && !group.trimFired
                    && group.plannedManeuvers.Count == 0)
                {
                    TryQueueTrimBurn(group, celestialMgr, physicsTick);
                }

                if (result.HasOrbitKeeping && result.OrbitKeepingDvCost > 0.0)
                {
                    // 轨道保持消耗工质：Δm ≈ m·ΔV/ve（小 ΔV 线性近似，避免对数），按启用网络 ṁ 比例分配
                    var okSrc = GetGroupPropulsionSource(group);
                    float okVe = okSrc != null ? okSrc.EffectiveVe : 0f;
                    bool okExhausted;
                    if (okVe <= 0f)
                    {
                        okExhausted = true; // 未装引擎 → 无法维持
                    }
                    else
                    {
                        double okMass = GetGroupMass(group, okSrc);
                        double dmTotal = okMass * result.OrbitKeepingDvCost / okVe;
                        // 分配到启用网络
                        float mfTotal = 0f;
                        foreach (var n in okSrc.Networks)
                        {
                            if (n.MassFlow <= 0f) continue;
                            if (group.burnEnabledNetworks != null && !group.burnEnabledNetworks.Contains(n.propellant)) continue;
                            mfTotal += n.MassFlow;
                        }
                        okExhausted = false;
                        if (mfTotal <= 0f)
                        {
                            okExhausted = true;
                        }
                        else
                        {
                            double consumedTotal = 0.0;
                            int okActive = 0, okExhaustedCount = 0;
                            foreach (var n in okSrc.Networks)
                            {
                                if (n.MassFlow <= 0f) continue;
                                if (group.burnEnabledNetworks != null && !group.burnEnabledNetworks.Contains(n.propellant)) continue;
                                okActive++;
                                double share = dmTotal * (n.MassFlow / mfTotal);
                                float actual = ConsumeGroupPropellant(group, n.propellant, (float)share);
                                consumedTotal += actual;
                                if (actual < share - 0.001)
                                    okExhaustedCount++;
                            }
                            okExhausted = okActive > 0 && okExhaustedCount >= okActive;
                        }
                    }
                    if (okExhausted)
                    {
                        group.ExitOrbitKeeping();
                        Log.Message(string.Format("[OrbitKeeping] Group {0} propellant exhausted, exited orbit-keeping mode.", group.GroupId));
                    }
                }

                group.localPosition = group.CurrentOrbit.position - result.SyncBodyPos;
                group.localVelocity = group.CurrentOrbit.velocity - result.SyncBodyVel;

                groupsToCheck.Add(group.GroupId);
            }

            if (PerfProbe.Enabled) PerfProbe.GroupsToCheckConsume(groupsToCheck.Count, movableGroups.Count);
            foreach (int groupId in groupsToCheck)
            {
                if (!groups.TryGetValue(groupId, out var group)) continue;
                CheckPlanetCollision(group, physicsTick);   // ⚠ 传物理时钟：撞击判据与经纬度都用它
            }

            // ── 每 240 真实 tick（4秒）检查一次流速：有机动→Normal，无机动→恢复设置 ──
            // 注意：必须用真实 tick 而非 physicsTick——Combat 模式下 physicsTick 以 1/60 慢速推进，
            // 若以其为基准，射弹消失后要等 16×60=960 真实 tick 才恢复，产生"锁死 1/60x"的错觉。
            if (timeScaleManager != null && Find.TickManager.TicksGame - lastManeuverCheckRealTick >= 240)
            {
                lastManeuverCheckRealTick = Find.TickManager.TicksGame;

                // 射弹在飞 / 导弹 Attack 终末自动段：保持 Combat（1/60）。降速由
                // CombatManager.ManageProjectileTimeScale 负责，这里不恢复期望流速，
                // 避免与降速逻辑打架（否则每 240 tick 会把 Combat 拉回 Fast）。
                var combatMgr = Current.Game?.GetComponent<CombatManager>();
                var avmCheck = Current.Game?.GetComponent<AutonomousVehicleManager>();
                if ((combatMgr != null && combatMgr.ActiveProjectiles.Count > 0)
                    || (avmCheck != null && avmCheck.HasActiveAttack))
                {
                    // 射弹在飞 / 导弹终末撞击中，维持 Combat 档
                }
                // ★★ **交会收尾自动降档**（2026-09-21，用户："最后一公里时自动切换到 1/60x 试试"）
                //   理由：1× 档下**一个物理步能给的 Δv ≈ 88 m/s**，而收尾的修正量只有 15–100 m/s
                //   ⇒ 一次修正 ≈ 一个物理步的粒度，几乎没有余量；而"一拍延迟"（2 s）对应约 1.5 km 的位置误差。
                //   1/60 档把物理步压到 0.033 s（每步 1.5 m/s、一拍延迟 ≈ 25 m）⇒ 最后收口才收得住。
                //   与 CombatManager 的降档同一手法（`QueueCommand`），只是触发条件是**交会距离**。
                else if (TryForceTerminalPrecision(timeScaleManager, out bool forcedNow))
                {
                    // 收尾期：已被强制进 Combat（本 tick 的强制在下一档判定处生效）
                }
                else if (trackedManeuvers.Count > 0)
                {
                    // 有机动待执行或推进中 → 降到 Normal
                    if (timeScaleManager.CurrentLevel == TimeScaleLevel.Fast &&
                        DesiredTimeScale == TimeScaleLevel.Fast)
                    {
                        timeScaleManager.QueueCommand(TimeScaleCommand.ToNormal());
                    }
                }
                else if (timeScaleManager.CurrentLevel != DesiredTimeScale)
                {
                    // 无机动且当前流速与设置不符 → 恢复
                    if (DesiredTimeScale == TimeScaleLevel.Fast)
                        timeScaleManager.QueueCommand(TimeScaleCommand.ToFast());
                    else if (DesiredTimeScale == TimeScaleLevel.Combat)
                        timeScaleManager.QueueCommand(TimeScaleCommand.ToCombat());
                    else
                        timeScaleManager.QueueCommand(TimeScaleCommand.ToNormal()); // 默认 Normal 流速：缺此分支会锁死 Combat 档
                }
            }

            // ── 执行到期机动 ──
            foreach (var group in groups.Values)
            {
                // （2026-09-19 的"[机动循环·诊断]"已撤：它服务的是"108° 对准"那个案子，
                //   根因已查明 = 场景提前量不足 + 燃烧居中吃掉对准时间 ⇒ 结论已进台账，探针退役别刷屏。）

                if (group.plannedManeuvers.Count == 0)
                {
                    group.pendingAlignDir = DVector3.zero; // 无待执行机动 → 取消预对准
                    continue;
                }

                var nextManeuver = group.PeekNextManeuver();
                if (nextManeuver == null)
                    continue;

                var pm = nextManeuver.Value;

                // 上一段机动仍在燃烧时，等待其结束再点火（防止覆盖 burn 状态）
                if (group.HasActiveBurn)
                    continue;

                // ── 意图活重解（P2 /《大修计划》§2.5 / B1.2）：进预点火窗口时按**活轨道**重算一次 ──
                // 为什么放这里：规划到点火之间可能隔了几百上千秒，那段时间里轨道已经漂了 ⇒
                // 照烧"规划那一刻的冻结向量"就是计划稿说的"提交过期向量"。重解结果**写回条目**
                // ⇒ 下面的预对准方向、可行性估算（dvMag）、点火全部自动用活值，改动面最小。
                // 每条约莫解一次（`intentResolved` 置位，解失败也置位，免得每 tick 反复重试）。
                if (pm.intent != null && !pm.intentResolved
                    && physicsTick >= pm.executeTick - ManeuverPreBurnLookaheadTicks)
                {
                    double dvBefore = Math.Sqrt(pm.radialDV * pm.radialDV + pm.tangentialDV * pm.tangentialDV
                                                + pm.normalDV * pm.normalDV);
                    if (OrbitAdapter.TryResolveIntentForBurn(group, pm, physicsTick, out var refreshed, out string resolveWhy))
                    {
                        pm = refreshed;
                        group.plannedManeuvers[0] = pm;
                        double dvAfter = Math.Sqrt(pm.radialDV * pm.radialDV + pm.tangentialDV * pm.tangentialDV
                                                   + pm.normalDV * pm.normalDV);
                        Log.Message($"[Maneuver] 意图活重解 G{group.GroupId}｜{pm.intent?.operationName}" +
                                    $"｜|Δv| {dvBefore:F2} → {dvAfter:F2} m/s｜tick={pm.executeTick}");
                    }
                    else
                    {
                        pm.intentResolved = true;
                        group.plannedManeuvers[0] = pm;
                        Log.Message($"[Maneuver] 意图重解不可用，沿用冻结向量 G{group.GroupId}｜{resolveWhy}");
                    }
                }

                // ── 预对准（前瞻）：──
                // ★★ **本条机动已经临门 ⇒ 不许再给"下一条"预对准**（2026-09-21 游戏内实测的真 bug）：
                //   原逻辑只做"队列里有下一条就转向它"，而下一条与本条可能差几十度
                //   （实测：近交点平面匹配 Δv=6.1 与它的下一条差 2.9°）⇒ 船被转到**下一条**的方向、
                //   停进 `AlignMainAxis` 的 1.5° 死区，而**点火门要 2.0°** ⇒ 门永远不成立 ⇒
                //   **船卡在"等对准"里直到时间走完**（实测 `节点 T+19s` → 计数器一路负到 `T--6104s`，
                //   1.7 小时零动作；且日志自相矛盾：`预对准·诊断 夹角=0.0° |ω|=0` 而 `主轴还差 2.9°`）✗✗。
                //   ⇒ 本条进入**预点火窗口**后，姿态让给**本条自己的方向**（见下方门前的 `burnDirection` 设置）。
                //   窗口外仍给下一条预对准（保住"排队就开始转"的原有收益 ✓）。
                if (group.abNoPreAlign) continue;   // 对照开关：不做预对准
                bool preAlignCurrent = physicsTick >= pm.executeTick - ManeuverPreBurnLookaheadTicks;
                // ★★ **预对准必须瞄“节点时刻”的方向**（2026-09-21，按 MJ 的“同一个 `_worldDirection`”）——
                //   执行器的靶 = 意图轨道在 **UT** 的速度 − 活轨道在 **UT** 的速度
                //   （点火前两项都固定 ⇒ 方向固定 ✓）。
                //   而本处原来把 R/T/N 分量用 `GetWorldDeltaV(localPos, localVel, …)`在
                //   **当前时刻**重新表达 ✗✗ ⇒ 两者相差 **`ω·(UT − now)`**：
                //   预对准从“排队就开始转”（可能早一整圈）⇒ 节点在 800 s 外时差 **~49°**、
                //   更早时差到几十上百度 ⇒ **正是日志里 74° / 93° 的带角度点火** ✗✗。
                //   （同一个“锚点必须是节点时刻”的教训，与 `SetBurnSolution` 那一条同族 ✓）
                DVector3 preAlignDv;
                if (pm.plannedWorldDv.sqrMagnitude > 0.0)
                {
                    preAlignDv = pm.plannedWorldDv;   // 已是惯性/world 向量 ⇒ 无需再分解
                }
                else if (celestialMgr != null && OrbitAdapter.TryToOrbitView(group, celestialMgr, pm.executeTick, out OrbitView ovPre))
                {
                    // 在**节点时刻**的位姿上分解（与 `OrbitAdapter.TryQueuePlan` 同口径 ✓）
                    (DVector3 rPre, DVector3 vPre) = OrbitAdapter.StateAtUT(ovPre, pm.executeTick);
                    preAlignDv = OrbitMath.GetWorldDeltaV(rPre, vPre, pm.radialDV, pm.tangentialDV, pm.normalDV);
                }
                else
                {
                    preAlignDv = OrbitMath.GetWorldDeltaV(group.localPosition, group.localVelocity, pm.radialDV, pm.tangentialDV, pm.normalDV);
                }
                bool hadPreAlign = group.pendingAlignDir.sqrMagnitude > 1e-12;
                if (preAlignCurrent)
                {
                    // ★★ **本条临门 ⇒ `pendingAlignDir` 不许继续指向"下一条"**（2026-09-21 实测的真 bug）。
                    //   ⚠ 必须在这里**主动设成本条的方向**，不能指望后面（门之后）那个块来覆盖：
                    //   `pendingAlignDir` 在本轮迭代的**预对准**处被写成了**下一条**机动（队列迭代顺序 =
                    //   本条在前、下一条在后 ⇒ 最后写进去的是下一条）✗；而**点火门过不去就 `continue`**
                    //   ⇒ 门后那个块根本执行不到 ⇒ 陈旧目标留了下来 ⇒ 船被转向**下一条**机动 ✗✗。
                    //   实测（`Player.log` 2026-09-21 12:23）：`等对准：主推轴还差 143.0°` +
                    //   `带角度点火：残留 79.5°` + `started gradual burn 夹角=79.5°`。
                    //   ⇒ 这里直接给出**同一个方向**（门的判据 = 姿态目标），门、姿态、`StartBurn` 三者一致 ✓。
                    DVector3 pmDirNow = pm.plannedWorldDv.sqrMagnitude > 0.0
                        ? pm.plannedWorldDv
                        : OrbitMath.GetWorldDeltaV(group.localPosition, group.localVelocity,
                            pm.radialDV, pm.tangentialDV, pm.normalDV);
                    if (pmDirNow.sqrMagnitude > 1e-12)
                    {
                        group.burnDirection = pmDirNow.normalized;
                        group.pendingAlignDir = group.burnDirection;
                    }
                }
                else
                {
                    group.pendingAlignDir = preAlignDv.sqrMagnitude > 1e-9 ? preAlignDv.normalized : DVector3.zero;
                    if (!hadPreAlign && group.pendingAlignDir.sqrMagnitude > 1e-12)
                        LogPreAlignStarted(group, pm.executeTick, physicsTick);
                }

                // ── 预对准诊断（2026-09-19）：177.4° 起点、窗口 58s、5°/s（只需 35s），
                //    点火时却仍 107.4° ⇒ 真因未知。这里按周期打出"夹角 + 角速率 + 本步是否在转"，
                //    让下一次运行直接给出"是没转 / 转错方向 / 目标漂了"三选一。
                if (group.pendingAlignDir.sqrMagnitude > 1e-12 && physicsTick >= PreAlignDiagNextTick)
                {
                    PreAlignDiagNextTick = physicsTick + 10.0;
                    Log.Message(string.Format(
                        "[预对准·诊断] G{0} 夹角={1:F1}° |ω|={2:F3}°/s（上限 {3:F1}）节点 T-{4:F0}s",
                        group.GroupId, MainAxisBurnAngleDeg(group, group.pendingAlignDir),
                        group.angularVelocity.magnitude * Mathf.Rad2Deg, group.MaxAngularRate * Mathf.Rad2Deg,
                        pm.executeTick - physicsTick));
                }

                // 预点火窗口：进入节点前 ManeuverPreBurnLookaheadTicks 才开始处理本条机动（可行性/点火）
                // ★ B5：判据本体在 `BurnExecutorPolicy.InPreBurnWindow`（窗口值仍由这里给，不许复制数值）
                if (!BurnExecutorPolicy.InPreBurnWindow(physicsTick, pm.executeTick, ManeuverPreBurnLookaheadTicks))
                    continue;

                // ── 机动可行性校验（按本条机动选用的工质网络），区分失败原因 ──
                // 提前到点火判断之前：既用于拒绝不可行机动，也用于计算燃烧时长（燃烧居中需要）
                float dvMag = (float)Math.Sqrt(
                    pm.radialDV * pm.radialDV + pm.tangentialDV * pm.tangentialDV + pm.normalDV * pm.normalDV);
                var maneuverSrc = GetGroupPropulsionSource(group);
                double mDry = GetGroupMass(group, null); // 干质量（不含工质）
                var mEst = BurnCalculator.Estimate(dvMag, (float)mDry, maneuverSrc,
                    maneuverSrc != null ? maneuverSrc.Networks : null, pm.useNetworks);
                if (!mEst.Feasible)
                {
                    string reason;
                    if (mEst.Networks == null || mEst.Networks.Count == 0)
                    {
                        reason = "未安装引擎或未启用任何工质网络（无法执行机动）";
                        Log.Message(string.Format("[Maneuver] BLOCKED (no thruster): G{0}", group.GroupId));
                    }
                    else if (mEst.RequiredPropellant <= 0f)
                    {
                        reason = "机动 ΔV 为 0 或无法分配";
                        Log.Message(string.Format("[Maneuver] BLOCKED (zero dv): G{0}", group.GroupId));
                    }
                    else
                    {
                        // 找到第一个不可行的网络，给出明细
                        var badNet = mEst.Networks.Find(ne => !ne.Feasible);
                        if (badNet.Propellant != null)
                            reason = string.Format("工质不足：网络「{0}」需 {1:F0} kg，当前 {2:F0} kg（最大可达 ΔV {3:F0} m/s）",
                                badNet.Propellant.label, badNet.RequiredPropellant, badNet.Available, badNet.MaxDv);
                        else
                            reason = string.Format("工质不足（总需 {0:F0} kg，最大可达 ΔV {1:F0} m/s）",
                                mEst.RequiredPropellant, mEst.MaxDeltaV);
                        Log.Message(string.Format("[Maneuver] BLOCKED (insufficient propellant): G{0} need={1:F1}kg maxDV={2:F1}m/s",
                            group.GroupId, mEst.RequiredPropellant, mEst.MaxDeltaV));
                    }
                    Messages.Message(string.Format("Group {0} 机动被拒绝：{1}", group.GroupId, reason), MessageTypeDefOf.RejectInput);
                    group.PopExecutedManeuver();
                    continue;
                }

                // （预对准已提到上面"队列里有下一条机动就转"——此处不再重复设置。）

                // ── 燃烧居中：点火时刻 = 节点时刻 − 半程燃烧时长，让冲量模型的"瞬间"落在燃烧中点 ──
                // 与 MechJeb 一致（ignitionUT = node.UT − halfBurnTime），抵消有限时长燃烧的时序误差。
                // ⚠ B2.2 更正（2026-09-19，用户澄清"轨道保持应当且必要"）：**居中必须用点火那一刻的活质量估**。
                // 计划里的 `burnSecondsPlanned` 是**排队时**算的，而轨道保持一直在扣工质 ⇒ 它会过期 ✗
                // （过期 ⇒ "半程"对不上 ⇒ 居中两端不对称 ⇒ 一阶残差；这正是"游戏行为像单侧燃烧"的来路）。
                // ⇒ 分工：**计划的值只用于排队时的可行性显示**（够不够转姿态）；
                //          **真正居中用活值 `mEst.BurnSeconds`**（质量是活的，它是权威）。
                double burnSecForCenter = mEst.BurnSeconds;

                // ⚠⚠ **回写（三次翻案，引以为戒）**：这里来过三回，最后查明是**仪器**的问题。
                //   ① 首段"4/5 步" ⇒ 断言"交付晚一步" ⇒ 把居中提前一步 ⇒ 因 e 变差撤回（判据=噪声 ✗）
                //   ② 见"模型 |v| 与实测恒差 14.5 m/s ≈ 一步的 Δv" ⇒ 又断言"交付晚一步"，并把点火提前一步
                //      （本版的前身）—— 结果**恒差纹丝不动** ✗✗ ⇒ 该恒差**不是交付差**。
                //   ③ 真因（本轮查明）：**诊断在"本步 Δv 累加之前"采样**，而模型按整个 elapsed 积分
                //      ⇒ 模型恒多一步的 Δv ⇒ 恒差 = 一步的量 ✓（模型移动窗口不改变已交付量 ⇒ 与相位无关 ✓，
                //      这正是 ② 里"移动点火不影响它"的原因 ✗）。
                //   ⇒ **两次"量纲证据"都是仪器造的** ✗ ⇒ 已按同一口径修正（见 `burnArcSeconds` 处的模型调用）。
                //   ⇒ 本版的"点火提前一步"**保留**：它让**实际交付窗口**正好落在 `节点 ± t_b/2`（之前晚 2 s），
                //     这是独立于上面那场误会的一个真实改进 ✓；撤掉的只是它曾经的（错误）理由。
                //   **教训（最终版）：先证明仪器的口径，再用它判任何事。**
                //
                // ★ **燃烧弧补偿（第一半，B2.2）**：既然前向模型 ≡ 执行器（实测全程 |v| 差 0.25%），
                //   就用它**扫点火相位**取预测 e 最小的那一档。依据：台架实测相位 ±2 s 让 e 摆动 **5.5 倍**
                //   （2.67e-4 ~ 1.47e-3）⇒ 相位是 e 的主项，挑对了就是数倍收益（不是把某个偏移写死：
                //   最优相位随几何/政策变，必须每轮算）。
                double bestE = double.MaxValue;
                double nominalE = double.NaN;
                // ★ 对照实验：**强制相位**（跳过自动扫描，直接采用给定偏移），并打印该偏移的模型预测
                if (!double.IsNaN(group.abForcePhaseShift) && !group.burnPhaseSwept)
                {
                    group.burnPhaseShift = group.abForcePhaseShift;
                    group.burnPhaseSwept = true;
                    if (mEst.BurnSeconds > 0f && pm.plannedWorldDv.sqrMagnitude > 0.0
                        && OrbitAdapter.TryToOrbitView(group, celestialMgr, physicsTick, out OrbitView ovF)
                        && BurnArc.TryPredict(ovF, pm.executeTick, OrbitView.GameWorldToAdapter(OrbitView.ToV3(pm.plannedWorldDv)),
                               mEst.BurnSeconds, group.burnPhaseShift + BurnArc.ExecutionDelaySeconds,
                               out BurnArc.Result fRes, out _))
                        Log.Message(string.Format("[对照·相位] G{0} 强制相位 {1:+0.0;-0.0;0.0}s｜模型预测 e={2:E3}",
                            group.GroupId, group.burnPhaseShift, fRes.eccentricity));
                }
                // ⚠ **本轮（"MJ 优先"第 5 件）起，自动扫描只服务退路律**：它最小化的是**冻结模型**的
                //   预测 e，而活差额律按构造就把那个 e 归零 ⇒ 再扫就是"用旧模型优化新执行器"
                //   （两份权威 ✓ J2）。判据与点火块记 `burnTargetV` 的条件**同一句**
                //   （`abFreezeDirection` 是对照用的人为冻结 ⇒ 那时走退路律，扫描照旧有意义 ✓）。
                bool liveDeficitLaw = pm.owner != PlanOwner.Executor && !group.abFreezeDirection;
                if (!liveDeficitLaw && !group.burnPhaseSwept && mEst.BurnSeconds > 0f && pm.plannedWorldDv.sqrMagnitude > 0.0
                    && OrbitAdapter.TryToOrbitView(group, celestialMgr, physicsTick, out OrbitView ovSweep))
                {
                    var shiftSb = new System.Text.StringBuilder();
                    for (double sh = -6.0; sh <= 6.0 + 1e-9; sh += 2.0)
                    {
                        // ⚠ 扫的是**执行器真正交付的窗口** ⇒ 模型要带上执行延迟（见 `ExecutionDelaySeconds`）
                        if (!BurnArc.TryPredict(ovSweep, pm.executeTick, OrbitView.GameWorldToAdapter(OrbitView.ToV3(pm.plannedWorldDv)),
                                mEst.BurnSeconds, sh + BurnArc.ExecutionDelaySeconds, out BurnArc.Result sw, out _))
                            continue;
                        double e = sw.eccentricity;
                        if (Math.Abs(sh) < 1e-9) nominalE = e;
                        shiftSb.Append(sh.ToString("+0;-0;0")).Append("s→").Append(e.ToString("E2")).Append(' ');
                        if (e < bestE) { bestE = e; group.burnPhaseShift = sh; }
                    }
                    group.burnPhaseSwept = true;   // 每机动只扫一次
                    Log.Message(string.Format("[燃烧·弧补偿] G{0} 相位扫描：{1}⇒ 选 {2:+0;-0;0}s（预测 e={3:E2}，居中档 {4:E2}）",
                        group.GroupId, shiftSb, group.burnPhaseShift, bestE, nominalE));
                }
                // ⚠ 偏移取自**群上**（跨步保留）；第一版用局部变量 ⇒ 每步归零、扫描白扫 ✗（见字段注释）
                // − 一步 = 补掉"首次交付比点火晚一步"（见上）；+ 扫描出的相位（若选中）。
                // ★ B5（2026-09-23）：这条算式的**唯一权威**已收到 `BurnExecutorPolicy.CenteredIgnitionTick`
                //   （台架/纯测试同一份）——本处只调用，逐位同算式。
                double ignitionTick = BurnExecutorPolicy.CenteredIgnitionTick(
                    pm.executeTick, burnSecForCenter, group.burnPhaseShift, BurnArc.ExecutionDelaySeconds);
                if (physicsTick < ignitionTick)
                    continue;
                // ★★ **对准才点火**（上游纪律，2026-09-21 补）——
                //   上游 `MechJebModuleNodeExecutor.DetermineState()`：
                //     `if (time >= _ignitionUT && Aligned()) return States.BURN;`（`AlignedToleranceDegrees = 1`）
                //   即 **到了点还要对准才点**。我们原来是“**到点就点、只打警告**” ✗✗
                //   ⇒ 实测（高推·异面异轨，`83EE2D20`）：**34 次带角度点火**，
                //   角度 42.7° / 91° / 111° / 123°（后三者根本进不了 10° 主推锥）
                //   ⇒ 净冲量方向不是指令方向 ⇒ 把刚收到的接近打散
                //   ⇒ 距离卡在 9.0–9.7 km 空转 **61 次拦截**（总 Δv 3691 m/s vs 估计 642）。
                //   ⚠ 死锁保护：万一姿态权限不够（转不到位），等超过
                //     （该超时已删：上游就是没对准一直等 ✓）。
                // ★★ **按上游：没对准就一直等（无超时）**（2026-09-21，用户观察到“方向疯狂翻转”）：
                //   上游 `DetermineState()`：`if (time >= _ignitionUT && Aligned()) return BURN;`
                //   —— **没对准就等，宁可烧晚也不打偏** ✓；我曾给它加了 25 s 超时
                //   （“防死锁”）⇒ 超时后“坐着点”就是**打偏** ✗✗：
                //   实测证据（同一轮）：计划的方向**每 1~2 发翻一次号**（R/T/N 从 +16.6/-92.4 ⇒ -11.6/+47.1 ⇒ … ⇒
                //   +136.7/-0.2 纯径向）⇒ 调头 180° 需 36 s（5°/s）而节点只在 15 s 后 ⇒ 点火残留 92.4°/66.5° ✗
                //   ⇒ 127 发、7497 m/s、`Intercept` 129 次 ✗✗。
                //   ⇒ **等**（不设超时）；晚烧的时序误差由下一次重规划吸收 ✓。
                //   （防死锁靠另一条：若船根本没有姿态权限，下面的可行性与超时仍会告警）。
                // ★★ **门拒一次 ⇒ 重算一次方向**（2026-09-21 实测）：门比的是 `group.burnDirection`，
                //   而它是**某一拍取下的快照**；活轨道一直在动 ⇒ 同一个快照与"节点处的真方向"
                //   会缓慢偏离。船预对准到 `AlignMainAxis` 的**死区**（`DefaultThrustAlignCos`≈1.5°）就停转，
                //   而门要 2.0° ⇒ **只剩 0.8° 裕度**，方向一漂过去就**永远点不着** ✗
                //   （实测 `带角度点火：残留 79.5°` / `等对准：主推轴还差 143.0°` 之后卡死）。
                //   ⇒ 拒绝的**同时**把方向刷成"此刻算出来的"（与预对准同一个算式），下一拍再比。
                //   这把"等对准"从"等一个死数"变成"追一个活数"，与 MJ 每帧重取 `_worldDirection` 同构。
                // ⚠ **门必须严格宽于死区**（`IgnitionAlignGateDeg` > 死区），否则船收心后仍在门外 ⇒ 死锁。
                //   这句在 `OnStartup` 里用 `Debug.Assert` 钉住（见 `AssertAlignGateWiderThanDeadzone`）。
                if (MainAxisBurnAngleDeg(group, group.burnDirection) > IgnitionAlignGateDeg)
                {
                    DVector3 pmDirGate = pm.plannedWorldDv.sqrMagnitude > 0.0
                        ? pm.plannedWorldDv
                        : OrbitMath.GetWorldDeltaV(group.localPosition, group.localVelocity,
                            pm.radialDV, pm.tangentialDV, pm.normalDV);
                    if (pmDirGate.sqrMagnitude > 1e-12)
                    {
                        group.burnDirection = pmDirGate.normalized;
                        group.pendingAlignDir = group.burnDirection;
                    }
                    if (group.burnIgnitionAlignWaitTick < 0.0)
                    {
                        group.burnIgnitionAlignWaitTick = physicsTick;
                        Log.Message(string.Format(
                            "[Maneuver] G{0} 等对准：主推轴还差 {1:F1}°（上游行为：对准才点火，不打偏）",
                            group.GroupId, MainAxisBurnAngleDeg(group, group.burnDirection)));
                    }
                    continue;   // 等到对准为止
                }
                // ★★ **点火门与船的姿态必须瞄同一个方向**（2026-09-21 游戏内实测的真 bug）——
                //   门比的是 `group.burnDirection`，而它要到 `StartBurn`（**门的后面**）才被设 ⇒
                //   门前那一刻它还是**上一次燃烧**留下的旧方向 ✗。船则被 `pendingAlignDir` 转到别处
                //   ⇒ 实测 `预对准·诊断 夹角=0.0°、|ω|=0` 与 `主轴还差 2.9°` 自相矛盾、船卡死 1.7 小时。
                //   ⚠ **修点在门的【前面】**（见上方 `preAlignCurrent` 那个分支）：那里同时设
                //   `burnDirection` 与 `pendingAlignDir`。**不能放在这里** —— 门过不去会 `continue`，
                //   放这里的块永远执行不到（我第一版就是这么写的，实测 `带角度点火：残留 79.5°` ✗✗）。

                bool hadOrbitKeeping = group.HasOrbitKeeping;
                HashSet<int> savedWhitelist = group.orbitKeepingWhitelist != null
                    ? new HashSet<int>(group.orbitKeepingWhitelist) : null;

                if (group.IsCircularOrbit)
                    group.ExitCircularOrbit();

                if (hadOrbitKeeping)
                {
                    group.HasOrbitKeeping = true;
                    group.orbitKeepingWhitelist = savedWhitelist;
                }

                DVector3 relPos2 = group.localPosition;
                DVector3 relVel2 = group.localVelocity;

                Log.Message(string.Format("[Maneuver] Group {0} localFrame: |pos|={1:F0}km |vel|={2:F1} m/s bodyId={3}",
                    group.GroupId, relPos2.magnitude / 1000.0, relVel2.magnitude, group.CelestialBodyId));

                // 优先用规划时刻的世界系 dV（惯性方向，避免点火时刻换算的帧旋转误差）；
                // 旧存档/手动机动未设置时退回点火时刻分量换算
                DVector3 dV = pm.plannedWorldDv.sqrMagnitude > 0.0
                    ? pm.plannedWorldDv
                    : OrbitMath.GetWorldDeltaV(relPos2, relVel2, pm.radialDV, pm.tangentialDV, pm.normalDV);

                double safeRadius = GetBodySafeRadius(group.CelestialBodyId);
                double mu = GetBodyGM(group.CelestialBodyId);
                DVector3 relVelSafe = group.localVelocity + dV;
                bool safe = OrbitMath.IsOrbitSafe(group.localPosition, relVelSafe, safeRadius, mu);
                if (!safe)
                {
                    double sma = OrbitMath.SemiMajorAxis(group.localPosition, relVelSafe);
                    OrbitMath.GetEccentricity(group.localPosition, relVelSafe, out _, out double ecc);
                    double pe = sma > 0 ? sma * (1.0 - ecc) : -1.0;
                    Log.Message(string.Format("[Maneuver] BLOCKED: G{0} sma={1:F0} e={2:F4} pe={3:F0}km safeR={4:F0}km |pos|={5:F0} |v|={6:F0} |dv|={7:F0}",
                        group.GroupId, sma, ecc, pe / 1000.0, safeRadius / 1000.0,
                        group.localPosition.magnitude / 1000.0,
                        relVelSafe.magnitude, dV.magnitude));
                    Messages.Message(string.Format("Group {0} 机动已跳过（将撞击行星）", group.GroupId), MessageTypeDefOf.RejectInput);
                    group.PopExecutedManeuver();
                    continue;
                }

                // 本条机动的推进配置：burn 只烧选用的工质网络（null = 全启用）
                group.burnEnabledNetworks = pm.useNetworks;
                group.StartBurn(dV);
                // ★ 命令恒定加速度 = **计划的 Δv / 计划的时长**（同一条 `BurnCore` 公式 ⇒
                //   估算/居中/模型/执行器**按构造同值**）。见执行器那段注释：绝不用"首个燃烧步"锁存。
                if (group.abOneShotImpulse) group.abNodeTick = pm.executeTick;   // 对照 F：记住节点
                group.burnCommandAccel = burnSecForCenter > 0.0
                    ? dV.magnitude / burnSecForCenter
                    : 0.0;
                // ⚠ 用 `dV` 而**不是** `pm.plannedWorldDv`：手动机动（`QueueManeuver`，含 B2.2-b 的微调
                //   与手动排的条目）**不带** world 向量，只有分量 ⇒ 用 plannedWorldDv 会读出 0
                //   （命令加速度恒 0 ⇒ 悄悄退回全油门，与恒定加速度政策不一致）。`dV` 就是执行器
                //   真正拿在手里的那一份（见上面那个三元式）⇒ 它才是这里的唯一权威。
                // ★ 方向锚点用**计划自带的节点帧分量**（不是在这里重新分解 —— 点火帧与节点相差
                //   ω·49s ≈ 2.44°，实测「模型方向 vs 执行方向」恒 2.23–2.36° 就是它；见 `SetBurnSolution`）
                group.SetBurnSolution(pm.radialDV, pm.tangentialDV, pm.normalDV);
                // ★ 执行器**实际所用的分量**（= 计划里那份，可能是活重解后的版本）：与验收里打印的
                //   "排队时那份"并排，就能判那 −0.23° 滚转是不是出自"两版计划分量不同"。
                Log.Message(string.Format(
                    "[燃烧·诊断] G{0} 执行器所用分量（重解后版本）：R/T/N = {1:F2} / {2:F2} / {3:F2}｜|Δv|={4:F2}",
                    group.GroupId, pm.radialDV, pm.tangentialDV, pm.normalDV, dV.magnitude));
                // 精确交会：出发段点火执行 → 激活闭环引导
                if (pm.armRendezvousGuidance)
                    group.ArmRendezvousGuidance();
                // 推进器喷焰表现：burn 期间地图内推进器标记为燃烧状态
                group.GetLinkedMapComp()?.SetThrustersBurning(true);
                group.PopExecutedManeuver();
                // 标记追踪表：机动已开始推进
                if (trackedManeuvers.ContainsKey(group.GroupId))
                    trackedManeuvers[group.GroupId] = new ManeuverTracker { ExecutePhysicsTick = physicsTick, IsActive = true };

                int remaining = group.plannedManeuvers.Count;
                // 点火时的对准残留角：0° 附近 = 预对准生效（主网从一开始就在锥内出力、不烧 RCS 起步）；
                // 明显大于 0 = 预对准时间不够（机动排得太近），起步段会由 RCS 承担。
                // 燃烧时长也打出来：规划器是**脉冲模型**，燃烧时长跨越大半个轨道时（>300s）实际轨迹
                // 会明显偏离计划 → 闭环引导要追大修正量（2026-09-16 实测 27.6 分钟注入即此类）。
                string longBurn = mEst.BurnSeconds > 300f
                    ? string.Format("⚠ 燃烧 {0:F0}s 已属长燃烧（脉冲模型误差大，建议提高推力或分段）", mEst.BurnSeconds)
                    : "";
                // 真正的点火点：记录起点 tick + 报居中偏移（这才是"是否居中"的读数；
                // ⚠ 上一版把它放在**预点火窗口**里（门槛之前）⇒ 每 tick 都打、且报的是"窗口开了多久" ✗）
                group.burnActualStartTick = physicsTick;
                // ★ 打出**物理量本身**（同一步）：`fTotal` 与 `groupMass`。累加器说"施加了 100% 命令"，
                //   而不变量（直接测量、无模型）说只交付了 98.9% ⇒ 亏空在"每步 Δv = (fTotal/mass)·dt"里
                //   ⇒ 这两个数一比就能定位（不是模型、不是转换：它们就是执行器实际用的两个输入）。
                // ⚠ **这两行是"全部候选网络"合计，不是执行器实际所用**：执行器按"对准了的主网 / 未对准才用
                //   RCS"逐**步**选，这里没有那个信息 ⇒ 只能给上界。**真实 ve 看燃烧结束行的 `veBurn`**
                //   （2026-09-19：这里打出过 merged ve=3687.5，让"白送 58 m/s"的假警报活了半天）。
                {
                    var dbgSrc = GetGroupPropulsionSource(group);
                    double dbgF = 0.0, dbgMf = 0.0;
                    if (dbgSrc != null)
                        foreach (var nn in dbgSrc.Networks)
                        {
                            if (nn == null || nn.MassFlow <= 0f) continue;
                            if (group.burnEnabledNetworks != null && !group.burnEnabledNetworks.Contains(nn.propellant)) continue;
                            if (nn.AvailableMass(dbgSrc) <= 0f) continue;
                            dbgF += nn.Thrust; dbgMf += nn.MassFlow;
                        }
                    double dbgMass = GetGroupMass(group, dbgSrc!);   // 允许 null（= 干质量口径），与既有调用一致
                    Log.Message(string.Format(
                        "[燃烧·诊断] G{0} 点火物理量（**全部候选网络合计**，非执行器所用）：ΣF={1:F1} N｜Σṁ={2:F4} kg/s｜mass={3:F1} kg｜a(上界)={4:F4} m/s²｜ve(合并上界)={5:F1} m/s｜计划 Δv={6:F2}｜**命令加速度 a_cmd={7:F4} m/s²（恒定）⇒ 预计时长 {8:F1}s**",
                        group.GroupId, dbgF, dbgMf, dbgMass, dbgMass > 0 ? dbgF / dbgMass : 0.0,
                        dbgMf > 0 ? dbgF / dbgMf : 0.0, dV.magnitude,
                        mEst.BurnSeconds > 0f ? dV.magnitude / mEst.BurnSeconds : 0.0, mEst.BurnSeconds));
                }

                BurnDiagNextTick = physicsTick + 10.0;
                // 暂存"燃烧弧预测"的输入（点火时确定的解 + 节点 + 时长）⇒ 燃烧期可做"预测 vs 实测"
                // ⚠ 2026-09-19 撤回一次（引以为戒）：我曾在这里用"**节点时刻**的状态"重新分解 RTN 分量，
                //   假设"计划属于节点那一刻的系"。**实测让它从 6.058e-3 变差到 7.921e-3** ✗ ⇒ 假设错。
                //   ⇒ 保留 `StartBurn` 原有的分解（点火时刻），别再动这里。

                // ⚠ 用 `dV` 而不是 `pm.plannedWorldDv`：手动机动（含 B2.2-b 的微调条目）只带分量、
                //   world 向量是 0 ⇒ 用 plannedWorldDv 会让模型/目标全部读到零向量（静默失效）。
                group.burnArcDv = OrbitView.GameWorldToAdapter(OrbitView.ToV3(dV));
                group.burnArcNodeUt = pm.executeTick;
                group.burnArcSeconds = burnSecForCenter;
                // 种子：点火那一刻的状态 ⇒ 第一次诊断（+10s）就能量出"这 10 秒真正施加了多少 Δv"
                // （不种的话，第一次会拿上一段燃烧的残留状态做差 ⇒ 得到一个假的巨额冲量 ✗）
                if (OrbitAdapter.TryToOrbitView(group, celestialMgr, physicsTick, out OrbitView ovSeed))
                {
                    (group.burnDiagR, group.burnDiagV) = ovSeed.RightHandedStateVectorsAtUT(physicsTick);
                    // ★ 同一次拿到的这条轨道就是**点火前**的（本步冲量还没施加）⇒ 存下来给前向模型当基准
                    //   ⚠ **微调条目不覆盖它**：`burnPreBurnOrbit` 的语义是"**计划那一条**的燃烧前轨道"，
                    //     验收的「两个燃烧前参考」交叉校验用的就是它 —— 被微调覆盖后那行会读出 6.3 km/807.9 m/s
                    //     的假偏差（2026-09-20 实测 ✗，又是一次"新机制污染既有仪器"）。
                    if (pm.owner != PlanOwner.Executor)
                    {
                        group.burnPreBurnOrbit = ovSeed;
                        group.burnHasPreBurnOrbit = true;
                    }

                    // ★ B2.2-b：记下**意图轨道** = 计划在节点处的**脉冲**结果 `(a, |h|)` —— 闭环微调的目标。
                    //   口径：点火前轨道推进到节点 + 计划 Δv 一次施加（**不是**模型预测的有限燃烧结果：
                    //   意图 = "计划想做的那件事"，模型猜的是"我会做成什么"，两者不是一回事）。
                    //   ⚠ 两个向量都必须先换到**同一个系**：`burnPreBurnOrbit`/`StateAtUT` 给的是游戏系，
                    //     而 `burnArcDv` 是适配层系（缓存时换过）⇒ 不换系相加 = 又一次混系事故。
                    //   ⚠ **微调条目本身不再记目标**（否则微调的燃烧结束时又会判定"还有残差" ⇒ 蚊子机动无限套娃）。
                    //     微调是执行器的收口动作，不是一次新意图。
                    // ★★ **MJ 的靶**（"暂时全盘照抄 MJ"，2026-09-20 用户裁定）：**节点处的目标速度**。
                    //   本体：`MechJebModuleNodeExecutor.NextDirection` 里 `node.GetBurnVector(Orbit)` ——
                    //   `Orbit` 是**活的**、靶是**固定速度向量** ⇒ 逐拍 `dv_rem = v_target(UT) − v_live_prop(UT)`。
                    //   ⚠ 我们一度把靶换成"意图轨道 (a,h向量) + 出范围政策"（台架 e 好两个数量级），
                    //     但**真实几何上出范围支是自证式的**：游戏实测 430.01 m/s 只交付 164.9 ✗
                    //     ⇒ 换回 MJ 的形式（无几何分支、良态）；精度交给收口微调（`trimTarget*`）✓。
                    if (group.burnArcDv.sqrMagnitude > 0.0 && pm.owner != PlanOwner.Executor)
                    {
                        // ★★ **靶的参考轨道：优先用计划自带的**（2026-09-21，两冲量转移的第二发）。
                        //   上游的节点自带宽义"Δv 是在哪条轨道上算的"（`node.patch` / `nextPatch`），
                        //   我们的 `PlannedManeuver.resolvedRefOrbit` 就是它的对应物。
                        //   对**第一发**它与 `burnPreBurnOrbit` 等价（都是点火前那条 ⇒ 行为逐位不变）；
                        //   对**第二发**（到达刹车）它必须是**转移轨道**，否则靶会算成
                        //   "从点火前轨道推到到达时刻再加 Δv"，方向与幅值双双是错的 ✗。
                        (DVector3 trimRN, DVector3 trimVN) = pm.hasResolvedRefOrbit
                            ? OrbitAdapter.StateAtUT(pm.resolvedRefOrbit, group.burnArcNodeUt)
                            : OrbitAdapter.StateAtUT(group.burnPreBurnOrbit, group.burnArcNodeUt);
                        DVector3 trimVAfter = trimVN + OrbitAdapter.ToD(OrbitView.AdapterToGameWorld(group.burnArcDv));
                        DVector3 hVec = DVector3.Cross(trimRN, trimVAfter);
                        group.trimTargetA = OrbitMath.SemiMajorAxis(trimRN, trimVAfter, ovSeed.mu);
                        group.trimTargetH = hVec.magnitude;
                        group.trimTargetHVec = hVec;
                        group.trimHasTarget = group.trimTargetA > 0.0 && group.trimTargetA < 1e12
                            && group.trimTargetH > 0.0;
                        // 执行器的靶（MJ 口径）：同一份意图轨道的**速度**在节点处 ⇒ 不需要 (a,h) 那套几何
                        group.burnTargetV = trimVAfter;
                        group.burnHasTargetV = true;
                    }
                }
                group.burnDiagTick = physicsTick;
                group.burnDiagRemaining = group.remainingBurnDV;
                Log.Message(string.Format(
                    "[燃烧·诊断] G{0} 点火 tick={1:F0} 节点={2} 偏移={3:F0}s（居中应为 −{4:F0}s）｜燃烧时长={5:F0}s",
                    group.GroupId, physicsTick, pm.executeTick, physicsTick - pm.executeTick,
                    burnSecForCenter / 2.0, burnSecForCenter));

                // 点火残留角明显非零 ⇒ 明说"对准时间不够"（而不是只给一个裸角度让人猜）
                double ignAngle = MainAxisBurnAngleDeg(group, group.burnDirection);
                if (ignAngle > 5.0)
                {
                    double usable = AlignFeasibility.UsableAlignSeconds(ManeuverPreBurnLookaheadTicks, mEst.BurnSeconds);
                    Log.Warning(string.Format(
                        "[Maneuver] G{0} 带角度点火：残留 {1:F1}°（可用对准 {2:F0}s、按动量轮 {3:F1}°/s 最多转 {4:F0}°）" +
                        "—— 机动排得太近，或被“燃烧居中”吃掉了时间。",
                        group.GroupId, ignAngle, usable, group.MaxAngularRate * Mathf.Rad2Deg,
                        usable * group.MaxAngularRate * Mathf.Rad2Deg));
                }
                Log.Message(string.Format("[Maneuver] Group {0} started gradual burn! dv={1:F1} m/s remaining={2} 主推轴与燃烧方向夹角={3:F1}° 预计燃烧={4:F0}s {5}",
                    group.GroupId, dV.magnitude, remaining, MainAxisBurnAngleDeg(group, group.burnDirection),
                    mEst.BurnSeconds, longBurn));
            }

            // ── 交会/接近引导（精确交会：固定到达时刻；近距离接近：动态到达时刻）──
            // ★ 分段计时（2026-09-20）：实测尖峰 19.2 ms（均值 0.19 ms 的约 100 倍）来源未知
            //   ⇒ 先把"引导整段"圈起来：**尖峰在引导内**（阶梯 / 终端 CW / 律）还是**在引导外**（物理/GC）。
            //   与 `[步耗时]` 同一把尺 ⇒ 两行一对照就能二分。
            {
                var guidSw = System.Diagnostics.Stopwatch.StartNew();
                UpdateRendezvousGuidance(physicsTick);
                guidSw.Stop();
                if (Prefs.DevMode && guidSw.Elapsed.TotalMilliseconds > 2.0)
                    Log.Message(string.Format("[交会引导·耗时] 整段 {0:F1} ms（t={1:F0}）",
                        guidSw.Elapsed.TotalMilliseconds, physicsTick));
            }
        }

        // 引导恢复阶梯的节流表（重规划要跑毫秒级扫描，别每步都来）
        private readonly Dictionary<int, double> guidanceRecoverNextTick = new Dictionary<int, double>();

        /// <summary>
        /// S1 重规划（时间当自由变量）：从当前状态重新找一次"等待 + 转移"的计划，
        /// 排队成新的出发段机动并重挂引导（新到达时刻）。找不到就返回 false，由调用方降级。
        /// 与 MechJeb 同思路：计划被有限燃烧打偏时，**重算并等下一个窗口**，而不是用越来越大的修正去追。
        /// </summary>
        private bool TryRecoverRendezvousPlan(SpaceEntityGroup group, SpaceEntityGroup target, DVector3 aimOffset,
            double gm, double physicsTick, DVector3 r1Rel, DVector3 v1Rel, double causeDv)
        {
            if (group == null || target == null) return false;
            // 节流 60s：扫描是 O(几百次 Lambert)，只在真的需要时跑
            if (guidanceRecoverNextTick.TryGetValue(group.GroupId, out double nextTick) && physicsTick < nextTick)
                return false;
            guidanceRecoverNextTick[group.GroupId] = physicsTick + 60.0;

            var (remainingDv, _, _, _) = GetGroupDeltaV(group);
            double budget = Math.Max(remainingDv * 0.6f, 0.0);
            if (budget <= 1.0) return false;

            double rRef = OrbitMath.SemiMajorAxis(r1Rel, v1Rel, gm);
            var plan = RendezvousCore.PlanRecovery(r1Rel, v1Rel, physicsTick,
                tick => TryPredictTargetAtTick(target, tick, out var p, out var v)
                    ? (p + aimOffset, v)
                    : default,
                gm, GetBodySafeRadius(group.CelestialBodyId), rRef,
                group.rendezvousPrograde, budget);
            if (!plan.Valid)
            {
                // 无解时给一条"相位优先"的量化建议（S3）：Δv 与圈数成反比，比硬追便宜一个量级
                double phaseErr = PhaseErrorRadians(group, target, gm);
                var ph = RendezvousCore.PlanPhasing(group.localPosition, group.localVelocity, gm, phaseErr);
                Log.Message(string.Format(
                    "[Guidance] Group {0} 重规划无解（预算 {1:F0} m/s，原修正量 {2:F0} m/s）｜相位差 {3:F1}°，相位规划建议：{4}",
                    group.GroupId, budget, causeDv, phaseErr * Mathf.Rad2Deg,
                    ph.Valid ? string.Format("切向 {0:F0} m/s 进相位轨道滑行 {1} 圈（共 {2:F0} m/s，滑行 {3:F0}s）",
                        ph.OutDv.magnitude, ph.Revs, ph.TotalDv, ph.CoastSeconds) : "相位差过小或无解"));
                return false;
            }

            // 排队新出发段：分量与惯性方向都要填（点火时 dvMag 走分量、实际烧走 plannedWorldDv）
            DVector3 rDep = OrbitMath.GetPositionAtTimeDelta(r1Rel, v1Rel, plan.WaitSeconds, gm);
            DVector3 vDep = OrbitMath.GetVelocityAtTimeDelta(r1Rel, v1Rel, plan.WaitSeconds, gm);
            OrbitMath.WorldDeltaVToComponents(rDep, vDep, plan.DepartureDv, out double rad, out double tan, out double norm);
            double arrivalTick = physicsTick + plan.WaitSeconds + plan.TransferSeconds;
            QueueGroupManeuver(group.GroupId, new SpaceEntityGroup.PlannedManeuver
            {
                radialDV = (float)rad,
                tangentialDV = (float)tan,
                normalDV = (float)norm,
                executeTick = (int)Math.Round(physicsTick + plan.WaitSeconds),
                plannedWorldDv = plan.DepartureDv,
                active = true,
                armRendezvousGuidance = true,   // 点火后重新挂上引导（新到达时刻）
            });
            // QueueManeuver 会清掉引导 ⇒ 按新计划重挂（与 RendezvousPlanner 的确认顺序一致）
            group.RegisterRendezvousGuidance(target.GroupId, arrivalTick, group.rendezvousPrograde, group.rendezvousUseNetworks);
            Log.Message(string.Format(
                "[Guidance] Group {0} 计划重规划：等待 {1:F0}s + 转移 {2:F0}s，出发 ΔV {3:F0} m/s（到达 ΔV {4:F0} m/s，总 {5:F0} m/s）",
                group.GroupId, plan.WaitSeconds, plan.TransferSeconds,
                plan.DepartureDv.magnitude, plan.ArrivalDv, plan.TotalDv));
            Messages.Message(string.Format("Group {0}：原交会计划已不可行，已自动重规划（等待 {1:F0}s 后出发）",
                group.GroupId, plan.WaitSeconds), MessageTypeDefOf.NeutralEvent, false);
            return true;
        }

        /// <summary>
        /// S4 换点烧（修正放节点）：当前点烧会撞行星，但同一修正放到**下一个近/远地点**可能既安全又更省。
        /// 找一个这样的点 → 排成一条定时机动（走既有队列：预对准 + 燃烧居中 + 工质检查），引导让位等它执行。
        /// </summary>
        private bool TryScheduleCorrectionAtApsis(SpaceEntityGroup group, SpaceEntityGroup target, DVector3 aimOffset,
            double gm, double physicsTick, DVector3 dvNow, double safeRadius)
        {
            if (group == null || target == null || !group.HasRendezvousGuidance || group.rendezvousIsApproach)
                return false;                                  // 接近模式没有固定到达时刻，不走这条
            double arrivalTick = group.rendezvousArrivalTick;
            if (!TryPredictTargetAtTick(target, arrivalTick, out var r2Rel, out _)) return false;
            DVector3 r2Aim = r2Rel + aimOffset;
            TransferGeometry geom = group.rendezvousPrograde ? TransferGeometry.ShortWay : TransferGeometry.LongWay;

            DVector3 r1Rel = group.localPosition, v1Rel = group.localVelocity;
            double[] candidates = { OrbitMath.GetTimeToPeriapsis(r1Rel, v1Rel), OrbitMath.GetTimeToApoapsis(r1Rel, v1Rel) };
            for (int i = 0; i < candidates.Length; i++)
            {
                double dt = candidates[i];
                if (!(dt > 1.0)) continue;                     // 已经在节点上（或算不出）
                double tofA = arrivalTick - (physicsTick + dt);
                if (tofA <= 1.0) continue;
                DVector3 rA = OrbitMath.GetPositionAtTimeDelta(r1Rel, v1Rel, dt, gm);
                DVector3 vA = OrbitMath.GetVelocityAtTimeDelta(r1Rel, v1Rel, dt, gm);
                DVector3 viA;
                try { (viA, _) = Gooding.Solve(gm, rA, r2Aim, tofA, geom); }
                catch (Exception) { continue; }
                DVector3 dvA = viA - vA;
                // 只有"那个点更省 **且** 那里安全"才值得等（否则不如就地裁剪/重规划）
                if (dvA.magnitude >= dvNow.magnitude) continue;
                if (!OrbitMath.IsOrbitSafe(rA, vA + dvA, safeRadius, gm)) continue;

                OrbitMath.WorldDeltaVToComponents(rA, vA, dvA, out double rad, out double tan, out double norm);
                QueueGroupManeuver(group.GroupId, new SpaceEntityGroup.PlannedManeuver
                {
                    radialDV = (float)rad,
                    tangentialDV = (float)tan,
                    normalDV = (float)norm,
                    executeTick = (int)Math.Round(physicsTick + dt),
                    plannedWorldDv = dvA,
                    active = true,
                    armRendezvousGuidance = true,
                });
                group.RegisterRendezvousGuidance(target.GroupId, arrivalTick, group.rendezvousPrograde, group.rendezvousUseNetworks);
                Log.Message(string.Format(
                    "[Guidance] Group {0} 当前点修正会撞行星 → 改排到{1}（T+{2:F0}s）：ΔV {3:F0} m/s（当前点需 {4:F0} m/s）",
                    group.GroupId, i == 0 ? "近地点" : "远地点", dt, dvA.magnitude, dvNow.magnitude));
                return true;
            }
            return false;
        }

        /// <summary>
        /// 降级为滚动接近（S1 的兜底）：把"冻结到达时刻 + 单脉冲修正"换成"tof = 距离/限速、修正量天然有界"
        /// 的接近模式——这是本库本来就有的 MechJeb 式滚动回路，只是原先两个模式之间没有这个台阶。
        ///
        /// ⚠ **降级阶梯必须有终点**（2026-09-17 实测教训）：已经处于接近模式时再降一次，就是
        /// "每检查周期重挂一次引导 + 重算一次不可行的 Lambert" ⇒ **无限循环刷日志**
        /// （实测 755 轮、每轮 2 行，同时永不收敛也永不告诉玩家）。到终点就如实放弃并通知玩家。
        /// </summary>
        private void DegradeToApproach(SpaceEntityGroup group, SpaceEntityGroup target, DVector3 aimOffset, string cause)
        {
            if (group == null || target == null) return;
            if (group.rendezvousIsApproach)
            {
                double distNow = (target.localPosition + aimOffset - group.localPosition).magnitude;
                Log.Warning(string.Format(
                    "[Guidance] Group {0} 交会中止：已在滚动接近模式且仍不可行（{1}）· 距离 {2:F0}km",
                    group.GroupId, cause, distNow / 1000.0));
                Messages.Message(string.Format(
                    "Group {0}：交会无法完成（{1}），已中止。若两者轨道差得远，请先用「转移到圆轨」变轨再交会。",
                    group.GroupId, cause), MessageTypeDefOf.RejectInput, false);
                group.ClearRendezvousGuidance();
                return;
            }
            group.RegisterApproachGuidance(target.GroupId, group.rendezvousUseNetworks, aimOffset);
            double distM = (target.localPosition + aimOffset - group.localPosition).magnitude;
            double etaH = distM / ApproachMaxClosingSpeed / 3600.0;
            Log.Message(string.Format(
                "[Guidance] Group {0} 精确交会 → 滚动接近（原因：{1}）：动态 tof=距离/{2:F0}m/s 修正量有界；" +
                "当前距离 {3:F0}km ⇒ 限速下约 {4:F1} 小时到达（远距降级会慢，属预期）",
                group.GroupId, cause, ApproachMaxClosingSpeed, distM / 1000.0, etaH));
            Messages.Message(string.Format("Group {0}：精确交会已转为滚动接近（{1}）", group.GroupId, cause),
                MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>相位差（弧度，>0 = 本船落后需要追）：用两船相对中心天体的相位角算。</summary>
        private double PhaseErrorRadians(SpaceEntityGroup group, SpaceEntityGroup target, double gm)
        {
            if (group == null || target == null) return 0.0;
            DVector3 rA = group.localPosition, rB = target.localPosition;
            DVector3 nA = DVector3.Cross(rA, group.localVelocity);
            if (nA.sqrMagnitude < 1e-12) return 0.0;
            DVector3 n = nA.normalized;
            // 目标在本船轨道面上的分量角
            DVector3 rBp = rB - n * DVector3.Dot(rB, n);
            if (rBp.sqrMagnitude < 1e-6 || rA.sqrMagnitude < 1e-6) return 0.0;
            double cosA = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(rA.normalized, rBp.normalized)));
            double angle = Math.Acos(cosA);
            // 目标在本船速度方向的哪一侧（>0 = 在前方 = 本船落后）
            double side = DVector3.Dot(DVector3.Cross(rA, rBp), n);
            return side >= 0.0 ? angle : -angle;
        }

        /// <summary>主推轴与目标方向的夹角（度）。无主推轴（全向类）返回 0。</summary>
        private double MainAxisBurnAngleDeg(SpaceEntityGroup group, DVector3 targetDir)
        {
            DVector3 axis = GetMainThrustWorldAxis(group);
            if (axis.sqrMagnitude < 1e-12 || targetDir.sqrMagnitude < 1e-12) return 0.0;
            double dot = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(axis.normalized, targetDir.normalized)));
            return Math.Acos(dot) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// 预对准开始日志：给出"当前夹角 / 按动量轮限速需要多久 / 距节点还有多久"三个数，
        /// 让"对准有没有留够时间"当场可判（预对准不耗料，故不涉及工质）。
        /// </summary>
        /// <summary>适配层系 → 游戏系（只用于把状态喂给游戏系口径的工具函数，如 `WorldDeltaVToComponents`）。</summary>
        private static DVector3 AdapterToGameD(MechJebLib.Primitives.V3 v) =>
            OrbitAdapter.ToD(OrbitView.AdapterToGameWorld(v));

        private void LogPreAlignStarted(SpaceEntityGroup group, int executeTick, double physicsTick)
        {
            double angleDeg = MainAxisBurnAngleDeg(group, group.pendingAlignDir);
            double rateDegS = Math.Max(group.MaxAngularRate, 1e-6) * Mathf.Rad2Deg;
            Log.Message(string.Format(
                "[预对准] G{0} 开始转向下一条机动：主推轴夹角 {1:F1}°（按 {2:F1}°/s 约需 {3:F0}s，节点 T-{4:F0}s）",
                group.GroupId, angleDeg, rateDegS, angleDeg / rateDegS, executeTick - physicsTick));
        }

        /// <summary>
        /// 交会/接近引导：为处于引导的群组按实际状态修正轨迹（MechJeb 式"评估→修正→冷却"节奏）。
        /// 两种模式共用一套引擎：
        /// - 精确交会（rendezvousIsApproach=false）：出发段点火后生效，固定到达时刻，巡航 Lambert + 终端消速；
        /// - 近距离接近（rendezvousIsApproach=true）：登记即生效，到达时刻按当前距离动态重算（tof=dist/限速），
        ///   巡航 Lambert 拦截 + 终端"消速+温柔收拢"，贴近且相对速度归零即完成。
        /// 无引导 / 燃烧中 / 队列里还有未执行机动（出发段或玩家接管）时跳过。
        /// 巡航段：检查节流（30s）+ 预测脱靶量门控（&lt; 2km 视为 on-course 零烧），脱靶才 Lambert 修正；
        /// 终端段（tof &lt; 60s）：消相对速度。修正烧后冷却 60s，避免追逐残差。
        /// 修正烧复用 StartBurn（固定惯性方向 + 工质消耗 + 撞行星保护）。
        /// 计算成本：节流后每次检查 = 1~2 次解析传播 + 偶尔 1 次 Lambert（µs 级），无性能压力。
        /// </summary>
        private void UpdateRendezvousGuidance(double physicsTick)
        {
            foreach (var group in groups.Values)
            {
                if (!group.HasRendezvousGuidance) continue;
                if (!group.IsMovable) continue;
                if (group.HasActiveBurn && !group.burnIsCorrection) continue; // 机动 burn 不打断；修正 burn 允许重规划

                // 队列里还有未执行机动（出发段在排队 / 玩家新排队）→ 引导让位
                // ★ B5：判据本体在 `BurnExecutorPolicy.GuidanceYieldsToQueue`（任何条目都让，不只 Manual ——
                //   比纯规则 `PlanOwnerRules.GuidanceMustYieldTo` 严；台架同一份）
                if (BurnExecutorPolicy.GuidanceYieldsToQueue(group.plannedManeuvers.Count)) continue;

                // 出发段尚未执行（仅精确交会模式）：若机动已被弹出（被阻止/取消）则引导作废
                // 接近模式无出发段（登记即生效），跳过此检查
                if (!group.rendezvousArmed && !group.rendezvousIsApproach)
                {
                    Log.Message(string.Format("[Guidance] Group {0} departure maneuver never executed, rendezvous guidance cancelled.",
                        group.GroupId));
                    group.ClearRendezvousGuidance();
                    continue;
                }

                if (!groups.TryGetValue(group.rendezvousTargetId, out var target) || !target.IsActive)
                {
                    Log.Message(string.Format("[Guidance] Group {0} rendezvous target {1} gone, guidance cancelled.",
                        group.GroupId, group.rendezvousTargetId));
                    group.ClearRendezvousGuidance();
                    continue;
                }

                // 瞄准偏移（跳帮落点）：目标局部系偏移 → 世界米偏移（含目标实体偏移与姿态旋转）。
                // 零偏移 = 瞄准 group 中心（原行为）。
                DVector3 aimOffset = ResolveTargetAimOffset(group, target);

                DVector3 r1Rel = group.localPosition;
                DVector3 v1Rel = group.localVelocity;

                // ── 新远场律（2026-09-17 接线）──────────────────────────────────────
                //   与纯测试台架**共用同一个入口** `RendezvousGuidanceLaw.Compose`（律只写一次）。
                //   本分支只负责"取状态 / 定预算 / 选终端"，产出的 Δv 走**与旧路径共用的落地段**
                //   （死区→可行性→撞行星保护→执行器→点火），故新律**原样继承**旧路径的全部保护。
                //
                //   口径差异（相对旧路径）：旧路径围绕"固定到达时刻 + 巡航 Lambert 修正"展开；
                //   新律是**无截止时刻的反馈律**、自带分腿 ⇒ 不用 rendezvousArrivalTick，也**不设**
                //   pendingAlignDir——旧预对准瞄的是"到达点消速方向"，对新律是错的目标，会与切向
                //   nudge 打架（小 Δv 走 RCS 全矢量、大 Δv 由 burn 内部对准，都不需要它）。
                if (UseNewRendezvousLaw && !group.rendezvousIsApproach
                    && target.CelestialBodyId == group.CelestialBodyId)
                {
                    RunNewRendezvousLaw(group, target, aimOffset, r1Rel, v1Rel, physicsTick);
                    continue;
                }

                // ⚠ 参考系前提（跨天体时显式挡住，不静默算错）：
                //   `TryPredictTargetAtTick` 给的是**目标自己中心天体**系下的状态，而 r1Rel 是
                //   追方中心天体系的 —— 两者只有在**同一中心天体**时才同系（旧路径隐含同一前提）。
                //   跨天体（绕地追绕月）时两系相差一个平移与速度 ⇒ 新律会去追一个**幻影相位差**并
                //   持续烧工质。这里退回旧路径（它至少还有脱靶量门控），并每检查周期报一次原因。
                if (UseNewRendezvousLaw && !group.rendezvousIsApproach
                    && target.CelestialBodyId != group.CelestialBodyId
                    && physicsTick >= group.rendezvousLastCheckTick + NewLawControlDt)
                {
                    group.rendezvousLastCheckTick = physicsTick;
                    Log.Warning(string.Format(
                        "[Guidance] Group {0} 与目标 {1} 的中心天体不同（{2} vs {3}）：新律的相位差需要同系状态，" +
                        "已退回旧路径。跨天体交会需先做参考系拼接（未实现）。",
                        group.GroupId, target.GroupId, group.CelestialBodyId, target.CelestialBodyId));
                }

                // ── 巡航预对准（持续）：出发 burn 完成后立刻调头，为下一次主推（终端消速）提前对准 ──
                // 终端 dv ≈ (目标到达速度 − 本船速度)，用当前相对速度方向做代理姿态；
                // 终端点火时已对准 → 无转向死区（否则最后 180° 调头 + 4km/s 消速叠一起必炸）。
                // 修正 burn 点火时由 burn 内部 AlignMainAxis 覆盖此姿态，烧完自动回归。
                if (!group.HasActiveBurn)
                {
                    if (TryPredictTargetAtTick(target, group.rendezvousArrivalTick, out _, out var vTargetHold))
                    {
                        DVector3 killDir = (vTargetHold - group.localVelocity).normalized;
                        if (killDir.sqrMagnitude > 1e-9)
                            group.pendingAlignDir = killDir;
                    }
                }

                double arrivalTick;
                double tof;
                if (group.rendezvousIsApproach)
                {
                    // 接近模式：动态到达时刻 = now + dist / 限速（修正量有界、轨道扰动小）；
                    // 贴近且相对速度归零 → 接近完成
                    double distNow = (target.localPosition + aimOffset - r1Rel).magnitude;
                    double relvelNow = (target.localVelocity - v1Rel).magnitude;
                    if (distNow < ApproachCompletionDistance && relvelNow < ApproachCompletionRelVel)
                    {
                        Log.Message(string.Format("[Guidance] Group {0} approach complete: dist={1:F0}m relvel={2:F1} m/s",
                            group.GroupId, distNow, relvelNow));
                        Messages.Message(string.Format("Group {0} 已接近 Group {1}（距离 {2:F0} m）",
                            group.GroupId, group.rendezvousTargetId, distNow), MessageTypeDefOf.TaskCompletion);
                        group.ClearRendezvousGuidance();
                        continue;
                    }
                    // ★ 时间律改成 **MJ 的形式**（2026-09-20，条目 16：我们抄错了 MJ）：
                    //   `tof = 逐轴 d / min(√(2·d·a), 限速)`（见 `RendezvousGuidanceCore.ApproachTimeSeconds`）。
                    //   ⚠ 旧写法是常数时间律 `tof = dist/50` ✗ + 一个"进 5 km 就 tof=1"的补丁压极限环 ✗✗；
                    //     MJ 这条对距离**连续** ⇒ **补丁不需要了**（顺手删掉一个静默失效面）。
                    double aApp = Math.Max(EstimateMainAcceleration(group), 0.01);
                    // 相对位置在**本船 RTN**上分解（`r1Rel/v1Rel` 就是本船相对中心天体的状态 ⇒ 它的 RTN 即本船 RTN ✓）
                    DVector3 rhatA = r1Rel.normalized;
                    DVector3 hhatA = DVector3.Cross(r1Rel, v1Rel).normalized;
                    DVector3 thatA = DVector3.Cross(hhatA, rhatA);
                    DVector3 relA = target.localPosition + aimOffset - r1Rel;      // 本船 → 目标
                    tof = Math.Max(RendezvousGuidanceCore.ApproachTimeSeconds(
                        DVector3.Dot(relA, rhatA), DVector3.Dot(relA, thatA), DVector3.Dot(relA, hhatA),
                        aApp, ApproachMaxClosingSpeed), 1.0);
                    arrivalTick = physicsTick + tof;
                }
                else
                {
                    arrivalTick = group.rendezvousArrivalTick;
                    tof = arrivalTick - physicsTick;
                    if (tof <= 0.0)
                    {
                        // 到达点已过：交会完成（终端消速已在上个检查周期内完成），不追打
                        Log.Message(string.Format("[Guidance] Group {0} rendezvous complete (arrival passed).", group.GroupId));
                        group.ClearRendezvousGuidance();
                        continue;
                    }
                }

                // ── 终端判定（动态）：固定窗口 60s，或「消相对速度所需时间已逼近剩余时间」→ 提前进终端 ──
                // 巡航 Lambert 只保证到点位置、不保证到点速度；必须提前开始消速（相对速度大时动辄数百秒）。
                bool inTerminal = tof < GuidanceTerminalWindow;
                double aMain = EstimateMainAcceleration(group);
                if (!inTerminal && aMain > 0.01
                    && TryPredictTargetAtTick(target, arrivalTick, out _, out var vTermRel0))
                {
                    DVector3 drEst = target.localPosition + aimOffset - r1Rel;
                    double distEst = drEst.magnitude;
                    double relVel = (vTermRel0 - v1Rel).magnitude;
                    // 终端 = 第二次主推（主引擎全力消速）：在"刹车距离"处入场——
                    // 刹车距离 = relVel²/(2·aMain)，此时全力消速恰好覆盖剩余距离，到目标时速度归零。
                    // 过早入场会停短（曾出现 ~1000km 停短）；过晚入场会冲过。
                    double brakingDist = relVel * relVel / (2.0 * aMain);
                    if (distEst <= brakingDist + 30000.0) // +30km 余量（引导 10s 节拍），末段位置收敛补正
                        inTerminal = true;
                }

                // ── 巡航预对准（持续）：出发 burn 完成后立刻调头，为下一次主推（终端消速）提前对准 ──
                // 终端 dv ≈ (目标到达速度 − 本船速度)，用当前相对速度方向做代理姿态；
                // 终端点火时已对准 → 无转向死区。修正 burn 点火时由 burn 内部 AlignMainAxis 临时覆盖。
                if (!group.HasActiveBurn)
                {
                    if (TryPredictTargetAtTick(target, arrivalTick, out _, out var vTargetHold))
                    {
                        DVector3 killDir = (vTargetHold - v1Rel).normalized;
                        if (killDir.sqrMagnitude > 1e-9)
                            group.pendingAlignDir = killDir;
                    }
                }

                // 修正烧后冷却 + 检查节流：避免追逐残差与高频点火（on-course 时大多更新直接跳过）。
                // 终端段跳过冷却（消速要连续迭代）；进行中的修正 burn 也不受冷却限制（允许打断重烧）。
                if (!group.HasActiveBurn && !inTerminal
                    && physicsTick < group.rendezvousLastBurnTick + GuidanceBurnCooldown) continue;
                double checkInterval = inTerminal ? GuidanceTerminalCheckInterval : GuidanceCheckInterval;
                if (physicsTick < group.rendezvousLastCheckTick + checkInterval) continue;
                group.rendezvousLastCheckTick = physicsTick;

                double gm = GetBodyGM(group.CelestialBodyId);
                double missM = double.NaN;
                DVector3 dv;

                if (inTerminal)
                {
                    // 终端 = 第二次主推（主引擎全力消速）：dv = 目标速度 − 当前速度（4.4km/s 级）。
                    // 入场已按刹车距离门控（relVel²/2aMain），全力消速恰好覆盖剩余距离 → 到目标速度归零。
                    // 速度接近匹配但距离仍大时补位置收敛（轨迹整形），避免"停在远处"。
                    if (!TryPredictTargetAtTick(target, arrivalTick, out _, out var vTargetRel))
                        continue;
                    DVector3 drClose = target.localPosition + aimOffset - r1Rel;
                    double distClose = drClose.magnitude;
                    dv = vTargetRel - v1Rel;
                    double relVel = dv.magnitude;
                    if (relVel < 100.0 && distClose > 10000.0)
                    {
                        // 速度已匹配但距离还大：补位置收敛（dr/tof，RCS 级小修正）
                        dv = vTargetRel + drClose / Math.Max(tof, 1.0) - v1Rel;
                    }
                    double needDv = dv.magnitude; // 数学模型：主推 Δv（或末段综合 dv）
                    // 对比日志：距离 + 相对速度 + 主推 Δv（数学模型目标）
                    if (DebugDetailedSteering)
                    {
                        Log.Message(string.Format("[Terminal] G{0} tof={1:F0}s dist={2:F0}km relVel={3:F1}m/s mainDv={4:F1}m/s",
                            group.GroupId, tof, distClose / 1000.0, relVel, needDv));
                    }
                }
                else
                {
                    // 巡航：预测脱靶量门控 → 只有真的会脱靶才修，on-course 零烧
                    if (!TryPredictTargetAtTick(target, arrivalTick, out var r2Rel, out _))
                        continue;
                    DVector3 r2Aim = r2Rel + aimOffset;
                    DVector3 r1ArrivalPred = OrbitMath.GetPositionAtTimeDelta(r1Rel, v1Rel, tof, gm);
                    missM = (r1ArrivalPred - r2Aim).magnitude;
                    if (double.IsNaN(missM) || missM < GuidanceMissThreshold)
                        continue;
                    TransferGeometry geom = group.rendezvousPrograde
                        ? TransferGeometry.ShortWay : TransferGeometry.LongWay;
                    DVector3 vi;
                    try
                    {
                        (vi, _) = Gooding.Solve(gm, r1Rel, r2Aim, tof, geom);
                    }
                    catch (Exception)
                    {
                        continue; // 本次无解，下次再试
                    }
                    dv = vi - v1Rel;
                }

                ApplyGuidanceCorrection(group, target, aimOffset, gm, physicsTick, r1Rel, v1Rel, dv,
                    inTerminal, inTerminal, tof, missM,
                    group.rendezvousIsApproach ? "(approach)" : inTerminal ? "(terminal)" : "",
                    newLawMode: false, deadbandMps: GuidanceBurnDeadband);
            }
        }

        /// <summary>
        /// 交会的**终点**：如实放弃并告诉玩家。不复用"降级到另一套引擎"——那在远目标上会算出
        /// 没道理的账单（限速 167 小时 / 一次 3828 m/s 拦截），实测还会每周期重挂一次引导空转。
        /// 消息里给出**可执行的下一步**（先用「转移到圆轨」变轨），而不是只报错。
        /// </summary>
        private void GiveUpRendezvous(SpaceEntityGroup group, SpaceEntityGroup target, DVector3 aimOffset, string cause)
        {
            if (group == null) return;
            double distKm = target != null
                ? (target.localPosition + aimOffset - group.localPosition).magnitude / 1000.0
                : double.NaN;
            Log.Warning(string.Format("[Guidance] Group {0} 交会中止：{1}（距离 {2:F0}km）",
                group.GroupId, cause, distKm));
            Messages.Message(string.Format(
                "Group {0}：交会中止（{1}）。若两者轨道相差较远，请先用「转移到圆轨」变轨后再交会。",
                group.GroupId, cause), MessageTypeDefOf.RejectInput, false);
            group.ClearRendezvousGuidance();
        }

        /// <summary>
        /// 引导修正烧的**落地段**（两条律共用）：死区 → 可行性 → 撞行星保护 → 执行器选择 → StartBurn。
        ///
        /// 抽出来的理由：新远场律（<see cref="RendezvousGuidanceLaw"/>）要**原样继承**旧路径的全部
        /// 保护（工质可行性、撞行星饱和裁剪/换点、圆轨解析模式退出、RCS/主推分发）。各写一份
        /// 就是两处需要同步的安全逻辑 —— 漏一处是静默失效，不是崩溃。
        /// </summary>
        /// <param name="inTerminal">终端段（决定死区分支是否就地完成交会 = <paramref name="completeOnDeadband"/>）。</param>
        /// <param name="completeOnDeadband">死区即"交会完成"（旧路径的终端语义）；新律自己判完成，传 false。</param>
        /// <param name="tof">剩余时间（仅日志）。</param>
        /// <param name="missM">预测脱靶量（仅日志，可 NaN）。</param>
        /// <param name="tag">日志标签（"(terminal)" / "(approach)" / ""）。</param>
        /// <param name="newLawMode">本段由**新律**驱动。它同时决定两件事：
        /// ① 不走旧路径的重规划（"固定到达时刻 + 单弧 Lambert"会把新律拽回旧提法）；
        /// ② 不可行时**不降级到滚动接近**——那是给"近距离目标"的引擎，对远目标会算出
        ///    "限速 167 小时 + 一次 3828 m/s 的 Lambert 拦截"这种没道理的账单（实测循环 755 轮）。
        ///    新律宁可如实中止并告诉玩家。</param>
        /// <param name="deadbandMps">点火死区（m/s）。⚠ **两条律不能共用一个常数**：
        /// 旧路径的 2 m/s 是给"数百 m/s 级修正"用的；新律每周期只交付 `aMax×dt`，
        /// 低推力船（aMax≈0.1 m/s²）每周期就是 1 m/s 级 ⇒ 用 2 m/s 死区会把整条腿的
        /// 相位机动**全部吃掉**，表现为"律在算、船不动"的静默失效。</param>
        private void ApplyGuidanceCorrection(
            SpaceEntityGroup group, SpaceEntityGroup target, DVector3 aimOffset,
            double gm, double physicsTick, DVector3 r1Rel, DVector3 v1Rel, DVector3 dv,
            bool inTerminal, bool completeOnDeadband, double tof, double missM, string tag,
            bool newLawMode, double deadbandMps)
        {
            // ★★ **引导修正烧的唯一收口（"MJ 那条路在走时，我们自己的东西一律不落地）**
            //   —— 2026-09-21 审计：这条有**三个**调用点（旧路径 / 远场新律 / 终端 CW），
            //   而我的门只加在“远场新律”那一段 ⇒ 终端那条从分派块落下来时照样开烧
            //   （实测：76 发、**2321 m/s**）。放在这里 = 不依赖调用方各自记得加门（本仓库的"唯一算式/唯一收口"纪律）。
            //   ❌ 反例链条：trim（42 次）· 远场修正烧（1036 m/s）· 终端（2321 m/s）——
            //   都是“我们的机制把自己当成**默认可用的兜底**” ⇒ 阶梯的每一个空档都被填上 ✗。
            if (group.ladderOwnsFarField) return;
            if (dv.magnitude < deadbandMps)
            {
                // 修正量可忽略：若正在烧修正 burn → 当前修正已多余，中断（防过烧）
                if (group.HasActiveBurn)
                    InterruptCorrectionBurn(group);
                // 终端：相对速度已匹配（≤ 死区）→ 交会完成（提前于"到达点已过"）
                if (completeOnDeadband)
                {
                    Log.Message(string.Format("[Guidance] Group {0} terminal complete: relVel={1:F1} m/s tof={2:F0}s",
                        group.GroupId, dv.magnitude, tof));
                    Messages.Message(string.Format("Group {0} 交会完成（相对速度已匹配）", group.GroupId),
                        MessageTypeDefOf.TaskCompletion);
                    group.ClearRendezvousGuidance();
                }
                return;
            }

            // ── 修正 burn 打断重烧：方向大变（>30°）→ 重烧；否则让当前 burn 继续 ──
            // 避免修正过期：烧中重新评估，若轨迹变化导致修正方向偏离，立即换新解。
            if (group.HasActiveBurn)
            {
                double curDot = DVector3.Dot(group.burnDirection.normalized, dv.normalized);
                if (curDot >= Math.Cos(30f * Mathf.Deg2Rad))
                    return; // 方向没大变 → 继续当前烧（不重复对准/浪费）
                InterruptCorrectionBurn(group);
                Log.Message(string.Format("[Guidance] Group {0} correction burn replanned: dv={1:F1} m/s (direction changed)",
                    group.GroupId, dv.magnitude));
            }

            // 可行性（继承出发机动的工质网络）
            var src = GetGroupPropulsionSource(group);
            double mDry = GetGroupMass(group, null);
            var est = BurnCalculator.Estimate((float)dv.magnitude, (float)mDry, src,
                src != null ? src.Networks : null, group.rendezvousUseNetworks);
            double safeRadius = GetBodySafeRadius(group.CelestialBodyId);

            if (!est.Feasible)
            {
                // 原计划已不可做（修正量失控 / 工质不够）→ **不终止**，走恢复阶梯：
                //   ① 重规划（等待 + 转移，时间当自由变量 = MechJeb 的"重算并等窗口"）
                //   ② 退化为滚动接近（有界收敛，永远有出路）
                Log.Message(string.Format(
                    "[Guidance] Group {0} correction infeasible: dv={1:F1} m/s 需 {2:F0} kg，可达上限 {3:F0} m/s → 尝试重规划",
                    group.GroupId, dv.magnitude, est.RequiredPropellant, est.MaxDeltaV));
                if (!newLawMode
                    && TryRecoverRendezvousPlan(group, target, aimOffset, gm, physicsTick, r1Rel, v1Rel, dv.magnitude))
                    return;
                if (newLawMode)
                {
                    GiveUpRendezvous(group, target, aimOffset, "工质不足以完成修正");
                    return;
                }
                DegradeToApproach(group, target, aimOffset, "修正不可行且重规划无解");
                return;
            }

            // 撞行星保护：修正烧后轨道近地点不低于安全半径。
            // 不再"整轮跳过"（旧行为死锁的根源）：先做**饱和裁剪**（S2，能烧多少烧多少），
            // 再尝试**换点烧**（S4，排到下一个近/远地点 —— MechJeb 的"修正放节点"），
            // 都不行才重规划 / 降级。
            if (!OrbitMath.IsOrbitSafe(r1Rel, v1Rel + dv, safeRadius, gm))
            {
                double safeK = RendezvousCore.MaxSafeDvScale(r1Rel, v1Rel, dv, safeRadius, gm);
                if (safeK >= 0.05)
                {
                    var clipped = dv * safeK;
                    Log.Message(string.Format(
                        "[Guidance] Group {0} 修正会掉高度 → 安全裁剪至 {1:F0}%（{2:F1}→{3:F1} m/s）：每轮推进一点，不放空等窗口",
                        group.GroupId, safeK * 100.0, dv.magnitude, clipped.magnitude));
                    dv = clipped;
                }
                else if (TryScheduleCorrectionAtApsis(group, target, aimOffset, gm, physicsTick, dv, safeRadius))
                {
                    return;
                }
                else if (!newLawMode
                    && TryRecoverRendezvousPlan(group, target, aimOffset, gm, physicsTick, r1Rel, v1Rel, dv.magnitude))
                {
                    return;
                }
                else if (newLawMode)
                {
                    // ⚠ 新律在"烧不动"时的正确动作是**这一轮不点火**，不是中止：
                    //   撞行星保护在 δn 上设了上限（LEO 缩轨只能缩到大气层边缘 ⇒ δn_max≈6.3e-5 rad/s
                    //   ⇒ 一圈能收 ≈20° 相位）。要收更大的相位差，律会一路饱和、被反复钳制，
                    //   而**船此刻正停在那个物理上限的相位轨道上、按最大速率收相位** ——
                    //   此时中止等于把正在正常工作的交会掐死（2026-09-17：这条不补上，
                    //   "一圈交会"遇到 Δφ>20° 会立刻被误判成失败）。
                    if (DebugDetailedSteering)
                    {
                        Log.Message(string.Format(
                            "[NewLaw] Group {0} 撞行星保护钳制（safeK<5%）：本轮不点火，按物理速率继续收相位",
                            group.GroupId));
                    }
                    return;
                }
                else
                {
                    DegradeToApproach(group, target, aimOffset, "修正会撞行星且无处可烧");
                    return;
                }
            }

            // ⚠ 任何引导烧都必须先**退出圆轨解析模式**：圆轨群组的位置/速度每步由 GetCircularPositionAtTick
            // /GetCircularVelocityAtTick 重算 ⇒ 冲量（pendingImpulse）会被静默抹掉，等于没烧。
            // 机动点火路径早有这一步（见上面 hadOrbitKeeping 的保存/恢复），引导修正烧此前漏了
            // ⇒ "登记即生效"的滚动接近对圆轨船完全无效（两艘放置船互相接近会原地不动，且日志全绿）。
            if (group.IsCircularOrbit)
            {
                bool hadKeeping = group.HasOrbitKeeping;
                HashSet<int>? keptWhitelist = group.orbitKeepingWhitelist;   // ExitCircularOrbit 会把它置空
                group.ExitCircularOrbit();
                if (hadKeeping)
                {
                    group.HasOrbitKeeping = true;                            // 圆轨保持是交会计划刻意开的，别丢
                    if (keptWhitelist != null)
                        group.orbitKeepingWhitelist = keptWhitelist;
                }
                Log.Message(string.Format("[Guidance] Group {0} 退出圆轨解析模式（修正烧要真的改轨道）", group.GroupId));
            }

            // 修正 burn 使用任务网络（继承出发机动的工质选择），避免沿用上一段机动残留的过滤
            group.burnEnabledNetworks = group.rendezvousUseNetworks;
            group.burnIsCorrection = true; // 标记引导修正 burn（允许引导打断重烧）
            // 分发：小修正用 RCS（全矢量免对准，秒级烧完，无转向死区）；
            // 大修正用主引擎（对准后全力烧）——RCS 推力小，大修正拖长引导冻结 → 修正过期 → 死循环。
            group.burnUseRcs = false;
            var rcsMc = group.GetLinkedMapComp();
            if (rcsMc != null && rcsMc.HasRcs)
            {
                double rcsMass = GetGroupMass(group, rcsMc);
                double aRcs = rcsMc.RcsTotalThrust / Math.Max(rcsMass, 1.0);
                // RCS 能在 ~15s 内烧完的修正量才用 RCS（引导冻结时间可控）
                group.burnUseRcs = aRcs > 0.0 && dv.magnitude <= aRcs * 15.0;
            }
            group.StartBurn(dv);
            group.rendezvousLastBurnTick = physicsTick; // 冷却：烧完等一个冷却期再评估
            group.GetLinkedMapComp()?.SetThrustersBurning(true);
            if (trackedManeuvers.ContainsKey(group.GroupId))
                trackedManeuvers[group.GroupId] = new ManeuverTracker { ExecutePhysicsTick = physicsTick, IsActive = true };
            Log.Message(string.Format("[Guidance] Group {0} correction burn started: dv={1:F1} m/s miss={2:F0}m tof={3:F0}s rcs={4} {5}",
                group.GroupId, dv.magnitude, missM, tof, group.burnUseRcs, tag));
        }

        /// <summary>
        /// **新远场律的游戏侧接线**：取状态 → 定预算 → 选终端 → 调纯核心 → 交给共用落地段。
        ///
        /// 与纯测试台架（<c>InPlaneRendezvousPilot</c>）的关系：**律的合一是同一个函数**
        /// （<see cref="RendezvousGuidanceLaw.Compose"/>）；此处的差异全部是"执行器口径"：
        ///   · 台架是每物理步节流执行（Δv 按步分批）；游戏侧 <c>StartBurn</c> 是**原子脉冲**
        ///     （满推力烧完为止）⇒ 本函数在烧的期间**不再发新烧**，避免重复交付；
        ///   · 台架 dt = 物理步；游戏侧 dt = 控制周期 <see cref="NewLawControlDt"/>，
        ///     且律给出的 |Δv| ≤ aMax×dt ⇒ 满推力能在一个周期内烧完（自洽，见下）。
        ///
        /// 完成判据用 <see cref="RendezvousGuidanceCore.HasRendezvoused"/>（距离 + 相对速度两项），
        /// 取代旧路径"到达点已过即完成"的静默假成功（旧口径不看距离，见审查 D4）。
        /// </summary>
        private void RunNewRendezvousLaw(
            SpaceEntityGroup group, SpaceEntityGroup target, DVector3 aimOffset,
            DVector3 r1Rel, DVector3 v1Rel, double physicsTick)
        {
            // 修正烧在飞：等它烧完（游戏侧的烧是原子脉冲，不像台架每步节流）
            if (group.HasActiveBurn) return;
            if (physicsTick < group.rendezvousLastCheckTick + NewLawControlDt) return;
            group.rendezvousLastCheckTick = physicsTick;

            if (!TryPredictTargetAtTick(target, physicsTick, out var r2Rel, out var v2Rel)) return;
            DVector3 rTgt = r2Rel + aimOffset;

            double gm = GetBodyGM(group.CelestialBodyId);
            double aMax = EstimateMaxAcceleration(group);
            if (!(gm > 0.0) || !(aMax > 0.0) || !(r1Rel.magnitude > 1.0)) return;

            DVector3 relPos = r1Rel - rTgt;
            DVector3 relVel = v1Rel - v2Rel;
            double relDist = relPos.magnitude;
            double relSpeed = relVel.magnitude;
            double rMag = r1Rel.magnitude;
            // 点火死区按**本船每周期能交付的 Δv** 定（见 NewLawDeadbandFraction 的说明）
            double deadband = Math.Max(NewLawDeadbandFraction * aMax * NewLawControlDt, 1e-3);

            // ── 完成（判据 = **目的**；唯一求值口 = `SpaceEntityGroup.IsRendezvousGoalMet`）──
            //   ⚠ 走统一口而不是直接 `IsMet`：相位目的要算有符号相位差与半长轴差，
            //     那些量在这里拼一次、在那里又拼一次就会两套口径（本会话的老毛病）。
            bool goalMet = group.IsRendezvousGoalMet(target, gm, out double phaseErrDeg);
            if (goalMet)
            {
                Log.Message(string.Format(
                    "[Guidance] Group {0} rendezvous complete (new law): dist={1:F0}m relVel={2:F2} m/s"
                    + "｜目的={3}｜相位差={4}",
                    group.GroupId, relDist, relSpeed, group.EffectiveRendezvousGoal,
                    double.IsNaN(phaseErrDeg) ? "—" : string.Format("{0:+0.0;-0.0;0}°", phaseErrDeg)));
                Messages.Message(string.Format("Group {0} 交会完成（距离 {1:F0} m，相对速度 {2:F2} m/s）",
                    group.GroupId, relDist, relSpeed), MessageTypeDefOf.TaskCompletion);
                group.ClearRendezvousGuidance();
                return;
            }

            // ★★★ **收尾捷径**（2026-09-21，用户："偷奸耍滑来避免过多消耗算力"）——
            //   条件（两条同时）：
            //     ① 到达判据（`EffectiveRendezvousGoal`）**已成立**；
            //     ② 残余相对速度 ≤ **一个物理步能给的 Δv**（`a_full · dt`，`a_full` = 当前可用全推力/质量
            //        = 执行器 `BurnStep.Compute` 的 `aFull` **同一份口径**）。
            //   则**直接把相对速度抹平**（改 `CurrentOrbit.velocity` 减去相对速度）、结束燃烧、
            //   **退出交会程序**——不再走"解 Δv → 选网 → 方向 → 居中 → 对锥"整条链。
            //
            //   ## 为什么这不是"作弊"（物理上它是可执行的）
            //   ② 保证了这个 Δv **一个物理步之内烧得完**——所以"直接抹平"与"老老实实烧一步"
            //   在物理上**等价**，差别只是省掉整条控制链（也省掉了那条链的失败模式）。
            //   ⚠ 这一条正好消灭本会话一路治不好的症状：收尾残余 15 m/s 时，主推**一步给 25 m/s**
            //     ⇒ 粒度比要做的活还大 ⇒ 方向噪声 + 每轮 180° 掉头 ⇒ 永远差一口气。
            //     在捷径下那种局面**根本不发生**。
            //
            //   ## 为什么阈值取 `a_full · dt` 而不是别的
            //   · 它随**时间流速**自动缩放（1/60 档 dt 小 60 倍 ⇒ 阈值也小 60 倍）；
            //   · `a_full` 取**当前可用的全推力/质量**（`EstimateMaxAcceleration`，与执行器的 `aFull`
            //     同一份口径）⇒ 换网/耗料/用不用 RCS 都不会让阈值失真。
            //   · **含 RCS 是刻意的**（用户 2026-09-21 裁定）：`EstimateMaxAcceleration` 把 RCS 计入
            //     ⇒ 它恰好等于执行器算得出来的 `aFull` ⇒ "一步能给的 Δv"没有高估也没有低估。
            //     ⚠ 别改用 `EstimateMainAcceleration`（"总推力 − RCS"）：它底下走 `GetLinkedMapComp()`，
            //       而无 Map 船那里返回 null ⇒ 减不掉 RCS ⇒ 口径名义/实际不符（是个坑，不是选择）。
            //   ⚠ 抹平的量 = `−v_rel`，即"把本船速度对齐到目标速度" ⇒ 抹平后相对速度**严格为 0**，
            //     距离**不再变化**（这就是"交会完成"的定义）。
            //   ⚠⚠ **条件① 不能省**（第一版漏了它，实测代价：在 dist=6972 m、容差 100 m 时就抹平了
            //     ⇒ **提前 7 km 结束交会** ✗）。条件①的真正作用是"**确认已经到位**"——
            //     没有它，捷径就变成"只要相对速度够小就停"，与交会目的无关。
            if (!group.HasActiveBurn)
            {
                double aMainStep0 = EstimateMaxAcceleration(group) * UPDATE_INTERVAL_TICKS;
                double aMainStep = aMainStep0;
                if (aMainStep > 0.0 && relSpeed <= aMainStep && goalMet)
                {
                    DVector3 vBefore = group.CurrentOrbit.velocity;
                    group.CurrentOrbit.velocity = vBefore + (v2Rel - v1Rel);   // 抹平：本船 → 目标速度
                    group.OrbitDirty = true;
                    group.EndBurn();                 // 清 burn 状态（含 remainingBurnDV / 选网标记）
                    group.pendingImpulse = DVector3.zero;   // 本步已无待施加冲量
                    Log.Message(string.Format(
                        "[Guidance·捷径] G{0} **收尾捷径生效**：dist={1:F0} m ≤ 容差、残余相对速度 {2:F3} m/s "
                        + "≤ 一步可给 {3:F3} m/s（a_full={4:F3}×dt={5:F0}s）⇒ **直接抹平相对速度**并**退出交会**"
                        + "（省掉整条控制链；物理上等价于全油门烧一步）",
                        group.GroupId, relDist, relSpeed, aMainStep,
                        aMainStep / Math.Max(UPDATE_INTERVAL_TICKS, 1e-9), UPDATE_INTERVAL_TICKS));
                    Messages.Message(string.Format("Group {0} 交会完成（捷径：抹平 {1:F2} m/s）",
                        group.GroupId, relSpeed), MessageTypeDefOf.TaskCompletion);
                    group.ClearRendezvousGuidance();
                    return;
                }
                // ⚠ **"到位了但引擎吃不掉"必须说出来**（2026-09-22）：低推力档实测
                //   `dist=246 m / relVel=2.5 m/s` 而一步只能给 2.27 m/s ⇒ 捷径**沉默地不触发**，
                //   看日志的人只能猜"为什么没走捷径"。这里明说，并把两个数都摆出来。
                //   （语义上这**不是失败**：到达判据才是完成判据，捷径只是快速路径；够不着就继续磨。）
                else if (goalMet)
                {
                    Log.Message(string.Format(
                        "[Guidance·捷径] G{0} 已到位（dist={1:F0} m）但**引擎吃不掉残余**："
                        + "残余相对速度 {2:F3} m/s > 一步可给 {3:F3} m/s（a_full={4:F3}×dt={5:F0}s）"
                        + "⇒ 捷径不适用（这不是失败：到达判据才是完成判据，捷径只是快速路径）",
                        group.GroupId, relDist, relSpeed, aMainStep0,
                        aMainStep0 / Math.Max(UPDATE_INTERVAL_TICKS, 1e-9), UPDATE_INTERVAL_TICKS));
                }
            }

            // ── 巡航预对准（**终端方向**）────────────────────────────────────────
            // ⚠ 2026-09-17 游戏内实测补回：新律此前**不设**预对准（我的理由是"推力是 ±切向、慢慢转就够"），
            //   那对远场成立，**对终端不成立**——终端方向 = "杀掉相对速度"，每轮都在变。
            //   而游戏里的 burn 是"固定惯性方向 + 主轴 1.5° 对准门"：方向没对上就等，
            //   等完再烧时状态已经跑掉 ⇒ 打的是**过期**的 Δv。实测症状：
            //   379 次终端点火、[Burn] 显示 appliedDv≈18-19 m/s **确实交付了**、
            //   而相对速度**卡在 ~20 m/s 不降**、距离在 2~10 km 之间循环（极限环）✗
            //   旧路径的注释早有此结论："终端点火时已对准 → 无转向死区（否则最后 180° 调头叠一起必炸）"。
            //   ⇒ 巡航期把主轴预先指向终端的第一个冲量方向（≈ −v_rel），远场则指向切向。
            // ★★ **切除（2026-09-21 用户：“把那狗日的连续率从流程里彻底切除”）**：
            //   这里原来是“巡航预对准”：远场把主轴指向**切向**、近场指向消速方向 ✗✗
            //   而燃烧需要的是**计划方向** ⇒ 它**覆盖了机动的预对准**（两者都写 `pendingAlignDir`）
            //   ⇒ 调相/平面那两级上两者差 **40°→上百度** ⇒ **就是 74°/93° 带角度点火的真身** ✗✗
            //   （我上一轮只修了“预对准锚点”那一半，被本段盖掉了）。
            //   它属于连续律流程 ⇒ 随连续律一起切除；机动的预对准（“排队就开始转、锚在节点时刻”）保留 ✓。
            // ══ 区制分派（B2.1，2026-09-20）：**按 MJ 来**——准脉冲区整条路（含末端）都交给阶梯 ══
            // 用户裁定（2026-09-20）："我们引进 MJ 就是为了处理这个问题；MJ 优秀就按 MJ 来"。
            // ⇒ **本段必须排在终端之前**（原来排在其后 ⇒ 末端永远被我们自己的滚动 CW 终端接管 ✗）：
            //   MJ 的末端只有三级、且极便宜 —— 1/2 **就地匹配速度**、3/4 **在最近接近点匹配速度**、3b **画拦截**；
            //   它便宜是因为**分担**：画拦截的使命就是"以小相对速度到达" ⇒ 末端那一发天然小 ✓。
            //   而我们的 CW 滚动终端每拍重解整段轨迹、反复买同一段 Δv ✗（实测量到：终端吃掉 **87%** 总 Δv）。
            // ⚠ 我们那套滚动终端**保留给连续区**（低推力船那里脉冲的"画拦截/匹配速度"不可执行）—— 同一刀分派。
            // ⚠ 依据：A4 判决（阶梯在"价 × 圈"两轴占优）只在准脉冲区成立 ✓ 区制判据见 `ManeuverRegime`。
            // ⚠ 队列非空 = 阶梯已排了节点 ⇒ 本函数**整段让位**（否则后面的机制会叠在阶梯的冲量上）。
            // ★ **MJ 的反打转纪律**（2026-09-21 补，上游 `MechJebModuleRendezvousAutopilot.Drive()` 顶部）：
            //   "若已进入期望距离、而队列里下一个节点的 UT 还在**遥远的未来** ⇒ 删掉它"
            //   （MJ 原文：*If we get within the target distance and then next maneuver node is still
            //   far in the future, delete it and we will create a new one to match velocities immediately.*）
            //   —— 它的作用是：**贴到目标附近就别再执行那些为远处设计的节点** ✓
            //   ⚠ 我们实测（高推·异面异轨那轮）：没抄这条 ⇒ 末端在 20 km 上反复画拦截（`Intercept` ×63、
            //     Δv 合计 **7397 m/s** vs 政策估计 642 ✗✗）⇒ 这条不是优化，是**收敛纪律**。
            if (group.plannedManeuvers.Count > 0)
            {
                if (TryPredictTargetAtTick(target, physicsTick, out var rNear, out var vNear))
                {
                    double distNow = (group.localPosition - (rNear + ResolveTargetAimOffset(group, target))).magnitude;
                    double nextUt = group.plannedManeuvers[0].executeTick;
                    // ⚠ 用**停下尺度**（上游的 100 m），不是完成容差 2 km ——
                    //   用 2 km 会在 2 km 就清队列（提前“就地匹配”）⇒ 又跟接近级打架 ✗
                    // ★ B5：判据本体在 `BurnExecutorPolicy.ShouldDropRemoteNodes`（台架同一份）
                    if (BurnExecutorPolicy.ShouldDropRemoteNodes(distNow, group.rendezvousMatchDist, nextUt, physicsTick))
                    {
                        int dropped = group.plannedManeuvers.Count;
                        group.plannedManeuvers.Clear();
                        Log.Message(string.Format(
                            "[阶梯] G{0} 已进入停下尺度（{1:F0} m < {2:F0} m）且下一条节点在 T+{3:F0}s ⇒ " +
                            "按 MJ 纪律**删掉全部 {4} 条**，改为就地匹配速度（防末端打转）",
                            group.GroupId, distNow, group.rendezvousMatchDist, nextUt - physicsTick, dropped));
                        group.ladderLastPlanTick = double.NegativeInfinity;   // 立刻允许重规划（就地匹配）
                    }
                    else return;
                }
                else return;
            }
            var ladderMgr = celestialRef.Get();      // 本方法的入参里没有管理器（与 1853 行同取法）
            if (group.ladderEnabled && ladderMgr != null
                && physicsTick - group.ladderLastPlanTick >= group.ladderMinReplanSeconds
                && OrbitAdapter.TryToOrbitView(group, ladderMgr, physicsTick, out OrbitView ovLadSelf)
                && OrbitAdapter.TryToOrbitView(target, ladderMgr, physicsTick, out OrbitView ovLadTgt))
            {
                double dPhiLad = RendezvousLadder.PhaseDifferenceDeg(ovLadSelf, ovLadTgt, physicsTick) * Mathf.Deg2Rad;
                // ⚠ 面外**必须进价目**（2026-09-20，B2.6）：否则"异面 + 低推力"会被低估 ⇒ 错分到准脉冲区，
                //   而那里的脉冲平面改变低推力船执行不了 ✗（设计稿 §10.5-N1）。
                double dIncLad = ovLadSelf.RelativeInclination(ovLadTgt);
                double dvScaleLad = RendezvousGuidanceCore.CostEstimate(
                    ovLadSelf.semiMajorAxis, ovLadTgt.semiMajorAxis, dPhiLad, dIncLad, gm);
                double vcLad = Math.Sqrt(gm / Math.Max(rMag, 1.0));
                double gLad = gm / Math.Max(rMag * rMag, 1.0);
                ManeuverRegime.Classify(dvScaleLad, vcLad, gLad, aMax, out double thetaLad, out _);
                // **迟滞**（防在阈值上翻）：进阶梯用 0.2，退出要 > 0.3。
                //   实测这批场景 Θ 压在 0.187~0.200（正好在阈上）⇒ 无迟滞就会反复换机制。
                // ★★ **优先级翻转（用户 2026-09-21："先按 MJ 的走"）**：阶梯**默认先试**。
                //   上游根本没有"按推力分派"这回事：它对什么推力都用节点（长燃烧靠
                //   “居中点火 + 逐拍活差额”扛住）。而我们原来是 Θ ≤ 0.2 才交给阶梯
                //   ⇒ 标准推力船（本例 Θ≈0.77）**直接被判给我们自己的连续律** ⇒ 实测
                //   1321 发微冲量、总 Δv 473 m/s、**交会未完成** ✗✗（而高推那轮走阶梯只花 719 m/s ✓）。
                //   ⇒ **阶梯先走**；只有它明确给不出节点、且这一档确实超出准脉冲区（Θ > 阈值）
                //     时，才把它当**逃生口**（那是 MJ 没有对应机制的低推力区 ✓）。
                bool useLadder = true;
                if (useLadder)
                {
                    group.ladderOwnsFarField = true;
                    // ★ **归属在本机制时，空档不许让给对方**：队列空但没到重规划点 ⇒ 本拍什么都不做。
                    //   （第一版缺这一句 ⇒ 空档落回连续律，日志里两套机制交替上场 ✗）
                    if (physicsTick - group.ladderLastPlanTick < group.ladderMinReplanSeconds) return;
                    group.ladderLastPlanTick = physicsTick;
                    var ladPars = new RendezvousLadder.Params
                    {
                        // ★★ **到位门 = 交会目的给的那个**（2026-09-22，照 MJ 把"到位"与"交付"分开）：
                        //   原来读 `group.rendezvousArriveDist`（固定 100 m）⇒ **换了目的也不生效** ✗。
                        //   现在读 `EffectiveRendezvousGoal.distanceTol` ⇒ 目的（精确/战斗/松）一处定义、处处生效。
                        DesiredDistance = group.EffectiveRendezvousGoal.distanceTol,
                        // ★ “停下尺度”**单独给**（上游 `desiredDistance=100`）：把 2 km 完成容差当停下门 ⇒
                        //   比接近尺度松 20 倍 ⇒ 停-接近打架 = 过冲（实测 Intercept×58 ⇄ CA×27）。
                        MatchDistance = group.rendezvousMatchDist,
                        MaxClosingSpeed = 100.0,  // **上游值**（本库试过 30，弄坏远场拦截，已还原；见 Params 注释）
                        MaxPhasingOrbits = 5.0,
                        MinRadius = GetBodySafeRadius(group.CelestialBodyId),
                    };
                    var ladSw = System.Diagnostics.Stopwatch.StartNew();
                    var ladRung = RendezvousLadder.MakePlan(ovLadSelf, ovLadTgt, physicsTick, ladPars,
                        out List<PlanNode> ladNodes, out string ladStatus);
                    // ★ 按选中的级决定“本机动允不允许微调”：只有“立轨道”那几级（平面/霍曼/调相/圆化）该用微调；
                    //   到达族（Intercept / MatchVelocitiesNow / MatchAtClosestApproach）**不允许**
                    //   （实测：不禁的话微调每轮打一发径向烧、跟拦截闭合对抗 ✗）。
                    group.trimAllowedByRung = ladRung == RendezvousLadder.Rung.MatchPlanes
                        || ladRung == RendezvousLadder.Rung.Hohmann
                        || ladRung == RendezvousLadder.Rung.PhasingOrbit
                        || ladRung == RendezvousLadder.Rung.Circularize;
                    ladSw.Stop();
                    // ★ 慢调用探针（**只在超过阈值时打印** ⇒ 不刷屏、也不改行为）：这就是"尖峰"的分段计时。
                    //   实测基线：同轨 ~0.5~3 ms、大半径差（走霍曼搜索）9~21 ms ⇒ 阈值取 2 ms 能把后者抓出来。
                    if (Prefs.DevMode && ladSw.Elapsed.TotalMilliseconds > 2.0)
                        Log.Message(string.Format("[阶梯·耗时] G{0} MakePlan {1:F1} ms（{2}）",
                            group.GroupId, ladSw.Elapsed.TotalMilliseconds, ladRung));
                    if (ladNodes.Count > 0)
                    {
                        // ★★ **新计划开始 ⇒ 先撤回上一条计划尚未执行的节点**（2026-09-21，"抽"的来路）。
                        //   阶梯一次排**一对**（起飞 + 到达刹车，后者在 100 秒外）。起飞出队后刹车还挂着，
                        //   而本轮重规划会另排一对 ⇒ **旧的刹车仍会执行**，且按"船应该在旧计划的到达点"
                        //   施加 ⇒ 一记按过期计划打的错向冲量（实测一步 1.5 km → 11.2 km）。
                        //   ⇔ MJ 纪律的另一半：承诺两发，撤回也两发。
                        group.BeginLadderPlanEpoch();
                        int stale = group.InvalidateStaleLadderNodes();
                        if (stale > 0)
                            Log.Message(string.Format(
                                "[阶梯] G{0} 新计划 ⇒ **撤回上一条计划未执行的 {1} 条节点**（防止按过期计划打冲量）",
                                group.GroupId, stale));
                        foreach (var node in ladNodes)
                        {
                            // 节点是**适配层系**的世界向量 ⇒ 换到游戏系、按**那一刻的** RTN 分解成分量
                            DVector3 dvWorld = AdapterToGameD(node.dV);
                            // ★★ **分解的参考系 = 节点自带的参考轨道**（2026-09-21）：两冲量转移的第二发
                            //   要在**转移轨道**上分解（那一刻船在那条轨道上），不能用"点火前轨道" ✗。
                            //   没带参考轨道的节点（既有全体）⇒ 退回原口径，行为逐位不变。
                            (DVector3 rAt, DVector3 vAt) = node.hasRefOrbit
                                ? OrbitAdapter.StateAtUT(node.refOrbit, node.ut)
                                : OrbitAdapter.StateAtUT(ovLadSelf, node.ut);
                            OrbitMath.WorldDeltaVToComponents(rAt, vAt, dvWorld,
                                out double ladRad, out double ladTan, out double ladNor);
                            int lead = (int)Math.Max(2.0, node.ut - physicsTick);
                            group.QueueManeuver((float)ladRad, (float)ladTan, (float)ladNor, lead,
                                PlanOwner.Guidance, dvWorld, node.hasRefOrbit, node.refOrbit);
                        }
                        Log.Message(string.Format(
                            "[阶梯] G{0} 区制=准脉冲（Θ={1:F3} rad）⇒ {2}｜{3}｜排入 {4} 发（Δv 合计 {5:F1} m/s）",
                            group.GroupId, thetaLad, ladRung, ladStatus, ladNodes.Count,
                            RendezvousLadder.TotalDv(ladNodes)));
                        // ★ **把"下一次点火还要等多久"直接告诉玩家**（2026-09-21）：
                        //   实测用户体验：平面改变只能在交点做 ⇒ 节点被排在 **T+2148 s**，
                        //   而那个信息**埋在一行 `T+2148 s` 里** ⇒ 玩家看到的是"船对齐了但什么都不做" ✗✗。
                        //   （等待本身是几何必然，不是 bug；但**不说出来**就是仪器的错。）
                        if (ladNodes.Count > 0 && ladNodes[0].ut - physicsTick > 120.0)
                            Messages.Message(string.Format(
                                "G{0}：下一次点火在 {1:F0} 模拟秒后（约 {2:F0} 分钟）—— {3}。等待是几何决定的（如平面改变只能在交点），不是卡住。",
                                group.GroupId, ladNodes[0].ut - physicsTick, (ladNodes[0].ut - physicsTick) / 60.0,
                                ladRung), MessageTypeDefOf.NeutralEvent, false);
                        return;
                    }
                    // 没给出节点：Done 由完成判据管（在上一步已返回）；其余情况**落回连续区**（保守：那条总能执行）
                    if (ladRung != RendezvousLadder.Rung.Done)
                        Log.Message(string.Format("[阶梯] G{0} 未给节点（{1}：{2}）（Θ={3:F3}）：{4}",
                            group.GroupId, ladRung, ladStatus, thetaLad,
                            thetaLad > ManeuverRegime.ThetaThreshold
                                ? "→ 超出准脉冲区 ⇒ 交给连续律（逃生口）"
                                : "⇒ 本拍不做事（等下一次重规划）"));

                    // ★★ **不许向下落到连续律/CW 终端**（2026-09-21 实测追到的结构漏洞）：
                    //   旧写法在此清掉归属并**继续往下走** ⇒ 落进 5% 的 CW 终端 ✗✗
                    //   （实测：76 发终端修正烧、合计 **2321 m/s**，且它每拍重解整段轨迹 ⇒
                    //   把阶梯刚收到的尺寸又拉开）。
                    //   用户已裁定：**准脉冲区整条路（含末端）交给阶梯** ⇒ 本区制内不该落到终端 ✗。
                    //   （MJ 也是这样：没计划 ⇒ 什么都不做，`Drive()` 下一帧再问）。
                    //   ★ 例外：Θ > 阈值（确实超出准脉冲区）且阶梯给不出节点 ⇒ **交还连续律**
                    //   （“逃生口”：MJ 对低推力区只有 PSG，而 PSG 依赖 alglib 不可移植 ⇒ 那里本就是我们的地盘）。
                    // ★ **一律 return**：流程里已经没有连续律了（用户 2026-09-21 切除）
                    //   ⇒ 没计划就**什么都不做**，等下一次重规划（与上游 `Drive()` 同）。
                    return;
                }
                else if (group.ladderOwnsFarField)
                {
                    Log.Message(string.Format("[阶梯] G{0} Θ={1:F3} > 退出阈值 {2:F2} ⇒ 交还连续律",
                        group.GroupId, thetaLad, LadderExitTheta));
                    group.ladderOwnsFarField = false;
                }
            }

            // ★★ **流程到此为止**（用户 2026-09-21：“把那狗日的连续率从流程里彻底切除”）：
            //   上面的分派若因**前置条件**不成立而被跳过（管理器取不到 / 轨道视图构造失败），
            //   旧写法会**继续往下走** ⇒ 落进连续律/终端 ✗✗。这里断死：本函数对参与交会的群组
            //   **只有一条路**（阶梯 + MJ 靶执行器）。连续律的代码保留但**不在流程里**（将来“总修缮”阶段若要用，
            //   必须按“先照 MJ”的规矩重新接入 ✓）。
            return;
        // ★★ **连续律/ CW 终端已从流程中删除**（2026-09-21，用户：
        //   “把那狗日的连续率从流程里彻底切除”）。
        //   原本在此的`RendezvousGuidanceLaw.Compose`远场段 + 5% CW 终端（约 160 行）**整段删除** ✓
        //   —— 流程对参与交会的群组只有**一条路**：**阶梯 + MJ 靶执行器**（与上游同）。
        //   ⚠ 被删的代码仍在 git 历史里；将来“总修缮”阶段若要接回低推力区，
        //   必须按“**先照 MJ**”的规矩重新接（不许另起一套 ✗）。
        }

        #region 新律的玩家入口（2026-09-17：玩家路径不再走旧范式）

        /// <summary>交会的**时间/燃料档**：同一个旋钮的两端（相位守恒律 Δv·t = (2/3)·r·Δφ）。</summary>
        public struct RendezvousLawOption
        {
            /// <summary>给玩家看的按钮文字（含预计耗时与 Δv 量级）。</summary>
            public string Label;
            /// <summary>每段（腿）的时间预算（秒）。</summary>
            public double LegSeconds;
            /// <summary>预计总耗时（秒）。</summary>
            public double Seconds;
            /// <summary>预计 Δv 量级（m/s）= 变轨（形状）+ 相位租金。</summary>
            public double DvEstimate;
            /// <summary>其中变轨那一笔（m/s，螺旋价 × <see cref="RendezvousGuidanceLaw.ShapeCostFactor"/>）。</summary>
            public double DvShape;
            /// <summary>预计 Δv 超过本编队剩余预算 ⇒ 菜单会标出，且 <see cref="BeginRendezvousLaw"/> 会拒绝。</summary>
            public bool OverBudget;
        }

        /// <summary>
        /// 交会几何读数（UI 与选项估计共用）。返回 false = 读不到（目标不在同中心天体 / 状态不可预测）。
        /// ⚠ 相位差用 <see cref="RendezvousGuidanceLaw.PhaseErrorRadians"/>——**与律同一个函数**，
        /// UI 另算一份会出现"界面说 120°、律按别的数在烧"的静默错配。
        /// </summary>
        public bool TryGetRendezvousReadout(SpaceEntityGroup group, SpaceEntityGroup target,
            out double phaseRad, out double distM, out double relVelMps)
        {
            phaseRad = 0.0; distM = double.NaN; relVelMps = double.NaN;
            if (group == null || target == null) return false;
            // 同中心天体是硬前提：跨天体要参考系拼接（未实现），此时两个状态不同系，算了就是错的
            if (group.CelestialBodyId != target.CelestialBodyId) return false;
            double now = TimeScaleManager.GetPhysicsTickSafe();
            if (!TryPredictTargetAtTick(target, now, out var r2, out var v2)) return false;

            DVector3 rTgt = r2 + ResolveTargetAimOffset(group, target);
            DVector3 r1 = group.localPosition, v1 = group.localVelocity;
            double mu = GetBodyGM(group.CelestialBodyId);
            if (!(mu > 0.0) || !(r1.magnitude > 1.0)) return false;

            DVector3 relPos = r1 - rTgt, relVel = v1 - v2;
            distM = relPos.magnitude;
            relVelMps = relVel.magnitude;
            phaseRad = RendezvousGuidanceLaw.PhaseErrorRadians(r1, v1, rTgt, v2,
                DVector3.Cross(rTgt, v2), mu);
            return true;
        }

        /// <summary>
        /// 交会的**可选档位**（玩家路径）：返回"快速"与"省燃料"两个选项（两者接近时只给一个）。
        /// 空列表 = 该目标不可交会（不同中心天体 / 无推进 / 状态不可预测）。
        ///
        /// ⚠ **账单必须含变轨那一笔**（2026-09-17 教训）：交会总 Δv = **形状（变轨）** + **相位（收相位）**。
        /// 只报相位租金（(2/3)rΔφ/T）会在 LEO→GEO 上给出 128 m/s 的假数字，而真实账单是
        /// ~3275 m/s（螺旋价 |v_c1−v_c2|，纯台架 T2 实测）——玩家按 128 去点，烧掉的是一整罐料。
        ///
        /// 数字怎么来的（都不"估"）：
        ///  · **变轨账单** = |v_c(a_self) − v_c(a_tgt)|（螺旋价；T2 实测 ≈ 该值、≤1.15×霍曼）；
        ///  · **物理下限** = |Δφ| / δn_max，δn_max 由"缩轨不能缩进大气层"定：
        ///    探针方向按 Δφ 符号选（落后 ⇒ 缩轨/逆行烧；超前 ⇒ 抬轨/顺行烧，无上界）；
        ///  · **腿长** = max(物理下限、MinLegSeconds×1.5)；**快速**取 1.05×下限，**省燃料**取默认腿长；
        ///  · **相位租金** = (2/3)·r·|Δφ|/T。律按 S 形式摊平到段末闭合 ⇒ **段多长就等多久**。
        /// </summary>
        public List<RendezvousLawOption> BuildRendezvousLawOptions(SpaceEntityGroup group, SpaceEntityGroup target)
        {
            var list = new List<RendezvousLawOption>();
            if (!TryGetRendezvousReadout(group, target, out double phi, out _, out _)) return list;

            DVector3 r1 = group.localPosition, v1 = group.localVelocity;
            double rMag = r1.magnitude;
            double mu = GetBodyGM(group.CelestialBodyId);
            double aMax = EstimateMaxAcceleration(group);
            if (!(aMax > 0.0)) return list;                    // 没有可用推进 ⇒ 交会无从谈起
            double budgetDv = GetGroupDeltaV(group).remainingDv;

            // 变轨那一笔（螺旋价）：目标半长轴由当前状态定
            double now = TimeScaleManager.GetPhysicsTickSafe();
            double dvShape = 0.0;
            double aTgtHere = rMag;
            // 目标轨道周期：**与 aTgtHere 用同一份状态、同一条权威**（§14.3『周期』行，收敛到 OrbitView.period）。
            // 声明提到循环外是因为 rT/vT 只在预测成功时才有值（predict 失败时 aTgtHere 退回 rMag ⇒ periodTgt 保持 0）。
            double periodTgt = 0.0;
            if (TryPredictTargetAtTick(target, now, out var rT, out var vT))
            {
                DVector3 rAim = rT + ResolveTargetAimOffset(group, target);
                aTgtHere = OrbitMath.SemiMajorAxis(rAim, vT, mu);
                double vcTgtHere = aTgtHere > 1.0 ? Math.Sqrt(mu / aTgtHere) : Math.Sqrt(mu / rMag);
                dvShape = Math.Abs(Math.Sqrt(mu / rMag) - vcTgtHere);

                if (aTgtHere > 1.0) // 保留原守卫（退化轨道给 0，而不是 NaN 传进下游的 Math.Max）
                {
                    OrbitView ovTgt = OrbitView.FromStateVectors(OrbitAdapter.ToV3(rAim), OrbitAdapter.ToV3(vT), mu, now);
                    double pTgt = ovTgt.period;
                    if (!double.IsNaN(pTgt) && pTgt > 0.0) periodTgt = pTgt;
                }
            }

            // ── 形状主导（半径差得需要一次真变轨）：时间由**螺旋**定，不是相位租金 ──
            //   与律的运行口径一致（RunNewRendezvousLaw 按模式定腿长）：这里也按螺旋时间给选项，
            //   否则菜单会拿"相位租金的 128 m/s"去描述一次真实的 3275 m/s 变轨。
            if (dvShape > RendezvousGuidanceLaw.ShapeGateMps)
            {
                double aLimTgt = aTgtHere > 1.0 ? 0.05 * mu / (aTgtHere * aTgtHere) : 0.05 * mu / (rMag * rMag);
                double shapeSec = dvShape / Math.Max(Math.Min(aMax, aLimTgt), 1e-9);
                double leg = Math.Max(RendezvousGuidanceLaw.ShapeLegFactor * shapeSec, 60.0);
                AddOption(list, leg, leg, rMag, phi, dvShape, "变轨 + 交会", budgetDv);
                return list;
            }

            double uNow = 3.0 * aMax / rMag;
            double needLeg = RendezvousGuidanceCore.MinLegSeconds(phi, uNow) * RendezvousGuidanceLaw.LegSafety;

            // 物理下限探针：δn_max 由"能改多少半长轴"定
            DVector3 nrm = DVector3.Cross(r1, v1).normalized;
            DVector3 tangent = DVector3.Cross(nrm, r1.normalized).normalized;
            DVector3 probeDir = tangent * (phi < 0.0 ? -1.0 : 1.0);   // 落后⇒缩轨(逆行)；超前⇒抬轨(顺行)
            const double probeDv = 2000.0;
            double kSafe = RendezvousCore.MaxSafeDvScale(r1, v1, probeDir * probeDv,
                GetBodySafeRadius(group.CelestialBodyId), mu);
            double vNew = (v1 + probeDir * (probeDv * kSafe)).magnitude;
            double aPh = 1.0 / Math.Max(2.0 / rMag - vNew * vNew / mu, 1e-15);
            double dnMax = Math.Abs(PhasingRate(aPh, mu) - PhasingRate(rMag, mu));
            double floorSeconds = dnMax > 1e-12 ? Math.Abs(phi) / dnMax : needLeg;

            // ── 时间尺度：**一圈**是目标，物理上限由"缩轨不能缩进大气层"定 ──
            //   LEO 一圈能收 ≈20°（δn_max≈6.3e-5 rad/s）；更大的相位差要么多等几圈（廉价路径），
            //   要么大幅加 Δv 走高轨（未实现）。这里如实报出来（E2b：给玩家"所需最短时间"）。
            double physicsFloor = Math.Max(floorSeconds, 60.0);      // 缩轨到大气层边缘所需的最短时间
            bool oneOrbitOk = periodTgt > 0.0 && physicsFloor <= periodTgt * 1.05;
            double fastLeg = oneOrbitOk
                ? Math.Max(periodTgt, Math.Max(needLeg, 60.0))
                : physicsFloor;                                      // 一圈做不到 ⇒ 就只能这么快
            double cheapLeg = Math.Max(fastLeg * 2.5, periodTgt * 2.5);
            AddOption(list, fastLeg, fastLeg, rMag, phi, dvShape,
                oneOrbitOk ? "一圈交会" : string.Format("最快交会（受大气层限制，约 {0:F1} 圈）",
                    periodTgt > 0.0 ? physicsFloor / periodTgt : 0.0), budgetDv);
            if (cheapLeg > fastLeg * 1.15)
                AddOption(list, cheapLeg, cheapLeg, rMag, phi, dvShape, "省燃料交会", budgetDv);
            return list;
        }

        private static double PhasingRate(double a, double mu) => Math.Sqrt(mu / (a * a * a));

        /// <summary>
        /// **终端预算的实测系数**：终端在交班时要杀掉的相对速度 ≈ `r·|Δφ|/T`（远场 δn 的峰值量级），
        /// 而滚动执行的 CW 终端账单 ≈ 2.5× 那个速度。依据两处实测：
        ///   · T12（LEO 同轨 15°、一圈）：r·φ/P = 251 m/s ⇒ 终端实测 597（2.4×）· 总 697（估计 796，偏保守 ✓）
        ///   · T11（LEO→GEO、T=2.95e5s）：估计 146 ⇒ 总估计 5057 vs 实测 4928 ✓
        /// 用途只是**账单估计与预算闸门**，不参与控制。宁可偏保守：低估会让玩家启动一次跑不完的交会。
        /// </summary>
        private const double TerminalCostFactor = 2.5;

        private static void AddOption(List<RendezvousLawOption> list, double leg, double seconds,
            double rMag, double phi, double dvShape, string name, double budgetDv)
        {
            double dvPhase = (2.0 / 3.0) * rMag * Math.Abs(phi) / Math.Max(seconds, 1.0);
            double dvTerminal = TerminalCostFactor * rMag * Math.Abs(phi) / Math.Max(seconds, 1.0);
            double dvTransfer = dvShape * RendezvousGuidanceLaw.ShapeCostFactor;   // 含 e 清理的实测倍率
            double dvTotal = dvTransfer + dvPhase + dvTerminal;
            bool over = budgetDv > 0.0 && dvTotal > budgetDv * 0.98;
            string head = dvShape > 1.0
                ? string.Format("{0}：约 {1:F1} 小时 · Δv 量级 {2:F0} m/s（含变轨 {3:F0}）",
                    name, seconds / 3600.0, dvTotal, dvTransfer)
                : string.Format("{0}：约 {1:F1} 小时 · Δv 量级 {2:F0} m/s",
                    name, seconds / 3600.0, dvTotal);
            list.Add(new RendezvousLawOption
            {
                LegSeconds = leg,
                Seconds = seconds,
                DvEstimate = dvTotal,
                DvShape = dvTransfer,
                OverBudget = over,
                Label = over
                    ? string.Format("{0} ⚠ 超出剩余 dV（{1:F0} m/s）", head, budgetDv)
                    : head,
            });
        }

        /// <summary>
        /// **玩家路径：启动新律交会**（无出发段——律本身就是整条转移）。
        ///
        /// 与旧路径的差别（这是玩家能感到的那部分）：
        ///  · 旧：「交会规划」用 Lambert 算一条转移轨道 + 排一次出发段大机动，引导只在出发之后做修正；
        ///  · 新：什么都不排，登记即接管——时间/燃料由 <paramref name="legSeconds"/> 一个数决定。
        ///
        /// ⚠ `rendezvousArrivalTick` 仍写一个值（= 现在 + 腿长）：新律**不用**它，但万一有人把
        /// <see cref="UseNewRendezvousLaw"/> 关掉回退旧引擎，有它旧引擎才是"在飞"而不是立刻
        /// "到达点已过"（那个静默假成功是审查 D4）。
        /// </summary>
        public bool BeginRendezvousLaw(SpaceEntityGroup group, SpaceEntityGroup target,
            double legSeconds, out string reason)
        {
            reason = null;
            if (group == null || target == null) { reason = "编队不存在"; return false; }
            if (group.GroupId == target.GroupId) { reason = "不能与自身交会"; return false; }
            // 不可机动 / 不活跃的编队挂了引导也永远不会被处理（UpdateRendezvousGuidance 的守卫会跳过）
            // ⇒ 在这里挡住并说清楚原因，而不是"点了没反应"
            if (!group.IsActive || !target.IsActive) { reason = "其一已不活跃"; return false; }
            if (!group.IsMovable) { reason = "该编队不可机动（没有引擎 / 已被固定）"; return false; }
            if (group.CelestialBodyId != target.CelestialBodyId)
            {
                reason = "两者中心天体不同（跨天体交会需要参考系拼接，未实现）";
                return false;
            }
            if (EstimateMaxAcceleration(group) <= 0.0) { reason = "该编队没有可用推进"; return false; }

            // 账单先算清楚再点火：**超出剩余 dV 就不启动**。
            //   理由不是"省着点用"：这种交会会一路烧到工质尽，然后走 GiveUpRendezvous 中止
            //   （2026-09-17 实测 5814 kg / 1549 m/s 白烧后才中止）。宁可一开始就说清楚，
            //   并给出可执行的下一步（先补给 / 先用「转移到圆轨」把轨道差做小）。
            var options = BuildRendezvousLawOptions(group, target);
            if (options.Count == 0) { reason = "无法规划（目标状态不可预测）"; return false; }
            var pick = options[0];
            if (pick.OverBudget)
            {
                reason = string.Format("预计需 {0:F0} m/s，本编队剩 {1:F0} m/s ⇒ 先补给，或先用「转移到圆轨」把轨道差做小",
                    pick.DvEstimate, GetGroupDeltaV(group).remainingDv);
                return false;
            }

            double now = TimeScaleManager.GetPhysicsTickSafe();
            double leg = legSeconds > 0.0 ? legSeconds : NewLawLegSeconds;
            var nets = group.currentNetwork != null
                ? new HashSet<PropellantDef> { group.currentNetwork } : null;
            group.RegisterRendezvousGuidance(target.GroupId, now + leg, prograde: true, useNetworks: nets);
            group.ArmRendezvousGuidance();       // 无出发段 ⇒ 登记即生效
            group.rendezvousLawLegSeconds = leg; // 必须在登记**之后**写（登记会清空律状态）
            if (group.CelestialBodyId >= 0)
            {
                group.EnterOrbitKeeping(new HashSet<int> { group.CelestialBodyId });
            }
            Log.Message(string.Format(
                "[Rendezvous] Group {0} → {1} 新律交会已启动：腿长 {2:F0}s（预计 ≈{3:F1} 小时）· 无出发段 · 轨道保持=开",
                group.GroupId, target.GroupId, leg, leg / 3600.0));
            return true;
        }

        #endregion

        /// <summary>
        /// 该船当前最大可用加速度（m/s²）= 全网络推力（含 RCS）/ 当前质量。
        /// 终端消速时间估算用（上限口径：不区分方向，忽略对准）。
        /// </summary>
        internal double EstimateMaxAcceleration(SpaceEntityGroup group)
        {
            var src = GetGroupPropulsionSource(group);
            if (src == null) return 0.0;
            double mass = GetGroupMass(group, src);
            if (mass <= 0.0) return 0.0;
            double f = 0.0;
            foreach (var n in src.Networks)
                f += n.Thrust; // RCS 引擎计入网络总推力
            return f / mass;
        }

        /// <summary>
        /// 该船主引擎加速度（m/s²）=（全网络推力 − RCS）/ 当前质量。
        /// 终端"第二次主推"的消速时间估算用（RCS 只做末段精修，不计入主推时长）。
        /// </summary>
        private double EstimateMainAcceleration(SpaceEntityGroup group)
        {
            var src = GetGroupPropulsionSource(group);
            if (src == null) return 0.0;
            double mass = GetGroupMass(group, src);
            if (mass <= 0.0) return 0.0;
            double f = 0.0;
            foreach (var n in src.Networks)
                f += n.Thrust;
            if (src is SpaceMapComp mc)
                f -= mc.RcsTotalThrust; // 主推 = 总推力 − RCS
            return f / mass;
        }

        /// <summary>
        /// 中断一次进行中的引导修正 burn（剩余 dV 丢弃），清理 burn 状态与表现层。
        /// 由引导"打断重烧"路径调用。
        /// </summary>
        private void InterruptCorrectionBurn(SpaceEntityGroup group)
        {
            if (group == null || !group.HasActiveBurn) return;
            group.EndBurn(); // 清 burnUseRcs / burnIsCorrection / remainingBurnDV
            alignStateByGroup.Remove(group.GroupId);
            trackedManeuvers.Remove(group.GroupId);
            group.GetLinkedMapComp()?.SetThrustersBurning(false);
        }

        /// <summary>
        /// 预测目标群在指定 physicsTick 的中心天体相对位置/速度（与 RendezvousPlanner 相同的坐标约定）。
        /// 圆轨：解析式（位置减中心天体偏移；速度取圆轨切向）。非圆轨：从当前相对状态开普勒传播。
        /// </summary>
        private bool TryPredictTargetAtTick(SpaceEntityGroup target, double tick,
            out DVector3 posRel, out DVector3 velRel)
        {
            posRel = DVector3.zero;
            velRel = DVector3.zero;
            if (target == null) return false;

            if (target.IsCircularOrbit)
            {
                posRel = target.GetCircularPositionAtTick(tick) - target.GetCentralBodyOffset(tick);
                DVector3 radialDir = posRel.normalized;
                DVector3 tangentDir = DVector3.Cross(target.circularOrbitNormal, radialDir).normalized;
                velRel = tangentDir * (target.circularAngularVelocity * target.circularRadius);
                return true;
            }

            if (target.CelestialBodyId < 0) return false;
            double gm = GetBodyGM(target.CelestialBodyId);
            double physicsTickNow = TimeScaleManager.GetPhysicsTickSafe();
            int nowTick = (int)Math.Round(physicsTickNow);
            DVector3 relPos = target.CurrentOrbit.position - target.GetCentralBodyOffset(nowTick);
            DVector3 relVel = target.CurrentOrbit.velocity - target.GetCentralBodyVelocity(nowTick);
            double tof = tick - physicsTickNow;
            posRel = OrbitMath.GetPositionAtTimeDelta(relPos, relVel, tof, gm);
            velRel = OrbitMath.GetVelocityAtTimeDelta(relPos, relVel, tof, gm);
            return true;
        }

        /// <summary>
        /// 解析引导瞄准偏移（世界米）：追逐者配置的目标局部系偏移 →
        /// 目标实体偏移 + 姿态旋转(offset)。零偏移返回零向量（兼容瞄准 group 中心）。
        /// 跳帮落点用：落点格子 → 目标空间站局部系偏移。
        /// </summary>
        private DVector3 ResolveTargetAimOffset(SpaceEntityGroup chaser, SpaceEntityGroup target)
        {
            if (chaser.rendezvousTargetOffset == DVector3.zero) return DVector3.zero;
            foreach (int eid in target.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e == null) continue;
                return e.Position + DVector3.Rotate(chaser.rendezvousTargetOffset, e.Rotation);
            }
            return chaser.rendezvousTargetOffset;
        }

        private void UpdateCombatInterpolation(int currentTick, TimeScaleManager timeScaleManager)
        {
            double secondsPerTick = 1.0 / 60.0;

            foreach (var group in groups.Values)
            {
                if (!group.IsMovable || group.CombatCache == null) continue;

                // 插值时间相对 CombatCache 基准连续推进（不再 elapsed % 2.0 循环）。
                // 原实现取模导致非圆轨目标位置每 2s 跳回战斗开始位置，射弹（物理位置连续）
                // 与其时间基准错位 → 相对位置突变 → 命中检测永远失败。
                double t = (currentTick - group.CombatCache.baseTick) * secondsPerTick;

                // CombatCache 覆盖区间（dt）已过 → 以当前状态重建，防止 acc/jerk 外推漂移
                if (t > group.CombatCache.dt)
                    RebuildCombatCache(group, currentTick);

                DVector3 interpolatedPos = group.CombatCache.InterpolatePosition(t);
                DVector3 interpolatedVel = group.CombatCache.InterpolateVelocity(t);

                // 物理/渲染状态分离：插值结果只写渲染缓存，绝不污染 CurrentOrbit。
                // CurrentOrbit 只由物理积分（UpdateAllMovements）在 physicsTick 时间轴推进，
                // 碰撞/射弹检测统一读物理状态，与射弹同基准（万物统一物理时间步长）。
                group.SetRenderedPosition(interpolatedPos);
            }
        }

        /// <summary>为全部可动群组重建战斗插值缓存（切档进入 Combat 时调用；实现唯一）。</summary>
        public void RebuildAllCombatCaches(int currentTick)
        {
            foreach (var group in groups.Values)
            {
                if (!group.IsMovable) continue;
                RebuildCombatCache(group, currentTick);
            }
        }

        /// <summary>
        /// 以当前轨道状态重建 Combat 插值缓存（更新基准 tick + 重新采样 acc/jerk）。
        /// 重建起点 = 当前插值末端 → 位置/速度连续无跳变。
        /// </summary>
        private void RebuildCombatCache(SpaceEntityGroup group, int currentTick)
        {
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (celestialMgr == null) return;

            // 重力采样用 physicsTick（物理时间轴）；baseTick 保持 realTick 供插值索引。
            // 采样点与插值起点**同为系统系坐标**（此前 acc/jerk 取局部系、startPos 取系统系，
            // 月心系群组的渲染插值会用到混系加速度——A0 一并修正）。
            double physicsTickSample = TimeScaleManager.GetPhysicsTickSafe();
            ComputeGroupFieldAccelJerk(group, celestialMgr, physicsTickSample, gravityScratchMain,
                out var acc, out var jerk, out _, out _);

            group.CombatCache = new SpaceEntityGroup.PhysicsStateCache
            {
                baseTick = currentTick,
                startPos = group.CurrentOrbit.position,
                startVel = group.CurrentOrbit.velocity,
                acc = acc,
                jerk = jerk,
                dt = 2.0,
                offset = 0.0
            };
        }

        #region 圆轨进入（两段式 dV 机动）

        /// <summary>
        /// 圆轨进入的残差阈值（m/s）：第二段一次性消耗允许的最大残差；
        /// 第一段 dV 可行性检查时也固定加此余量（两段机动最坏消耗 = 机动所需 + 阈值）。
        /// </summary>
        public const double CircularizeDvThreshold = 10.0;

        // ── B2.2-b 闭环微调（`TrimBurn`）的判据与节拍 ──────────────────────────────
        /// <summary>微调 Δv 的下限（m/s）：比它更小的修正没有意义（下面的死区/点火门会把它吃掉），
        /// 而且每 tick 都可能触发 ⇒ 会变成"蚊子机动"。</summary>
        private const double TrimDvMin = 0.05;
        /// <summary>微调 Δv 的上限（m/s）：超过它就不是"末段收口"而是**计划本身错了** ——
        /// 那种情况该由计划侧报警（或让玩家重排），**不能悄悄补一个大机动**。</summary>
        private const double TrimDvMax = 200.0;
        /// <summary>微调相对当前 tick 的提前量（tick）：留一步让"主燃烧的最后一步冲量"先落位，
        /// 微调才按**真实**（含最后冲量）的轨道解算。</summary>
        private const int TrimLeadTicks = 2;

        /// <summary>
        /// 发起进入圆轨的两段式机动（取代直接 EnterCircularOrbit 的免费置态）：
        /// ① 按当前轨道计算圆轨机动节点 ΔV → 检查工质（所需+阈值）→ 有则立即开始缓释 burn；
        /// ② burn 完成后由 <see cref="TryCompleteCircularize"/> 按当前轨道二次计算圆轨，
        ///    残差 ≤ 阈值则一次性扣除对应工质并入轨（第二次计算的圆轨），否则保持正常轨道。
        /// </summary>
        public bool BeginCircularize(SpaceEntityGroup group, int bodyId)
        {
            if (group == null) return false;
            if (group.IsCircularOrbit)
            {
                Messages.Message(string.Format("Group {0} 已在圆轨。", group.GroupId), MessageTypeDefOf.RejectInput, false);
                return false;
            }
            if (group.HasActiveBurn || group.HasPendingCircularize)
            {
                Messages.Message(string.Format("Group {0} 正在执行机动，请稍候。", group.GroupId), MessageTypeDefOf.RejectInput, false);
                return false;
            }

            int realTick = Find.TickManager.TicksGame;
            if (!TryComputeCircularState(bodyId, group.CurrentOrbit.position, group.CurrentOrbit.velocity, realTick,
                out DVector3 vCircWorld, out double r))
            {
                Messages.Message(string.Format("Group {0} 无法计算天体 {1} 的圆轨状态。", group.GroupId, bodyId), MessageTypeDefOf.RejectInput, false);
                return false;
            }

            DVector3 dvVec = vCircWorld - group.CurrentOrbit.velocity;
            double nodeDV = dvVec.magnitude;
            if (nodeDV < 0.5)
            {
                // 已接近圆轨（<0.5 m/s）：免 burn，直接走第二段结算
                return TryCompleteCircularize(group, bodyId, vCircWorld);
            }

            // ── 工质检查：所需 + 阈值（两段机动最坏消耗 = 机动所需 + 残差阈值）──
            var src = GetGroupPropulsionSource(group);
            if (src == null || src.Networks.Count == 0)
            {
                Messages.Message(string.Format("Group {0} 无推进能力，无法消耗 dV 进入圆轨。", group.GroupId), MessageTypeDefOf.RejectInput, false);
                return false;
            }
            double mDry = GetGroupMass(group, null);
            var est = BurnCalculator.Estimate((float)(nodeDV + CircularizeDvThreshold), (float)mDry, src, src.Networks, null);
            if (!est.Feasible)
            {
                string reason = est.Networks == null || est.Networks.Count == 0
                    ? "未安装引擎或未启用任何工质网络"
                    : string.Format("工质不足（需约 {0:F0} kg，最大可达 ΔV {1:F0} m/s）", est.RequiredPropellant, est.MaxDeltaV);
                Messages.Message(string.Format("Group {0} 无法进入圆轨：{1}。", group.GroupId, reason), MessageTypeDefOf.RejectInput, false);
                return false;
            }

            // ── 立即执行节点：退出圆轨残留，开始缓释 burn（全网络）──
            if (group.IsCircularOrbit)
                group.ExitCircularOrbit();
            group.burnEnabledNetworks = null;
            group.StartBurn(dvVec);
            group.GetLinkedMapComp()?.SetThrustersBurning(true);
            group.HasPendingCircularize = true;
            group.pendingCircularizeBodyId = bodyId;
            // 机动追踪：进入圆轨 burn 期间自动降速到 Normal
            trackedManeuvers[group.GroupId] = new ManeuverTracker
            {
                ExecutePhysicsTick = TimeScaleManager.GetPhysicsTickSafe(),
                IsActive = true
            };

            Log.Message(string.Format("[Circularize] Group {0} 圆轨机动开始: body={1} r={2:F0}km 节点ΔV={3:F1}m/s（检查含阈值余量 {4:F1}）",
                group.GroupId, bodyId, r / 1000.0, nodeDV, CircularizeDvThreshold));
            return true;
        }

        /// <summary>
        /// 圆轨第二段结算（在缓释 burn 完成后调用）：按当前轨道二次计算圆轨，
        /// 当前速度与圆轨速度残差 ≤ 阈值则一次性扣除对应工质并入轨（第二次计算的圆轨）；
        /// 残差过大则放弃入轨，保持正常轨道（可再次点击圆轨重试）。
        /// </summary>
        /// <param name="precomputedVCirc">已接近圆轨时直接传入的圆轨速度（避免重复计算）。</param>
        private bool TryCompleteCircularize(SpaceEntityGroup group, int bodyId, DVector3? precomputedVCirc = null)
        {
            if (group == null) return false;
            group.HasPendingCircularize = false;
            group.pendingCircularizeBodyId = -1;

            int realTick = Find.TickManager.TicksGame;
            // 最后一步 burn 冲量尚未施加：并入当前速度得到真实速度
            DVector3 vNow = group.CurrentOrbit.velocity + group.pendingImpulse;

            DVector3 vCirc;
            double r;
            if (precomputedVCirc.HasValue)
            {
                vCirc = precomputedVCirc.Value;
                r = 0.0;
            }
            else if (!TryComputeCircularState(bodyId, group.CurrentOrbit.position, vNow, realTick, out vCirc, out r))
            {
                Log.Warning(string.Format("[Circularize] Group {0} 二次计算圆轨失败，保持正常轨道。", group.GroupId));
                return false;
            }

            double residual = (vCirc - vNow).magnitude;
            if (residual > CircularizeDvThreshold)
            {
                Log.Message(string.Format("[Circularize] Group {0} 残差 {1:F1} m/s 超过阈值 {2:F1}，未进入圆轨（保持正常轨道，可重试）。",
                    group.GroupId, residual, CircularizeDvThreshold));
                Messages.Message(string.Format("Group {0} 圆轨失败：残差 {1:F1} m/s 超过阈值 {2:F1} m/s。",
                    group.GroupId, residual, CircularizeDvThreshold), MessageTypeDefOf.RejectInput, false);
                return false;
            }

            // ── 成功：真实速度（含最后冲量）落位 → 清空 pending → 一次性扣残差工质 → 进入圆轨 ──
            group.CurrentOrbit.velocity = vNow;
            group.pendingImpulse = DVector3.zero; // 已并入 vNow，不再叠加
            ConsumeDeltaVOneShot(group, residual);
            group.EnterCircularOrbit(realTick, bodyId);

            Log.Message(string.Format("[Circularize] Group {0} 进入圆轨: body={1} r={2:F0}km 残差ΔV={3:F1} m/s（≤阈值 {4:F1}）已一次性扣工质",
                group.GroupId, bodyId, r > 0.0 ? r / 1000.0 : group.circularRadius / 1000.0, residual, CircularizeDvThreshold));
            return true;
        }

        /// <summary>
        /// 计算某天体在当前状态下的圆轨速度向量（世界系）与轨道半径。
        /// 数学与 EnterCircularOrbit 一致（含月球等非行星天体的坐标换算）。
        /// </summary>
        private bool TryComputeCircularState(int bodyId, DVector3 worldPos, DVector3 worldVel, int realTick,
            out DVector3 vCircWorld, out double radiusMeters)
        {
            vCircWorld = DVector3.zero;
            radiusMeters = 0.0;
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return false;
            var body = mgr.GetBodyById(bodyId);
            if (body == null) return false;

            DVector3 relPos = worldPos, relVel = worldVel;
            if (body.bodyType != CelestialBodyType.Planet)
            {
                relPos -= mgr.GetBodyLocalPosition(bodyId, realTick);
                relVel -= mgr.GetBodyLocalVelocity(bodyId, realTick);
            }
            radiusMeters = relPos.magnitude;
            if (radiusMeters < 0.01) return false;

            double mu = body.gravitationalParameter > 0.0
                ? body.gravitationalParameter : SpaceConstants.PrimaryGravitationalParameter;
            double vTan = System.Math.Sqrt(mu / radiusMeters);

            DVector3 radialDir = relPos.normalized;
            DVector3 normal = DVector3.Cross(relPos, relVel).normalized;
            if (normal.sqrMagnitude < 0.01)
                normal = DVector3.up; // 速度为零/纯径向退化
            DVector3 tangentDir = DVector3.Cross(normal, radialDir).normalized;
            if (tangentDir.sqrMagnitude < 0.01)
            {
                DVector3 refVec = System.Math.Abs(radialDir.y) < 0.9 ? DVector3.up : DVector3.forward;
                tangentDir = DVector3.Cross(refVec, radialDir).normalized;
            }
            vCircWorld = tangentDir * vTan;
            if (body.bodyType != CelestialBodyType.Planet)
                vCircWorld += mgr.GetBodyLocalVelocity(bodyId, realTick);
            return true;
        }

        /// <summary>
        /// 一次性按火箭方程把残差 dV 换算为工质并扣除（按各网络 ṁ 比例分配，与 burn/轨道保持一致）。
        /// 对应「小于阈值就一次性减去 dV（减去对应工质）」。
        /// </summary>
        /// <summary>
        /// B2.2-b **闭环微调**：按「现在这一刻的轨道」与**意图轨道**的差算一次小 Δv 并排队。
        /// 执行完全走**既有机动队列**（执行器、选网、工质、居中、日志、诊断全部沿用现成那一套 ——
        /// 微调就是一次普通机动，不是第二套执行路径）。
        ///
        /// 判据（三条，全部满足才打）：
        ///   · `TrimBurn.TryPlan` 成功（共面可达）；
        ///   · Δv 落在**合理带** <c>[TrimDvMin, TrimDvMax]</c>：太小没有意义（会被点火门/死区吃掉），
        ///     太大说明不是"末段收口"而是"计划本身错了"—— 那种情况该由计划侧报警，**不能悄悄补一个大机动**；
        ///   · 每次机动最多一次（`trimFired`）。
        /// </summary>
        private void TryQueueTrimBurn(SpaceEntityGroup group, CelestialBodyManager mgr, double physicsTick)
        {
            group.trimFired = true;      // 无论成不成，判过就不再判（否则每 tick 刷屏）
            // 主燃的累计 Δv 此刻还在（`appliedBurnDV` 要等微调自己 `StartBurn` 才被清）⇒ 留给验收读数用：
            // 否则验收会显示"实际已烧 = 微调那 1.3 m/s"，把主燃的 808 藏起来（2026-09-20 实测踩到）。
            group.trimMainBurnDv = group.appliedBurnDV;
            if (!OrbitAdapter.TryToOrbitView(group, mgr, physicsTick, out OrbitView ovNow))
                return;

            (DVector3 rNow, DVector3 vNow) = OrbitAdapter.StateAtUT(ovNow, physicsTick);
            // ★ 主燃烧**最后一步的冲量**此刻可能还没落位（它在下一步的物理步首才并入速度）。
            //   不补上的话，微调会把"那一笔还没到"当成残差 ⇒ 规划出一个假的修正量。
            //   口径与 `TryCompleteCircularize` 逐字相同（"最后一步 burn 冲量尚未施加：并入当前速度"）。
            vNow += group.pendingImpulse;
            if (!TrimBurn.TryPlan(rNow, vNow, ovNow.mu, group.trimTargetA, group.trimTargetH,
                    out DVector3 dvWorld, out string why, out double aUsed))
            {
                Log.Message(string.Format("[燃烧·微调] G{0} 不微调：{1}", group.GroupId, why));
                return;
            }

            double dvMag = dvWorld.magnitude;
            if (dvMag < TrimDvMin || dvMag > TrimDvMax)
            {
                Log.Message(string.Format(
                    "[燃烧·微调] G{0} 不微调：Δv={1:F3} m/s 落在合理带 [{2:F2}, {3:F0}] 之外｜目标 a={4:F0} km",
                    group.GroupId, dvMag, TrimDvMin, TrimDvMax, group.trimTargetA / 1000.0));
                return;
            }

            // 分量按**当前** RTN 分解（执行器整段燃烧只换框架、不换解）
            OrbitMath.WorldDeltaVToComponents(rNow, vNow, dvWorld, out double rad, out double tan, out double nor);
            group.trimLastDv = dvMag;
            group.QueueManeuver((float)rad, (float)tan, (float)nor, TrimLeadTicks, PlanOwner.Executor);
            Log.Message(string.Format(
                "[燃烧·微调] G{0} 闭环微调：Δv={1:F3} m/s（R/T/N {2:F2}/{3:F2}/{4:F2}）｜本点 r={5:F1} km，"
                + "目标 a={6:F1} km（差 {7:F1} km）",
                group.GroupId, dvMag, rad, tan, nor,
                rNow.magnitude / 1000.0, group.trimTargetA / 1000.0,
                (rNow.magnitude - group.trimTargetA) / 1000.0));
            // 退化支必须**显式写出**：本点不在意图曲线上时，一发点火只能"保形状、把尺度锚到本点"
            // （见 `TrimBurn` 类注释的取舍）⇒ 半长轴会落在 r 而不是 a_t，读数上要看得出来。
            if (Math.Abs(aUsed - group.trimTargetA) > 1.0)
                Log.Message(string.Format(
                    "[燃烧·微调] G{0} 目标退化：本点 r={1:F1} km 不在意图轨道径向范围内（a_t={2:F1} km、e_t={3:E4}）"
                    + "⇒ 保形状、锚定本点 ⇒ a 落在 {4:F1} km（与意图差 {5:F1} km）；**这一发修的是 e，不是 a**",
                    group.GroupId, rNow.magnitude / 1000.0, group.trimTargetA / 1000.0,
                    Math.Sqrt(Math.Max(0.0, 1.0 - group.trimTargetH * group.trimTargetH / (ovNow.mu * group.trimTargetA))),
                    aUsed / 1000.0, (aUsed - group.trimTargetA) / 1000.0));
        }

        /// <summary>
        /// **一次性付一笔 Δv**（火箭方程唯一算式：`Δm = m₀(1 − e^(−Δv/ve))`，按各网络 ṁ 占比分摊扣料）。
        /// <paramref name="tag"/> 只影响日志前缀（既有调用方传默认值 ⇒ 输出逐字不变）；
        /// 主动降落那条路传 "降落" —— 免得日志里打出"残差"这种与场景不符的假话。
        /// </summary>
        private void ConsumeDeltaVOneShot(SpaceEntityGroup group, double dv, string tag = "Circularize")
        {
            if (dv <= 0.001) return;
            var src = GetGroupPropulsionSource(group);
            if (src == null) return;
            double ve = src.EffectiveVe;
            if (ve <= 0.0) return;
            double m0 = GetGroupMass(group, src);
            // Δm = m0·(1 − e^(−ΔV/ve))
            double dm = m0 * (1.0 - Math.Exp(-dv / ve));
            if (dm <= 0.0) return;

            float mfTotal = 0f;
            foreach (var n in src.Networks)
                mfTotal += n.MassFlow;
            if (mfTotal <= 0f) return;
            foreach (var n in src.Networks)
            {
                double share = dm * (n.MassFlow / mfTotal);
                ConsumeGroupPropellant(group, n.propellant, (float)share);
            }
            Log.Message(string.Format("[{0}] Group {1} {2}dV 一次性消耗: {3:F1} m/s → 工质 {4:F1} kg",
                tag, group.GroupId, tag == "Circularize" ? "残差 " : "", dv, dm));
        }

        /// <summary>
        /// 紧急终止当前缓释 burn：丢弃剩余 dV（已烧的工质不退，已累积未施加的冲量照常施加），
        /// 恢复网络/喷焰表现，清掉圆轨结算标记与机动追踪。
        /// </summary>
        public void EmergencyStopBurn(SpaceEntityGroup group)
        {
            if (group == null || !group.HasActiveBurn) return;
            double applied = group.appliedBurnDV;
            double planned = applied + group.remainingBurnDV;
            group.EndBurn();
            group.burnEnabledNetworks = null;
            group.GetLinkedMapComp()?.SetThrustersBurning(false);
            trackedManeuvers.Remove(group.GroupId);
            if (group.HasPendingCircularize)
            {
                group.HasPendingCircularize = false;
                group.pendingCircularizeBodyId = -1;
            }
            Log.Message(string.Format("[Burn] Group {0} burn EMERGENCY STOPPED by player (applied {1:F1} / {2:F1} m/s)",
                group.GroupId, applied, planned));
            Messages.Message(string.Format("Group {0} 机动已紧急终止（已施加 {1:F1} / {2:F1} m/s）。",
                group.GroupId, applied, planned), MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>
        /// 取消 group 从指定索引（含）开始的机动规划（保留前面的）。
        /// 队列清空时同时移除机动追踪，避免自动降速卡在 Normal。
        /// </summary>
        public void CancelGroupManeuvers(SpaceEntityGroup group, int index)
        {
            if (group == null || index < 0) return;
            group.CancelManeuversFrom(index);
            if (group.plannedManeuvers.Count == 0)
                trackedManeuvers.Remove(group.GroupId);
            Messages.Message(string.Format("Group {0} 已取消第 {1} 段及以后的机动规划。",
                group.GroupId, index + 1), MessageTypeDefOf.NeutralEvent, false);
        }

        #endregion

        #region 行星碰撞

        public double GetBodySafeRadius(int celestialBodyId)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return SpaceConstants.PlanetRadiusMeters;
            var body = mgr.GetBodyById(celestialBodyId);
            if (body == null) return SpaceConstants.PlanetRadiusMeters;
            return body.radius + body.atmosphereThickness;
        }

        public double GetBodyGM(int celestialBodyId)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return SpaceConstants.PrimaryGravitationalParameter;
            var body = mgr.GetBodyById(celestialBodyId);
            if (body == null || body.gravitationalParameter <= 0.0)
                return SpaceConstants.PrimaryGravitationalParameter;
            return body.gravitationalParameter;
        }

        private DVector3 GetBodyWorldOffset(int celestialBodyId, int tick)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return DVector3.zero;
            var body = mgr.GetBodyById(celestialBodyId);
            if (body == null || body.bodyType == CelestialBodyType.Planet)
                return DVector3.zero;
            return mgr.GetBodyLocalPosition(celestialBodyId, tick);
        }

        private DVector3 GetBodyWorldVelOffset(int celestialBodyId, int tick)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return DVector3.zero;
            var body = mgr.GetBodyById(celestialBodyId);
            if (body == null || body.bodyType == CelestialBodyType.Planet)
                return DVector3.zero;
            return mgr.GetBodyLocalVelocity(celestialBodyId, tick);
        }

        /// <summary>天体**表面**半径（不含大气）。撞击判定用这个，不用 <see cref="GetBodySafeRadius"/>。</summary>
        public double GetBodySurfaceRadius(int celestialBodyId)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var body = mgr?.GetBodyById(celestialBodyId);
            if (body == null || body.radius <= 0.0) return SpaceConstants.PlanetRadiusMeters;
            return body.radius;
        }

        /// <summary>经纬度（弧度）→ "北纬 30.0° 东经 120.0°"。**落点读数的唯一格式口**（两处各写一份即两套口径）。</summary>
        private static string FormatLongLat(double latRad, double lonRad)
        {
            double latDeg = latRad * 180.0 / Math.PI;
            double lonDeg = lonRad * 180.0 / Math.PI;
            return string.Format("{0} {1:F1}° {2} {3:F1}°",
                latDeg >= 0.0 ? "北纬" : "南纬", Math.Abs(latDeg),
                lonDeg >= 0.0 ? "东经" : "西经", Math.Abs(lonDeg));
        }

        /// <summary>
        /// 撞击天体的判定与结算 —— **"着陆 / 坠毁"的唯一事件源**。
        ///
        /// ## 语义（2026-09-22 用户裁定）
        /// **着陆 = 以一个安全的速度撞击天体**：接地速度 ≤ <see cref="SpaceConstants.MaxSafeLandingSpeedMps"/> ⇒ 着陆，
        /// 超过 ⇒ 坠毁。两种结果**各自的事件树尚未实现**，本步只做两件事：Message 结果 + 该时刻的
        /// **天体经纬度**（落点是事件树要用的输入）。
        ///
        /// ## 三处口径（2026-09-22 全部更正 —— 旧写法三条都错）
        /// ① **速度取天体相对**（`group.localVelocity`）而不是 `CurrentOrbit.velocity`（根系/惯性）：
        ///    后者对**月球**会把月球自身 ~2 km/s 的公转算进"撞击速度" ⇒ 任何月球着陆都判坠毁；
        /// ② **再扣地表共转**（<see cref="SurfaceFrame.GroundSpeed"/>）：地球赤道地表共转
        ///    ω·R = 523.6 m/s，不扣的话"跟着地面一起垂直下降"也读 524 ⇒ 阈值取多少都不可能达成"着陆"；
        /// ③ **时刻用 `physicsTick`**（与 `group.localPosition` 同一个时钟）。旧写法用
        ///    `Find.TickManager.TicksGame`（真实 tick）⇒ 战斗 1/60× 流速下两者差着量级，
        ///    有平动的天体（月球）会把偏移量算在错误的相位上。
        ///
        /// ⚠ **撞击面 = 天体表面半径**（不含大气）：旧写法用"安全半径 = 半径 + 大气厚度"
        ///   ⇒ 撞击在**大气顶**（地球 100 km 高空）就结算，与"撞击天体"不符。
        ///   撞行星**保护**仍用含大气的安全半径（那是给引导燃烧设的下限，两者不是一回事）。
        /// </summary>
        private void CheckPlanetCollision(SpaceEntityGroup group, double physicsTick)
        {
            if (group == null || !group.IsActive) return;
            if (group.exemptPlanetCollision) return; // 火箭爬升段豁免（出生于大气层内），入轨时清除

            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            double surfaceRadius = GetBodySurfaceRadius(group.CelestialBodyId);

            // 天体相对的位置/速度：本物理步**刚算好**（`localPosition/localVelocity`），不再另算一份
            DVector3 relPos = group.localPosition;
            DVector3 relVel = group.localVelocity;
            if (relPos.sqrMagnitude > surfaceRadius * surfaceRadius) return;

            // ⚠ 管理器为 null 时 `RotationOmega` 是**显式容忍**的（内部 `mgr?.GetBodyById` ⇒ 取不到天体则用默认一天）
            //   ⇒ 这里给 `!` 只是免去可空标注噪声，不是假设它非空。
            double omega = Launch.GetSurfaceState.RotationOmega(mgr!, group.CelestialBodyId);
            double impactSpeed = SurfaceFrame.GroundSpeed(relPos, relVel, omega);
            (double latRad, double lonRad) = SurfaceFrame.EciToLongLat(relPos, omega, physicsTick);
            string spot = FormatLongLat(latRad, lonRad);
            string bodyName = mgr?.GetBodyName(group.CelestialBodyId) ?? "天体";
            int groupId = group.GroupId;

            if (impactSpeed > SpaceConstants.MaxSafeLandingSpeedMps)
            {
                Log.Message(string.Format(
                    "[Collision] Group {0} CRASHED into {1} at ({2}) — ground-relative impact speed {3:F1} m/s (max safe {4:F0} m/s). Entities destroyed.",
                    groupId, bodyName, spot, impactSpeed, SpaceConstants.MaxSafeLandingSpeedMps));

                Messages.Message(string.Format(
                    "Group {0} 坠毁于{1}（{2}）：撞击速度 {3:F1} m/s，超过安全限值 {4:F0} m/s。",
                    groupId, bodyName, spot, impactSpeed, SpaceConstants.MaxSafeLandingSpeedMps),
                    MessageTypeDefOf.ThreatBig);
            }
            else
            {
                Log.Message(string.Format(
                    "[Collision] Group {0} LANDED on {1} at ({2}) — ground-relative touchdown speed {3:F1} m/s.",
                    groupId, bodyName, spot, impactSpeed));

                Messages.Message(string.Format(
                    "Group {0} 着陆于{1}（{2}）：接地速度 {3:F1} m/s。",
                    groupId, bodyName, spot, impactSpeed),
                    MessageTypeDefOf.TaskCompletion);
            }

            DestroyGroup(groupId);
        }

        /// <summary>
        /// **选 tile 主动降落的报价**（只读，不动任何状态）—— 校验链 + <see cref="LandingQuote"/> + 工质可行性。
        /// UI 的"能不能落/要多少"与结算 <see cref="TryLandAtTile"/> **共用这一条链**（两处各写一份 = 两套权威）。
        ///
        /// 校验链（顺序即拒绝优先级）：① 编队有效可动 · ② 目标天体有固体表面 · ③ 该天体有地表网格且 tile 合法
        /// · ④ 距离门（<see cref="LandingQuote.MaxSurfaceDistance"/>）· ⑤ Δv 可行性（工质）。
        /// </summary>
        public bool TryQuoteLanding(SpaceEntityGroup group, int targetBodyId, PlanetTile target,
            out LandingQuote.Result quote, out BurnEstimate est, out string bodyName, out string spot,
            out string? reason)
        {
            quote = default;
            est = default;
            bodyName = string.Empty;
            spot = string.Empty;
            reason = null;

            if (group == null || !group.IsActive) { reason = "编队无效"; return false; }
            if (!group.IsMovable) { reason = "编队不可动"; return false; }

            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) { reason = "天体管理器缺失"; return false; }
            var body = mgr.GetBodyById(targetBodyId);
            if (body == null) { reason = string.Format("天体 {0} 不存在", targetBodyId); return false; }
            bodyName = mgr.GetBodyName(targetBodyId);
            if (!body.hasSurface) { reason = string.Format("{0} 没有固体表面 ⇒ 不能降落", bodyName); return false; }

            var layer = Launch.GetSurfaceState.GetLayerForBody(mgr, targetBodyId);
            if (layer == null) { reason = string.Format("{0} 没有地表网格（layer 缺失）", bodyName); return false; }

            // ⚠ **跨层错配必须在这里挡住**：tile 索引在另一层上"看起来合法"（越界检查会放行），
            //   但落点会在**另一个天体**上 ⇒ 静默落到别处。比的是 LayerDef（实例可能不是同一个对象）。
            if (!target.Valid) { reason = "尚未选中落点（在星球视图上点一个 tile）"; return false; }
            if (!string.Equals(target.LayerDef?.defName, layer.Def?.defName, StringComparison.Ordinal))
            {
                reason = string.Format("选中的落点不在{0}的地表上（当前选中的是另一层地表 —— 先用「查看天体」切过去）",
                    bodyName);
                return false;
            }
            int tile = target.tileId;
            if (tile < 0 || tile >= layer.TilesCount)
            {
                reason = string.Format("落点 tile {0} 越界（合法 0..{1}）", tile, layer.TilesCount - 1);
                return false;
            }

            double tick = TimeScaleManager.GetPhysicsTickSafe();
            int tickI = (int)Math.Round(tick);
            double omega = Launch.GetSurfaceState.RotationOmega(mgr, targetBodyId);
            // ⚠ 参考系硬约定（见 LandingQuote 类注释）：船用 **CurrentOrbit**（根系）、天体用 GetBody*Offset
            DVector3 offPos = GetBodyWorldOffset(targetBodyId, tickI);
            DVector3 offVel = GetBodyWorldVelOffset(targetBodyId, tickI);
            DVector3 tileFixed = Launch.GetSurfaceState.TileToFixedPosition(layer, tile, body.radius);

            quote = LandingQuote.Evaluate(group.CurrentOrbit.position, group.CurrentOrbit.velocity,
                offPos, offVel, body.radius, omega, tileFixed, tick);
            if (!quote.ok) { reason = quote.reason; return false; }

            // 落点经纬度（tile 的**固连**经纬度；走既有唯一实现 —— 这里只是把落点换到天体中心系）
            (double latRad, double lonRad) = SurfaceFrame.EciToLongLat(quote.tilePos - offPos, omega, tick);
            spot = FormatLongLat(latRad, lonRad);

            // Δv 可行性：**沿用既有唯一判据**（主网/RCS 接力语义），不另写一份火箭方程
            var src = GetGroupPropulsionSource(group);
            double mDry = GetGroupMass(group, null);
            est = BurnCalculator.Estimate((float)quote.deltaV, (float)mDry, src, src?.Networks);
            if (!est.Feasible)
            {
                reason = string.Format("工质不足：本次需 Δv {0:F0} m/s（工质 {1:F0} kg），当前可达 {2:F0} m/s",
                    quote.deltaV, est.RequiredPropellant, est.MaxDeltaV);
                return false;
            }
            return true;
        }

        /// <summary>
        /// **选 tile 主动降落的唯一收口**（2026-09-22 用户裁定："接近天体时选一个 tile、消耗一定 Δv、
        /// 瞬间完成降落"）。校验与报价走 <see cref="TryQuoteLanding"/>；本函数只做**动作**：
        /// 扣料（<see cref="ConsumeDeltaVOneShot"/>：火箭方程唯一算式）→ 消息（落点经纬度）→ 销毁编队。
        /// 拒绝时**不动任何状态**。
        ///
        /// ⚠ 与"物理撞击"的分工（别混）：<see cref="CheckPlanetCollision"/> 是**没付钱**的那条路
        ///   （接地速度 > 100 m/s ⇒ 坠毁）；本函数是**主动付费**那条，永远安全。
        /// 事件树的空位：将来"着陆/坠毁各自的事件树"就挂在两处的消息收口旁边。
        /// </summary>
        public bool TryLandAtTile(SpaceEntityGroup group, int targetBodyId, PlanetTile target, out string? reason)
        {
            if (!TryQuoteLanding(group, targetBodyId, target, out var q, out var est,
                    out var bodyName, out var spot, out reason))
                return false;

            ConsumeDeltaVOneShot(group, q.deltaV, "降落");

            Log.Message(string.Format(
                "[降落] G{0} 主动降落于{1}（{2}）tile={3}｜付出 Δv={4:F1} m/s（工质 {5:F1} kg）｜地表距离 {6:F0} km",
                group.GroupId, bodyName, spot, target.tileId, q.deltaV, est.RequiredPropellant,
                q.surfaceDistance / 1000.0));
            Messages.Message(string.Format(
                "Group {0} 着陆于{1}（{2}）：付出 Δv {3:F0} m/s（工质 {4:F0} kg）。",
                group.GroupId, bodyName, spot, q.deltaV, est.RequiredPropellant),
                MessageTypeDefOf.TaskCompletion);

            DestroyGroup(group.GroupId);
            return true;
        }

        /// <summary>
        /// 从 group 的推进源工质池扣除工质。返回实际扣除量（kg，受存量截断）。
        /// 实际扣除 < 需求表示工质已耗尽（或无推进源）。
        /// </summary>
        private float ConsumeGroupPropellant(SpaceEntityGroup group, PropellantDef def, float kg)
        {
            if (def == null || kg <= 0f) return 0f;
            var src = GetGroupPropulsionSource(group);
            if (src == null) return 0f; // 无推进源 → 扣 0
            return src.TryConsumePropellant(def, kg);
        }

        /// <summary>无 Map 主推型双罐燃料摘要（主网 vs RCS 网，同 def 去重后合计）供 [Thrust] 日志。</summary>
        private string NoMapFuelSummary(SpaceEntityGroup group)
        {
            var src = GetGroupPropulsionSource(group);
            if (src == null) return "无源";
            double main = 0.0, rcs = 0.0;
            var seenMain = new HashSet<PropellantDef>();
            var seenRcs = new HashSet<PropellantDef>();
            foreach (var n in src.Networks)
            {
                if (n.propellant == null) continue;
                double m = src.GetPropellantMass(n.propellant);
                if (n.HasRcsEngines)
                {
                    if (seenRcs.Add(n.propellant)) rcs += m;
                }
                else if (seenMain.Add(n.propellant))
                {
                    main += m;
                }
            }
            return string.Format("主={0:F0} RCS={1:F0}kg", main, rcs);
        }

        /// <summary>
        /// 取 group 的推进源：优先任一实体关联的 SpaceMapComp（Map 建筑引擎/工质）；
        /// 无 Map 时 = 自带推进实体（导弹/载荷等纯物理实体，PropulsionData）：
        ///   1 个 → 该实体（单发/火箭热路径，行为不变）；
        ///   &gt;1 个（弹群，2026-09-08 思路 A）→ 群级聚合器 GroupPropulsionAggregator
        ///   （N 引擎同步：推力/流率全组合并、工质按实体分摊——燃料归实体自己，无记账）。
        /// 聚合器按「实体集版本 ⊕ 各推进实体推进版本」指纹缓存在 group 上复用（2026-09-10
        /// 性能第 1 条）：多实体组每物理步被查 4+ 次，缓存后 BuildNetworks 每变更只跑一次。
        /// 并行只读安全：指纹与 Networks 只依赖实体集与推进数据，物理步内无人变更它们
        /// （消耗工质只改存量，不入指纹）；唯一并行区 Parallel.For 每组恰好一个迭代。
        /// </summary>
        public IPropulsionSource GetGroupPropulsionSource(SpaceEntityGroup group)
        {
            var mapComp = group.GetLinkedMapComp();
            if (mapComp != null) return mapComp;
            SpaceEntity first = null;
            int n = 0;
            // 指纹复用既有遍历计算（每实体 2 次整数运算），无需在增删实体/改推进处回调置脏。
            int stamp = group.entitySetVersion;
            foreach (int eid in group.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e == null || !e.HasPropulsionData) continue;
                if (first == null) first = e;
                n++;
                stamp = stamp * 31 + e.PropulsionVersion;
            }
            if (n == 1) return first;
            if (n > 1)
            {
                var agg = group.cachedPropulsionAggregator;
                if (agg == null || agg.SourceStamp != stamp)
                {
                    agg = new GroupPropulsionAggregator(group, this, stamp);
                    group.cachedPropulsionAggregator = agg;
                    if (PerfProbe.Enabled) PerfProbe.AggRebuild(group, group.entitySetVersion, stamp, n);
                }
                else if (PerfProbe.Enabled)
                {
                    PerfProbe.AggReuse(group.GroupId);
                    if (PerfProbe.ShadowChecks && PerfProbe.ShouldShadowAgg())
                        ProbeAggregatorShadow(group, agg);
                }
                return agg;
            }
            return null;
        }

        /// <summary>
        /// 探针（第 1 条影子校验）：用同一 group/指纹现建一个聚合器（= 优化前的旧行为：每次现建），
        /// 与缓存实例逐网络比对（工质引用 / 推力 / 流率 / 主-RCS 归属），并核对燃料总量与容量是否实时。
        /// 任何差异即"缓存陈旧"，由 PerfProbe 红字报出。仅在探针开启且命中节流时调用。
        /// </summary>
        private void ProbeAggregatorShadow(SpaceEntityGroup group, GroupPropulsionAggregator cached)
        {
            var fresh = new GroupPropulsionAggregator(group, this, cached.SourceStamp);
            var a = cached.Networks;
            var b = fresh.Networks;
            if (a.Count != b.Count)
            {
                PerfProbe.AggShadowResult(false, $"G{group.GroupId} 网络数 缓存={a.Count} 现建={b.Count}");
                return;
            }
            for (int i = 0; i < a.Count; i++)
            {
                if (!ReferenceEquals(a[i].propellant, b[i].propellant))
                {
                    PerfProbe.AggShadowResult(false, $"G{group.GroupId} 网络{i} 工质引用不同：{a[i].propellant}≠{b[i].propellant}");
                    return;
                }
                if (a[i].HasRcsEngines != b[i].HasRcsEngines)
                {
                    PerfProbe.AggShadowResult(false, $"G{group.GroupId} 网络{i} 主/RCS 归属不同");
                    return;
                }
                if (Math.Abs(a[i].Thrust - b[i].Thrust) > 1e-3f || Math.Abs(a[i].MassFlow - b[i].MassFlow) > 1e-6f)
                {
                    PerfProbe.AggShadowResult(false,
                        $"G{group.GroupId} 网络{i} 参数不同：推力 {a[i].Thrust:F6}≠{b[i].Thrust:F6} 流率 {a[i].MassFlow:F6}≠{b[i].MassFlow:F6}");
                    return;
                }
            }
            // 燃料/容量必须实时（缓存不得固化存量——这是第 1 条"物理是否不及时"的关键判据）
            if (Math.Abs(cached.TotalPropellantMass - fresh.TotalPropellantMass) > 1e-3f
                || Math.Abs(cached.TotalPropellantCapacity - fresh.TotalPropellantCapacity) > 1e-3f)
            {
                PerfProbe.AggShadowResult(false,
                    $"G{group.GroupId} 燃料/容量不一致：存量 {cached.TotalPropellantMass:F3}≠{fresh.TotalPropellantMass:F3} 容量 {cached.TotalPropellantCapacity:F3}≠{fresh.TotalPropellantCapacity:F3}");
                return;
            }
            PerfProbe.AggShadowResult(true, null);
        }

        /// <summary>group 当前质量（kg）= Σ 实体结构质量 + 推进源工质总量。</summary>
        /// <summary>油门份额（aCmd/可用 a，截到 1）——头步定点读数用。</summary>
        private static double dvFullSafe(double aCmd, double accel) => accel > 0.0 ? Math.Min(1.0, aCmd / accel) : 0.0;

        /// <summary>编组质量（kg）。`src = null` ⇒ **干质量**口径（故参数显式可空）。</summary>
        private double GetGroupMass(SpaceEntityGroup group, IPropulsionSource? src)
        {
            double mass = 0.0;
            foreach (int eid in group.GetEntityIds())
            {
                if (entities.TryGetValue(eid, out var e))
                    mass += e.TotalMass;
            }
            if (src != null)
                mass += src.TotalPropellantMass;
            return mass;
        }

        /// <summary>
        /// group 级 dV 显示数据（剩余/最大/工质），供信息面板展示。
        /// 与实体 RemainingDeltaV 同口径：ve·ln(wet/dry)。
        /// </summary>
        public (float remainingDv, float maxDv, float propellantMass, float propellantCapacity) GetGroupDeltaV(SpaceEntityGroup group)
        {
            if (group == null) return (0f, 0f, 0f, 0f);
            var src = GetGroupPropulsionSource(group);
            if (src == null) return (0f, 0f, 0f, 0f);
            if (src.EffectiveVe <= 0f)
                return (0f, 0f, src.TotalPropellantMass, src.TotalPropellantCapacity);
            double dry = GetGroupMass(group, null);
            double wet = dry + src.TotalPropellantMass;
            double wetMax = dry + src.TotalPropellantCapacity;
            float remaining = wet > dry ? (float)(src.EffectiveVe * Math.Log(wet / dry)) : 0f;
            float maxDv = wetMax > dry ? (float)(src.EffectiveVe * Math.Log(wetMax / dry)) : 0f;
            return (remaining, maxDv, src.TotalPropellantMass, src.TotalPropellantCapacity);
        }

        #endregion

        #endregion

        #region 持久化

        public override void LoadedGame()
        {
            base.LoadedGame();
            SpacecraftMapRegistry.Reconcile();
        }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            SpacecraftMapRegistry.Reconcile();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref entities, "entities", LookMode.Value, LookMode.Deep);
            Scribe_Collections.Look(ref groups, "groups", LookMode.Value, LookMode.Deep);
            Scribe_Values.Look(ref nextGroupId, "nextGroupId", 0);
            Scribe_Values.Look(ref nextEntityId, "nextEntityId", 0);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                planetGrids = new Dictionary<int, SpatialGrid3D<ISpatialEntity>>();
                groupLastCoarsePos = new Dictionary<int, DVector3>();
                foreach (var group in groups.Values)
                {
                    var targetGrid = GetGridForGroup(group.GroupId);
                    if (targetGrid != null)
                    {
                        targetGrid.Add(group, group.Position);
                        groupLastCoarsePos[group.GroupId] = group.Position;
                    }
                }
            }
        }

        #endregion
    }
}
