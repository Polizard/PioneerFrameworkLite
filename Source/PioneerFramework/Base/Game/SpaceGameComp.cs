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
using PioneerFramework.Combat;
using MechJebLib.Lambert;

namespace PioneerFramework
{
    public class SpaceGameComp : GameComponent
    {
        /// <summary>
        /// **tick 级日志统一入口**（2026-10-29 用户令"把 tick 日志全关了"）：默认**不发**。
        /// 单步/单次点火级别的探针（`[冲量落地]`/`[物理步输出]`/`[燃烧·诊断]`/`[燃料账]`/`[抛级判据]`）
        /// 全部走这里；要调试时把 `PerfProbe.TickLog` 设 true（调试菜单有开关）。
        /// </summary>
        private static void TickLog(string msg)
        {
            if (PerfProbe.TickLog) Log.Message(msg);
        }

        // ── 轨道实体/群组 ──
        private Dictionary<int, SpaceEntity> entities = new();
        private Dictionary<int, SpaceEntityGroup> groups = new();

        // ── id 分配：持久化单调计数器（A5a）。≤0 = 未初始化 → 首次分配时一次性播种 ──
        private int nextGroupId;
        private int nextEntityId;

        // ── 参考系网格（根行星/恒星ID → 粗网格）──
        private Dictionary<int, SpatialGrid3D<ISpatialEntity>> planetGrids = new();

        /// <summary>
        /// 粗网格中 group 上次同步的位置（每 2 tick SyncGroupToCoarseGrid 维护）。
        /// 圆轨船位置是解析函数（GetCircularPositionAtTick），粗网格若停在入轨点会永久过期，
        /// 定向能 raycast 的 8 大区块粗筛就会漏检已绕出去的船 → 必须定期同步保鲜。
        /// </summary>
        private Dictionary<int, DVector3> groupLastCoarsePos = new();

        private Dictionary<int, object> mapComponents = new();

        // 时间规则：1 tick = 1 秒，每 2 tick 更新一次物理
        internal const int UPDATE_INTERVAL_TICKS = 2;
        private int lastUpdateTick ;

        /// <summary>
        /// **上一次物理步的物理 tick**（2026-10-11）：积分轨（非圆轨）组的 `positionSystem/velocitySystem`
        /// 只在物理步边界被写（每 `UPDATE_INTERVAL_TICKS` = 2 tick 一步）⇒ 表示层要用 `pos + v·Δt` 把它
        /// 外推到当前**渲染时刻**（连续时钟 = `TimeScaleManager.GetRenderTick()`）。
        /// 与射弹那条"渲染位置解析化"是同一手法（那里用 `proj.prevPhysicsTick`，这里用组级统一零点）。
        /// </summary>
        public double LastPhysicsStepTick { get; private set; }

        // PERF-5：物理并行下限——编组数低于此值时顺序执行（线程调度 + 并发容器分配 > 顺序开销）
        // ⚠ `internal`（批 0-4）：规模验收场景必须断言**同一个**阈值——抄一份 32 就是第二份权威。
        internal const int PARALLEL_PHYSICS_MIN_GROUPS = 32;

        /// <summary>
        /// **并行区相位标记**（G1 的第一片，2026-09-26）：`Parallel.For` 前后由**主线程**置位/清位，
        /// 表达"此刻处于并行区相位"。供共享写路径**当场**断言用（见 `ParallelRegionGuard` / `CelestialBodyManager` 的 R1 守卫）。
        /// 为什么不用线程身份判：线程 id 在"组件由谁构造"这类问题上不可靠（实测误报，见该守卫注释）。
        /// 为什么用 `static bool` 而不是 `[ThreadStatic]`：这是**全局相位**，不是"当前线程是不是 worker"——
        /// 由主线程写、工作线程读；bool 读写原子，且**只用于判据**（不进任何计算）。
        /// ⚠ **`volatile` 不是装饰**：它是"跨线程标志"的正确声明 —— 没有它，理论上工作线程可以不观察主线程的写
        ///   （实务上 `Parallel.For` 的派发点有屏障所以能看见，但**判据不许建立在"实务上"之上**）。
        /// </summary>
        internal static volatile bool ParallelPhysicsInProgress;

        // ── 每物理步 scratch 复用（2026-09-10 性能第 2 条：消除每步固定容器分配）──
        // 仅主线程持有与消费；物理并行区只按索引写 physicsResultsScratch（组数与顺序步内稳定）。
        private readonly List<SpaceEntityGroup> movableGroupsScratch = new();
        private GroupPhysicsResult[] physicsResultsScratch = [];
        private readonly List<int> groupsToCheckScratch = new();
        private readonly List<ThrustSlice> steeringSliceScratch = new();

        // ── 组件引用缓存（2026-09-10 性能第 5 条）：GameComponentTick / 每物理步都要查的组件 ──
        private readonly GameComponentRef<TimeScaleManager> timeScaleRef = new();
        private readonly GameComponentRef<SensorManager> sensorMgrRef = new();   // [V3] 认知层
        private readonly GameComponentRef<CombatManager> combatMgrRef = new();
        private readonly GameComponentRef<AutonomousVehicleManager> avmRef = new();
        private readonly GameComponentRef<CelestialBodyManager> celestialRef = new();

        /// <summary>
        /// **带缓存的自身自取**（R5，2026-09-26）：`SpaceEntityGroup`（纯数据类，拿不到 `this`）与其它
        /// 需要"当前 SpaceGameComp"的地方走它 ⇒ 相位里不再做组件表线性扫描（理由同
        /// <see cref="CelestialBodyManager.GetCached"/>：走**既有的** `GameComponentRef`，不新造机制）。
        /// </summary>
        public static SpaceGameComp? GetCached() => selfRef.Get();

        private static readonly GameComponentRef<SpaceGameComp> selfRef = new();

        /// <summary>
        /// 方向感知推进的默认矢量锥半角余弦（cos 10° ≈ 0.9848）。
        /// burn / 手动平移只使用「轴向世界方向与目标方向夹角 ≤ 锥角」的引擎。
        /// </summary>
        public const float DefaultThrustConeCos = 0.984807753f;

        /// <summary>
        /// **无图主推的点火锥**（cos 25°，2026-10-28 用户裁定）：旧口径直接沿用 `DefaultThrustConeCos`（10°）
        /// ⇒ 低推重比 / 大惯量火箭在 10° 外**永不出力**（实测 `主推轴 vs 指令` 21.9°→15.7° 一路收不进去，
        /// 质量冻结在 219 t、罐里还剩 102 t、`切片=0`）。点火锥放宽到 25°，方向大致对就点火，
        /// 残差交给姿态继续收心（真正的对准死区仍是 `DefaultThrustAlignCos`）。
        /// ⚠ 只用于**无图主推**的点火门；有图的 `GetThrustAlong` 仍按 10° 出锥（导弹/炮塔语义不变）。
        /// </summary>
        public const float NoMapIgnitionConeCos = 0.906307787f;   // cos 25°
        /// <summary>姿态"对准完成"阈值（≈1.5° 死区）：主推轴收心到正中才点火/消旋（2026-09-06 收心语义）。
        /// 与 DefaultThrustConeCos（10° 引擎允许窗口）区分：窗口内可出力、收心到死区才点火。</summary>
        public const float DefaultThrustAlignCos = 0.999657325f;

        /// <summary>操舵/自动对准详细日志开关（验证用，默认关；Debug 菜单「PFL 测试」页签切换）。</summary>
        public static bool DebugDetailedSteering ;
        /// <summary>无 Map 主推方向型机动详细日志（D15 新增改动验证）：每物理步输出网选择/对准角/RCS/燃料/燃尽抑制。</summary>
        public static bool DebugDetailedThrust ;

        /// <summary>
        /// **闭式霍曼的影子对照**开关（2026-10-21；**默认关**，Debug 菜单「PFL 测试 → 80 调试日志」切换）。
        ///
        /// 打开后：每次走「霍曼拦截（闭式）」时**再跑一次原搜索**，把 Δv 与点火 UT 的差打一行
        /// `[引导·影子对照]`，用来持续自证"闭式 ≈ 搜索"（2026-10-21 实测 5 次：4 次逐位同、
        /// 1 次 Δv 0.01% / UT 143 s = 2.34% 周期，归因 = e→0.005 档解析模型的固有误差）。
        ///
        /// ⚠ **为什么默认关**（用户 2026-10-21 令："我不允许增加性能消耗了"）：
        /// 每份闭式计划多跑一次搜索 = **1.7–2.7 ms**（该组 120 s 才重规划一次 ⇒ 一局几分钟触发 5–10 次）。
        /// 关着时调用点只多读一个 `static bool` ⇒ 零成本；要证据时在这里手动打开，查完关掉。
        /// </summary>
        public static bool DebugShadowCompareClosedForm;

        /// <summary>
        /// 高频诊断日志的**抽样闸**（2026-10-17；用户令"多探针"的配套：保留读数、掐掉洪泛）。
        ///
        /// ## 治什么（实测账）
        /// 验收/蜂群场景会把「燃烧·头步 / 燃烧·对账 / Thrust / 导弹制导」按物理步刷成十万行量级：
        /// 2026-10-17 一键验收实测 **10 MB / 85 s**（头步 2413 + 对账 2412 + Thrust 2412 + 制导 2240，
        /// 每条还带调用栈）⇒ 撞 `Verse` 的 "max messages limit" 后**日志断流**（验收只剩到 3/8 阶段）；
        /// 且步尖峰细分直接量到 `诊断日志=2000µs / 本步 2419µs`——**日志 I/O 就是尖峰本身**。
        ///
        /// ## 语义（读数能力保留）
        /// **小场景全量**（场景单位数 ≤ 8 ⇒ 单船/小规模调试一条不少）；
        /// **大场景轮转抽样**（`(组号×素数) ⊕ 物理拍` 低 4 位 ⇒ 每 16 拍轮到一次，所有大组都会出现，速率降 16×）。
        /// 只门控**高频诊断**；error/warning 与低频事件（窗口过/收尾/包线/燃尽/再捕获/接管…）不受影响。
        /// </summary>
        internal static bool DiagSamplingAllowed(int id, double physicsTick, int sceneUnits)
        {
            if (sceneUnits <= 8) return true;
            int h = id * 486187739; // 质数散列
            return ((h ^ (int)physicsTick) & 0xF) == 0;
        }

        // 自动对准 / 手动操舵详细日志状态（主线程）
        private readonly Dictionary<int, bool> alignStateByGroup = new();
        private int nextAlignLogTick ;
        private int nextSteerLogTick ;

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
        ///   而冲量是沿 `burnDirectionSystem` 累积的（`pendingImpulse += burnDirectionSystem * dv`）⇒
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

        // ── 交会引导开关（2026-09-17 立；2026-09-28 更正标签与文档）────────────────────
        //   ⚠⚠ **命名债**：这个字段与下面两个常数都还叫 "NewLaw"，但**那套自制远场律
        //   （`RendezvousGuidanceLaw`）已于 2026-09-22 由用户裁定作废**，其远场段 + 5% CW 终端
        //   已从本文件整段删除（见 `:4159` 附近的记录）。现在驱动计划的是**交会阶梯**
        //   （`RendezvousLadder`，日志里打 `[阶梯]` 行）。
        //   字段名暂不改：它被调试页签用**反射字符串**引用（`AddCheckbox(... "UseNewRendezvousLaw")`），
        //   跨文件改名一旦漏同步就会**静默失效**（复选框点了没反应）。要改就一次性改两处并复测。
        /// <summary>
        /// 交会引导开关（**默认开**）：开 = 走阶梯引导；关 = 回退旧路径（Lambert 规划 + 出发段 + 巡航修正）。
        /// 旧路径代码保留，只从调试页签进入，用于对照与回退。
        /// 只影响 <b>精确交会</b>（<c>rendezvousIsApproach=false</c>）；近距离接近保持原引擎
        /// （它的目标是"追上并停下"、限速更温柔，同时还是本路的降级出口）。
        /// </summary>
        public static bool UseNewRendezvousLaw = true;
        // 批5.B（2026-09-28）：此处原有 `NewLawLegSeconds`（旧律的默认腿长 60000 s）——
        // 旧入口撤销后它**零读者**（"腿长的硬下限 = 目标轨道一个周期"这条知识已由阶梯的
        // `MaxPhasingOrbits` / `RendezvousLadder` 自己承载）。
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
        // 批5.B（2026-09-28）：原先这里写着"形状/相位段门槛、腿长系数、腿长安全系数**都定义在纯核心**
        // RendezvousGuidanceLaw"——那套律已归档（`Source/已弃置/`）⇒ **这句话现在是错的**：
        // 那些常数（ShapeGateMps / ShapeExitMps / ShapeLegFactor / LegSafety）随律一起退役，
        // 活路径（阶梯）的门槛与腿长由 `RendezvousLadder.Params` 出。**别再按旧注释去找那四个常数。**

        // ── 地图封存/取档异步操作 ──
        public MapRestoreOperation? ActiveRestoreOperation { get; private set; }
        public MapArchiveOperation? ActiveArchiveOperation { get; private set; }

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
                double d = (g.positionBody - tgt.positionBody).magnitude;
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
                    Log.Message($"[交会·收尾] 距离 < {TerminalPrecisionDist:F0} m ⇒ **自动降档到 1/60×**（物理步 {UPDATE_INTERVAL_TICKS / 60.0:F3} s，每步可给 Δv 约 1.5 m/s；1× 下是 2 s / 88 m/s ⇒ 最后收口没有分辨率）");
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
                Log.Error($"[自检] 点火门({IgnitionAlignGateDeg:F2}°)未严格宽于收心死区({deadzoneDeg:F2}°) ⇒ **船会永远点不着火**。调大 `IgnitionAlignGateDeg` 或调小死区（见 AlignMainAxis）。");
            // ★ 门的**上限**也必须自检（2026-09-21 用户指出"主推带矢量角"）：
            //   门比主推锥窗（`DefaultThrustConeCos`）更宽 ⇒ 门内仍可能一根轴都不在锥内 ⇒ 出力恒 0 ⇒ 白等。
            //   本库取"门 ≡ 锥窗"（门由锥窗推导）⇒ 这条断言是防将来有人把它改宽。
            double coneWindowDeg = Math.Acos(Math.Min(1.0, DefaultThrustConeCos)) * Mathf.Rad2Deg;
            if (IgnitionAlignGateDeg > coneWindowDeg + 1e-9)
                Log.Error($"[自检] 点火门({IgnitionAlignGateDeg:F2}°)比主推锥窗({coneWindowDeg:F2}°)更宽 ⇒ 门内可能一根主推轴都不在锥内、出力恒 0 而船在白等。门应 ≤ 锥窗（见 `GetThrustAlong` 的逐引擎判据）。");
        }

        // ── 地图控件 ──
        public void EnterHiddenMap(int mapId)
        {
            var map = SpaceMapGenerator.FindPocketMap(mapId);
            SpaceMapGenerator.EnterPocketMap(map);
        }

        // ── 网格路由辅助 ──

        /// <summary>获取指定 Group 所属参考系的粗网格。</summary>
        private SpatialGrid3D<ISpatialEntity>? GetGridForGroup(int groupId)
        {
            if (groups.TryGetValue(groupId, out var group) && group.CelestialBodyId >= 0)
            {
                // 地表锚点（星座成员载体）不进任何空间索引：粗网格的唯一消费者是
                // 定向能粗筛 / GetGroupsInRange 的候选预过滤，锚点没有参与资格。
                // 在本方法短路 = RegisterGroup / SyncGroupToCoarseGrid / DestroyGroup 三处一并覆盖。
                if (group.IsSurfaceAnchor || !string.IsNullOrEmpty(group.LandingTaskId)) return null;
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

        // ── 注册/注销 ──

        public void RegisterEntity(SpaceEntity entity)
        {
            if (entity == null) return;
            if (entities.ContainsKey(entity.EntityId))
            {
                Log.Warning($"SpaceEntity {entity.EntityId} already registered.");
                return;
            }

            if (entity.GroupId == 0)
            {
                Log.Error($"Entity {entity.EntityId} has GroupId 0. It must belong to a group before registration.");
                return;
            }

            if (!groups.TryGetValue(entity.GroupId, out var group))
            {
                Log.Error($"Group {entity.GroupId} not found for entity {entity.EntityId}. Registration failed.");
                return;
            }

            if (!group.ContainsEntity(entity.EntityId))
                group.AddEntity(entity.EntityId);

            entities[entity.EntityId] = entity;
            nextEntityId = Math.Max(nextEntityId, entity.EntityId + 1);

            // 2026-10-26：实体入世时补一次零件→推进合成（只补空、只无图；显式推进不覆盖）。
            // 载荷部署走这里（组先注册、实体后注册），所以钩子必须放在实体侧而不只放 RegisterGroup。
            if (!entity.HasPropulsionData && !entity.PropulsionFromParts)
                PartPropulsionBuilder.BuildForMapless(entity);

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
                Log.Warning($"SpaceEntityGroup {group.GroupId} already registered.");
                return;
            }
            groups[group.GroupId] = group;
            nextGroupId = Math.Max(nextGroupId, group.GroupId + 1);

            // 2026-10-26：组生成时**一次**零件→推进合成（只补空、只无图；显式推进不覆盖）。
            // 之后的增删/损毁由 `SpaceEntity.InvalidateStats` 在 `PropulsionFromParts` 为真时重建。
            foreach (int eid in group.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e != null && !e.PropulsionFromParts && !e.HasPropulsionData)
                    PartPropulsionBuilder.BuildForMapless(e);
            }

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
            Log.Message($"[Grid] Group {group.GroupId} registered: bodyId={group.CelestialBodyId} frameId={frameId} 粗网格={(targetGrid != null ? "已入" : group.IsSurfaceAnchor ? "锚点不入" : "无参考系")}");

            // 细网格：注册群组到战斗网格
            var combatGrid = Current.Game?.GetComponent<CombatGrid>();
            if (combatGrid != null && group.IsCollidable)
            {
                combatGrid.AddShip(group);
                // 碰撞扫掠需要"出生位置"作为第一步的上一位置；否则新船首步跨过对方时检测不到。
                // 只在确实入网格时播种，避免"有 lastPos 但不在网格"的错配。
                Current.Game?.GetComponent<CombatManager>()?.NoteGroupSpawned(group);
            }

            if (group.CelestialBodyId >= 0)
            {
                var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>(); // 与全库其它调用点同款判空
                if (celestialMgr != null)
                    celestialMgr.RegisterGroupToBody(group.GroupId, group.CelestialBodyId);
            }
        }

        /// <summary>
        /// **带出生种类的注册入口**（2026-10-31 后端裁定；契约见 `DesignNotes/交接-20261031-出生协议定案.md`）。
        ///
        /// ## 本批（契约批）只做声明与自检，**行为完全等于** <see cref="RegisterGroup(SpaceEntityGroup)"/>
        /// 因为"出生对账"依赖远端的**分段物理时钟**（`PhysicsTimeLine` / `MarkPhysicsEvent`，合并后才有）。
        /// 在没有那个时钟的树上先写对账，只会得到一份拿假时钟自证绿灯的代码
        /// （正是教训"假宿主自测的契约形状必须来自源码"要防的东西）。
        ///
        /// ## 合并后这里的落点
        /// `Emitted` 改为：先入 `pendingBirths`（`groups` 里看不到）→ 等母体状态提交到 `physicsBirthTick`
        /// → 由"局部系偏移 + 相对速度"反推位姿 → 再进 `groups`。`Instantiated` / `Continuation` 路径不变。
        /// </summary>
        /// <param name="kind">出生种类。由出生点声明，不由调用位置推断。</param>
        /// <param name="source">母体（`Continuation` / `Emitted` 必填）：子体状态的唯一出处。</param>
        public void RegisterGroup(SpaceEntityGroup group, GroupBirthKind kind, SpaceEntityGroup? source = null)
        {
#if DEBUG
            ValidateGroupBirth(group, kind, source);
#endif
            RegisterGroup(group);
        }

#if DEBUG
        /// <summary>
        /// Debug 期出生自检（Release 不编入 ⇒ 不动确定性双构建的字节）。
        /// 只报错不改行为：违反契约是**编程错误**，不该被运行期兜底掩盖成"看起来能跑"。
        /// </summary>
        private void ValidateGroupBirth(SpaceEntityGroup group, GroupBirthKind kind, SpaceEntityGroup? source)
        {
            if (group == null) return;

            if (!GroupBirthPolicy.IsDefined(kind))
                Log.Error($"[出生] G{group.GroupId}: 未定义的出生种类 {(int)kind}（只许 Instantiated/Continuation/Emitted；不做兜底）");

            if (GroupBirthPolicy.RequiresSource(kind) && source == null)
                Log.Error($"[出生] G{group.GroupId}: {GroupBirthPolicy.Describe(kind)} 必须给 source（母体），否则子体状态没有出处");

            if (source != null)
            {
                if (source.GroupId == group.GroupId)
                    Log.Error($"[出生] G{group.GroupId}: source 与子组同 id（自指出生）");
                else if (!groups.ContainsKey(source.GroupId))
                    Log.Error($"[出生] G{group.GroupId}: source G{source.GroupId} 不在 groups（母体未注册 ⇒ 子体状态无出处）");
            }

            if (GroupBirthPolicy.RequiresReconciliation(kind))
                Log.Error($"[出生] G{group.GroupId}: Emitted 需要出生对账，本树尚未接入（合并后由 pendingBirths 实现）；合并前请勿使用该种类");
        }
#endif

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
        // 契约改真（2026-10-15 手清）：事件在没有订阅者时本就为 null（消费方是 `?.Invoke` / `+=`/`-=`）
        public event Action<int>? GroupDestroyed;

        /// <summary>Successful landing detaches orbital ownership without destroying source maps.</summary>
        public event Action<int>? GroupLanded;

        internal void RemoveLandingGroupFromIndexes(SpaceEntityGroup group, Landing.LandingSuspensionData? startup = null)
        {
            Current.Game?.GetComponent<CombatGrid>()?.RemoveShip(group);
            foreach (var grid in GetAllGrids()) { grid.Remove(group); grid.ApplyChanges(); }
            groupLastCoarsePos.Remove(group.GroupId);
            if (startup != null && trackedManeuvers.TryGetValue(group.GroupId, out var tracked)) startup.StartupManeuverTracker = tracked;
            trackedManeuvers.Remove(group.GroupId);
            group.GetLinkedMapComp()?.SetThrustersBurning(false);
        }

        internal void FinishLandingGroup(Landing.LandingTaskData task)
        {
            if (!groups.TryGetValue(task.GroupId, out var group)) { task.Cleanup.SpaceRemoved = true; return; }
            RemoveLandingGroupFromIndexes(group);
            Current.Game?.GetComponent<CombatManager>()?.OnGroupDestroyed(group.GroupId);
            if (!task.Cleanup.Notified)
            {
                var errors = new List<Exception>();
                foreach (var handler in GroupLanded?.GetInvocationList() ?? Array.Empty<Delegate>())
                    try { ((Action<int>)handler)(group.GroupId); } catch (Exception e) { errors.Add(e); }
                if (errors.Count != 0) throw new AggregateException(errors);
                task.Cleanup.Notified = true;
            }
            foreach (int id in group.GetEntityIds().ToList())
                if (entities.TryGetValue(id, out var entity)) UnregisterEntity(entity);
            UnregisterGroup(group);
            task.Cleanup.SpaceRemoved = true;
        }

        internal double ConsumeLandingDeltaV(SpaceEntityGroup group, double deltaV)
        {
            var source = GetGroupPropulsionSource(group);
            var estimate = EstimateLandingBurn(group, deltaV);
            if (source == null || !estimate.Feasible || estimate.Networks == null) return 0.0;
            double consumed = 0.0;
            foreach (var network in estimate.Networks)
                if (!network.IsReserve && network.RequiredPropellant > 0f)
                    consumed += source.TryConsumePropellant(network.Propellant, network.RequiredPropellant);
            lastLandingQuotedDeltaV = deltaV;
            lastLandingConsumedKg = consumed;
            Log.Message($"[Landing] Group {group.GroupId} Δv={deltaV:F1} m/s, quoted={estimate.RequiredPropellant:F3} kg, debited={consumed:F3} kg");
            return consumed;
        }

        internal void RestoreLandingTracker(int groupId, Landing.LandingSuspensionData state)
        { if (state.StartupManeuverTracker is ManeuverTracker tracker) trackedManeuvers[groupId] = tracker; }

        internal double LandingRequiredFuel(SpaceEntityGroup group, double deltaV) => EstimateLandingBurn(group, deltaV).RequiredPropellant;

        private BurnEstimate EstimateLandingBurn(SpaceEntityGroup group, double deltaV)
        {
            var source = GetGroupPropulsionSource(group);
            return BurnCalculator.Estimate((float)deltaV, (float)GetGroupMass(group, null), source!, source?.Networks);
        }

        /// <summary>
        /// 销毁群组：从粗细网格移除 → 清理战斗缓存 → 注销实体 → 注销群组。
        /// </summary>
        /// <summary>
        /// 销毁群组：粗/细网格移除 → 战斗缓存清理 → 收尸事件 → 注销实体 → 注销群组 → **处置船上地图**。
        ///
        /// ⚠ **[2026-10-08 用户裁定] 地图处置在**本方法内部**收口**：任何路径销毁一艘"有 Map 的船"，
        /// 它的内景都必须跟着一起收场——否则留下的就是"**孤儿地图**"（建筑/物品/小人静默失联，
        /// 属静默数据损失）。原先只有"坠毁"那一条路做了这件事，而全库有 **31 处** `DestroyGroup` 调用点
        /// （安全接地 / 选中降落 / 战斗损毁 / 星座收尸 / 登船拆除 / 各类清理）⇒ 逐条补一定会再漏第 32 处。
        /// `reason` **只进日志**（"坠毁于地球（北纬…）" / "选中降落" / "战斗中损毁" …），不改行为。
        ///
        /// ⚠ 与**地图归档**不冲突：归档（`BeginArchiveMap`）是把内景写成文件留底，走的是独立的显式 API；
        /// 本方法只处置**在活着的**那张地图 ⇒ 归过档的船即使被销毁，档案还在、仍可取档。
        /// </summary>
        public void DestroyGroup(int groupId, string? reason = null)
        {
            if (!groups.TryGetValue(groupId, out var group))
                return;

            // 0. **先收集"这艘船有哪些地图"**（第 3 步注销实体之后就问不到 MapId 了）
            //    ★ 修复（2026-10-17 批 -1 · 多part 就绪）：除实体级 MapId 外，各 part 的 `linkedMapId`（舱室图）也要收
            //    ——否则销毁一艘多舱船会留下孤儿舱室图（正是本收口点要消灭的静默数据损失）。按 id 去重（单part 时二者同值）。
            List<int>? doomedMapIds = null;
            foreach (int entityId in group.GetEntityIds())
            {
                if (!entities.TryGetValue(entityId, out var e) || e == null) continue;
                // B6：GetMapIds = 全部挂图 part（去重；旧档无 Part→Map 指针时回退 MapId）
                var mapIds = e.GetMapIds();
                for (int i = 0; i < mapIds.Count; i++)
                {
                    int mapId = mapIds[i];
                    if (mapId < 0) continue;
                    doomedMapIds ??= new List<int>();
                    if (!doomedMapIds.Contains(mapId)) doomedMapIds.Add(mapId);
                }
            }

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

            // 6. **船上地图处置**（收口点；放在最后：此刻群组与实体都已注销，没人再指着一张正在拆的地图）
            if (doomedMapIds != null)
                for (int i = 0; i < doomedMapIds.Count; i++)
                    DisposeGroupMap(groupId, doomedMapIds[i], reason);
        }

        public void RegisterMapComponent(int mapId, object mapComp) => mapComponents[mapId] = mapComp;
        public void UnregisterMapComponent(int mapId) => mapComponents.Remove(mapId);
        // 契约改真（2026-10-15 手清）：查不到 / 类型不符都返回 null（两个消费方本就 null 守卫）
        public T? GetMapComponent<T>(int mapId) where T : class => mapComponents.TryGetValue(mapId, out var comp) ? comp as T : null;

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


        // ── 空间站创建与管理 ──

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
            while (groups.ContainsKey(nextGroupId)) nextGroupId++;
            return nextGroupId++;
        }

        /// <summary>分配下一个 entity id（持久化单调计数器，同上）。</summary>
        public int AllocEntityId()
        {
            if (nextEntityId <= 0) nextEntityId = MaxLiveId(entities.Keys) + 1;
            while (entities.ContainsKey(nextEntityId)) nextEntityId++;
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

        // ── 载荷注册表（2026-10-26 用户裁定 A 方案）──
        // 载荷舱组件只存 id；载荷的零件表在这里（随存档落盘）。**载荷无 Map**（同裁定）。
        private Dictionary<int, GroundAssembly.RocketDesignSpec> payloadDesigns = new();
        private int nextPayloadDesignId = 1;

        /// <summary>注册一份载荷设计（深拷随存档；调用方之后改原件不影响已注册条目由 Scribe 深拷保证）。
        /// 返回 id（&gt;0）；`spec == null` ⇒ -1。</summary>
        public int RegisterPayloadDesign(GroundAssembly.RocketDesignSpec? spec)
        {
            if (spec == null) return -1;
            int id = nextPayloadDesignId++;
            payloadDesigns[id] = spec;
            return id;
        }

        /// <summary>按 id 取载荷设计（未注册 ⇒ false）。</summary>
        public bool TryGetPayloadDesign(int id, out GroundAssembly.RocketDesignSpec spec)
        {
            if (payloadDesigns != null && payloadDesigns.TryGetValue(id, out var found) && found != null)
            {
                spec = found;
                return true;
            }
            spec = null!;
            return false;
        }

        /// <summary>下一个可用载荷设计 id（>0）。</summary>
        public int NextPayloadDesignId => nextPayloadDesignId;

        /// <summary>
        /// **释放载荷**（2026-10-26；用户口径：载荷舱销毁自己、原地放出同位置/同速度的新 Group）。
        ///
        /// 顺序：注册表取设计 → 装配（**无舱盒**）→ 新组（逐字段拷贝父组轨道状态）→ 注册实体/组 →
        /// 父舰 `RemovePart(舱)`（正常分离，不走 `MarkPartDestroyed`，不触发漏光/关键件失能）。
        /// 载荷**无 Map**（已裁）。失败返回 null 并给 `reason`（不抛、不静默改状态）。
        /// </summary>
        public SpaceEntityGroup? TryDeployPayload(SpaceEntityGroup parent, SpacePart bayPart, out string reason)
        {
            reason = "";
            if (parent == null || bayPart == null) { reason = "invalid-args"; return null; }

            var bay = bayPart.GetComponent<Components.PayloadBayComponentData>();
            if (bay == null || bay.payloadDesignId <= 0) { reason = "no-payload-design-id"; return null; }
            if (!TryGetPayloadDesign(bay.payloadDesignId, out var spec)) { reason = "payload-design-missing"; return null; }

            SpaceEntity? owner = null;
            foreach (int eid in parent.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e != null && e.GetPartById(bayPart.partId) != null) { owner = e; break; }
            }
            if (owner == null) { reason = "bay-part-not-found"; return null; }

            var parts = GroundAssembly.DesignPartAssembler.BuildParts(
                spec, 1, 1, out _, includeUnknownDefs: false, includeCabin: false);
            if (parts.Count == 0) { reason = "empty-payload"; return null; }

            int gid = AllocGroupId();
            var group = new SpaceEntityGroup(gid, parent.FactionId)
            {
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = parent.CelestialBodyId,
            };
            // 出生状态 = 父组**逐字段拷贝**（位置/速度/姿态/角速度）——用户要求的"速度和原先 Group 一样"。
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = parent.CurrentOrbit.positionSystem,
                velocitySystem = parent.CurrentOrbit.velocitySystem,
                rotation = parent.CurrentOrbit.rotation,
            };
            group.angularVelocity = parent.angularVelocity;
            RegisterGroup(group, GroupBirthKind.Continuation, parent);   // 同体切分：出生状态 = 父组已提交态逐字段拷贝

            var entity = new SpaceEntity(AllocEntityId(), parent.FactionId)
            {
                GroupId = gid,
                HasMap = false,
                IsMovable = true,
                IsAffectedByGravity = true,
                CanInteract = false,
            };
            entity.SetParts(parts);
            RegisterEntity(entity);   // 会把实体加进组，并触发零件→推进合成

            owner.RemovePart(bayPart.partId);   // 载荷舱销毁自己（正常移除）
            return group;
        }

        /// <summary>
        /// 抛级时的**相对分离速度**（m/s，沿"两半质心连线"把被抛级推开）。0 = 只做刚体分裂、不推开。
        /// 真实分离机构量级 0.3–2 m/s；这里取 1.0（2 s 步长 ⇒ 一步分开 2 m）。
        /// </summary>
        internal const double StageSeparationRelativeSpeedMps = 1.0;

        /// <summary>
        /// **按级号抛级**（2026-10-27 用户裁定：不需要分离器部件）。
        ///
        /// 2026-10-30（用户方案）：**两组原点都重定到各自质心，速度按刚体分裂严格算**，见
        /// <see cref="StageSeparationCore"/>：
        /// · 新原点 `P_i = P + ω×0 + R·c_i`、新速度 `V_i = V + ω×(R·c_i)`（`c_i` = 该半质心，体轴、相对旧原点）；
        /// · 再沿 `u = normalize(c_B − c_A)` 施加一对**等大反向中心冲量**（`J = v_rel/(1/m_A+1/m_B)`）
        ///   ⇒ 动量/角动量精确守恒、两边角速度不变；母舰自动得到反向 Δv（按质量分配）。
        /// 口径边界：只管**单实体、非圆轨档**的组（多实体/圆轨档拒绝；见 reason）。
        /// </summary>
        public SpaceEntityGroup? TrySeparateStage(SpaceEntityGroup parent, out string reason)
        {
            reason = "";
            if (parent == null) { reason = "invalid-args"; return null; }
            // 解析档（圆轨）：位置/速度每步由 circular* 重新导出 ⇒ 重定会被下一步覆盖。
            // 2026-10-30 用户裁定：**不拒绝，先把圆轨物化（退出解析档）再分离**。
            if (parent.IsCircularOrbit)
            {
                parent.ExitCircularOrbit();      // 写回当前位置/速度并清 circular* 参数
                if (Prefs.DevMode)
                    Log.Message($"[抛级] G{parent.GroupId} 先退出圆轨解析档（物化位置/速度）再分离");
            }

            SpaceEntity? owner = null;
            int entityCount = 0;
            int max = 0;
            foreach (int eid in parent.GetEntityIds())
            {
                entityCount++;
                var e = GetEntityById(eid);
                if (e == null) continue;
                owner ??= e;
                int m = StagePartition.MaxStage(e.PartsList);
                if (m > max) max = m;
            }
            // 没有可抛的级（含空组）：保持 2026-10-27 的 reason 契约
            if (max <= 0) { reason = "single-stage"; return null; }
            if (owner == null) { reason = "no-entity"; return null; }
            // 本期只做单实体组（多实体要按实体拆/合，另案；硬拆会把两个实体并成一个）。
            if (entityCount > 1) { reason = "multi-entity"; return null; }

            var all = owner.PartsList;

            // 两半的质量/质心（一次遍历；质量口径与 InertiaTensor.Compose 一致）
            var keptMass = new SeparationMass();
            var droppedMass = new SeparationMass();
            StageSeparation.Accumulate(owner, max, ref keptMass, ref droppedMass);
            if (keptMass.mass <= 0.0) { reason = "would-orphan"; return null; }

            // 刚体分裂解算（原点/速度/方向/相对速度）；退化几何在这里被拒（coincident-centers 等）
            if (!StageSeparationCore.Solve(keptMass, droppedMass,
                    parent.CurrentOrbit.positionSystem.v, parent.CurrentOrbit.velocitySystem.v,
                    parent.angularVelocity, parent.CurrentOrbit.rotation,
                    StageSeparationRelativeSpeedMps, out var sol))
            {
                reason = sol.reason;
                return null;
            }

            // 切件（**批量一次失效**）：先把被抛件的坐标重定到子组原点，再一次性移除
            var dropped = new List<SpacePart>();
            var droppedIds = new List<int>();
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null || p.stageId != max) continue;
                dropped.Add(p);
                droppedIds.Add(p.partId);
            }
            if (dropped.Count == 0) { reason = "empty-stage"; return null; }

            for (int i = 0; i < dropped.Count; i++)
                dropped[i].positionShip -= sol.centerB;      // 子组原点 = 被抛级质心
            owner.RemoveParts(droppedIds);
            owner.RecenterParts(sol.centerA);                // 母舰原点 = 保留下来的那半质心

            int gid = AllocGroupId();
            var group = new SpaceEntityGroup(gid, parent.FactionId)
            {
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = parent.CelestialBodyId,
            };
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)sol.originB,
                velocitySystem = (SystemVec3)sol.velocityB,
                rotation = parent.CurrentOrbit.rotation,
            };
            group.angularVelocity = parent.angularVelocity;
            RegisterGroup(group, GroupBirthKind.Continuation, parent);   // 同体切分：位姿由 StageSeparationCore 按父组已提交态解出

            var entity = new SpaceEntity(AllocEntityId(), parent.FactionId)
            {
                GroupId = gid,
                HasMap = false,
                IsMovable = true,
                IsAffectedByGravity = true,
                CanInteract = false,
            };
            // 2026-10-30：**碰撞用实体旋转**（`CollectPartAabbs` 读 `entity.Rotation`），不复制就会错向。
            entity.SetRotation(owner.Rotation);
            entity.SetParts(dropped);
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            RegisterEntity(entity);
            PartPropulsionBuilder.BuildForMapless(entity);   // 碎片也是"真船"：罐里剩的油要算进质量

            // 母舰：新原点/速度 + 诊断重基线 + 清扫掠历史（位置跳变不该被当成一次冲量/一段位移）
            parent.CurrentOrbit.positionSystem = (SystemVec3)sol.originA;
            parent.CurrentOrbit.velocitySystem = (SystemVec3)sol.velocityA;
            parent.burnDiagTick = -1.0;
            Current.Game?.GetComponent<CombatManager>()?.ResetSweepHistory(parent);

            // **抛级后强制按"活动级罐"重建推进池**（2026-10-28 用户裁定）。
            // 实测分叉：抛级后 `网络池=2028 kg` 而 `罐件Σ=102028 kg` ⇒ 上面级明明满油却只肯烧 2 t
            // 接着就是"质量冻结"。这里不给时序留余地：移除完成后对每个母舰实体显式重建一次。
            // ⚠ 2026-10-30：`RecenterParts` 已触发过一次（`InvalidateStats` 收口），这里是**第二次幂等重建**
            //   ——保留它是因为"抛级后必须重建"这条是踩过坑的；一次 O(N) 分组 ~µs，不心疼。
            double poolBefore = 0.0, poolAfter = 0.0, tankAfter = 0.0;
            foreach (int eid in parent.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e == null) continue;
                poolBefore += SumPropulsionMass(e);
                if (!e.PropulsionFromParts && e.propulsion.Count > 0)
                    e.PropulsionFromParts = true;     // 零件合成的船：重建必须真的发生
                PartPropulsionBuilder.RebuildMapless(e);
                poolAfter += SumPropulsionMass(e);
                tankAfter += SumActiveStageTankMass(e);
            }
            if (Prefs.DevMode)
            {
                Log.Message($"[抛级·重建] G{parent.GroupId} 池 {poolBefore:F1} → {poolAfter:F1} kg"
                    + $"｜活动级罐Σ={tankAfter:F1} kg｜剩余件={parent.GetEntityIds().Count} 实体");
                Log.Message($"[抛级·帧] G{parent.GroupId}→G{gid} 母舰 Δpos={sol.centerA.magnitude:F2}m"
                    + $" Δv={DVector3.Cross(parent.angularVelocity, DVector3.Rotate(sol.centerA, parent.CurrentOrbit.rotation)).magnitude:F3}"
                    + $"m/s｜两半质量 {sol.massA:F0}/{sol.massB:F0}kg"
                    + $"｜相对分离速度 {sol.relativeSpeed:F2}m/s（J={sol.impulseJ:F1}N·s）"
                    + $"｜质心 A=({sol.centerA.x:F1},{sol.centerA.y:F1},{sol.centerA.z:F1})"
                    + $" B=({sol.centerB.x:F1},{sol.centerB.y:F1},{sol.centerB.z:F1})");
            }

            return group;
        }

        /// <summary>本实体全部推进网络的料重之和（kg）。</summary>
        private static double SumPropulsionMass(SpaceEntity e)
        {
            double sum = 0.0;
            if (e?.propulsion == null) return sum;
            for (int i = 0; i < e.propulsion.Count; i++)
                if (e.propulsion[i] != null) sum += e.propulsion[i].mass;
            return sum;
        }

        /// <summary>本实体**活动级**（最大 stageId）罐件的燃料之和（kg）——推进池的权威口径。</summary>
        private static double SumActiveStageTankMass(SpaceEntity e)
        {
            if (e == null) return 0.0;
            var parts = e.Parts;
            int active = 0;
            for (int i = 0; i < parts.Count; i++)
                if (parts[i] != null && !parts[i].isDestroyed && parts[i].stageId > active) active = parts[i].stageId;

            double sum = 0.0;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null || p.isDestroyed || p.stageId != active) continue;
                var t = p.GetComponent<Components.PropellantTankComponentData>();
                if (t != null && t.massKg > 0.0) sum += t.massKg;
            }
            return sum;
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
            DVector3 initialPosition, int mapWidth = 60, int mapHeight = 60, string? stationName = null)
        {
            var group = new SpaceEntityGroup(groupId, factionId)
            {
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = 1   // EARTH_BODY_ID，确保空间站放入地球参考系网格
            };
            group.CurrentOrbit = new OrbitState { positionSystem = (SystemVec3)initialPosition, velocitySystem = SystemVec3.zero, rotation = DQuaternion.identity };
            RegisterGroup(group, GroupBirthKind.Instantiated);   // 调试/建站：状态自持，无母体

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
            // [V7] 试验品：站内放一座传感器阵列建筑（每物理步投影进本站 group 的侦测技术）
            SpawnSensorArrayForTest(map);
            // [V7]/[E2] 试验品：再放一座主动雷达建筑（开机 ⇒ 本站更响 = W1 的代价侧）
            SpawnActiveRadarForTest(map);
            // [E3] 试验品：干扰机建筑（群级开关 `JammingEnabled` 默认关 ⇒ 不影响别的读数）
            SpawnJammerForTest(map);

            RegisterEntity(entity);
            group.AddEntity(entityId);

            // 语义确认日志：entity 局部偏移应为 zero（实体在 group 原点），AABB 中心 = groupPos
            Log.Message($"[创建] 空间站 Entity {entity.EntityId} Group {groupId} groupPos=({initialPosition.x / 1000.0:F0},{initialPosition.y / 1000.0:F0},{initialPosition.z / 1000.0:F0})km entityShip=({entity.Position.x / 1000.0:F0},{entity.Position.y / 1000.0:F0},{entity.Position.z / 1000.0:F0})km AABB=({entity.BoundsSize.x:F0}x{entity.BoundsSize.y:F0}x{entity.BoundsSize.z:F0})m");

            return entity;
        }


        // ── 地图封存/取档 ──

        /// <summary>开始封存操作（异步多 tick；单图入口）。</summary>
        public bool BeginArchiveMap(SpaceEntity entity) => BeginArchiveMap(entity, entity?.MapId ?? -1);

        /// <summary>**B6：按 mapId 封存指定的一张图**（多图船逐张封存）。</summary>
        public bool BeginArchiveMap(SpaceEntity entity, int mapId)
        {
            if (entity == null || mapId < 0) return false;
            if (!string.IsNullOrEmpty(GetGroupById(entity.GroupId)?.LandingTaskId)) return false;
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

            var map = SpaceMapGenerator.FindPocketMap(mapId);
            if (map == null) return false;

            var op = MapArchiveManager.BeginArchiveMap(map, entity);
            if (op == null) return false;

            ActiveArchiveOperation = op;
            Messages.Message("开始封存地图...", MessageTypeDefOf.SilentInput, false);
            return true;
        }

        /// <summary>开始取档操作（异步多 tick；单图入口）。</summary>
        public bool BeginRestoreMap(SpaceEntity entity) => BeginRestoreMap(entity, -1, -1);

        /// <summary>**B6：按 mapId/partId 取档**（多图船恢复指定的一张）。</summary>
        public bool BeginRestoreMap(SpaceEntity entity, int mapId, int partId)
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

            var op = mapId >= 0
                ? MapArchiveManager.BeginRestoreMap(entity, mapId, partId)
                : MapArchiveManager.BeginRestoreMap(entity);
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
            StepActiveMapRestore();
        }

        internal bool StepActiveMapRestore()
        {
            if (ActiveRestoreOperation == null) return true;

            bool done = MapArchiveManager.ProcessBuildingBatch(
                ActiveRestoreOperation, MapRestoreOperation.BATCH_SIZE);

            if (done)
            {
                FinalizeRestoreOperation();
            }
            return ActiveRestoreOperation == null;
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
            entity.IsMapArchived = false;

            // B6：优先挂回原 part；原件不在了才自动找空位。
            if (op.TargetPartId >= 0 && entity.GetPartById(op.TargetPartId) != null
                && entity.GetPartById(op.TargetPartId)!.linkedMapId < 0)
                SpaceMapGenerator.LinkEntityToMap(entity, op.NewMap, op.TargetPartId);
            else
                SpaceMapGenerator.LinkEntityToMap(entity, op.NewMap);

            entity.RemoveArchivedMap(op.SourceMapId);
            entity.IsMapArchived = !entity.HasMap;

            // 删除封存文件
            if (op.ArchiveFilePath != null && File.Exists(op.ArchiveFilePath))
                File.Delete(op.ArchiveFilePath);

            Log.Message($"[MapArchive] Restore complete: map {op.NewMap.uniqueID} for entity {entity.EntityId}");
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


        // ── 查询 ──

        public SpaceEntity? GetEntityById(int id) => entities.TryGetValue(id, out var e) ? e : null;   // 契约改真（2026-10-15）：查不到返回 null
        public SpaceEntityGroup? GetGroupById(int id) => groups.TryGetValue(id, out var g) ? g : null;   // 契约改真（2026-10-15）

        /// <summary>
        /// **[V7]/[N7] 建筑级投影**（每物理步一次）：把各地图（=船/站）的 EW 建筑贡献写进其所属 group。
        /// · 归属口径（用户 2026-10-01 裁定）：**地图所属的那条 group**（空间站自己就是一条 group）；
        /// · 贡献已按建筑**毁损**折算（`SpaceMapComp.SensorArrayTech` 逐座乘 HP 比例）；
        /// · 只在**物理步**里调（`UpdateAllMovements` 之后，与其它每物理步消费者同拍）⇒ 读的是当前态。
        /// ⚠ 写成"变了才置脏"的属性（`SpaceEntityGroup.BuildingTechBonus`）⇒ 值没变不会白刷派生缓存。
        /// </summary>
        // ── [V7]/[E3] 建筑级投影 scratch（2026-10-17 批 -1：同组多图 ⇒ 归并后一次写入）──
        // 加性字段求和、波束几何取最大（与 SpaceMapComp 内部合并规则同口径）；每组每步恰好写一次。
        private readonly Dictionary<int, SensorTech> projTechAcc = new();
        private readonly Dictionary<int, RadarCapability> projRadarAcc = new();
        private readonly Dictionary<int, float> projJamAcc = new();

        private void ProjectBuildingDetectTech()
        {
            if (mapComponents.Count == 0) return;

            projTechAcc.Clear();
            projRadarAcc.Clear();
            projJamAcc.Clear();

            foreach (var kv in mapComponents)
            {
                if (!(kv.Value is SpaceMapComp mc) || mc.ownerEntityId < 0) continue;
                if (!mc.SensorsEnabled) continue;   // B6：切断该图的传感器系统
                var entity = GetEntityById(mc.ownerEntityId);
                if (entity == null) continue;
                var group = GetGroupById(entity.GroupId);
                if (group == null) continue;
                int gid = group.GroupId;

                // ★ 修复（2026-10-17 批 -1）：原实现逐图直接赋值 ⇒ 同组多图时**最后一张覆盖其余**；改按组归并。
                var tech = mc.SensorArrayTech();
                if (projTechAcc.TryGetValue(gid, out var tAcc))
                {
                    tAcc.sensitivityMeters += tech.sensitivityMeters;
                    tAcc.memorySeconds += tech.memorySeconds;
                    tAcc.ecmResist += tech.ecmResist;
                    tAcc.eccmResist += tech.eccmResist;
                    projTechAcc[gid] = tAcc;
                }
                else projTechAcc[gid] = tech;

                var radar = mc.RadarCapability();
                if (projRadarAcc.TryGetValue(gid, out var rAcc))
                {
                    rAcc.activeGainBonus += radar.activeGainBonus;
                    rAcc.activeRadiation += radar.activeRadiation;
                    if (radar.beamHalfAngleDeg > rAcc.beamHalfAngleDeg) rAcc.beamHalfAngleDeg = radar.beamHalfAngleDeg;
                    if (radar.beamRangeMeters > rAcc.beamRangeMeters) rAcc.beamRangeMeters = radar.beamRangeMeters;
                    if (radar.scanRateDegPerSec > rAcc.scanRateDegPerSec) rAcc.scanRateDegPerSec = radar.scanRateDegPerSec;
                    projRadarAcc[gid] = rAcc;
                }
                else projRadarAcc[gid] = radar;

                float jam = mc.JammerStrength();
                projJamAcc[gid] = projJamAcc.TryGetValue(gid, out var jAcc) ? jAcc + jam : jam;
            }

            foreach (var kv in projTechAcc)
            {
                var group = GetGroupById(kv.Key);
                if (group == null) continue;
                group.BuildingTechBonus = kv.Value;
                if (projRadarAcc.TryGetValue(kv.Key, out var radarCap))
                    group.RadarCap = radarCap;
                if (projJamAcc.TryGetValue(kv.Key, out var jamAcc))
                    group.BuildingJamBonus = jamAcc;
            }
        }

        /// <summary>
        /// **[V7] 试验用**：往站内地图放一座传感器阵列建筑（Def 缺失时静默跳过）。
        /// 验收三件：① 站 group 的 `DetectTech` 叠加出非零贡献；② 打掉它 ⇒ 贡献掉；③ 存档往返读数不变。
        /// </summary>
        private static void SpawnSensorArrayForTest(Map? map)   // 契约改真（2026-10-15 手清）：实现本就容忍 null（`map == null` 早退）
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail("PF_Building_SensorArray");
            if (def == null || map == null) return;
            IntVec3 cell = CellFinder.RandomClosewalkCellNear(map.Center, map, 8);
            if (!cell.IsValid || !cell.InBounds(map)) cell = map.Center;
            var t = GenSpawn.Spawn(ThingMaker.MakeThing(def), cell, map);
            Log.Message($"[V7] 站内传感器阵列建筑已放置：{def.defName} @ {cell}");
        }

        /// <summary>[E3] 试验用：往站内地图放一座干扰机建筑（Def 缺失时静默跳过；群级开关默认关）。</summary>
        private static void SpawnJammerForTest(Map? map)   // 契约改真（2026-10-15 手清）：实现本就容忍 null（`map == null` 早退）
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail("PF_Building_Jammer");
            if (def == null || map == null) return;
            IntVec3 cell = CellFinder.RandomClosewalkCellNear(map.Center, map, 16);
            if (!cell.IsValid || !cell.InBounds(map) || !cell.Standable(map))
                cell = CellFinder.RandomClosewalkCellNear(map.Center, map, 22);
            if (!cell.IsValid || !cell.InBounds(map)) cell = map.Center;
            GenSpawn.Spawn(ThingMaker.MakeThing(def), cell, map);
            Log.Message($"[E3] 站内干扰机建筑已放置：{def.defName} @ {cell}");
        }

        /// <summary>[V7]/[E2] 试验用：往站内地图放一座主动雷达建筑（Def 缺失时静默跳过）。</summary>
        private static void SpawnActiveRadarForTest(Map? map)   // 契约改真（2026-10-15 手清）：实现本就容忍 null（`map == null` 早退）
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail("PF_Building_ActiveRadar");
            if (def == null || map == null) return;
            IntVec3 cell = CellFinder.RandomClosewalkCellNear(map.Center, map, 12);
            if (!cell.IsValid || !cell.InBounds(map) || !cell.Standable(map))
                cell = CellFinder.RandomClosewalkCellNear(map.Center, map, 18);   // 别和传感器阵列叠在一格
            if (!cell.IsValid || !cell.InBounds(map)) cell = map.Center;
            GenSpawn.Spawn(ThingMaker.MakeThing(def), cell, map);
            Log.Message($"[V7] 站内主动雷达建筑已放置：{def.defName} @ {cell}");
        }
        public IEnumerable<SpaceEntity> GetAllEntities() => entities.Values;
        public IEnumerable<SpaceEntityGroup> GetAllGroups() => groups.Values;

        /// <summary>
        /// 获取指定范围内所有空间群组（遍历所有参考系网格）。
        /// </summary>
        public IEnumerable<SpaceEntityGroup> GetGroupsInRange(DVector3 centerMeters, double radiusMeters, int factionFilter = -1, Func<SpaceEntityGroup, bool>? extraFilter = null)
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


        // ── 集群同步 ──

        /// <summary>
        /// 更新集群轨道，同步所有成员实体位置，并更新粗网格。
        /// </summary>
        public void UpdateGroupOrbit(int groupId, OrbitState newOrbit)
        {
            if (!groups.TryGetValue(groupId, out var group))
                return;

            if (group.CurrentOrbit.positionSystem == newOrbit.positionSystem &&
                group.CurrentOrbit.velocitySystem == newOrbit.velocitySystem &&
                group.CurrentOrbit.rotation == newOrbit.rotation)
                return;

            group.CurrentOrbit = newOrbit;
            group.OrbitDirty = false;

            // 姿态同步：group 旋转 = 全实体旋转（单 part 化，实体无独立姿态）。
            // 碰撞 AABB（GetAabbSystem）与射弹/光束命中（局部系变换）都消费 entity.Rotation，
            // 必须跟随 group 姿态，否则船转了碰撞盒/命中判定不转。
            foreach (int eid in group.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e != null && e.Rotation != newOrbit.rotation)
                    e.SetRotation(newOrbit.rotation);
            }

            // 更新粗网格中的群组位置（统一走 SyncGroupToCoarseGrid，与每 2 tick 同步共用位置追踪，
            // 缓存 last 即粗网格中 group 当前所在格的位置，不会用错误的旧位置移除导致泄漏）
            SyncGroupToCoarseGrid(groupId, newOrbit.positionSystem.v);

            // 注意：entity.Position 是 Entity 在 Group 内的局部偏移量，
            // Group 移动时实体偏移量不变，不要累加 deltaPos。
        }

        /// <summary>
        /// 直接设置实体的位置/速度/旋转。不涉及网格更新（实体不入网格）。
        /// </summary>
        public void MoveEntity(SpaceEntity entity, DVector3 newPositionMeters, DVector3 newVelocityMeters, DQuaternion newRotation)
        {
            if (entity == null) return;
            entity.Position = newPositionMeters;
            entity.Velocity = newVelocityMeters;
            entity.Rotation = newRotation;

            if (entity.GroupId != 0 && groups.TryGetValue(entity.GroupId, out var group))
                group.OrbitDirty = true;
        }


        // ── Tick 更新（物理积分） ──

        public override void GameComponentTick()
        {
            // ★ 2026-10-02：**整 tick 钟**（与"物理步"分开）——来袭场景里"整步周期 700 ms"
            //   必须能分辨"是本组件的 tick 贵"还是"tick 之外"（UI / 渲染 / 其它 comp）。
            long tickClock = PerfProbe.TickClockStart();
            base.GameComponentTick();

            // 后台阶梯 warmup（2026-10-21）：完成一次后在主线程打一行，然后清空（DevMode）。
            if (ladderWarmBg != null)
            {
                if (Prefs.DevMode) Log.Message($"[阶梯·预热·后台] {ladderWarmBg}");
                ladderWarmBg = null;
            }

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
                long blockClock = PerfProbe.ExtraClockStart();   // ★ 2026-10-02：整步块钟（含全部每物理步消费者）
                lastUpdateTick = currentTick;
                // 渲染外推基准（2026-10-11，"别的航天器位置在抖"）：**积分轨**组的位置/速度只在物理步边界更新
                // （每 2 tick 一步）⇒ 表示层在步与步之间要用 `pos + v·Δt` 补平滑；Δt 的零点就是这里。
                // ⚠ 不写 `timeScaleManager != null ? …`：`GameComponentRef.Get()` 的契约是非空
                //   （缺组件时它返回 `null!`、下面 `UpdateAllMovements` 同样会 NRE）——多余的空判反会把
                //   可空流分析变坏（CS8604 假红，2026-10-11 踩过）。
                LastPhysicsStepTick = timeScaleManager.GetPhysicsTick();
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
                DualBodyLaunchAcceptanceRunner.Tick(); // 双天体×10 发爬升验收：空闲时第一行返回
                // 每物理步消费者统一在推进完成后驱动（2026-09-07）：GameComponent tick 顺序按类型
                // 全名字母序（AutonomousVehicleManager → 本组件 → CombatManager），若制导/碰撞各自
                // %2 tick 会有一方读到推进前的旧位置 → 同 tick 位置错位 2s ≈ 5km（导弹制导报 0.7km
                // 命中、碰撞实测 2.6km 的日志实证）。此处推进完毕 = 所有消费者同 tick 同位。
                // ★ 2026-10-02：**顺序固定为 物理 → 能力面 → 认知 → 决策**（[V3] 认知层夹在中间）。
                // [V7]/[N7] 建筑级投影（2026-10-01）：EW 建筑住在 pocket map，comp 不可靠地 Tick
                // ⇒ 由**物理步**把它们的贡献投影进所属 group（设计稿 §5.4 的脊柱：建筑只登记、不自己改 Group）。
                // ⚠ 同日由"消费者之后"提到**之前**：认知层要读它（`DetectTech` / `RadarCap`）；
                //   今天 combat/AVM 都还不读那两个字段 ⇒ 这一步移动对它们是**行为中性**的。
                ProjectBuildingDetectTech();
                // [V3] 认知层（S2）：每物理步无条件刷新 + 状态门控（2026-10-02 裁定 ③）
                long sensorClock = PerfProbe.ExtraClockStart();
                sensorMgrRef.Get()?.OnPhysicsStepCompleted();
                PerfProbe.ExtraClockEnd(PerfProbe.ExtraClockSensor, sensorClock);
                // 决策类消费者（读认知层；S3 才接线，今天仍读真值）
                // ★ 两个消费者各自圈时（来袭场景里"整 tick 192.8 ms"必须能拆开）
                long combatClock = PerfProbe.ExtraClockStart();
                combatMgrRef.Get()?.OnPhysicsStepCompleted();
                PerfProbe.ExtraClockEnd(PerfProbe.ExtraClockCombat, combatClock);
                long avmClock = PerfProbe.ExtraClockStart();
                avmRef.Get()?.OnPhysicsStepCompleted();
                PerfProbe.ExtraClockEnd(PerfProbe.ExtraClockAvm, avmClock);
                Current.Game?.GetComponent<SpaceAI.SpaceAiManager>()?.OnPhysicsStepCompleted();
                // [V7] 验收状态机放在投影**之后**：它读的就是本步刚投影进去的值（不然会差一拍）。
                //
                // ⚠ **[2026-10-08 加固] 这一整段跑批器/框架是"一荣俱荣、一损俱损"的**：它们同处每步块，
                //   且**这一段原本没有任何兜底** ⇒ 任何一处抛异常，后面的跑批器与 `PflTestKit.Tick()`
                //   （异步用例的**超时判定**）会被**一起跳过**；万一是持续性抛（每步都抛），
                //   症状就是"用例冻结：既无结算、也无超时、日志里什么都看不到"（2026-10-08 实测撞到过）。
                //   ⇒ 这里统一兜住 + **把异常打出来**（去重限频）：宁可留一行红字，也不许再静默。
                try
                {
                    SensorProjectionAcceptanceRunner.Tick();
                    // 来袭性能场景（2026-10-02）：空闲时第一行返回（静态 stage 判据）⇒ 零成本
                    SwarmAttackPerfRunner.Tick();
                    // [V3] 认知层验收（2026-10-02 S2）：必须在 `SensorManager` **之后**（读它本步刚写的视图）
                    SensorCognitionAcceptanceRunner.Tick();
                    // 电子战总控（2026-10-02）：按段推进已跑完的验收（空闲时零开销）
                    EwSystemAcceptanceRunner.Tick();
                    AtmosphereAcceptanceRunner.Tick();   // 大气/悬浮/档位（L1）：空闲时第一行返回
                    ComplexCollisionAcceptanceRunner.Tick(); // 多part 复杂碰撞：等撞击记录后自动结算（空闲零成本）
                }
                catch (Exception ex)
                {
                    LogAcceptanceTickException(ex);
                }
                // ⚠ 总控要在**各段自己的 Tick 之后**：它靠"上一段跑完"来起下一段。
                //   （放在本块末尾即可——它自己只做读取与派发。）
                PerfProbe.ExtraClockEnd(PerfProbe.ExtraClockBlock, blockClock);   // 整步块钟闭合
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
                {
                    smc.TickPropellantTankSync();
                    smc.TickSpaceWeapons(currentTick);
                }
            }

            // 游戏内架构验证测试（burn 差值同步/合冲量/推进器状态）——空闲时零开销
            BurnArchitectureTest.Tick();

            // 性能优化验收探针汇总（2026-09-10；开关关闭时仅一次静态 bool 读，内部限频）
            PerfProbe.MaybeLogSummary();

            // 实体注册表一致性（A5b；独立限频 600 tick，不受汇总频率影响）
            ProbeEntityRegistryConsistency();

            // 游戏内一键验收状态机（未运行时立即返回，空闲零开销）
            PerfAcceptanceRunner.Tick();
            PflTestKit.Tick();   // S1 框架：异步段的超时判定（同步用例不受影响）
            AtmosphereAcceptanceRunner.TickAutoRun();   // [A8] 载入自动跑（DevMode + 标记文件；只查一次）
            LandingAcceptanceRunner.Tick();   // S3 判据：撞击事件（异步段；空闲时第一行返回）

            // 调试场景驱动（原 DebugShipSpawner.CompTick：场景自动下令 + 碰撞距离探针）
            DebugShipSpawner.Tick();
            SwarmScaleAcceptanceRunner.Tick();   // 批 0-4：≥32 组可复现性（空闲时第一行返回，零开销）
            PerfProbe.TickClockEnd(tickClock);   // 整 tick 钟（2026-10-02）：与上面的物理步钟配对读数
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
            public DQuaternion NewRotation;
            /// <summary>本步角速度是否由无外力矩物理改写（仅 torqueFreePhysics 演示组）。</summary>
            public bool HasAngularVelocityChange;
            public DVector3 NewAngularVelocity;
            public bool HasActiveBurn;
            public bool BurnExhausted;
            /// <summary>§5（2026-10-26 裁定）：本步燃烧因**欠电**被拒（本步不出力、不耗料；主线程统一熄火）。</summary>
            public bool BurnStarvedNoPower;
            /// <summary>RCS 修正燃烧耗尽时不终结 burn（主网仍有料、可能仍在等对准），避免误终止（D15 弱 RCS）。</summary>
            public bool SuppressAutoBurnEnd;
            // ── D15 无 Map 主推/RCS 详细日志快照（并行区只写，主线程打日志）──
            public bool NoMapThrust;
            public bool UseMainSel;
            public bool UseRcsSel;
            public bool AligningMain;      // 主推型且本步未对准（主网等待）
            public bool RcsFallbackSel;    // 主推已对准但主网尽 → RCS 兜底
            public double MainDot;         // mainAxisSystem·burnDir（已归一；全向=1）
            public double AppliedDv;       // 本步实际 ΔV（m/s）
            /// <summary>本次 tick 应消耗的工质总量（kg），主线程按各网络切片流率比例分配扣除。</summary>
            public double PropellantConsumed;
            /// <summary>
            /// **[A3] 本步进气充填量（kg）**：并行区**只算不写**（读 ρ/空速/罐余量），主线程统一入账
            /// （与 `PropellantConsumed` 同纪律：并行区只累积，主线程才改 `propulsion[].mass`）。
            /// 0 = 无进气道/无空气罐/罐满/不在大气（缺席即零）。
            /// </summary>
            public double IntakeFillKg;
            /// <summary>本次 burn 参与推进的方向切片（并行阶段构建的活动网络快照，含锥内推力/流率）。</summary>
            public List<ThrustSlice> ActiveSlices;
            /// <summary>
            /// **R4（2026-09-26）：并行区里不许打日志**（`Verse.Log` 不是线程安全的，它会写自己的静态容器）
            /// ⇒ 罕见事件的读数由并行体**只写这里**、主线程统一打（与 D15 同形态）。
            /// ⚠ **惰性分配**：只在真要打时才 `new List&lt;string&gt;` —— 并行区零分配那条纪律仍然成立。
            /// ⚠ 无陈旧风险：每个 `ComputeGroupPhysics` 都 `new GroupPhysicsResult{…}`（本字段为 null），
            ///   而主循环只读 `< movableGroups.Count` 那些**本步刚赋值**的槽位。
            /// </summary>
            public List<string>? DiagLines;

            /// <summary>把一行诊断挂到本组本步的结果上（由主线程打）。</summary>
            public void AddDiag(string line)
            {
                if (DiagLines == null) DiagLines = new List<string>(2);
                DiagLines.Add(line);
            }

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
            /// <summary>**本组自己收集**的引力源表（S-a 收尾 2026-09-27）：
            /// 语义已**收窄** —— 它只在"预取表不可用"时（分子步档 / 未预取）作为收集的落地处；
            /// 命中预取时**根本不用它**（相位里改成把共享只读表当入参传下去，零拷贝）。
            /// ⚠ 旧名 `all`（"全量源表"）会让人以为相位里总有一份自己的副本 —— 那是旧设计的说法。</summary>
            public readonly List<GravitySource> own = new();
            /// <summary>轨道保持用的白名单子集（**每组独占**：每次用前 `Clear` 再填）。</summary>
            public readonly List<GravitySource> kept = new();
        }

        private GravityScratch[] gravityScratchByIndex = [];

        // 批7 #6（2026-09-30）：原先这里有一个 `gravityScratchMain`（"主线程路径专用容器"）——
        // 它只被**单组**的 `RebuildCombatCache` 用；那个方法已被 `RebuildCombatCachesBatch` 取代
        // （批版给**每个下标一份** scratch：`GetGravityScratch(k)`）⇒ 本字段随之删除（零调用者）。

        // 批7 #6（2026-09-30）：战斗插值的两张临时表（复用，避免每 tick 分配）
        private readonly List<SpaceEntityGroup> combatRebuildScratch = new();
        private readonly List<(SpaceEntityGroup group, double t)> combatInterpTickScratch = new();

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
        /// <summary>
        /// **S-a 收尾（2026-09-27）：把"本组本步该读哪张引力源表"解析成一次引用选择** ——
        /// 命中预取表 ⇒ 直接返回**共享表**（只读，零拷贝）；否则收集到**本组自己的** `scratch.own` 并返回它。
        /// ⇒ 相位里每群每步只剩"一次字典查 + 一次引用选择"（原来还要把整张表拷一遍 ⇒ 那是随 N 线性的一项）。
        /// ⚠ 共享表只能读（写者只有 `CollectSystemSources`，而它只会被写进本组自己的 list）。
        /// ⚠ 子步档 tick 逐子步变 ⇒ 每次都不命中 ⇒ 自动走"收集"（正确，退回计数也能看见）。
        /// </summary>
        private List<GravitySource> ResolveSystemSources(CelestialBodyManager mgr, SpaceEntityGroup group,
            double tick, GravityScratch scratch)
        {
            // 计时**放在这里**：这是"每群每步"唯一还剩的取表成本（命中 ⇒ 一次字典查 + 引用选择）。
            long scClock = PerfProbe.SystemCollectStart();
            var shared = mgr.TryGetPrefetchedSources(group.CelestialBodyId, tick);
            if (shared != null) { PerfProbe.SystemCollectEnd(scClock); return shared; }
            mgr.CollectSystemSources(group.CelestialBodyId, tick, scratch.own);   // 退回：收集到本组自己的表
            PerfProbe.SystemCollectEnd(scClock);
            return scratch.own;
        }

        private void ComputeOrbitKeepingCorrection(SpaceEntityGroup group, CelestialBodyManager mgr,
            GravityScratch scratch, List<GravitySource> sources, out DVector3 corrAcc, out DVector3 corrJerk)
        {
            corrAcc = DVector3.zero;
            corrJerk = DVector3.zero;

            int centerBodyId = group.CelestialBodyId;
            var whitelist = group.orbitKeepingWhitelist;

            // ① 非白名单天体（除计划中心本身）在当前位的全量项
            scratch.kept.Clear();
            for (int i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                if (s.bodyId == centerBodyId) continue;
                if (whitelist != null && whitelist.Contains(s.bodyId)) continue;
                scratch.kept.Add(s);
            }

            if (scratch.kept.Count > 0)
            {
                // [A6]：掩码同进求和循环（被忽略的天体不在场里 ⇒ 它的扰动也不该被"抵消"）。
                NBodyIntegrator.FieldAccelJerk(group.CurrentOrbit.positionSystem.v, group.CurrentOrbit.velocitySystem.v,
                    scratch.kept, group.ignoredGravityBodyIds, out corrAcc, out corrJerk);
            }

            // ② 计划中心的受迫（自由落体）加速度里、由非白名单父天体贡献的那部分：
            //    逐级取「父对子的拉力，评估在子处」——链到系统外的父天体即止（根域为空）。
            //
            // ⚠⚠ 批5.C 更正（2026-09-29，**真 bug**，用户报障"绕月交会不正常"+ 巡检读数定性）：
            //   这里算出的 `a` 是**中心天体自身的自由落体加速度**（如月球绕地）。
            //   动力学要的是**"中心天体参考系"里的加速度**：`a_ship − a_center` ⇒
            //   **必须把 `a` 从飞船的加速度里减掉**（`freeFallAcc += a`），而 **①（飞船处的真实引力）必须保留**
            //   —— 它减掉后剩下的正是**潮汐项**（那才是中心天体系里的正确动力学）。
            //   旧写法把 ① 也减了（`corrAcc = ① − ②` = **潮汐**），于是：
            //   **潮汐被抵消、均匀项（月球处的地球引力 0.031 m/s² = 月球本地引力的 6.8%）留在动力学里**
            //   ⇒ 月心系里凭空多一项指向地球的加速度 ⇒ 远地侧抬远点、近地侧压低近点
            //   （巡检实测：ApR 3656→5651km、PeR 3303→**335km**、随后坠月 2096 m/s）。
            //   地球场景看不见它，因为"均匀项"与"潮汐"在那个尺度上差着几个数量级。
            //   `tidalAcc/tidalJerk` 仍按**旧口径**（① − ②）输出 ⇒ **燃料结算语义不变**（用户裁定"耗料是设计意图"）。
            int childId = centerBodyId;
            for (int guard = 0; guard < 8; guard++)
            {
                var childBody = mgr.GetBodyById(childId);
                if (childBody == null) break;
                int parentId = childBody.ParentBodyId;
                if (parentId < 0) break;

                if (!NBodyIntegrator.TryFindSource(sources, parentId, out var parentSrc)) break;

                // [A6]：被掩码的天体不进引力场 ⇒ 它在②里的"自由落体项"也不该被减（同一条语义）。
                //   注意是"跳过这一项、继续走链"（祖辈项仍在场里，仍要减）。
                if ((whitelist == null || !whitelist.Contains(parentId)) && !group.IsGravityIgnored(parentId))
                {
                    if (NBodyIntegrator.TryFindSource(sources, childId, out var childSrc))
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
            GravityScratch scratch, List<GravitySource> sources, out DVector3 acc, out DVector3 jerk,
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

            // S-a 子段计时（**只量不改**）：回答"建引力源列表"那一段值不值得加 (system,tick) 缓存。
            // 见 `PerfProbe` 里 S-a 的说明：引力场段占整步可达 28.5%，但其中大头是**对 B 个天体求和**
            // 本身，S-a 只管建列表那一小段 ⇒ 收益必须实测。同一只 Stopwatch 口径 ⇒ 可直接比。
            // S-a 收尾：**表已由调用方解析好**（共享只读表 或 本组自己的 list）⇒ 这里零拷贝。
            // ⚠ 计时**不在这里**：解析（唯一还剩的每群每步成本）在 `ResolveSystemSources` 里计时，
            //   否则这一格会变成"包着一句空话"的假读数（改 S-a 时实测踩过这个形态）。
            if (sources.Count == 0)
            {
                if (timing) PerfProbe.GravityClockEnd(clock);
                return;
            }

            // [A6]：**组引力掩码**在这一处（唯一的场求和）进去——默认 null ⇒ 与旧路径逐位同路；
            //   大气档的非空掩码在求和循环内跳过被忽略天体（"只算中心天体"）。
            NBodyIntegrator.FieldAccelJerk(group.CurrentOrbit.positionSystem.v, group.CurrentOrbit.velocitySystem.v,
                sources, group.ignoredGravityBodyIds, out acc, out jerk);

            if (timing) PerfProbe.GravityClockEnd(clock);

            if (PerfProbe.Enabled) ProbeGravityField(group, mgr, scratch, sources, acc, tick);

            if (!group.HasOrbitKeeping) return;

            // ⚠⚠ 批5.C 回撤记录（2026-09-29 深夜，**我自己的错，已还原**）：
            //   我曾把这里改成"动力学减自由落体项（②）、燃料仍按 ① − ② 结算"，理由是"该减的是自由落体"。
            //   **实测+推演证明那是我推错了**：物理在**世界系**积分，而**中心天体的星历本身就是自由落体轨迹**
            //   ⇒ `acc − (① − ②)` = `月引力@船 + 地球@月`，再减去月球实际加速度（地球@月）⇒ **正好是"月心系两体"** ✓
            //   ⇒ **原写法本来就是对的**（它做的正是"维持圆轨"）；我的改法把自由落体项**减了两次** ✗
            //   （绕月实测：轨迹照旧一路坠月 ⇒ 与"减两次"的推断一致）。
            //   ⇒ **还原原样**。绕月那次坠月的**真正原因仍未定**（下面那支探针继续取证）。
            ComputeOrbitKeepingCorrection(group, mgr, scratch, sources, out keepAcc, out keepJerk);
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
        private void ProbeGravityField(SpaceEntityGroup group, CelestialBodyManager mgr, GravityScratch scratch,
            List<GravitySource> sources, DVector3 acc, double tick)
        {
            bool isSubBodyDomain = mgr.GetReferenceFrameId(group.CelestialBodyId) != group.CelestialBodyId;

            // 根域：热路径（地球系群组多）→ 限频；子天体域：群组稀少（月球系）→ 逐次采样，
            // 保证验收阶段一定拿得到证据，且 min/max 区间更密。
            if (!isSubBodyDomain && !PerfProbe.ShouldProbeGravity(group.GroupId, tick)) return;

            if (!isSubBodyDomain)
            {
                // 根域：与「根 + 直接子天体」子集比较
                scratch.kept.Clear();
                for (int i = 0; i < sources.Count; i++)
                {
                    var s = sources[i];
                    bool directChild = s.bodyId == group.CelestialBodyId
                        || mgr.GetBodyById(s.bodyId)?.ParentBodyId == group.CelestialBodyId;
                    if (directChild) scratch.kept.Add(s);
                }

                NBodyIntegrator.FieldAccelJerk(group.CurrentOrbit.positionSystem.v, group.CurrentOrbit.velocitySystem.v,
                    scratch.kept, out var subsetAcc, out _);

                double diff = (acc - subsetAcc).magnitude;
                PerfProbe.GravityRootDomain(diff < 1e-9,
                    $"G{group.GroupId} 全域与根+直接子天体不一致 Δ={diff:E3} m/s²（天体枚举/位置累加错？）");
                return;
            }

            // 子天体域：差分 = 场 − 计划（唯一口径见 CelestialBodyManager.ComputeFieldMinusPlan）。
            // 注意不能写成"场 − 中心天体单独在场"——那是外面天体的**全量**项（实测 6.86%），
            // 不是潮汐（0.3%–0.6%）。2026-09-14 首轮验收正是这样误报的。
            mgr.ComputeFieldMinusPlan(group.CurrentOrbit.positionSystem.v, group.CurrentOrbit.velocitySystem.v,
                group.CelestialBodyId, sources, out var diffAcc, out _);

            scratch.kept.Clear();
            for (int i = 0; i < sources.Count; i++)
            {
                if (sources[i].bodyId == group.CelestialBodyId) scratch.kept.Add(sources[i]);
            }
            NBodyIntegrator.FieldAccelJerk(group.CurrentOrbit.positionSystem.v, group.CurrentOrbit.velocitySystem.v,
                scratch.kept, out var localAcc, out _);

            PerfProbe.GravitySubBodyDomain(diffAcc.magnitude, localAcc.magnitude);
        }

        /// <summary>
        /// **[交会·轨迹] 读数**（2026-10-01）：把"距目标 / 相对速度"在**点火前**与**烧完**各打一行 ——
        /// 用来**分解**"公里级误差出在起飞/转移侧还是刹车侧"。
        ///
        /// ## 为什么要它
        /// 地球那条（5500→6300 km）的距离在 **3–16 km 反复弹**（88 次拦截、烧干），而**逐个嫌疑人都被读数排除**：
        /// 执行器忠实（按节点配对 99%）、窗口闸 0 次、两个解算器都 1e-8 m、"计划坏"是仪器假红（§10.27）。
        /// 剩下的问题是"**误差在哪一段长出来的**"，而现有日志只有"每个计划之间的距离"，
        /// 分不开"转移没到位"与"刹车没落位"⇒ 这两行就是那把刀（点火前一行、烧完一行，同一口径）。
        ///
        /// ⚠ **纯读数**：不改任何计划/政策；仅 DevMode 下、且只在有交会目标的组上打。
        /// </summary>
        private void LogRendezvousTrack(SpaceEntityGroup group, string phase, double appliedDv = double.NaN)
        {
            if (!Prefs.DevMode || !PerfProbe.TickLog) return;
            if (group.rendezvousTargetId < 0) return;
            if (!groups.TryGetValue(group.rendezvousTargetId, out var tgt) || tgt == null || !tgt.IsActive) return;
            double dist = (tgt.positionBody - group.positionBody).magnitude;
            double rel = (tgt.velocityBody - group.velocityBody).magnitude;
            Log.Message($"[交会·轨迹] G{group.GroupId} {phase}：距目标 {dist:F0} m｜相对速度 {rel:F2} m/s｜已交付 {appliedDv:F1} m/s");
        }

        /// <summary>
        /// **燃烧诊断块（R3+R4 修复，2026-09-26）**：原先是 `ComputeGroupPhysics` 里的一段 —— 也就是
        /// **跑在并行区的工作线程上**，于是有两个病（都在 2026-09-26 规模验收B 里当场看到）：
        ///   **R3** `BurnDiagNextTick` 是**静态读-改-写**当全局节流 ⇒ 谁先写谁就把它推到 +10 s ⇒
        ///        **后读的组被静默跳过**。实测（64 组全烧，每个 10 s 窗口应当诊断 64 个组）：
        ///        **每窗口只有 1 个组**被诊断（赢的是迭代序第一个），其余 63 个一声不响 ⇒
        ///        这套"用来发现燃烧异常"的仪器**自己丢掉了 98.4% 的样本**，而且丢哪些随调度变。
        ///   **R4** 块里 8 处 `Log.Message` 在工作线程上跑（`Verse.Log` 不是线程安全的）。
        /// 搬法：**整块搬到主线程结果应用循环的顶部**（`UpdateAllMovements` 的 `for (ri…)`，
        /// 与既有的 D15「并行区只写 result、主线程打日志」是同一个既定形态）。
        /// **采样点不变（可证）**：并行体不写 `CurrentOrbit`/`positionBody`/`velocityBody`
        /// （它们只在**下一物理步首**的 `ApplyPendingImpulses` 里更新）⇒ 主循环顶部读到的值与
        /// 并行体当时读到的**逐位相同**。
        /// ⚠ 调用位置的约束：必须在主循环**自己的写入之前**（`burnThrustDt`/工质消耗那一批），
        ///   否则读到的 `remainingBurnDV` 已不是并行体看到的那一份。
        /// </summary>
        private void RunBurnDiagnostics(SpaceEntityGroup group, CelestialBodyManager celestialMgr, double physicsTick)
        {
            // R3 的**直接判据面**：本组被诊断过几次。改前 = 64 个组里 1 个是 1、其余 63 个是 0；
            // 改后 = 每个燃烧组每轮都该被诊断（进指纹，且单独当判据用）。
            group.burnDiagRuns++;

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
                if (BurnArc.TryPredictPartial(arcBase, group.burnArcNodeUt, group.burnArcDvAdapter, group.burnArcSeconds,
                        elapsedBurn - UPDATE_INTERVAL_TICKS,
                        group.burnPhaseShift + BurnArc.ExecutionDelaySeconds,
                        out BurnArc.Result pre, out string preWhy))
                {
                    predTxt = $"预测 |r|={pre.achieved.radius:F0}m |v|={pre.achieved.RightHandedOrbitalVelocityAtUT(pre.achieved.epoch).magnitude:F3}m/s";
                    if (pre.finalDir.sqrMagnitude > 0.0)
                        dirTxt = $"模型方向 vs 执行方向={Math.Acos(Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(pre.finalDir,
                                group.burnDirectionSystem)))) * Mathf.Rad2Deg:F2}°";
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
                    (DVector3 rP, DVector3 vP) = MechJebLib.TwoBody.Shepperd.Solve(
                        ovNow.mu, dt, group.burnDiagR, group.burnDiagV);
                    (DVector3 rN, DVector3 vN) = ovNow.RightHandedStateVectorsAtUT(physicsTick);
                    DVector3 dvApplied = vN - vP;
                    _ = rP; _ = rN;
                    double dvCmd = Math.Max(0.0, group.burnDiagRemaining - group.remainingBurnDV);
                    // ⚠ 历史（批4 P4 之前）：这里**必须**把指令换到适配层系再比 —— 两系当时差一次 90° 旋转，
                    //   不换系就恒读 ~88° ✗（同一行里"模型方向 vs 执行方向"换过系 ⇒ 读 2° ✓，那是铁证）。
                    //   **P4 起两系同轴 ⇒ 转换恒等且已收进 `OrbitView` 私有**（批6 S2）⇒ 下面直接传值；
                    //   将来两系若再分离，跨系只改 `OrbitView` 的进/出两个口，别在这里加变换。
                    DVector3 cmdInAdapter = group.burnDirectionSystem;
                    double ang = DVector3.Dot(dvApplied.normalized, cmdInAdapter.normalized);
                    // ★ **冲量的 R/T/N 分解**（2026-09-19 通道扫描的结论）：径向一阶不改 ε/|h|
                    //   （R±10 m/s 都测不出 ✓），**切向灵敏**（T+5 → Δε −0.73%、Δa +14.8 km）。
                    //   游戏残差 = T ≈ +2.8 m/s（≡ 方向朝顺行侧转 ~0.7°）⇒ 必须**逐窗**看 T 的实测 vs 指令，
                    //   才能定位它是"每窗都少一点"还是"某几窗少"。
                    (DVector3 rImp, DVector3 vImp) = ovNow.GameFrameStateAtUT(physicsTick);   // 出边口（批6 S2）
                    DVector3 rhatI = rImp.normalized;
                    DVector3 hhatI = DVector3.Cross(rImp, vImp).normalized;
                    DVector3 thatI = DVector3.Cross(hhatI, rhatI);
                    // ⚠ 历史（批4 P4 之前）：`dvApplied` 在适配层系、`StateAtUT` 给游戏系 ⇒ 必须换一次系再点乘，
                    //   混系的后果 = 恒读 ~87°（"实测 R/T/N 几乎全在法向上"那个假象 —— 我据此一度以为冲量打到面外，
                    //   而 i/LAN 恰恰是精确 0 ⇒ 自相矛盾。第 10 次仪器事故：同一行里两个向量必须来自同一个系）。
                    //   **P4 起两系同轴 ⇒ 无系可混**；出边状态走 `ov.GameFrameStateAtUT`（批6 S2），Δv 直传。
                    double impR = DVector3.Dot(dvApplied, rhatI);
                    double impT = DVector3.Dot(dvApplied, thatI);
                    double impN = DVector3.Dot(dvApplied, hhatI);
                    DVector3 cmdDirSystem = group.burnDirectionSystem * dvCmd;
                    double cmdR = DVector3.Dot(cmdDirSystem, rhatI);
                    double cmdT = DVector3.Dot(cmdDirSystem, thatI);
                    double cmdN = DVector3.Dot(cmdDirSystem, hhatI);
                    // 2026-10-28：`dvCmd` 在"每步重开燃烧"的发射链上恒为 0（`StartBurn` 每步把
                    // `burnDiagRemaining`/`remainingBurnDV` 一起重置）⇒ 旧行永远打"指令 0.000 / 比值 NaN"。
                    // 这里退回**上一步实际命令**并去掉 NaN：有累计命令就用它，没有就打"—"。
                    double cmdShown = dvCmd > 1e-9 ? dvCmd : group.burnLastAppliedDv;
                    string ratioTxt = cmdShown > 1e-9
                        ? (dvApplied.magnitude / cmdShown).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                        : "—";
                    impulseTxt = $"实交 |Δv|={dvApplied.magnitude:F3}（指令 {cmdShown:F3}，比值 {ratioTxt}）｜施加方向 vs 指令={Math.Acos(Math.Max(-1.0, Math.Min(1.0, ang))) * Mathf.Rad2Deg:F1}°｜R/T/N 实测 {impR:F2}/{impT:F2}/{impN:F2} vs 指令 {cmdR:F2}/{cmdT:F2}/{cmdN:F2}（ΔT={impT - cmdT:F3}）";
                }
            }

            // ★ 最后一块拼图：**副本 vs 正本**。探针读 `velocityBody`（每步从 CurrentOrbit 同步的副本），
            //   而 `e` 来自适配层（读 CurrentOrbit 正本）⇒ 若两者不等，"幅值/方向"测的就是副本 ✗
            //   （与今天开头"改了 velocityBody 却没人读"是同一个坑的两面）。
            double vBody = group.velocityBody.magnitude;
            double vCanon = haveOv ? ovNow.RightHandedOrbitalVelocityAtUT(physicsTick).magnitude : double.NaN;
            double rGroup = group.positionBody.magnitude;
            double rCanon = haveOv ? ovNow.RightHandedBCIPositionAtUT(physicsTick).magnitude : double.NaN;

            // ★ 加上"当前质量"：修复（燃耗逐步入账）后它该随工质**下降**；
            //   若它整段燃烧期不动 ⇒ 燃耗没进到质量里 ⇒ `a` 偏大 ⇒ 白送 Δv（2026-09-19 的结论）。
            double dbgMNow = GetGroupMass(group, GetGroupPropulsionSource(group));
            // ★ 本轮起**哪条律在跑**必须自证（否则读日志的人分不清"活差额生效了"与"退路律在跑"）——
            //   这正是游戏内验收要看的第一件事。
            string lawTxt = group.abFreezeDirection ? "冻结(对照)"
                : (group.burnHasTargetV ? "活差额(MJ靶=UT速度)" : "RTN退路");
            // ⚠ 修（2026-09-26）：`已烧` 原来恒打 0 —— `burnActualStartTick` 只在**机动执行器**点火时记，
            //   而"直接 `StartBurn`"（调试场景/台架）没有那一步 ⇒ 两者都读成 0s，与"刚点火"无法区分。
            //   ⇒ 没记点火 tick 时打 `?`，**不许用 0 冒充已知**。
            string elapsedTxt = group.burnActualStartTick >= 0.0
                ? $"{elapsedBurn:F0}" + "s"
                : "?（未记点火 tick：直接 StartBurn）";
            TickLog($"[燃烧·诊断] G{group.GroupId} 已烧={elapsedTxt} tick={physicsTick:F0} 剩余={group.remainingBurnDV:F1}｜律={lawTxt}｜质量={dbgMNow:F1}kg（点火时 {group.burnStartMass:F1}）｜{predTxt}");
            TickLog($"[燃烧·诊断] G{group.GroupId} 副本 vs 正本：|v| {vBody:F6} vs {vCanon:F6}（差 {System.Math.Abs(vBody - vCanon):E3}）｜|r| {rGroup:F3} vs {rCanon:F3}（差 {System.Math.Abs(rGroup - rCanon):E3}）");
            // ★★ **绝对探针**（2026-09-19）：执行器施加的**世界系**方向 vs **计划的世界系向量**。
            //   两者都是惯性系 ⇒ **不受"执行器 vs 自己的输入"那种共模抵消** ⇒ 这才是唯一能看见
            //   "定常面内偏置（≈0.23~0.5°）"的读数（终端真值已从状态侧证实它存在）。
            string absTxt = "绝对=?";
            if (group.burnArcDvAdapter.sqrMagnitude > 0.0)
            {
                // ⚠ `pm` 不在此作用域 ⇒ 用群里存的 **计划 Δv**（两系同轴 ⇒ 直传，批6 S2）
                DVector3 planSystem = group.burnArcDvAdapter;
                double absAng = Math.Acos(Math.Max(-1.0, Math.Min(1.0,
                    DVector3.Dot(group.burnDirectionSystem.normalized, planSystem.normalized)))) * Mathf.Rad2Deg;
                absTxt = $"★绝对：执行方向 vs 计划world向量={absAng:F3}°";
            }
            // ⚠ **修（2026-09-26）**：这一行原先是 **7 个实参对 5 个占位符** ⇒ `{4:F1}` 拿到的是
            //   **`group.GroupId`**（第 5 个实参），"主推轴 vs 指令"那一位**一直在印编号**
            //   （G256 打成 "256.0°"、G384 打成 "384.0°" —— 正因为它等于 id 才被看出来）。
            //   多出来的实参被 `string.Format` 静默忽略 ⇒ 编译器一声不响。**读数说谎**的典型。
            TickLog($"[燃烧·诊断] G{group.GroupId} {impulseTxt}｜{dirTxt}｜{absTxt}｜主推轴 vs 指令={MainAxisBurnAngleDeg(group, group.burnDirectionSystem):F1}°");

            if (haveOv) (group.burnDiagR, group.burnDiagV) = ovNow.RightHandedStateVectorsAtUT(physicsTick);
            group.burnDiagTick = physicsTick;
            group.burnDiagRemaining = group.remainingBurnDV;
        }

        private GroupPhysicsResult ComputeGroupPhysics(SpaceEntityGroup group, double dtSeconds, double physicsTick, CelestialBodyManager celestialMgr, GravityScratch gravityScratch, int subSteps = 1)
        {
            var result = new GroupPhysicsResult { GroupId = group.GroupId };

            // **Landed = 纯解析档**（2026-10-28 用户裁定）：位置/速度由 `StepLandedGroups` 每步解析给出，
            // 这里直接返回 `NeedsUpdate=false` ⇒ 不积分、不燃烧、不查重力场。
            if (group.landed)
                return result;

            // §5（用户 2026-10-26 裁定：欠电 ⇒ 拒绝点火）：本步推进通道拿不到全额电力 ⇒
            // **本步不出力、不耗料**，并且由主线程统一熄火（与"工质尽"同一条退场路径，避免僵尸燃烧）。
            // `PropulsionPowered` 由主线程 `StepPowerThermal` 每步写好；无推进功耗的旧船恒 true。
            bool burnRunning = group.HasActiveBurn && group.PropulsionPowered;
            if (group.HasActiveBurn && !group.PropulsionPowered)
                result.BurnStarvedNoPower = true;

            if (burnRunning)
            {
                // ── 方向与剩余量：**MJ 节点执行器的活差额律**（"MJ 优先"第 5 件，2026-09-20）──
                // 逐拍：把**活轨道**推到节点 `UT`，与**目标速度**（`burnTargetV`）做差 ⇒ 差额既给方向又给剩余量。
                // 史（三层，每层都被实测钉过）：
                //   ① 整段惯性冻结 ⇒ e=8.6e-3（台架）；② 按 2° 节拍刷新（已删）⇒ e=3.8e-3，且**不变量 0.9897
                //   与游戏实测逐位吻合** ⇒ 量化就是那 1% 的来源；③ 每步把"同一个解"在当前 RTN 里重表达 ⇒ e=7.8e-4；
                //   ④ **本轮 = 不再重表达，而是每步重测差额**（解本身随活状态更新）—— 这是 MJ 的做法，
                //   差的那一项正是"重力在燃烧窗口内的贡献"（= 弧补偿一直在追、追不干净的那笔）。
                // **只换方向，不动 remainingBurnDV 记账**。
                // ⚠ **燃烧诊断块已搬到主线程**（R3+R4，2026-09-26）——见 `RunBurnDiagnostics`。
                //   为什么不能留在这里：那块里的静态节流是读-改-写（并行区里"谁先写谁赢"⇒ 其余组被静默
                //   跳过，实测每窗口 64 组只诊断到 1 组），块里那 8 处日志还在工作线程上跑。


                // ★ **每步**重取方向（2026-09-19 台架定量：按 2° 节拍刷新的量化 = e 3.76e-3 vs 每步
                //   7.79e-4，不变量 0.9897 vs 0.9975 —— 游戏实测 0.9897 与之逐位吻合 ⇒ 量化就是残差源）。
                //   这里只是把**同一个解**在当前 RTN 里重新表达，不是重解 ⇒ 没有理由省。
                {
                    // ★★ 2026-09-19 真凶：这里原来用 `positionBody/velocityBody`（**副本**），
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
                    // ⚠ 批6 S3-3d：退回值**改成惰性求值** —— 这段跑在 `Parallel.For` 的**并行体**里，
                    //   而"现算"的 `positionBody` 每次要查一次天体表（R5 意义上能省就省）；
                    //   而且它**只在 `haveArc` 为假时才被用**（为真时紧接着就被出边口覆盖）。
                    DVector3 arcPos = default, arcVel = default;
                    bool haveArc = OrbitAdapter.TryToOrbitView(group, celestialMgr, physicsTick, out OrbitView ovArc);
                    if (haveArc)
                        (arcPos, arcVel) = ovArc.GameFrameStateAtUT(physicsTick + dtSeconds);   // 出边口；落位拍
                    else
                        (arcPos, arcVel) = (group.positionBody, group.velocityBody);            // 退回：天体局部系状态
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
                            result.AddDiag($"[燃烧·窗口] G{group.GroupId} 节点窗口已过（tick={physicsTick:F0} > 截止 {BurnStep.NodeWindowDeadline(group.burnArcNodeUt, group.burnArcSeconds,
                                    BurnNodeWindowPostSeconds):F0}）⇒ **停火**。原因：这一发已作废（节点 UT={group.burnArcNodeUt:F0}、名义燃时={group.burnArcSeconds:F0}s、已烧 {(group.burnActualStartTick >= 0.0 ? physicsTick - group.burnActualStartTick : 0.0):F0}s、已交付 {group.appliedBurnDV:F1} m/s）—— 继续追一个过期靶会无界燃烧（2026-09-21 实测烧掉 4412 m/s / 9080s）");
                            // 本拍**不再刷新方向**（`remainingBurnDV = 0` 已使本拍的
                            // `BurnStep.Compute(aCmd, accel, mfTotal, dt, 0)` 直接返回 exhausted ⇒
                            // 下面统一走 `EndBurn()`，与"烧完"是同一条退场路径）
                        }
                        else
                        {
                        (DVector3 rLiveUt, DVector3 vLiveUt) = ovArc.GameFrameStateAtUT(group.burnArcNodeUt);   // 出边口
                        _ = rLiveUt;
                        // 算式与判据都在纯层（`BurnStep`），台架用**同一份**（唯一算式纪律）
                        BurnStep.DeficitToTargetV(vLiveUt, group.burnTargetV, group.burnDirectionSystem,
                            BurnTargetVFloor, out DVector3 defDir, out double defRem, out bool defRev);
                        group.burnLastDeficit = defRem;
                        group.burnLastVLiveUt = vLiveUt;
                        group.burnLastDeficitDir = defDir;
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
                            result.AddDiag($"[燃烧·收尾] G{group.GroupId} 差额只剩 {defRem:F2} m/s（< {BurnDeficitIgnoreFloor:F1} m/s 地板）⇒ **不再发指令**（方向已进入噪声：追它只会导致 180° 掉头）。已交付 {group.appliedBurnDV:F1} m/s；完成判据接管");
                            defRem = 0.0;   // 让下面的分支走"收工"那一支（不再刷新方向）
                        }
                            if (defRem > 0.0)
                            {
                                group.burnTargetReached = false;
                                group.burnDirectionSystem = defDir;
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
                                group.burnTargetReached = true;
                                group.remainingBurnDV = 0.0;   // 达标或反向 ⇒ 收工（下方 BurnStep 会报 exhausted）
                                group.burnThrottleCap = 0.0;
                                if (defRev) group.burnReversedStopCount++;
                            }
                        }
                    }
                    // ── 退路（**拿不到意图轨道**的燃烧：微调条目、引导逐帧指令）：旧的 RTN 分量律 ──
                    else
                    {
                        // ★ 首次刷新：把**计划的解**（= 当前 `burnDirectionSystem`，`StartBurn` 设的世界方向）在
                        //   这一帧的**正本** RTN 里分解一次。此后整段燃烧只换框架、不换解。
                        //   ⚠ 必须在下面那句**之前**做，且不能在 `StartBurn` 里做 —— 那里拿不到正本
                        //   （副本差一个帧速 ⇒ ~0.13° 方向偏差），而这里若晚一步就会用**上一次燃烧的残留分量**
                        //   去合成方向（= 把刚设好的正确方向覆盖掉）。
                        if (!group.burnComponentsInit)
                        {
                            OrbitMath.DeltaVSystemToComponents(arcPos, arcVel, group.burnDirectionSystem,
                                out group.burnRadial, out group.burnTangential, out group.burnNormal);
                            group.burnComponentsInit = true;
                        }

                        DVector3 arcDir = OrbitMath.GetDeltaVSystem(arcPos, arcVel,
                            group.burnRadial, group.burnTangential, group.burnNormal);
                        if (arcDir.sqrMagnitude > 1e-12 && !group.abFreezeDirection)
                            group.burnDirectionSystem = arcDir.normalized;
                    }
                }

                // 多工质网络并行烧：F = Σ(ve·ṁ)，a = F/m_now，各网络按 ṁ 比例消耗各自工质
                // （质量随工质消耗减小，加速度自然增大，无需对数记账）
                group.ClearAttitudeRequest();   // 2026-10-27：新一步的姿态请求从空开始
                var burnSrc = GetGroupPropulsionSource(group);

                float fTotal = 0f, mfTotal = 0f;
                // 第 3 条：组级 scratch 复用（本组本步独占；下方非 Map 分支是 Add 累加，必须先清空）
                var activeSlices = group.thrustSliceScratch;
                activeSlices.Clear();
                // ⚠ 探针 `PerfProbe.SliceBuffer` **已搬出并行区**（R2，2026-09-26）——见 `UpdateAllMovements`
                //   里"切片 scratch 会计（R2）"那一遍（进并行区之前、单线程）。留在这里会在 32+ 组时
                //   把它的两个静态 Dictionary 用多线程写坏（实测 90 次 AggregateException、**整个物理步被放弃**）。

                if (burnSrc is SpaceMapComp mapComp)
                {
                    if (group.burnUseRcs && mapComp.HasRcs)
                    {
                        // ── 显式 RCS 模式（微调/交会）：任意方向出力、免对准 ──
                        // 2026-10-27：**不跨类回退**——RCS 出力为 0（干了/没有）⇒ 本段燃烧结束，
                        // 由上层重规划（真实航天器不会拿 RCS 去接替主发的活）。
                        mapComp.GetRcsThrustAlong(group.burnEnabledNetworks, activeSlices, out fTotal, out mfTotal);
                        ApplyMapThrustGates(group, celestialMgr, group.CurrentOrbit.positionSystem.v,
                            group.CurrentOrbit.velocitySystem.v, group.GetLinkedMapComp(), dtSeconds,
                            activeSlices, ref fTotal, ref mfTotal);
                        if (fTotal <= 0f)
                            result.BurnExhausted = true;
                    }
                    else
                    {
                        if (!mapComp.HasDirectionalEngines)
                        {
                            // 纯全矢量船（仅虚拟推进/RCS，无 per-axis 引擎）：任意方向出力，免对准
                            mapComp.GetRcsThrustAlong(group.burnEnabledNetworks, activeSlices, out fTotal, out mfTotal);
                            ApplyMapThrustGates(group, celestialMgr, group.CurrentOrbit.positionSystem.v,
                                group.CurrentOrbit.velocitySystem.v, group.GetLinkedMapComp(), dtSeconds,
                            activeSlices, ref fTotal, ref mfTotal);   // [A4]/[A5] 包线 + 进气门
                            if (fTotal <= 0f && mapComp.TotalThrust <= 0f)
                                result.BurnExhausted = true; // 完全没有引擎 → 无法推进，终止
                        }
                        else
                        {
                            // ── 主引擎方向感知：矢量锥内轴向出力 + 自动对准兜底 ──
                            // GetThrustAlong 只取「轴向落在 burnDirectionSystem 矢量锥内」的引擎；
                            // 未对准（锥内无推力）→ 本步只旋转对准（Align 控制器），不推力不耗工质。
                            mapComp.GetThrustAlong(group.burnDirectionSystem, group.CurrentOrbit.rotation, DefaultThrustConeCos,
                                group.burnEnabledNetworks, activeSlices, out fTotal, out mfTotal);
                            ApplyMapThrustGates(group, celestialMgr, group.CurrentOrbit.positionSystem.v,
                                group.CurrentOrbit.velocitySystem.v, group.GetLinkedMapComp(), dtSeconds,
                            activeSlices, ref fTotal, ref mfTotal);   // [A4]/[A5] 包线 + 进气门
                            // 已对准 → 消旋；未对准 → 转向（顺带零掉残余角速度）
                            group.RequestAttitude(AttitudeAxis.MainThrust, group.burnDirectionSystem, 30);
                            if (fTotal <= 0f && mapComp.TotalThrust <= 0f)
                                result.BurnExhausted = true; // 完全没有引擎 → 无法推进，终止
                        }
                    }
                }
                else
                {
                    // ── 无 Map 源：主推方向型（D15）+ 可选弱 RCS 副网，或纯全向 ──
                    // 主网（PropulsionData.isVectoredRcs=false）：主推型沿 mainAxisShip 锥内出力；
                    // RCS 网（isVectoredRcs=true）：全矢量小推力——主推未对准时仍可出力修正、主网尽时兜底。
                    DVector3 mainAxisSystem = GetMainThrustAxisSystem(group);
                    bool isMainThrust = mainAxisSystem.sqrMagnitude > 1e-12;
                    double axisDot = 1.0;
                    if (isMainThrust)
                    {
                        // 姿态控制独立于燃料（魔动量轮不限料）：本步开头执行——
                        // 已收心（axis·burn ≥ cos~1.5°）→ 立即消旋，杜绝残余 ω 让弹体在主推燃烧时漂出；
                        // 未收心 → 全速转向（剩余夹角小于一步转角则降速收敛）。
                        axisDot = DVector3.Dot(mainAxisSystem.normalized, group.burnDirectionSystem.normalized);
                        AlignMainAxis(group, group.burnDirectionSystem, dtSeconds);
                    }
                    // ★ 点火门 = **锥角 10°**（`DefaultThrustConeCos` —— 本文件 65 行的**文档口径**就是
                    //   "窗口内可出力、收心到死区才点火"，`DefaultThrustAlignCos`(1.5°) 只用于消旋/精修）。
                    //   ⚠ 2026-09-19 实测（**代码与自己的文档矛盾**）：这里原来用 1.5° 死区当点火门 ⇒
                    //   质心在死区附近摆动时**整步空转**（不推力不耗料）⇒ 燃烧被拉长（98s→106s）、
                    //   Δv 质心后移。台架定量：起步空转 2 步就吃掉不变量 4.4%（0.9975 → **0.956**），
                    //   与实测 0.9808 同档 ⇒ **这就是"幅值方向都对、轨道就是偏"的最后一环**。
                    // 2026-10-28 二次修正：**锥门只做"点火门"，不做"持续油门门"**。
                    // 实测（23:44 那发）：转平段指令方向一甩，船头滞后到 56°⇒26°，锥门当步就 `切片=0`
                    // ⇒ 船一边转一边掉高度，等到 25° 以内时已经晚了。
                    // 而冲量本来就是沿**指令方向**施加的（`pendingImpulse += burnDirectionSystem * burnDv`），
                    // 姿态只是表现层 ⇒ 已点火的燃烧不再按姿态掐油门；只有"从静止点火"那一下要求进锥。
                    bool mainAligned = !isMainThrust
                        || axisDot >= NoMapIgnitionConeCos
                        || group.HasActiveBurn;

                    // pass1：各类网络可用性（启用 + ṁ>0 + 有料）+ RCS 存量/流率（预判本步是否耗尽）
                    bool[]? netMask = burnSrc != null ? group.GetNetworkMask(burnSrc) : null;
                    bool hasMainFuel = false, hasRcsFuel = false;
                    if (burnSrc != null)
                    {
                        var nets = burnSrc.Networks;
                        for (int ni = 0; ni < nets.Count; ni++)
                        {
                            var n = nets[ni];
                            if (n.MassFlow <= 0f) continue;
                            if (netMask != null && !netMask[ni]) continue;
                            double avail = n.AvailableMass(burnSrc);
                            if (avail <= 0f) continue;
                            if (n.HasRcsEngines)
                            {
                                hasRcsFuel = true;
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
                        // 无图侧选网规则（**选中谁烧谁，不跨类回退**；2026-10-27 收敛）：
                        //   显式 RCS 模式 → 只出 RCS，干了就结束；
                        //   无主推轴       → 全向，全部网络出力；
                        //   主推已对准     → 只出主网，干了就结束；
                        //   主推未对准     → 只转向、不出力（等对准）。
                        bool useMain = false, useRcs = false;
                        if (group.burnUseRcs)
                        {
                            useRcs = hasRcsFuel;
                            useMain = false;
                        }
                        else if (!isMainThrust)
                        {
                            useMain = true;
                            useRcs = true;
                        }
                        else if (mainAligned)
                        {
                            useMain = hasMainFuel;
                            useRcs = false;
                        }
                        else
                        {
                            useMain = false;
                            useRcs = false;
                        }

                        // 显式 RCS 段：RCS 干了 ⇒ 本段结束（不回退主发）。
                        if (group.burnUseRcs && !hasRcsFuel)
                            result.BurnExhausted = true;

                        // 2026-10-27：不再有"RCS 干了但主网还有料 ⇒ 压住自动结束"的补丁——
                        // 选中的机构干了就结束，由上层重规划（真实航天器不跨类接替）。

                        // ── D15 详细日志快照（主线程打 [Thrust]）──
                        result.NoMapThrust = true;
                        result.UseMainSel = useMain;
                        result.UseRcsSel = useRcs;
                        result.AligningMain = isMainThrust && !mainAligned;
                        result.RcsFallbackSel = false;   // 2026-10-27：无跨类回退
                        result.MainDot = isMainThrust
                            ? Math.Max(-1.0, Math.Min(1.0, axisDot))
                            : 1.0;

                        if (burnSrc != null && (useMain || useRcs))
                        {
                            // [A4] 包线上下文（**惰性**：只有真出现"带包线的网络"才算；纯火箭一次都不进）
                            bool aeroCtxReady = false, aeroCtxOk = false;
                            double aeroRho = 0.0, aeroRho0 = 0.0, aeroMach = 0.0;
                            float envFactor = 1f;
                            var nets = burnSrc.Networks;
                            for (int ni = 0; ni < nets.Count; ni++)
                            {
                                var n = nets[ni];
                                if (n.MassFlow <= 0f) continue;
                                // 无工质的网络不是可烧网络：**下一行的 `AvailableMass(null) == 0` 本来就会跳过它**
                                // ⇒ 本行把这件事显式化（零行为差异），顺带把可空性收窄给编译器。
                                if (n.propellant == null) continue;
                                if (netMask != null && !netMask[ni]) continue;
                                if (n.AvailableMass(burnSrc) <= 0f) continue;
                                bool isRcs = n.HasRcsEngines;
                                if ((isRcs && !useRcs) || (!isRcs && !useMain)) continue;
                                // ── [A5] 进气限流（"抽干 ⇒ 熄火"）──────────────────────────────
                                // 本网若声明了附带进气（空气），本步的可用流率还受**空气存量**封顶；
                                // 空气一点没有 ⇒ cap = 0 ⇒ **不建切片**（推力 0、主工质不扣 = 熄火）。
                                // ⚠ 纯火箭（`gasDraws == null`，绝大多数）不进这段：cap 恒 1 ⇒ 下面两行逐位同旧码。
                                float cap = 1f;
                                if (n.gasDraws != null)
                                {
                                    for (int g = 0; g < n.gasDraws.Count; g++)
                                    {
                                        var gd = n.gasDraws[g];
                                        var gdDef = gd.propellant;
                                        if (gdDef == null) continue;   // 构建端已跳过空 Def ⇒ 这里只是收窄可空性
                                        double oneCap = AirBreathingCore.IntakeThrottleCap(
                                            burnSrc.GetPropellantMass(gdDef), dtSeconds,
                                            gd.flowKgPerSec, n.MassFlow);
                                        if (oneCap < cap) cap = (float)oneCap;
                                        if (cap <= 0f) break;
                                    }
                                    if (cap <= 0f)
                                    {
                                        // 熄火（本步）：**不建切片** ⇒ 本网不耗主工质（D4/§2.3 的"熄火期间燃料不扣"）。
                                        // 逐网报一次（首步）——熄火会连报（每步都试）⇒ 用组级一次性标志。
                                        if (!group.intakeFlameoutLogged)
                                        {
                                            group.intakeFlameoutLogged = true;
                                            // ⚠ 空气存量一并报（2026-10-06 首轮实测：空罐起飞第一步必熄火一行，
                                            //   那是**正确**行为 —— 报出 0 kg 才不会被读成故障）。
                                            double airHave = 0.0;
                                            for (int g = 0; g < n.gasDraws.Count; g++)
                                            {
                                                var gdAir = n.gasDraws[g];
                                                if (gdAir.propellant != null) airHave += burnSrc.GetPropellantMass(gdAir.propellant);
                                            }
                                            result.AddDiag($"[大气·进气] G{group.GroupId} 熄火：空气不足（{(n.gasDraws[0].propellant?.defName ?? "?")}，现有 {airHave:F1} kg）⇒ 本步不出力、不耗主工质（t={physicsTick:F0}）");
                                        }
                                        continue;
                                    }
                                }
                                // ── [A4] 包线（密度 × Mach）+ 真空门控 ──────────────────────────
                                // φ 只乘**推力**、不乘流率：低密度 = 同样的油少出力（这正是包线的物理含义；
                                // 流率由"油门"管 —— 上面的 cap 是**空气**给的流率上限，两者各司其职）。
                                // φ == 0（真空里的吸气式）⇒ **不建切片** = D5 的"真空不得路由到吸气式网络"。
                                if (n.envelopeTerms != null)
                                {
                                    if (!aeroCtxReady)
                                    {
                                        aeroCtxReady = true;
                                        aeroCtxOk = TryGetAeroContext(group, celestialMgr,
                                            group.CurrentOrbit.positionSystem.v, group.CurrentOrbit.velocitySystem.v,
                                            out aeroRho, out DVector3 vAirEnv);
                                        aeroMach = aeroCtxOk
                                            ? AirBreathingCore.MachFromAirSpeed(vAirEnv.magnitude)
                                            : 0.0;
                                        // ρ0 取本群中心天体的剖面常数（与 `TryGetAeroContext` 同一个 body）；
                                        // 拿不到（无大气/无中心天体）⇒ 0 ⇒ `ThrustFactor` 判 `ρ0 ≤ 0` 返回 0（保守不路由）。
                                        var envBody = celestialMgr.GetBodyById(group.CelestialBodyId);
                                        aeroRho0 = envBody?.Atmosphere.rho0 ?? 0.0;
                                    }
                                    // 真空（aeroCtxOk == false）⇒ ρ = 0 ⇒ a>0 的项 φ = 0 ⇒ 整网可能归零。
                                    double phi = AirBreathingCore.EnvelopeFactor(n.envelopeTerms, n.Thrust,
                                        aeroRho, aeroRho0, aeroMach);
                                    if (!(phi > 0.0))
                                    {
                                        if (!group.envelopeRoutedOffLogged)
                                        {
                                            group.envelopeRoutedOffLogged = true;
                                            result.AddDiag($"[大气·包线] G{group.GroupId} 网络（{(n.propellant?.defName ?? "?")}）包线因子 0（ρ={aeroRho:F4E2}）⇒ 本步不路由、不耗料（t={physicsTick:F0}）");
                                        }
                                        continue;
                                    }
                                    envFactor = (float)phi;
                                }
                                else
                                {
                                    envFactor = 1f;
                                }
                                activeSlices.Add(new ThrustSlice { network = n, thrust = n.Thrust * cap * envFactor, massFlow = n.MassFlow * cap });
                                fTotal += n.Thrust * cap * envFactor;
                                mfTotal += n.MassFlow * cap;
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
                        group.pendingImpulse += group.burnDirectionSystem * dvAll;   // 节点处 burnDirectionSystem = 计划向量 ✓
                        group.burnLastCmdDv = dvAll;
                        group.remainingBurnDV = 0.0;
                        group.appliedBurnDV += dvAll;
                        result.HasActiveBurn = true;
                        result.BurnExhausted = true;
                        result.PropellantConsumed = 0.0;   // F 行不记账工质（只判几何/映射）
                        result.AppliedDv = dvAll;
                        result.AddDiag($"[对照·F] G{group.GroupId} 单脉冲施加 |Δv|={dvAll:F2} m/s @tick={physicsTick:F0}（节点 {group.abNodeTick:F0}）");
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
                    // 本步燃烧输入的读数快照（逐步指纹把它与 pos/vel 一起哈希 ⇒ 定位到"哪个输入差了"）
                    group.burnDbgF = fTotal;
                    group.burnDbgMf = mfTotal;
                    group.burnDbgMass = groupMass;
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
                            result.AddDiag($"[燃烧] G{group.GroupId} 命令加速度 {aCmd:F3} 超过可用 {accel:F3} m/s²（换网？）⇒ 本步按可用烧");
                        aCmd = accel;
                    }
                    // ★ 头 6 步定点读数（2026-09-19）：实测"执行器头 ~20 s 少交付 ~1.7 m/s，之后追平"
                    //   （模型 vs 实测的曲线在样本 2→3 之间跳 +1.74 ✓）⇒ 只有逐步读数能看见它。
                    //   每轮燃烧只打 6 行，不是散射探针。
                    if (group.burnHeadStepsLogged < 6)
                    {
                        group.burnHeadStepsLogged++;
                        // 抽样闸（2026-10-17）：计数器照走，只门控**打印**（大场景降 16×，小场景全量）。
                        if (DiagSamplingAllowed(group.GroupId, physicsTick, groups.Count))
                            result.AddDiag($"[燃烧·头步] G{group.GroupId} 第{group.burnHeadStepsLogged}步 t={physicsTick:F0}：aCmd={aCmd:F4}｜可用a={accel:F4}｜份额={dvFullSafe(aCmd, accel):F4}｜mass={groupMass:F1}｜fTotal={fTotal:F0}｜网络={activeSlices.Count}");
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
                    group.burnLastAppliedDv = burnDv;
                    if (burnDv > 0.0)
                    {
                        result.HasActiveBurn = true;
                        result.BurnExhausted = bs.Exhausted;
                        // 活靶律的“预算烧完”不等于靶达成：有限燃烧的重力项会让实际节点速度仍有差额。
                        // 靶未达成且有推力 ⇒ 继续，下一步从活状态重算差额；靶已达成/无推力才允许结束。
                        if (group.burnHasTargetV && !group.abFreezeDirection
                            && !group.burnTargetReached && fTotal > 0f)
                            result.BurnExhausted = false;
                        result.PropellantConsumed = bs.Propellant;
                        result.ActiveSlices = activeSlices; // 引用快照，主线程按切片流率比例分配扣除
                        // ⚠ 探针 `SliceAlias` 也搬出并行区（R2，2026-09-26）：它写静态计数 + 可能 `Mismatch`
                        //   （→ 错误日志），而判的是"**快照**必须指回本组自己的 scratch"——快照在并行区
                        //   结束时才存在 ⇒ 判据挪到主线程结果循环（语义不变，见该处注释）。
                        // 冲量累积：并行区只写本 group 独占的累积器，主线程在下一物理步首统一施加
                        // （避免并行区直接改 CurrentOrbit.velocity，消除多冲量顺序依赖）
                        group.burnLastCmdDv = burnDv;   // 逐步对账：本步命令的量
                        group.pendingImpulse += group.burnDirectionSystem * burnDv;
                        group.remainingBurnDV -= burnDv;
                        group.appliedBurnDV += burnDv; // 累计已施加 dV（诊断/日志）
                        result.AppliedDv = burnDv;
                    }
                    else
                    {
                        result.BurnExhausted = true;

                        // 2026-10-28 临时探针 `[燃烧·断油]`（用户实报"质量停在 219 t、还剩 155 t 油"）：
                        // 燃烧中**有网络、有油**却算出 `burnDv = 0` ⇒ 把四个输入一次打全，
                        // 判读：`可用a=0` ⇒ 供油/电门；`fTotal=0` ⇒ 网络没进活动切片（锥角/包线门）；
                        // `剩余=0` ⇒ Δv 预算用尽（正常收工）；`cap=0` 且 `aCmd>0` ⇒ 需求推油门把它们压住了。
                        if (Prefs.DevMode)
                        {
                            group.burnZeroDvSteps++;
                            if (group.burnZeroDvSteps <= 5 || group.burnZeroDvSteps % 50 == 0)
                            {
                                result.AddDiag($"[燃烧·断油] G{group.GroupId} 第{group.burnZeroDvSteps}次零Δv："
                                    + $"aCmd={aCmd:F3} 可用a={accel:F3} fTotal={fTotal:F0}N mf={mfTotal:F1}kg/s "
                                    + $"mass={groupMass:F1} 剩余={group.remainingBurnDV:F1} 油门帽={group.burnThrottleCap:F1} "
                                    + $"网络={activeSlices.Count} 方向模={group.burnDirectionSystem.magnitude:F3} "
                                    + $"电门={(group.BurnIgnitionBlockedByPower ? "断" : "通")} "
                                    + $"控制门={(group.BurnIgnitionBlockedNoControl ? "断" : "通")}");
                            }
                        }
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
                group.RequestAttitude(AttitudeAxis.MainThrust, group.pendingAlignDir, 20);

            // ── 大气段**顺气流指向**（[A6+]，2026-10-06 实测补）────────────────────────────
            // 起因（游戏内实测，Player.log）：测试火箭（[补3] 长棍 3×3×30）**姿态固定在世界系单位阵**
            // （全向船无主推轴 ⇒ 既没有对准语义，爬升又走 RCS 全矢量 ⇒ 全程不对准），于是**侧着飞**：
            // 头锥 9 m² ↔ 侧向 90 m² ⇒ 阻力吃掉大半推力（29 km/747 m/s 处 ≈9.7 m/s²，推力才 21）
            // ⇒ 50 t 烧干在 162 km、到不了 550 km。
            // D13 早有裁定："第一期不做风标力矩（魔法动量轮会瞬间抵消它）⇒ **指向纪律由自动驾驶/制导承担**"
            // ⇒ 这一条就是那条纪律的落点：**制导请求时**（`group.aeroAlignToAirflow`），把**船体前轴**（+z，
            // 也就是气动外形的"头锥"轴）转到**空气相对速度方向**（顺气流 = 最小迎风面积）。
            // ⚠ 与主推轴/矢量锥**无关**：全向船（无主推轴）照样要被指向。
            // ⚠ 放在预对准**之后**：气动不可忽略的空域里，顺气流优先于"对准待点火方向"。
            // [A7] 悬浮时**不**走这条：D10 定的是"悬浮锁铅垂"，两条指向纪律不能同时对船体前轴下手。
            // 2026-10-27：**燃烧期间不走这条** —— 制导的点火方向（低空是径向）与气流方向（切向）
            // 可差 ~90°，两条指向纪律同时写 angularVelocity ⇒ 主推轴永远对不准点火方向 ⇒ 锥角门关闭
            // ⇒ 有油不烧、原地掉高度（地表演示实测）。有推力时"机头沿推力"优先；滑行/再入才顺气流。
            if (group.aeroAlignToAirflow && !group.hoverActive && !group.HasActiveBurn)
            {
                DVector3 alignPos = group.CurrentOrbit.positionSystem.v;
                DVector3 alignVel = group.CurrentOrbit.velocitySystem.v;
                if (TryGetAeroContext(group, celestialMgr, alignPos, alignVel, out _, out DVector3 vAirAlign)
                    && vAirAlign.sqrMagnitude > 1e-6)
                {
                    group.RequestAttitude(AttitudeAxis.Forward, vAirAlign, 25);   // 高于待点火(20)，低于燃烧(30)
                }
            }

            // ── [A7] 悬浮：**船体上轴（+y）= 本地铅垂**（D10；与 SAS 的"主推轴对准"是两根不同的轴）──
            if (group.hoverActive)
            {
                DVector3 upPos = group.CurrentOrbit.positionSystem.v;
                DVector3 upTarget = HoverCore.UpAxisTarget(upPos);
                group.RequestAttitude(AttitudeAxis.Up, upTarget, 50);
            }

            // ── 姿态解析（每步一次，2026-10-27）：优先级最高的请求 → 一次 `AlignShipAxis` ──
            // 玩家手动操舵/SAS 打开时不抢（玩家权威最高；那两条仍走 `ApplySteering`）。
            if (group.attitudePriority > int.MinValue
                && !(SteeringController.IsOpen && SteeringController.GroupId == group.GroupId))
            {
                DVector3 currentAxis = group.attitudeAxis switch
                {
                    AttitudeAxis.MainThrust => GetMainThrustAxisSystem(group),
                    AttitudeAxis.Forward => DVector3.Rotate(DVector3.forward, group.CurrentOrbit.rotation),
                    _ => DVector3.Rotate(DVector3.up, group.CurrentOrbit.rotation),
                };
                if (currentAxis.sqrMagnitude > 1e-12)
                    AlignShipAxis(group, currentAxis, group.attitudeTargetSystem, dtSeconds);
            }

            // 引力场采样：非圆轨需要它做积分；圆轨（解析推进）只有在轨道保持需要结算 dV 时才需要。
            // 保持旧成本画像——圆轨且无轨道保持的群组不做任何引力计算。
            var scratch = gravityScratch;
            bool needField = !group.IsCircularOrbit || group.HasOrbitKeeping;
            DVector3 fieldAcc = DVector3.zero, fieldJerk = DVector3.zero;
            DVector3 keepAcc = DVector3.zero, keepJerk = DVector3.zero;

            // S-a 收尾：**本步的引力源表在这里解析一次**（共享只读表 或 本组自己的 list）——
            // 只解析一次的原因：主路径与下面两处 `.Count` 判断、以及子步档都要用同一份来源；
            // 子步档的 tick 逐子步不同 ⇒ 那时在循环内逐子步重解析（会走"收集"分支，正确）。
            List<GravitySource>? sources = null;
            if (needField)
            {
                sources = ResolveSystemSources(celestialMgr, group, physicsTick, scratch);
                ComputeGroupFieldAccelJerk(group, celestialMgr, physicsTick, scratch, sources,
                    out fieldAcc, out fieldJerk, out keepAcc, out keepJerk, timeSegment: true);
            }

            // 轨道保持的 dV 结算：**两条推进路径共用**（圆轨走解析推进，但其"抵消非白名单天体扰动"
            // 的燃料开销与积分路径同源；旧实现同样对圆轨结算，勿因重构而漏掉）
            if (group.HasOrbitKeeping && sources != null && sources.Count > 0)
            {
                result.HasOrbitKeeping = true;
                result.OrbitKeepingDvCost = keepAcc.magnitude * dtSeconds;
            }

            // ── [A8] **解析档悬浮**：位置/速度由**定点解析**给出（不积分、不受力），**照扣料** ──
            //   形状与解析圆轨完全一样（本库"位置解析、耗料照算"跑了整整一年的那条先例，D8 的出处）。
            //   耗料在主线程 `ApplyHover` 里按 `m·dv/ve` 结算（与轨道保持同一个口径）。
            // ── [D7②] **交战降级**（用户 2026-10-08 裁定：**不新建"交战触发"**，用现成的"有射弹"信号，
            //    信号 = **既有的唯一口径** （见 `CombatActionActive`）——
            //    即"有射弹在飞 **或** 导弹处于终末攻击"，与"子弹时间"降速用的是同一份判据。
            //    解析档（轨上/定点解析）**不模拟受力** ⇒ 交战中意味着"免疫扰动"，那是不该有的特权
            //    ⇒ 一出现交战动作就**自动降级到物理档**（D7 的不对称原则：降级只加精度，**可自动**）。
            //    ⚠ 信号是**全局**的（任何地方在打都算）——按用户裁定刻意取的粗口径；
            //      若要"只对附近交火降级"，加局部性判据（射弹到本组的距离）即可，另案。
            if (group.hoverActive && group.hoverAnalytic && CombatActionActive)
            {
                Log.Message($"[档位] G{group.GroupId} **交战动作出现 ⇒ 自动退出解析档**（降级只加精度、可自动）｜定点不变，改由物理档保持");
                SwitchHoverTier(group, analytic: false);
            }

            if (group.transferActive)
            {
                // [A8] 解析搬运：位置/速度由**解析路径**给（大圆 + 半径线性），不走积分、不受力。
                double u = AnalyticTransferCore.Progress(physicsTick - group.transferStartTick, group.transferDuration);
                result.NeedsUpdate = true;
                result.NewPosition = AnalyticTransferCore.PositionAt(group.transferFromPos, group.transferToPos, u);
                result.NewVelocity = AnalyticTransferCore.VelocityAt(group.transferFromPos, group.transferToPos, u,
                    group.transferDuration);
            }
            else if (group.hoverActive && group.hoverAnalytic)
            {
                var hvBody = celestialMgr.GetBodyById(group.CelestialBodyId);
                if (hvBody != null)
                {
                    double hvOmega = Launch.GetSurfaceState.RotationOmega(celestialMgr, group.CelestialBodyId);
                    var (hvPos, hvVel) = HoverCore.TargetState(hvBody.radius, group.hoverLatRad, group.hoverLonRad0,
                        group.hoverAltitudeMeters, hvOmega, physicsTick);
                    result.NeedsUpdate = true;
                    result.NewPosition = hvPos;
                    result.NewVelocity = hvVel;
                }
            }
            else if (group.IsCircularOrbit)
            {
                DVector3 newPos = group.GetCircularPositionAtTick(physicsTick);                // 圆轨下速度是随相位旋转的解析函数：同步 CurrentOrbit.velocity，
                // 使预览/机动规划/交会等所有读 velocity 的消费方得到当前相位正确值
                // （否则它们会拿到入轨瞬间的初始切线速度，导致轨道被算成漂移的椭圆）。
                DVector3 newVel = group.GetCircularVelocityAtTick(physicsTick);
                if (newPos != group.CurrentOrbit.positionSystem.v)
                {
                    result.NeedsUpdate = true;
                    result.NewPosition = newPos;
                    result.NewVelocity = newVel;
                }
            }
            else if (sources != null && sources.Count > 0)
            {
                // ── 引力场（A0）：系统系内**全部天体**全量求和，无"环绕天体"参量 ──
                // 坐标 = CurrentOrbit.position（系统系 = 根行星中心，与场同系）→ 不再做任何帧转换。
                // 子天体为中心时（如月球），由场自动带上根天体的贡献（等价于"局部系 + 潮汐项"），
                // 不再需要旧的"减天体位置 / 加回天体位置"两段式。
                DVector3 pos = group.CurrentOrbit.positionSystem.v;
                DVector3 vel = group.CurrentOrbit.velocitySystem.v;

                if (subSteps <= 1)
                {
                    // [A2-2] 气动（阻尼）：与引力同为"合力"的一部分；域外/真空 ⇒ **精确零**
                    //   （三道"缺席即零"早退见 ComputeGroupAeroAccel）。jerk 不含气动项（局部带估算，见 AeroDrag.Accel）。
                    // ⚠ 零气动时**不加**（`fieldAcc + 零` 会把 −0.0 归一成 +0.0）⇒ 无气动的世界与旧码逐位一致。
                    DVector3 aeroAcc = ComputeGroupAeroAccel(group, celestialMgr, pos, vel);
                    DVector3 totalAcc = aeroAcc.sqrMagnitude > 0.0 ? fieldAcc + aeroAcc : fieldAcc;
                    NBodyIntegrator.ThirdOrderTaylorStep(pos, vel, totalAcc, fieldJerk, dtSeconds,
                        out var newPos, out var newVel);

                    // [A3] 进气充填（只算不写）：与气动**同一次上下文**（ρ、v_air 同口径），
                    //   量记进结果，主线程统一入账（并行区不许改 `propulsion[].mass`）。
                    result.IntakeFillKg = ComputeGroupIntakeFill(group, celestialMgr, pos, vel, dtSeconds);

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
                        // 子步：tick 变了 ⇒ 共享表不适用（`ResolveSystemSources` 会走收集分支）
                        var subSources = ResolveSystemSources(celestialMgr, group, physicsTick + i * subDt, scratch);
                        ComputeGroupFieldAccelJerk(group, celestialMgr, physicsTick + i * subDt, scratch, subSources,
                            out var acc, out var jerk, out _, out _, timeSegment: true);
                        // [A2-2] 子步档：气动按**子步当前** (pos, vel) 重算（ρ 与 v_air 都随步内演化）；零时同上门。
                        DVector3 subAero = ComputeGroupAeroAccel(group, celestialMgr, pos, vel);
                        DVector3 subAcc = subAero.sqrMagnitude > 0.0 ? acc + subAero : acc;
                        NBodyIntegrator.ThirdOrderTaylorStep(pos, vel, subAcc, jerk, subDt, out pos, out vel);
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

            // ── 姿态积分（魔法动量轮 / 演示用无外力矩物理）──
            // 角速度 → rotation 增量；大步长（物理步 2s）下按角度限幅分子步，避免肉眼可见跳变。
            // 并行区只计算新姿态，主线程在结果应用时统一写 CurrentOrbit.rotation。
            // torqueFreePhysics（仅贾尼别科夫演示组）：用多part惯量张量按 Euler 方程演化 ω，再积分姿态；
            // 不手写旋转，翻转是方程自然结果。默认 false ⇒ 生产路径不变。
            DQuaternion tfRot = default;
            DVector3 tfOmega = default;
            bool torqueFree = group.torqueFreePhysics
                && TorqueFreeRotation.Step(group, dtSeconds, out tfOmega, out tfRot);
            if (torqueFree)
            {
                result.HasAngularVelocityChange = true;
                result.NewAngularVelocity = tfOmega;
                result.HasRotationChange = true;
                result.NewRotation = tfRot;
                result.NeedsUpdate = true;
            }
            else if (group.angularVelocity.sqrMagnitude > 1e-12)
            {
                double w = group.angularVelocity.magnitude;
                DVector3 axis = group.angularVelocity / w;
                double totalAngle = w * dtSeconds;
                int steps = Math.Max(1, (int)Math.Ceiling(totalAngle / (10f * Mathf.Deg2Rad)));
                steps = Math.Min(steps, 32);
                DQuaternion rot = group.CurrentOrbit.rotation;
                DQuaternion stepRot = DQuaternion.AngleAxis(totalAngle / steps, axis);
                for (int i = 0; i < steps; i++)
                    rot = stepRot * rot;
                result.HasRotationChange = true;
                result.NewRotation = rot;
                result.NeedsUpdate = true;
            }

            result.SyncBodyPos = GetBodyOffsetSystem(group.CelestialBodyId, (int)System.Math.Round(physicsTick)).v;
            result.SyncBodyVel = GetBodyVelocityOffsetSystem(group.CelestialBodyId, (int)System.Math.Round(physicsTick)).v;

            return result;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  气动（[A2-2]，2026-10-06）：唯一消费者 = ComputeGroupPhysics 的力总和
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>[补1] "不许静默零阻力"的一次性告警闸（并行区里只读）。</summary>
        private static int aeroShapeWarned;

        /// <summary>
        /// 群级气动外形（[A2-2] 的无 Map 生产者；**有 Map 组走同一盒模型的显式 fallback** —— [补1]：
        /// map 组 part 的 `boundsSizeShip` = 地图足迹 (宽, 50, 高)，见 `SpaceMapGenerator`）。
        /// 逐 part 求盒模型三轴 `(Cd·A)` 再**相加**（不建模遮挡）——L1 的 LOD 就是"把外形降成三个数"。
        /// **B8 明示口径：每件独立、无遮挡、无串列屏蔽**；遮挡模型单列后置（会改变存量船阻力，
        /// 必须另走裁定，不随本批偷偷生效）。损毁件不再贡献气动面积。
        /// 并行区只读调用：只遍历实体/部件表，无分配、无写。
        /// </summary>
        private AeroShape GetGroupAeroShape(SpaceEntityGroup group)
        {
            // **2026-10-28：整船机体包围盒 → 一次盒模型**（用户裁定"面积改投影轮廓"）。
            // 旧实现把每件的 CdA **逐轴相加**：竖着飞的「4×40 罐 + 12 台 2×3 引擎」在推力轴上的
            // CdA 会累到 232 m²，而真实迎风面只有最大截面 16 m² ⇒ 阻力虚高 10 倍以上
            // （用户实报"我们的空气阻力是不是也比地球大很多"）。改成包围盒后：
            // · 堆叠火箭 = 真实的"最大截面 × 长度"，恰好正确；
            // · 侧向 = 整船轮廓，比逐件求和更接近（不再把被遮挡的零件重复计面积）；
            // · 代价：L 形/带外伸支架的异形船会被包围盒**高估**（空当也算面积）——已与用户确认接受。
            // 成本：仍是 O(零件数)，但只在**每条数据只算一次**，比原来每件 3 次乘法更省。
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
            foreach (int eid in group.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e == null) continue;
                var parts = e.Parts;
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    if (p.isDestroyed) continue;
                    var half = p.boundsSizeShip * 0.5;
                    minX = System.Math.Min(minX, p.positionShip.x - half.x);
                    minY = System.Math.Min(minY, p.positionShip.y - half.y);
                    minZ = System.Math.Min(minZ, p.positionShip.z - half.z);
                    maxX = System.Math.Max(maxX, p.positionShip.x + half.x);
                    maxY = System.Math.Max(maxY, p.positionShip.y + half.y);
                    maxZ = System.Math.Max(maxZ, p.positionShip.z + half.z);
                }
            }

            // [补1] 纪律：不许静默零面积 ⇒ 没有可测零件时给显式 fallback（1 m² 的球）。
            if (!(maxX > minX) || !(maxY > minY) || !(maxZ > minZ))
                return AeroShape.Isotropic(1.0, 1.0);

            return AeroShape.FromBox(new DVector3(maxX - minX, maxY - minY, maxZ - minZ));
        }

        /// <summary>
        /// **气动上下文**（[A6+] 2026-10-06 从 `ComputeGroupAeroAccel` 抽出的唯一实现）：
        /// 三道"缺席即零"早退（成本从低到高）之后给出 ρ 与**空气相对速度** v_air。
        /// ① 全库一份活大气都没有 ⇒ 一次静态 bool 读即返回；
        /// ② 本群中心天体无活剖面（ρ≡0 / 无大气）⇒ 返回；
        /// ③ **域外一次标量比较**（r ≥ R + atmoTop）⇒ 返回。
        /// 两个使用者：**阻力**（`ComputeGroupAeroAccel`）与**顺气流指向**（物理步的姿态纪律）。
        /// ⚠ 并行区只读调用；天体剖面已由 `CelestialBodyManager.EnsureInitialized` 预暖（建表是分配）。
        /// </summary>
        private bool TryGetAeroContext(SpaceEntityGroup group, CelestialBodyManager mgr,
            in DVector3 pos, in DVector3 vel, out double rho, out DVector3 vAir)
        {
            rho = 0.0;
            vAir = DVector3.zero;

            if (!AtmosphereModel.AnyAtmosphereActive) return false;                            // ①

            var body = mgr.GetBodyById(group.CelestialBodyId);
            if (body == null) return false;
            var atmo = body.Atmosphere;
            if (!atmo.IsActive) return false;                                                  // ②

            double r = pos.magnitude;
            if (!(r < body.radius + body.atmosphereThickness)) return false;                   // ③

            rho = atmo.Density(r - body.radius);
            if (!(rho > 0.0)) return false;

            double omega = Launch.GetSurfaceState.RotationOmega(mgr, group.CelestialBodyId);
            vAir = vel - SurfaceFrame.SurfaceVelocity(pos, omega);
            return true;
        }

        /// <summary>
        /// 气动加速度（系统系；[A2-2]，计划稿 §2.2）。消费 `AeroDrag.Accel`（唯一算式）；
        /// 三道"缺席即零"早退在 `TryGetAeroContext` 里（这里只再判外形）。
        /// ⚠ 解析圆轨（`IsCircularOrbit`）走不到这里（圆轨是解析推进、不积分）——"解析轨与气动互斥"
        ///   那条规则由 **[A6]** 的档位守卫负责（此处不预埋）。
        /// </summary>
        private DVector3 ComputeGroupAeroAccel(SpaceEntityGroup group, CelestialBodyManager mgr,
            in DVector3 pos, in DVector3 vel)
        {
            if (!TryGetAeroContext(group, mgr, pos, vel, out double rho, out DVector3 vAir))
                return DVector3.zero;

            var shape = GetGroupAeroShape(group);
            if (shape.IsDegenerate)
            {
                // [补1]：不许静默"零面积/零阻力"——生产者喂了退化外形（部件无 bounds）是数据事故，报一次。
                if (System.Threading.Interlocked.CompareExchange(ref aeroShapeWarned, 1, 0) == 0)
                    Log.Error("[大气·气动] 群 " + group.GroupId + " 外形退化（三轴 Cd·A 有 ≤0 项）⇒ 本步不产生阻力。" +
                        "查部件 boundsSizeShip；[补1]：有 Map 组也要有显式 fallback，不许静默零面积。" +
                        "（本条只报一次，后续同类不再刷屏）");
                return DVector3.zero;
            }

            DQuaternion rot = group.CurrentOrbit.rotation;
            DVector3 axisFwd = DVector3.Rotate(DVector3.forward, rot);
            DVector3 axisLeft = DVector3.Rotate(DVector3.left, rot);
            DVector3 axisUp = DVector3.Rotate(DVector3.up, rot);
            double mass = GetGroupMass(group, GetGroupPropulsionSource(group));
            return AeroDrag.Accel(rho, vAir, shape, axisFwd, axisLeft, axisUp, mass);
        }

        /// <summary>
        /// **进气道工质 Def 的一次解析缓存（[A3]）**：`DefDatabase` 查询只允许在主线程做，且只做一次
        /// （解析点 = 物理步预暖段，与成员表/源表同处）；并行区只读这个静态字段。
        /// ⚠ Def 在运行期只读 ⇒ 缓存无陈旧风险；**Def 缺席 ⇒ 保持 null ⇒ 进气整条路径缺席即零**
        ///   （不报错：本 mod 的 Def 集是自带的，缺它就是"这一版没有进气"）。
        /// </summary>
        private static PropellantDef? intakeAirDefCache;

        private static PropellantDef? IntakeAirDef
        {
            get
            {
                if (intakeAirDefCache == null)
                    intakeAirDefCache = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_IntakeAir");
                return intakeAirDefCache;
            }
        }

        /// <summary>
        /// **[A4 有 Map 侧] 对切片按包线 φ 缩放 + 真空不路由**（与无 Map 侧**同一条语义**）：
        /// · φ **只乘推力、不乘流率**（低密度 = 同样的油少出力）；
        /// · φ = 0（真空里的吸气式）⇒ **整条切片剔掉**（D5 网络可用性门控）⇒ `fTotal`/`mfTotal` 按保留项重算；
        /// · **纯火箭（`envelopeTerms == null`）逐位不变**（一次比较就返回，连 ρ 都不取）。
        /// 为什么放在这里而不是 `SpaceMapComp`：它是**帧/上下文**的事（要 ρ、v_air、Mach），
        /// 而地图侧只认识"推力/流率"——半径对得上才不重复实现（与无 Map 侧共用 `TryGetAeroContext`）。
        /// </summary>
        private void ApplyMapThrustGates(SpaceEntityGroup group, CelestialBodyManager mgr,
            in DVector3 pos, in DVector3 vel, IPropulsionSource? src, double dtSeconds,
            List<ThrustSlice> slices, ref float fTotal, ref float mfTotal)
        {
            bool anyGate = false;
            for (int i = 0; i < slices.Count; i++)
            {
                var n0 = slices[i].network;
                if (n0 != null && (n0.envelopeTerms != null || n0.gasDraws != null)) { anyGate = true; break; }
            }
            if (!anyGate) return;                                                // 纯火箭 ⇒ 逐位不变

            double rho = 0.0, rho0 = 0.0, mach = 0.0;
            if (TryGetAeroContext(group, mgr, pos, vel, out double rhoC, out DVector3 vAirC))
            {
                rho = rhoC;
                mach = AirBreathingCore.MachFromAirSpeed(vAirC.magnitude);
                var b = mgr.GetBodyById(group.CelestialBodyId);
                rho0 = b?.Atmosphere.rho0 ?? 0.0;
            }
            // 真空（取不到上下文）⇒ ρ=0 ⇒ a>0 的网 φ=0 ⇒ 下面会整条剔掉（正是 D5 要的门控）

            float f = 0f, mf = 0f;
            bool routedOff = false, flamedOut = false;
            for (int i = slices.Count - 1; i >= 0; i--)
            {
                var sl = slices[i];
                var net = sl.network;
                if (net != null)
                {
                    // 门① 包线 `(ρ/ρ0)^a·f(M)`：φ = 0 ⇒ 不路由
                    if (net.envelopeTerms != null)
                    {
                        double phi = AirBreathingCore.EnvelopeFactor(net.envelopeTerms, net.Thrust, rho, rho0, mach);
                        if (!(phi > 0.0))
                        {
                            routedOff = true;
                            slices.RemoveAt(i);
                            continue;
                        }
                        sl.thrust = (float)(sl.thrust * phi);
                    }
                    // 门② 附带进气（空气）：抽不到 ⇒ 熄火（本步不出力、主工质不扣）
                    //   与无 Map 侧**同一份算式**（`IntakeThrottleCap`）—— 那边在构造前算 cap 因而不建切片，
                    //   这边在构造后按同一口径缩 F 与 ṁ（物理含义一致：流率被空气封顶）。
                    if (net.gasDraws != null && src != null)
                    {
                        float cap = 1f;
                        for (int g = 0; g < net.gasDraws.Count; g++)
                        {
                            var gd = net.gasDraws[g];
                            if (gd.propellant == null) continue;
                            double oneCap = AirBreathingCore.IntakeThrottleCap(src.GetPropellantMass(gd.propellant),
                                dtSeconds, gd.flowKgPerSec, net.MassFlow);
                            if (oneCap < cap) cap = (float)oneCap;
                            if (cap <= 0f) break;
                        }
                        if (cap <= 0f)
                        {
                            flamedOut = true;
                            slices.RemoveAt(i);            // 熄火：不建这条切片
                            continue;
                        }
                        if (cap < 1f)
                        {
                            sl.thrust *= cap;
                            sl.massFlow *= cap;            // 流率随空气封顶（与无 Map 侧同义）
                        }
                    }
                    slices[i] = sl;
                }
                f += sl.thrust;
                mf += sl.massFlow;
            }
            fTotal = f;
            mfTotal = mf;

            if (routedOff && !group.envelopeRoutedOffLogged)
            {
                group.envelopeRoutedOffLogged = true;
                Log.Message($"[大气·包线] G{group.GroupId}（有 Map 侧）某网包线因子 0（ρ={rho:F4E2}）⇒ 本步不路由该网（D5 门控）");
            }
            if (flamedOut && !group.intakeFlameoutLogged)
            {
                group.intakeFlameoutLogged = true;
                Log.Message($"[大气·进气] G{group.GroupId}（有 Map 侧）熄火：空气不足 ⇒ 本步不出力、不耗主工质");
            }
        }

        /// <summary>
        /// **群级进气面积**（m²·k）：Σ 各**未毁**部件的 `IntakeComponentData.intakeArea · coefficient`。
        /// **没有进气道 ⇒ 0 ⇒ 一点不充**（[补1] 的"不许静默兜底"：不进气道这件事必须由生产者显式声明）。
        /// 并行区只读调用（与 `GetGroupAeroShape` 同纪律；无 Map 与有 Map 都读 `e.Parts`）。
        /// </summary>
        private double GetGroupIntakeArea(SpaceEntityGroup group)
        {
            double area = 0.0;
            foreach (int eid in group.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e == null) continue;
                var parts = e.Parts;
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    if (p.isDestroyed) continue;
                    var intake = p.GetComponent<Components.IntakeComponentData>();
                    if (intake == null) continue;
                    double k = intake.coefficient > 0f ? intake.coefficient : 0.0;
                    area += intake.intakeArea * k;
                }
            }
            return area;
        }

        /// <summary>
        /// **[A3] 本步进气充填量（kg，只算不写）**：主线程入账前的量。
        /// 判据（计划稿 §2.3）：速率 = `k·A·ρ(h)·|v_air|`（唯一算式 = `AirBreathingCore.IntakeRateKgPerSec`），
        /// 再按**罐余量**截断（`FillStep`）。三道缺席即零：① Def 缺席 ② 无进气道 ③ 罐满/无罐。
        /// ⚠ 与阻力**共用** `TryGetAeroContext`（同一次 ρ/空速口径）；这里多一次调用是有意的：
        ///   阻力只在该组"非圆轨"分支里算，而充填与"怎么推进"无关（悬浮档/解析档的船在大气里照样吸气）。
        ///   成本 = 三道早退（真空里就是一次静态 bool 读）⇒ 与气动同量级。
        /// ⚠ 解析圆轨（`IsCircularOrbit`）组也走这里：`TryGetAeroContext` 不含"解析档互斥"判定（那是 [A6] 的
        ///   档位守卫）⇒ 解析档照充填 —— 这正是 D8 要的（档位只管受不受力，不管烧不烧料/吸不吸气）。
        /// </summary>
        private double ComputeGroupIntakeFill(SpaceEntityGroup group, CelestialBodyManager mgr,
            in DVector3 pos, in DVector3 vel, double dtSeconds)
        {
            if (!(dtSeconds > 0.0)) return 0.0;
            var airDef = IntakeAirDef;
            if (airDef == null) return 0.0;                                                    // ① Def 缺席

            if (!TryGetAeroContext(group, mgr, pos, vel, out double rho, out DVector3 vAir)) return 0.0;

            // [A3] 有 Map 侧（2026-10-07 接线）：面积与余量都从**地图聚合**取（按工质那本容量账）
            var mapComp = group.GetLinkedMapComp();
            double area, room;
            if (mapComp != null)
            {
                area = mapComp.TotalIntakeArea;
                room = mapComp.GetPropellantRoom(airDef);
            }
            else
            {
                area = GetGroupIntakeArea(group);
                room = GetGroupPropellantRoom(group, airDef);
            }
            if (!(area > 0.0)) return 0.0;                                                      // ② 无进气道
            if (!(room > 0.0)) return 0.0;                                                       // ③ 罐满/无罐

            double rate = AirBreathingCore.IntakeRateKgPerSec(1.0, area, rho, vAir.magnitude);
            double add = AirBreathingCore.FillStep(rate, dtSeconds, 0.0, room);                   // 以"余量"为容器的截断
            return add > 0.0 ? add : 0.0;
        }

        /// <summary>
        /// 群内**某工质**的罐余量（kg）—— 无 Map 侧的唯一口径：Σ 实体 `propulsion` 同工质条目的
        /// `capacity − mass`（逐条截断后求和 ⇒ 不会把"这条罐超容、那条罐空着"算成有余量）。
        /// ⚠ 只服务**无 Map**组；有 Map 组走 `SpaceMapComp.GetPropellantRoom`（它按工质记容量，2026-10-07 补）。
        /// </summary>
        private double GetGroupPropellantRoom(SpaceEntityGroup group, PropellantDef def)
        {
            double room = 0.0;
            if (def == null) return 0.0;
            foreach (int eid in group.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e == null || e.propulsion == null) continue;
                for (int i = 0; i < e.propulsion.Count; i++)
                {
                    var d = e.propulsion[i];
                    if (d == null || d.propellant != def) continue;
                    double r = d.capacity - d.mass;
                    if (r > 0.0) room += r;
                }
            }
            return room;
        }

        /// <summary>
        /// **[A3] 进气充填的入账**（主线程唯一写点）：把 `kg` 沿**各实体的空气罐**按余量分摊
        /// —— 与 `TryConsumePropellant` 沿实体分摊**同构**（一个加一个减，都"先吃余量大的、天然截断"）。
        /// 入参是并行区按**群总余量**截断过的量；逐实体再按各自余量截断 ⇒ 合起来 = `min(群余量, kg)` ✓。
        /// </summary>
        private void ApplyGroupIntakeFill(SpaceEntityGroup group, double kg)
        {
            var def = IntakeAirDef;
            if (def == null) return;

            // [A3] 有 Map 侧：按供油计划加注（高层先加满），池 raw 记账
            var mapComp = group.GetLinkedMapComp();
            if (mapComp != null)
            {
                mapComp.TryFillPropellant(def, kg);
                return;
            }

            double remaining = kg;
            foreach (int eid in group.GetEntityIds())
            {
                if (remaining <= 0.0) break;
                var e = GetEntityById(eid);
                if (e != null) remaining -= e.TryFillPropellant(def, remaining);
            }
        }

        /// <summary>
        /// 主推轴世界方向（D15/B8）：Map = mapComp.MainThrustAxis 随 rotation 旋转；
        /// 无 Map 主推方向型 = part 引擎按 `ve·ṁ` 聚合出的轴（`SpaceEntity.GetMainThrustAxisShip`），
        /// 无引擎件时回退旧 `entity.mainAxisShip`；全向类（无主推轴）= zero。
        /// 并行区只读调用（与 GetGroupPropulsionSource 同纪律）。
        /// 只读公开给表现层（PFView 朝向指示）——**唯一出处**，视图不再复制一份"Map/无 Map"解析分支。
        /// </summary>
        public DVector3 GetMainThrustAxisSystem(SpaceEntityGroup group)
        {
            var mapComp = group.GetLinkedMapComp();
            if (mapComp != null)
                return group.GetThrustAxisSystem(mapComp.MainThrustAxis);
            // 无 Map：任一带主推轴的实体（导弹弹群成员同构同姿态；2026-09-08 思路 A——多实体组推进源
            // 是群级聚合器，不再 as SpaceEntity 取轴，改扫实体；单实体组结果与原来逐字节一致）
            foreach (int eid in group.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e == null) continue;
                // B8：part 引擎聚合优先；无引擎件 ⇒ 旧 mainAxisShip（D15）兜底。
                DVector3 axisShip = e.GetMainThrustAxisShip();
                if (axisShip.sqrMagnitude > 1e-12)
                    return DVector3.Rotate(axisShip, group.CurrentOrbit.rotation);
            }
            return DVector3.zero;
        }

        /// <summary>
        /// 自动对准（SAS 兜底）：把主推进轴转到目标世界方向（收心正中，死区 ~1.5°），向量角速度（动量轮限速）。
        /// 已收心 → 零角速度（消旋）。未收心 → 按剩余夹角比例调速，一步转不完则满速。
        /// 主推轴来源：Map = mapComp.MainThrustAxis；无 Map 主推型 = entity.mainAxisShip（D15）；
        /// 全向类（无主推轴）→ 直接返回（无对准语义）。
        /// 在并行物理区调用（只写本 group 独占的 angularVelocity，与 pendingImpulse 同纪律）。
        /// </summary>
        private void AlignMainAxis(SpaceEntityGroup group, DVector3 targetDir, double dtSeconds)
            => AlignShipAxis(group, GetMainThrustAxisSystem(group), targetDir, dtSeconds);

        /// <summary>
        /// **把船体的任意一条轴转到目标世界方向**（[A6+] 2026-10-06 抽自 `AlignMainAxis`，唯一实现）：
        /// 收心正中（死区 `DefaultThrustAlignCos` ~1.5°）⇒ 消旋；否则按剩余夹角限速转动
        /// （角速度 = 最短旋转轴 × 角速率，反平行时换一根垂直轴）。
        ///
        /// 两个使用者：① `AlignMainAxis`（**主推轴**，烧/预对准）；② **大气段顺气流指向**（**船体前轴 +z**）。
        /// `currentAxisSystem` 必须是**单位**世界方向；零向量 ⇒ 直接返回（该轴无定义，如全向类的主推轴）。
        /// 在并行物理区调用（只写本 group 独占的 `angularVelocity`，与 pendingImpulse 同纪律）。
        /// </summary>
        private static void AlignShipAxis(SpaceEntityGroup group, DVector3 currentAxisSystem,
            DVector3 targetDir, double dtSeconds)
        {
            if (currentAxisSystem.sqrMagnitude < 1e-12)
                return; // 该轴无定义（如全向类的主推轴）：无对准语义
            DVector3 targetN = targetDir.normalized;
            if (targetN.sqrMagnitude < 1e-12)
            {
                group.angularVelocity = DVector3.zero;
                return;
            }
            double dot = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(currentAxisSystem, targetN)));
            if (dot >= DefaultThrustAlignCos)
            {
                group.angularVelocity = DVector3.zero; // 已收心到正中（≤~1.5°）：消旋，防 burn 期间漂移
                return;
            }
            double error = Math.Acos(dot);
            // 最短旋转轴 = 该轴 × target（右手系）；反平行时叉积退化 → 任取垂直轴
            DVector3 rotAxis = DVector3.Cross(currentAxisSystem, targetN);
            if (rotAxis.sqrMagnitude < 1e-12)
                rotAxis = DVector3.Cross(currentAxisSystem, Math.Abs(currentAxisSystem.y) < 0.9 ? DVector3.up : DVector3.forward);
            DVector3 axisN = rotAxis.normalized;
            // 批 1（2026-10-17）：限速按**这根旋转轴**的转动惯量取 `τ_ref/I_轴`（大船慢；
            // 滚/偏/俯上限各不同；`maxAngularRate` def 覆盖优先——导弹读数不变）。
            double maxRate = group.MaxAngularRateAboutWorldAxis(axisN) * group.attitudeRateScale;
            // 剩余夹角小于一步最大转角 → 降速收敛（避免越过目标震荡）
            double rate = Math.Min(maxRate, error / Math.Max(dtSeconds, 1e-9));
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
            SteeringInputHost.EnsureInputSafety();
            var group = GetGroupById(SteeringController.GroupId);
            if (group == null || !group.IsActive || !group.IsMovable)
            {
                SteeringController.Close();
                return;
            }
            // 控制核心（2026-10-26）：装过核心的舰失去核心 ⇒ 操舵输入无效（关台，不施加任何冲量）
            if (!group.IsControllableNow())
            {
                SteeringController.Close();
                return;
            }
            bool anyInput = SteeringController.AnyTranslation || SteeringController.AnyRotation
                || SteeringController.SasKind != SasTargetKind.None;

            // ── [A8]/D7 降级触发①「**玩家下令（操舵台接管）**」：接管即**退出悬浮** ──
            //    ⚠ **必须在地图门之前**（2026-10-07 自动判据抓到）：`ApplySteering` 对无 Map 船是**早退**的，
            //    原先写在门后 ⇒ 无 Map 组的接管永远不触发（判据当场红：hoverActive=True）。
            //    接管语义与"这条船能不能被操舵"无关：玩家按下了接管，悬浮（自动驾驶的工作模式）就该让位。
            if (anyInput && group.hoverActive)
            {
                SetHover(group, false);
                Log.Message($"[档位] G{group.GroupId} 操舵台接管 ⇒ 退出悬浮（D7 降级触发①：玩家下令）");
            }

            var mapComp = group.GetLinkedMapComp();
            if (mapComp == null) return; // 无 Map 船暂不支持操舵

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
                    Log.Message($"[Steer] Group {group.GroupId} 手动接管：清空机动队列/引导");
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
                // [A4] 手动推力同样过包线（真空里吸气式网不出力；火箭恒 1.0 ⇒ 逐位不变）
                var steerMgr = celestialRef.Get();
                if (steerMgr != null)
                {
                    var steerSrc = GetGroupPropulsionSource(group);
                    ApplyMapThrustGates(group, steerMgr, group.CurrentOrbit.positionSystem.v,
                        group.CurrentOrbit.velocitySystem.v, steerSrc, dtSeconds, slices, ref f, ref mf);
                }
                SteeringController.ScaleManualThrust(ref f, ref mf);
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
                SteeringController.ActualThrust = burning ? f : 0f;
                SteeringController.ThrustUnavailable = !burning;
                if (DebugDetailedSteering && Find.TickManager.TicksGame >= nextSteerLogTick)
                {
                    DVector3 dirN = transDir.normalized;
                    Log.Message($"[Steer] Group {group.GroupId} 平移 dir=({dirN.x:F2},{dirN.y:F2},{dirN.z:F2}) 推力={f / 1000f:F1}kN 流率={mf:F1}kg/s rcs={mapComp.HasRcs} 实际推进={burning}");
                    nextSteerLogTick = Find.TickManager.TicksGame + 10;
                }
            }
            else
            {
                SteeringController.ActualThrust = 0f;
                SteeringController.ThrustUnavailable = false;
                // 清掉上一步手动平移留下的喷焰。
                mapComp.SetThrustersBurning(false);
            }

            // 手动转向先接管 SAS；松键按本次会话设置处理角速度。
            SteeringController.ApplyManualRotation(group, dtSeconds);

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
                    Log.Message($"[Steer] Group {group.GroupId} 旋转 ω=({w.x * Mathf.Rad2Deg:F2},{w.y * Mathf.Rad2Deg:F2},{w.z * Mathf.Rad2Deg:F2})°/s 幅值={(float)w.magnitude * Mathf.Rad2Deg:F2}°/s");
                }
                if (SteeringController.SasKind != SasTargetKind.None)
                {
                    DVector3 t = SteeringController.GetSasTarget(group);
                    Log.Message($"[Steer] Group {group.GroupId} SAS→{SteeringController.SasKind} target=({t.x:F2},{t.y:F2},{t.z:F2})");
                }
                nextSteerLogTick = Find.TickManager.TicksGame + 10;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  [A7] 悬浮（定点保持）—— 主线程，紧挨操舵之后（同一套"算片 → 冲量 → 扣料"链条）
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>在悬浮的组数（**静态计数**：没有它时整条路径一次比较就返回 ⇒ "缺席即零"）。</summary>
        private static int hoverActiveCount;

        /// <summary>主线程用的引力 scratch（悬浮每步要在**并行相位之外**取一次本地场）。</summary>
        private readonly GravityScratch hoverScratch = new();

        /// <summary>悬浮读数**按物理秒**累积（每 10 s 一行；踩过"用 tick 差当秒"的坑，见读数块注释）。</summary>
        private const double HoverLogIntervalSeconds = 10.0;

        /// <summary>
        /// **开/关悬浮（唯一入口）**：开时把**当前星下点**锁成定点（固连经纬度 + 当前高度）。
        /// 与 `SteeringController` 无关（本刀不做玩家 UI —— 船上操舵台/PFView 那条线是视觉/交互 WIP 的属地）。
        /// </summary>
        public bool SetHover(SpaceEntityGroup? group, bool on) => SetHover(group, on, analytic: false);

        /// <summary>
        /// **[A8] 开/关悬浮 + 选档位**（`analytic` = 解析档）。解析档的**准入判据**照 D8 的先例
        /// （tile-landing"付不起直接拒绝"）：**必须有全矢量（RCS）网** —— 否则等效计费没有 `ve` 可依、
        /// 也没法在退出解析档时兑现 ⇒ 直接拒绝并如实报。
        /// **进出必须显式**（D7）：两条路都写日志，含**当前档位 + 计费速率**（面板口径的日志版）。
        /// </summary>
        public bool SetHover(SpaceEntityGroup? group, bool on, bool analytic)
        {
            if (group == null) return false;
            // [模式互斥] 搬运中不许开悬浮（理由见 `CanHover`）：两者都"每步改位置" ⇒ 同时跑就是互相打架。
            if (on && group.transferActive)
            {
                Log.Warning($"[档位] G{group.GroupId} 拒绝开悬浮：**正在搬运中**（搬运与定点保持互斥；先「停止」或等它到达）");
                return false;
            }
            if (on && analytic && !CanHover(group, analytic: true, out string? whyAna))
            {
                Log.Warning($"[档位] G{group.GroupId} 拒绝进解析档：{(whyAna ?? "原因未给")}（D8：付不起直接拒绝）");
                return false;
            }
            if (!SetHoverCore(group, on)) return false;
            if (on) group.hoverAnalytic = analytic;
            LogTier(group, on ? (analytic ? "进解析档（定点解析，照扣料）" : "进物理档（定点保持控制器）")
                              : "退出悬浮 ⇒ 回默认（解析搬运/巡航）");
            return true;
        }

        /// <summary>
        /// **悬浮可用性预检**（**只读**、不改任何状态）——控制台拿它把"为什么按不动 / 开了会怎样"
        /// **写在界面上**，而不是让玩家去猜（2026-10-07：控制台原来给的是"不在大气里/没有网/没引擎"三选一，
        /// 玩家只能看日志猜是哪一条 ⇒ 改成**唯一出处**的预检，返回**具体**原因）。
        ///
        /// 与生产门**同一份判据**（本方法就是 `SetHover`/`SwitchHoverTier` 用的那道门，不再各写一份）：
        /// · 没有天体 / 在地表以下 ⇒ 不可用（硬拒）；
        /// · **解析档**必须有全矢量（RCS）网（`ve` 可依）⇒ 没有则硬拒；
        /// · **物理档**生产上允许开 ⇒ 本方法**放行但给出警告语**（"全矢量网为空 ⇒ 开了会掉"）——
        ///   实测：玩家对一台**没有 RCS** 的试验船开了物理档，船直接掉到地上（`[悬浮] … 推力不足`）。
        /// </summary>
        public bool CanHover(SpaceEntityGroup group, bool analytic, out string? reason)
        {
            reason = null;
            if (group == null) { reason = "编队不存在"; return false; }
            var mgr = celestialRef.Get();
            var body = mgr?.GetBodyById(group.CelestialBodyId);
            if (body == null) { reason = "找不到天体"; return false; }
            if (group.CurrentOrbit.positionSystem.v.magnitude <= body.radius)
            {
                reason = "本船在地表以下 ⇒ 没有可锁的定点";
                return false;
            }
            // **[模式互斥]** 搬运中不能开悬浮：两者都"每步改位置"（搬运按航线推、悬浮按定点按），
            // 一起跑就是互相打架（2026-10-07 用户实测："船疑似被锚定死了，走不出去"）。
            if (group.transferActive)
            {
                reason = "正在搬运中 ⇒ 悬浮不可用（先按「停止」或等它到达）";
                return false;
            }
            // **[D7②] 交战 ⇒ 解析档不可用**（用户 2026-10-08：用现成的"有射弹"信号，不新建交战触发）
            if (analytic && CombatActionActive)
            {
                reason = "处于交战动作（有射弹在飞 / 导弹终末攻击）⇒ 解析档（定点解析）不可用；物理档照常（降级只加精度）";
                return false;
            }
            var src = GetGroupPropulsionSource(group);
            if (src == null || EffectiveFullVectorVe(src) <= 0f)
            {
                reason = "没有可用的全矢量（RCS）网：解析档没有 ve 可计费；物理档开了也纠不动（会掉）";
                return !analytic;     // 物理档生产上允许开 ⇒ 放行（警告语已给）
            }
            return true;
        }

        /// <summary>
        /// **[A8] 档位切换（解析 ⇄ 物理）**：**只翻档位、不重锁定点** ——
        /// 定点是**工作模式**的属性（D9），档位只管"受不受力"（D7）⇒ 切档不该把船按到"现在所处的位置"上。
        /// ⚠ 实测踩过：切档误走"重开悬浮"⇒ 重新锁一次定点（日志第二条"开启"、切回后横向误差 434 m 再收敛）。
        /// </summary>
        public bool SwitchHoverTier(SpaceEntityGroup group, bool analytic)
        {
            if (group == null || !group.hoverActive) return false;
            if (group.hoverAnalytic == analytic) return true;
            if (analytic && !CanHover(group, analytic: true, out string? whyAna))
            {
                Log.Warning($"[档位] G{group.GroupId} 拒绝切解析档：{(whyAna ?? "原因未给")}（D8：付不起直接拒绝）");
                return false;
            }
            group.hoverAnalytic = analytic;
            LogTier(group, analytic ? "切解析档（定点解析，照扣料；**定点不变**）"
                                    : "切物理档（定点保持控制器；**定点不变**）");
            return true;
        }

        /// <summary>全矢量（RCS）网的有效 ve（解析档计费用；与物理档用同一批网 ⇒ 两档账单可比）。</summary>
        private static float EffectiveFullVectorVe(IPropulsionSource src)
        {
            float f = 0f, mf = 0f;
            var nets = src.Networks;
            for (int i = 0; i < nets.Count; i++)
            {
                if (!nets[i].HasRcsEngines || nets[i].MassFlow <= 0f) continue;
                f += nets[i].Thrust;
                mf += nets[i].MassFlow;
            }
            return mf > 0f ? f / mf : 0f;
        }

        /// <summary>
        /// **[A8] 档位日志（D7"进出必须显式"）**：报**档位 + 耗料率 + 还能撑多久** ——
        /// 面板口径的日志版（面板 UI 属视觉/交互线，本刀先只保证"玩家能在日志里看到自己在哪档"）。
        /// </summary>
        private void LogTier(SpaceEntityGroup group, string what)
        {
            var src = GetGroupPropulsionSource(group);
            double mass = GetGroupMass(group, src);
            var mgr = celestialRef.Get();
            var body = mgr != null ? mgr.GetBodyById(group.CelestialBodyId) : null;
            double g = body != null && body.radius > 0.0
                ? SpaceConstants.PrimaryGravitationalParameter / (body.radius * body.radius)
                : 0.0;
            double ve = src != null ? EffectiveFullVectorVe(src) : 0f;
            double rate = ve > 0.0 ? mass * g / ve : 0.0;          // 悬浮的等效质量流率（kg/s）
            double fuel = src != null ? src.TotalPropellantMass : 0.0;
            Log.Message($"[档位] G{group.GroupId} {what}｜档位={(group.hoverActive ? (group.hoverAnalytic ? "解析" : "物理") : "—（回默认）")}｜全矢量 ve={ve:F0} m/s｜等效耗料率 ≈{rate:F2} kg/s｜余料 {fuel:F0} kg（≈{(rate > 0.0 ? fuel / rate : 0.0):F0} s）");
        }

        private bool SetHoverCore(SpaceEntityGroup group, bool on)
        {
            if (group == null) return false;
            if (!on)
            {
                if (group.hoverActive) hoverActiveCount--;
                group.hoverActive = false;
                group.hoverAnalytic = false;   // [A8] 档位随悬浮一起清（下次开启重新选档）
                return true;
            }
            var mgr = celestialRef.Get();
            if (mgr == null) return false;
            var body = mgr.GetBodyById(group.CelestialBodyId);
            if (body == null) return false;

            double omega = Launch.GetSurfaceState.RotationOmega(mgr, group.CelestialBodyId);
            var pos = group.CurrentOrbit.positionSystem.v;
            double r = pos.magnitude;
            if (!(r > body.radius)) return false;                       // 在地表以下 ⇒ 不锁
            // ⚠ tick 口径 = **物理时间轴**（`GetPhysicsTickSafe`）：与固连系的其它调用点同一根轴
            //   （用 `TicksGame` 会在时间倍率下把经度算歪 ω·Δt）。
            var (lat, lon) = SurfaceFrame.EciToLongLat(pos, omega, TimeScaleManager.GetPhysicsTickSafe());
            group.hoverLatRad = lat;
            group.hoverLonRad0 = lon;
            group.hoverAltitudeMeters = r - body.radius;
            if (!group.hoverActive) hoverActiveCount++;
            group.hoverActive = true;
            group.hoverShortfallLogged = false;
            Log.Message($"[悬浮] G{group.GroupId} 开启：锁定点 φ={lat:F5} rad λ0={lon:F5} rad 高度 {group.hoverAltitudeMeters:F1} m（本刀口径 = 全矢量 RCS 推力 + 锁铅垂）");
            return true;
        }

        /// <summary>
        /// **[A8] 解析搬运的每步记账与到达清单**（主线程）：
        /// · 记 `dv = Δv_total·dt/T`（**末步截断**），扣 `m·dv/ve`（沿途全部网络按 ṁ 比例分账，与其它路径同口径）；
        /// · 料尽 ⇒ **显式退出**（不静默、不留"免费档"）；
        /// · `u ≥ 1` ⇒ **精确落位**到目标状态 + 清搬运单 + 一行日志（D7"进出必须显式"）。
        /// </summary>
        private void ApplyAnalyticTransfer(double dtSeconds, double physicsTick, CelestialBodyManager mgr)
        {
            if (!(dtSeconds > 0.0)) return;
            foreach (var group in groups.Values)
            {
                if (!group.transferActive) continue;
                var src = GetGroupPropulsionSource(group);
                float ve = src != null ? src.EffectiveVe : 0f;
                if (src == null || ve <= 0f)
                {
                    Log.Warning($"[搬运] G{group.GroupId} 中断：没有引擎（或 ve≤0）");
                    group.transferActive = false;
                    continue;
                }

                double elapsed = physicsTick - group.transferStartTick;
                double u = AnalyticTransferCore.Progress(elapsed, group.transferDuration);
                double dtUsed = Math.Min(dtSeconds, Math.Max(group.transferDuration - (elapsed - dtSeconds), 0.0));
                if (dtUsed < 0.0) dtUsed = 0.0;

                // **[D7②] 交战中止**（用户 2026-10-08 裁定：用现成的"有射弹"信号）：
                //   解析搬运是"轨上"的 ⇒ 交战中不该继续享受"不模拟受力"的特权 ⇒ 中途**中止**、
                //   交还物理档（船按当前状态继续飞，玩家可以物理档机动/悬浮）。停在哪 = 当前位置。
                if (u < 1.0 && CombatActionActive)
                {
                    group.transferActive = false;
                    group.OrbitDirty = true;
                    Log.Message($"[搬运] G{group.GroupId} **中止**（交战动作出现 ⇒ 解析轨降级为物理档）：停在当前位置（已到 u={u:F2}）");
                    TryResumeHoverAfterTransfer(group, src, "中止");   // 起搬前在悬着 ⇒ 就地恢复（不然在大气里会开始掉）
                    continue;
                }

                if (u < 1.0 && dtUsed > 0.0)
                {
                    double dvStep = AnalyticTransferCore.DvThisStep(group.transferDvTotal, group.transferDuration, dtUsed);
                    double mass = GetGroupMass(group, src);
                    double dm = AnalyticTransferCore.PropellantThisStep(mass, dvStep, ve);
                    if (dm > 0.0)
                    {
                        float mfTotal = 0f;
                        var nets = src.Networks;
                        for (int i = 0; i < nets.Count; i++)
                            if (nets[i].MassFlow > 0f) mfTotal += nets[i].MassFlow;
                        double got = 0.0;
                        if (mfTotal > 0f)
                        {
                            for (int i = 0; i < nets.Count; i++)
                            {
                                var n = nets[i];
                                if (n.MassFlow <= 0f || n.propellant == null) continue;
                                double share = dm * ((double)n.MassFlow / mfTotal);
                                got += ConsumeGroupPropellant(group, n.propellant, (float)share);
                            }
                        }
                        // 料尽 ⇒ 显式退出（与悬浮解析档同一条纪律）
                        if (got < dm * 0.5)
                        {
                            group.transferActive = false;
                            Log.Message($"[搬运] G{group.GroupId} **料尽** ⇒ 退出搬运（不静默；已到 u={u:F2}）");
                            continue;
                        }
                    }
                }

                if (u >= 1.0)
                {
                    group.transferActive = false;
                    group.CurrentOrbit.positionSystem = (SystemVec3)group.transferToPos;   // **精确落位**
                    group.CurrentOrbit.velocitySystem = (SystemVec3)group.transferToVel;
                    // 到达误差**当场量**（就在写完之后、同一步内）—— 事后重建会掺进"一步的位移"（判据侧踩过三次）
                    group.transferArrivalErrorMeters =
                        (group.CurrentOrbit.positionSystem.v - group.transferToPos).magnitude;
                    group.OrbitDirty = true;
                    if (!group.transferArriveLogged)
                    {
                        group.transferArriveLogged = true;
                        Log.Message($"[搬运] G{group.GroupId} **到达**（解析档）：Δv 记账 {group.transferDvTotal:F1} m/s｜落位误差 {group.transferArrivalErrorMeters:F3} m｜余料 {src.TotalPropellantMass:F0} kg");
                    }
                    // **到点恢复悬浮**（起搬前在悬着的话）：大气层里"搬完不悬"= 开始掉（实测 30 km 掉到地面、
                    // 撞地 339.7 m/s ⇒ 坠毁 + 地图销毁）。恢复点 = 到达点（`SetHoverCore` 按**当前位置**锁点 ✓）。
                    TryResumeHoverAfterTransfer(group, src, "到达");
                }
                else if (!group.transferMidLogged && u >= 0.5)
                {
                    group.transferMidLogged = true;   // 中点报一行（长搬运才有；防"整段无声"）
                    Log.Message($"[搬运] G{group.GroupId} 中点 u=0.50｜余料 {src.TotalPropellantMass:F0} kg");
                }
            }
        }

        /// <summary>
        /// **悬浮控制（每物理步，主线程）**：定点误差 → 指令加速度 → 全矢量推力 → 冲量 + 扣料。
        ///
        /// 口径（本刀，与计划稿 §2.6 一致）：
        ///   · 指令 = `HoverCore.CommandAccel(fieldAcc, posErr, velErr)`（**每步刷新引力抵消**，
        ///     台架实测这比"常推力"抖幅小得多，且对 dt 不敏感 —— 见 `HoverBenchTests`）；
        ///   · 推力**只走全矢量（RCS / 垂直推进器）网**：悬浮要锁铅垂（D10），主引擎会对准而把船转歪；
        ///     本刀不转船体，RCS 不够就是"推力不足"（如实报，不偷偷给主网）。
        ///   · 包线与进气**照常生效**（吸气式 RCS 在高空一样衰减/抽干）⇒ 悬浮不能绕开大气模型。
        ///   · 冲量进 `pendingImpulse` ⇒ 本步 `ApplyPendingImpulses` 立即生效（与手动操舵同相）。
        /// </summary>
        private void ApplyHover(double dtSeconds, double physicsTick, CelestialBodyManager mgr)
        {
            if (hoverActiveCount <= 0 || !(dtSeconds > 0.0)) return;

            foreach (var group in groups.Values)
            {
                if (!group.hoverActive) continue;
                if (!group.IsMovable || !group.IsActive) continue;

                var body = mgr.GetBodyById(group.CelestialBodyId);
                if (body == null) continue;
                double omega = Launch.GetSurfaceState.RotationOmega(mgr, group.CelestialBodyId);

                var (tPos, tVel) = HoverCore.TargetState(body.radius, group.hoverLatRad, group.hoverLonRad0,
                    group.hoverAltitudeMeters, omega, physicsTick);
                var pos = group.CurrentOrbit.positionSystem.v;
                var vel = group.CurrentOrbit.velocitySystem.v;

                // 本地场（主线程算一次；掩码/多体都在里面 ⇒ 与积分用的是同一个场）
                var sources = ResolveSystemSources(mgr, group, physicsTick, hoverScratch);
                ComputeGroupFieldAccelJerk(group, mgr, physicsTick, hoverScratch, sources,
                    out DVector3 fieldAcc, out _, out _, out _, timeSegment: false);

                // ⚠ 只做**半步重力回正**（实测已验：稳态偏置 162 m ⇒ 5.1 m）；**定点前馈暂不加** ——
                //   1-D 台架验证不了它（没建模切向运动，符号/相位反复翻）⇒ 另案：要加就配 2-D 台架。
                //   残留的 +5.1 m = `ω²r/Kp`（PD 不带前馈时的固有偏置），是**已知且已量化**的。
                DVector3 aCmd = HoverCore.CommandAccel(fieldAcc, tPos - pos,
                    tVel - HoverCore.DebiasVelocity(vel, fieldAcc, dtSeconds),   // 半步重力回正（见 HoverCore）
                    feedForwardAccel: HoverCore.TargetAccel(tPos, omega));        // 定点前馈（2-D 台架验过后上线）
                double dvWanted = aCmd.magnitude * dtSeconds;
                if (!(dvWanted > 1e-9)) continue;                        // 已在定点（死区内）⇒ 本步不点火

                // 全矢量网可用推力（含包线 + 空气门控；与燃烧路径同源的两条算式）
                var src = GetGroupPropulsionSource(group);
                if (src == null) continue;
                var nets = src.Networks;                                 // 解析档与物理档**同源**（两档账单才可比）

                // ── [A8] **解析档**：不出推力、不算误差 ⇒ 只按 D8 等效计费（`m·g·dt/ve`）扣料 ──
                //   位置由物理区的定点解析给出（见 ComputeGroupPhysics 的同名分支）⇒ 这里只管账。
                //   ⚠ 料尽必须**显式退出解析档**（D7：进出都必须写日志）——不然就成了"免费档"。
                if (group.hoverAnalytic)
                {
                    float veA = EffectiveFullVectorVe(src);
                    double gMagA = fieldAcc.magnitude;
                    double dvA = HoverCore.EquivalentDvPerStep(gMagA, dtSeconds);
                    if (veA <= 0f || !(dvA > 0.0))
                    {
                        LogTier(group, "解析档中止（没有可用全矢量 ve）⇒ 退出悬浮");
                        SetHover(group, false);
                        continue;
                    }
                    double dmA = GetGroupMass(group, src) * dvA / veA;
                    float mfA = 0f;
                    for (int i = 0; i < nets.Count; i++)
                    {
                        var nA = nets[i];
                        if (!nA.HasRcsEngines || nA.MassFlow <= 0f || nA.propellant == null) continue;
                        mfA += nA.MassFlow;
                    }
                    bool dryA = mfA <= 0f;
                    double gotA = 0.0;
                    if (!dryA)
                    {
                        for (int i = 0; i < nets.Count; i++)
                        {
                            var nA = nets[i];
                            if (!nA.HasRcsEngines || nA.MassFlow <= 0f || nA.propellant == null) continue;
                            double shareA = dmA * ((double)nA.MassFlow / mfA);
                            gotA += ConsumeGroupPropellant(group, nA.propellant, (float)shareA);
                        }
                        dryA = gotA < dmA * 0.5;   // 抽不够（料尽）⇒ 判干
                    }
                    if (dryA)
                    {
                        LogTier(group, "解析档**料尽** ⇒ 退出悬浮（不静默）");
                        SetHover(group, false);
                        continue;
                    }
                    // 解析档的读数（**必须打**：这一档的看点就是"位置被钉死" ⇒ 区间 min/max 宽度应为 0）
                    if (DebugDetailedSteering)
                    {
                        double rNowA = pos.magnitude;
                        DVector3 radHatA = rNowA > 1e-9 ? pos * (1.0 / rNowA) : DVector3.up;
                        double eA = DVector3.Dot(pos - tPos, radHatA);
                        double vA = DVector3.Dot(vel, radHatA);
                        group.hoverLogErrSum += eA; group.hoverLogVSum += vA; group.hoverLogCount++;
                        if (eA < group.hoverLogErrMin) group.hoverLogErrMin = eA;
                        if (eA > group.hoverLogErrMax) group.hoverLogErrMax = eA;
                        group.hoverLogAccumSeconds += dtSeconds;
                        if (group.hoverLogAccumSeconds >= HoverLogIntervalSeconds)
                        {
                            double nA = Math.Max(group.hoverLogCount, 1);
                            Log.Message($"[悬浮·解析档] G{group.GroupId} 高度差 现{eA:F3}｜区间[{group.hoverLogErrMin:F3},{group.hoverLogErrMax:F3}]均{group.hoverLogErrSum / nA:F3}｜径向速度 现{vA:F3}｜横向{(tPos - pos).magnitude:F2}｜**本步记账 Δv={dvA:F4} m/s ⇒ 耗料 {gotA:F3} kg**｜料{src.TotalPropellantMass:F0} kg");
                            group.hoverLogAccumSeconds = 0.0;
                            group.hoverLogErrMin = double.MaxValue;
                            group.hoverLogErrMax = double.MinValue;
                            group.hoverLogErrSum = 0.0;
                            group.hoverLogVSum = 0.0;
                            group.hoverLogCount = 0;
                        }
                    }
                    continue;
                }
                var hoverSlices = hoverSliceScratch;
                hoverSlices.Clear();
                double fTotal = 0.0, mfTotal = 0.0;
                for (int i = 0; i < nets.Count; i++)
                {
                    var n = nets[i];
                    if (!n.HasRcsEngines || n.MassFlow <= 0f) continue;
                    if (n.propellant == null || n.AvailableMass(src) <= 0f) continue;

                    float envFactor = 1f;
                    if (n.envelopeTerms != null)
                    {
                        double rhoH = 0.0;
                        DVector3 vAirH = DVector3.zero;
                        if (TryGetAeroContext(group, mgr, pos, vel, out double rhoCtx, out DVector3 vAirCtx))
                        { rhoH = rhoCtx; vAirH = vAirCtx; }
                        double phiH = AirBreathingCore.EnvelopeFactor(n.envelopeTerms, n.Thrust, rhoH, body.Atmosphere.rho0,
                            AirBreathingCore.MachFromAirSpeed(vAirH.magnitude));
                        if (!(phiH > 0.0)) continue;                     // 真空 ⇒ 吸气式网不出力（D5 同一条门控）
                        envFactor = (float)phiH;
                    }
                    float cap = 1f;
                    if (n.gasDraws != null)
                    {
                        for (int g = 0; g < n.gasDraws.Count; g++)
                        {
                            var gd = n.gasDraws[g];
                            if (gd.propellant == null) continue;
                            double oneCap = AirBreathingCore.IntakeThrottleCap(src.GetPropellantMass(gd.propellant),
                                dtSeconds, gd.flowKgPerSec, n.MassFlow);
                            if (oneCap < cap) cap = (float)oneCap;
                        }
                        if (cap <= 0f) continue;                         // 空气抽干 ⇒ 熄火（悬浮也不例外）
                    }
                    float fNet = (float)(n.Thrust * (double)cap * envFactor);
                    hoverSlices.Add(new ThrustSlice { network = n, thrust = fNet, massFlow = n.MassFlow * cap });
                    fTotal += fNet;
                    mfTotal += n.MassFlow * cap;
                }

                double mass = GetGroupMass(group, src);

                // ── [A7] 周期读数（**按物理秒累积**，不按 tick 差 —— 2026-10-06 实测踩过：
                //    这个 mod 里 1 tick ≈ 1 物理秒，写成 `TicksGame + 600` 本意"10 秒"实际成了 10 分钟，
                //    整段悬浮只打出一行。放在"推力不足"检查**之前**：料尽那一刻也要看得见。──
                if (DebugDetailedSteering)
                {
                    DVector3 dPosR = pos - tPos;
                    double rNow = pos.magnitude;
                    DVector3 radialHat = rNow > 1e-9 ? pos * (1.0 / rNow) : DVector3.up;
                    double eNow = DVector3.Dot(dPosR, radialHat);        // 正 = 高于目标
                    double vRadNow = DVector3.Dot(vel, radialHat);       // 正 = 上升
                    double gRadNow = DVector3.Dot(fieldAcc, radialHat);

                    // 区间统计（2026-10-06 第三轮实测后加）：单点读数**看不出**"真在漂"还是"相位锁定的振荡"
                    // （上一轮读数里"高度差恒定 + 径向速度恒定"两者自相矛盾，缺的就是这一组数）。
                    group.hoverLogErrSum += eNow;
                    group.hoverLogVSum += vRadNow;
                    group.hoverLogCount++;
                    if (eNow < group.hoverLogErrMin) group.hoverLogErrMin = eNow;
                    if (eNow > group.hoverLogErrMax) group.hoverLogErrMax = eNow;

                    if (group.hoverLogCount == 1)   // 首次给基线（开悬浮那一刻的半径/场/ρ）
                    {
                        group.hoverLogRadius0 = rNow;
                        Log.Message($"[悬浮] G{group.GroupId} 起点：船半径 {rNow:F1} m｜目标半径 {tPos.magnitude:F1} m｜场径向 {gRadNow:F4} m/s²｜定点高 {group.hoverAltitudeMeters:F1} m｜ρ {(TryGetAeroContext(group, mgr, pos, vel, out double rho0L, out _) ? rho0L : 0.0):F5} kg/m³");
                    }

                    group.hoverLogAccumSeconds += dtSeconds;
                    if (group.hoverLogAccumSeconds >= HoverLogIntervalSeconds)
                    {
                        double n = Math.Max(group.hoverLogCount, 1);
                        Log.Message($"[悬浮] G{group.GroupId} 高度差 现{eNow:F1}｜区间[{group.hoverLogErrMin:F1},{group.hoverLogErrMax:F1}]均{group.hoverLogErrSum / n:F1}｜径向速度 现{vRadNow:F2}均{group.hoverLogVSum / n:F2}｜半径 船{rNow:F1}(起始{group.hoverLogRadius0:F1})目标{tPos.magnitude:F1}｜场径向{gRadNow:F4}｜横向现{(dPosR - radialHat * eNow).magnitude:F1}｜速度误差{(vel - tVel).magnitude:F2}｜指令{dvWanted:F3} 实给{(fTotal > 0.0 ? Math.Min(dvWanted, mass > 0.0 ? fTotal / mass * dtSeconds : 0.0) : 0.0):F3}｜可用a{(mass > 0.0 ? fTotal / mass : 0.0):F3}｜料{src.TotalPropellantMass:F0} kg");
                        group.hoverLogAccumSeconds = 0.0;
                        group.hoverLogErrMin = double.MaxValue;
                        group.hoverLogErrMax = double.MinValue;
                        group.hoverLogErrSum = 0.0;
                        group.hoverLogVSum = 0.0;
                        group.hoverLogCount = 0;
                    }
                }

                if (!(fTotal > 0.0) || !(mfTotal > 0.0))
                {
                    if (!group.hoverShortfallLogged)
                    {
                        group.hoverShortfallLogged = true;
                        // 读数要能自解释（2026-10-06 首轮实测的教训）：只写"推力不足"会让下一个人
                        // 以为是"没装 RCS"，而实测的真相是"**全矢量网被抽干了**"（场景把船扔成惯性零速、
                        // 控制器去追 523 m/s 的地面共转目标 ⇒ 3000 kg 烧光）。⇒ 网数/余料一并报出来。
                        int rcsNets = 0, rcsDry = 0;
                        for (int i = 0; i < nets.Count; i++)
                        {
                            if (!nets[i].HasRcsEngines || nets[i].MassFlow <= 0f) continue;
                            rcsNets++;
                            if (nets[i].propellant == null || nets[i].AvailableMass(src) <= 0f) rcsDry++;
                        }
                        Log.Warning($"[悬浮] G{group.GroupId} 推力不足 ⇒ 本步不纠偏（会掉）｜全矢量网 {rcsNets} 条（其中 **料尽/无料** {rcsDry} 条）｜全组余料 {src.TotalPropellantMass:F0} kg｜可用 a=0");
                        // **料尽 ⇒ 显式退出**（2026-10-07 自动判据抓到：原先物理档料尽后 `hoverActive` 仍是 true，
                        //   等于"嘴上还悬着、身子在掉" —— 与设计稿引的先例"燃料干涸自动退出"（§1.2）相悖，
                        //   也与解析档的料尽退出不对称）。只有**真的没料**才退；"没装网"这类留着让人看见。
                        if (HoverCore.ShouldExitHoverWhenDry(rcsNets, rcsDry))
                        {
                            LogTier(group, "物理档**料尽** ⇒ 退出悬浮（不静默：状态不再谎报'在悬浮'）");
                            SetHover(group, false);
                        }
                    }
                    continue;
                }

                double aAvail = fTotal / Math.Max(mass, 1.0);
                double dvApply = Math.Min(dvWanted, aAvail * dtSeconds);   // 给不出就只给得起的量
                double throttle = dvWanted > 0.0 ? dvApply / dvWanted : 0.0;
                group.pendingImpulse += aCmd * (dvApply / Math.Max(aCmd.magnitude, 1e-12));
                group.hoverLastDvCmd = dvWanted;
                group.hoverLastDvApplied = dvApply;
                group.hoverLastThrustN = fTotal;

                // 扣料：耗料 = 等效质量流 × 实际油门（= m·dv/ve 的同一条账，走既有唯一消耗口）
                double dm = mfTotal * dtSeconds * throttle;
                for (int i = 0; i < hoverSlices.Count; i++)
                {
                    var s = hoverSlices[i];
                    double share = mfTotal > 0.0 ? dm * (s.massFlow / mfTotal) : 0.0;
                    if (share > 0.0)
                        ConsumeGroupPropellant(group, s.network.propellant, (float)share);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  [A8] 点到点解析搬运（解析档的"搬运/巡航"）
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// **起一次解析搬运**（唯一入口）：把船从**当前位置**搬到给定目标状态，按 `Δv 总额` 计时计费。
        ///
        /// 准入（D8"付不起直接拒绝" + [A6]"解析档与气动互斥"）：
        /// · 有引擎（`ve > 0`）—— 没引擎就没"等效 dv"可付；
        /// · **起点与终点都不在大气里**（ρ = 0）—— 大气是物理档的属地，解析档不模拟受力 ✓ 与解析圆轨同一条规矩。
        /// 进出**必须显式**（D7）：成与不成各一行日志（含 Δv 总额、时长、等效耗料率）。
        /// </summary>
        public bool StartAnalyticTransfer(SpaceEntityGroup group, DVector3 targetPos, DVector3 targetVel,
            double dvTotal, double physicsTick)
        {
            if (group == null || !(dvTotal > 0.0)) return false;
            var src = GetGroupPropulsionSource(group);
            if (src == null || src.EffectiveVe <= 0f)
            {
                Log.Warning($"[搬运] G{group.GroupId} 拒绝：没有引擎 ⇒ 付不起等效 Δv");
                return false;
            }
            float ve = src.EffectiveVe;
            var mgr = celestialRef.Get();
            if (mgr == null) return false;
            // ⚠ **大气层内是允许的**（2026-10-07 用户纠正）：搬运/巡航这一行与"解析圆轨"不是一回事 ——
            //   D8 说的是"解析档**不模拟受力**但**照扣料**"，不是"解析档不许进大气"。
            //   原先把 [A6] 的"解析圆轨不许进大气"越界套到了搬运上 ⇒ 把巡航关在了大气层外面（那是错的门）。
            //   这里只挡**真正不可能的事**：端点在地表以下（或天体中心）⇒ 拒绝。
            if (group.CurrentOrbit.positionSystem.v.magnitude <= mgr.GetBodyById(group.CelestialBodyId)?.radius
                || targetPos.magnitude <= 1e-6)
            {
                Log.Warning($"[搬运] G{group.GroupId} 拒绝：起点或终点在地表以下（几何不可能）");
                return false;
            }

            // **[D7②] 交战 ⇒ 解析档不可用**（用户 2026-10-08 裁定：用现成的"有射弹"信号，**不新建交战触发**）：
            //   解析搬运是"轨上"的（不模拟受力）⇒ 交战中不许用它（免"免疫扰动"的特权）。
            //   ⚠ 这道门必须在**任何副作用之前**（尤其别先把悬浮退掉再拒绝 ⇒ 船白丢一次定点）。
            if (CombatActionActive)
            {
                Log.Warning($"[搬运] G{group.GroupId} 拒绝起搬：**处于交战动作（有射弹在飞 / 导弹终末攻击）** ⇒ 解析档（轨上）不可用；交战结束后再搬，或改物理档机动");
                return false;
            }

            // **[模式互斥] 起搬 ⇒ 先显式退出悬浮**（D7"进出必须显式"）：
            //   悬浮 = 定点保持（每步把船按在锁点上），搬运 = 按航线每步推位置 —— **两种工作模式不能同时跑**。
            //   ⚠ 2026-10-07 用户实测："飞船疑似被锚定死了，走不出去" —— 就是这两条在互相打架（旧代码起搬不清悬浮、
            //     悬浮也不看搬运单）。这里改成一件事：**玩家要它走，就先把按着它的手松开**。
            //   ⚠ 同时**记住"原来在悬着"**：大气层里没有悬浮就等于开始掉（实测：到达后 30 km 一路掉到地面，
            //     撞地 339.7 m/s ⇒ 坠毁）。巡航的语义 = "搬过去、然后继续悬停" ⇒ **到点自动恢复原档位**。
            if (group.hoverActive)
            {
                group.hoverResumeAfterTransfer = true;
                group.hoverResumeAnalytic = group.hoverAnalytic;
                SetHoverCore(group, false);
                Log.Message($"[档位] G{group.GroupId} 起搬 ⇒ **退出悬浮**（搬运与定点保持互斥）——**到点自动恢复{(group.hoverResumeAnalytic ? "解析" : "物理")}档悬浮**");
            }
            else
            {
                group.hoverResumeAfterTransfer = false;
                group.hoverResumeAnalytic = false;
            }

            var pos = group.CurrentOrbit.positionSystem.v;
            var vel = group.CurrentOrbit.velocitySystem.v;
            double mass = GetGroupMass(group, src);
            double aAvg = GetGroupAvailableAccel(group, src);

            group.transferFromPos = pos;
            group.transferFromVel = vel;
            group.transferToPos = targetPos;
            group.transferToVel = targetVel;
            group.transferDvTotal = dvTotal;
            group.transferStartTick = physicsTick;
            group.transferDuration = AnalyticTransferCore.Duration(dvTotal, aAvg);
            group.transferActive = true;
            group.transferMidLogged = false;
            group.transferArriveLogged = false;
            Log.Message($"[搬运] G{group.GroupId} 起搬（解析档）：Δv={dvTotal:F1} m/s｜时长={group.transferDuration:F1} s｜可用 a≈{aAvg:F3} m/s²｜等效耗料率≈{AnalyticTransferCore.PropellantThisStep(mass, dvTotal, ve) / Math.Max(group.transferDuration, 1e-9):F2} kg/s｜余料 {src.TotalPropellantMass:F0} kg");
            return true;
        }

        /// <summary>
        /// **中止解析搬运**（玩家从控制台按「停止」）：清搬运单、**留在当前位置与当前速度**（不"精确落位"到目标）。
        /// 与料尽退出同一条纪律 —— **显式**（一行日志），不许静默消失。
        /// </summary>
        public bool StopAnalyticTransfer(SpaceEntityGroup group, string reason)
        {
            if (group == null || !group.transferActive) return false;
            double u = AnalyticTransferCore.Progress(
                TimeScaleManager.GetPhysicsTickSafe() - group.transferStartTick, group.transferDuration);
            group.transferActive = false;
            group.OrbitDirty = true;
            Log.Message($"[搬运] G{group.GroupId} **中止**（{(reason ?? "无理由")}）：停在当前位置（已到 u={u:F2}）｜Δv 只记到这儿");
            return true;
        }

        /// <summary>本处可用推力加速度（解析搬运估时用）。</summary>
        private double GetGroupAvailableAccel(SpaceEntityGroup group, IPropulsionSource src)
        {
            float f = 0f;
            var nets = src.Networks;
            for (int i = 0; i < nets.Count; i++) f += nets[i].Thrust;
            double mass = GetGroupMass(group, src);
            return mass > 0.0 ? f / mass : 0.0;
        }

        /// <summary>该点是否在大气里（ρ > 0）—— 与解析圆轨守卫**同一条口径**（`AtmosphereModel.IsInsideAtmosphere`）。</summary>
        private static bool IsInsideAtmosphere(SpaceEntityGroup group, CelestialBodyManager mgr, DVector3 pos)
        {
            var body = mgr.GetBodyById(group.CelestialBodyId);
            if (body == null || !body.Atmosphere.IsActive) return false;
            double r = pos.magnitude;
            if (!(r > body.radius)) return true;      // 地表以下也算"大气里"（保守）
            return body.Atmosphere.IsInsideAtmosphere(r - body.radius);
        }

        /// <summary>悬浮的切片 scratch（主线程复用；与操舵那个分开，免得两条路互相清空）。</summary>
        private readonly List<ThrustSlice> hoverSliceScratch = new(4);

        /// <summary>
        /// 物理步首：把各 group 累积的合冲量施加到速度并清零。
        /// 圆轨 group 先退出圆轨（解析位置/速度会被覆盖）；施加后标记轨道脏。
        /// </summary>        // DevMode 逐笔对账的**每步行数上限**（2026-10-02：大批量燃烧组会把这一步刷爆 ⇒ 审计自己成了被测负载）。
        private const int BurnAuditMaxLinesPerStep = 40;
        private int burnAuditLinesThisStep;
        private bool burnAuditTruncatedThisStep;

        private void ApplyPendingImpulses()
        {
            burnAuditLinesThisStep = 0;             // 审计限流（2026-10-02）：每步重置
            burnAuditTruncatedThisStep = false;
            foreach (var group in groups.Values)
            {
                if (!group.IsMovable) continue;
                if (group.pendingImpulse.sqrMagnitude <= 0.0) continue;
                if (group.IsCircularOrbit)
                    group.ExitCircularOrbit();
                // ★ **逐步对账**（2026-09-19）：命令说这一步该给 aCmd·dt，这一行是**状态实际吃到的量**。
                //   悬案：交付端逐项验证精确（每步命令 / 工质 / 方向），但有效能量系统性少 0.34%
                //   （Δa = +8.46 km，而台架同政策全族只有 +0.9~+1.9 km）⇒ 这一步必须逐笔对。
                // ⚠ **2026-10-02 修两处仪器污染**（实测：2040 组来袭场景里这一步被刷到 54–62 ms/步，
                //   而该步真物理只有 ~7 ms ⇒ 量到的是"审计日志自己"；2000 枚弹 × 每枚 60 行 = 12 万行）：
                //   ① **只审与燃烧有关的冲量** —— 轨道保持/平移的微冲量（实测 2e-5 m/s）不进来；
                //      保留 `burnLastCmdDv > 0` 这一支 ⇒ 燃烧收尾那一步（`EndBurn` 已清 `HasActiveBurn`）仍在对账范围内。
                //   ② **每步全局行数上限** —— 否则一个 2040 枚弹的齐射正处在燃烧段时，逐组一行会再次把这一步刷爆。
                if (Prefs.DevMode && group.burnStepAuditCount < 60
                    && (group.HasActiveBurn || group.burnLastCmdDv > 0.0))
                {
                    group.burnStepAuditCount++;
                    double got = group.pendingImpulse.magnitude;
                    group.appliedDvAudit += got;   // ⚠ 先累加**再**打印（上一版打的是"加本笔之前"的值 ⇒ 我据此误判过一次 ✗）
                    if (burnAuditLinesThisStep < BurnAuditMaxLinesPerStep
                        && DiagSamplingAllowed(group.GroupId, Find.TickManager.TicksGame, groups.Count))
                    {
                        burnAuditLinesThisStep++;
                        Log.Message($"[燃烧·对账] G{group.GroupId} 第{group.burnStepAuditCount}笔：命令={group.burnLastCmdDv:F5}｜实收={got:F5}｜差={(group.burnLastCmdDv > 1e-9 ? (got - group.burnLastCmdDv) / group.burnLastCmdDv * 100.0 : 0.0):F2}%｜**含本笔**累计实收={group.appliedDvAudit:F3} m/s");
                    }
                    else if (!burnAuditTruncatedThisStep)
                    {
                        burnAuditTruncatedThisStep = true;
                        Log.Message($"[燃烧·对账] 本步对账行已达上限 {BurnAuditMaxLinesPerStep} 行 ⇒ 其余省略（**记账不受影响**：`appliedDvAudit` 照常累加）");
                    }
                }
                // 2026-10-28 临时探针（用户实报"交付了 Δv 但速度不动"）：只看燃烧头 10 步。
                // 判据：`|v|` 的增量必须≈`|imp|`；不相等 ⇒ 冲量在这条链上被吃/被覆盖。
                bool impulseProbe = Prefs.DevMode && group.impulseProbeSteps++ < 10
                    && (group.HasActiveBurn || group.burnLastCmdDv > 0.0);
                double vBeforeImpulse = group.CurrentOrbit.velocitySystem.magnitude;
                double impMag = group.pendingImpulse.magnitude;

                group.CurrentOrbit.velocitySystem = group.CurrentOrbit.velocitySystem + (SystemVec3)group.pendingImpulse;
                group.pendingImpulse = DVector3.zero;
                group.OrbitDirty = true;

                if (impulseProbe)
                {
                    TickLog($"[冲量落地] G{group.GroupId} |imp|={impMag:F3} |v| {vBeforeImpulse:F3} → "
                        + $"{group.CurrentOrbit.velocitySystem.magnitude:F3}（差 {group.CurrentOrbit.velocitySystem.magnitude - vBeforeImpulse:F3}）");
                }
            }
        }

        /// <summary>仅用于 FinalizeInit 的 JIT 预热：入口处返回，方法体已被完整编译。</summary>
        private static bool warmupPhysicsStep;

        /// <summary>
        /// **§5 电/热闭环**：给每个"有电/热输入"的群跑一步规则（主线程）。
        /// 成本 = O(组数) 的 bool 判断 + 有输入群的 O(1) 算术；输入来自 `RecomputeDerived` 预聚合的 float
        /// ⇒ **物理步里不遍历组件**（§0-1）。老船 `HasPowerOrHeat=false` ⇒ 整条跳过。
        /// </summary>
        // ── 动量轮 + RCS 卸载（2026-10-26 用户裁定）──
        // 口径：引擎全开时按标称推力算偏心力矩 τ，轮子吸收；连续比例卸载；工质从"贴了 rcsSupply 的罐"扣。
        // 位置：主线程、紧跟电/热步（并行物理区只读，扣料是写操作不能进并行区）。
        private const double WheelDesatSeconds = 120.0;
        private const double RcsEffectiveVe = 3000.0;

        internal void StepAllGroupsWheel(float dt)
        {
            if (dt <= 0f) return;
            foreach (var g in groups.Values)
            {
                if (g == null || !g.IsActive) continue;
                if (!g.HasActiveBurn && !g.WheelHasMomentum) continue;

                var inertia = g.InertiaResult;
                DVector3 com = new(inertia.comX, inertia.comY, inertia.comZ);
                bool any = false;
                foreach (int eid in g.GetEntityIds())
                {
                    var e = GetEntityById(eid);
                    if (e == null || e.wheelCapacity <= 0.0) continue;

                    // 坎星魔法动量轮（2026-10-27）：开 ⇒ 引擎力矩**不进轮子**；其余（卸载/扣料）照旧。
                    bool kerbinMagic = PioneerFrameworkLiteMod.Settings?.kerbinMagicWheel ?? false;
                    DVector3 tau = (g.HasActiveBurn && !kerbinMagic)
                        ? e.GetEngineTorqueAboutCom(com, out _)
                        : DVector3.zero;
                    var tank = FindRcsTank(e);
                    double arm = Math.Max(e.BoundsSize.x, Math.Max(e.BoundsSize.y, e.BoundsSize.z)) * 0.5;
                    if (arm <= 0.0) arm = 0.5;

                    var o = WheelDesaturationCore.Step(new WheelDesaturationCore.Inputs(
                        dt, tau, e.wheelMomentum, e.wheelCapacity, WheelDesatSeconds,
                        RcsEffectiveVe, arm, tank?.rcsEfficiency ?? 0f, tank?.massKg ?? 0.0));

                    e.wheelMomentum = o.Momentum;
                    if (o.FuelKg > 0.0 && tank != null) ConsumeRcs(e, tank, o.FuelKg);
                    if (o.Momentum.sqrMagnitude > 1e-12) any = true;
                }
                g.WheelHasMomentum = any;
            }
        }

        private static Components.PropellantTankComponentData? FindRcsTank(SpaceEntity e)
        {
            for (int i = 0; i < e.Parts.Count; i++)
            {
                var p = e.Parts[i];
                if (p == null || p.isDestroyed) continue;
                var t = p.GetComponent<Components.PropellantTankComponentData>();
                if (t != null && t.rcsSupply && !t.leaked && t.massKg > 0.0) return t;
            }
            return null;
        }

        /// <summary>扣 RCS 工质：罐镜像 + 对应推进池（O(1)，不走通用供油计划）。</summary>
        private static void ConsumeRcs(SpaceEntity e, Components.PropellantTankComponentData tank, double kg)
        {
            if (kg <= 0.0) return;
            double take = Math.Min(kg, tank.massKg);
            tank.massKg -= take;
            var def = DefDatabase<PropellantDef>.GetNamedSilentFail(tank.propellantDefName);
            if (def != null)
            {
                for (int i = 0; i < e.propulsion.Count; i++)
                {
                    if (e.propulsion[i].propellant == def)
                    {
                        e.propulsion[i].mass = (float)Math.Max(0.0, e.propulsion[i].mass - take);
                        break;
                    }
                }
            }
            e.InvalidatePropulsion();
        }

        // ── 自动抛级计划（2026-10-27 地表发射演示）──
        // internal（非 public）：仅为 `PioneerFramework.Tests` 可见（AssemblyInfo 已有 InternalsVisibleTo），
        // 供自动抛级开关/读档重建的判据直接读计划表；不进公开 API，运行时零成本。
        internal sealed class AutoStagePlan
        {
            public int GroupId;
            public double SecondsToStage;   // 时间模式（>0）
            public double FuelFraction;      // 燃料模式（>0）：当前级池 ≤ 容量×该比例 ⇒ 抛
            public double MinSeconds;        // 燃料模式的最小燃烧时间
            public double Elapsed;
            public int StagesLeft;
            public double LogAccum;

            /// <summary>探针：前几次抛级判据评估打日志用（2026-10-28，临时）。</summary>
            public int ProbeSteps;
        }

        internal readonly List<AutoStagePlan> autoStagePlans = new();

        public void AddAutoStagePlan(int groupId, double stageEverySeconds, int stages)
        {
            if (stages <= 0 || stageEverySeconds <= 0.0) return;
            autoStagePlans.Add(new AutoStagePlan
            { GroupId = groupId, SecondsToStage = stageEverySeconds, StagesLeft = stages });
        }

        /// <summary>油尽抛级（2026-10-27）：当前活动级池 ≤ 容量×`fuelFraction` 且已烧 `minSeconds` ⇒ 抛最大级号。</summary>
        public void AddAutoStageFuelPlan(int groupId, double fuelFraction, int stages, double minSeconds)
        {
            if (stages <= 0 || fuelFraction <= 0.0 || fuelFraction >= 1.0) return;
            autoStagePlans.Add(new AutoStagePlan
            { GroupId = groupId, FuelFraction = fuelFraction, StagesLeft = stages, MinSeconds = minSeconds });
        }

        /// <summary>撤掉某群的自动抛级计划（不动组上的开关位）。</summary>
        public void CancelAutoStagePlan(int groupId)
            => autoStagePlans.RemoveAll(p => p.GroupId == groupId);

        /// <summary>玩家开关：状态落组上；开 ⇒ 建计划，关 ⇒ 撤计划。</summary>
        public void SetAutoStage(SpaceEntityGroup? group, bool enabled)
        {
            if (group == null) return;
            group.autoStageEnabled = enabled;
            CancelAutoStagePlan(group.GroupId);
            if (!enabled) return;
            AddAutoStageFuelPlan(group.GroupId, group.autoStageFuelFraction, stages: 99, group.autoStageMinSeconds);
        }

        /// <summary>读档后按组上的开关重建计划（计划列表本身不序列化）。</summary>
        internal void RebuildAutoStagePlans()
        {
            autoStagePlans.Clear();
            if (groups == null) return;
            foreach (var g in groups.Values)
                if (g.autoStageEnabled)
                    AddAutoStageFuelPlan(g.GroupId, g.autoStageFuelFraction, stages: 99, g.autoStageMinSeconds);
        }

        private SpaceEntity? FirstEntityOf(SpaceEntityGroup g)
        {
            foreach (int eid in g.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e != null) return e;
            }
            return null;
        }

        private static double FirstTankMass(SpaceEntity e)
        {
            for (int i = 0; i < e.Parts.Count; i++)
            {
                var t = e.Parts[i].GetComponent<Components.PropellantTankComponentData>();
                if (t != null) return t.massKg;
            }
            return -1.0;
        }

        private void StepAutoStagePlans(double dt)
        {
            if (dt <= 0.0 || autoStagePlans.Count == 0) return;
            for (int i = autoStagePlans.Count - 1; i >= 0; i--)
            {
                var plan = autoStagePlans[i];
                var g = GetGroupById(plan.GroupId);
                if (g == null || !g.IsActive) { autoStagePlans.RemoveAt(i); continue; }

                plan.Elapsed += dt;
                var te = FirstEntityOf(g);
                double pool = te != null && te.HasPropulsionData ? te.propulsion[0].mass : -1.0;
                double cap = te != null && te.HasPropulsionData ? te.propulsion[0].capacity : -1.0;
                double tank = te != null ? FirstTankMass(te) : -1.0;

                plan.LogAccum += dt;
                if (plan.LogAccum >= 10.0)
                {
                    plan.LogAccum -= 10.0;
                    double frac = cap > 0.0 ? pool / cap : -1.0;
                    Log.Message($"[发射] G{g.GroupId} r={g.positionBody.magnitude / 1000.0:F1}km "
                        + $"v={g.velocityBody.magnitude:F0}m/s 待抛={plan.StagesLeft} "
                        + $"池={pool:F0}kg 罐={tank:F0}kg 油比={frac:F2} 网络={te?.propulsion.Count ?? 0} 质量={te?.AggregatedStats.mass ?? 0f:F0}kg 燃烧={g.HasActiveBurn}");
                }

                bool due;
                if (plan.FuelFraction > 0.0)
                {
                    // 2026-10-28：燃烧中不切级（先把本级最后一点 ΔV 用完）；熄火/已空才切。
                    due = AutoStageCore.ShouldStageByFuel(pool, cap, plan.FuelFraction,
                        g.HasActiveBurn, plan.Elapsed, plan.MinSeconds);

                    // 2026-10-28 临时探针：抛级判据的**输入**（池/罐容/油比/燃烧）+ 罐件权威账本。
                    if (Prefs.DevMode && plan.ProbeSteps < 8)
                    {
                        plan.ProbeSteps++;
                        double tankSum = SumTankKg(g);
                        TickLog($"[抛级判据] G{g.GroupId} 池={pool:F0}kg 罐容={cap:F0}kg "
                            + $"油比={(cap > 0.0 ? pool / cap : -1.0):F3} 燃烧={g.HasActiveBurn} "
                            + $"已跑={plan.Elapsed:F1}s due={due}｜罐件Σ={tankSum:F0}kg 网络={te?.propulsion.Count ?? 0}"
                            + $" 群质量={(te != null ? GetGroupMass(g, GetGroupPropulsionSource(g)) : 0.0):F0}kg");
                    }
                }
                else
                {
                    plan.SecondsToStage -= dt;
                    due = plan.SecondsToStage <= 0.0;
                }
                if (!due) continue;

                var ng = TrySeparateStage(g, out string reason);
                if (ng == null)
                {
                    Log.Message($"[发射] G{g.GroupId} 抛级失败：{reason}");
                    autoStagePlans.RemoveAt(i);
                    continue;
                }

                plan.StagesLeft--;
                plan.Elapsed = 0.0;
                // 2026-10-28：删掉"母舰 up×1500 占位再点火"——抛级后上升段有制导接手；
                // 1500 m/s 径向冲量既非物理，也会把刚入轨的船打出目标轨道（实测：剩余 1500→1334）。
                Log.Message($"[发射] 抛级 → G{ng.GroupId}；母舰 G{g.GroupId} 在烧={g.HasActiveBurn} 剩余待抛={plan.StagesLeft}");
                if (plan.StagesLeft <= 0) autoStagePlans.RemoveAt(i);
            }
        }

        /// <summary>
        /// **落地船的纯解析档**（2026-10-28 用户裁定）：落地后固连坐标不变，
        /// 每步只需按天体自转把固连点解析成 ECI 位置/速度。
        /// 成本 = O(落地船数) × (2 次三角函数 + 2 次乘)；**不积分、不查重力场、不做碰撞**。
        /// </summary>
        private void StepLandedGroups(CelestialBodyManager mgr, double physicsTick)
        {
            if (mgr == null) return;
            foreach (var kv in groups)
            {
                var g = kv.Value;
                if (g == null || !g.landed || !g.IsActive) continue;
                double omega = Launch.GetSurfaceState.RotationOmega(mgr, g.CelestialBodyId);
                var fixedPos = new DVector3(g.landedFixedX, g.landedFixedY, g.landedFixedZ);
                DVector3 pos = SurfaceFrame.FixedToEci(fixedPos, omega, physicsTick);
                DVector3 vel = SurfaceFrame.SurfaceVelocity(pos, omega);
                g.CurrentOrbit.positionSystem = (SystemVec3)pos;
                g.CurrentOrbit.velocitySystem = (SystemVec3)vel;
                g.OrbitDirty = true;
            }
        }

        /// <summary>
        /// **安全接地 ⇒ 进入 Landed（解析档）**（2026-10-28 用户裁定，替代原来的"落地即销毁"）。
        /// 记录**固连坐标**（自转系里不变），清掉燃烧/角速度，并给碰撞豁免（它已经在面上）。
        /// </summary>
        private void LandGroup(SpaceEntityGroup group, double physicsTick)
        {
            if (group == null) return;
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return;
            double omega = Launch.GetSurfaceState.RotationOmega(mgr, group.CelestialBodyId);

            DVector3 fixedPos = SurfaceFrame.EciToFixed(group.positionBody, omega, physicsTick);
            group.landed = true;
            group.landedFixedX = fixedPos.x;
            group.landedFixedY = fixedPos.y;
            group.landedFixedZ = fixedPos.z;

            group.EndBurn();
            group.angularVelocity = DVector3.zero;
            group.exemptPlanetCollision = true;
            group.OrbitDirty = true;
        }

        private void StepAllGroupsPowerThermal(float dtSeconds)
        {
            foreach (var group in groups.Values)
            {
                if (!group.HasPowerOrHeat) continue;
                group.StepPowerThermal(group.BuildPowerInputs(dtSeconds));
            }
        }

        protected virtual void UpdateAllMovements(TimeScaleManager timeScaleManager)
        {
            if (warmupPhysicsStep) return;   // JIT 预热调用：编译完方法体即返回，不碰状态
            // ★ 2026-10-01 第三轮：**从函数第一行起**计时（此前只圈 `ApplySteering` 之后 ⇒
            //   `dt/physicsTick` 与 `celestialRef.Get()` 这段"未计时头"不在任何阶段里 —— 实测有一条
            //   3.16 ms 的步（t=27782，燃烧中）五段合计只有 78 µs，时间就落在这类缝里）。
            long phSteer = PerfProbe.PhaseClockStart();
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

            // ── **Landed = 纯解析档**（2026-10-28 用户裁定："Landed 状态下纯解析节约性能"）──
            // 落地船的固连坐标不变 ⇒ 每步只按自转解析出 ECI 位置/速度（O(落地船数)，两次三角+一次乘），
            // 不进积分、不燃烧、不参与碰撞（`ComputeGroupPhysics` 与 `CheckPlanetCollision` 都早退）。
            StepLandedGroups(celestialMgr, physicsTick);

            // ── 手动操舵（每物理步）：平移/旋转/SAS 输入应用 + 输入打断清队列（D10）──
            // ★ 2026-10-01：五个阶段各自圈时（`PerfProbe.PhaseClock*`）⇒ 步尖峰那行能直接指认"是哪一段贵"
            ApplySteering(dtSeconds);
            PerfProbe.PhaseClockEnd(PerfProbe.PhaseSteer, phSteer);

            // ── [A8] 解析搬运：每步记账（`m·dv/ve`，与悬浮解析档同一个消耗口）+ 到达清单 ──
            long phTransfer = PerfProbe.PhaseClockStart();
            ApplyAnalyticTransfer(dtSeconds, physicsTick, celestialMgr);
            PerfProbe.PhaseClockEnd(PerfProbe.PhaseAnalyticTransfer, phTransfer);

            // ── [A7] 悬浮控制（每物理步，主线程）：定点误差 → 全矢量推力 → 冲量 + 扣料 ──
            //    放这里与操舵同相：冲量进 `pendingImpulse` ⇒ 下一行的 `ApplyPendingImpulses` 本步立即生效。
            long phHover = PerfProbe.PhaseClockStart();
            ApplyHover(dtSeconds, physicsTick, celestialMgr);
            PerfProbe.PhaseClockEnd(PerfProbe.PhaseHover, phHover);

            // ── 物理步首：统一施加上一步累积的合冲量（burn/碰撞动量交换/爆炸/手动 ΔV），
            //    然后清零。并行区此后只累积新冲量，不直接改速度。──
            long phImpulse = PerfProbe.PhaseClockStart();
            ApplyPendingImpulses();
            PerfProbe.PhaseClockEnd(PerfProbe.PhaseImpulse, phImpulse);

            // ── §5 电/热闭环（2026-10-22 步 4/5）：每物理步、每群一次，**只读派生缓存** ──
            // 成本：O(组数) 的 bool 判断 + 有电/热的群各一次 O(1) 分配算术；**不遍历组件**（§0-1 红线）。
            // 老船（无反应堆/电池/产热件）`HasPowerOrHeat=false` ⇒ 整条跳过。
            // ⚠ 本批只出系数，**消费端接线另批**（推进要动 Δv 记账口径，需裁定；见交接 §4）。
            StepAllGroupsPowerThermal((float)dtSeconds);
            // 动量轮 + RCS 卸载（2026-10-26）：紧跟电/热步，主线程写、并行区只读。
            StepAllGroupsWheel((float)dtSeconds);
            // 自动抛级（2026-10-27 地表发射演示）：主线程、按时间阈值抛级并给下一级点火。
            StepAutoStagePlans(dtSeconds);

            // PERF-5：小规模回退顺序——预分配数组存结果，编组数低于阈值走顺序循环，避免并发容器分配
            long phPhysics = PerfProbe.PhaseClockStart();
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

            // ── R2 修复（2026-09-26 实测抓到崩溃）：**切片 scratch 的会计搬出并行区** ────────────
            // 原来这两个探针调用在 `ComputeGroupPhysics` 里（= 工作线程上）：
            //   · `SliceBuffer` 写**两个静态 Dictionary**（`sliceBufferByGroup` / `sliceGroupByBuffer`）
            //     ⇒ 多线程并发 `set_Item` ⇒ `Dictionary.TryInsert` 内部状态被破坏。
            //     **实测（23:50 会话）**：一次 `AggregateException` 裹 14+ 条内层异常
            //     （`InvalidOperationException` 非并发集合被并发改 + `IndexOutOfRangeException`），栈名确凿：
            //       `Dictionary.set_Item ← PerfProbe.SliceBuffer ← ComputeGroupPhysics ← Parallel.ForWorker`
            //   · **后果不是"探针记不准"，是"那一步没算"**：异常穿出 `Parallel.For` 与 `UpdateAllMovements`
            //     ⇒ **整个物理步被放弃**（RimWorld 打一行 AggregateException 就往下走）。
            // 修法：这两条判的是"group→buffer 映射"的**不变式**，与并行无关 ⇒ 放到**进并行区之前**、
            //   单线程扫一遍（失败发生在破坏之前，且不再走多线程）。
            // ⚠ **放在这里而不是主线程结果循环**：这里有**两分支**（≥32 走 `Parallel.For`、<32 走顺序循环），
            //   而探针在改前是**两条分支都会调**的 ⇒ 只有放在分支之前，两组组数档的 `SliceBufferChecks`
            //   增量才都与改前**逐位相同**（否则 <32 档的计数会凭空消失，把"燃烧路径走过的证据"读没了）。
            // ⚠ 语义等价：探针只比**引用身份**、不看内容 ⇒ 挪到 `activeSlices.Clear()` 之前无差别。
            if (PerfProbe.Enabled)
            {
                for (int pi = 0; pi < movableGroups.Count; pi++)
                {
                    var pg = movableGroups[pi];
                    if (pg.HasActiveBurn) PerfProbe.SliceBuffer(pg, pg.thrustSliceScratch);
                }
            }

            // ── R1 修复（批 0-4 的 ≥32 组验收场景当场抓到，2026-09-26）────────────────────
            // 并行体内会首次触碰 `CelestialBodyManager.systemMembersCache`（空字典）：
            //   ComputeGroupPhysics → ComputeGroupFieldAccelJerk → CollectSystemSources → GetSystemMembers
            // ⇒ 32+ 个线程**同时**对同一个普通 `Dictionary` 做 `set_Item` ⇒ `TryInsert` 内部越界
            //   （桶链被破坏）。实测栈（Player.log 277 次）：
            //     IndexOutOfRangeException at Dictionary.TryInsert ← GetSystemMembers ← CollectSystemSources
            //     ← ComputeGroupFieldAccelJerk ← ComputeGroupPhysics ← <UpdateAllMovements>b__0 ← Parallel.ForWorker
            // **后果不只是抛异常**：那几次迭代的 `physicsResults[i]` 没被赋值 ⇒ 该步结果被丢弃
            //   ⇒ 那些组的 `CurrentOrbit.position` **少更新一步**（实测表现 = 可复现性验收第 1 轮指纹不同）。
            // ⇒ 形态 B（预暖）：**进并行区之前、单线程**把成员表算好。这同时把 `EnsureInitialized`
            //   的惰性初始化也拉到主线程（否则两个线程可能同时初次初始化，那是同一个病的另一种发作）。
            // ⚠ 这里刻意**不加"已预暖集合"**：缓存由 `EnsureInitialized` 清（读档 PostLoadInit 会走到），
            //   再维护一个集合就多出一个"它清了、我没清"的失效点 —— 正是本库吃过的那种漏同步。
            //   代价只是每组一次 O(1) 字典查（缓存命中路径），量级远低于阈值。
            // （`celestialMgr` 在本方法开头已 `if (celestialMgr == null) return;` 收窄为非空 ⇒
            //   这里**不要再写一次空判断**：多余的空判断会把编译器对下方既有调用点的流分析搅乱，
            //   实测多出 3 条 CS8604/CS8602 —— 新增警告必须是 0。）
            if (movableGroups.Count >= PARALLEL_PHYSICS_MIN_GROUPS)
            {
                for (int i = 0; i < movableGroups.Count; i++)
                {
                    int prewarmBodyId = movableGroups[i].CelestialBodyId;
                    if (prewarmBodyId >= 0) celestialMgr.GetSystemMembers(prewarmBodyId);
                }
            }

            // [A3] 进气工质 Def 的**一次解析**（`DefDatabase` 查询只在主线程做；并行区只读静态缓存）。
            // 放在这里与上面同一条理由："进并行区之前、单线程"的那一段。
            // 只在"活大气 且 确有带进气道的组"时才查 —— 两者任一不成立 ⇒ 整条进气路径缺席，不必解析。
            if (AtmosphereModel.AnyAtmosphereActive && IntakeAirDef == null)
            {
                for (int i = 0; i < movableGroups.Count; i++)
                {
                    if (GetGroupIntakeArea(movableGroups[i]) <= 0.0) continue;
                    _ = IntakeAirDef;
                    break;
                }
            }

            // ── S-a（2026-09-27）：**引力源表预取**（主线程、进并行区之前）────────────────
            // 原来 `CollectSystemSources` 每群每步各建一次（N 遍"查成员 + 走父链 + 命中判定 + 构造入列"）；
            // 实测"建表段"占引力场段 **80%** ⇒ 这里按**系统**只建一次，相位里只拷贝（详见管理器的说明）。
            // ⚠ N ≥ 2 才划算（N=1 时"预取 + 拷贝"两次字典操作 ≥ 原来一次收集）⇒ 按组数门控。
            // ⚠ 必须在**相位之外**（`ParallelPhysicsInProgress` 仍为 false）：否则就变成"相位里写共享态"，
            //   会被 G1 守卫当场判红 —— 那正是它该抓的形态。
            if (movableGroups.Count >= 2)
            {
                for (int i = 0; i < movableGroups.Count; i++)
                {
                    int preBodyId = movableGroups[i].CelestialBodyId;
                    if (preBodyId >= 0) celestialMgr.PrefetchSystemSources(preBodyId, physicsTick);
                }
            }

            if (movableGroups.Count < PARALLEL_PHYSICS_MIN_GROUPS)
            {
                for (int i = 0; i < movableGroups.Count; i++)
                    physicsResults[i] = ComputeGroupPhysics(movableGroups[i], dtSeconds, physicsTick, celestialMgr, GetGravityScratch(i), subSteps);
            }
            else
            {
                ParallelPhysicsInProgress = true;
                try
                {
                    Parallel.For(0, movableGroups.Count, i =>
                    {
                        physicsResults[i] = ComputeGroupPhysics(movableGroups[i], dtSeconds, physicsTick, celestialMgr, GetGravityScratch(i), subSteps);
                    });
                }
                finally
                {
                    ParallelPhysicsInProgress = false;   // try/finally：并行体抛异常时也不能把相位留在 true
                }
            }
            PerfProbe.PhaseClockEnd(PerfProbe.PhaseGroupPhysics, phPhysics);

            // ★ 主循环应用段（含燃烧诊断/日志、工质账、EndBurn、计划节点处理）：单独圈起来 ——
            //   这条路上有 **大量日志**（`[燃烧·对账]`/`[燃烧·诊断]`），是"步尖峰"的头号嫌疑之一。
            long phApply = PerfProbe.PhaseClockStart();
            var groupsToCheck = groupsToCheckScratch;
            if (PerfProbe.Enabled) PerfProbe.GroupsToCheckLeftover(groupsToCheck.Count); // 探针：上一步遗留项（应被清空）
            groupsToCheck.Clear();

            // 只遍历本步有效的前 Count 项：复用数组时尾部仍是上一步的结果，不得参与本步
            // ── R3（2026-09-26）：**节流判据每步只判一次** ─────────────────────────────
            // 原判据写在下面的**每组循环里** ⇒ 第一个燃烧组把 `BurnDiagNextTick` 推到 +10 s，
            // 同一步里后面的组读到新值就**全被跳过**。实测（64 组全烧）：每 10 s 窗口**只有 1 个组**
            // 被诊断（赢的是迭代序第一个），其余 63 个一声不响 ⇒ 这套"用来发现燃烧异常"的仪器
            // **自己丢掉了 98.4% 的样本**。
            // ⚠⚠ **这一条不是竞态、是逻辑错**：光把它挪到主线程**不解决问题**（第一组照样推走它）
            //   —— 是我先把判据升成"每个燃烧组都被诊断到"才当场发现搬了个空。
            //   正解 = 把"到点了"这个**决定提到循环之外、每步只算一次**，本步**每个**燃烧组都诊断。
            bool burnDiagDue = physicsTick >= BurnDiagNextTick;
            if (burnDiagDue) BurnDiagNextTick = physicsTick + 10.0;

            long apBurn = PerfProbe.ApplySubClockStart();   // apply 细分①：燃烧/工质/诊断日志（2026-10-01 第二轮）
            for (int ri = 0; ri < movableGroups.Count; ri++)
            {
                var result = physicsResults[ri];
                if (!groups.TryGetValue(result.GroupId, out var group)) continue;

                // §5（用户 2026-10-26 裁定：欠电 ⇒ 拒绝点火）：本步推进拿不到全额电力 ⇒
                // 直接熄火（**主线程唯一写点**；与"工质尽"的 EndBurn 同一条退场路径）。
                // 并行区只置 `BurnStarvedNoPower` 标记（不碰 group 状态）。
                if (result.BurnStarvedNoPower)
                {
                    group.BurnEndedByPower = true;
                    group.EndBurn();
                }

                // R3+R4：诊断块在**主线程**跑（并行体不再碰静态节流与日志）；"到点了"由上面
                // **每步一次**的决定给出 ⇒ 本步**所有**燃烧组都轮到（不再只轮到第一个）。
                // ⚠ 用 `group.HasActiveBurn`（不是 `result.HasActiveBurn`）：与并行体当时判的是**同一份字段**，
                //   而此时主循环还没执行下面的 `EndBurn()` ⇒ 与改前的进入条件逐位一致。
                long apBd = PerfProbe.ApplySubClockStart();   // 块①a：燃烧诊断 + DiagLines 日志
                if (burnDiagDue && group.HasActiveBurn)
                    RunBurnDiagnostics(group, celestialMgr, physicsTick);

                // ── R2（同批）：`SliceAlias` 搬到这里判 ──
                // 判据语义不变：**本步快照必须指回本组自己的 scratch**（否则并行区里被别的组改写过）。
                // 改前它在并行体内用局部量比（恒真，只能抓"赋值被覆盖"）；现在直接比**快照**与 scratch，
                // 比改前更能说明问题，且判的是同一件事。只在有快照时判（与改前的 `burnDv > 0` 同门槛）。
                if (PerfProbe.Enabled && result.ActiveSlices != null)
                    PerfProbe.SliceAlias(ReferenceEquals(result.ActiveSlices, group.thrustSliceScratch),
                        "G" + group.GroupId);

                // R4：并行体只把罕见事件读数挂在这里 ⇒ **主线程**统一打（顺序 = 组序，稳定）。
                if (result.DiagLines != null)
                {
                    for (int di = 0; di < result.DiagLines.Count; di++) Log.Message(result.DiagLines[di]);
                }
                PerfProbe.ApplySubClockEnd(PerfProbe.ApplySubBurnDiag, apBd);

                long apBf = PerfProbe.ApplySubClockStart();   // 块①b：工质账 + EndBurn + 收工结算
                bool burnEndedThisStep = false;

                // ── [A3] 进气充填入账（主线程唯一写点；与"扣料"同在块①b，同相）──
                // 前提量在并行区算好（`IntakeFillKg`）⇒ 这里只做写入，不查大气。
                if (result.IntakeFillKg > 0.0)
                    ApplyGroupIntakeFill(group, result.IntakeFillKg);

                if (result.HasActiveBurn)
                {
                    // 多网络按切片流率比例分配消耗并扣各自工质；全部网络工质尽 → 燃烧终止
                    float totalConsumed = 0f;
                    var slices = result.ActiveSlices;
                    int activeCount = slices != null ? slices.Count : 0;
                    int exhaustedCount = 0;
                    // slices != null 并入判据（activeCount > 0 本就蕴含它）⇒ 块内 `slices` 被编译器收窄，免 `!`
                    if (slices != null && activeCount > 0)
                    {
                        float mfTotal = 0f;
                        for (int i = 0; i < activeCount; i++)
                            mfTotal += slices[i].massFlow;
                        // ★ 执行器**实际所用**网络的 ΣF·dt 与 Σṁ·dt ⇒ 真实 ve = ΣF/Σṁ 的唯一来源。
                        //   ⚠ 用"全部网络合并"的 `EffectiveVe` 会**白送 Δv 假警报**：2026-09-19 实测
                        //   过推档船（主网 ve=4000/ṁ=140 + RCS 网 ve=1500/ṁ=20）= 合并 ve 3687.5，
                        //   而一次只烧主网 ⇒ 808.5 施加 vs "749.6 的质量口径" 被误读成"白送 58 m/s"。
                        double fSum = 0.0, mfSum = 0.0;
                        for (int i = 0; i < activeCount; i++)
                        {
                            fSum += slices[i].thrust * dtSeconds;
                            mfSum += slices[i].massFlow * dtSeconds;
                        }
                        group.burnThrustDt += fSum;
                        group.burnFlowDt += mfSum;
                        for (int i = 0; i < activeCount; i++)
                        {
                            var slice = slices[i];
                            float share = mfTotal > 0f ? (float)result.PropellantConsumed * (slice.massFlow / mfTotal) : 0f;
                            float actual = ConsumeGroupPropellant(group, slice.network.propellant, share);
                            totalConsumed += actual;

                            // 2026-10-28 临时探针（用户实报"只有第一级能用"）：**三本账同框**——
                            // 推进网络的池 / 罐件之和 / 群质量。抛级判据读的是第一本，零件化后权威是第二本。
                            if (Prefs.DevMode && group.impulseProbeSteps <= 10 && slice.network.propellant != null)
                            {
                                var srcDbg = GetGroupPropulsionSource(group);
                                double pool = srcDbg?.GetPropellantMass(slice.network.propellant) ?? 0.0;
                                TickLog($"[燃料账] G{group.GroupId} 扣 {share:F1}kg（实扣 {actual:F1}）"
                                    + $"｜网络池={pool:F1}kg｜罐件Σ={SumTankKg(group):F1}kg"
                                    + $"｜群质量={GetGroupMass(group, srcDbg):F1}kg");
                            }
                            if (actual < share - 0.001f)
                                exhaustedCount++;
                            // [A5] 附带进气同步扣（空气）：按**同一个比**——`每单位主工质要多少空气`
                            //   = `gd.flowKgPerSec / ṁ`（注册时就是按 ṁ 累的 ⇒ 与引擎配比等价）。
                            //   扣的量恒由上面的 cap 保证 ≤ 存量（cap 就是按存量算的）⇒ 这里不会抽穿。
                            var draws = slice.network.gasDraws;
                            if (draws != null && actual > 0f && slice.network.MassFlow > 0f)
                            {
                                for (int g = 0; g < draws.Count; g++)
                                {
                                    var gd = draws[g];
                                    double perFuel = gd.flowKgPerSec / slice.network.MassFlow;
                                    double airNeed = AirBreathingCore.AirRequired(actual, perFuel);
                                    if (airNeed > 0.0 && gd.propellant != null)
                                        ConsumeGroupPropellant(group, gd.propellant, (float)airNeed);
                                }
                            }
                        }
                    }
                    group.burnConsumedPropellant += totalConsumed;

                    // 2026-10-28 主线程侧探针（与 `[燃烧·断油]` 配对）：燃烧中**一滴没扣** ⇒ 打两本账。
                    // 判读：`网络池` 与 `罐件Σ` 一起不动 ⇒ 供油计划空了；只有池不动 ⇒ 镜像分叉。
                    if (Prefs.DevMode && totalConsumed <= 1e-3)
                    {
                        group.burnDrySteps++;
                        if (group.burnDrySteps <= 5 || group.burnDrySteps % 50 == 0)
                        {
                            var srcDry = GetGroupPropulsionSource(group);
                            double activeTankSum = 0.0;
                            foreach (int eidDry in group.GetEntityIds())
                            {
                                var eDry = GetEntityById(eidDry);
                                if (eDry != null) activeTankSum += SumActiveStageTankMass(eDry);
                            }
                            TickLog($"[燃烧·断油·主线程] G{group.GroupId} 第{group.burnDrySteps}步零扣料："
                                + $"网络池={srcDry?.TotalPropellantMass ?? 0.0:F1}kg 罐件Σ={SumTankKg(group):F1}kg "
                                + $"活动级罐Σ={activeTankSum:F1}kg "
                                + $"切片={result.ActiveSlices?.Count ?? 0} 命令={group.burnLastCmdDv:F1} 剩余={group.remainingBurnDV:F1}");
                        }
                    }
                    else
                    {
                        group.burnDrySteps = 0;
                    }

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
                        Log.Message($"[Burn] Group {group.GroupId} burn completed: 实际时长={(group.burnActualStartTick >= 0.0 ? physicsTick - group.burnActualStartTick : 0.0):F0}s appliedDv={group.appliedBurnDV:F1} m/s actualDv(mass,veBurn={veBurn:F0})={actualDv:F1} m/s（比值 {(group.appliedBurnDV > 1e-9 ? actualDv / group.appliedBurnDV : 0.0):F4}）propellant={group.burnConsumedPropellant:F1} kg (propellantOut={allNetworksExhausted})｜全部网络合并 ve={veMerged:F0}（**非执行器所用**，仅参考）");
                        group.burnActualEndTick = physicsTick;
                        LogNonRootBurnState(group, physicsTick, "收工后");   // 批5.B 探针（见方法注释）
                        LogRendezvousTrack(group, "烧完", group.appliedBurnDV);   // 交会轨迹读数：烧完那一刻
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
                    LogRendezvousTrack(group, "烧完", group.appliedBurnDV);   // 交会轨迹读数：烧完那一刻
                    burnEndedThisStep = true;
                }
                PerfProbe.ApplySubClockEnd(PerfProbe.ApplySubBurnFuel, apBf);

                long apBp = PerfProbe.ApplySubClockStart();   // 块①c：调试日志 + 轨道保持 + 巡检探针 + 验收 Tick
                // ── 无 Map 主推/RCS 机动详细日志（DebugDetailedThrust，D15 改动验证）──
                // 每物理步：选网（主/RCS/兜底）、主推轴-目标夹角、Δv/余 dv、双罐燃料、燃尽抑制、转向角速度。
                if (DebugDetailedThrust && result.NoMapThrust
                    && DiagSamplingAllowed(group.GroupId, Find.TickManager.TicksGame, groups.Count))
                {
                    double angleDeg = Mathf.Rad2Deg * Mathf.Acos((float)Math.Max(-1.0, Math.Min(1.0, result.MainDot)));
                    string sel = result.UseMainSel && result.UseRcsSel ? "主+RCS"
                        : result.UseRcsSel ? "RCS" : result.UseMainSel ? "主" : "无";
                    if (result.RcsFallbackSel) sel += "(兜底)";
                    string alignState = result.AligningMain ? "对准中" : (result.UseMainSel ? "已对准" : "全向");
                    string note = result.SuppressAutoBurnEnd ? "  [!RCS尽主料存→不终burn]" : "";
                    Log.Message($"[Thrust] G{group.GroupId} tick={Find.TickManager.TicksGame} {alignState} | 用网={sel} | axis·burn={result.MainDot:F3}({angleDeg:F1}°) | Δv={result.AppliedDv:F1} m/s 余dv={group.remainingBurnDV:F1} | 耗={result.PropellantConsumed:F2}kg | {NoMapFuelSummary(group)}{note} | ω={(float)group.angularVelocity.magnitude * Mathf.Rad2Deg:F1}°/s");
                }

                // ── 自动对准详细日志（DebugDetailedSteering）：对准开始/完成/进度 ──
                if (DebugDetailedSteering && result.HasActiveBurn)
                {
                    bool aligningNow = result.ActiveSlices == null || result.ActiveSlices.Count == 0;
                    bool wasAligning = alignStateByGroup.TryGetValue(group.GroupId, out var prevAlign) && prevAlign;
                    if (aligningNow && !wasAligning)
                    {
                        DVector3 mainAxisSystem = GetMainThrustAxisSystem(group);
                        Log.Message($"[Align] Group {group.GroupId} 自动对准开始：burnDir=({group.burnDirectionSystem.x:F2},{group.burnDirectionSystem.y:F2},{group.burnDirectionSystem.z:F2}) 主轴世界=({mainAxisSystem.x:F2},{mainAxisSystem.y:F2},{mainAxisSystem.z:F2})");
                    }
                    else if (!aligningNow && wasAligning)
                    {
                        Log.Message($"[Align] Group {group.GroupId} 对准完成 → 进入 burn（主轴进入 {Mathf.Rad2Deg * Mathf.Acos(Mathf.Clamp(SpaceGameComp.DefaultThrustConeCos, -1f, 1f))}° 矢量锥）");
                    }
                    else if (aligningNow && Find.TickManager.TicksGame >= nextAlignLogTick)
                    {
                        DVector3 mainAxisSystem = GetMainThrustAxisSystem(group);
                        double dot = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(mainAxisSystem, group.burnDirectionSystem.normalized)));
                        Log.Message($"[Align] Group {group.GroupId} 对准中：主轴=({mainAxisSystem.x:F2},{mainAxisSystem.y:F2},{mainAxisSystem.z:F2}) vs burnDir 夹角 {Mathf.Rad2Deg * Mathf.Acos((float)dot):F1}° 角速度={(float)group.angularVelocity.magnitude * Mathf.Rad2Deg:F2}°/s");
                        nextAlignLogTick = Find.TickManager.TicksGame + 30;
                    }
                    alignStateByGroup[group.GroupId] = aligningNow;
                }

                if (result.HasAngularVelocityChange)
                    group.angularVelocity = result.NewAngularVelocity;

                if (result.NeedsUpdate)
                {
                    // 2026-10-28 临时探针（与 [冲量落地] 配对）：物理步输出要能看见冲量，
                    // 否则就是"积分读的是加冲量之前的状态"。
                    bool physProbe = Prefs.DevMode && group.impulseProbeSteps <= 10
                        && (group.HasActiveBurn || group.burnLastCmdDv > 0.0);
                    double vBeforeCommit = group.CurrentOrbit.velocitySystem.magnitude;
                    if (physProbe)
                    {
                        TickLog($"[物理步输出] G{group.GroupId} NewV={result.NewVelocity.magnitude:F3} "
                            + $"当前V={vBeforeCommit:F3}（提交后会变成 NewV）");
                    }
                    var newOrbit = new OrbitState
                    {
                        positionSystem = (SystemVec3)result.NewPosition,
                        velocitySystem = (SystemVec3)result.NewVelocity,
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
                    // 轨道保持消耗工质：Δm ≈ m·ΔV/ve（小 ΔV 线性近似），按**启用且同类**网络 ṁ 比例分配。
                    // 2026-10-27：与燃烧共用选网掩码与类属（`burnUseRcs` ⇒ 只用 RCS 网，否则主网），
                    // 不再"所有网络按 EffectiveVe 混算"（那会让维持轨道偷偷烧 RCS / 拉低等效 ve）。
                    var okSrc = GetGroupPropulsionSource(group);
                    bool okExhausted = false;
                    if (okSrc == null)
                    {
                        okExhausted = true;
                    }
                    else
                    {
                        var mask = group.GetNetworkMask(okSrc);
                        var nets = okSrc.Networks;
                        // 类属：玩家/机动选了 RCS ⇒ RCS；否则优先主网；**只有 RCS 网的船**才用 RCS 维持。
                        bool hasMainNet = false;
                        for (int i = 0; i < nets.Count; i++)
                        {
                            var n0 = nets[i];
                            if (n0.MassFlow > 0f && n0.propellant != null && mask[i] && !n0.HasRcsEngines)
                            { hasMainNet = true; break; }
                        }
                        bool rcsClass = group.burnUseRcs || !hasMainNet;
                        double sumF = 0.0, mfTotal = 0.0;
                        for (int i = 0; i < nets.Count; i++)
                        {
                            var n = nets[i];
                            if (n.MassFlow <= 0f || n.propellant == null || !mask[i]) continue;
                            if (n.HasRcsEngines != rcsClass) continue;   // 同一时刻只用一类
                            if (n.AvailableMass(okSrc) <= 0f) continue;
                            sumF += (double)n.Ve * n.MassFlow;
                            mfTotal += n.MassFlow;
                        }

                        if (sumF <= 0.0 || mfTotal <= 0.0)
                        {
                            okExhausted = true;
                        }
                        else
                        {
                            double okVe = sumF / mfTotal;
                            double okMass = GetGroupMass(group, okSrc);
                            double dmTotal = okMass * result.OrbitKeepingDvCost / okVe;
                            int okActive = 0, okExhaustedCount = 0;
                            for (int i = 0; i < nets.Count; i++)
                            {
                                var n = nets[i];
                                if (n.MassFlow <= 0f || n.propellant == null || !mask[i]) continue;
                                if (n.HasRcsEngines != rcsClass) continue;
                                okActive++;
                                double share = dmTotal * (n.MassFlow / mfTotal);
                                float actual = ConsumeGroupPropellant(group, n.propellant, (float)share);
                                if (actual < share - 0.001) okExhaustedCount++;
                            }
                            okExhausted = okActive == 0 || okExhaustedCount >= okActive;
                        }
                    }
                    if (okExhausted)
                    {
                        group.ExitOrbitKeeping();
                        Log.Message($"[OrbitKeeping] Group {group.GroupId} propellant exhausted, exited orbit-keeping mode.");
                    }
                }

                // 批6 S3-3d：`positionBody`/`velocityBody` 已**从"步末写的滞后副本"改成现算属性** ⇒
                // 这里不再写副本（实测滞后 ≤ 一步、月球档 ≈1.8 km，与交会门同阶 ⇒ 读现算值才是对的）。
                // `result.SyncBodyPos/Vel` 仍留给下面的巡检探针用（它是那一步的系统系偏移）。

                // 调用点先判：绝大多数编队没有交会引导/验收场景，没必要每步付组件/天体查询，
                // 也避免为无关编队首次触发这两条调试路径的 JIT（2026-10-19 尖峰归因：块①c 2.8ms）。
                if (group.HasRendezvousGuidance)
                {
                    LogNonRootRendezvousState(group, physicsTick,
                        dtSeconds > 0.0 ? result.OrbitKeepingDvCost / dtSeconds : 0.0,
                        result.SyncBodyPos, result.SyncBodyVel);   // 批5.C 巡检探针（"全打上"版）
                }
                if (MoonRendezvousAcceptanceRunner.IsArmed)
                    MoonRendezvousAcceptanceRunner.Tick(physicsTick, this);   // 绕月交会：自动判定（每步查一次）
                PerfProbe.ApplySubClockEnd(PerfProbe.ApplySubBurnProbe, apBp);

                groupsToCheck.Add(group.GroupId);
            }
            PerfProbe.ApplySubClockEnd(PerfProbe.ApplySubBurn, apBurn);

            if (PerfProbe.Enabled) PerfProbe.GroupsToCheckConsume(groupsToCheck.Count, movableGroups.Count);
            long apCollision = PerfProbe.ApplySubClockStart();   // apply 细分②：碰撞检查
            foreach (int groupId in groupsToCheck)
            {
                if (!groups.TryGetValue(groupId, out var group)) continue;
                CheckPlanetCollision(group, physicsTick);   // ⚠ 传物理时钟：撞击判据与经纬度都用它
            }
            PerfProbe.ApplySubClockEnd(PerfProbe.ApplySubCollision, apCollision);

            // ── 每 240 真实 tick（4秒）检查一次流速：有机动→Normal，无机动→恢复设置 ──
            // 注意：必须用真实 tick 而非 physicsTick——Combat 模式下 physicsTick 以 1/60 慢速推进，
            // 若以其为基准，射弹消失后要等 16×60=960 真实 tick 才恢复，产生"锁死 1/60x"的错觉。
            long apTimeScale = PerfProbe.ApplySubClockStart();   // apply 细分③：240 真实 tick 块（时标/降档/收尾强制）
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
            PerfProbe.ApplySubClockEnd(PerfProbe.ApplySubTimeScale, apTimeScale);

            // ── 执行到期机动 ──
            long apManeuver = PerfProbe.ApplySubClockStart();   // apply 细分④：机动/节点处理（意图重解・预对准・点火）
            foreach (var group in groups.Values)
            {
                // （2026-09-19 的"[机动循环·诊断]"已撤：它服务的是"108° 对准"那个案子，
                //   根因已查明 = 场景提前量不足 + 燃烧居中吃掉对准时间 ⇒ 结论已进台账，探针退役别刷屏。）

                if (group.plannedManeuvers.Count == 0)
                {
                    // 自动对准目标消失（清队列/取消机动）⇒ 消旋；手动旋转/SAS 开着时不碰 ω（随后会写）。
                    bool hadAutoAlign = group.pendingAlignDir.sqrMagnitude > 1e-12;
                    group.pendingAlignDir = DVector3.zero;
                    bool manualRotation = SteeringController.IsOpen
                        && SteeringController.GroupId == group.GroupId
                        && (SteeringController.AnyRotation || SteeringController.SasKind != SasTargetKind.None);
                    if (hadAutoAlign && !manualRotation)
                        group.angularVelocity = DVector3.zero;
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
                //   ⇒ 本条进入**预点火窗口**后，姿态让给**本条自己的方向**（见下方门前的 `burnDirectionSystem` 设置）。
                //   窗口外仍给下一条预对准（保住"排队就开始转"的原有收益 ✓）。
                if (group.abNoPreAlign) continue;   // 对照开关：不做预对准
                bool preAlignCurrent = physicsTick >= pm.executeTick - ManeuverPreBurnLookaheadTicks;
                // ★★ **预对准必须瞄“节点时刻”的方向**（2026-09-21，按 MJ 的“同一个 `_worldDirection`”）——
                //   执行器的靶 = 意图轨道在 **UT** 的速度 − 活轨道在 **UT** 的速度
                //   （点火前两项都固定 ⇒ 方向固定 ✓）。
                //   而本处原来把 R/T/N 分量用 `GetDeltaVSystem(localPos, localVel, …)`在
                //   **当前时刻**重新表达 ✗✗ ⇒ 两者相差 **`ω·(UT − now)`**：
                //   预对准从“排队就开始转”（可能早一整圈）⇒ 节点在 800 s 外时差 **~49°**、
                //   更早时差到几十上百度 ⇒ **正是日志里 74° / 93° 的带角度点火** ✗✗。
                //   （同一个“锚点必须是节点时刻”的教训，与 `SetBurnSolution` 那一条同族 ✓）
                DVector3 preAlignDv;
                if (pm.plannedDvBody.sqrMagnitude > 0.0)
                {
                    preAlignDv = pm.plannedDvBody;   // 已是惯性/world 向量 ⇒ 无需再分解
                }
                else if (celestialMgr != null && OrbitAdapter.TryToOrbitView(group, celestialMgr, pm.executeTick, out OrbitView ovPre))
                {
                    // 在**节点时刻**的位姿上分解（与 `OrbitAdapter.TryQueuePlan` 同口径 ✓）
                    (DVector3 rPre, DVector3 vPre) = ovPre.GameFrameStateAtUT(pm.executeTick);   // 出边口（批6 S2）
                    preAlignDv = OrbitMath.GetDeltaVSystem(rPre, vPre, pm.radialDV, pm.tangentialDV, pm.normalDV);
                }
                else
                {
                    preAlignDv = OrbitMath.GetDeltaVSystem(group.positionBody, group.velocityBody, pm.radialDV, pm.tangentialDV, pm.normalDV);
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
                    DVector3 pmDirNow = pm.plannedDvBody.sqrMagnitude > 0.0
                        ? pm.plannedDvBody
                        : OrbitMath.GetDeltaVSystem(group.positionBody, group.velocityBody,
                            pm.radialDV, pm.tangentialDV, pm.normalDV);
                    if (pmDirNow.sqrMagnitude > 1e-12)
                    {
                        group.burnDirectionSystem = pmDirNow.normalized;
                        group.pendingAlignDir = group.burnDirectionSystem;
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
                // ⚠ 2026-10-01：**挂到 `DebugDetailedSteering` 开关上** —— 它是长滑行段每 10 s 一行的
                //    无条件打点（本轮一轮 4,570 行、占日志 38%，把"唯一的人读取证通道"埋了），
                //    与邻居（`[Thrust]`/`[Align]`/`[燃烧·诊断]`）同一待遇；要看时把它打开即可。
                if (DebugDetailedSteering && group.pendingAlignDir.sqrMagnitude > 1e-12 && physicsTick >= PreAlignDiagNextTick)
                {
                    PreAlignDiagNextTick = physicsTick + 10.0;
                    Log.Message($"[预对准·诊断] G{group.GroupId} 夹角={MainAxisBurnAngleDeg(group, group.pendingAlignDir):F1}° |ω|={group.angularVelocity.magnitude * Mathf.Rad2Deg:F3}°/s（上限 {group.MaxAngularRate * Mathf.Rad2Deg:F1}）节点 T-{pm.executeTick - physicsTick:F0}s");
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
                // maneuverSrc 非空凭据：null ⇒ networks 实参为 null ⇒ Estimate 在 active.Count==0 处早退（BurnCalculator.cs:78），不触 src
                var mEst = BurnCalculator.Estimate(dvMag, (float)mDry, maneuverSrc!,
                    maneuverSrc != null ? maneuverSrc.Networks : null, pm.useNetworks);
                if (!mEst.Feasible)
                {
                    string reason;
                    if (mEst.Networks == null || mEst.Networks.Count == 0)
                    {
                        reason = "未安装引擎或未启用任何工质网络（无法执行机动）";
                        Log.Message($"[Maneuver] BLOCKED (no thruster): G{group.GroupId}");
                    }
                    else if (mEst.RequiredPropellant <= 0f)
                    {
                        reason = "机动 ΔV 为 0 或无法分配";
                        Log.Message($"[Maneuver] BLOCKED (zero dv): G{group.GroupId}");
                    }
                    else
                    {
                        // 找到第一个不可行的网络，给出明细
                        var badNet = mEst.Networks.Find(ne => !ne.Feasible);
                        if (badNet.Propellant != null)
                            reason = $"工质不足：网络「{badNet.Propellant.label}」需 {badNet.RequiredPropellant:F0} kg，当前 {badNet.Available:F0} kg（最大可达 ΔV {badNet.MaxDv:F0} m/s）";
                        else
                            reason = $"工质不足（总需 {mEst.RequiredPropellant:F0} kg，最大可达 ΔV {mEst.MaxDeltaV:F0} m/s）";
                        Log.Message($"[Maneuver] BLOCKED (insufficient propellant): G{group.GroupId} need={mEst.RequiredPropellant:F1}kg maxDV={mEst.MaxDeltaV:F1}m/s");
                    }
                    Messages.Message($"Group {group.GroupId} 机动被拒绝：{reason}", MessageTypeDefOf.RejectInput);
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
                    if (mEst.BurnSeconds > 0f && pm.plannedDvBody.sqrMagnitude > 0.0
                        && OrbitAdapter.TryToOrbitView(group, celestialMgr, physicsTick, out OrbitView ovF)
                        && BurnArc.TryPredict(ovF, pm.executeTick, pm.plannedDvBody,   // 两系同轴（P4）⇒ 直传
                               mEst.BurnSeconds, group.burnPhaseShift + BurnArc.ExecutionDelaySeconds,
                               out BurnArc.Result fRes, out _))
                        Log.Message($"[对照·相位] G{group.GroupId} 强制相位 {group.burnPhaseShift:+0.0;-0.0;0.0}s｜模型预测 e={fRes.eccentricity:E3}");
                }
                // ⚠ **本轮（"MJ 优先"第 5 件）起，自动扫描只服务退路律**：它最小化的是**冻结模型**的
                //   预测 e，而活差额律按构造就把那个 e 归零 ⇒ 再扫就是"用旧模型优化新执行器"
                //   （两份权威 ✓ J2）。判据与点火块记 `burnTargetV` 的条件**同一句**
                //   （`abFreezeDirection` 是对照用的人为冻结 ⇒ 那时走退路律，扫描照旧有意义 ✓）。
                bool liveDeficitLaw = pm.owner != PlanOwner.Executor && !group.abFreezeDirection;
                if (!liveDeficitLaw && !group.burnPhaseSwept && mEst.BurnSeconds > 0f && pm.plannedDvBody.sqrMagnitude > 0.0
                    && OrbitAdapter.TryToOrbitView(group, celestialMgr, physicsTick, out OrbitView ovSweep))
                {
                    var shiftSb = new System.Text.StringBuilder();
                    // ── 批7 #3（2026-09-30）：7 档预测**彼此独立** ⇒ 可并行。独立性依据（逐行读过）：
                    //    `BurnArc.TryPredict` 与它下面那一串全是**纯静态函数**、类里**零静态可变状态**；
                    //    实测单次扫描 **6.389 ms**（台架 `BurnArcPhaseScanCostTests`）—— 它是"最慢步"的大头之一。
                    //   ⚠ **刻意不嵌套**：本段跑在物理相位的 `Parallel.For` 里。
                    //      · 外层**已并行**（≥32 组）⇒ 再开内层只会跟外层抢同一个线程池 ⇒ 那时**串行**
                    //        （外层本来就把这些活摊到各核了）；
                    //      · 外层**串行**（<32 组 —— 正是"一两艘船在机动"的常见档）⇒ 内层并行真削尖峰。
                    //   归约按 **sh 升序串行**做 ⇒ 与原来"先到先得（严格 `<`）"**同语义、逐位相同**。
                    const int ShiftCount = 7;
                    var shiftE = new double[ShiftCount];
                    if (ParallelOps.InParallelRegion)
                    {
                        for (int kk = 0; kk < ShiftCount; kk++)
                            shiftE[kk] = PredictOneShift(ovSweep, pm, mEst, -6.0 + 2.0 * kk);
                    }
                    else
                    {
                        ParallelOps.For(ShiftCount, 1e-3, kk =>
                            shiftE[kk] = PredictOneShift(ovSweep, pm, mEst, -6.0 + 2.0 * kk));
                    }

                    for (int kk = 0; kk < ShiftCount; kk++)
                    {
                        double sh = -6.0 + 2.0 * kk;
                        if (double.IsNaN(shiftE[kk])) continue;      // 该档预测失败（与原 `continue` 同语义）
                        double e = shiftE[kk];
                        if (Math.Abs(sh) < 1e-9) nominalE = e;
                        shiftSb.Append(System.Globalization.CultureInfo.CurrentCulture, $"{sh:+0;-0;0}").Append("s→").Append(System.Globalization.CultureInfo.CurrentCulture, $"{e:E2}").Append(' ');
                        if (e < bestE) { bestE = e; group.burnPhaseShift = sh; }
                    }
                    group.burnPhaseSwept = true;   // 每机动只扫一次
                    Log.Message($"[燃烧·弧补偿] G{group.GroupId} 相位扫描：{shiftSb}⇒ 选 {group.burnPhaseShift:+0;-0;0}s（预测 e={bestE:E2}，居中档 {nominalE:E2}）");
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
                // ★★ **门拒一次 ⇒ 重算一次方向**（2026-09-21 实测）：门比的是 `group.burnDirectionSystem`，
                //   而它是**某一拍取下的快照**；活轨道一直在动 ⇒ 同一个快照与"节点处的真方向"
                //   会缓慢偏离。船预对准到 `AlignMainAxis` 的**死区**（`DefaultThrustAlignCos`≈1.5°）就停转，
                //   而门要 2.0° ⇒ **只剩 0.8° 裕度**，方向一漂过去就**永远点不着** ✗
                //   （实测 `带角度点火：残留 79.5°` / `等对准：主推轴还差 143.0°` 之后卡死）。
                //   ⇒ 拒绝的**同时**把方向刷成"此刻算出来的"（与预对准同一个算式），下一拍再比。
                //   这把"等对准"从"等一个死数"变成"追一个活数"，与 MJ 每帧重取 `_worldDirection` 同构。
                // ⚠ **门必须严格宽于死区**（`IgnitionAlignGateDeg` > 死区），否则船收心后仍在门外 ⇒ 死锁。
                //   这句在 `OnStartup` 里用 `Debug.Assert` 钉住（见 `AssertAlignGateWiderThanDeadzone`）。
                if (MainAxisBurnAngleDeg(group, group.burnDirectionSystem) > IgnitionAlignGateDeg)
                {
                    DVector3 pmDirGate = pm.plannedDvBody.sqrMagnitude > 0.0
                        ? pm.plannedDvBody
                        : OrbitMath.GetDeltaVSystem(group.positionBody, group.velocityBody,
                            pm.radialDV, pm.tangentialDV, pm.normalDV);
                    if (pmDirGate.sqrMagnitude > 1e-12)
                    {
                        group.burnDirectionSystem = pmDirGate.normalized;
                        group.pendingAlignDir = group.burnDirectionSystem;
                    }
                    if (group.burnIgnitionAlignWaitTick < 0.0)
                    {
                        group.burnIgnitionAlignWaitTick = physicsTick;
                        TickLog($"[Maneuver] G{group.GroupId} 等对准：主推轴还差 {MainAxisBurnAngleDeg(group, group.burnDirectionSystem):F1}°（上游行为：对准才点火，不打偏）");
                    }
                    continue;   // 等到对准为止
                }
                // ★★ **点火门与船的姿态必须瞄同一个方向**（2026-09-21 游戏内实测的真 bug）——
                //   门比的是 `group.burnDirectionSystem`，而它要到 `StartBurn`（**门的后面**）才被设 ⇒
                //   门前那一刻它还是**上一次燃烧**留下的旧方向 ✗。船则被 `pendingAlignDir` 转到别处
                //   ⇒ 实测 `预对准·诊断 夹角=0.0°、|ω|=0` 与 `主轴还差 2.9°` 自相矛盾、船卡死 1.7 小时。
                //   ⚠ **修点在门的【前面】**（见上方 `preAlignCurrent` 那个分支）：那里同时设
                //   `burnDirectionSystem` 与 `pendingAlignDir`。**不能放在这里** —— 门过不去会 `continue`，
                //   放这里的块永远执行不到（我第一版就是这么写的，实测 `带角度点火：残留 79.5°` ✗✗）。

                bool hadOrbitKeeping = group.HasOrbitKeeping;
                HashSet<int>? savedWhitelist = group.orbitKeepingWhitelist != null
                    ? new HashSet<int>(group.orbitKeepingWhitelist) : null;

                if (group.IsCircularOrbit)
                    group.ExitCircularOrbit();

                if (hadOrbitKeeping)
                {
                    group.HasOrbitKeeping = true;
                    group.orbitKeepingWhitelist = savedWhitelist;
                }

                DVector3 relPos2 = group.positionBody;
                DVector3 relVel2 = group.velocityBody;

                TickLog($"[Maneuver] Group {group.GroupId} localFrame: |pos|={relPos2.magnitude / 1000.0:F0}km |vel|={relVel2.magnitude:F1} m/s bodyId={group.CelestialBodyId}");

                // 优先用规划时刻的世界系 dV（惯性方向，避免点火时刻换算的帧旋转误差）；
                // 旧存档/手动机动未设置时退回点火时刻分量换算
                DVector3 dV = pm.plannedDvBody.sqrMagnitude > 0.0
                    ? pm.plannedDvBody
                    : OrbitMath.GetDeltaVSystem(relPos2, relVel2, pm.radialDV, pm.tangentialDV, pm.normalDV);

                double safeRadius = GetBodySafeRadius(group.CelestialBodyId);
                double mu = GetBodyGM(group.CelestialBodyId);
                DVector3 relVelSafe = group.velocityBody + dV;
                bool safe = OrbitMath.IsOrbitSafe(group.positionBody, relVelSafe, safeRadius, mu);
                if (!safe)
                {
                    // ⚠ 2026-09-26（D6）：这两处原先省略 `mu`（静默取默认 GM）。本次显式传上面算好的 `mu`。
                    double sma = OrbitMath.SemiMajorAxis(group.positionBody, relVelSafe, mu);
                    OrbitMath.GetEccentricity(group.positionBody, relVelSafe, out _, out double ecc, mu);
                    double pe = sma > 0 ? sma * (1.0 - ecc) : -1.0;
                    Log.Message($"[Maneuver] BLOCKED: G{group.GroupId} sma={sma:F0} e={ecc:F4} pe={pe / 1000.0:F0}km safeR={safeRadius / 1000.0:F0}km |pos|={group.positionBody.magnitude / 1000.0:F0} |v|={relVelSafe.magnitude:F0} |dv|={dV.magnitude:F0}");
                    Messages.Message($"Group {group.GroupId} 机动已跳过（将撞击行星）", MessageTypeDefOf.RejectInput);
                    group.PopExecutedManeuver();
                    continue;
                }

                // 本条机动 → 计划对象（唯一入口：选网/加速度/方向锚点/挂引导都在 `BeginBurn` 内）。
                // ⚠ 用 `dV` 而不是 `pm.plannedDvBody`：手排计划不带 world 向量（只有分量）⇒
                //   `dV` 是执行器真正拿在手里的唯一权威。
                var plan = new ManeuverPlan
                {
                    dv = dV,
                    nodeTick = pm.executeTick,
                    burnSeconds = burnSecForCenter,
                    networks = pm.useNetworks != null ? ManeuverNetworkMode.Explicit : ManeuverNetworkMode.InheritPlayer,
                    useNetworks = pm.useNetworks,
                    useRcs = pm.useNetworks == null && group.enabledNetworkRcsOnly,
                    hasSolution = true,
                    radialDV = pm.radialDV,
                    tangentialDV = pm.tangentialDV,
                    normalDV = pm.normalDV,
                    armRendezvousGuidance = pm.armRendezvousGuidance,
                };
                if (!BeginBurn(group, in plan))
                    Log.Message($"[Maneuver] G{group.GroupId} 点火被拒（控制核心/欠电）|Δv|={dV.magnitude:F1}");
                // 执行器实际所用的分量（= 计划里那份，可能是活重解后的版本）：与验收打印并排用。
                TickLog($"[燃烧·诊断] G{group.GroupId} 执行器所用分量（重解后版本）：R/T/N = {pm.radialDV:F2} / {pm.tangentialDV:F2} / {pm.normalDV:F2}｜|Δv|={dV.magnitude:F2}");
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
                    ? $"⚠ 燃烧 {mEst.BurnSeconds:F0}s 已属长燃烧（脉冲模型误差大，建议提高推力或分段）"
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
                    TickLog($"[燃烧·诊断] G{group.GroupId} 点火物理量（**全部候选网络合计**，非执行器所用）：ΣF={dbgF:F1} N｜Σṁ={dbgMf:F4} kg/s｜mass={dbgMass:F1} kg｜a(上界)={(dbgMass > 0 ? dbgF / dbgMass : 0.0):F4} m/s²｜ve(合并上界)={(dbgMf > 0 ? dbgF / dbgMf : 0.0):F1} m/s｜计划 Δv={dV.magnitude:F2}｜**命令加速度 a_cmd={(mEst.BurnSeconds > 0f ? dV.magnitude / mEst.BurnSeconds : 0.0):F4} m/s²（恒定）⇒ 预计时长 {mEst.BurnSeconds:F1}s**");
                }

                BurnDiagNextTick = physicsTick + 10.0;
                // 暂存"燃烧弧预测"的输入（点火时确定的解 + 节点 + 时长）⇒ 燃烧期可做"预测 vs 实测"
                // ⚠ 2026-09-19 撤回一次（引以为戒）：我曾在这里用"**节点时刻**的状态"重新分解 RTN 分量，
                //   假设"计划属于节点那一刻的系"。**实测让它从 6.058e-3 变差到 7.921e-3** ✗ ⇒ 假设错。
                //   ⇒ 保留 `StartBurn` 原有的分解（点火时刻），别再动这里。

                // ⚠ 用 `dV` 而不是 `pm.plannedDvBody`：手动机动（含 B2.2-b 的微调条目）只带分量、
                //   world 向量是 0 ⇒ 用 plannedDvBody 会让模型/目标全部读到零向量（静默失效）。
                group.burnArcDvAdapter = dV;   // 两系同轴（批4 P4）⇒ 直传，跨系只经 OrbitView 的两个口（批6 S2）
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
                    //   ⚠ 两个向量必须来自**同一个系**（历史教训）。批4 P4 起游戏系与适配层系同轴
                    //     ⇒ `burnArcDvAdapter` 与出边状态（`GameFrameStateAtUT`）本就在同一个系，直传即可。
                    //   ⚠ **微调条目本身不再记目标**（否则微调的燃烧结束时又会判定"还有残差" ⇒ 蚊子机动无限套娃）。
                    //     微调是执行器的收口动作，不是一次新意图。
                    // ★★ **MJ 的靶**（"暂时全盘照抄 MJ"，2026-09-20 用户裁定）：**节点处的目标速度**。
                    //   本体：`MechJebModuleNodeExecutor.NextDirection` 里 `node.GetBurnVector(Orbit)` ——
                    //   `Orbit` 是**活的**、靶是**固定速度向量** ⇒ 逐拍 `dv_rem = v_target(UT) − v_live_prop(UT)`。
                    //   ⚠ 我们一度把靶换成"意图轨道 (a,h向量) + 出范围政策"（台架 e 好两个数量级），
                    //     但**真实几何上出范围支是自证式的**：游戏实测 430.01 m/s 只交付 164.9 ✗
                    //     ⇒ 换回 MJ 的形式（无几何分支、良态）；精度交给收口微调（`trimTarget*`）✓。
                    if (group.burnArcDvAdapter.sqrMagnitude > 0.0 && pm.owner != PlanOwner.Executor)
                    {
                        // ★★ **靶的参考轨道：优先用计划自带的**（2026-09-21，两冲量转移的第二发）。
                        //   上游的节点自带宽义"Δv 是在哪条轨道上算的"（`node.patch` / `nextPatch`），
                        //   我们的 `PlannedManeuver.resolvedRefOrbit` 就是它的对应物。
                        //   对**第一发**它与 `burnPreBurnOrbit` 等价（都是点火前那条 ⇒ 行为逐位不变）；
                        //   对**第二发**（到达刹车）它必须是**转移轨道**，否则靶会算成
                        //   "从点火前轨道推到到达时刻再加 Δv"，方向与幅值双双是错的 ✗。
                        (DVector3 trimRN, DVector3 trimVN) = pm.hasResolvedRefOrbit
                            ? pm.resolvedRefOrbit.GameFrameStateAtUT(group.burnArcNodeUt)   // 出边口（批6 S2）
                            : group.burnPreBurnOrbit.GameFrameStateAtUT(group.burnArcNodeUt);
                        DVector3 trimVAfter = trimVN + group.burnArcDvAdapter;   // 两系同轴（P4）⇒ 直传
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
                TickLog($"[燃烧·诊断] G{group.GroupId} 点火 tick={physicsTick:F0} 节点={pm.executeTick} 偏移={physicsTick - pm.executeTick:F0}s（居中应为 −{burnSecForCenter / 2.0:F0}s）｜燃烧时长={burnSecForCenter:F0}s");
                LogRendezvousTrack(group, "点火前");   // 交会轨迹读数：点火前的"距目标/相对速度"

                // 点火残留角明显非零 ⇒ 明说"对准时间不够"（而不是只给一个裸角度让人猜）
                double ignAngle = MainAxisBurnAngleDeg(group, group.burnDirectionSystem);
                if (ignAngle > 5.0)
                {
                    double usable = AlignFeasibility.UsableAlignSeconds(ManeuverPreBurnLookaheadTicks, mEst.BurnSeconds);
                    Log.Warning($"[Maneuver] G{group.GroupId} 带角度点火：残留 {ignAngle:F1}°（可用对准 {usable:F0}s、按动量轮 {group.MaxAngularRate * Mathf.Rad2Deg:F1}°/s 最多转 {usable * group.MaxAngularRate * Mathf.Rad2Deg:F0}°）—— 机动排得太近，或被“燃烧居中”吃掉了时间。");
                }
                TickLog($"[Maneuver] Group {group.GroupId} started gradual burn! dv={dV.magnitude:F1} m/s remaining={remaining} 主推轴与燃烧方向夹角={MainAxisBurnAngleDeg(group, group.burnDirectionSystem):F1}° 预计燃烧={mEst.BurnSeconds:F0}s {longBurn}");
                LogNonRootBurnState(group, physicsTick, "点火前");   // 批5.B 探针（见方法注释）
            }
            PerfProbe.ApplySubClockEnd(PerfProbe.ApplySubManeuver, apManeuver);

            // ── 交会/接近引导（精确交会：固定到达时刻；近距离接近：动态到达时刻）──
            // ★ 分段计时（2026-09-20）：实测尖峰 19.2 ms（均值 0.19 ms 的约 100 倍）来源未知
            //   ⇒ 先把"引导整段"圈起来：**尖峰在引导内**（阶梯 / 终端 CW / 律）还是**在引导外**（物理/GC）。
            //   与 `[步耗时]` 同一把尺 ⇒ 两行一对照就能二分。
            // ★ 2026-10-01：主循环应用段到此结束；引导段的值同时累进 `PerfProbe.PhaseGuidance`（步尖峰行直接读）。
            PerfProbe.PhaseClockEnd(PerfProbe.PhaseApply, phApply);
            {
                var guidSw = System.Diagnostics.Stopwatch.StartNew();
                UpdateRendezvousGuidance(physicsTick);
                guidSw.Stop();
                PerfProbe.PhaseClockAdd(PerfProbe.PhaseGuidance, guidSw.ElapsedTicks);
                if (Prefs.DevMode && guidSw.Elapsed.TotalMilliseconds > 2.0)
                    Log.Message($"[交会引导·耗时] 整段 {guidSw.Elapsed.TotalMilliseconds:F1} ms（t={physicsTick:F0}）");
            }
        }

        // 引导恢复阶梯的节流表（重规划要跑毫秒级扫描，别每步都来）
        private readonly Dictionary<int, double> guidanceRecoverNextTick = new();

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
            var plan = OrbitPlanner.PlanRecovery(r1Rel, v1Rel, physicsTick,
                tick => TryPredictTargetAtTick(target, tick, out var p, out var v)
                    ? (p + aimOffset, v)
                    : default,
                gm, GetBodySafeRadius(group.CelestialBodyId), rRef,
                group.rendezvousPrograde, budget);
            if (!plan.Valid)
            {
                // 无解时给一条"相位优先"的量化建议（S3）：Δv 与圈数成反比，比硬追便宜一个量级
                double phaseErr = PhaseErrorRadians(group, target, gm);
                var ph = OrbitPlanner.PlanPhasing(group.positionBody, group.velocityBody, gm, phaseErr);
                Log.Message($"[Guidance] Group {group.GroupId} 重规划无解（预算 {budget:F0} m/s，原修正量 {causeDv:F0} m/s）｜相位差 {phaseErr * Mathf.Rad2Deg:F1}°，相位规划建议：{(ph.Valid ? $"切向 {ph.OutDv.magnitude:F0} m/s 进相位轨道滑行 {ph.Revs} 圈（共 {ph.TotalDv:F0} m/s，滑行 {ph.CoastSeconds:F0}s）" : "相位差过小或无解")}");
                return false;
            }

            // 排队新出发段：分量与惯性方向都要填（点火时 dvMag 走分量、实际烧走 plannedDvBody）
            DVector3 rDep = OrbitMath.GetPositionAtTimeDelta(r1Rel, v1Rel, plan.WaitSeconds, gm);
            DVector3 vDep = OrbitMath.GetVelocityAtTimeDelta(r1Rel, v1Rel, plan.WaitSeconds, gm);
            OrbitMath.DeltaVSystemToComponents(rDep, vDep, plan.DepartureDv, out double rad, out double tan, out double norm);
            double arrivalTick = physicsTick + plan.WaitSeconds + plan.TransferSeconds;
            QueueGroupManeuver(group.GroupId, new SpaceEntityGroup.PlannedManeuver
            {
                radialDV = (float)rad,
                tangentialDV = (float)tan,
                normalDV = (float)norm,
                executeTick = (int)Math.Round(physicsTick + plan.WaitSeconds),
                plannedDvBody = plan.DepartureDv,
                active = true,
                armRendezvousGuidance = true,   // 点火后重新挂上引导（新到达时刻）
            });
            // QueueManeuver 会清掉引导 ⇒ 按新计划重挂（与 RendezvousPlanner 的确认顺序一致）
            group.RegisterRendezvousGuidance(target.GroupId, arrivalTick, group.rendezvousPrograde, group.rendezvousUseNetworks);
            Log.Message($"[Guidance] Group {group.GroupId} 计划重规划：等待 {plan.WaitSeconds:F0}s + 转移 {plan.TransferSeconds:F0}s，出发 ΔV {plan.DepartureDv.magnitude:F0} m/s（到达 ΔV {plan.ArrivalDv:F0} m/s，总 {plan.TotalDv:F0} m/s）");
            Messages.Message($"Group {group.GroupId}：原交会计划已不可行，已自动重规划（等待 {plan.WaitSeconds:F0}s 后出发）", MessageTypeDefOf.NeutralEvent, false);
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

            DVector3 r1Rel = group.positionBody, v1Rel = group.velocityBody;
            double[] candidates = [OrbitMath.GetTimeToPeriapsis(r1Rel, v1Rel), OrbitMath.GetTimeToApoapsis(r1Rel, v1Rel)];
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

                OrbitMath.DeltaVSystemToComponents(rA, vA, dvA, out double rad, out double tan, out double norm);
                QueueGroupManeuver(group.GroupId, new SpaceEntityGroup.PlannedManeuver
                {
                    radialDV = (float)rad,
                    tangentialDV = (float)tan,
                    normalDV = (float)norm,
                    executeTick = (int)Math.Round(physicsTick + dt),
                    plannedDvBody = dvA,
                    active = true,
                    armRendezvousGuidance = true,
                });
                group.RegisterRendezvousGuidance(target.GroupId, arrivalTick, group.rendezvousPrograde, group.rendezvousUseNetworks);
                Log.Message($"[Guidance] Group {group.GroupId} 当前点修正会撞行星 → 改排到{(i == 0 ? "近地点" : "远地点")}（T+{dt:F0}s）：ΔV {dvA.magnitude:F0} m/s（当前点需 {dvNow.magnitude:F0} m/s）");
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
                double distNow = (target.positionBody + aimOffset - group.positionBody).magnitude;
                Log.Warning($"[Guidance] Group {group.GroupId} 交会中止：已在滚动接近模式且仍不可行（{cause}）· 距离 {distNow / 1000.0:F0}km");
                Messages.Message($"Group {group.GroupId}：交会无法完成（{cause}），已中止。若两者轨道差得远，请先用「转移到圆轨」变轨再交会。", MessageTypeDefOf.RejectInput, false);
                group.ClearRendezvousGuidance();
                return;
            }
            group.RegisterApproachGuidance(target.GroupId, group.rendezvousUseNetworks, aimOffset);
            double distM = (target.positionBody + aimOffset - group.positionBody).magnitude;
            double etaH = distM / ApproachMaxClosingSpeed / 3600.0;
            Log.Message($"[Guidance] Group {group.GroupId} 精确交会 → 滚动接近（原因：{cause}）：动态 tof=距离/{ApproachMaxClosingSpeed:F0}m/s 修正量有界；当前距离 {distM / 1000.0:F0}km ⇒ 限速下约 {etaH:F1} 小时到达（远距降级会慢，属预期）");
            Messages.Message($"Group {group.GroupId}：精确交会已转为滚动接近（{cause}）",
                MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>相位差（弧度，>0 = 本船落后需要追）：用两船相对中心天体的相位角算。</summary>
        private double PhaseErrorRadians(SpaceEntityGroup group, SpaceEntityGroup target, double gm)
        {
            if (group == null || target == null) return 0.0;
            DVector3 rA = group.positionBody, rB = target.positionBody;
            DVector3 nA = DVector3.Cross(rA, group.velocityBody);
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
            DVector3 axis = GetMainThrustAxisSystem(group);
            if (axis.sqrMagnitude < 1e-12 || targetDir.sqrMagnitude < 1e-12) return 0.0;
            double dot = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(axis.normalized, targetDir.normalized)));
            return Math.Acos(dot) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// 预对准开始日志：给出"当前夹角 / 按动量轮限速需要多久 / 距节点还有多久"三个数，
        /// 让"对准有没有留够时间"当场可判（预对准不耗料，故不涉及工质）。
        /// </summary>
        private void LogPreAlignStarted(SpaceEntityGroup group, int executeTick, double physicsTick)
        {
            double angleDeg = MainAxisBurnAngleDeg(group, group.pendingAlignDir);
            double rateDegS = Math.Max(group.MaxAngularRate, 1e-6) * Mathf.Rad2Deg;
            Log.Message($"[预对准] G{group.GroupId} 开始转向下一条机动：主推轴夹角 {angleDeg:F1}°（按 {rateDegS:F1}°/s 约需 {angleDeg / rateDegS:F0}s，节点 T-{executeTick - physicsTick:F0}s）");
        }

        // ══════════════════════════════════════════════════════════════════════════════════
        // 批7 #2（2026-09-30）：阶梯规划的**本拍预取** —— 把"这一拍要重规划的组"的 `MakePlan`
        // 先**并行**算出来（k 个组从 Σ(k) 变 ≈max(k)；实测天花板：8 组同拍 12.49 → 2.95 ms，4.24×），
        // 主循环里照旧**串行提交**（排队/日志/状态写入一律留在主线程，R4 纪律）。
        //
        // ⚠ **行为不可能变**，靠三道保险：
        //   ① 预取只搬**纯计算**（`MakePlan` 是 (OrbitView, OrbitView, tick, Params) 的纯函数）；
        //   ② 主循环用到它之前**核对输入指纹**（两个 `OrbitView` 的 r/v **逐位**、tick、以及三个会变的
        //      Params 来源）—— 不一致 ⇒ **回退到原来那行内联调用**（结果必然正确）；
        //   ③ 预取是**最优努力**：没预取到、或预取多了没用上，都只是"少省一点"，不影响任何语义。
        // ══════════════════════════════════════════════════════════════════════════════════
        private struct LadderPrefetch
        {
            public double physicsTick;
            public OrbitView self, goal;
            public double desiredDistance, matchDistance, safeRadius;
            public RendezvousLadder.Rung rung;
            public List<PlanNode>? nodes;
            public string? status;
        }

        private readonly Dictionary<int, LadderPrefetch> ladderPrefetch = new();

        /// <summary>本进程内**内联**阶梯规划被调用的次数（只喂 `[阶梯·耗时]` 的 `#序号`，DevMode 用）。</summary>
        private int ladderPlanCalls;

        /// <summary>后台 200 次 warmup 的曲线（完成前为 null）；由 `GameComponentTick` 在主线程打一次。</summary>
        private string? ladderWarmBg;

        /// <summary>本拍预取的阶梯计划（`UpdateRendezvousGuidance` 开头调一次；主循环里消费）。</summary>
        private void PrefetchLadderPlans(double physicsTick)
        {
            long pfClock = Prefs.DevMode ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            ladderPrefetch.Clear();
            var mgr = celestialRef.Get();
            if (mgr == null) return;

            // ── 串行：筛候选 + 快照输入（只读；与主循环同序） ──
            var ids = new List<int>();
            var selfs = new List<OrbitView>();
            var goals = new List<OrbitView>();
            var parsList = new List<RendezvousLadder.Params>();
            foreach (var g in groups.Values)
            {
                if (!g.HasRendezvousGuidance || !g.IsMovable || !g.ladderEnabled) continue;
                if (g.HasActiveBurn && !g.burnIsCorrection) continue;
                if (physicsTick - g.ladderLastPlanTick < g.ladderMinReplanSeconds) continue;   // 节流没过 ⇒ 本拍不规划
                if (!groups.TryGetValue(g.rendezvousTargetId, out var tgt) || !tgt.IsActive) continue;
                // 走新律的组在主循环里**提前 return**、到不了阶梯块 ⇒ 别为它们白算（预取要最优努力，别浪费）
                if (UseNewRendezvousLaw && !g.rendezvousIsApproach && tgt.CelestialBodyId == g.CelestialBodyId) continue;
                if (!OrbitAdapter.TryToOrbitView(g, mgr, physicsTick, out OrbitView s)) continue;
                if (!OrbitAdapter.TryToOrbitView(tgt, mgr, physicsTick, out OrbitView t)) continue;

                ids.Add(g.GroupId);
                selfs.Add(s);
                goals.Add(t);
                parsList.Add(new RendezvousLadder.Params
                {
                    DesiredDistance = g.EffectiveRendezvousGoal.distanceTol,
                    MatchDistance = g.rendezvousMatchDist,
                    MaxClosingSpeed = 100.0,
                    MaxPhasingOrbits = 5.0,
                    MinRadius = GetBodySafeRadius(g.CelestialBodyId),
                });
            }
            if (ids.Count == 0) return;

            // ── 并行：每组各写自己的槽位（单项成本取实测量级 ~1.7 ms 是保守的） ──
            int n = ids.Count;
            var rungs = new RendezvousLadder.Rung[n];
            var nodesArr = new List<PlanNode>[n];
            var statusArr = new string[n];
            ParallelOps.For(n, 2e-3, k =>
            {
                rungs[k] = RendezvousLadder.MakePlan(selfs[k], goals[k], physicsTick, parsList[k],
                    out List<PlanNode> nodes, out string status);
                nodesArr[k] = nodes;
                statusArr[k] = status;
            });

            // ── 串行：入表（键 = 组 id；指纹随条目一起存） ──
            for (int k = 0; k < n; k++)
                ladderPrefetch[ids[k]] = new LadderPrefetch
                {
                    physicsTick = physicsTick,
                    self = selfs[k],
                    goal = goals[k],
                    desiredDistance = parsList[k].DesiredDistance,
                    matchDistance = parsList[k].MatchDistance,
                    safeRadius = parsList[k].MinRadius,
                    rung = rungs[k],
                    nodes = nodesArr[k],
                    status = statusArr[k],
                };

            // 慢预取读数（2026-10-19）：2.1ms 引导尖峰到底是一份重几何，还是多组同拍重规划。
            if (pfClock != 0)
            {
                double pfMs = (System.Diagnostics.Stopwatch.GetTimestamp() - pfClock) * 1000.0
                              / System.Diagnostics.Stopwatch.Frequency;
                if (pfMs > 2.0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append(System.Globalization.CultureInfo.CurrentCulture, $"[阶梯·预取] {pfMs:F1} ms｜{n} 份");
                    for (int k = 0; k < n; k++)
                        sb.Append(System.Globalization.CultureInfo.CurrentCulture, $" G{ids[k]}/{rungs[k]}");
                    Log.Message(sb.ToString());
                }
            }
        }

        /// <summary>
        /// 取本拍预取的阶梯计划：**只有输入指纹与当下完全一致才认**（逐位比 r/v + tick + 三个 Params 来源）。
        /// 认不了就返回 false ⇒ 调用点回退到内联 `MakePlan`（行为与没有预取时**一模一样**）。
        /// </summary>
        private bool TryUseLadderPrefetch(SpaceEntityGroup group, OrbitView self, OrbitView goal,
            double physicsTick, double desiredDistance, double matchDistance, double safeRadius,
            out RendezvousLadder.Rung rung, out List<PlanNode> nodes, out string status)
        {
            rung = default;
            nodes = new List<PlanNode>();
            status = "";
            if (!ladderPrefetch.TryGetValue(group.GroupId, out LadderPrefetch p)) return false;
            if (p.physicsTick != physicsTick) return false;
            if (!p.self.r.Equals(self.r) || !p.self.v.Equals(self.v)) return false;      // 逐位（`DVector3.Equals` 按位）
            if (!p.goal.r.Equals(goal.r) || !p.goal.v.Equals(goal.v)) return false;
            if (p.desiredDistance != desiredDistance || p.matchDistance != matchDistance || p.safeRadius != safeRadius)
                return false;
            if (p.nodes == null || p.status == null) return false;

            rung = p.rung;
            nodes = p.nodes;
            status = p.status;
            return true;
        }

        /// <summary>
        /// **闭式霍曼的 DevMode 影子对照**（2026-10-21 正确性保险；交接 §11.36-N「若要加保险」）。
        ///
        /// 闭式霍曼与旧搜索**不再逐位相同**（台架：Δv ≤0.01%、点火时刻 ≤0.11% 周期）。这条探针让
        /// 这种差异在**每一份真实计划上**自证一次，而不是只靠语料：同一几何再跑一次原搜索，
        /// 把两边的 Δv 与点火时刻差打进日志。
        ///
        /// · **不参与任何决策**：结果只打日志、随即丢弃（计划/节点/组状态一个字段都不动）；
        /// · **默认关**：`DebugShadowCompareClosedForm` 打开才跑（每份闭式计划多一次搜索 ≈1.7–2.7 ms，
        ///   用户 2026-10-21 令"不允许增加性能消耗" ⇒ 关着时调用点只多读一个 static bool）；
        /// · 调用点在 `[阶梯·耗时]` 计时块**之后** ⇒ 不污染规划成本读数；
        /// · 搜索侧无解/抛异常也只打一行（闭式已给出计划，不影响）。
        /// </summary>
        private void ShadowCompareClosedFormHohmann(SpaceEntityGroup group, OrbitView self, OrbitView goal,
            double now, List<PlanNode> nodes)
        {
            try
            {
                (DVector3 sv1, double st1, DVector3 sv2, double st2) =
                    OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(self, goal, now, coplanar: false);
                if (double.IsNaN(st1) || double.IsInfinity(st1) || double.IsNaN(st2) || double.IsInfinity(st2))
                {
                    Log.Message($"[引导·影子对照] G{group.GroupId} 搜索侧无解（闭式已给出计划，不影响）");
                    return;
                }
                double dvSearch = sv1.magnitude + sv2.magnitude;
                double dvClosed = RendezvousLadder.TotalDv(nodes);
                double rel = dvSearch > 1e-9 ? Math.Abs(dvClosed - dvSearch) / dvSearch : 0.0;
                double utDelta = nodes.Count > 0 ? Math.Abs(nodes[0].ut - st1) : double.NaN;
                double period = self.period;
                double utRel = period > 0.0 ? utDelta / period : double.NaN;
                Log.Message($"[引导·影子对照] G{group.GroupId} Δv 闭式 {dvClosed:F2} / 搜索 {dvSearch:F2} m/s" +
                            $"（相对差 {rel:P2}）｜点火 UT 差 {utDelta:F0} s（{utRel:P2} 周期）");
            }
            catch (Exception ex)
            {
                Log.Message($"[引导·影子对照] G{group.GroupId} 搜索侧抛异常（闭式已给出计划，不影响）：" +
                            $"{ex.GetType().Name}: {ex.Message}");
            }
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
            PrefetchLadderPlans(physicsTick);   // 批7 #2：本拍要重规划的组先并行算好（见其文档的三道保险）
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
                    Log.Message($"[Guidance] Group {group.GroupId} departure maneuver never executed, rendezvous guidance cancelled.");
                    group.ClearRendezvousGuidance();
                    continue;
                }

                if (!groups.TryGetValue(group.rendezvousTargetId, out var target) || !target.IsActive)
                {
                    Log.Message($"[Guidance] Group {group.GroupId} rendezvous target {group.rendezvousTargetId} gone, guidance cancelled.");
                    group.ClearRendezvousGuidance();
                    continue;
                }

                // 瞄准偏移（跳帮落点）：目标局部系偏移 → 世界米偏移（含目标实体偏移与姿态旋转）。
                // 零偏移 = 瞄准 group 中心（原行为）。
                DVector3 aimOffset = ResolveTargetAimOffset(group, target);

                DVector3 r1Rel = group.positionBody;
                DVector3 v1Rel = group.velocityBody;

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
                    Log.Warning($"[Guidance] Group {group.GroupId} 与目标 {target.GroupId} 的中心天体不同（{group.CelestialBodyId} vs {target.CelestialBodyId}）：新律的相位差需要同系状态，已退回旧路径。跨天体交会需先做参考系拼接（未实现）。");
                }

                // ── 巡航预对准（持续）：出发 burn 完成后立刻调头，为下一次主推（终端消速）提前对准 ──
                // 终端 dv ≈ (目标到达速度 − 本船速度)，用当前相对速度方向做代理姿态；
                // 终端点火时已对准 → 无转向死区（否则最后 180° 调头 + 4km/s 消速叠一起必炸）。
                // 修正 burn 点火时由 burn 内部 AlignMainAxis 覆盖此姿态，烧完自动回归。
                if (!group.HasActiveBurn)
                {
                    if (TryPredictTargetAtTick(target, group.rendezvousArrivalTick, out _, out var vTargetHold))
                    {
                        DVector3 killDir = (vTargetHold - group.velocityBody).normalized;
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
                    double distNow = (target.positionBody + aimOffset - r1Rel).magnitude;
                    double relvelNow = (target.velocityBody - v1Rel).magnitude;
                    if (distNow < ApproachCompletionDistance && relvelNow < ApproachCompletionRelVel)
                    {
                        Log.Message($"[Guidance] Group {group.GroupId} approach complete: dist={distNow:F0}m relvel={relvelNow:F1} m/s");
                        Messages.Message($"Group {group.GroupId} 已接近 Group {group.rendezvousTargetId}（距离 {distNow:F0} m）", MessageTypeDefOf.TaskCompletion);
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
                    DVector3 relA = target.positionBody + aimOffset - r1Rel;      // 本船 → 目标
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
                        Log.Message($"[Guidance] Group {group.GroupId} rendezvous complete (arrival passed).");
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
                    DVector3 drEst = target.positionBody + aimOffset - r1Rel;
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
                    DVector3 drClose = target.positionBody + aimOffset - r1Rel;
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
                        Log.Message($"[Terminal] G{group.GroupId} tof={tof:F0}s dist={distClose / 1000.0:F0}km relVel={relVel:F1}m/s mainDv={needDv:F1}m/s");
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
                ? (target.positionBody + aimOffset - group.positionBody).magnitude / 1000.0
                : double.NaN;
            Log.Warning($"[Guidance] Group {group.GroupId} 交会中止：{cause}（距离 {distKm:F0}km）");
            Messages.Message($"Group {group.GroupId}：交会中止（{cause}）。若两者轨道相差较远，请先用「转移到圆轨」变轨后再交会。", MessageTypeDefOf.RejectInput, false);
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
                    Log.Message($"[Guidance] Group {group.GroupId} terminal complete: relVel={dv.magnitude:F1} m/s tof={tof:F0}s");
                    Messages.Message($"Group {group.GroupId} 交会完成（相对速度已匹配）",
                        MessageTypeDefOf.TaskCompletion);
                    group.ClearRendezvousGuidance();
                }
                return;
            }

            // ── 修正 burn 打断重烧：方向大变（>30°）→ 重烧；否则让当前 burn 继续 ──
            // 避免修正过期：烧中重新评估，若轨迹变化导致修正方向偏离，立即换新解。
            if (group.HasActiveBurn)
            {
                double curDot = DVector3.Dot(group.burnDirectionSystem.normalized, dv.normalized);
                if (curDot >= Math.Cos(30f * Mathf.Deg2Rad))
                    return; // 方向没大变 → 继续当前烧（不重复对准/浪费）
                InterruptCorrectionBurn(group);
                Log.Message($"[Guidance] Group {group.GroupId} correction burn replanned: dv={dv.magnitude:F1} m/s (direction changed)");
            }

            // 可行性（继承出发机动的工质网络）
            var src = GetGroupPropulsionSource(group);
            double mDry = GetGroupMass(group, null);
            // src 非空凭据：null ⇒ networks 实参为 null ⇒ Estimate 在 active.Count==0 处早退（BurnCalculator.cs:78），不触 src
            var est = BurnCalculator.Estimate((float)dv.magnitude, (float)mDry, src!,
                src != null ? src.Networks : null, group.rendezvousUseNetworks);
            double safeRadius = GetBodySafeRadius(group.CelestialBodyId);

            if (!est.Feasible)
            {
                // 原计划已不可做（修正量失控 / 工质不够）→ **不终止**，走恢复阶梯：
                //   ① 重规划（等待 + 转移，时间当自由变量 = MechJeb 的"重算并等窗口"）
                //   ② 退化为滚动接近（有界收敛，永远有出路）
                Log.Message($"[Guidance] Group {group.GroupId} correction infeasible: dv={dv.magnitude:F1} m/s 需 {est.RequiredPropellant:F0} kg，可达上限 {est.MaxDeltaV:F0} m/s → 尝试重规划");
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
                    Log.Message($"[Guidance] Group {group.GroupId} 修正会掉高度 → 安全裁剪至 {safeK * 100.0:F0}%（{dv.magnitude:F1}→{clipped.magnitude:F1} m/s）：每轮推进一点，不放空等窗口");
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
                        Log.Message($"[NewLaw] Group {group.GroupId} 撞行星保护钳制（safeK<5%）：本轮不点火，按物理速率继续收相位");
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
                Log.Message($"[Guidance] Group {group.GroupId} 退出圆轨解析模式（修正烧要真的改轨道）");
            }

            // 修正 burn 使用任务网络（继承出发机动的工质选择）；执行机构沿用既有 15 s 窗口判据。
            var rcsMc = group.GetLinkedMapComp();
            bool useRcs = false;
            if (!group.enabledNetworkRcsOnly && rcsMc != null && rcsMc.HasRcs)
            {
                double rcsMass = GetGroupMass(group, rcsMc);
                double aRcs = rcsMc.RcsTotalThrust / Math.Max(rcsMass, 1.0);
                useRcs = BurnExecutorPolicy.UseRcs(dv.magnitude, aRcs);
            }
            var plan = new ManeuverPlan
            {
                dv = dv,
                networks = group.rendezvousUseNetworks != null
                    ? ManeuverNetworkMode.Explicit : ManeuverNetworkMode.InheritPlayer,
                useNetworks = group.rendezvousUseNetworks,
                useRcs = useRcs,
                isCorrection = true,
            };
            if (!BeginBurn(group, in plan))
            {
                Log.Message($"[Guidance] Group {group.GroupId} 修正点火被拒（控制核心/欠电）dv={dv.magnitude:F1} {tag}");
                return;
            }
            group.rendezvousLastBurnTick = physicsTick; // 冷却：烧完等一个冷却期再评估
            if (trackedManeuvers.ContainsKey(group.GroupId))
                trackedManeuvers[group.GroupId] = new ManeuverTracker { ExecutePhysicsTick = physicsTick, IsActive = true };
            Log.Message($"[Guidance] Group {group.GroupId} correction burn started: dv={dv.magnitude:F1} m/s miss={missM:F0}m tof={tof:F0}s rcs={useRcs} {tag}");
        }

        /// <summary>
        /// **交会引导的游戏侧入口**（"NewLaw" 这个措辞是命名债，见 <see cref="UseNewRendezvousLaw"/> 的说明）。
        ///
        /// ⚠ 2026-09-28 更正：本文原写"与纯测试台架共用 `RendezvousGuidanceLaw.Compose`"——**已过期**。
        ///   那套自制远场律（含 5% CW 终端）已于 2026-09-22 由用户裁定作废并整段删除；
        ///   现在驱动计划的是**交会阶梯**（`RendezvousLadder`，日志打 `[阶梯]` 行）。
        ///   本函数现在的职责是：**完成判据 + 收尾捷径 + 与旧路径共用的落地段**。
        ///
        /// 执行器口径（与台架/阶梯的差异都在这里）：
        ///   · 台架是每物理步节流执行（Δv 按步分批）；游戏侧 <c>StartBurn</c> 是**原子脉冲**
        ///     （满推力烧完为止）⇒ 本函数在烧的期间**不再发新烧**，避免重复交付；
        ///   · dt = 控制周期 <see cref="NewLawControlDt"/>，给出的 |Δv| ≤ aMax×dt ⇒ 满推力能在一个周期内烧完。
        ///
        /// 完成判据走 <see cref="SpaceEntityGroup.IsRendezvousGoalMet"/>（**目的**口径：距离 + 相对速度两项，
        /// 见 `EffectiveRendezvousGoal`），取代旧路径"到达点已过即完成"的静默假成功（旧口径不看距离，见审查 D4）。
        /// </summary>
        /// <summary>终端律参数（柔档 = 离线判据量出来的默认：τ=600 s、接近率上限 20 m/s；见 `TerminalRendezvousLaw` 类注释的取舍表）。</summary>
        /// <summary>
        /// **终端律的门与收口 = 全部沿用既有口径**（2026-10-01 第二版；用户："保留新律的思路，到位门/停下门用原来的"）：
        /// · 入口距离 = 阶梯 `Intercept` 级**同一个门** `r / CloseRadiusDiv`（= `RendezvousLadder.InterceptRegimeDiv` 的口径）；
        /// · 入口速度 = 阶梯 `Params.MaxClosingSpeed`（上游值 100 m/s）；
        /// · 收口（到位）= 既有**到位门** `group.EffectiveRendezvousGoal.distanceTol` + 阶梯自己的完成速度上限 `DoneRelVel`。
        /// ⚠ 第一版自造了 50 km / 300 m/s / 300 m / 2 m/s 四道阈值 —— **那是第三套权威**，已按用户裁定撤掉。
        /// </summary>
        private static TerminalRendezvousLaw.Params TerminalParamsFor(SpaceEntityGroup group, double rMag,
            double relSpeed, double aMax)
        {
            var p = new TerminalRendezvousLaw.Params();
            // ★★ **可变门 = 减速距离**（2026-10-01 用户裁定："进入末端的距离应该是以规划时的加速度计算的减速距离"）：
            //   `d_brake = k · v² / (2a)`（`a` = `EstimateMaxAcceleration`，**规划时**算）。
            //   物理依据（本库实测）：低推力档 a≈0.3 m/s² 时，50 m/s 进场要飞 4.2 km 才刹得住 ⇒ **进场速度决定终端账单**；
            //   所以"什么时候进末端"必须由**这条船现在还刹不刹得住**给出，而不是一个对低推力船天然不公的固定数。
            const double K = 2.0;                                          // 安全系数（= 用户说的"2 倍"）
            double dBrake = aMax > 0.0 ? K * relSpeed * relSpeed / (2.0 * aMax) : 0.0;
            //   进场门取"减速距离"与既有近距门（r/25）的**较大者**：**宁可早接管，不可晚**（晚=刹不住 ✗）
            p.GateDistance = Math.Max(Math.Max(dBrake, rMag / 25.0), 1.0);
            p.GateRelVel = 100.0;                                          // = 阶梯 MaxClosingSpeed（上游值）
            // 斩杀线同族：**当下还刹得住就算到了** ⇒ 停下时（v→0）自动退化回用户裁定的 2 km ✓
            double dKill = aMax > 0.0 ? K * relSpeed * relSpeed / (2.0 * aMax) : 0.0;
            p.SettledDistance = Math.Max(group.EffectiveRendezvousGoal.distanceTol, dKill);
            p.SettledRelVel = 1.0;                                         // = 阶梯 DoneRelVel（完成判据的相对速度上限）
            return p;
        }

        /// <summary>
        /// **终端律合成单节点计划**（2026-10-01 接线）：把 `TerminalRendezvousLaw` 的 Δv 交给**既有的修正烧通道**
        /// （`ApplyGuidanceCorrection`，调用点见 `RunNewRendezvousLaw` 的 `terminalActive` 分支 ——
        /// 小量走 RCS / 死区 / 圆轨退出**全在那里面**）。
        ///
        /// 返回 `null` = **本拍不发**（未到 `GuidanceTerminalCheckInterval` 节拍）；
        /// "Δv 太小也不发"那道检查在**调用点**（`dvT.magnitude &lt; GuidanceBurnDeadband` ⇒ 直接 return）。
        /// ⚠ 系：相对状态（`relPos/relVel`）与目标的 `r2Rel/v2Rel` 都在**同一个局部系**（调用方已保证同中心天体）；
        ///   批4 P4"两系同轴 ⇒ 换系恒等"⇒ 算出来的 Δv 直接就是适配层系的向量 ✓
        /// </summary>
        private DVector3? BuildTerminalDv(SpaceEntityGroup group, SpaceEntityGroup target,
            DVector3 relPos, DVector3 relVel, double relDist, double relSpeed, double gm,
            DVector3 r2Rel, DVector3 v2Rel, double physicsTick, TerminalRendezvousLaw.Params tp)
        {
            if (physicsTick < group.terminalNextTick) return null;      // 节拍未到
            group.terminalNextTick = physicsTick + GuidanceTerminalCheckInterval;   // 用**既有的终端闭环节拍**（10 s）

            // 目标局部 RTN 基（x 径向向外、y 沿迹、z 面外）
            DVector3 rHat = r2Rel.normalized;
            DVector3 hHat = DVector3.Cross(r2Rel, v2Rel).normalized;
            DVector3 tHat = DVector3.Cross(hHat, rHat);

            DVector3 relPosRtn = new DVector3(DVector3.Dot(relPos, rHat), DVector3.Dot(relPos, tHat), DVector3.Dot(relPos, hHat));
            DVector3 relVelRtn = new DVector3(DVector3.Dot(relVel, rHat), DVector3.Dot(relVel, tHat), DVector3.Dot(relVel, hHat));
            double rTgtMag = r2Rel.magnitude;
            if (!(rTgtMag > 1.0)) return null;
            double n = Math.Sqrt(gm / (rTgtMag * rTgtMag * rTgtMag));

            DVector3 dvRtn = TerminalRendezvousLaw.ComputeDv(relPosRtn, relVelRtn, n, GuidanceTerminalCheckInterval, tp);
            double dvMag = dvRtn.magnitude;
            DVector3 dv = dvRtn.x * rHat + dvRtn.y * tHat + dvRtn.z * hHat;   // 回惯用系（P4：换系恒等）
            Log.Message($"[终端律] G{group.GroupId} 算得 Δv={dvMag:F2} m/s（{(dvMag < GuidanceBurnDeadband ? "低于死区 ⇒ 不发" : "交既有修正烧通道")}）｜dist={relDist:F0} m｜相对速度={relSpeed:F2} m/s｜R/T/N={dvRtn.x:F2}/{dvRtn.y:F2}/{dvRtn.z:F2}");
            return dv;
        }

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

            // ══════════════════════════════════════════════════════════════════════════════════
            // **近场终端律的准入（2026-10-01）**：相对位置 + 相对速度同时进粗门 ⇒ **终端接管**（粘滞 + 2× 迟滞）。
            //   为什么在**这里**（`RunNewRendezvousLaw` 的开头）：这条路是唯一"阶梯优先、连续律兜底"的统一入口，
            //   现场失败那条（地球·低推力：Intercept×56、2↔21 km 极限环、烧干）正是走的它 ⇒ 接管点就在阶梯之前。
            //   为什么接管后**不许回阶梯**（粘滞）：2026-09-20 实测过"归属来回插队"（两套机制交替上场、日志自相矛盾）
            //   ⇒ 模式必须粘滞到收口或超迟滞门；同族教训见 `SpaceEntityGroup.ladderOwnsFarField` 的注释。
            // ══════════════════════════════════════════════════════════════════════════════════
            bool terminalActive = false;
            if (group.terminalLawEnabled && target.CelestialBodyId == group.CelestialBodyId)
            {
                TerminalRendezvousLaw.Params tParams = TerminalParamsFor(group, rMag, relSpeed, aMax);
                bool gated = TerminalRendezvousLaw.GateMet(relDist, relSpeed, tParams);
                if (!group.terminalMode && gated)
                {
                    group.terminalMode = true;
                    Log.Message($"[终端律] G{group.GroupId} **切入**（位置+速度联合）：dist={relDist:F0} m（门 {tParams.GateDistance:F0}）｜相对速度={relSpeed:F2} m/s（门 {tParams.GateRelVel:F0}）｜**减速距离门**（2v²/2a，a={aMax:F3} m/s² ⇒ {2.0 * relSpeed * relSpeed / (2.0 * Math.Max(aMax, 1e-9)):F0} m）");
                }
                else if (group.terminalMode
                         && (relDist > tParams.GateDistance * tParams.HysteresisFactor
                             || relSpeed > tParams.GateRelVel * tParams.HysteresisFactor))
                {
                    group.terminalMode = false;
                    Log.Message($"[终端律] G{group.GroupId} **退出**（超迟滞门 {tParams.HysteresisFactor:F0}×）：dist={relDist:F0} m｜相对速度={relSpeed:F2} m/s ⇒ 交回阶梯");
                }

                if (group.terminalMode)
                {
                    if (TerminalRendezvousLaw.Settled(relDist, relSpeed, tParams))
                    {
                        group.terminalMode = false;
                        Log.Message($"[终端律] G{group.GroupId} **收口**：dist={relDist:F0} m｜相对速度={relSpeed:F2} m/s ⇒ 交回完成判据");
                    }
                    else terminalActive = true;
                }
            }

            // ── 完成（判据 = **目的**；唯一求值口 = `SpaceEntityGroup.IsRendezvousGoalMet`）──
            //   ⚠ 走统一口而不是直接 `IsMet`：相位目的要算有符号相位差与半长轴差，
            //     那些量在这里拼一次、在那里又拼一次就会两套口径（本会话的老毛病）。
            bool goalMet = group.IsRendezvousGoalMet(target, gm, out double phaseErrDeg);

            // ★★ 2026-09-28 **修死代码**（用户："收尾捷径这次没触发，再检查"）：
            //   下面那段「收尾捷径」原先排在完成分支**之后**，而它自己又要求 `goalMet` 为真
            //   ⇒ 到达判据一成立就被完成分支 `return` 掉 ⇒ 走到捷径时 `goalMet` **必为 false**
            //   ⇒ **这条机制从未触发过**（日志里连它那两条提示都从没出现过 ✓ 本次交会也是）。
            //   ⚠ 我上一轮把"没触发"解释成"判据还没成立、没轮到它"——**那是错的**，它根本不可能成立。
            //   ⇒ 把关口提到完成分支**之前**：判据仍是"到位（`goalMet`）"，只多问一句
            //   "残余相对速度能不能被**一个物理步**抹平"：能就走捷径（严格共速、省掉整条控制链），
            //   不能就走普通完成分支 —— 两者都是"完成"，交会语义不变。
            double shortcutStepDv = group.HasActiveBurn
                ? 0.0
                : EstimateMaxAcceleration(group) * UPDATE_INTERVAL_TICKS;
            bool shortcutApplies = goalMet && shortcutStepDv > 0.0 && relSpeed <= shortcutStepDv;

            if (goalMet && !shortcutApplies)
            {
                // ⚠ "已到位但引擎吃不掉残余"必须说出来（原在捷径段落里，2026-09-22 立、2026-09-28 折到这里，
                //   因为捷径一旦走到这里就必然不适用）：低推力档实测 `dist=246 m / relVel=2.5 m/s`
                //   而一步只能给 2.27 m/s ⇒ 否则看日志的人只能猜"为什么没走捷径"。
                //   语义上这**不是失败**：到达判据才是完成判据，捷径只是快速路径。
                if (shortcutStepDv <= 0.0 || relSpeed > shortcutStepDv)
                    Log.Message($"[Guidance] Group {group.GroupId} 已到位（dist={relDist:F0} m）但残余相对速度 {relSpeed:F3} m/s 超过一步可给 {shortcutStepDv:F3} m/s（a_full×dt，含 RCS）⇒ 捷径不适用，走普通完成路径");

                Log.Message($"[Guidance] Group {group.GroupId} rendezvous complete（阶梯引导）: dist={relDist:F0}m relVel={relSpeed:F2} m/s｜目的={group.EffectiveRendezvousGoal}｜相位差={(double.IsNaN(phaseErrDeg) ? "—" : $"{phaseErrDeg:+0.0;-0.0;0}°")}");
                Messages.Message($"Group {group.GroupId} 交会完成（距离 {relDist:F0} m，相对速度 {relSpeed:F2} m/s）", MessageTypeDefOf.TaskCompletion);
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
            if (shortcutApplies)
            {
                double aMainStep = shortcutStepDv;
                if (aMainStep > 0.0)
                {
                    DVector3 vBefore = group.CurrentOrbit.velocitySystem.v;
                    group.CurrentOrbit.velocitySystem = (SystemVec3)(vBefore + (v2Rel - v1Rel));   // 抹平：本船 → 目标速度
                    group.OrbitDirty = true;
                    group.EndBurn();                 // 清 burn 状态（含 remainingBurnDV / 选网标记）
                    group.pendingImpulse = DVector3.zero;   // 本步已无待施加冲量
                    Log.Message($"[Guidance·捷径] G{group.GroupId} **收尾捷径生效**：dist={relDist:F0} m ≤ 容差、残余相对速度 {relSpeed:F3} m/s ≤ 一步可给 {aMainStep:F3} m/s（a_full={aMainStep / Math.Max(UPDATE_INTERVAL_TICKS, 1e-9):F3}×dt={UPDATE_INTERVAL_TICKS:F0}s）⇒ **直接抹平相对速度**并**退出交会**（省掉整条控制链；物理上等价于全油门烧一步）");
                    Messages.Message($"Group {group.GroupId} 交会完成（捷径：抹平 {relSpeed:F2} m/s）", MessageTypeDefOf.TaskCompletion);
                    group.ClearRendezvousGuidance();
                    return;
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
                    double distNow = (group.positionBody - (rNear + ResolveTargetAimOffset(group, target))).magnitude;
                    double nextUt = group.plannedManeuvers[0].executeTick;
                    // ⚠ 用**停下尺度**（上游的 100 m），不是完成容差 2 km ——
                    //   用 2 km 会在 2 km 就清队列（提前“就地匹配”）⇒ 又跟接近级打架 ✗
                    // ★ B5：判据本体在 `BurnExecutorPolicy.ShouldDropRemoteNodes`（台架同一份）
                    if (BurnExecutorPolicy.ShouldDropRemoteNodes(distNow, group.rendezvousMatchDist, nextUt, physicsTick))
                    {
                        int dropped = group.plannedManeuvers.Count;
                        group.plannedManeuvers.Clear();
                        Log.Message($"[阶梯] G{group.GroupId} 已进入停下尺度（{distNow:F0} m < {group.rendezvousMatchDist:F0} m）且下一条节点在 T+{nextUt - physicsTick:F0}s ⇒ 按 MJ 纪律**删掉全部 {dropped} 条**，改为就地匹配速度（防末端打转）");
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
                // ★★ **价目改由阶梯自己出**（2026-09-28，用户裁定"交会旧律完全不要，只要我们的新 MJ"）：
                //   原先这里借旧律的 `RendezvousGuidanceCore.CostEstimate(sma…, Δφ, Δi)` 当"这次机动多大"的
                //   量级 ⇒ 那是旧律"形状+相位"两段账，与阶梯真正排的不是同一笔。
                //   现在**先让阶梯排计划、用它自己的 `TotalDv` 当量级**（同一份权威、零借用）。
                //   （B2.6 那条"面外必须进价目"由构造满足：平面改变本来就在阶梯的计划里。）
                //   ⚠ 2026-10-28：区制分类现在**只喂日志**（`Θ=`），不参与分派；阶梯是唯一路径。
                //     旧 `useLadder` 开关与 `LadderExitTheta` 迟滞常量均已删（连续律 2026-09-21 切除）。
                //     ⇒ 不再有"进 0.2 / 退出 0.3"的机制切换（那段是连续律时代的残留）。
                // ★★ **优先级翻转（用户 2026-09-21："先按 MJ 的走"）**：阶梯**默认先试**。
                //   上游根本没有"按推力分派"这回事：它对什么推力都用节点（长燃烧靠
                //   “居中点火 + 逐拍活差额”扛住）。而我们原来是 Θ ≤ 0.2 才交给阶梯
                //   ⇒ 标准推力船（本例 Θ≈0.77）**直接被判给我们自己的连续律** ⇒ 实测
                //   1321 发微冲量、总 Δv 473 m/s、**交会未完成** ✗✗（而高推那轮走阶梯只花 719 m/s ✓）。
                //   ⇒ **阶梯先走**；只有它明确给不出节点、且这一档确实超出准脉冲区（Θ > 阈值）
                //     时，才把它当**逃生口**（那是 MJ 没有对应机制的低推力区 ✓）。
                // 本拍的量纲读数（`Θ`）：**只有阶梯跑过才有意义**（价目 = 它自己排的计划 ⇒ 必须先规划）
                // ⇒ 在下面分支外声明，供"没给节点"与"未走阶梯"两处日志共用。
                // ══════════════════════════════════════════════════════════════════════════════════
                // **终端律（2026-10-01 第三版）**：门用既有（已做）· 节拍用既有（`GuidanceTerminalCheckInterval`）
                // · 通道用既有（`ApplyGuidanceCorrection` ⇒ 小量自动 RCS、死区、圆轨退出全在里面）✓
                // ⚠ 必须先**交出远场归属**：`ApplyGuidanceCorrection` 第一句就是 `if (ladderOwnsFarField) return;`
                //   （那条是 2026-09-21 立的"阶梯在走时我们的机制一律不落地"）⇒ 不交出去，指令会被**静默丢掉** ✗✗
                // ══════════════════════════════════════════════════════════════════════════════════
                if (terminalActive)
                {
                    group.ladderOwnsFarField = false;
                    DVector3? dvT = BuildTerminalDv(group, target, relPos, relVel, relDist, relSpeed, gm,
                        r2Rel, v2Rel, physicsTick, TerminalParamsFor(group, rMag, relSpeed, aMax));
                    if (dvT == null || dvT.Value.magnitude < GuidanceBurnDeadband) return;   // 死区与既有同口径
                    ApplyGuidanceCorrection(group, target, aimOffset, gm, physicsTick, r1Rel, v1Rel, dvT.Value,
                        inTerminal: true, completeOnDeadband: false, tof: 0.0, missM: relDist,
                        tag: "(terminal-law)", newLawMode: true, deadbandMps: GuidanceBurnDeadband);
                    return;                                  // 终端接管：本拍阶梯不参与
                }

                double thetaLad = 0.0;
                double alphaLad = 0.0;
                var ladKind = ManeuverRegime.Kind.QuasiImpulsive;
                // 2026-10-28：`useLadder` 死开关已删（连续律切除后只剩阶梯一条路）；保留块作用域。
                {
                    group.ladderOwnsFarField = true;
                    RendezvousLadder.Rung ladRung;
                    List<PlanNode>? ladNodes;      // 可空：终端律那条路"本拍不发"时是 null（下面 return）
                    string ladStatus;
                    // ★★ **终端律在**本函数开头**就 `return` 了（第三版：交出归属 + 走既有修正烧通道）⇒ 这里只走阶梯。
                    {
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
                    // ── 批7 #2：本拍预取到（且输入指纹逐位一致）就用它；否则**回退到内联调用** ──
                    //   ⚠ 用预取时下面那个耗时读数**不是规划成本**（规划已在并行相位算完）⇒ 日志里显式标注，
                    //     否则那条探针就在说谎（"仪器的名字必须等于它实际量的东西"）。
                    bool ladPrefetched = TryUseLadderPrefetch(group, ovLadSelf, ovLadTgt, physicsTick,
                        ladPars.DesiredDistance, ladPars.MatchDistance, ladPars.MinRadius,
                        out ladRung, out ladNodes, out ladStatus);
                    if (!ladPrefetched)
                        ladRung = RendezvousLadder.MakePlan(ovLadSelf, ovLadTgt, physicsTick, ladPars,
                            out ladNodes, out ladStatus);
                    // ★ **阶梯自己出价**：这次机动的量级 = 它刚排出来的计划总 Δv
                    //   （面外改变在计划里 ⇒ B2.6"面外必须进价目"由构造满足）。
                    double dvScaleLad = RendezvousLadder.TotalDv(ladNodes);
                    double vcLad = Math.Sqrt(gm / Math.Max(rMag, 1.0));
                    double gLad = gm / Math.Max(rMag * rMag, 1.0);
                    ladKind = ManeuverRegime.Classify(dvScaleLad, vcLad, gLad, aMax,
                        out thetaLad, out alphaLad);
                    // ★ 按选中的级决定“本机动允不允许微调”：只有“立轨道”那几级（平面/霍曼/调相/圆化）该用微调；
                    //   到达族（Intercept / MatchVelocitiesNow / MatchAtClosestApproach）**不允许**
                    //   （实测：不禁的话微调每轮打一发径向烧、跟拦截闭合对抗 ✗）。
                    group.trimAllowedByRung = ladRung == RendezvousLadder.Rung.MatchPlanes
                        || ladRung == RendezvousLadder.Rung.Hohmann
                        || ladRung == RendezvousLadder.Rung.PhasingOrbit
                        || ladRung == RendezvousLadder.Rung.Circularize;
                    ladSw.Stop();
                    // 远端立轨道级（Hohmann/Phasing）的窗口以分钟计：30s 重算纯属浪费。
                    // 实测 G2 PhasingOrbit 9.2ms / Hohmann 6.9ms（2026-10-19 22:30 日志）；终端/拦截级
                    // 仍保持 30s（需要密闭环），这里只把下一次到期推后 90s（总 120s）。
                    if (ladRung == RendezvousLadder.Rung.Hohmann || ladRung == RendezvousLadder.Rung.PhasingOrbit)
                        group.ladderLastPlanTick += 90.0;
                    // ★ 慢调用探针（**只在超过阈值时打印** ⇒ 不刷屏、也不改行为）：这就是"尖峰"的分段计时。
                    //   实测基线：同轨 ~0.5~3 ms、大半径差（走霍曼搜索）9~21 ms ⇒ 阈值取 2 ms 能把后者抓出来。
                    //   2026-10-21：**前 12 次无论快慢都打**（带 #序号）——用来分辨"还在吃冷启动"(首≫末)
                    //   与"成本本来就是算法的"(首末接近)。之后仍只在 >2 ms 时打。
                    if (Prefs.DevMode && !ladPrefetched)
                    {
                        ladderPlanCalls++;
                        double ladMs = ladSw.Elapsed.TotalMilliseconds;
                        if (ladMs > 2.0 || ladderPlanCalls <= 12)
                            Log.Message($"[阶梯·耗时] G{group.GroupId} MakePlan {ladMs:F1} ms（{ladRung}）#{ladderPlanCalls}");
                    }
                    }   // ← 关闭"终端律 / 阶梯"二选一（2026-10-01）：下面是**共用**的入队口径
                    if (ladNodes == null) return;    // 让可空性闭合（两条路都保证非空，这里只是给编译器一个明确界）
                    // ★ 2026-10-21 正确性保险（**默认关**）：闭式霍曼的影子对照 —— 同一几何再跑一次原搜索。
                    //   开关 = `DebugShadowCompareClosedForm`（Debug 菜单切）；关着时只多读一个 static bool ⇒ 零成本。
                    //   结果**只进日志**、不参与决策；放在计时块之后 ⇒ 不污染读数。
                    if (DebugShadowCompareClosedForm && Prefs.DevMode
                        && ladRung == RendezvousLadder.Rung.Hohmann && ladStatus.Contains("闭式"))
                        ShadowCompareClosedFormHohmann(group, ovLadSelf, ovLadTgt, physicsTick, ladNodes);
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
                            Log.Message($"[阶梯] G{group.GroupId} 新计划 ⇒ **撤回上一条计划未执行的 {stale} 条节点**（防止按过期计划打冲量）");
                        foreach (var node in ladNodes)
                        {
                            // 节点是**适配层系**的世界向量 —— 两系同轴（批4 P4）⇒ 与游戏系直传；
                            // 跨系只剩 `OrbitView` 的进/出两个口（批6 S2）
                            DVector3 dvSystem = node.dV;
                            // ★★ **分解的参考系 = 节点自带的参考轨道**（2026-09-21）：两冲量转移的第二发
                            //   要在**转移轨道**上分解（那一刻船在那条轨道上），不能用"点火前轨道" ✗。
                            //   没带参考轨道的节点（既有全体）⇒ 退回原口径，行为逐位不变。
                            (DVector3 rAt, DVector3 vAt) = node.hasRefOrbit
                                ? node.refOrbit.GameFrameStateAtUT(node.ut)
                                : ovLadSelf.GameFrameStateAtUT(node.ut);
                            OrbitMath.DeltaVSystemToComponents(rAt, vAt, dvSystem,
                                out double ladRad, out double ladTan, out double ladNor);
                            int lead = (int)Math.Max(2.0, node.ut - physicsTick);
                            group.QueueManeuver((float)ladRad, (float)ladTan, (float)ladNor, lead,
                                PlanOwner.Guidance, dvSystem, node.hasRefOrbit, node.refOrbit);
                        }
                        // 2026-10-27：**按真实分类打日志**（旧版无条件写"准脉冲"，Θ=0.343 也照写）。
                        string regimeLabel = ladKind == ManeuverRegime.Kind.QuasiImpulsive
                            ? "准脉冲"
                            : "连续（仅标注：连续律未接，阶梯仍执行）";
                        Log.Message($"[阶梯] G{group.GroupId} 区制={regimeLabel}（Θ={thetaLad:F3} rad｜α={alphaLad:F2}）⇒ {ladRung}｜{ladStatus}｜排入 {ladNodes.Count} 发（Δv 合计 {RendezvousLadder.TotalDv(ladNodes):F1} m/s）");
                        // ★ **把"下一次点火还要等多久"直接告诉玩家**（2026-09-21）：
                        //   实测用户体验：平面改变只能在交点做 ⇒ 节点被排在 **T+2148 s**，
                        //   而那个信息**埋在一行 `T+2148 s` 里** ⇒ 玩家看到的是"船对齐了但什么都不做" ✗✗。
                        //   （等待本身是几何必然，不是 bug；但**不说出来**就是仪器的错。）
                        if (ladNodes.Count > 0 && ladNodes[0].ut - physicsTick > 120.0)
                            Messages.Message($"G{group.GroupId}：下一次点火在 {ladNodes[0].ut - physicsTick:F0} 模拟秒后（约 {(ladNodes[0].ut - physicsTick) / 60.0:F0} 分钟）—— {ladRung}。等待是几何决定的（如平面改变只能在交点），不是卡住。", MessageTypeDefOf.NeutralEvent, false);
                        return;
                    }
                    // 没给出节点：Done 由完成判据管（在上一步已返回）；其余情况**落回连续区**（保守：那条总能执行）
                    if (ladRung != RendezvousLadder.Rung.Done)
                        Log.Message($"[阶梯] G{group.GroupId} 未给节点（{ladRung}：{ladStatus}）（Θ={thetaLad:F3}）：{(ladKind == ManeuverRegime.Kind.Continuous
                                ? "→ 超出准脉冲区（逃生口标注；⚠ 连续律已切除 ⇒ 本拍同样什么都不做，与上游 `Drive()` 同）"
                                : "⇒ 本拍不做事（等下一次重规划）")}");

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

        // ── 交会几何读数（世界视图面板用） ──

        // 批5.B（2026-09-28）：本 region 原有 `RendezvousLawOption`（旧律的"时间/燃料档"结构）+
        // `BuildRendezvousLawOptions` / `AddOption` / `BeginRendezvousLaw` —— 全部随旧律**撤销**
        // （旧律已作废：用户 2026-09-22 裁定"那个东西不能用了"；入口 2026-09-28 由 `Window_Rendezvous` 替代）。
        // 留下的是**读数**：它与交会入口无关，世界视图面板一直在用。

        /// <summary>
        /// 交会几何读数（**世界视图面板用**）。返回 false = 读不到（目标不在同中心天体 / 状态不可预测）。
        /// ⚠ 相位差用 <see cref="RendezvousGoal.SignedPhaseDeg"/>（**有符号，单位度**，+ = 目标在前）——
        ///   它是本库对"相位"的**判据权威**（`RendezvousGoal` 文档原话："做判据必须用有符号的这一个"）。
        ///   批5.B（2026-09-28，用户："相位让我们的 MJ 接管"）：原先这里调的是**已作废旧律**搬来的
        ///   平黄经版（`PhaseErrorRadians`）⇒ 那份函数随旧律一起退役，相位口径改由阶梯/判据这一侧提供。
        /// </summary>
        public bool TryGetRendezvousReadout(SpaceEntityGroup group, SpaceEntityGroup target,
            out double phaseDeg, out double distM, out double relVelMps)
        {
            phaseDeg = 0.0; distM = double.NaN; relVelMps = double.NaN;
            if (group == null || target == null) return false;
            // 同中心天体是硬前提：跨天体要参考系拼接（未实现），此时两个状态不同系，算了就是错的
            if (group.CelestialBodyId != target.CelestialBodyId) return false;
            double now = TimeScaleManager.GetPhysicsTickSafe();
            if (!TryPredictTargetAtTick(target, now, out var r2, out var v2)) return false;

            DVector3 rTgt = r2 + ResolveTargetAimOffset(group, target);
            DVector3 r1 = group.positionBody, v1 = group.velocityBody;
            double mu = GetBodyGM(group.CelestialBodyId);
            if (!(mu > 0.0) || !(r1.magnitude > 1.0)) return false;

            DVector3 relPos = r1 - rTgt, relVel = v1 - v2;
            distM = relPos.magnitude;
            relVelMps = relVel.magnitude;
            phaseDeg = RendezvousGoal.SignedPhaseDeg(r1, v1, rTgt);
            return true;
        }

        // 批5.B（2026-09-28）：此处原有 `BuildRendezvousLawOptions`（旧律的"快速/省燃料"两档价目）与
        // 它的三个私有助手（`PhasingRate` / `TerminalCostFactor` / `AddOption`），以及 `BeginRendezvousLaw`
        // （旧律的启动入口）。全部随旧律一并**撤销**——阶梯**没有"腿长"这个旋钮**，"这次机动多大"
        // 由阶梯自己的 `TotalDv` 出（批4 已改成"先 MakePlan"）。
        // 要回看：git 历史 / `_backups/20260928-2315-pre-legacy-rendezvous-removal-459f7e2b1fc681fa/`。

        /// <summary>
        /// **批5.C 巡检探针（2026-09-29）**：**非根天体 + 已挂交会引导**的编队，每 300 物理秒打一行状态对账。
        ///
        /// 为什么还要它（第一版探针只打"点火前/收工后"，已证明**点火是干净的**）：绕月那次实测里，
        /// 收工后状态正确（PeR=2870km ✓），但下一次规划时编队已经是 `|pos|=4089km |vel|=670` ⇒ 近月点 814km（月面以下）
        /// —— **状态在一次"没有任何点火记录"的窗口里被换掉了**。而全库唯一能"不响铃地整体换掉状态"的机制是
        /// **圆轨模式**（`SpaceGameComp:1986-1998`：圆轨下每步用 `GetCircularPositionAtTick/VelocityAtTick`
        /// **覆盖**物理状态，批4 的"烧了不动"就是它）⇒ 本巡检把「圆轨模式 / 轨道保持 / 在烧 / 剩余」四个标志
        /// 与两套状态、绕月要素一起打出来 ⇒ **一跳就能看见是哪一刻、什么标志在位**。
        /// ⚠ 只在**非根行星**且**有交会引导**时打印（对地球场景零影响）· 每群每 300 模拟秒一行。
        /// </summary>
        private static readonly Dictionary<int, double> NonRootProbeNextTick = new();
        private static readonly Dictionary<int, (double tick, DVector3 vel)> NonRootEphCache =
            new();

        /// <summary>两质点引力加速度的**模**（探测用；不作生产算式）。</summary>
        private static double PullMag(DVector3 at, DVector3 src, double mu)
        {
            double d2 = (at - src).sqrMagnitude;
            return d2 > 0.0 ? mu / d2 : 0.0;
        }

        private static void LogNonRootRendezvousState(SpaceEntityGroup group, double physicsTick, double keepingAccMps2,
            DVector3 syncPos, DVector3 syncVel)
        {
            // 最便宜的闸门先走：没有交会引导的编队连组件/天体表都不查（调用点已先筛，这里是双保险）。
            if (!group.HasRendezvousGuidance) return;
            var cm = Current.Game?.GetComponent<CelestialBodyManager>();
            var cd = cm?.GetBodyById(group.CelestialBodyId);
            if (cm == null || cd == null || cd.bodyType == CelestialBodyType.Planet) return;
            if (NonRootProbeNextTick.TryGetValue(group.GroupId, out double next) && physicsTick < next) return;
            NonRootProbeNextTick[group.GroupId] = physicsTick + 300.0;
            if (!OrbitAdapter.TryToOrbitView(group, cm, physicsTick, out OrbitView ov)) return;

            // ── ① 星历一致性（"月球给的轨迹是不是它自己那条开普勒轨道"）──
            //    a_eph = 星历速度的有限差（Δv/Δt，300s 间隔 ⟹ 分辨到 ~1e-4 量级 ✓）
            //    a_父@子 = 父天体（地球）在中心天体（月球）处的引力 ⇒ 两者**必须相等**（星历=自由落体）。
            int tickI = (int)physicsTick;
            DVector3 bodyPos = cm.GetBodyPositionParent(group.CelestialBodyId, tickI);   // 体相对**父**
            DVector3 bodyVel = cm.GetBodyVelocityParent(group.CelestialBodyId, tickI);
            var parentDef = cd.ParentBodyId >= 0 ? cm.GetBodyById(cd.ParentBodyId) : null;
            double aEph = double.NaN, aParentAtBody = double.NaN, parentMu = 0.0;
            DVector3 parentSys = DVector3.zero;
            if (parentDef != null)
            {
                parentMu = parentDef.gravitationalParameter;
                // ⚠⚠ **参考系必须同系（2026-10-01 修）**：原来这里拿 `GetBodyPositionParent(父)` =
                //    "父相对**祖父**"，而 `bodyPos` 是"体相对**父**" ⇒ 两个位置**不同系相减**、距离不是真距离
                //    （实测 a_父@天体 反推 1.335e8 m ≠ 真距离 8.0e7 ⇒ "差=2.0e-2" 全是这个假象）。
                //    现在父天体的位置从**物理同源**的 `CollectSystemSources`（系统系、根在原点）里取 ⇒ 同系相减。
                var srcs = new List<GravitySource>();
                cm.CollectSystemSources(group.CelestialBodyId, tickI, srcs);
                if (NBodyIntegrator.TryFindSource(srcs, parentDef.bodyId, out var ps)) parentSys = ps.position;
                // 体在系统系里的位置 = 父在系统系里的位置 + （体相对父）：两块位置同系，距离才是真距离
                aParentAtBody = PullMag(parentSys + bodyPos, parentSys, parentMu);
            }
            if (NonRootEphCache.TryGetValue(group.CelestialBodyId, out var prev) && physicsTick > prev.tick + 1.0)
                aEph = (bodyVel - prev.vel).magnitude / (physicsTick - prev.tick);
            NonRootEphCache[group.CelestialBodyId] = (physicsTick, bodyVel);

            // ── ② 场对照（飞船实际受的力；都从"飞船世界系位置"算，与物理同系）──
            double aMoonAtShip = PullMag(group.CurrentOrbit.positionSystem.v, bodyPos, cd.gravitationalParameter);
            double aParentAtShip = parentDef != null ? PullMag(group.CurrentOrbit.positionSystem.v, parentSys, parentMu) : 0.0;

            // ── ④ 偏移一致性：本步用来把世界系状态转成体心系的偏移，必须等于该天体在这一 tick 的星历状态 ──
            //    （若偏移取错 tick / 取错天体，体心系状态会整体错位 ⇒ 阶梯按错状态排计划 ⇒ 又一次"参考系混用"）
            double syncPosErr = (syncPos - bodyPos).magnitude;
            double syncVelErr = (syncVel - bodyVel).magnitude;

            // ── ③ 要素 ──
            Log.Message($"[非根天体·巡检] G{group.GroupId} t={physicsTick:F0} 圆轨模式={group.IsCircularOrbit} 保持={group.HasOrbitKeeping} 在烧={group.HasActiveBurn} 余={group.remainingBurnDV:F2}｜体心系 r={group.positionBody.magnitude / 1000.0:F0}km v={group.velocityBody.magnitude:F1}｜世界系 r={group.CurrentOrbit.positionSystem.magnitude / 1000.0:F0}km v={group.CurrentOrbit.velocitySystem.magnitude:F1}｜要素 a={ov.semiMajorAxis / 1000.0:F0}km e={ov.eccentricity:F5} PeR={ov.PeR / 1000.0:F0}km ApR={ov.ApR / 1000.0:F0}km\n    ├ 星历：|v天体|={bodyVel.magnitude:F1}｜a_eph(Δv/Δt)={aEph:E3}｜a_父@天体={aParentAtBody:E3}（**两者应相等**；差={aEph - aParentAtBody:E3}）\n    ├ 场@船：a_中心@船={aMoonAtShip:E3}｜a_父@船={aParentAtShip:E3}｜保持修正(①−②)={keepingAccMps2:E3}（①=a_父@船(+其他非白名单)，②=a_父@天体）\n    ├ 偏移一致：|Δ偏移位置|={syncPosErr:E3} m｜|Δ偏移速度|={syncVelErr:E3} m/s（**都应为 0**）\n    └ 该天体本地引力={aMoonAtShip:E3}（修正/本地={(aMoonAtShip > 0.0 ? keepingAccMps2 / aMoonAtShip : 0.0):F4}）· 父引力/本地={(aMoonAtShip > 0.0 ? aParentAtShip / aMoonAtShip : 0.0):F4}");
        }

        /// <summary>
        /// **批5.B 探针（2026-09-28）：中心天体非根行星时的"点火前后状态对账"**。
        ///
        /// 起因（用户 2026-09-28 报"绕月的交会好像不是特别正常"）：日志里看到一次**绕月**交会——
        /// 阶梯的规划与第一次点火都**对**（压近点 3300→2844 km 正好要 46.5 m/s，实测 46.52、对账 0.00%），
        /// 但飞船随后**撞上月面**（落地相对速度 2075.9 m/s），而撞击前**没有任何其它点火**。
        /// 静态读码已排除：规划/执行器的参考系链自洽（同一体心系）、物理引力按世界系位置算（对）。
        /// ⇒ 剩下的两个嫌疑需要**读数**才能二分：①点火方向/作用点是否落在体心系；
        ///   ②**圆轨模式**（`ExitCircularOrbit` 会用解析圆轨的状态**覆盖**物理状态 ⇒ 点火被抹掉）。
        /// 本探针把两套状态 + 绕中心天体的要素 + 圆轨模式标志一起打出来 ⇒ 下一轮日志即可定案。
        /// ⚠ 它只在**非根行星**时打印（绕月球等卫星）⇒ 对地球/太阳场景零影响。
        /// </summary>
        private static void LogNonRootBurnState(SpaceEntityGroup group, double physicsTick, string tag)
        {
            if (!PerfProbe.TickLog) return;
            var cm = Current.Game?.GetComponent<CelestialBodyManager>();
            var cd = cm?.GetBodyById(group.CelestialBodyId);
            if (cm == null || cd == null || cd.bodyType == CelestialBodyType.Planet) return;
            if (!OrbitAdapter.TryToOrbitView(group, cm, physicsTick, out OrbitView ov)) return;
            Log.Message($"[非根天体·{tag}] G{group.GroupId} body={group.CelestialBodyId} 圆轨模式={group.IsCircularOrbit}｜体心系 r={group.positionBody.magnitude / 1000.0:F0}km v={group.velocityBody.magnitude:F1} m/s｜世界系 r={group.CurrentOrbit.positionSystem.magnitude / 1000.0:F0}km v={group.CurrentOrbit.velocitySystem.magnitude:F1}｜要素 a={ov.semiMajorAxis / 1000.0:F0}km e={ov.eccentricity:F5} PeR={ov.PeR / 1000.0:F0}km ApR={ov.ApR / 1000.0:F0}km（μ={ov.mu:E2}）");
        }

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
                posRel = FrameConvert.SystemToBody(target.GetCircularPositionAtTick(tick),
                    target.GetCentralBodyOffset(tick)).v;
                DVector3 radialDir = posRel.normalized;
                DVector3 tangentDir = DVector3.Cross(target.circularOrbitNormal, radialDir).normalized;
                velRel = tangentDir * (target.circularAngularVelocity * target.circularRadius);
                return true;
            }

            if (target.CelestialBodyId < 0) return false;
            double gm = GetBodyGM(target.CelestialBodyId);
            double physicsTickNow = TimeScaleManager.GetPhysicsTickSafe();
            int nowTick = (int)Math.Round(physicsTickNow);
            (BodyVec3 tgtBodyPos, BodyVec3 tgtBodyVel) = target.GetBodyState(nowTick);
            DVector3 relPos = tgtBodyPos.v;
            DVector3 relVel = tgtBodyVel.v;
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

            // ── 批7 #6（2026-09-30）：把这一拍**要重建缓存**的组挑出来批并行重建（每组一次完整引力采样），
            //    再走原来的逐组插值。战斗模式下这是**唯一还在按真实 tick 跑的持续成本**（≤60/s）。
            //    ⚠ **严格保持原行为**：`t` 仍取自**重建前**的缓存基准（原代码就是这么用的，见下面的登记项），
            //      插值仍按组序、`SetRenderedPosition` 仍串行（渲染状态只许主线程写）。
            combatInterpTickScratch.Clear();
            combatRebuildScratch.Clear();
            foreach (var group in groups.Values)
            {
                if (!group.IsMovable || group.CombatCache == null) continue;

                // 插值时间相对 CombatCache 基准连续推进（不再 elapsed % 2.0 循环）。
                // 原实现取模导致非圆轨目标位置每 2s 跳回战斗开始位置，射弹（物理位置连续）
                // 与其时间基准错位 → 相对位置突变 → 命中检测永远失败。
                double t = (currentTick - group.CombatCache.baseTick) * secondsPerTick;
                combatInterpTickScratch.Add((group, t));

                // CombatCache 覆盖区间（dt）已过 → 以当前状态重建，防止 acc/jerk 外推漂移
                if (t > group.CombatCache.dt) combatRebuildScratch.Add(group);
            }

            RebuildCombatCachesBatch(combatRebuildScratch, currentTick);

            foreach (var (group, t) in combatInterpTickScratch)
            {
                DVector3 interpolatedPos = group.CombatCache.InterpolatePosition(t);

                // 物理/渲染状态分离：插值结果只写渲染缓存，绝不污染 CurrentOrbit。
                // CurrentOrbit 只由物理积分（UpdateAllMovements）在 physicsTick 时间轴推进，
                // 碰撞/射弹检测统一读物理状态，与射弹同基准（万物统一物理时间步长）。
                group.SetRenderedPosition(interpolatedPos);
            }
        }

        /// <summary>
        /// 批7 #3（2026-09-30）：7 档相位扫描的**单档**预测（纯函数）—— 返回预测 e，预测失败给 `NaN`。
        ///
        /// 抽出来是为了"串行/并行两条路**共用同一份算式**"（唯一权威；不许写成两份，否则迟早漂移）。
        /// ⚠ 扫的是**执行器真正交付的窗口** ⇒ 模型要带上执行延迟（见 `BurnArc.ExecutionDelaySeconds`）。
        /// </summary>
        private static double PredictOneShift(OrbitView ov, SpaceEntityGroup.PlannedManeuver pm,
            BurnEstimate mEst, double sh)
        {
            return BurnArc.TryPredict(ov, pm.executeTick, pm.plannedDvBody,   // 两系同轴（P4）⇒ 直传
                mEst.BurnSeconds, sh + BurnArc.ExecutionDelaySeconds, out BurnArc.Result sw, out _)
                ? sw.eccentricity
                : double.NaN;
        }

        /// <summary>
        /// **批量重建战斗插值缓存**（批7 #6）：每组一次完整引力采样 ⇒ 组间独立、**批并行**；
        /// 结果先写各自的槽位，最后**按列表序串行赋值**（每组只写自己的 `CombatCache`）。
        ///
        /// 线程安全的两条依据（都是"按构造成立"，不是猜）：
        ///   ① `ComputeGroupFieldAccelJerk` 就是**物理相位（`Parallel.For` 内）在调的那个函数** ⇒ 它本来就必须并发安全；
        ///   ② **每索引一份 scratch**（`GetGravityScratch(k)`）—— 共享源表（S-a 的预取表）是**只读**的，
        ///      退回路径也只写进 `scratch.own`（`CollectSystemSources` 会先 `Clear`）。
        /// </summary>
        private void RebuildCombatCachesBatch(List<SpaceEntityGroup> targets, int currentTick)
        {
            if (targets.Count == 0) return;
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (celestialMgr == null) return;

            // 重力采样用 physicsTick（物理时间轴）；baseTick 保持 realTick 供插值索引。
            // 采样点与插值起点**同为系统系坐标**（此前 acc/jerk 取局部系、startPos 取系统系，
            // 月心系群组的渲染插值会用到混系加速度——A0 一并修正）。
            double physicsTickSample = TimeScaleManager.GetPhysicsTickSafe();
            int n = targets.Count;
            EnsureGravityScratchCapacity(n);
            var accs = new DVector3[n];
            var jerks = new DVector3[n];
            var startPos = new DVector3[n];
            var startVel = new DVector3[n];

            // 单项 ≈ 一次完整引力场采样（含可能的"收集"退回）⇒ 5e-5 是量级估计
            ParallelOps.For(n, 5e-5, k =>
            {
                var group = targets[k];
                var scratch = GetGravityScratch(k);
                ComputeGroupFieldAccelJerk(group, celestialMgr, physicsTickSample, scratch,
                    ResolveSystemSources(celestialMgr, group, physicsTickSample, scratch),
                    out accs[k], out jerks[k], out _, out _);
                startPos[k] = group.CurrentOrbit.positionSystem.v;
                startVel[k] = group.CurrentOrbit.velocitySystem.v;
            });

            for (int k = 0; k < n; k++)
                targets[k].CombatCache = new SpaceEntityGroup.PhysicsStateCache
                {
                    baseTick = currentTick,
                    startPos = startPos[k],
                    startVel = startVel[k],
                    acc = accs[k],
                    jerk = jerks[k],
                    dt = 2.0,
                    offset = 0.0
                };
        }

        /// <summary>为全部可动群组重建战斗插值缓存（切档进入 Combat 时调用；实现唯一）。</summary>
        public void RebuildAllCombatCaches(int currentTick)
        {
            combatRebuildScratch.Clear();
            foreach (var group in groups.Values)
            {
                if (!group.IsMovable) continue;
                combatRebuildScratch.Add(group);
            }
            RebuildCombatCachesBatch(combatRebuildScratch, currentTick);
        }

        // 批7 #6（2026-09-30）：原来的**单组** `RebuildCombatCache(group, tick)` 已删除 ——
        // 它被 `RebuildCombatCachesBatch` 完全取代（同一个采样路径 + 同一个缓存构造），
        // 删时**零调用者**（两处调用点都已改走批版）。要取回：见
        // `_backups/20260930-2055-post-batch7-5-recovery-grid-4E0A9C65370566D5/`。

        // ── 圆轨进入（两段式 dV 机动） ──

        /// <summary>
        /// 圆轨进入的残差阈值（m/s）：第二段一次性消耗允许的最大残差；
        /// 第一段 dV 可行性检查时也固定加此余量（两段机动最坏消耗 = 机动所需 + 阈值）。
        /// 2026-10-29：10 → **25**。地球实测 8/10 发卡在"残差 10.0–10.9 vs 阈值 10.0"——
        /// 那 10 m/s 是**第一段 30 s 缓释燃烧**在远地点附近积累的径向漂移（≈1.2 m/s² × 30 s 的一部分），
        /// 不是制导误差（手动"再点一次圆轨"必成，因为第二发几乎在圆轨上烧）。
        /// 阈值本身就只是安全网：阈值内会"一次性扣工质"把残差补掉，25 m/s 仍远小于任何真实机动量级。
        /// </summary>
        public const double CircularizeDvThreshold = 25.0;

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
        /// <summary>兼容壳：点火唯一入口在 <see cref="SpaceEntityGroup.BeginBurn" />（2026-10-27）。</summary>
        public bool BeginBurn(SpaceEntityGroup group, in ManeuverPlan plan)
            => group != null && group.BeginBurn(in plan);

        public bool BeginCircularize(SpaceEntityGroup group, int bodyId)
        {
            if (group == null) return false;
            if (group.IsCircularOrbit)
            {
                Messages.Message($"Group {group.GroupId} 已在圆轨。", MessageTypeDefOf.RejectInput, false);
                return false;
            }
            if (group.HasActiveBurn || group.HasPendingCircularize)
            {
                Messages.Message($"Group {group.GroupId} 正在执行机动，请稍候。", MessageTypeDefOf.RejectInput, false);
                return false;
            }

            int realTick = Find.TickManager.TicksGame;
            if (!TryComputeCircularState(bodyId, group.CurrentOrbit.positionSystem.v, group.CurrentOrbit.velocitySystem.v, realTick,
                out DVector3 vCircSystem, out double r))
            {
                Messages.Message($"Group {group.GroupId} 无法计算天体 {bodyId} 的圆轨状态。", MessageTypeDefOf.RejectInput, false);
                return false;
            }

            DVector3 dvVec = vCircSystem - group.CurrentOrbit.velocitySystem.v;
            double nodeDV = dvVec.magnitude;
            if (nodeDV < 0.5)
            {
                // 已接近圆轨（<0.5 m/s）：免 burn，直接走第二段结算
                return TryCompleteCircularize(group, bodyId, vCircSystem);
            }

            // ── 工质检查：所需 + 阈值（两段机动最坏消耗 = 机动所需 + 残差阈值）──
            var src = GetGroupPropulsionSource(group);
            if (src == null || src.Networks.Count == 0)
            {
                Messages.Message($"Group {group.GroupId} 无推进能力，无法消耗 dV 进入圆轨。", MessageTypeDefOf.RejectInput, false);
                return false;
            }
            double mDry = GetGroupMass(group, null);
            var est = BurnCalculator.Estimate((float)(nodeDV + CircularizeDvThreshold), (float)mDry, src, src.Networks, null);
            if (!est.Feasible)
            {
                string reason = est.Networks == null || est.Networks.Count == 0
                    ? "未安装引擎或未启用任何工质网络"
                    : $"工质不足（需约 {est.RequiredPropellant:F0} kg，最大可达 ΔV {est.MaxDeltaV:F0} m/s）";
                Messages.Message($"Group {group.GroupId} 无法进入圆轨：{reason}。", MessageTypeDefOf.RejectInput, false);
                return false;
            }

            // ── 立即执行节点：退出圆轨残留，开始缓释 burn（全网络）──
            // 燃烧时长按**可用加速度**给（2026-10-29 A）：旧值固定 30 s ⇒ 在远地点附近烧 30 秒，
            // 船离开顶点、积累 ~10 m/s 径向漂移 ⇒ 结算残差正好压在阈值上方（地球 8/10 发失败）。
            // 现在 = 2×最短时间（≈脉冲量级），夹在 [2 s, 30 s]；低推力船仍能烧完。
            double thrustSum = 0.0;
            for (int i = 0; i < src.Networks.Count; i++) thrustSum += src.Networks[i].Thrust;
            double aCirc = thrustSum > 1.0 ? thrustSum / Math.Max(mDry, 1.0) : 0.0;
            double burnSeconds = aCirc > 1e-6
                ? Math.Clamp(2.0 * nodeDV / aCirc, 2.0, 30.0)
                : 30.0;

            var plan = new ManeuverPlan
            {
                dv = dvVec,
                burnSeconds = burnSeconds,
                networks = ManeuverNetworkMode.All,
                clearCircularOrbit = true,
            };
            if (!BeginBurn(group, in plan))
            {
                Messages.Message($"Group {group.GroupId} 圆轨点火被拒（控制核心/欠电）。", MessageTypeDefOf.RejectInput, false);
                return false;
            }
            group.HasPendingCircularize = true;
            group.pendingCircularizeBodyId = bodyId;
            // 机动追踪：进入圆轨 burn 期间自动降速到 Normal
            trackedManeuvers[group.GroupId] = new ManeuverTracker
            {
                ExecutePhysicsTick = TimeScaleManager.GetPhysicsTickSafe(),
                IsActive = true
            };

            Log.Message($"[Circularize] Group {group.GroupId} 圆轨机动开始: body={bodyId} r={r / 1000.0:F0}km 节点ΔV={nodeDV:F1}m/s（检查含阈值余量 {CircularizeDvThreshold:F1}）");
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
            DVector3 vNow = group.CurrentOrbit.velocitySystem.v + group.pendingImpulse;

            DVector3 vCirc;
            double r;
            if (precomputedVCirc.HasValue)
            {
                vCirc = precomputedVCirc.Value;
                r = 0.0;
            }
            else if (!TryComputeCircularState(bodyId, group.CurrentOrbit.positionSystem.v, vNow, realTick, out vCirc, out r))
            {
                Log.Warning($"[Circularize] Group {group.GroupId} 二次计算圆轨失败，保持正常轨道。");
                return false;
            }

            double residual = (vCirc - vNow).magnitude;
            if (residual > CircularizeDvThreshold)
            {
                Log.Message($"[Circularize] Group {group.GroupId} 残差 {residual:F1} m/s 超过阈值 {CircularizeDvThreshold:F1}，未进入圆轨（保持正常轨道，可重试）。");
                Messages.Message($"Group {group.GroupId} 圆轨失败：残差 {residual:F1} m/s 超过阈值 {CircularizeDvThreshold:F1} m/s。", MessageTypeDefOf.RejectInput, false);
                return false;
            }

            // ── 成功：真实速度（含最后冲量）落位 → 清空 pending → 一次性扣残差工质 → 进入圆轨 ──
            group.CurrentOrbit.velocitySystem = (SystemVec3)vNow;
            group.pendingImpulse = DVector3.zero; // 已并入 vNow，不再叠加
            ConsumeDeltaVOneShot(group, residual);
            group.EnterCircularOrbit(realTick, bodyId);

            // 2026-10-30 用户裁定：**圆轨化收尾要中和角速度（消旋）**。
            // 为什么必须在这里做：圆轨化成功 = 制导交接点（`RocketLaunchController` 随后
            // `activeLaunches.RemoveAt`、`aeroAlignToAirflow=false`）⇒ 之后**没有任何东西再写 ω**，
            // 而空间里也没有阻尼 ⇒ 交接瞬间的残余 ω（上升段姿态对准的收敛尾巴，或分离接触擦出来的转动）
            // 会被**永久冻结**，玩家看到"二级一直在转"。这里主动归零，交出一条姿态稳定的船。
            // 口径：手动旋转 / SAS 开着时不碰 ω（由玩家或控制器随后写），与机动队列的消旋同一判据。
            bool manualRotation = SteeringController.IsOpen
                && SteeringController.GroupId == group.GroupId
                && (SteeringController.AnyRotation || SteeringController.SasKind != SasTargetKind.None);
            double spinBeforeDeg = group.angularVelocity.magnitude * Mathf.Rad2Deg;
            if (!manualRotation) group.angularVelocity = DVector3.zero;

            Log.Message($"[Circularize] Group {group.GroupId} 进入圆轨: body={bodyId} r={(r > 0.0 ? r / 1000.0 : group.circularRadius / 1000.0):F0}km 残差ΔV={residual:F1} m/s（≤阈值 {CircularizeDvThreshold:F1}）已一次性扣工质"
                + (manualRotation ? $"｜手动/SAS 在控，保留 |ω|={spinBeforeDeg:F3}°/s" : $"｜已消旋（交接前 |ω|={spinBeforeDeg:F3}°/s）"));
            return true;
        }

        /// <summary>
        /// 计算某天体在当前状态下的圆轨速度向量（世界系）与轨道半径。
        /// 数学与 EnterCircularOrbit 一致（含月球等非行星天体的坐标换算）。
        /// </summary>
        private bool TryComputeCircularState(int bodyId, DVector3 posSystem, DVector3 velSystem, int realTick,
            out DVector3 vCircSystem, out double radiusMeters)
        {
            vCircSystem = DVector3.zero;
            radiusMeters = 0.0;
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return false;
            var body = mgr.GetBodyById(bodyId);
            if (body == null) return false;

            DVector3 relPos = posSystem, relVel = velSystem;
            if (body.bodyType != CelestialBodyType.Planet)
            {
                relPos -= mgr.GetBodyPositionParent(bodyId, realTick);
                relVel -= mgr.GetBodyVelocityParent(bodyId, realTick);
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
            vCircSystem = tangentDir * vTan;
            if (body.bodyType != CelestialBodyType.Planet)
                vCircSystem += mgr.GetBodyVelocityParent(bodyId, realTick);
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

            (DVector3 rNow, DVector3 vNow) = ovNow.GameFrameStateAtUT(physicsTick);   // 出边口（批6 S2）
            // ★ 主燃烧**最后一步的冲量**此刻可能还没落位（它在下一步的物理步首才并入速度）。
            //   不补上的话，微调会把"那一笔还没到"当成残差 ⇒ 规划出一个假的修正量。
            //   口径与 `TryCompleteCircularize` 逐字相同（"最后一步 burn 冲量尚未施加：并入当前速度"）。
            vNow += group.pendingImpulse;
            if (!TrimBurn.TryPlan(rNow, vNow, ovNow.mu, group.trimTargetA, group.trimTargetH,
                    out DVector3 dvSystem, out string why, out double aUsed))
            {
                Log.Message($"[燃烧·微调] G{group.GroupId} 不微调：{why}");
                return;
            }

            double dvMag = dvSystem.magnitude;
            if (dvMag < TrimDvMin || dvMag > TrimDvMax)
            {
                Log.Message($"[燃烧·微调] G{group.GroupId} 不微调：Δv={dvMag:F3} m/s 落在合理带 [{TrimDvMin:F2}, {TrimDvMax:F0}] 之外｜目标 a={group.trimTargetA / 1000.0:F0} km");
                return;
            }

            // 分量按**当前** RTN 分解（执行器整段燃烧只换框架、不换解）
            OrbitMath.DeltaVSystemToComponents(rNow, vNow, dvSystem, out double rad, out double tan, out double nor);
            group.trimLastDv = dvMag;
            group.QueueManeuver((float)rad, (float)tan, (float)nor, TrimLeadTicks, PlanOwner.Executor);
            Log.Message($"[燃烧·微调] G{group.GroupId} 闭环微调：Δv={dvMag:F3} m/s（R/T/N {rad:F2}/{tan:F2}/{nor:F2}）｜本点 r={rNow.magnitude / 1000.0:F1} km，目标 a={group.trimTargetA / 1000.0:F1} km（差 {(rNow.magnitude - group.trimTargetA) / 1000.0:F1} km）");
            // 退化支必须**显式写出**：本点不在意图曲线上时，一发点火只能"保形状、把尺度锚到本点"
            // （见 `TrimBurn` 类注释的取舍）⇒ 半长轴会落在 r 而不是 a_t，读数上要看得出来。
            if (Math.Abs(aUsed - group.trimTargetA) > 1.0)
                Log.Message($"[燃烧·微调] G{group.GroupId} 目标退化：本点 r={rNow.magnitude / 1000.0:F1} km 不在意图轨道径向范围内（a_t={group.trimTargetA / 1000.0:F1} km、e_t={Math.Sqrt(Math.Max(0.0, 1.0 - group.trimTargetH * group.trimTargetH / (ovNow.mu * group.trimTargetA))):E4}）⇒ 保形状、锚定本点 ⇒ a 落在 {aUsed / 1000.0:F1} km（与意图差 {(aUsed - group.trimTargetA) / 1000.0:F1} km）；**这一发修的是 e，不是 a**");
        }

        /// <summary>
        /// **一次性付一笔 Δv**（火箭方程唯一算式：`Δm = m₀(1 − e^(−Δv/ve))`，按各网络 ṁ 占比分摊扣料）。
        /// <paramref name="tag"/> 只影响日志前缀（既有调用方传默认值 ⇒ 输出逐字不变）；
        /// 返回实际扣掉的工质 (kg)，受存量截断时可与需求不同。
        /// 主动降落由 ConsumeLandingDeltaV 按报价分配逐个网络扣料。
        /// </summary>
        private double ConsumeDeltaVOneShot(SpaceEntityGroup group, double dv, string tag = "Circularize")
        {
            if (dv <= 0.001) return 0.0;
            var src = GetGroupPropulsionSource(group);
            if (src == null) return 0.0;
            double ve = src.EffectiveVe;
            if (ve <= 0.0) return 0.0;
            double m0 = GetGroupMass(group, src);
            // Δm = m0·(1 − e^(−ΔV/ve))
            double dm = m0 * (1.0 - Math.Exp(-dv / ve));
            if (dm <= 0.0) return 0.0;

            double mfTotal = 0.0;
            foreach (var n in src.Networks)
                mfTotal += n.MassFlow;
            if (mfTotal <= 0.0) return 0.0;
            double actual = 0.0;
            foreach (var n in src.Networks)
            {
                double share = dm * (n.MassFlow / mfTotal);
                actual += ConsumeGroupPropellant(group, n.propellant, (float)share);
            }
            Log.Message($"[{tag}] Group {group.GroupId} {(tag == "Circularize" ? "残差 " : "")}dV 一次性消耗: {dv:F1} m/s → 需工质 {dm:F1} kg／实扣 {actual:F1} kg");
            return actual;
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
            group.RestorePlayerNetworkSelection();
            group.GetLinkedMapComp()?.SetThrustersBurning(false);
            trackedManeuvers.Remove(group.GroupId);
            if (group.HasPendingCircularize)
            {
                group.HasPendingCircularize = false;
                group.pendingCircularizeBodyId = -1;
            }
            Log.Message($"[Burn] Group {group.GroupId} burn EMERGENCY STOPPED by player (applied {applied:F1} / {planned:F1} m/s)");
            Messages.Message($"Group {group.GroupId} 机动已紧急终止（已施加 {applied:F1} / {planned:F1} m/s）。", MessageTypeDefOf.NeutralEvent, false);
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
            Messages.Message($"Group {group.GroupId} 已取消第 {index + 1} 段及以后的机动规划。", MessageTypeDefOf.NeutralEvent, false);
        }


        // ── 行星碰撞 ──

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

        /// <summary>
        /// 天体在**系统系（= 根行星系）**里的偏移：根行星（Planet）返零、卫星给"绕父天体"的偏移。
        /// `internal`（2026-09-24）：`LandingAcceptanceRunner` 要按**同一个约定**把船的
        /// `CurrentOrbit.position` 折成天体相对位置（换系只准有一处，不许复制一份）。
        /// </summary>
        internal SystemVec3 GetBodyOffsetSystem(int celestialBodyId, int tick)
        {
            // R5（2026-09-26）：改用**已有的** `celestialRef`（组件引用缓存）⇒ 相位里零组件表扫描。
            // 原来这里 `Current.Game?.GetComponent<CelestialBodyManager>()`，而本方法只被
            // `ComputeGroupPhysics` 在相位里调用（每组每步一次）。
            var mgr = celestialRef.Get();
            if (mgr == null) return SystemVec3.zero;
            var body = mgr.GetBodyById(celestialBodyId);
            if (body == null || body.bodyType == CelestialBodyType.Planet)
                return SystemVec3.zero;
            return new SystemVec3(mgr.GetBodyPositionParent(celestialBodyId, tick));
        }

        private SystemVec3 GetBodyVelocityOffsetSystem(int celestialBodyId, int tick)
        {
            var mgr = celestialRef.Get();
            if (mgr == null) return SystemVec3.zero;
            var body = mgr.GetBodyById(celestialBodyId);
            if (body == null || body.bodyType == CelestialBodyType.Planet)
                return SystemVec3.zero;
            return new SystemVec3(mgr.GetBodyVelocityParent(celestialBodyId, tick));
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
            return $"{(latDeg >= 0.0 ? "北纬" : "南纬")} {Math.Abs(latDeg):F1}° {(lonDeg >= 0.0 ? "东经" : "西经")} {Math.Abs(lonDeg):F1}°";
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
        /// ① **速度取天体相对**（`group.velocityBody`）而不是 `CurrentOrbit.velocity`（系统系/惯性）：
        ///    后者对**月球**会把月球自身 ~2 km/s 的公转算进"撞击速度" ⇒ 任何月球着陆都判坠毁；
        /// ② **再扣地表共转**（<see cref="SurfaceFrame.GroundSpeed"/>）：地球赤道地表共转
        ///    ω·R = 523.6 m/s，不扣的话"跟着地面一起垂直下降"也读 524 ⇒ 阈值取多少都不可能达成"着陆"；
        /// ③ **时刻用 `physicsTick`**（与 `group.positionBody` 同一个时钟）。旧写法用
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
            if (group.landed) return;                // 已在面上（解析档）：不再做碰撞结算

            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            double surfaceRadius = GetBodySurfaceRadius(group.CelestialBodyId);

            // 天体相对的位置/速度：本物理步**刚算好**（`positionBody/velocityBody`），不再另算一份
            DVector3 relPos = group.positionBody;
            DVector3 relVel = group.velocityBody;
            if (relPos.sqrMagnitude > surfaceRadius * surfaceRadius) return;

            // ⚠ 管理器为 null 时 `RotationOmega` 是**显式容忍**的（内部 `mgr?.GetBodyById` ⇒ 取不到天体则用默认一天）
            //   ⇒ 这里给 `!` 只是免去可空标注噪声，不是假设它非空。
            double omega = Launch.GetSurfaceState.RotationOmega(mgr!, group.CelestialBodyId);
            double impactSpeed = SurfaceFrame.GroundSpeed(relPos, relVel, omega);
            (double latRad, double lonRad) = SurfaceFrame.EciToLongLat(relPos, omega, physicsTick);
            string spot = FormatLongLat(latRad, lonRad);
            string bodyName = mgr?.GetBodyName(group.CelestialBodyId) ?? "天体";
            int groupId = group.GroupId;

            // 导弹沿用 AVM 的在册身份；触地即结算弹头，避免进入舰船的安全着陆通知。
            var vehicleManager = Current.Game?.GetComponent<AutonomousVehicleManager>();
            var missile = vehicleManager?.GetRecord(groupId);
            if (missile != null && !string.IsNullOrEmpty(missile.missileDefName))
            {
                Log.Message($"[Collision] Missile G{groupId} IMPACT on {bodyName} at ({spot}) — ground-relative speed {impactSpeed:F1} m/s.");
                var combat = Current.Game?.GetComponent<CombatManager>();
                if (combat != null)
                    combat.ExplodeMissileAt(group, missile, this);
                else
                {
                    vehicleManager!.UnregisterGroup(groupId);
                    DestroyGroup(groupId, $"导弹撞击{bodyName}（{spot}）");
                }
                return;
            }

            // 判据本体 = `SpaceConstants.IsSafeLandingSpeed`（唯一权威、可纯测；本处只调用）
            if (!SpaceConstants.IsSafeLandingSpeed(impactSpeed))
            {
                Log.Message($"[Collision] Group {groupId} CRASHED into {bodyName} at ({spot}) — ground-relative impact speed {impactSpeed:F1} m/s (max safe {SpaceConstants.MaxSafeLandingSpeedMps:F0} m/s). Entities destroyed.");

                Messages.Message($"Group {groupId} 坠毁于{bodyName}（{spot}）：撞击速度 {impactSpeed:F1} m/s，超过安全限值 {SpaceConstants.MaxSafeLandingSpeedMps:F0} m/s。",
                    MessageTypeDefOf.ThreatBig);

                // **坠毁 ⇒ 连地图一起销毁**（2026-10-07 用户裁定）—— 这件事现在**收口在 `DestroyGroup` 内部**，
                // 本处只把原因传进去（2026-10-08 用户裁定："地图处置收口进 DestroyGroup"）。
                DestroyGroup(groupId, $"坠毁于{bodyName}（{spot}），撞击 {impactSpeed:F1} m/s");
                return;
            }
            else
            {
                Log.Message($"[Collision] Group {groupId} LANDED on {bodyName} at ({spot}) — ground-relative touchdown speed {impactSpeed:F1} m/s.");

                Messages.Message($"Group {groupId} 着陆于{bodyName}（{spot}）：接地速度 {impactSpeed:F1} m/s。",
                    MessageTypeDefOf.TaskCompletion);

                // **安全接地 ⇒ 进入 Landed 解析档（保船）**（2026-10-28 用户裁定）
                LandGroup(group, physicsTick);
                return;
            }
        }

        /// <summary>
        /// **跑批器/框架这一段异常的"留痕"出口**（2026-10-08 加固）：去重限频（同一消息只报第一次 + 每 600 次再报一次），
        /// 目的是把"每步都抛 ⇒ 后面的跑批器与超时判定被一起跳过 ⇒ 用例静默冻结"这一类事故**直接显形**。
        /// 用 `Log.Error`（红字）：这条路径上静默是最贵的代价（2026-10-08 我为此查了一整个会话）。
        /// </summary>
        private static readonly Dictionary<string, int> acceptanceTickErrorCounts = new();

        private static void LogAcceptanceTickException(Exception ex)
        {
            string key = ex.GetType().Name + ": " + ex.Message;
            acceptanceTickErrorCounts.TryGetValue(key, out int n);
            acceptanceTickErrorCounts[key] = n + 1;
            if (n == 0 || n % 600 == 0)
            {
                Log.Error($"[验收·异常] 每步块里的跑批器抛出异常（第 {n + 1} 次）⇒ **本步后面的跑批器与异步超时判定都会被跳过**；若持续出现就是「用例冻结（无结算/无超时）」的根因：{ex}");
            }
        }

        /// <summary>
        /// **是否处于「战斗动作」= 交战** —— [D7②] 的信号源。
        ///
        /// **不新建任何交战触发**（2026-10-08 用户裁定："我们本来那个就有，用那个就行"）：
        /// 直接读**既有的唯一口径** `TimeScaleManager.IsInCombatAction()` ——
        /// 它 = **有射弹在飞**（`CombatManager.IsBattleActive` = `activeProjectiles.Count > 0`，
        /// 与"子弹时间"降速用的是同一个源）**或** 导弹处于 Attack 终末自动段（`HasActiveAttack`）。
        /// 那个判据原本是 private（只有时间流速闸在用），本批让它公开 + 走组件缓存，两个读者共用一份实现。
        /// ⚠ 因此它的覆盖比"只看射弹"更宽（导弹终末也算）—— 这正是既有口径的原意。
        /// </summary>
        private bool CombatActionActive
        {
            get
            {
                var tsm = timeScaleRef.Get();
                return tsm != null && tsm.IsInCombatAction();
            }
        }

        /// <summary>
        /// **船上地图处置（销毁收口点）** —— 由 `DestroyGroup` 统一调用（2026-10-08 用户裁定：
        /// "地图处置收口进 `DestroyGroup` 内部"）。**不静默**：日志写明丢掉了什么 + **原因**；
        /// 小人走原版 `MapDeiniter` 的"地图关闭"路径（发信 + 转世界，不凭空消失）。
        /// 相机/当前地图的切换由原版 `Game.DeinitAndRemoveMap` 负责（正在看这张图时会自动切走）。
        /// ⚠ 归档过（`MapArchiveManager`）的船，档案是独立文件 ⇒ 地图被销毁后仍可取档。
        /// </summary>
        private void DisposeGroupMap(int groupId, int mapId, string? reason)
        {
            try
            {
                Map map = SpaceMapGenerator.FindPocketMap(mapId);
                if (map == null) return;
                int buildings = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial).Count;
                int pawns = map.mapPawns.AllPawnsSpawned.Count;
                SpaceMapGenerator.DestroyShipMap(map);
                Log.Message($"[地图] G{groupId} 销毁 ⇒ **船上地图 {mapId} 已清场**：{buildings} 个建筑、{pawns} 个小人（按原版「地图关闭」处理）｜原因：{(string.IsNullOrEmpty(reason) ? "未给（DestroyGroup 调用点未传）" : reason)}");
            }
            catch (Exception ex)
            {
                // 收尸路径吞异常是最难查的一类事故 ⇒ 至少留一行红字（但不要连带把主循环炸掉）
                Log.Error($"[地图] G{groupId} 处置船上地图 {mapId} 时出错：{ex}");
            }
        }

        /// <summary>
        /// **搬运收尾时按需恢复悬浮**（起搬前在悬着才恢复；`where` 只进日志："到达" / "中止"）。
        /// **唯一实现**：到达分支与"交战中止"分支共用（两处各写一份 = 两套权威）。
        /// 不恢复时**必留一行 Warning**（大气层里没有悬浮 = 开始掉，不静默）。
        /// </summary>
        private void TryResumeHoverAfterTransfer(SpaceEntityGroup group, IPropulsionSource src, string where)
        {
            if (!group.hoverResumeAfterTransfer) return;
            group.hoverResumeAfterTransfer = false;
            bool wantAnalytic = group.hoverResumeAnalytic;
            bool dry = src == null || src.TotalPropellantMass <= 0.0;
            string? why = null;
            bool ok = !dry && CanHover(group, wantAnalytic, out why) && SetHoverCore(group, true);
            if (ok && wantAnalytic) group.hoverAnalytic = true;
            if (ok)
            {
                Log.Message($"[搬运] G{group.GroupId} {where} ⇒ **恢复{(wantAnalytic ? "解析" : "物理")}档悬浮**（定点 = 当前位置：φ={group.hoverLatRad:F5} λ0={group.hoverLonRad0:F5} 高度 {group.hoverAltitudeMeters:F1} m）");
            }
            else
            {
                Log.Warning($"[搬运] G{group.GroupId} {where}，但**没能恢复悬浮**（{(dry ? "料尽" : (why ?? "原因未给"))}）——⚠ 在大气层里它会开始掉；要么手动按悬浮，要么离开大气");
            }
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
            if (body == null) { reason = $"天体 {targetBodyId} 不存在"; return false; }
            bodyName = mgr.GetBodyName(targetBodyId);
            if (!body.hasSurface) { reason = $"{bodyName} 没有固体表面 ⇒ 不能降落"; return false; }

            var layer = Launch.GetSurfaceState.GetLayerForBody(mgr, targetBodyId);
            if (layer == null) { reason = $"{bodyName} 没有地表网格（layer 缺失）"; return false; }

            // ⚠ **跨层错配必须在这里挡住**：tile 索引在另一层上"看起来合法"（越界检查会放行），
            //   但落点会在**另一个天体**上 ⇒ 静默落到别处。比的是 LayerDef（实例可能不是同一个对象）。
            if (!target.Valid) { reason = "尚未选中落点（在星球视图上点一个 tile）"; return false; }
            if (!string.Equals(target.LayerDef?.defName, layer.Def?.defName, StringComparison.Ordinal))
            {
                reason = $"选中的落点不在{bodyName}的地表上（当前选中的是另一层地表 —— 先用「查看天体」切过去）";
                return false;
            }
            int tile = target.tileId;
            if (tile < 0 || tile >= layer.TilesCount)
            {
                reason = $"落点 tile {tile} 越界（合法 0..{layer.TilesCount - 1}）";
                return false;
            }

            double tick = TimeScaleManager.GetPhysicsTickSafe();
            int tickI = (int)Math.Round(tick);
            double omega = Launch.GetSurfaceState.RotationOmega(mgr, targetBodyId);
            // ⚠ 参考系硬约定（见 LandingQuote 类注释）：船用 **CurrentOrbit**（系统系）、天体用 GetBody*Offset
            DVector3 offPos = GetBodyOffsetSystem(targetBodyId, tickI).v;
            DVector3 offVel = GetBodyVelocityOffsetSystem(targetBodyId, tickI).v;
            DVector3 tileFixed = Launch.GetSurfaceState.TileToFixedPosition(layer, tile, body.radius);

            quote = LandingQuote.Evaluate(group.CurrentOrbit.positionSystem.v, group.CurrentOrbit.velocitySystem.v,
                offPos, offVel, body.radius, omega, tileFixed, tick);
            if (!quote.ok) { reason = quote.reason; return false; }

            // 落点经纬度（tile 的**固连**经纬度；走既有唯一实现 —— 这里只是把落点换到天体中心系）
            (double latRad, double lonRad) = SurfaceFrame.EciToLongLat(quote.tilePos - offPos, omega, tick);
            spot = FormatLongLat(latRad, lonRad);

            // Δv 可行性：**沿用既有唯一判据**（主网/RCS 接力语义），不另写一份火箭方程
            est = EstimateLandingBurn(group, quote.deltaV);
            if (!est.Feasible)
            {
                reason = $"工质不足：本次需 Δv {quote.deltaV:F0} m/s（工质 {est.RequiredPropellant:F0} kg），当前可达 {est.MaxDeltaV:F0} m/s";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 最近一次主动降落扣料的读数，只作探针/判据用，不序列化。
        /// 语义：`lastLandingQuotedDeltaV` = 当时报的价（m/s）；`lastLandingConsumedKg` = 实际扣掉的工质（kg）。
        /// 两者应满足同一套火箭方程（判据在 `LandingAcceptanceRunner`）。
        /// </summary>
        public double lastLandingQuotedDeltaV = -1.0;
        public double lastLandingConsumedKg = -1.0;

        public bool TryLandAtTile(SpaceEntityGroup group, int targetBodyId, PlanetTile target, out string? reason)
        {
            bool started = Landing.LandingTransitService.TryStart(this, group, targetBodyId, target, 180, out _, out var report);
            reason = started ? null : report.Text;
            if (started) Messages.Message("PFL_LandingStarted".Translate(group.GroupId), MessageTypeDefOf.NeutralEvent);
            return started;
        }

        /// <summary>
        /// 从 group 的推进源工质池扣除工质。返回实际扣除量（kg，受存量截断）。
        /// 实际扣除 < 需求表示工质已耗尽（或无推进源）。
        /// </summary>
        /// <summary>扣工质。**返回 float 是边界 cast**：内部累加器（`IPropulsionSource`）已是 double
        /// （见 `PropulsionData.mass` 的说明）；请求量 `kg` 的 float 量化 ~1e-8 kg 量级，
        /// 比"累加器 float 分辨率 7.8 g"低 5 个数量级 ⇒ 不影响那个已被钉死的缺陷。</summary>
        /// <summary>探针用：本组**罐件**燃料之和（kg）。零件化之后这才是燃料的权威账本。</summary>
        private double SumTankKg(SpaceEntityGroup group)
        {
            double sum = 0.0;
            if (group == null) return sum;
            foreach (int eid in group.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e == null) continue;
                var parts = e.PartsList;
                for (int i = 0; i < parts.Count; i++)
                {
                    var t = parts[i]?.GetComponent<Components.PropellantTankComponentData>();
                    if (t != null && t.massKg > 0.0) sum += t.massKg;
                }
            }
            return sum;
        }

        private float ConsumeGroupPropellant(SpaceEntityGroup group, PropellantDef def, float kg)
        {
            if (def == null || kg <= 0f || group == null) return 0f;   // 无群组/无工质 → 扣 0
            // [调试] 无限燃料（`group.debugInfinitePropellant`）：**报告"扣完了"但一动也不动**。
            // 放在**唯一的扣减口**上 ⇒ 悬浮/解析档/搬运/料尽判据全都自动跟着"料一直在"，
            // 而质量与推力一个字都没改（**不能靠把罐加大来假装无限**：那会把重量加上去、反而悬不住）。
            if (group.debugInfinitePropellant) return kg;
            var src = GetGroupPropulsionSource(group);
            if (src == null) return 0f; // 无推进源 → 扣 0
            return (float)src.TryConsumePropellant(def, kg);
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
            return $"主={main:F0} RCS={rcs:F0}kg";
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
        public IPropulsionSource? GetGroupPropulsionSource(SpaceEntityGroup group)
        {
            // B6：先看本组有多少张图 —— 多图/图+无图实体都走多源聚合；一图也走（口径统一）。
            // P0-1：走 SpaceEntity 的图链缓存（只随挂图/解挂/主图变化失效），不再每次扫全部 part。
            int mapStamp = group.entitySetVersion;
            int mapCount = 0;
            foreach (int eid in group.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e == null) continue;
                if (e.HasPropulsionData)
                    mapStamp = mapStamp * 31 + e.PropulsionVersion;
                mapStamp = mapStamp * 31 + e.mapLinkVersion;
                var mapIds = e.GetPartLinkedMapIdsCached();
                for (int i = 0; i < mapIds.Count; i++)
                {
                    var mc = e.GetMapComp(mapIds[i]);
                    if (mc == null) continue;
                    mapCount++;
                    mapStamp = mapStamp * 31 + mc.NetworksVersion;
                    mapStamp = mapStamp * 31 + (int)mc.disabledSystems;
                    mapStamp = mapStamp * 31 + 1;   // 图数变化也进指纹
                }
            }

            if (mapCount > 0)
            {
                var agg = group.cachedMapPropulsionAggregator;
                if (agg == null || agg.SourceStamp != mapStamp)
                {
                    var sources = new List<IPropulsionSource>(mapCount + 2);
                    var seenMaps = new HashSet<int>();
                    foreach (int eid in group.GetEntityIds())
                    {
                        var e = GetEntityById(eid);
                        if (e == null) continue;
                        var mapIds = e.GetPartLinkedMapIdsCached();
                        for (int i = 0; i < mapIds.Count; i++)
                        {
                            var mc = e.GetMapComp(mapIds[i]);
                            if (mc != null && seenMaps.Add(mc.map.uniqueID)) sources.Add(mc);
                        }
                        if (e.HasPropulsionData) sources.Add(e);
                    }
                    agg = new CompositePropulsionSource(sources, mapStamp);
                    group.cachedMapPropulsionAggregator = agg;
                }
                return agg;
            }

            var mapComp = group.GetLinkedMapComp();
            if (mapComp != null) return mapComp;
            SpaceEntity? first = null;
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
        /// <summary>群组 dV/工质读数。**工质两项是 double**（与累加器同精度：面板/验收读数不该比物理更粗）。</summary>
        /// <summary>
        /// **[A8]/③ 面板口径：某工质网该显示什么**（"全推续航"而不是 Δv）—— 2026-10-07 补。
        ///
        /// **为什么**：Tsiolkovsky 的 Δv 对**吸气式**没有指挥意义 —— 它的 `ve_eff` 是**包线给定**的等效值，
        /// 随 ρ 与 Mach 变，"还能变多少速"由当时的空气决定 ⇒ 能答的只有"**这么开还能开多久**"（`存量/ṁ`）。
        /// 判据（"这条网是不是环境相关"）= **有包线表 或 有附带进气**：两者任一存在，Δv 就不是可承诺量。
        ///
        /// **本件只给口径与数据（单一出处）**：面板 UI 在 PFView 那条线，本线不碰它的文件。
        /// 返回：每条网 —— 工质 / 是否环境相关（该显示续航）/ 全推续航秒 / 该网现存料（kg）/ ṁ。
        /// </summary>
        public List<(PropellantDef propellant, bool environmentDependent, double enduranceSeconds,
            double propellantMass, double massFlow)> GetGroupEnduranceReport(SpaceEntityGroup group)
        {
            var result = new List<(PropellantDef propellant, bool environmentDependent, double enduranceSeconds,
                double propellantMass, double massFlow)>();
            if (group == null) return result;
            var src = GetGroupPropulsionSource(group);
            if (src == null) return result;

            var nets = src.Networks;
            for (int i = 0; i < nets.Count; i++)
            {
                var n = nets[i];
                if (n.MassFlow <= 0f) continue;                      // 不成网的条目（如空气罐）不在此列
                // "环境相关" = 有包线表（a≠0 / 折线）**或**要另外抽气（空气）⇒ Δv 不可承诺，改报续航
                bool envDependent = n.envelopeTerms != null || n.gasDraws != null;
                double fuel = n.AvailableMass(src);
                result.Add((
                    n.propellant,
                    envDependent,
                    AirBreathingCore.FullThrottleEnduranceSeconds(fuel, n.MassFlow),
                    fuel,
                    n.MassFlow));
            }
            return result;
        }

        public (float remainingDv, float maxDv, double propellantMass, double propellantCapacity) GetGroupDeltaV(SpaceEntityGroup group)
        {
            if (group == null) return (0f, 0f, 0f, 0f);
            var src = GetGroupPropulsionSource(group);
            if (src == null) return (0f, 0f, 0.0, 0.0);
            if (src.EffectiveVe <= 0f)
                return (0f, 0f, src.TotalPropellantMass, src.TotalPropellantCapacity);
            double dry = GetGroupMass(group, null);
            double wet = dry + src.TotalPropellantMass;
            double wetMax = dry + src.TotalPropellantCapacity;
            float remaining = wet > dry ? (float)(src.EffectiveVe * Math.Log(wet / dry)) : 0f;
            float maxDv = wetMax > dry ? (float)(src.EffectiveVe * Math.Log(wetMax / dry)) : 0f;
            return (remaining, maxDv, src.TotalPropellantMass, src.TotalPropellantCapacity);
        }



        // ── 持久化 ──

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

        /// <summary>
        /// 读档/新局收尾后预热（2026-10-19 尖峰归因）：
        /// 实测第一次碰撞帧 11.2ms 里碰撞本身只有 1.1ms；2.5ms 是引力源表**首次构建**、
        /// 1.3ms 是 apply 块里调试/巡检路径首次 JIT。这类一次性成本必须放在 **FinalizeInit**
        /// 而不是 `ExposeData(PostLoadInit)`：`CelestialBodyManager.ExposeData` 会在 PostLoadInit
        /// 把 `initialized` 置回 false，早于它的预热会被随后的重建吃掉（2026-10-19 13:20 日志实证）。
        /// 失败只告警：预热是优化，不是读档依赖。
        /// </summary>
        public override void FinalizeInit()
        {
            base.FinalizeInit();
            try
            {
                var warmMgr = Current.Game?.GetComponent<CelestialBodyManager>();
                if (warmMgr != null)
                {
                    double warmTick = TimeScaleManager.GetPhysicsTickSafe();
                    warmMgr.PrefetchSystemSources(1, warmTick);   // 1 = 地球（与调试场景同值）
                    ApplyAnalyticTransfer(0.0, warmTick, warmMgr);
                    ApplyHover(0.0, warmTick, warmMgr);

                    // 最大的一笔：`UpdateAllMovements` 自身（~千行）的首次 JIT 落在第一个物理步的
                    // "阶段之前"（步钟已启动、第一个阶段钟还没开始）⇒ 实测 12–13ms 残差。
                    // 这里用 early-return 预热：方法体在入口处已被完整编译，随后立刻返回，不碰任何状态。
                    warmupPhysicsStep = true;
                    // 形参在标志检查之后才会被碰 ⇒ 传 null! 安全（只为触发方法体 JIT）
                    try { UpdateAllMovements(Current.Game?.GetComponent<TimeScaleManager>()!); }
                    finally { warmupPhysicsStep = false; }

                    // 合成组预热：`ComputeGroupPhysics` 的首次 JIT / 天体场路径只在“有组的第一步”付
                    // （2026-10-19 13:32 日志：碰撞帧的组物理 4,326µs，扣掉引力/源表后 ~3.6ms 是首次路径）。
                    // 临时组不注册进 groups / 细网格、不可碰撞、远场；结果丢弃，不落盘。
                    var warmGroup = new SpaceEntityGroup(-1, 0)
                    {
                        IsActive = true,
                        IsMovable = true,
                        IsCollidable = false,
                        CelestialBodyId = 1,
                        CurrentOrbit = new OrbitState
                        {
                            positionSystem = (SystemVec3)new DVector3(1.0e9, 0.0, 0.0),
                            velocitySystem = (SystemVec3)new DVector3(0.0, 3000.0, 0.0),
                            rotation = DQuaternion.identity
                        }
                    };
                    ComputeGroupPhysics(warmGroup, 0.0, warmTick, warmMgr, new GravityScratch(), 1);

                    // 碰撞纯函数预热（2026-10-19 P0）：实测隔离冷启动 CollectPartContacts 1.8–2.3ms、
                    // TryFindDeepestPartPair 0.5–0.7ms、CollisionSolver 4.4–5.6ms。纯静态、无组/实体/
                    // 地图状态；10 km/s 的爆炸/伤害路径不覆盖（需另案隔离预热）。
                    CombatManager.WarmupCollisionPaths();

                    // 阶梯规划预热（2026-10-21）：游戏内 MakePlan 6.9–9.2ms vs 台架 0.2–0.5ms。
                    // **实测结论（重要）**：net10 上 `DOTNET_TieredCompilation=0` 时全支 0.26–0.95ms，
                    // 开着分层编译时即使 24 次热身仍 2.2ms、**200 次才收敛到 0.27/0.78ms** ⇒
                    // 贵的不是算法、是 **tier0 代码**（每组每 120s 才调一次 ⇒ 永远升不上 tier1）。
                    // 所以：①同步 24 次兜住"读档后马上要用"的第一拍；②后台再跑 200 次把 tier1 催到位
                    // （结果丢弃、不碰状态；`Warmup` 是纯函数，后台线程安全）。曲线只在 DevMode 打。
                    double ladderMu = GetBodyGM(1);
                    double ladderBodyR = GetBodySafeRadius(1);
                    string ladderWarm = RendezvousLadder.Warmup(ladderMu, ladderBodyR, 24);
                    if (ladderWarm.Length > 0 && Prefs.DevMode)
                        Log.Message($"[阶梯·预热] {ladderWarm}");
                    try
                    {
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try { ladderWarmBg = RendezvousLadder.Warmup(ladderMu, ladderBodyR, 200); }
                            catch { /* 后台预热失败无声：只影响速度 */ }
                        });
                    }
                    catch { /* 线程池不可用时静默降级 */ }
                }
            }
            catch (System.Exception e)
            {
                Log.Warning($"[Prefetch] 初始化预热失败（不影响读档）：{e.GetType().Name}: {e.Message}");
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref entities, "entities", LookMode.Value, LookMode.Deep);
            Scribe_Collections.Look(ref groups, "groups", LookMode.Value, LookMode.Deep);
            Scribe_Values.Look(ref nextGroupId, "nextGroupId", 0);
            Scribe_Values.Look(ref nextEntityId, "nextEntityId", 0);
            // 载荷注册表（2026-10-26）：id → 载荷设计（深拷）；载荷舱只存 id。
            Scribe_Collections.Look(ref payloadDesigns, "payloadDesigns", LookMode.Value, LookMode.Deep);
            Scribe_Values.Look(ref nextPayloadDesignId, "nextPayloadDesignId", 1);
            if (payloadDesigns == null) payloadDesigns = new Dictionary<int, GroundAssembly.RocketDesignSpec>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                RebuildAutoStagePlans();
                nextGroupId = Math.Max(nextGroupId,MaxLiveId(groups.Keys)+1);
                nextEntityId = Math.Max(nextEntityId,MaxLiveId(entities.Keys)+1);
                planetGrids = new Dictionary<int, SpatialGrid3D<ISpatialEntity>>();
                groupLastCoarsePos = new Dictionary<int, DVector3>();
                hoverActiveCount = 0;   // [A7] 静态计数**按档重数**（静态量跨会话 ⇒ 不重数就会与档差一位）
                foreach (var group in groups.Values)
                {
                    if (group.hoverActive) hoverActiveCount++;
                    var targetGrid = GetGridForGroup(group.GroupId);
                    if (targetGrid != null)
                    {
                        targetGrid.Add(group, group.Position);
                        groupLastCoarsePos[group.GroupId] = group.Position;
                    }
                }

            }
        }

    }
}



