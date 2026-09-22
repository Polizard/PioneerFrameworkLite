using System;

namespace PioneerFramework
{
    /// <summary>
    /// **反推着陆制导的纯核**（2026-09-22）。
    ///
    /// ## 为什么是"垂直反推"这一块（而不是照搬 MJ 的着陆栈）
    /// MJ 的 `HoverslamSimulation` 是**数值打靶**（`MechJebLib/ODE` + `PSG` + `alglib`），
    /// 而我们**全库零第三方依赖**且规模小（"杀鸡用牛刀"）——这与 P-1 决策、以及
    /// `MechJeb参考大修计划` 里"alglib ⚪ 拒"一致。而**垂直反推有闭式**：
    /// 反推段既不是"从零加速"也不是"绕行星"，而是**一维变质量减速**，可以精确求解。
    /// ⇒ 只取其**成功判据的形状**（"按时间飞 + 提交解"），实现自己闭式。
    ///
    /// ## 本文的物理（三条，都是在**反推段**成立的近似）
    /// ① **一维**：反推段水平速度已被前面的段消掉/很小 ⇒ 只解径向；
    /// ② **g 在反推段内近似常数**（反推段只有几十秒~百米级，`g` 随高度变化可忽略）——
    ///    要更准的话每次调用重算一次 `g(h)` 即可（本核就是这样：`g` 是入参，调用方按当前高度给）；
    /// ③ **节流连续**（推力可在 0~aMax 之间调），质量随工质按 `ṁ = F/ve` 减小 ⇒ 加速度增大。
    ///
    /// ## 与火箭方程的衔接（为什么不是简单的 `v²/2a`）
    /// 定加速度 `a₀` 会把工质算多/算少：反推段**质量在掉、可用加速度在涨**。
    /// ⇒ 用**火箭方程**把"现有工质能消掉多少速度"算准：
    /// `Δv_cap = ve·ln(m₀/m_dry_with_margin)`，再令需求 `Δv_need = √(2·a_guess·h) + Δv_margin`，
    /// 二者相交给**点火高度**。这样"能不能刹住、在哪点火"是一个**可判定的数**（不是拍脑袋的常数）。
    ///
    /// **纯**：只吃 double/DVector3，不碰 Verse/UnityEngine ⇒ 可链进纯测试。
    /// </summary>
    public static class LandingGuidanceCore
    {
        /// <summary>制导动作。<see cref="Burn"/> 时读 <see cref="LandingOutput.dir"/>/<see cref="LandingOutput.targetAccel"/>。</summary>
        public enum Action
        {
            /// <summary>还没到点火高度：不要烧（保留工质给反推段）。</summary>
            Coast = 0,
            /// <summary>反推中：按 <see cref="LandingOutput.targetAccel"/> 调油门（沿 <see cref="LandingOutput.dir"/>）。</summary>
            Burn = 1,
            /// <summary>已到目标高度且速度足够低：结束。</summary>
            Landed = 2,
            /// <summary>**刹不住**（工质不够把当前速度在到达前消掉）⇒ 让调用方明确中止/告警，不要静默撞地。</summary>
            Insufficient = 3,
        }

        /// <summary>制导参数（纯数据，可序列化/可单测）。默认值按 PFL 的火箭档给，可被场景覆盖。</summary>
        public struct Params
        {
            /// <summary>点火判定需要的安全余量（m/s）：需求速度要**低于**能力这个量才点火。</summary>
            public double dvMargin;
            /// <summary>目标悬停高度（m，相对表面）：到它就算到（不是 0 米——留出来给着陆腿/建筑）。</summary>
            public double targetAltitude;
            /// <summary>到达判据的速度窗口（m/s）：|径向速度| 小于它就判 <see cref="Action.Landed"/>。</summary>
            public double landSpeedTol;
            /// <summary>**最终减速段的目标余速**（m/s）：反推把它作为"到达时还要剩多少向下速度"的终端条件
            /// ⇒ 让最后一段有个小的正余速（贴着地面再收尾），而不是"恰好为零"（那对离散步长太脆）。</summary>
            public double terminalSpeed;
            /// <summary>加速度指令的斜率上限（m/s³）：防"一步之内把指令从 0 拉到满"造成抖振。
            /// 0 = 不限（旧行为）。</summary>
            public double accelSlew;

