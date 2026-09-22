using System;

namespace PioneerFramework
{
    /// <summary>
    /// **烧执行器的政策（纯）** —— B5 的第一份交付物（2026-09-23）。
    ///
    /// ## 它补的是什么
    /// 交接 §B5：**"所有'治了又回'的坑，根因都是台架没建游戏侧的执行器政策"**。
    /// 台架的模型比真代码"干净" ⇒ 台架上收敛、游戏里不收敛，而我每次只能靠看日志猜。
    /// 四项最要命的政策里：
    /// · ① **原子烧** —— 一次烧一旦开始，`HasActiveBurn` 期间拒发新烧（新机动只能排队等它结束）；
    /// · ② **三个 dt** —— 物理步 / 控制周期 / 重规划周期**是三个不同的数**（本类只吃"物理步"这一个入参；
    ///   另两个由调用方持有，**本类不复制它们的值**，免得又多一份口径）；
    /// · ③ **一步延迟** —— 本步算出的冲量在**下一物理步首**才并入速度（`pendingImpulse`）；
    /// · ④ **执行器选择** —— 小 Δv 走 RCS（全矢量、免对准），大 Δv 走主推（要对准）。
    /// 逐条算式里**能复用 <see cref="BurnStep" /> 的全部复用**（单步数学 / 收手判据 / 节点窗口都在那边，
    /// 本类不重写一条）。
    ///
    /// ## ⚠ 已登记的三份"小 Δv 走 RCS"口径（本类是要收成的那一份）
    /// 1. `SpaceGameComp.cs:3606`（引导修正烧的分发，Map 船路径）；
    /// 2. `SpaceGameComp.cs:1700-1708`（无 Map 执行器内部的选网）；
    /// 3. `RendezvousGuidanceCore.SelectActuator`（**只有台架在用**，游戏侧零调用者）。
    /// ⇒ 收敛动作：三处改调 <see cref="SelectActuator" />；**本轮只落纯层这一份**，游戏侧改动登记待办
    /// （动它等于动活路径，要有把握再动）。
    ///
    /// ## ⚠ 本条与"实交/指令 = 0.481"的关系（交接里登记"纯核复现失败"的那条）
    /// 该读数的**定义**是：一个窗口内 **实测**施加的 Δv（状态差 − 纯引力预测）÷ 同窗口内**记账**扣掉的 Δv
    /// （`SpaceGameComp.cs:1372-1377`）。两者可以不相等，来路只有三条：
    /// ① 冲量在**下一步首**才并入（一拍错位）；② 记账按"计划/请求"而物理按"**可用**"（换网、未对准）；
    /// ③ 窗口边界把某一步的冲量切在外面（燃烧收尾那一窗必然 < 1）。
    /// 三条都不是"物理错"，而是**时间基与选网** —— 也就是本类建模的东西。
    /// </summary>
    public static class BurnExecutorPolicy
    {
        /// <summary>
        /// "小 Δv 走 RCS"的窗口（秒）：`dv ≤ aRcs × 本值` ⇒ RCS。
        /// **唯一常量**（此前 15.0 在游戏两处 + 纯核一处各写一遍）。
        /// 取值的理由（游戏注释原文）：RCS 推力小，大修正会拖长引导冻结 ⇒ 修正过期 ⇒ 死循环。
        /// </summary>
        public const double RcsFineDvWindowSeconds = 15.0;

        // 三态版的选择（None/Main/Rcs）在 `RendezvousGuidanceCore.SelectActuator`，它**转发**到下面这个布尔判决；
        // ⚠ 本类**不另立枚举**（本库 J3：不许影子类型）—— 游戏那两处内联规则要的本来就是布尔。

        /// <summary>
        /// ④ **是否走 RCS**（唯一算式）。语义与 `RendezvousGuidanceCore.SelectActuator` 逐条一致：
        /// 小 Δv 优先 RCS → 大 Δv 且主推已对准走主网 → 主推未对准/主网尽则 RCS 兜底。
        /// </summary>
        /// <param name="dvMagnitude">本次机动的 |Δv|（m/s）</param>
        /// <param name="aMain">主网全油门加速度（m/s²，0 = 无主网）</param>
        /// <param name="aRcs">RCS 加速度（m/s²，0 = 无 RCS）</param>
        /// <param name="mainAligned">主推轴是否已对准（未对准时主网本步不出力）</param>
        public static bool UseRcs(double dvMagnitude, double aMain, double aRcs, bool mainAligned,
            double windowSeconds = RcsFineDvWindowSeconds)
        {
            bool hasMain = aMain > 0.0;
            bool hasRcs = aRcs > 0.0;
            if (!hasRcs) return false;                                  // 只剩主网（哪怕未对准：等姿态）
            if (dvMagnitude <= aRcs * windowSeconds) return true;       // 小修正优先 RCS：全矢量、免对准
            if (hasMain && mainAligned) return false;                   // 大 Δv：主网（要求对准）
            return true;                                                // 主推未对准 / 主网尽：RCS 兜底
        }

