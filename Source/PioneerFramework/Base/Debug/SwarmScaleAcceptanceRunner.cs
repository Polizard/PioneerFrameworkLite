using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **规模验收：≥32 组下的可复现性**（批 0-4，2026-09-26）。
    ///
    /// ## 为什么要有这个场景
    /// 最终计划把阶段 2（正确性）的**开工前提**定成"必须有一个 ≥32 组的验收场景"：
    /// `SpaceGameComp` 的并行物理区下限是 <see cref="SpaceGameComp.PARALLEL_PHYSICS_MIN_GROUPS"/>，
    /// 而 R1–R5 那五条并行竞态**只在该阈值之上才会发生** ⇒ 现有闸门与既有游戏内场景**都测不到它们**。
    /// 不先建这个场景，改完 R1–R5 也**没有判据能证明改对了**（本库反复栽在"判据不覆盖 ⇒ 假绿"）。
    ///
    /// ## 它的首要验收是**可复现性**，不是性能
    /// R3（`BurnDiagNextTick` 静态读-改-写）的后果是**行为不确定**，而不确定会让本库
    /// "同场景同机器前后对比"这套性能判据的**地基**失效 ⇒ 先钉住"同一场景连跑三次，物理状态逐位相同"。
    ///
    /// ## 两个变体（**这是 2026-09-26 加第二个变体时发现的事**）
    /// **变体 A「解析圆轨」**：`circularOrbit: true` ⇒ 位置/速度由 `GetCircularPositionAtTick` 解析给出
    ///   ⇒ **`pos`/`vel` 半张指纹是"tick 的函数"，按构造必然逐轮相同**。
    ///   ⇒ 它对"并行区里某一步的结果被丢掉"这类后果**只是偶尔抓得到**（要那一步正好落在指纹窗口内）。
    ///   ⚠ 所以"三轮指纹相同"在变体 A 里**比看上去弱得多**（真正有信号的只有 `rotation`/`remainingBurnDV`/
    ///   `burnDiag*` 与轨道保持结算）。这一条**不是**可选项，是 2026-09-26 逐行读代码查出来的。
    /// **变体 B「积分 + 燃烧」**：`circularOrbit: false` ⇒ 走 `NBodyIntegrator.ThirdOrderTaylorStep` 真积分，
    ///   且**全群同时点火**（进并行区的燃烧路径）⇒ 提供两样变体 A 给不了的东西：
    ///   ① 位置/速度**依赖积分结果** ⇒ 丢掉一步 = **永久**位置差 ⇒ 判据从"偶尔"变成"必然"；
    ///   ② `HasActiveBurn` 每步为真 ⇒ 诊断块（R3 的静态节流 / R4 的 8 处日志 / R7 的引力探针）**真的被走到**。
    ///
    /// ## 它证明什么 / 不证明什么（**不许用沉默冒充覆盖**）
    /// · 证明：**可动组数 ≥ 并行下限**（读的就是并行区构建 `movableGroups` 的同一个集合 `GetAllGroups()` + `IsMovable`）
    ///   ⇒ 并行分支**必然**被走到；且三次运行的物理指纹（位置/速度/姿态/燃烧记账/诊断字段，**按 GroupId 排序**）逐位相同。
    /// · **不**证明：冷缓存窗口（R1 的竞争面）· 绕月（非根天体）路径 · "解析圆轨 + 直接 `StartBurn`"
    ///   （那条路位置仍由解析圆轨给出 ⇒ 烧了不动；真实的机动执行器会先 `ExitCircularOrbit`）。
    ///   三项都**显式登记为 UNCHECKED**。
    ///
    /// ## 为什么指纹要按 GroupId 排序
    /// 组 id 是**单调递增、永不复用**的（`SpaceGameComp` 的 id 分配器），三次运行拿到的 id 区间不同
    /// ⇒ 按"字典遍历顺序"取指纹会因插入顺序不同而假红。按 id 排序后，序列 == 铺场顺序 ⇒ 跨轮可比。
    ///
    /// ## 与既有框架的关系
    /// 判定与记账全走 <see cref="PflTestKit"/>；清场走它的 `SnapshotGroups`/`CleanupSince`（**只销毁本次新建的组**，
    /// 不动玩家资产）。它是**异步段** ⇒ 按框架规矩要**单独登记**（登记表只有一个槽位），
    /// 所以 `FullTestRunner` 里只登记一行"要自己跑"，不并进去抢槽位。
    ///
    /// ## ⚠ 两个已知局限（都会表现为"点了没反应"，先看这里别怀疑功能坏了）
    /// 1. **暂停会静默挂住**：推进与超时都读 `Find.TickManager.TicksGame` ⇒ **游戏暂停时时钟不前进**，
    ///    于是既不会推进、也不会超时。跑之前请**关掉那些 `forcePause` 的窗口**（计划编辑器 / 机动规划器等）。
    ///    这与既有几个跑批器同源，不是本场景引入的。
    /// 2. 本场景只覆盖**地球系 / 赤道近圆轨 / 不燃烧 / 热缓存**四条件；其余三项已在结论块里显式登记为 UNCHECKED。
    /// </summary>
    public static class SwarmScaleAcceptanceRunner
    {
        // ── 场景常数：**全部确定，不许有随机数**（可复现性的前提）──
        private const int GroupCount = 64;   // ≥ SpaceGameComp.PARALLEL_PHYSICS_MIN_GROUPS；也是"目标 N"的一个采样点
        private const int RunCount = 3;
        // `StepsPerRun` 的语义是**物理步数**；实现上用 `GetPhysicsTick()` 的变化次数间接计（= tick 跨度）。
        // ⚠ 注意口径：`GetPhysicsTick()` 是 `TicksGame` 的函数 ⇒ **每真实 tick 都变**，所以数出来的是 **tick 跨度**。
        //    它在定档（`scale` 不变）下是步数的**充分代理**：`步数 = floor(tick 跨度 / UPDATE_INTERVAL_TICKS)`，
        //    ⇒ tick 跨度逐轮相同 ⇒ 步数逐轮相同。名称按实际测的东西写（tick 跨度），不叫"步数"。
        private const int StepsPerRun = 30;
        // ⚠ **不要**"铺场后再等 N tick 才开始计数"（2026-09-26 第一版这么写，是错的）：
        //   圆轨位置 = f(physicsTick − circularStartTick)，而 `circularStartTick` 是**铺场当拍**记下的。
        //   物理步每 2 tick 一拍 ⇒ "等 4 tick" 里走了 **1 步还是 2 步随奇偶翻转** ⇒
        //   计数基准落在铺场之后 = 把奇偶问题从推进段挪到等待段，指纹照样假红。
        //   ⇒ **计数基准必须在铺场当拍**，且 `SpawnRun` 自己记（见该方法的末尾）。
        private const double BaseAltitude = 500_000.0;  // 与 SpawnAxialThrustTest 同量级

        /// <summary>变体 B 的燃烧量：≈30 s 满推燃烧（a≈7.87 m/s²·240 m/s≈30 s），
        /// 恰好铺满整个推进窗口 ⇒ 取指纹时 `HasActiveBurn` 仍为真、诊断块被走到 ≥3 次。
        /// 轨道后果：Δv/v≈3.1% ⇒ 远地点升 ~430 km，**不入地**（安全性由"根域引力 + 无 BLOCKED 路径"保证）。
        /// ⚠ 与 `StepsPerRun` 是**一对量纲**：改一个必须看另一个（燃烧若先于窗口结束，判据面就空了一半）。</summary>
        private const double BurnDv = 240.0;

        /// <summary>两个变体。**名字要能进用例名**（两个变体各自一行验收结论，不许互相覆盖）。</summary>
        private enum Variant { AnalyticCircular, IntegratedBurn, ScaleSweep }

        // ── N 扫描（2026-09-27，用户提议）：**不同组数下的"每群每步"代价** ────────────────
        // 为什么比"单场景占比"强：计划批 3 的判据原文就是"**每群每步分配不随 N 上升**"，
        // 而占比会被场景本身（多少组在燃烧、多少在算场）带偏 ⇒ **只有斜率才是"N 未知"下真正要的答案**。
        //   · 跨过 `PARALLEL_PHYSICS_MIN_GROUPS = 32` ⇒ **天然是"顺序 vs 并行"的 A/B**（<32 走顺序循环）；
        //   · 每档取"每步每组的 µs"（= 该档 µs/步 ÷ N）⇒ 逐档比它是否变大。
        // ⚠ 用**常驻**的步钟/引力段/建表段（"★ 2026-09-20 改为常驻测量"）⇒ 不需要勾探针开关。
        private static readonly int[] SweepNs = { 8, 16, 32, 64, 128 };
        private const int SweepSteps = 60;   // 每档的 tick 跨度（= 30 物理步；比 StepsPerRun 大一倍 ⇒ 读数更稳）
        private static int sweepIndex;
        private static readonly double[] swAvgUs = new double[SweepNs.Length];
        private static readonly double[] swPerGroupUs = new double[SweepNs.Length];
        private static readonly double[] swGravUs = new double[SweepNs.Length];
        private static readonly double[] swGravPerGroupUs = new double[SweepNs.Length];
        private static readonly double[] swCollectUs = new double[SweepNs.Length];
        private static readonly double[] swMaxUs = new double[SweepNs.Length];
        private static readonly int[] swSteps = new int[SweepNs.Length];
        private static readonly int[] swGroups = new int[SweepNs.Length];
        private static readonly int[] swGen0 = new int[SweepNs.Length];

        private static double ToMicros(double stopwatchTicks)
            => stopwatchTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency;

        private static Variant variant = Variant.AnalyticCircular;

        /// <summary>同一变体**连跑几轮**（2026-09-27 加）。
        /// 为什么需要：唯一还没收口的读数是"变体 B 三轮指纹偶发不一致"（实测 1.7e-5 m 级，
        /// 只在某一轮出现过一次、下一次就没了 ⇒ **间歇**）。一次点击只有 3 轮 ⇒ 命中率约 15%/轮
        /// ⇒ 单次点击常常看不到它。连跑 4 次（= 12 轮）把命中率抬到 ~86%，**一次点击就能抓到**，
        /// 抓到就由逐步指纹报出"第几个物理时刻变化点"。它不是新判据：每轮各自独立结算，
        /// 连跑只是把"多跑几次"自动化（否则要靠人点 4 次，且每次都得更仔细看日志）。</summary>
        private static int repeatsLeft;

        /// <summary>**猎捕模式**剩余次数（2026-09-27 加）：连跑到**抓到一次红为止**（或到上限）。
        /// 为什么要有它：那个间歇的指纹分歧实测约 10%/轮 ⇒ 一次点击（3 轮）常常看不到、而固定 ×4
        /// 又可能被中途打断（实测发生过：只跑了 2 跑）。猎捕 = "跑到抓到为止"，**抓到就停**
        /// （继续跑只浪费时间，答案已经在日志里了）。上限 10 次 ≈ 30 轮 ⇒ 命中率 &gt;95%。</summary>
        private static int huntLeft;

        /// <summary>上一跑是否红了（猎捕模式据此决定"还跑不跑"；每轮开始时重置）。</summary>
        private static bool lastRunRed;

        private static int runSerial;   // 本次点击里的第几跑（连跑时进用例名，免得几跑在结论表里重名）

        private static string CaseNameFor(Variant v)
        {
            string baseName = v == Variant.AnalyticCircular
                ? "规模验收（≥32 组 · 可复现性 · 解析圆轨）"
                : v == Variant.IntegratedBurn
                    ? "规模验收（≥32 组 · 可复现性 · 积分+燃烧）"
                    : "规模验收·N 扫描（8/16/32/64/128 组）";   // ⚠ 必须单独一支：否则扫描会被印成"积分+燃烧"（实测踩到）
            return runSerial > 1 ? baseName + " ·第" + runSerial + "跑" : baseName;
        }

        private const string Category = "00 验收";

        private enum Stage { Idle, Advance, Done }

        private static Stage stage = Stage.Idle;
        private static int runIndex;

        private static readonly ulong[] physicsHash = new ulong[RunCount];
        private static readonly int[] countedGroups = new int[RunCount];
        private static readonly int[] movableGroups = new int[RunCount];
        private static readonly double[] timeScale = new double[RunCount];
        private static readonly HashSet<int> spawned = new HashSet<int>();
        private static long missBaseline;        // 读数用（未命中数含预暖那一次，不作判据）
        private static long inRegionBaseline;     // R1 直接判据：并行区相位里建表的计数
        private static long sliceChecksBaseline;  // 变体 B 的正对照：切片 scratch 检查次数（燃烧路径走过的证据）
        private static long sharedWriteBaseline;  // G1 判据：相位里共享写次数（**增量**口径 ⇒ 自检不污染它）
        private static long globalLookupBaseline; // G1/R5 判据：相位里全局查找次数（同上）

        // ── 推进用：**按物理步数计数**，不按真实 tick ──────────────────────────────
        // 为什么必须这样（2026-09-26 第一跑就栽在这上面）：
        //   物理步每 2 tick 走一次，而本状态机每一轮跨 `SettleTicks + RunTicks + 1` tick。
        //   原先取指纹发生在"越界后的下一拍" ⇒ 轮周期 = 4+60+1 = **65（奇数）** ⇒
        //   步节拍相位逐轮翻转 ⇒ **交替的轮次里物理步数差一步** ⇒ 指纹假红。
        //   读数里当时正是 `1==3 ≠ 2` 这个形状。⇒ 改成"数物理步 + 越界当拍取指纹"。
        private static double lastPhysicsTick;
        private static int tickSpanThisRun;   // 观察到的**物理时刻变化次数** = tick 跨度（见下注）
        private static readonly int[] tickSpanPerRound = new int[RunCount];
        private static readonly double[] endPhysicsTick = new double[RunCount];
        private static readonly double[] startPhysicsTick = new double[RunCount];   // 铺场当拍的物理时刻（绝对值 ⇒ 只进读数）

        // ── 定位用：每轮每组的**同一个字段表** ────────────────────────────────────────
        // ⭐ 哈希与留档**共用这张表** ⇒ 指纹一旦不同，必然能报出"第几个组 / 哪个字段 / 两轮各是多少"，
        //    不会出现"差异在没留档的字段上"这种含糊结局（2026-09-26：首跑 FAIL 因为缺定位花了一整轮）。

        /// <summary>
        /// **判据的容差**（2026-09-27 用户裁定改为有界容差）。
        /// **界从哪来**：位置/速度是逐步浮点累加的结果，单步相对误差 ~1e-16、几步累加 ~1e-14 量级
        /// ⇒ 取 **1e-9** 有 5 个数量级余量（实测到的最大偏差是 **3.1e-12**，即"2 个 float ULP 的质量差"
        /// 传播到位置上）。
        /// **为什么这不是放水**：要抓的故障（竞态、丢步、丢更新）**超出这个界好几个数量级** ——
        /// R1 的真身是"**整个物理步没算**"，差的是**公里级**（相对 1e-4 量级）⇒ 在 1e-9 的界下照样当场红。
        /// 而"逐位相同"这条**比物理需要严得多**：任何含浮点累加的路径都会周期性给出差 1–2 ULP 的结果，
        /// 每次红都要人来分辨是噪声还是故障 —— 实测为此烧掉了 6 轮人机往返（见计划 §2 的纪律条）。
        /// </summary>
        private const double FingerprintRelTol = 1e-9;

        private static readonly string[] FieldNames =
        {
            "pos.x", "pos.y", "pos.z", "vel.x", "vel.y", "vel.z",
            "rot.x", "rot.y", "rot.z", "rot.w",
            // ⚠ **绝对时钟值不许进哈希**：`circularStartTick` 是"铺场那一刻的物理 tick"，
            //    按构造逐轮必然不同（77/107/137）⇒ 放进来会让每一轮永远"不可复现"。
            //    ⇒ 它进**读数**（见 startPhysicsTick），不进字段表。
            "circRadius", "circStartAngle", "circAngVel",
            "remainingBurnDV",
            // ⚠ **`burnDiagTick` 不许进哈希**（2026-09-26 实测踩到：它是**绝对时刻** 1094/1124/1154
            //   ⇒ 逐轮必然不同 ⇒ 让变体 B 恒红，而那是**仪器造的假红**，不是竞争。
            //   第二例"绝对量进哈希"陷阱（第一例是 `circularStartTick`）。
            //   ⇒ 换成**无量纲计数** `burnDiagRuns`：它同时就是 R3 的直接判据（改前 1 个组=1、63 个组=0）。
            "burnDiagRuns", "burnDiagRemaining",
            // 本步燃烧的**输入**（2026-09-27 加）：逐步指纹定位到"某一步某组的 remainingBurnDV 差 3.3e-6"
            // 之后，下一步要回答的就是"**哪个输入**差"——这三列 + `burnLastCmdDv` 直接给答案。
            "burnLastCmdDv", "burnDbgF", "burnDbgMf", "burnDbgMass",
            // 变体 B 的真推力证据：`appliedBurnDV` 只由燃烧步累加 ⇒ 它 >0 才说明"燃烧真的出了推力"
            // （只有 `HasActiveBurn` 为真是不够的：没对准时推力恒 0，路径走到了但什么也没发生）。
            "appliedBurnDV",
        };

        /// <summary>**哪些列必须精确相等**（与 `FieldNames` **同序**，长度必须一致）。
        /// 为什么需要分列：判据的**有界容差**只对"浮点累加量"成立 ——
        /// **计数列（如 `burnDiagRuns`）差 1 就是真差异**，给它容差等于放水。
        /// ⇒ 只有"本步被诊断几次"是计数；其余（位置/速度/姿态/Δv/质量/ΣF/Σṁ）都是浮点量。
        ///
        /// ⚠⚠ **本字段必须声明在 `FieldNames` 之后**（2026-09-27 实测：放前面 ⇒ 静态字段初值化
        /// 按**文本顺序**执行 ⇒ 轮到它时 `FieldNames` 还是 null ⇒ `Enumerable.Select` 抛
        /// `ArgumentNullException` ⇒ 整个类型的静态构造抛 `TypeInitializationException`**，
        /// 而调试页签每建一次节点就再抛一次 ⇒ 一进游戏就满屏红字）。
        /// **这一类 bug 编译器与单元测试都抓不到**（静态序是运行期的；测试不碰这个调试类）
        /// ⇒ 只能靠这条注释 + 「触达测试」（见 `Tests` 里那份把所有调试跑批器都点一遍的用例）。</summary>
        private static readonly bool[] FieldExact = FieldNames.Select(n => n == "burnDiagRuns").ToArray();

        /// <summary>
        /// **逐列的绝对地板**（与 `FieldNames` **同序**，长度必须一致）—— 判据的 `atol`。
        /// 定法：取"**物理上不可见**"的量级 ⇒ 位置 1 mm、速度 1 µm/s、质量 20 g、Δv 1e-5 m/s…
        /// 于是**近零列不会爆相对式**（实测教训：纯相对式报了 1.38e-6 假红，而位置其实只差 1.3e-11）。
        /// **为什么这不是放水**：要抓的故障是**整步没算 / 丢更新** ⇒ 位置差是**公里级**（≫ 1 mm）、
        /// 计数列仍要求**精确相等**（`FieldExact`）⇒ 判据的力量一点没减。
        ///
        /// ⚠ `burnDbgMass` 的地板给到 **0.02 kg** 是有依据的、不是凑的：工质质量存在
        /// `PropulsionData.mass`（**float**），在 72 t 处 1 ULP = **0.0078 kg**
        /// ⇒ 实测到的差正是 **0.015625 kg = 2 ULP**（2026-09-27 逐步定位给出：某步某组一次扣减落在
        /// 不同舍入上，再被 `accel = F/m` 放大成 3.4e-6 m/s 的 Δv 差）。**要求比这更严等于要求 float
        /// 有 double 的分辨率** —— 那是**平台缺陷**（已登记为根因），不是这条判据该拦的东西。
        /// </summary>
        private static readonly double[] FieldAtol =
        {
            1e-3, 1e-3, 1e-3,          // pos.x/y/z（m）
            1e-6, 1e-6, 1e-6,          // vel.x/y/z（m/s）
            1e-9, 1e-9, 1e-9, 1e-9,    // rot.x/y/z/w（单位四元数）
            1e-3,                      // circRadius（m）
            1e-9,                      // circStartAngle（rad）
            1e-12,                     // circAngVel（rad/s）
            1e-5,                      // remainingBurnDV（m/s）
            0.0,                       // burnDiagRuns（计数 ⇒ 走 FieldExact 精确比，地板无意义）
            1e-5,                      // burnDiagRemaining（m/s）
            1e-5,                      // burnLastCmdDv（m/s）
            1e-3,                      // burnDbgF（N）
            1e-9,                      // burnDbgMf（kg/s）
            2e-2,                      // burnDbgMass（kg）—— 见上：float 质量在 72 t 处的 2 ULP
            1e-5,                      // appliedBurnDV（m/s）
        };

        private static readonly List<int>[] roundIds = NewRounds<int>();
        private static readonly List<double[]>[] roundFields = NewRounds<double[]>();

        private static List<T>[] NewRounds<T>()
        {
            var a = new List<T>[RunCount];
            for (int i = 0; i < RunCount; i++) a[i] = new List<T>();
            return a;
        }

        /// <summary>正在跑（菜单据此置灰）。</summary>
        public static bool Running => stage != Stage.Idle;

        /// <summary>入口（调试页签按钮）· 变体 A：解析圆轨。</summary>
        public static void Run() => Start(Variant.AnalyticCircular);

        /// <summary>入口 · 变体 B：积分 + 全群燃烧（R3/R4/R7 的判据面）。</summary>
        public static void RunIntegratedBurn() => Start(Variant.IntegratedBurn);

        /// <summary>入口 · 变体 B **连跑 4 次**（抓那个间歇的指纹分歧；见 `repeatsLeft` 的说明）。</summary>
        public static void RunIntegratedBurnX4()
        {
            if (Running) { Log.Message("[规模验收] 已在跑 ⇒ 忽略本次"); return; }
            repeatsLeft = 3;   // 加上本次共 4 次
            huntLeft = 0;
            Start(Variant.IntegratedBurn);
        }

        /// <summary>入口 · 变体 B **猎捕模式**：跑到抓到一次红为止（上限 10 次）。</summary>
        public static void RunIntegratedBurnHunt()
        {
            if (Running) { Log.Message("[规模验收] 已在跑 ⇒ 忽略本次"); return; }
            repeatsLeft = 0;
            huntLeft = 9;      // 加上本次共 10 次
            Log.Message("[规模验收] 猎捕模式：最多 10 次，**抓到红就停**（每次 3 轮）");
            Start(Variant.IntegratedBurn);
        }

        /// <summary>入口 · **N 扫描**：8/16/32/64/128 组，各跑 30 物理步，报"每群每步"代价与斜率。
        /// 32 是顺序/并行的分界 ⇒ 同一份扫描顺带量出并行区的实际收益。</summary>
        public static void RunScaleSweep()
        {
            if (Running) { Log.Message("[规模验收] 已在跑 ⇒ 忽略本次"); return; }
            repeatsLeft = 0;
            huntLeft = 0;
            sweepIndex = 0;
            Log.Message("[规模验收] N 扫描：档位 8/16/32/64/128 组，每档 30 物理步（顺序/并行分界 = 32）");
            Start(Variant.ScaleSweep);
        }

        private static void Start(Variant v)
        {
            if (Running)
            {
                Log.Message("[规模验收] 已在跑 ⇒ 忽略本次");
                return;
            }

            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var tsm = Current.Game?.GetComponent<TimeScaleManager>();
            if (sg == null || mgr == null || tsm == null)
            {
                PflTestKit.Begin(CaseNameFor(v), Category);
                PflTestKit.Unchecked("前置", "无 SpaceGameComp / 天体管理器 / 时间档管理器 ⇒ 一条判据都跑不了");
                return;
            }

            variant = v;
            lastRunRed = false;
            if (repeatsLeft <= 0 && huntLeft <= 0) runSerial = 1; else runSerial++;
            int timeout = RunCount * (StepsPerRun * SpaceGameComp.UPDATE_INTERVAL_TICKS + 90) + 600;
            PflTestKit.Begin(CaseNameFor(v), Category, timeout);
            PflTestKit.Note(string.Format("目标 N={0}（默认口径 256）｜本场景铺 {1} 组 × {2} 轮 × {3} 物理时刻跨度｜{4}",
                "256（最终计划默认）", GroupCount, RunCount, StepsPerRun,
                v == Variant.AnalyticCircular
                    ? "解析圆轨（位置=tick 的函数）· 不燃烧"
                    : "非圆轨（三阶泰勒积分）· 全群燃烧 " + BurnDv.ToString("F0") + " m/s"));

            // **预热组件引用缓存**（2026-09-27）：`GameComponentRef` 的**第一次**实查必然发生一次
            // `GetComponent`（组件表线性扫描）——那是"缓存"的定义，不是相位里的违规。而它**恰好**
            // 发生在场景首跑的第一步 ⇒ G1/R5 判据（要求增量 = 0）**每个会话的首跑都假红**
            // （实测 29 / 39 / 52 次，之后逐跑为 0）。
            // ⇒ 在主线程、**进并行区之前**把它们先取一次（此时不在相位里 ⇒ 守卫不计数）⇒
            //   首跑就干净，而"场景期间真的在相位里查组件"仍然会被判红（判据没有被削弱）。
            if (CelestialBodyManager.GetCached() == null || SpaceGameComp.GetCached() == null)
            {
                PflTestKit.Unchecked("前置", "组件缓存预热失败（SpaceGameComp / CelestialBodyManager 取不到）");
                return;
            }

            missBaseline = CelestialBodyManager.DebugSystemMembersMisses;
            inRegionBaseline = CelestialBodyManager.DebugParallelRegionCacheWrites;
            sliceChecksBaseline = PerfProbe.SliceBufferChecks;
            sharedWriteBaseline = ParallelRegionGuard.SharedWrites;
            globalLookupBaseline = ParallelRegionGuard.GlobalLookups;
            ParallelRegionGuard.ResetSiteTally();   // 分档只统计本场景（否则会把玩家自己那几百步的写混进来）
            PflTestKit.SnapshotGroups();   // 清场口径：只销毁本次新建的组
            spawned.Clear();
            runIndex = 0;
            stage = Stage.Advance;
            SpawnRun(sg, mgr, tsm);
        }

        /// <summary>中止：当前用例记 UNCOVERED，并清掉本次铺的场。</summary>
        public static void Abort()
        {
            if (!Running) return;
            PflTestKit.CleanupSince();
            PflTestKit.Abort();
            stage = Stage.Idle;
            spawned.Clear();
        }

        /// <summary>由 `SpaceGameComp.GameComponentTick` 驱动（空闲时第一行返回，零开销）。</summary>
        public static void Tick()
        {
            if (stage == Stage.Idle) return;

            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var tsm = Current.Game?.GetComponent<TimeScaleManager>();
            if (sg == null || tsm == null) { Abort(); return; }

            switch (stage)
            {
                case Stage.Advance:
                {
                    double pt = tsm.GetPhysicsTick();
                    if (pt != lastPhysicsTick)
                    {
                        lastPhysicsTick = pt;
                        tickSpanThisRun++;
                        // 逐步指纹：在计数之后取 ⇒ **样本号 == tick 跨度**（跨轮同一语义）
                        TakeStepFingerprint(sg, tickSpanThisRun);
                    }
                    if (tickSpanThisRun < (variant == Variant.ScaleSweep ? SweepSteps : StepsPerRun)) return;

                    // ── N 扫描档：取这一档的读数、清场、换档 ──
                    if (variant == Variant.ScaleSweep)
                    {
                        TakeSweepReading();
                        PflTestKit.CleanupSince();
                        spawned.Clear();
                        sweepIndex++;
                        if (sweepIndex < SweepNs.Length)
                        {
                            var mgr0 = Current.Game?.GetComponent<CelestialBodyManager>();
                            if (mgr0 == null) { Abort(); return; }
                            stage = Stage.Advance;
                            SpawnRun(sg, mgr0, tsm);
                        }
                        else
                        {
                            stage = Stage.Done;
                            DeliberateSweep();
                        }
                        return;
                    }

                    // ⭐ **在越界当拍**取指纹（不留到下一拍 —— 那会让轮周期变成奇数 tick，
                    //    进而在"每 2 tick 一物理步"的节拍上造成交替轮次差一步，伪装成不可复现）
                    TakeFingerprint(sg, pt, tickSpanThisRun);
                    PflTestKit.CleanupSince();
                    spawned.Clear();
                    runIndex++;
                    if (runIndex < RunCount)
                    {
                        var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
                        if (mgr == null) { Abort(); return; }
                        stage = Stage.Advance;
                        SpawnRun(sg, mgr, tsm);
                    }
                    else
                    {
                        stage = Stage.Done;
                        Deliberate();
                    }
                    return;
                }
            }
        }

        // ── 铺场：**确定性**（同一套半径/相位，显式 pos/vel，无随机数）────────────────
        private static void SpawnRun(SpaceGameComp sg, CelestialBodyManager mgr, TimeScaleManager tsm)
        {
            int bodyId = ResolveRootBodyId(mgr);
            double mu = sg.GetBodyGM(bodyId);
            if (!(mu > 0.0))
            {
                PflTestKit.Unchecked("前置", string.Format("根天体 {0} 的 GM 非正 ⇒ 铺不出轨道", bodyId));
                stage = Stage.Idle;
                return;
            }

            // 本档要铺几组：正常模式 = GroupCount；N 扫描 = 当前档位。
            // ⚠ 半径/相位一律按**实际组数**算 ⇒ 各档之间只有"组数"这一个变量（否则半径间距变了，读数不可比）。
            int n = variant == Variant.ScaleSweep ? SweepNs[sweepIndex] : GroupCount;
            if (variant == Variant.ScaleSweep)
            {
                // N 扫描：**每档开工前清零常驻步钟** ⇒ 这一档的读数就是这一档自己的（含"最慢一步"）。
                PerfProbe.ResetStepClock();
                swGen0Start = System.GC.CollectionCount(0);
                if (PerfProbe.Enabled) ParallelRegionGuard.ResetSiteTally();
            }

            for (int i = 0; i < n; i++)
            {
                // 半径逐个略微错开（避免同轨重叠加），相位均布 ⇒ 全体都在赤道面内顺行
                double radius = SpaceConstants.PlanetRadiusMeters + BaseAltitude + i * 2_000.0;
                double phase = 2.0 * Math.PI * i / n;
                var radial = new DVector3(Math.Cos(phase), 0.0, Math.Sin(phase));
                // 顺行切向 = d/dφ(radial) = Cross(radial, up)（与 DebugShipSpawner.ProgradeEquatorialTangent 同式）
                var tangent = DVector3.Cross(radial, DVector3.up);
                double v = Math.Sqrt(mu / radius);

                var group = DebugShipSpawner.CreateNoMapShipGroup(sg, bodyId, radial * radius, tangent * v,
                    out int entityId, overThrust: true, circularOrbit: variant != Variant.IntegratedBurn);
                if (group == null)
                {
                    // ⚠ 已经铺出去的组必须清掉再结案（否则用例结束了、场还留在玩家世界里 = 留垃圾）
                    PflTestKit.CleanupSince();
                    spawned.Clear();
                    PflTestKit.Unchecked("铺场", string.Format("第 {0} 组创建失败（工质 Def 缺失？）", i));
                    stage = Stage.Idle;
                    return;
                }

                if (variant == Variant.IntegratedBurn)
                {
                    // ① 姿态对准顺行：主推轴 `mainAxisShip=(0,0,-1)`（工厂设的）⇒
                    //    转成"主推轴指顺行" ⇒ `AlignMainAxis` 一步就满足（dot≈1）⇒ **立刻出推力**。
                    //    ⚠ 不对准的话 `AlignMainAxis` 要转 180°（默认角速率下 ~36 s > 本窗口 30 s）
                    //      ⇒ `fTotal` 恒 0 ⇒ 燃烧"发生过但没推力" ⇒ 正对照会把它抓出来（见 Deliberate）。
                    tangent = tangent.normalized;
                    group.CurrentOrbit.rotation = Quaternion.FromToRotation(
                        new Vector3(0f, 0f, -1f), new Vector3((float)tangent.x, (float)tangent.y, (float)tangent.z));

                    // ② 点火：走得是**每步重取方向**的 RTN 退路律（`burnHasTargetV=false`）——
                    //    不造机动条目 ⇒ 不经过 `ExitCircularOrbit`（那条路的位置仍由解析圆轨给出，
                    //    烧了不动 ⇒ 不是本变体要测的东西，已在类注释里登记为 UNCHECKED）。
                    group.StartBurn(tangent * BurnDv);
                }

                spawned.Add(group.GroupId);
            }

            // ⭐ 计数基准 = **铺场当拍**的物理时刻：此后每观察到 physicsTick 变化就 +1 步，
            //    满 StepsPerRun 就在当拍取指纹 ⇒ 三轮"从铺场起恰好 30 步"是构造性成立的。
            lastPhysicsTick = tsm.GetPhysicsTick();
            startPhysicsTick[runIndex] = lastPhysicsTick;
            tickSpanThisRun = 0;
            // 第 0 号样本 = **铺场当拍**：三轮的起点。分歧若在第 0 号就出现，说明"铺场本身"不同
            // （那是比物理分歧更基础的一类问题，先排掉它，剩下的才是物理）。
            if (runIndex == 0) InitStepArrays();
            TakeStepFingerprint(sg, 0);
        }

        // ── N 扫描：取一档读数（常驻步钟 ⇒ 清零后读到的就是这一档自己的）──────────────
        private static int swGen0Start;

        private static void TakeSweepReading()
        {
            int n = SweepNs[sweepIndex];
            long steps = PerfProbe.StepClocks;
            swSteps[sweepIndex] = (int)steps;
            swGroups[sweepIndex] = n;
            swGen0[sweepIndex] = System.GC.CollectionCount(0) - swGen0Start;

            if (steps > 0)
            {
                swAvgUs[sweepIndex] = ToMicros((double)PerfProbe.StepTicksTotal / steps);
                swMaxUs[sweepIndex] = ToMicros(PerfProbe.StepTicksMax);
                swGravUs[sweepIndex] = ToMicros((double)PerfProbe.GravityTicksTotal / steps);
                swCollectUs[sweepIndex] = ToMicros((double)PerfProbe.SystemCollectTicksTotal / steps);
            }
            swPerGroupUs[sweepIndex] = n > 0 ? swAvgUs[sweepIndex] / n : 0.0;
            swGravPerGroupUs[sweepIndex] = n > 0 ? swGravUs[sweepIndex] / n : 0.0;

            Log.Message(string.Format(
                "[N扫描] N={0,-4} 步={1,-3} 平均={2,7:F1}µs/步 **每群每步={3,7:F2}µs** 最慢={4,8:F1}µs｜"
                + "引力段={5,7:F1}µs/步 每群={6,6:F2}µs｜建表段={7,6:F2}µs/步｜Gen0={8}",
                n, steps, swAvgUs[sweepIndex], swPerGroupUs[sweepIndex], swMaxUs[sweepIndex],
                swGravUs[sweepIndex], swGravPerGroupUs[sweepIndex], swCollectUs[sweepIndex], swGen0[sweepIndex]));
        }

        /// <summary>N 扫描结案：**读数为主**（不发明阈值）。每群每步的耗时若**随 N 下降或持平** ⇒ 线性/更好；
        /// 若显著**上升** ⇒ 有随 N 增长的超线性项（那才需要修，且指出修哪一段）。</summary>
        private static void DeliberateSweep()
        {
            stage = Stage.Idle;

            bool allRan = true;
            for (int i = 0; i < SweepNs.Length; i++)
            {
                bool ok = swGroups[i] == SweepNs[i] && swSteps[i] > 0;
                allRan = allRan && ok;
                PflTestKit.Check(PflTestKit.Kind.Coverage, ok,
                    string.Format("N={0} 档真的铺到并推进了（正对照）", SweepNs[i]),
                    string.Format("实铺 {0} 组 · 计时步数 {1}", swGroups[i], swSteps[i]));
            }
            if (!allRan)
            {
                PflTestKit.Unchecked("N 扫描读数", "有档位没跑成 ⇒ 斜率不可比");
                PflTestKit.End();
                return;
            }

            // ⚠ **G1 不变量也要判**（2026-09-27 补）：N≥32 的档位**走进了并行物理区**，
            //   所以这一跑同时是"相位里零共享写 / 零全局查找"的机会 ⇒ 原来漏了这两条
            //   （`DeliberateSweep` 一开始只报读数，日志里连"G1 分档"那一行都没有 ⇒ 实测发现）。
            long sharedWrites = ParallelRegionGuard.SharedWrites - sharedWriteBaseline;
            long globalLookups = ParallelRegionGuard.GlobalLookups - globalLookupBaseline;
            PflTestKit.Check(PflTestKit.Kind.Invariant, sharedWrites == 0,
                "本场景期间零并行区相位共享写（G1 判据）",
                string.Format("增量={0}（正常 0）。按站点：{1}", sharedWrites, ParallelRegionGuard.SiteReport()));
            PflTestKit.Check(PflTestKit.Kind.Invariant, globalLookups == 0,
                "本场景期间零并行区相位全局查找（G1/R5 判据）",
                string.Format("增量={0}（正常 0）", globalLookups));

            // ⭐ **判据**（2026-09-27 用户提议的 N 扫描，读数从"只报变化"升级为真判据）：
            // 计划批 3 的判据原文 = "**每群每步分配不随 N 上升**" ⇒ 就按每群每步耗时比来判。
            // ⚠ **比较 N32→N128 而不是 N8→N128**：N=8 那档被**铺场首步**污染（实测最慢步 12.2ms 全在那一步，
            //   摊到只有 8 个组上 ⇒ 每群 59µs 的离群值），那是 spawn 瞬态、不是稳态缩放 ✗。
            //   32 与 128 两档都已过瞬态，比值才说明"随 N 涨不涨"。
            // ⚠ 阈值 1.5 的依据：稳态实测比值是 **0.63（整步）/ 1.00（引力段）** ⇒ 留 50% 余量；
            //   真有超线性项（例如某个 O(N²) 混进来）会几倍地涨 ⇒ 这个界抓得住，且不会因噪声假红。
            int a = System.Array.IndexOf(SweepNs, 32), b = System.Array.IndexOf(SweepNs, 128);
            double perGroupRatio = swPerGroupUs[a] > 0.0 ? swPerGroupUs[b] / swPerGroupUs[a] : double.NaN;
            double gravPerGroupRatio = swGravPerGroupUs[a] > 0.0 ? swGravPerGroupUs[b] / swGravPerGroupUs[a] : double.NaN;
            PflTestKit.Check(PflTestKit.Kind.Value, perGroupRatio <= 1.5,
                "每群每步耗时**不随 N 上升**（N32→N128 比值 ≤ 1.5）",
                string.Format("整步 {0:F2}×（N32={1:F2}µs → N128={2:F2}µs）｜引力段 {3:F2}×（{4:F2} → {5:F2}µs）"
                    + "｜⚠ N=8 档已排除：含铺场首步（最慢步 {6:F1}µs）的瞬态",
                    perGroupRatio, swPerGroupUs[a], swPerGroupUs[b],
                    gravPerGroupRatio, swGravPerGroupUs[a], swGravPerGroupUs[b], swMaxUs[0]));

            int first = 0, last = SweepNs.Length - 1;
            double g1 = swPerGroupUs[first], g2 = swPerGroupUs[last];
            double ratio = g1 > 0.0 ? g2 / g1 : double.NaN;
            // 顺序/并行分界的直接对照：N=32 与 N=16 都够小/够大，比值说明并行区值多少
            var sb = new System.Text.StringBuilder();
            sb.Append("每群每步耗时：");
            for (int i = 0; i < SweepNs.Length; i++)
                sb.Append(string.Format("N{0}={1:F2}µs ", SweepNs[i], swPerGroupUs[i]));
            sb.Append(string.Format("｜**N{0}→N{1} 比值={2:F2}×**（≈1 或更小 = 线性或更好；显著 >1 = 有超线性项）",
                SweepNs[first], SweepNs[last], ratio));

            PflTestKit.Check(PflTestKit.Kind.Coverage, true, "读数：每群每步耗时随 N 的变化（判据等读数定）", sb.ToString());
            PflTestKit.Check(PflTestKit.Kind.Coverage, true, "读数：每群每步**引力段**随 N 的变化",
                string.Format("引力段每群：N{0}={1:F2}µs N{2}={3:F2}µs｜建表段/步：N{0}={4:F2}µs N{2}={5:F2}µs"
                    + "（S-a 的去留看这个：建表段若只占引力段个位数百分比 ⇒ 不值得动物理热路径）",
                    SweepNs[first], swGravPerGroupUs[first], SweepNs[last], swGravPerGroupUs[last],
                    swCollectUs[first], swCollectUs[last]));
            var gen0Txt = new System.Text.StringBuilder();
            for (int i = 0; i < SweepNs.Length; i++)
            {
                if (i > 0) gen0Txt.Append(" / ");
                gen0Txt.Append(SweepNs[i]).Append("组=").Append(swGen0[i]);
            }
            PflTestKit.Check(PflTestKit.Kind.Coverage, true, "读数：每档 Gen0 回收次数（分配代理）", gen0Txt.ToString());

            Log.Message("[N扫描] " + sb.ToString());
            PflTestKit.End();
        }

        /// <summary>根天体：优先问管理器（`GetPlanets()` 的唯一权威），取不到再退回 1。
        /// ⚠ **本库现有 14 份 `private const int EARTH_BODY_ID = 1` 的副本**（那是 U-7 意义上的一份权威写多遍）——
        /// 本文件**不添第 15 份**，改走管理器。</summary>
        private static int ResolveRootBodyId(CelestialBodyManager mgr)
        {
            var planets = mgr.GetPlanets();
            return planets != null && planets.Count > 0 ? planets[0].bodyId : 1;
        }

        // ── 逐步指纹（2026-09-27）──────────────────────────────────────────────
        // 只取**末步**指纹的后果实测过：变体 B 报"第 3 轮不同"，定位到 `下标 62（G255）｜pos.x`
        // 差 **1.7e-5 m**，但**不知道从哪一步开始差** ⇒ 嫌疑范围 = 整段 30 s（64 组 × 15 步的输入）。
        // ⇒ 每个"物理时刻变化点"取一次指纹（3 轮 × 30 个样本），报**第一处分歧的样本号**，
        //   并把那一步的字段留档 ⇒ 直接缩到"某一格"，那一格的输入才能全 dump。
        // 成本：30 样本 × 64 组 × 17 字段 × 3 轮 ≈ 10 万次混合 —— 与"取一次末步"同量级，可忽略。
        private const ulong FnvOffset = 14695981039346656037UL;
        private const int MaxSamples = StepsPerRun + 2;
        // 声明处就赋空数组：**nullable 分析要求**（CS8618），顺带让"未初始化也安全"成为类型层事实
        private static ulong[][] stepHash = new ulong[0][];             // [轮][样本]
        private static List<double[]>[][] stepFields = new List<double[]>[0][];  // [轮][样本] → 各组字段

        private static void InitStepArrays()
        {
            stepHash = new ulong[RunCount][];
            stepFields = new List<double[]>[RunCount][];
            for (int r = 0; r < RunCount; r++)
            {
                stepHash[r] = new ulong[MaxSamples];
                stepFields[r] = new List<double[]>[MaxSamples];
                for (int i = 0; i < MaxSamples; i++) stepFields[r][i] = new List<double[]>();
            }
        }

        /// <summary>本场景铺出去的组，**按 GroupId 排序**（序列 == 铺场顺序 ⇒ 跨轮可比）。</summary>
        private static List<SpaceEntityGroup> CollectSpawned(SpaceGameComp sg)
        {
            var list = new List<SpaceEntityGroup>();
            foreach (var g in sg.GetAllGroups())
                if (spawned.Contains(g.GroupId)) list.Add(g);
            list.Sort((a, b) => a.GroupId.CompareTo(b.GroupId));
            return list;
        }

        /// <summary>组 → 字段数组。**必须与 <see cref="FieldNames"/> 一一对应且同序**（哈希与定位共用）。</summary>
        private static double[] FieldsOf(SpaceEntityGroup g) => new double[]
        {
            g.CurrentOrbit.position.x, g.CurrentOrbit.position.y, g.CurrentOrbit.position.z,
            g.CurrentOrbit.velocity.x, g.CurrentOrbit.velocity.y, g.CurrentOrbit.velocity.z,
            g.CurrentOrbit.rotation.x, g.CurrentOrbit.rotation.y, g.CurrentOrbit.rotation.z, g.CurrentOrbit.rotation.w,
            g.circularRadius, g.circularStartAngle, g.circularAngularVelocity,
            g.remainingBurnDV,
            // 诊断字段（R3 的面：全局节流若失效 ⇒ "哪些组被诊断到"会显形）
            g.burnDiagRuns, g.burnDiagRemaining,
            g.burnLastCmdDv, g.burnDbgF, g.burnDbgMf, g.burnDbgMass,
            g.appliedBurnDV,
        };

        /// <summary>取一个"物理时刻变化点"的逐步指纹并留档字段。</summary>
        private static void TakeStepFingerprint(SpaceGameComp sg, int sample)
        {
            if (stepHash.Length < RunCount || runIndex >= RunCount || sample < 0 || sample >= MaxSamples) return;
            var list = CollectSpawned(sg);
            var stash = stepFields[runIndex][sample];
            stash.Clear();
            ulong h = FnvOffset;
            for (int i = 0; i < list.Count; i++)
            {
                double[] f = FieldsOf(list[i]);
                stash.Add(f);
                for (int k = 0; k < f.Length; k++) h = Mix(h, f[k]);
            }
            stepHash[runIndex][sample] = h;
        }

        /// <summary>**第一处分歧的样本号**（-1 = 逐样本全同）；`otherRound` 给出与它不同的那一轮。</summary>
        private static int FirstDivergingSample(out int otherRound)
        {
            otherRound = -1;
            if (stepHash.Length < RunCount) return -1;
            for (int smp = 0; smp < MaxSamples; smp++)
                for (int r = 1; r < RunCount; r++)
                    if (stepHash[r][smp] != stepHash[0][smp]) { otherRound = r; return smp; }
            return -1;
        }

        /// <summary>
        /// 在指定样本上把分歧定位到组与字段（用逐步留档，不是末步留档）。
        /// ⚠⚠ **必须列**全部**不同的列，不能只返第一处**（2026-09-27 实测踩到）：字段表里
        /// `remainingBurnDV` 排在新增的燃烧**输入**列（`burnDbgF/…Mf/…Mass`、`burnLastCmdDv`）之前，
        /// 而"只返第一处"于是一律停在 `remainingBurnDV` ⇒ **输入列永远印不出来**，而"是哪个输入差的"
        /// 正是下一步要的答案。⇒ 现在：先定位第一个不同的组，再把**该组所有不同的列**列出来（上限 6 条），
        /// 并报该样本上"有几个组不同"（区分"单组事件"与"普遍差异"）。
        /// </summary>
        private static string LocalizeAtSample(int sample, int otherRound)
        {
            if (stepHash.Length < RunCount || sample < 0 || sample >= MaxSamples) return "样本号越界";
            var a = stepFields[0][sample];
            var b = stepFields[otherRound][sample];
            int n = System.Math.Min(a.Count, b.Count);

            int diffGroups = 0, firstGroup = -1;
            for (int i = 0; i < n; i++)
            {
                if (FieldsEqual(a[i], b[i])) continue;
                diffGroups++;
                if (firstGroup < 0) firstGroup = i;
            }
            if (firstGroup < 0)
                return string.Format("样本 {0} 哈希不同但逐字段全同 ⇒ 差异在字段表之外（组数 {1} vs {2}？）",
                    sample, a.Count, b.Count);

            var sb = new System.Text.StringBuilder();
            sb.Append("样本 ").Append(sample).Append("：**").Append(diffGroups).Append(" 个组不同**（首个 下标 ")
              .Append(firstGroup).Append("）｜该组**全部**不同列：");
            int shown = 0;
            var fa = a[firstGroup];
            var fb = b[firstGroup];
            int len = System.Math.Min(fa.Length, fb.Length);
            for (int k = 0; k < len && shown < 6; k++)
            {
                if (fa[k] == fb[k]) continue;
                if (shown > 0) sb.Append('、');
                sb.Append(FieldNames[k]).Append(" R1=").Append(fa[k].ToString("R"))
                  .Append(" vs R").Append(otherRound + 1).Append('=').Append(fb[k].ToString("R"))
                  .Append("（Δ=").Append((fb[k] - fa[k]).ToString("E3")).Append('）');
                shown++;
            }
            return sb.ToString();
        }

        /// <summary>两组的字段数组是否逐位相同（NaN != NaN 也判为不同，正是我们要的）。</summary>
        private static bool FieldsEqual(double[] x, double[] y)
        {
            if (x.Length != y.Length) return false;
            for (int k = 0; k < x.Length; k++)
                if (x[k] != y[k]) return false;
            return true;
        }

        // ── 指纹 ────────────────────────────────────────────────────────────────
        private static void TakeFingerprint(SpaceGameComp sg, double physicsTickAtEnd, int steps)
        {
            var list = new List<SpaceEntityGroup>();
            int movable = 0;
            foreach (var g in sg.GetAllGroups())
            {
                if (g.IsMovable) movable++;
                if (spawned.Contains(g.GroupId)) list.Add(g);
            }
            list.Sort((a, b) => a.GroupId.CompareTo(b.GroupId));   // 序列 == 铺场顺序 ⇒ 跨轮可比

            // 末步指纹：**留档**（哈希 + 逐字段数组）用于定位；字段表与逐步指纹**共用 `FieldsOf`**
            ulong h = FnvOffset;
            for (int i = 0; i < list.Count; i++)
            {
                roundIds[runIndex].Add(list[i].GroupId);
                double[] f = FieldsOf(list[i]);
                roundFields[runIndex].Add(f);
                for (int k = 0; k < f.Length; k++) h = Mix(h, f[k]);
            }

            physicsHash[runIndex] = h;
            countedGroups[runIndex] = list.Count;
            movableGroups[runIndex] = movable;
            tickSpanPerRound[runIndex] = steps;
            endPhysicsTick[runIndex] = physicsTickAtEnd;
            var tsm = Current.Game?.GetComponent<TimeScaleManager>();
            timeScale[runIndex] = tsm != null ? (double)tsm.PhysicsSpeedMultiplier : 1.0;
        }


        /// <summary>
        /// **最坏越界倍数**（判据用它，而不是"第一处不同"）：逐轮对 1↔r 逐组逐列算
        /// `|a-b| / (atol[k] + rtol·max(|a|,|b|))`，取最大（>1 即越界）。
        /// 计数列不等直接记 <see cref="double.PositiveInfinity"/>。
        ///
        /// ⚠⚠ **为什么不能只用相对容差**（2026-09-27 实测踩到）：纯相对式在**近零字段**上会爆 ——
        /// 实测报"最大相对偏差 1.38e-6"判 FAIL，而真正关心的位置只差 **1.3e-11**（早就合格）✗
        /// ⇒ 那一格是某个**本该接近 0 的列**（赤道面内 pos.y/vel.y、姿态分量、收尾时的 Δv…）贡献的。
        /// ⇒ 采用**绝对地板 + 相对**的混合式（`atol` 逐列给，见 <see cref="FieldAtol"/>）：
        ///   大值走相对、小值走地板 ⇒ 判据只对**物理上看得见的差**红。
        /// </summary>
        private static double WorstToleranceViolation(out int round, out int idx, out int col, out bool countMismatch)
        {
            double worst = 0.0;
            round = -1; idx = -1; col = -1; countMismatch = false;
            int n = roundFields[0].Count;
            for (int r = 1; r < RunCount; r++) n = System.Math.Min(n, roundFields[r].Count);
            for (int r = 1; r < RunCount; r++)
            {
                if (roundFields[r].Count != roundFields[0].Count) { countMismatch = true; }
                for (int i = 0; i < n; i++)
                {
                    var a = roundFields[0][i];
                    var b = roundFields[r][i];
                    int len = System.Math.Min(a.Length, b.Length);
                    for (int k = 0; k < len; k++)
                    {
                        double d;
                        if (FieldExact[k])
                            d = a[k] == b[k] ? 0.0 : double.PositiveInfinity;
                        else
                        {
                            double bound = FieldAtol[k] + FingerprintRelTol * System.Math.Max(System.Math.Abs(a[k]), System.Math.Abs(b[k]));
                            d = System.Math.Abs(a[k] - b[k]) / bound;
                        }
                        if (d > worst) { worst = d; round = r; idx = i; col = k; }
                    }
                }
            }
            return worst;
        }
        /// <summary>
        /// 把"指纹不同"**定位到具体的组与字段**：按铺场顺序逐组比 1↔2、1↔3，
        /// 报出第一个不同的下标、组号、以及两轮的 pos/vel 分量。找不到就明说"没定位到"
        /// （那说明差异在**没被留档的字段**上 —— 本身也是有用信息，不许含糊过去）。
        /// </summary>
        private static string LocalizeFirstDiff()
        {
            int n = roundFields[0].Count;
            for (int r = 1; r < RunCount; r++) n = Math.Min(n, roundFields[r].Count);
            for (int r = 1; r < RunCount; r++)
            {
                for (int i = 0; i < n; i++)
                {
                    var a = roundFields[0][i];
                    var b = roundFields[r][i];
                    int len = Math.Min(a.Length, b.Length);
                    for (int k = 0; k < len; k++)
                    {
                        if (a[k] == b[k]) continue;   // 逐**位**比（NaN != NaN 也会被判为不同，正好）
                        return string.Format(
                            "首个不同：下标 {0}（G{1}）｜字段 {2}｜R1={3:R} vs R{4}={5:R}",
                            i, roundIds[0][i], FieldNames[k], a[k], r + 1, b[k]);
                    }
                }
            }
            return string.Format("指纹不同但**字段表逐位全同**（{0} 组 × {1} 字段）⇒ 差异在字段表之外（哈希本身或计数）",
                n, FieldNames.Length);
        }

        /// <summary>把一个 double 的**全部 64 位**拌进哈希（逐位可比，NaN 也稳定）。</summary>
        private static ulong Mix(ulong h, double v)
        {
            long bits = BitConverter.DoubleToInt64Bits(v);
            for (int b = 0; b < 8; b++)
            {
                h ^= (byte)(bits >> (b * 8));
                h *= 1099511628211UL;
            }
            return h;
        }

        // ── 判定 ────────────────────────────────────────────────────────────────
        private static void Deliberate()
        {
            stage = Stage.Idle;

            // ① 反空样本：**先证明真的跑了**（否则"三次相同"是平凡成立 —— 本库踩过"0 样本报 PASS"）
            bool nonEmpty = countedGroups[0] > 0 && physicsHash[0] != 14695981039346656037UL;
            PflTestKit.Check(PflTestKit.Kind.Coverage, nonEmpty, "样本非空（真的铺到并推进了组）",
                string.Format("三轮计数 {0}/{1}/{2}", countedGroups[0], countedGroups[1], countedGroups[2]));

            // ② 并行分支的前提：可动组数 ≥ 下限（与 SpaceGameComp 用**同一个常量**，不抄数）
            bool covers = movableGroups[0] >= SpaceGameComp.PARALLEL_PHYSICS_MIN_GROUPS;
            PflTestKit.Check(PflTestKit.Kind.Coverage, covers, "可动组数 ≥ 并行下限（并行分支必然被走到）",
                string.Format("可动 {0} / 下限 {1}（三轮 {2}/{3}/{4}）",
                    movableGroups[0], SpaceGameComp.PARALLEL_PHYSICS_MIN_GROUPS,
                    movableGroups[0], movableGroups[1], movableGroups[2]));

            // ③ 时间档必须三轮一致，否则跨轮比较**无效**（不是失败，是无法断言）
            if (timeScale[0] != timeScale[1] || timeScale[1] != timeScale[2])
            {
                PflTestKit.Unchecked("时间档一致", string.Format(
                    "三轮时间档不同（{0}/{1}/{2}）⇒ 物理步长不同，跨轮指纹不可比；请在 1x 档下重跑",
                    timeScale[0], timeScale[1], timeScale[2]));
                return;
            }

            // ④ **步数必须逐轮相同**（否则"指纹不同"可能只是走的路不一样 —— 仪器自检；
            //     2026-09-26 第一跑就是因为轮周期是奇数 tick 而在这一条上栽的）
            bool sameSteps = tickSpanPerRound[0] == tickSpanPerRound[1] && tickSpanPerRound[1] == tickSpanPerRound[2];
            PflTestKit.Check(PflTestKit.Kind.Invariant, sameSteps, "三轮物理时刻跨度相同 ⇒ 物理步数相同（仪器自检）",
                string.Format("{0}/{1}/{2}｜铺场物理时刻 {3:F1}/{4:F1}/{5:F1}｜取指纹物理时刻 {6:F1}/{7:F1}/{8:F1}",
                    tickSpanPerRound[0], tickSpanPerRound[1], tickSpanPerRound[2],
                    startPhysicsTick[0], startPhysicsTick[1], startPhysicsTick[2],
                    endPhysicsTick[0], endPhysicsTick[1], endPhysicsTick[2]));

            // ④b **R1 的直接判据：零"工作线程写共享字典"**。
            //     为什么需要直接判据：指纹（症状）对 R1 只是**偶尔**抓得到（只有被丢结果的那一步正好落在指纹窗口里才显形）
            //     —— 2026-09-26 实测"日志 277 次 IndexOutOfRange、而三次指纹相同"。⇒ 症状判据不许单独用。
            //     ⚠ 未命中数**不作判据**：预暖自己第一次取就是要建表（合法），所以它只是读数。
            long inRegionWrites = CelestialBodyManager.DebugParallelRegionCacheWrites - inRegionBaseline;
            PflTestKit.Check(PflTestKit.Kind.Invariant, inRegionWrites == 0,
                "本场景期间零并行区内建表（R1 直接判据）",
                string.Format("并行区相位里建表次数={0}（正常 0；>0 即并行区内的共享 Dictionary 写）", inRegionWrites));

            long missDelta = CelestialBodyManager.DebugSystemMembersMisses - missBaseline;
            PflTestKit.Check(PflTestKit.Kind.Coverage, true, "读数：成员表未命中增量",
                string.Format("{0}（含预暖那一次，合法；应为 1）", missDelta));

            // ④b′ **G1 判据：并行区相位里零共享写 + 零全局查找**（增量口径 ⇒ 点过守卫自检也不污染读数）。
            //     覆盖 R2/R3/R5/R7/R8 那一整类；**R4（8 处日志）不在覆盖面内**（`Verse.Log` 是静态类，
            //     没有可挂的钩子）⇒ 那一条判据是静态的（并行体区间里 grep 不到 `Log.`），见类注释。
            //     ⚠ `PerfProbe` 内部那几处（R2/R7/R8）**只在探针开关打开时才执行** ⇒ 本判据要覆盖它们，
            //       跑之前必须勾上「性能优化验收探针」；R3/R5 那几处**不依赖开关**，任何会话里都算数。
            long sharedWrites = ParallelRegionGuard.SharedWrites - sharedWriteBaseline;
            long globalLookups = ParallelRegionGuard.GlobalLookups - globalLookupBaseline;
            // ⚠ 明细里带上**按站点的分档**：只报总数等于"有条漏了"，说不出**是哪几条** ⇒ 修的时候还得再查一遍
            //   （2026-09-26 实测：变体 B 报 8484 次写，而它由 SliceBuffer + SliceAlias + 引力探针三处构成，
            //    总数分不开它们）。分档名册只统计**非零**项，稳态（全 0）时那一格是空的。
            string sharedSiteTally = ParallelRegionGuard.SiteReport();
            // ⚠ 无条件直打日志：挂在判据 detail 上的话，**判据通过时**它落在框架"通过判据读数最多 240 字"
            //   的截断区里 ⇒ 读数=0 的场景反而看不见分档（而"哪几条被搬干净了"正是要留的证据）。
            Log.Message(string.Format("[规模验收] G1 分档（本场景）：共享写增量={0}｜全局查找增量={1}｜按站点：{2}",
                sharedWrites, globalLookups, sharedSiteTally));
            PflTestKit.Check(PflTestKit.Kind.Invariant, sharedWrites == 0,
                "本场景期间零并行区相位共享写（G1 判据；含 R2/R3/R7/R8）",
                string.Format("增量={0}（正常 0）。**按站点**：{1}"
                    + "⚠ 覆盖 PerfProbe 那几处需要勾上「性能优化验收探针」", sharedWrites, sharedSiteTally));
            PflTestKit.Check(PflTestKit.Kind.Invariant, globalLookups == 0,
                "本场景期间零并行区相位全局查找（G1/R5 判据）",
                string.Format("增量={0}（正常 0；>0 即相位里还有 GetComponent/Find.Maps 没收到形参）", globalLookups));

            // ④c **正对照**（变体 B）：证明"燃烧路径真的被走到"，否则下面的指纹判据都是**空样本**上的平凡成立。
            //     形态照框架的 0 样本纪律：**拿不到证据 ⇒ UNCHECKED + 不再宣称 PASS**（不是"记一条 coverage 接着走"
            //     —— `Kind.Coverage` 的失败**不进 `HasFailure`**，那样写等于正对照不设防）。
            //     `SliceBufferChecks` 是 PerfProbe 既有的读数，语义就是"燃烧路径走过的证据"（复用，不新造）。
            long sliceChecks = PerfProbe.SliceBufferChecks - sliceChecksBaseline;
            int burnedGroups = 0, thrustGroups = 0;
            for (int i = 0; i < roundFields[RunCount - 1].Count; i++)
            {
                var f = roundFields[RunCount - 1][i];
                if (f[14] > 0.0) burnedGroups++;    // burnDiagRuns>0 = 这一组被诊断到过（R3 的判据面）
                if (f[16] > 0.0) thrustGroups++;    // appliedBurnDV（真的出了推力）
            }
            if (variant == Variant.IntegratedBurn)
            {
                if (burnedGroups == 0 || sliceChecks == 0)
                {
                    PflTestKit.Unchecked("正对照（燃烧路径）", string.Format(
                        "诊断块跑过的组 {0}/{1}、切片检查增量 {2} —— 有一个为 0 就说明" +
                        "**燃烧路径没被走到**，本变体的指纹判据在空样本上成立，不作数",
                        burnedGroups, GroupCount, sliceChecks));
                    return;
                }
                // ⭐ **升级成 R3 的直接判据（2026-09-26）**：改前它只能报"路径被走到"（`> 0`），
                //   而实测"64 个组每窗口只诊断到 1 个"也能满足 `> 0` ⇒ **判据太弱，放过了 98% 的丢失**。
                //   现在要求**每个燃烧组都被诊断到过**（改后应恰好 = GroupCount；改前实测 = 3）。
                PflTestKit.Check(PflTestKit.Kind.Invariant, burnedGroups >= GroupCount,
                    "每个燃烧组都被诊断到过（R3 直接判据：诊断不该只轮到迭代序第一个组）",
                    string.Format("被诊断到的组 {0}/{1}｜切片检查增量 {2}（改前实测 3/{1} —— 全局静态节流把其余 61 个静默跳过）",
                        burnedGroups, GroupCount, sliceChecks));
                // 推力是**另一件事**：没对准时燃烧"发生了但没推力" ⇒ 判据面只覆盖到"方向刷新/记账"，
                // 没覆盖到"冲量施加"⇒ 不足时**同样不许宣称 PASS**（但已走到的部分如实登记为读数）。
                if (thrustGroups == 0)
                {
                    PflTestKit.Unchecked("正对照（真推力）", string.Format(
                        "末轮 appliedBurnDV>0 的组 0/{0} ⇒ 燃烧空转（多半是主推轴没对准顺行）" +
                        "⇒ 冲量施加侧的判据面未覆盖", GroupCount));
                    return;
                }
                PflTestKit.Check(PflTestKit.Kind.Coverage, true, "正对照：燃烧真的出了推力",
                    string.Format("末轮 appliedBurnDV>0 的组 {0}/{1}", thrustGroups, GroupCount));
            }
            else
            {
                PflTestKit.CheckUnchecked("燃烧诊断路径（R3/R4/R7 的判据面）",
                    "变体 A 是解析圆轨、不燃烧 ⇒ 请另跑「规模验收B · 积分+全群燃烧」");
            }

            // ⑤ 首要验收：**三次运行物理指纹等价**（**绝对地板 + 相对**的混合容差，见 `FieldAtol` 的论证）
            bool stable = physicsHash[0] == physicsHash[1] && physicsHash[1] == physicsHash[2];
            int wRound, wIdx, wCol; bool countMismatch;
            double violation = WorstToleranceViolation(out wRound, out wIdx, out wCol, out countMismatch);
            bool equivalent = !countMismatch && violation <= 1.0;
            lastRunRed = !equivalent;   // 猎捕模式据此决定还跑不跑（现在是"真差异"才停）
            string worstTxt = double.IsPositiveInfinity(violation)
                ? "∞（计数列不等或组数不同）"
                : string.Format("{0:E3}× 于 {1}（下标 {2}，R1 vs R{3}）",
                    violation, FieldNames[wCol >= 0 ? wCol : 0], wIdx, wRound + 1);
            PflTestKit.Check(PflTestKit.Kind.Invariant, equivalent,
                "三次运行物理指纹等价（逐列绝对地板 + 相对 1e-9）",
                string.Format("{0:X16} / {1:X16} / {2:X16}｜时间档 {3}｜**最坏越界倍数 {4}**（≤1 = 合格）{5}",
                    physicsHash[0], physicsHash[1], physicsHash[2], timeScale[0],
                    worstTxt, countMismatch ? "｜**组数逐轮不同**（那是真差异）" : ""));
            // **逐位相同降为登记读数**（不静默通过）：ULP 级差异是"浮点非结合性"的必然产物，
            // 记下来（带定位），但不判失败 —— 判失败会让判据每隔几轮就红一次，而每次红都要人来分辨。
            PflTestKit.Check(PflTestKit.Kind.Coverage, true, "读数：三轮是否逐位相同",
                stable ? "逐位相同"
                       : string.Format("有 ULP 级差异（登记，不判失败）：最坏越界倍数 {0:E3}×｜{1}",
                           violation, LocalizeFirstDiff()));
            // ⚠ **定位必须单独打日志**（2026-09-26 实测才发现）：框架的用例结算行**只印前 2 条失败明细**
            //   （`PflTestKit` 里那条 `if (bad > 2) continue`），而指纹判据排在后面 ⇒ 指纹一红就只能知道
            //   "红了"、**不知道是哪个字段**（第一轮为此白花了一整轮）。⇒ 定位直接进日志，不吃那个上限。
            if (!stable)
            {
                Log.Message("[规模验收] **指纹定位（末步）**：" + LocalizeFirstDiff());

                int otherRound;
                int firstSample = FirstDivergingSample(out otherRound);
                if (firstSample < 0)
                {
                    Log.Message("[规模验收] **逐步定位**：末步三阶段不同，但**逐步指纹逐样本全同**"
                        + " ⇒ 分歧出现在**最后一次取样之后**（末步内 / 取指纹之间）——这本身就把范围缩到最后一步内部");
                }
                else
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("[规模验收] **逐步定位**：**第 ").Append(firstSample)
                      .Append(" 个物理时刻变化点**起分歧（R1 vs R").Append(otherRound + 1).Append("）｜")
                      .Append(LocalizeAtSample(firstSample, otherRound));
                    // 分歧点**前后各一个样本**的哈希：一次性区分"一次性抖动"与"从此持续偏"
                    sb.Append("｜哈希序列：");
                    int lo = firstSample > 0 ? firstSample - 1 : 0;
                    int hi = firstSample + 1 < MaxSamples ? firstSample + 1 : firstSample;
                    for (int smp = lo; smp <= hi; smp++)
                    {
                        sb.Append("样本").Append(smp).Append("[");
                        for (int r = 0; r < RunCount; r++)
                        {
                            if (r > 0) sb.Append(' ');
                            sb.Append(stepHash[r][smp].ToString("X16"));  // 全串（比较等同性用；3 样本 × 3 轮共 ~150 字符）
                        }
                        sb.Append("]");
                    }
                    Log.Message(sb.ToString());
                }
            }

            // ⑤ 覆盖度小结 + **未覆盖项显式登记**（不许用沉默冒充覆盖）
            PflTestKit.Check(PflTestKit.Kind.Coverage, true, "覆盖：每群每步的并行区 + R5（每群每步的 GetBodyOffsetSystem）");
            PflTestKit.Check(PflTestKit.Kind.Coverage, true, "读数：每轮步数/组数",
                string.Format("tick 跨度 {0}/{1}/{2}｜组 {3}/{4}/{5}",
                    tickSpanPerRound[0], tickSpanPerRound[1], tickSpanPerRound[2],
                    countedGroups[0], countedGroups[1], countedGroups[2]));
            PflTestKit.CheckUnchecked("冷缓存窗口（R1 的竞争面）",
                "`EnsureInitialized` 只在读档/初始化时清缓存 ⇒ 该窗口只在读档后首步出现，三次运行都命中热缓存。批 2 需要时补「清缓存后铺场」的变体");
            PflTestKit.CheckUnchecked("绕月（非根天体）路径",
                "本场景只在地球系铺场；`GetBodyPositionParent` 那条路（R1 的另一半）未覆盖");
            PflTestKit.CheckUnchecked("解析圆轨 + 直接 StartBurn（机动执行器那条路）",
                "本变体用非圆轨模式 ⇒ 不经过 `ExitCircularOrbit`；解析圆轨下位置由 tick 解析给出，烧了不动 ⇒ 那是另一条路（真实机动走它）");

            PflTestKit.End();

            // 连跑 / 猎捕：本跑结算完 ⇒ 立刻再铺一次（每跑独立结算 ⇒ 判据语义不变）
            bool wantMore;
            if (huntLeft > 0)
            {
                if (lastRunRed)
                {
                    huntLeft = 0;
                    wantMore = false;
                    Log.Message("[规模验收] **猎捕结束：抓到红**（上一跑即答案，逐步定位在上方）");
                }
                else { huntLeft--; wantMore = true; }
            }
            else if (repeatsLeft > 0) { repeatsLeft--; wantMore = true; }
            else { wantMore = false; runSerial = 0; }

            if (wantMore)
            {
                var sg2 = Current.Game?.GetComponent<SpaceGameComp>();
                var mgr2 = Current.Game?.GetComponent<CelestialBodyManager>();
                var tsm2 = Current.Game?.GetComponent<TimeScaleManager>();
                if (sg2 != null && mgr2 != null && tsm2 != null)
                {
                    Log.Message(string.Format("[规模验收] 继续跑：猎捕剩 {0} 次、连跑剩 {1} 次",
                        huntLeft, repeatsLeft));
                    Start(variant);
                }
                else { huntLeft = 0; repeatsLeft = 0; }
            }
        }
    }
}