            public static Params CreateDefault()
            {
                return new Params
                {
                    dvMargin = 50.0,
                    targetAltitude = 20.0,
                    landSpeedTol = 2.0,
                    terminalSpeed = 1.0,
                    accelSlew = 0.0,
                };
            }
        }

        /// <summary>
        /// ★★ **跨步状态**（2026-09-22）——这就是第一版缺的东西。
        ///
        /// ## 为什么必须有
        /// "**有没有开始反推**"是一个**跨步的锁存**：真正的反推一旦点着就**一直推到落地**，
        /// 中途不熄火。而第一版把它做成"每步重算的无状态函数"（每步都问"现在该不该烧"），
        /// ⇒ 一旦 `h_ign`（它随速度平方缩小）掉到当前余高之下，函数就**又返回 Coast、把引擎关掉**，
        /// 速度冻在那儿、一路冲出目标（闭环实测：降到 −210 km 而永不判到达）✗✗。
        ///
        /// ⚠ **锁存一旦置位就不再清**（离开只有"到达"或"工质尽"）——这正是"点着了就推到底"。
        /// 调用方（游戏侧控制器）持有它，**每步传同一个实例**。
        ///
        /// ⚠⚠ **本核不提供"无状态单步"重载**（第一版有，已删）：它底下必然要一个 scratch 锁存，
        ///   而那个 scratch 会**跨调用/跨对象共享**（实测：两个测试互相污染，前一个点着火 ⇒ 后一个读到的
        ///   状态是"已经在反推"）。**要么自己持有 `State`、要么用 `Probe`**——不要让核替你藏状态。
        /// </summary>
        public struct State
        {
            /// <summary>已进入反推段（锁存）。</summary>
            public bool ignited;
        }

        /// <summary>
        /// 制导输出。**唯一算式**：调用方（游戏侧控制器）只管按它烧，不许另算一份。
        /// </summary>
        public readonly struct LandingOutput
        {
            public readonly Action action;
            /// <summary>推力方向（单位矢量；<see cref="Action.Coast"/>/<see cref="Action.Landed"/> 时为 0）。</summary>
            public readonly DVector3 dir;
            /// <summary>目标加速度（m/s²）：向上为正。执行器把它换算成油门 = `targetAccel / aMax`。</summary>
            public readonly double targetAccel;
            /// <summary>点火高度（m）——**读数用**：`Coast` 时告诉你"还有多高开始烧"。</summary>
            public readonly double ignitionAltitude;
            /// <summary>需求 Δv（m/s）与能力 Δv（m/s）——**诊断用**（"为什么判刹不住"要靠这两个数）。</summary>
            public readonly double dvNeed, dvCap;
            /// <summary>人类可读的原因（日志用）。</summary>
            public readonly string why;

            public LandingOutput(Action a, DVector3 d, double acc, double ignAlt, double need, double cap, string why)
            {
                action = a; dir = d; targetAccel = acc; ignitionAltitude = ignAlt;
                dvNeed = need; dvCap = cap; this.why = why;
            }
        }