        /// <summary>有没有任何可用推进（主网或 RCS）。</summary>
        public static bool HasAnyActuator(double aMain, double aRcs) => aMain > 0.0 || aRcs > 0.0;

        /// <summary>
        /// ① **原子烧门**：正在烧 ⇒ 新的机动**不许开烧**（只能排队、等这一发烧完）。
        /// 游戏侧的三个拒绝点：队列循环 `SpaceGameComp.cs:2501`、新律 `:3635`、引导 `:3201`。
        /// ⚠ 它**只**拒绝"开新烧"；同一发烧内部的每拍更新（需求推油门）不受影响。
        /// </summary>
        public static bool CanStartNewBurn(bool hasActiveBurn) => !hasActiveBurn;

        /// <summary>燃时（s）= `Δv / 加速度`；加速度 ≤0 ⇒ 无穷（"烧不完"，不是 0）。</summary>
        public static double BurnSeconds(double dv, double accel)
            => accel > 0.0 ? Math.Max(dv, 0.0) / accel : double.PositiveInfinity;

        /// <summary>
        /// ★ **计划所用执行器 ≠ 实际所用执行器**的量化（B5 第一份交付物的核心）：
        /// 规划器按**主网**估燃时（`BurnCalculator.Estimate` 走全网络），而执行器在小 Δv 时**选 RCS**
        /// ⇒ 燃时差 `aMain/aRcs` 倍。游戏的实测样本：计划 0.8 s、实烧 14 s（**17.5 倍**）。
        /// </summary>
        public static double PlannedOverActualBurnSeconds(double dv, double aPlanned, double aActual)
        {
            double planned = BurnSeconds(dv, aPlanned);
            double actual = BurnSeconds(dv, aActual);
            if (!(planned > 0.0) || double.IsInfinity(actual)) return double.PositiveInfinity;
            return actual / planned;   // = aPlanned / aActual
        }

        /// <summary>
        /// ★★ **级联的量级**（为什么燃时估错会把整条时间表弄垮）：点火是**居中**的
        /// （`t_ign = UT − T_est/2`），所以实际燃时 T_real 的收尾落在 `UT + (T_real − T_est)/2`。
        /// ⇒ 燃时差 ΔT 只会让**收尾**偏离节点 ΔT/2，但下一发节点若排在 `UT + <ΔT/2` 之内，
        /// 它的点火时刻就已经在**过去**了 ⇒ "每一发都晚 ⇒ 下一发落进过去"的级联。
        /// 实测样本：T_est 0.8 s、T_real 14 s ⇒ 收尾偏离节点 **+6.6 s**。
        /// </summary>
        public static double BurnEndOffsetFromNode(double actualSeconds, double estimatedSeconds)
            => (actualSeconds - estimatedSeconds) / 2.0;

        /// <summary>
        /// `实交/指令` 的**读数口径**（与游戏 `SpaceGameComp.cs:1372-1377` 同一个定义）：
        /// 分子 = 窗口内**实测**施加的 Δv（状态差 − 纯引力预测），分母 = 同窗口内**记账**扣掉的 Δv。
        /// ⚠ 读它时必须同时看**窗口里有没有燃烧**：收尾那一窗、以及无燃烧的窗，比值没有意义
        /// （本库"读数必须无条件、且必须能自证语境"的纪律）。
        /// </summary>
        public static double DeliveredOverCommanded(double appliedDvMagnitude, double commandedDv)
            => commandedDv > 1e-9 ? appliedDvMagnitude / commandedDv : double.NaN;