        /// <summary>
        /// 一步制导（**唯一算式**）。
        /// </summary>
        /// <param name="altitude">当前高度（m，相对**表面**）</param>
        /// <param name="radialSpeed">径向速度（m/s，**向下为正**——本核统一用"下降率"口径，避免符号混用）</param>
        /// <param name="g">当前高度的重力加速度（m/s²，正数）</param>
        /// <param name="mass">当前总质量（kg，含工质）</param>
        /// <param name="dryMass">干质量（kg）——能力上限要用它</param>
        /// <param name="ve">有效排气速度（m/s，= ΣF/Σṁ）</param>
        /// <param name="aMax">当前**可用**全油门加速度（m/s²，= ΣF/mass）</param>
        /// <param name="p">参数</param>
        /// <summary>
        /// **纯查询**（无副作用，不碰锁存）：用来问"点火高度是多少 / 现在该不该烧"。
        /// 诊断与 UI 用它；**闭环控制必须用 <see cref="Step" />**（那才有锁存）。
        /// </summary>
        public static LandingOutput Probe(double altitude, double radialSpeed, double g,
            double mass, double dryMass, double ve, double aMax, in Params p)
        {
            var st = new State();
            return Step(altitude, radialSpeed, g, mass, dryMass, ve, aMax, p, ref st);
        }

        /// <summary>
        /// 一步制导（**唯一算式**，带跨步状态）。
        /// </summary>
        public static LandingOutput Step(double altitude, double radialSpeed, double g,
            double mass, double dryMass, double ve, double aMax, in Params p, ref State st)
        {
            // ── 到达：**已降到目标高度或以下** 且 下降率在窗口内 ──
            // ⚠ 判据是**单向**的（`altitude ≤ target`），不是"离目标 ±x 内"：
            //   第一版写成 `|altitude − target| ≤ max(target·0.5, 1)`，而 target=20 m 时那是 **±10 m**
            //   配 2 m/s 的速度窗 ⇒ 时间窗 < 一个积分步（实测闭环 1600 步卡在 h=0 而永不判到达）✗✗。
            //   物理上也该是单向的：**降到目标高度就算到**（不可能"从下面回升上来才算到达"）。
            if (altitude <= p.targetAltitude && Math.Abs(radialSpeed) <= p.landSpeedTol)
                return new LandingOutput(Action.Landed, DVector3.zero, 0.0, 0.0, 0.0, 0.0,
                    string.Format("已到 {0:F1} m（目标 {1:F0}）且下降率 {2:F2} m/s ≤ {3:F1}",
                        altitude, p.targetAltitude, radialSpeed, p.landSpeedTol));

            if (!(altitude > 0.0) || !(g > 0.0) || !(mass > 0.0) || !(ve > 0.0) || !(aMax > 0.0))
                return new LandingOutput(Action.Coast, DVector3.zero, 0.0, 0.0, 0.0, 0.0, "输入不完整");

            // ── 需求 Δv：把"下降率 v、终端还要剩 vEnd"在余下高度里消掉 ──
            //    所需减速 a_need = (v² − vEnd²) / (2·(h − h_target))，Δv_need = a_need · t
            //    用平均速度近似 t = 2(h−h_target)/(v + vEnd) ⇒ Δv_need = (v² − vEnd²)/(v + vEnd) = v − vEnd
            //    ⚠ 这个简化**正是"能量守恒"的一半**：把"速度从 v 消到 vEnd"当纯动能问题，
            //      而"要用多少工质"由火箭方程给——两者相乘才是完整答案（下一段算能力）。
            double v = Math.Max(radialSpeed, 0.0);                  // 向上飞（爬升）时不需要反推
            double vEnd = Math.Max(p.terminalSpeed, 0.0);
            // 余高（用于点火高度与净减速）：低于目标高度时归零（此时已经在做终端收尾）
            double hUse = Math.Max(altitude - p.targetAltitude, 0.0);
            double dvNeed = Math.Max(v - vEnd, 0.0);

            // ── 能力 Δv：火箭方程（用**余下工质**）──
            double fuelLeft = Math.Max(mass - dryMass, 0.0);
            double massAfter = Math.Max(mass - fuelLeft, 1.0);
            double dvCap = ve * Math.Log(Math.Max(mass, 1.0) / Math.Max(massAfter, 1.0));

            // ── 点火高度：以**净**减速度能力反推"还有多高就该开始烧" ──
            //    `h_ign = (v² − vEnd²) / (2·(aMax − g))`
            //
            // ⚠⚠ **必须是净减速度 `aMax − g`，不能用 `aMax`**（第一版就是错的，实测代价见下）：
            //   重力**不是在帮忙减速**，它是**每时每刻都要被抵消掉的那一份**——推力先抵掉 g、
            //   剩下的才是净减速。用 `aMax` 会把点火高度算小 `aMax/(aMax−g)` 倍：
            //   本例 17.74/8.87 = **2.0 倍**（实测 913 m vs 正确 3654 m）⇒ 点火晚一倍 ⇒
            //   **反推段刹不住、冲过目标**（闭环实测：下降到 −200 km 而永不判到达）。
            //   ⚠ 若 `aMax ≤ g` ⇒ **根本没有净减速能力**（推重比 ≤1）⇒ 一条明确的可判定结论：
            //     直接报 `Insufficient`，而不是算出一个负的点火高度。
            double aNetCap = aMax - g;                 // 净减速能力（正 = 真能减速）
            if (!(aNetCap > 0.0))
                return new LandingOutput(Action.Insufficient, DVector3.zero, 0.0, double.PositiveInfinity,
                    dvNeed, dvCap,
                    string.Format("推重比不足：aMax={0:F2} ≤ g={1:F2} m/s² ⇒ **没有净减速能力**（推重比 {2:F2}）",
                        aMax, g, aMax / g));
            double hIgn = Math.Max(0.0, (v * v - vEnd * vEnd)) / (2.0 * aNetCap);

            // ── 刹不住：**需求 > 能力 + 余量** ⇒ 明确报（不静默撞地）──
            if (dvNeed > dvCap + p.dvMargin)
                return new LandingOutput(Action.Insufficient, DVector3.zero, 0.0, hIgn, dvNeed, dvCap,
                    string.Format("刹不住：需消 {0:F1} m/s，工质只够 {1:F1} m/s（h={2:F0} m、下降率 {3:F1}）",
                        dvNeed, dvCap, altitude, v));

            // ── 还没点火：看点火高度决定"要不要开始" ──
            // ⚠⚠ **这一段只在 `st.ignited == false` 时有意义**（第一版没这个门，实测代价见 `State` 的注释）：
            //   一旦点火，点火高度就**不再参与**（它随速度平方缩小，再判一次会把引擎关掉）✗✗。
            if (!st.ignited && hUse > hIgn)
                return new LandingOutput(Action.Coast, DVector3.zero, 0.0, hIgn, dvNeed, dvCap,
                    string.Format("等点火：还有 {0:F0} m（点火高度 {1:F0} m）", hUse, hIgn));
            st.ignited = true;   // ← 锁存：从这里起一直推到底

            // ── 反推（进入后持续到到达/刹不住）：把当前余速在余下高度里消到 vEnd ──
            //    所需**净**减速度（不含重力）：a_net = (v² − vEnd²) / (2·hUse)
            //    推力指令 = 净需求 + 重力（把重力抵消掉，剩下的才是净减速）
            //    指令超上限时按上限给 ⇒ 调用方满油门烧（那正是"进入反推段"的物理含义）
            double aNet = hUse > 0.0 ? (v * v - vEnd * vEnd) / (2.0 * hUse) : 0.0;
            double acc = Math.Max(aNet, 0.0) + g;
            if (acc > aMax) acc = aMax;
            return new LandingOutput(Action.Burn, DVector3.up, acc, hIgn, dvNeed, dvCap,
                string.Format("反推：净需 {0:F2} m/s²（+g={1:F2}）⇒ 指令 {2:F2} m/s²（上限 {3:F2}）",
                    Math.Max(aNet, 0.0), g, acc, aMax));
        }
    }
}