        // ══════════════════════════════════════════════════════════════════════════════
        //  ④ **队列整体**（B5-3，2026-09-23）—— 之前台架上"队列整体不存在"，只有
        //  `LadderVsLawComparison.LadderPilot` 一个近似物（缺原子烧 / 三个 dt / 一步延迟）。
        //  这里把**队列消费循环里那几条判据**收成一份；队列的状态本体仍在
        //  `SpaceEntityGroup.plannedManeuvers`（本类**不另立队列类型** —— J3 影子类型禁令）。
        // ══════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// **队列非空 ⇒ 引导让位**（游戏 `SpaceGameComp.cs:3204`：`if (group.plannedManeuvers.Count > 0) continue;`）。
        /// ⚠ 比纯规则严：`PlanOwnerRules.GuidanceMustYieldTo` 只让 Manual，而这里**任何**条目都让
        /// （阶梯排的节点也算）—— 台架照这条走，否则会出现"两套机制交替上场"（实测：日志交替出现
        /// `[阶梯] … 排入 2 发` 与 `[Guidance] … correction burn started (new-law)`）。
        /// </summary>
        public static bool GuidanceYieldsToQueue(int queueCount) => queueCount > 0;

        /// <summary>
        /// **预点火窗口**（游戏 `SpaceGameComp.cs:2607`）：进入节点前 `lookahead` 秒才开始处理这一条
        /// （可行性校验 + 居中点火）。窗口值由调用方给（游戏是 `ManeuverPreBurnLookaheadTicks = 600`），
        /// **本类不复制它的数值** —— 免得又多一份口径。
        /// </summary>
        public static bool InPreBurnWindow(double now, double executeTick, double lookaheadSeconds)
            => now >= executeTick - lookaheadSeconds;

        /// <summary>
        /// **居中点火时刻**（游戏 `SpaceGameComp.cs:2717` 的唯一算式）：
        /// `ignitionTick = UT − 燃时/2 + 相位扫描偏移 − 执行延迟`。
        /// · 居中 ⇒ 冲量模型的"瞬间"落在燃烧中点（与 MJ 的 `ignitionUT = node.UT − halfBurnTime` 同构）；
        /// · **燃时必须用点火那一刻的活质量估**（计划里的值是排队时算的，而轨道保持一直在扣工质 ⇒ 会过期
        ///   ⇒ "半程"对不上 ⇒ 居中两端不对称）；
        /// · 减**一个执行延迟** = 补掉"首次交付比点火晚一步"（见 `BurnArc.ExecutionDelaySeconds`）。
        /// </summary>
        public static double CenteredIgnitionTick(double executeTick, double burnSecondsForCenter,
            double phaseShiftSeconds, double executionDelaySeconds)
            => executeTick - burnSecondsForCenter / 2.0 + phaseShiftSeconds - executionDelaySeconds;

        /// <summary>
        /// ★ **MJ 的反打转纪律**（游戏 `SpaceGameComp.cs:3767-3785`）：**已进入"停下尺度"、
        /// 而队列里下一条节点还在遥远的未来** ⇒ 删掉队列、改为就地匹配速度。
        /// 上游原文：*If we get within the target distance and then next maneuver node is still far in the
        /// future, delete it and we will create a new one to match velocities immediately.*
        /// 作用：贴到目标附近之后，别再执行那些"为远处设计"的节点（实测没抄它 ⇒ 末端在 20 km 上反复画拦截，
        /// `Intercept` ×63、Δv 合计 7397 m/s vs 政策估计 642）。
        /// </summary>
        /// <param name="distNow">到目标的当前距离（m）</param>
        /// <param name="matchDistance">"停下尺度"（游戏 `rendezvousMatchDist`，100 m；**不是**完成容差）</param>
        /// <param name="nextUt">队列里下一条节点的时刻</param>
        /// <param name="now">当前时刻</param>
        /// <param name="guardSeconds">"还在未来"的严格判据用的守卫（游戏传 1.0）</param>
        public static bool ShouldDropRemoteNodes(double distNow, double matchDistance,
            double nextUt, double now, double guardSeconds = 1.0)
            => distNow < matchDistance && nextUt > now + guardSeconds;

        /// <summary>
        /// **撤回上一条计划未执行的节点**（游戏 `BeginLadderPlanEpoch` / `InvalidateStaleLadderNodes`）：
        /// 阶梯一次排一对（起飞 + 到达刹车），重规划又排一对 ⇒ **旧的刹车仍会执行、且按过期计划的到达点施加**
        /// ⇒ 一记按过期计划打的错向冲量（实测一步 1.5 km → 11.2 km）。
        /// 判据：条目所属的 plan epoch 落后于当前 epoch ⇒ 撤回。
        /// </summary>
        public static bool ShouldRetractStaleNode(double nodePlanEpoch, double currentPlanEpoch)
            => nodePlanEpoch < currentPlanEpoch;
    }
}
