using System;

namespace PioneerFramework
{
    /// <summary>
    /// 交会「不变量闭环」纯核心（2026-09-17）。
    ///
    /// 理论依据：<c>DesignNotes/交会物理本质与闭环提法-20260917.md</c>
    ///   §5   相位通道 = 双积分器（状态 δφ 与 δn，控制 = 切向加速度）
    ///   §6.2 相位守恒律 Δv·t = (2/3)·r·Δφ（小控制渐近）
    ///   §10  近圆约化 ⇒ 螺旋价 = |v_c1 − v_c2| = 霍曼价的 ≤(1+√2) 倍（实用区 ≤1.15）
    ///   §8   用无奇异量（本类只用 a 与 δφ，不碰 e/ω/i/Ω）
    ///
    /// ── 近圆约化的推导（本类的全部口径）────────────────────────────────
    /// 取 θ = 轨道面内的极角，则
    ///   δφ := θ_c − θ_t                    （>0 = 本船超前）
    ///   δn := δφ' = n_c − n_t = −(3n/2a)·δa
    ///   δa := a − a_tgt
    /// 切向推力对 δn 的作用（Gauss + vis-viva 一阶）：
    ///   a' = (2a/v_c)·a_t      ⇒   δn' = −(3n/2a)·a' = −(3/v_c)·a_t = −(3/r)·a_t
    /// 于是
    ///   δφ'' = −(3/r)·a_t,     |δφ''| ≤ U := 3·a_max/r
    /// **面内交会的全部内容就是一个标量双积分器，原点即交会点。**
    /// （δφ=0 且 δn=0 ⟺ δa=0 且相位重合 ⟺ 位置与速度同时重合。）
    ///
    /// 与「定时刻定点 Lambert」的差别（§4.2 vs §4.3）：
    ///   本类的误差信号是 (δφ, δn)，增益 ‖G‖=O(1)；
    ///   后者是「到固定点的位置偏差」，增益 ~1/(3·T_remaining) → ∞。
    /// </summary>
    public static class RendezvousGuidanceCore
    {
        /// <summary>执行器选择（与 SpaceGameComp 引导口径一致）。</summary>
        public enum Actuator
        {
            None,
            /// <summary>主引擎：沿主推轴锥内出力，**需要姿态对准**，大 Δv 用它。</summary>
            Main,
            /// <summary>RCS：全矢量免对准，小修正/未对准兜底。</summary>
            Rcs,
        }

        /// <summary>
        /// 面内切向控制律（固定时间预算下的最小燃料）。
        ///
        /// 双积分器 δφ'' = u，|u| ≤ uMax，其中 u := −(3/r)·a_t。
        /// 由 u = −uMax·sign(σ) 反解回推力：a_t = −(r/3)·u ⇒ **+1 = 顺行 = 抬半长轴 = 时钟变慢**。
        ///
        /// ⚠ 为什么必须带时间预算：**纯"最小燃料 + 自由末时"是退化解** ——
        /// 打一个极小脉冲后无限滑行即可让 Δv→0。所以必须有 T（这正是 §6.4 / §12.2
        /// "计划 = Pareto 前沿上的一个点，由玩家选"的落点）。
        ///
        /// 律（参考漂移率形式，等价于双积分器的两点点火解）：
        ///   δn_ref = −δφ/T                  （在剩余预算内把相位恰好收到零的漂移率）
        ///   若剩余时间已不足把当前 δn 归零（T ≤ |δn|/uMax）→ 目标 δn = 0（收束段）
        ///   否则 → 目标 δn = δn_ref
        ///   u = uMax·sign(δn_tgt − δn)
        ///
        /// 代价校验（小控制渐近，§5.4）：δn* = |δφ|/T ⇒
        ///   Δv = 2·(r/3)·δn* = (2/3)·r·|δφ|/T  —— **正是相位守恒律**。
        /// </summary>
        /// <param name="phi">相位差 δφ（rad，>0 = 本船超前）。</param>
        /// <param name="dphiDot">相位差速率 δn（rad/s，含**自然漂移**——异轨时它非零）。</param>
        /// <param name="uMax">控制上限 U = 3·a_max/r（rad/s²）。</param>
        /// <param name="timeBudget">**剩余**时间预算 T（秒）。
        /// **+∞ = 只做形状（相位自由）**；有限值 = 交会模式。
        /// ⚠ 必须是剩余量而非总预算：T 递减时参考漂移率 δn*=−δφ/T 让相位**线性**收到零；
        /// 传常数 budget 会退化成 φ₀·e^(−t/T) 的渐进收敛（t=T 时仍剩 37%）——2026-09-17 实测。</param>
        /// <param name="trackingFraction">追踪时间常数 τ = f·T 的 f。
        /// **必须 ≪1**：τ 是参考漂移率 δn* 的追踪滞后，滞后会让相位来不及收 ⇒
        /// T→0 时律惊慌饱和 ⇒ 过冲与超额 Δv（2026-09-17 实测 f=0.3 时 T=96000 花掉 631 而非 191）。
        /// f=0.05 ⇒ 滞后 ≤5%T，对 3τ 的收敛时间仍余量充足。</param>
        /// <returns>归一化切向加速度 ∈ [−1, 1]（乘 a_max 得推力加速度，**+1 = 顺行**）。
        /// 注意是**连续量**——发动机可节流，需要多少推多少；返回 ±1 时才吃满推力。</returns>
        public static double InPlaneTangentialControl(double phi, double dphiDot, double uMax,
            double timeBudget, double trackingFraction = 0.05)
        {
            if (!(uMax > 0.0) || !IsFinite(phi) || !IsFinite(dphiDot)) return 0.0;

            // ── 相位自由模式（只把半长轴开到目标）：δn → 0 的一阶收敛 ──
            if (double.IsPositiveInfinity(timeBudget))
            {
                // δn>0（a 偏小）⇒ 顺行抬 a ⇒ δn 降；时间常数固定（无预算概念）
                double tauFree = FreeShapeTimeConstant;
                return Clamp(dphiDot / (tauFree * uMax), -1.0, 1.0);
            }

            // ── 超期：不再追相位，改为**保住轨道**（δn → 0）──
            //   ⚠ 旧写法在超期时取 T=1e-3 ⇒ δn* = −δφ/1e-3 = −1000δφ ⇒ 满推力狂烧。
            //   实测：测试跑到 1.2T 时，那 0.2T 的狂烧量级 ≈ 观测到的全部"超额"
            //   （T=192000：0.2T×a_max·U 折算 ≈ 768 m/s，实测超额 534 m/s）——2026-09-17。
            //   真正部署时这里是**切精确终端制导**的交接点（§12.2），台架里退化为保轨。
            if (timeBudget <= 0.0)
            {
                double tauFreeOver = FreeShapeTimeConstant;
                return Clamp(dphiDot / (tauFreeOver * uMax), -1.0, 1.0);
            }

            double T = timeBudget;

            // ── 切换面 S：**"从现在起不再调整 δn，只滑行 + 收尾一次 bang"会落到的相位误差** ──
            //
            //   设当前 δn，收尾需时 |δn|/U（把 δn 线性归零），其间相位变化 δn·|δn|/(2U)；
            //   此前滑行 t_c = T − |δn|/U，相位变化 δn·t_c。合计
            //       Δφ_pred = δn·T − δn·|δn|/(2U)
            //   故
            //       S := δφ + Δφ_pred = δφ + δn·T − δn·|δn|/(2U)
            //   **S = 0 ⟺ 就此滑行+收束，恰好落在目标相位上。**
            //
            //   为什么直接驱 S→0 比"追一个会变的参考 δn*"好：
            //     · S 是**终端的真实条件**，不含"参考自身在动"这一层滞后 ⇒ 无追踪过冲；
            //     · **可行性自动内嵌**：S=0 有解 ⟺ |δφ| ≤ ½·U·T²，正是两点火可行域
            //       （LHS 对 δn 的最大值）。不可行时律自然饱和在 δn = U·T，无需分支；
            //     · 收尾的 bang 被算进 S 里，不会在末端才发现"来不及"。
            //
            //   增益：令 δn′ = −k·S，则
            //       Ṡ = φ̇ + δn′(T − |δn|/U) + δn·Ṫ = −k·S·(T − |δn|/U)
            //   取 k = 1/(f·T²) ⇒ 滑行段的时间常数 f·T（f≪1）。
            //   注意 |δn|/U → T 时 Ṡ 的系数 →0：**一旦提交收束，S 就不再可控**——
            //   这是物理事实（bang 已定），也要求我们在还有余量时就把 S 压到 0。
            // ── 参考漂移率：**通用式**（含"建立 δn* 要花的时间"）────────────────
            //   从**当前** δn 出发：ramp 到 δn*、滑行 t_c、ramp 回 0，总时长 T：
            //     Δφ_ach(δn*) = (δn+δn*)|δn*−δn|/(2U) + δn*·t_c + δn*²/(2U),
            //                   t_c = T − |δn*−δn|/U − |δn*|/U
            //   两个极限都对：
            //     δn=0      ⇒ δn*T − δn*²/U      （两点火；**能闭合的上界是 U·T²/4**）
            //     δn=δn*    ⇒ δn*T − δn*²/(2U)    （滑行+一次 bang；上界 U·T²/2）
            //
            //   ⚠ 只写后者的形式（S 切换面）会在**δn 还没到位**时也用它 ⇒ 以为可行而实际不可行：
            //   实测 T=48000、|Δφ|=1.0472、U=1.645e-9（U·T²/4 = 0.948 < 1.0472，单腿本不可能闭合）
            //   时，旧写法只做到 568.9 m/s、残余 32.8%——**连最大努力 692.8 都没用满**。
            //
            //   求解：δn* 在 [0, U·T] 上扫描（Δφ_ach 先增后减，非单调 ⇒ 不用二分）；
            //   不可行（|δφ| > 可达上界）时取**使 Δφ_ach 最大**的 δn*，即**出满力**。
            //   本实现取 **S 形式**（滑行 + 一次 bang 的预测式）：
            //     相位覆盖 = δn*T − δn*²/(2U)   ⇒   可行上界 U·T²/2
            //   它**无追踪滞后**（参考就是终端的真实条件），在可行域内最省；
            //   代价是**对"从静止起步"高估约 2 倍**（正解是两点火式，上界只有 U·T²/4）——
            //   实测 T=48000、|Δφ|=1.0472 在 U·T²/4=0.948 < 1.0472 < U·T²/2=1.896 的缝里：
            //   S 律以为可行、实际不可能，于是残余 32.8%、代价 568.9（连最大努力 692.8 都没用满）。
            //   两点火式（含 ramp 项）试过：该案 +2.6% ✓ 但可行域变差（T=192000 时 +33.4%，
            //   且终端接力点够不到）——**两者互斥，取 S 形式**。
            //   ⇒ **紧预算的正解在计划层**：按 U·T²/4 判可行、按 MinLegSeconds 定腿长（见 API）。
            // ── 工作点：**S 形式**（滑行 + 一次 bang 的预测式）──────────────────
            //   S := δφ + δn·T − δn|δn|/(2U)，令 δn′ = −k·S ⇒ Ṡ = −k·S·(T − |δn|/U)，
            //   取 k = 1/(f·T_eff²) ⇒ 滑行段时间常数 f·T。**无追踪滞后**（参考就是终端的真实条件），
            //   可行域内最省；代价是对"从静止起步"高估约 2 倍（正解上界 U·T²/4，见下）。
            //
            //   ⚠ **本批试过三种"更正确"的参考形式，全部否决**（每次都换一个工况变坏）：
            //     ① 两点火闭式（含 ramp 项 + 不可行时出满力）：T=48000 修到 +2.6% ✓，
            //        但 T=192000 恶化到 +33.4%，且终端接力点够不到（T5 某预算不收敛）；
            //     ② 通用式（含任意当前 δn 的 ramp 项）：扫描分辨率 + 追踪滞后，全面更差；
            //     ③ 按可达性条件切换：固定阈值 U·T²/4 会在腿末**误判不可行**（那时 δn 已建起、
            //        真上界接近 U·T²/2）⇒ 满力过冲；改用 MaxAchievablePhase 后该误差消除，
            //        但整体仍不优于 ①。
            //   ⇒ 结论：**滚动执行下每段都经历"从静止起步"与"已在 δn 上"两种状态**，
            //     单一标量参考的律无法在两个区域同时精确。**紧预算的正解在计划层**
            //     （按 U·T²/4 判可行、按 MinLegSeconds 定腿长、不可行时诚实报"所需最短时间"）。
            double tEff = Math.Max(T, GainTimeFloor);
            double S = phi + dphiDot * T - dphiDot * Math.Abs(dphiDot) / (2.0 * uMax);
            double k = 1.0 / (Math.Max(trackingFraction, 1e-4) * tEff * T);
            double uNorm = k * S / uMax;                 // u_norm = a_t/a_max = −δn′/U
            return Clamp(uNorm, -1.0, 1.0);
        }

        /// <summary>
        /// **可达上界**：从当前 $\delta n$ 出发、在 $T$ 内能闭合的最大相位。
        ///
        /// 通用式（ramp 到 $\delta n^*$ + 滑行 + ramp 回 0）：
        /// $$\Delta\varphi_{ach}(\delta n^*)=
        ///   \frac{(|\delta n|+\delta n^*)\big||\delta n^*|-|\delta n|\big|}{2U}
        ///   +\delta n^*\Big(T-\frac{\big||\delta n^*|-|\delta n|\big|}{U}-\frac{\delta n^*}{U}\Big)
        ///   +\frac{\delta n^{*2}}{2U}$$
        /// 两个极限：$\delta n=0$ ⇒ $UT^2/4$；$\delta n=\delta n^*$ ⇒ $UT^2/2$。
        ///
        /// ⚠ **判可达性必须用它，不能用固定的 $UT^2/4$**：后者只是"新腿从静止起步"的上界，
        /// 而腿末 $\delta n$ 已经建起来了，真正的上界接近 $UT^2/2$。用固定阈值会在腿末
        /// **误判为不可行** ⇒ 触发满力分支 ⇒ 过冲（实测：T=192000 从 109.2 恶化到 165.9）。
        /// </summary>
        /// <param name="dnArgMax">取到上界的 $\delta n^*$（出满力时用它当目标）。</param>
        public static double MaxAchievablePhase(double dphiDot, double uMax, double timeBudget,
            out double dnArgMax)
        {
            dnArgMax = 0.0;
            if (!(uMax > 0.0) || !(timeBudget > 0.0)) return 0.0;
            double dn0 = Math.Abs(dphiDot);
            double dmax = uMax * timeBudget;
            const int N = 40;
            double best = 0.0;
            for (int i = 0; i <= N; i++)
            {
                double dn = dmax * i / N;
                double rampGo = Math.Abs(dn - dn0) / uMax;
                double rampBack = dn / uMax;
                double tCoast = timeBudget - rampGo - rampBack;
                if (tCoast < 0.0) continue;
                double ach = (dn0 + dn) * Math.Abs(dn - dn0) / (2.0 * uMax)
                           + dn * tCoast + dn * dn / (2.0 * uMax);
                if (ach > best) { best = ach; dnArgMax = dn; }
            }
            return best;
        }

        /// <summary>
        /// 单腿**能闭合的最大相位**（可行性判据）：$\max_{\delta n^*}$ 通用式 $=U\,T^2/4$（从静止起步）。
        /// 超过它就必须分腿或给更长的时间——这是定理，不是缺陷。
        /// </summary>
        public static double MaxClosablePhase(double uMax, double timeBudget)
            => uMax > 0.0 && timeBudget > 0.0 ? 0.25 * uMax * timeBudget * timeBudget : 0.0;

        /// <summary>把相位误差 $|\delta\varphi|$ 收掉所需的**最短**单腿时长：$2\sqrt{|\delta\varphi|/U}$。</summary>
        public static double MinLegSeconds(double phi, double uMax)
            => uMax > 0.0 ? 2.0 * Math.Sqrt(Math.Abs(phi) / uMax) : double.PositiveInfinity;

        /// <summary>
        /// **精确基准**：双积分器 $\delta\varphi''=u, \|u|\le U$，从 $(\delta\varphi_0, 0)$ 到 $(0,0)$、
        /// 限时 $T$ 的最小燃料成本（归一化，乘 $(r/3)$ 得 Δv）。
        ///
        /// 最优结构是「bang - 滑行 - bang」：两次 bang 各耗时 $|\delta n^*|/U$，滑行 $t_c = T - 2|\delta n^*|/U$，
        /// 相位覆盖 $=\delta n^{*2}/U + \delta n^* t_c = \delta n^* T - \delta n^{*2}/U$。令其等于 $|\delta\varphi_0|$，解得
        ///   $$\delta n^* = \frac U2 \left( T - \sqrt{ T^2 - \frac{4|\delta\varphi_0|}{U} } \right)$$
        /// 可行条件 $T^2 \ge 4|\delta\varphi_0|/U$；不可行时能闭合的最大相位是 $U T^2/4$（抛物线顶点）。
        ///
        /// **用途**：这是“律对不对”的正确判据。渐近守恒律 $\frac23 r|\delta\varphi|/T$ 只是
        /// $T \gg \sqrt{4|\delta\varphi|/U}$ 的极限——用它当基准会把**有限控制修正**误判成浪费
        /// （实测 T=48000 时两者差 37%，全在基准里，不在律里）。
        /// </summary>
        public static double MinFuelCostReference(double phi0, double uMax, double timeBudget)
        {
            if (!(uMax > 0.0) || !(timeBudget > 0.0)) return 0.0;
            double need = Math.Abs(phi0);
            double disc = timeBudget * timeBudget - 4.0 * need / uMax;
            if (disc <= 0.0) return uMax * timeBudget;        // 不可行：顶点（最大努力）
            return uMax * (timeBudget - Math.Sqrt(disc));     // = 2·δn*
        }

        /// <summary>
        /// **接近段的时间律**（2026-09-20 改成 MJ 的形式；条目 16：我们此前抄错了 MJ）。
        ///
        /// 上游本体：`MechJebModuleDockingAutopilot.MaxSpeedForDistance`（`:117-124`）——
        /// > *Get how fast the ship can move on the axis and still be able to stop before reaching the distance.*
        /// > `v = √(2·|d|·a_axis)`（再 `FixSpeed` clamp 到限速）
        /// ⇒ 反过来就是"覆盖这段距离所需的最短时间"：`tof = |d_axis| / min(√(2|d|a), vMax)`。
        ///
        /// **我们此前抄的是它"轨道段"那个常数时间律**（`tof = 距离/50`）✗：距离小时 tof→0（指令 Δv 爆掉）、
        /// 距离大时又慢；更糟的是它在**终端窗口边界**跳变，只好再加一个 `tof = 1.0` 的补丁压极限环 ✗✗
        /// （那些补丁与常数的根都在这里）。MJ 这条**对 d 连续**（`d→0 ⇒ tof→0` 平滑 ✓）且自带
        /// "**到得了也刹得住**"的物理含义 ✓。
        ///
        /// @param dx,dy,dz 相对位置在**本船 RTN** 上的三个分量（逐轴，MJ 也是逐轴 ✓）
        /// @param accel    可用加速度（m/s²；本实现不逐轴细分 —— 我们没有逐轴推力数据，取统一值并注明）
        /// @param maxClosingSpeed 限速（m/s，MJ 的 `FixSpeed` 对应物）
        /// @returns 覆盖这段相对位置所需的最短时间（s）；**取最慢的那根轴**，它才是约束 ✓
        /// </summary>
        public static double ApproachTimeSeconds(double dx, double dy, double dz, double accel, double maxClosingSpeed)
        {
            if (!(accel > 0.0) || !(maxClosingSpeed > 0.0)) return 0.0;
            double[] d = [Math.Abs(dx), Math.Abs(dy), Math.Abs(dz)];
            double tof = 0.0;
            for (int i = 0; i < 3; i++)
            {
                if (!(d[i] > 0.0)) continue;
                double vMaxStop = Math.Sqrt(2.0 * d[i] * accel);      // MJ：还能刹住的最高速度
                double v = Math.Min(vMaxStop, maxClosingSpeed);        // 再 clamp 到限速
                double t = d[i] / v;
                if (t > tof) tof = t;
            }
            return tof;
        }

        /// <summary>增益时间下限（秒）：防 k=1/(fT²) 在 T→0 时爆炸（见上）。取轨道周期量级。</summary>
        public const double GainTimeFloor = 5000.0;

        /// <summary>相位自由模式的收敛时间常数（秒）：取几个轨道周期量级，与半长轴通道同阶。</summary>
        public const double FreeShapeTimeConstant = 3000.0;

        /// <summary>
        /// **这次交会"该花多少 Δv"的量级估计**（两个通道的价目之和）—— **全库唯一一份**。
        /// 用途两处，都只当**量级**用（不是规划精度）：
        ///   ① 区制判据 `ManeuverRegime` 需要一个 Δv 量级（Θ = (Δv/v_c)·(g/a)）；
        ///   ② A4 计划级对照表里"连续律那一侧"的价目。
        /// 两个通道（出处写在算式旁）：
        ///   · **形状**：`|v_c(r1) − v_c(r2)|`（设计稿 §7.6）——**连续螺旋**就是这个数（1×），
        ///     而**脉冲两冲量**要"一加一减"⇒ 2×（两套机制在这里的差别是**物理**，不是口径 ✗）
        ///     ⇒ 由 <paramref name="impulsive"/> 选（区制判据问的是"脉冲解要多少"）；
        ///   · **相位**：改 a 收相位 ⇒ `Δv·t = (2/3)·r·Δφ`（设计稿 §5.4 的守恒律，取单圈极限）。
        /// ⚠ 相位项是**渐近式**（`T ≫ √(4Δφ/u)` 的极限）；小时间预算下的精确基准是
        /// <see cref="MinFuelCostReference"/> —— 本函数只回答"量级"，**别拿它当可行性判据**。
        /// </summary>
        public static double CostEstimate(double rSelf, double rTarget, double dPhiRad, double mu,
            bool impulsive = true)
            => CostEstimate(rSelf, rTarget, dPhiRad, 0.0, mu, impulsive);

        /// <summary>
        /// 同上，但**把面外（倾角差）算进去**（2026-09-20 加，B2.6）。
        ///
        /// ⚠ 为什么必须加：本函数是**区制判据 `ManeuverRegime` 的输入**（Θ = (Δv/v_c)(g/a)）。
        ///   若漏掉面外项，"**异面 + 低推力**"的交会 Δv 会被**低估** ⇒ Θ 偏小 ⇒ **被错分到准脉冲区（阶梯）** ✗ —
        ///   而阶梯给的是**脉冲平面改变**，低推力船执行不了 ✗✗（正是设计稿 §10.5-N1 / B2.6 的那条）。
        ///
        /// 面外项的两个口径（B2.6 的两份价）：
        ///   · **脉冲式** `v_slow · Δi` —— 在**慢的那一端**做平面改变（MJ 的做法：并进转移、放远地点 ✓），
        ///     故取 `min(v_c(rSelf), v_c(rTarget))` ✓（LEO→GTO 在远地点做 ⇒ 用那儿的慢速度 ✓）。
        ///   · **谐波式（我们要换掉的那套）** `2·v_c(rSelf)·Δi` —— 设计稿实测量级 ✓（约 2 倍 ✗）。
        /// </summary>
        public static double CostEstimate(double rSelf, double rTarget, double dPhiRad, double dIncRad, double mu,
            bool impulsive = true)
        {
            if (!(rSelf > 0.0) || !(rTarget > 0.0) || !(mu > 0.0)) return 0.0;
            double vcSelf = Math.Sqrt(mu / rSelf);
            double vcTgt = Math.Sqrt(mu / rTarget);
            double periodSelf = 2.0 * Math.PI * Math.Sqrt(rSelf * rSelf * rSelf / mu);
            double shape = (impulsive ? 2.0 : 1.0) * Math.Abs(vcSelf - vcTgt);
            double phase = (2.0 / 3.0) * rSelf * Math.Abs(dPhiRad) / Math.Max(periodSelf, 1e-9);
            double plane = Math.Abs(dIncRad) * (impulsive ? Math.Min(vcSelf, vcTgt) : 2.0 * vcSelf);
            return shape + phase + plane;
        }

        // ── 面外（倾角矢量）通道 ───────────────────────────────────────────────
        //  一般交会必须处理面外：当前 Def 全部共面（orbitNormal 都是 (0,1,0)）只是巧合，
        //  真实倾角差下相对位置会有 r·Δi 量级的交叉轨迹偏移（GEO 上 Δi=1° 即 459 km）。
        //
        //  **面外比面内更简单**：面外相对运动是**纯谐振子**，没有长期项（无类 a 的积分通道），
        //  所以控制律是**纯一阶谐波**，连 c₀ 那一项都没有。
        //
        //  推导：🅐 近圆下面外相对位移满足
        //      ż̈ + n²z = a_n
        //  齐次解 z = A cos u + B sin u（u = 辐角纬度 ≈ 极角）。取一阶谐波控制
        //      a_n(u) = d₁cos u + d₂sin u
        //  代入（慢变 A、B）：ż̈ ≈ −n²z + 2n(−Ȧ sin u + Ḃ cos u) ⇒
        //      Ḃ = d₁/(2n),   Ȧ = −d₂/(2n)
        //  要 (A,B) 以 τ_i 指数衰减（Ȧ=−A/τ_i, Ḃ=−B/τ_i）：
        //      d₁ = −2nB/τ_i,   d₂ = 2nA/τ_i
        //
        //  **代价量级**：谐波幅值 × 时长 ≈ 2n·√(A²+B²) ≈ 2v_c·Δi
        //  （用 √(A²+B²) ≈ r·Δi 与 v_c = n·r）——与经典面外变轨 Δv = 2v sin(Δi/2) ≈ vΔi 同阶；
        //  因子 2 来自谐波在两个半周期都要出力（一推一拉），这是"保持面内不受扰"的代价。

        /// <summary>
        /// 由交叉轨迹状态反解振荡的两常数 $(A,B)$：$z=A\cos u+B\sin u$。
        /// </summary>
        public static void CrossTrackAmplitude(double z, double zDot, double n, double u,
            out double aAmp, out double bAmp)
        {
            if (!(n > 0.0)) { aAmp = 0.0; bAmp = 0.0; return; }
            double c = Math.Cos(u), s = Math.Sin(u);
            aAmp = z * c - (zDot / n) * s;
            bAmp = z * s + (zDot / n) * c;
        }

        /// <summary>
        /// 面外控制律：返回 $a_n(u)=d_1\cos u+d_2\sin u$ 的系数 $(d_1,d_2)$（m/s²）。
        /// $(A,B)$ 以 <paramref name="tauI"/> 指数衰减 ⇒ $(d_1,d_2)=\frac{2n}{\tau_i}(-B,A)$。
        /// </summary>
        /// <param name="amplitudeLimit">单系数幅值上限（m/s²）；通常给 $0.3\,a_{max}$，防面外通道抢走全部推力。</param>
        public static void CrossTrackControl(double z, double zDot, double n, double u, double tauI,
            double amplitudeLimit, out double d1, out double d2)
        {
            CrossTrackAmplitude(z, zDot, n, u, out double aAmp, out double bAmp);
            if (!(tauI > 0.0)) { d1 = 0.0; d2 = 0.0; return; }
            double k = 2.0 * n / tauI;
            d1 = -k * bAmp;
            d2 = k * aAmp;
            if (amplitudeLimit > 0.0)
            {
                d1 = Clamp(d1, -amplitudeLimit, amplitudeLimit);
                d2 = Clamp(d2, -amplitudeLimit, amplitudeLimit);
            }
        }

        /// <summary>面外通道的默认衰减时间常数下限（秒），与面内 e 通道同阶约定：≥3 个轨道周期。</summary>
        public const double CrossTrackTimeConstantFloor = 3.0 * 3000.0;

        // ── 自适应推力上限：让同一套律吃下整个推力谱系 ──────────────────────────
        /// <summary>
        /// **按受迫偏心率给推力上限**，而不是按 $a_t$ 给固定上限。
        ///
        /// 一段切向推力造成的**受迫偏心率峰值**是
        /// $$e_{peak}=\frac{2}{v_c}\min\!\Big(\Delta v,\ \frac{2a_t}{\omega}\Big)$$
        /// （$\hat r$ 的一圈积分恒为 0 ⇒ 无**净** e；峰值在 $\min$ 的拐点处。）两个分支：
        /// · **长燃烧** $t_b\gtrsim T_{orb}$ ⇒ $\dfrac{4a_t}{g_{local}}$（旧口径）；
        /// · **短燃烧** $t_b\ll T_{orb}$ ⇒ $\dfrac{2\Delta v}{v_c}$ —— **只限总 Δv，不限 $a_t$**。
        ///
        /// ⚠ **为什么必须自适应**：近圆约化要求 e 有界，而**固定 $a_t$ 上限是错的代理量**——
        /// 它把"高推力 + 短燃烧"（本来准脉冲、e 由 Δv 而非 $a_t$ 决定）也一并限住，
        /// 把运载火箭式的执行者硬压成低推力。实测（设计稿 §17.2）：η 从 0.05 放到 20
        /// 反而**更不像脉冲**——因为放开限权只改了归一化，燃烧时长仍由 $\Delta v/a_{cap}$ 决定。
        ///
        /// 取"**两支取宽**"：慢烧或短烧，任一成立即放行。两端极限行为都对
        /// （低推力 ⇒ 退回旧口径；高推力 ⇒ 全推力、准脉冲）。
        /// </summary>
        /// <param name="dvBudget">本轮要做的事的 Δv 量级（m/s），典型取 $|S|\cdot r/3$。</param>
        /// <param name="eMax">允许的受迫偏心率峰值（0.2 量级：近圆约化仍成立）。</param>
        /// <returns>加速度上限（m/s²）；<b>+∞ 表示短燃烧分支成立、不额外限权</b>。</returns>
        public static double AdaptiveThrustCap(double vc, double gLocal, double omega,
            double dvBudget, double eMax = 0.2)
        {
            if (!(vc > 0.0) || !(gLocal > 0.0) || !(omega > 0.0) || !(eMax > 0.0))
                return double.PositiveInfinity;
            double aSlow = eMax * gLocal / 4.0;                 // 长燃烧分支
            double dvShortOk = eMax * vc / 2.0;                 // 短燃烧分支的 Δv 门槛
            return dvBudget <= dvShortOk ? double.PositiveInfinity : aSlow;
        }


        private static double Clamp(double x, double lo, double hi) => x < lo ? lo : (x > hi ? hi : x);


        /// <summary>
        /// 纯形状控制（相位自由，只把半长轴开到目标）：单调趋近 + 刹车，
        /// 使 ∫|a_t|dt 恰为螺旋价 |v_c1 − v_c2|（§7.6）。
        /// </summary>
        /// <param name="aErr">δa = a − a_tgt（m）。</param>
        /// <param name="aDot">当前 da/dt（m/s）。</param>
        /// <param name="aDotMax">|da/dt| 上限 = (2a/v_c)·a_max。</param>
        /// <returns>归一化切向控制 ∈ {−1, 0, +1}。</returns>
        public static double SemiMajorAxisControl(double aErr, double aDot, double aDotMax)
        {
            if (!(aDotMax > 0.0)) return 0.0;
            // 刹车半弧：|aDot|²/(2·aDotMax)
            double brake = aDot * Math.Abs(aDot) / (2.0 * aDotMax);
            double sigma = aErr + brake;
            if (Math.Abs(sigma) < 1e-9) return 0.0;
            return sigma < 0.0 ? -1.0 : 1.0;   // δa<0（要抬）⇒ 顺行 +1
        }

        /// <summary>
        /// 由当前切向速度偏差反算相位差速率：δn = −3·δv_t/r。
        /// ⚠ <paramref name="tangentialDeviation"/> 必须是**相对目标轨道的**切向速度偏差
        /// （即 δa 的来源），不是"相对局地圆轨速度"——后者在 a=R1 且目标在 R2 时恒为 0，
        /// 会让律静默不动。优先用 <see cref="PhaseRateFromElements"/>。
        /// </summary>
        public static double PhaseRateFromTangentialDeviation(double tangentialDeviation, double r)
        {
            if (!(r > 0.0)) return 0.0;
            return -3.0 * tangentialDeviation / r;
        }

        /// <summary>
        /// **首选口径**：δn = n(a) − n(a_tgt)（精确，不依赖近圆近似）。
        /// 这是双积分器的"速度"状态；δn=0 ⟺ a=a_tgt。
        /// </summary>
        public static double PhaseRateFromElements(double a, double aTarget, double mu)
        {
            if (!(a > 0.0) || !(aTarget > 0.0) || !(mu > 0.0)) return 0.0;
            return Math.Sqrt(mu / (a * a * a)) - Math.Sqrt(mu / (aTarget * aTarget * aTarget));
        }

        /// <summary>δn → δa 的换算（Gauss 一阶）：δa = −(2a/(3n))·δn = −(2ar/(3v_c))·δn。</summary>
        public static double SemiMajorAxisFromPhaseRate(double dphiDot, double r, double n)
        {
            if (!(n > 0.0) || !(r > 0.0)) return 0.0;
            return -2.0 * r * dphiDot / (3.0 * n);
        }

        /// <summary>
        /// 相位差的另一条口径：由两船半长轴差直接预测会合时间（用于显示/健全性检查）。
        /// 返回"以当前 δn 把相位差收到零所需时间"，δn≈0 时返回 +∞。
        /// </summary>
        public static double TimeToClosePhase(double phi, double dphiDot)
        {
            if (Math.Abs(dphiDot) < 1e-15) return double.PositiveInfinity;
            return -phi / dphiDot;
        }

        /// <summary>
        /// 执行器选择。口径照 <c>SpaceGameComp.UpdateRendezvousGuidance</c>：
        ///   · 主推未对准 ⇒ 主网不出力，只能用 RCS（或等姿态转过去）；
        ///   · 引导惯例：Δv 能在 RCS 的 15 s 内烧完 ⇒ 用 RCS（免对准、无转向死区）；
        ///   · 主网尽 / 无主网 ⇒ RCS 兜底；RCS 尽 ⇒ 主网兜底。
        /// </summary>
        /// <param name="dvMagnitude">本次所需 Δv（m/s）。</param>
        /// <param name="aMain">主网加速度（m/s²，0 = 无主网/无料）。</param>
        /// <param name="aRcs">RCS 加速度（m/s²，0 = 无 RCS/无料）。</param>
        /// <param name="mainAligned">主推轴是否在**出锥窗口**内（`DefaultThrustConeCos`，10°）
        /// —— 2026-09-19 起"能不能出力"用它；1.5° 的 `DefaultThrustAlignCos` 只管消旋。</param>
        /// <param name="rcsWindowSeconds">RCS 适用窗口（秒），与游戏侧 15.0 对齐。</param>
        public static Actuator SelectActuator(double dvMagnitude, double aMain, double aRcs,
            bool mainAligned, double rcsWindowSeconds = 15.0)
        {
            // ⚠ 2026-09-23（B5）：**规则本体已收到 `BurnExecutorPolicy`**（政策件是唯一权威；游戏侧那两处
            //   内联规则将来也调它）—— 本函数退化为"布尔判决 → 三态枚举"的翻译，行为与收敛前**逐位一致**
            //   （有等价性测试钉住：`BurnExecutorPolicyTests`）。
            if (!BurnExecutorPolicy.HasAnyActuator(aMain, aRcs)) return Actuator.None;
            return BurnExecutorPolicy.UseRcs(dvMagnitude, aMain, aRcs, mainAligned, rcsWindowSeconds)
                ? Actuator.Rcs
                : Actuator.Main;
        }

        /// <summary>
        /// 健全性判据（供探针/测试断言用）：残差是否已进入交会容差。
        /// 判据含**相位与相位速率两项**——只看位置会漏掉"同位置不同速度"。
        /// </summary>
        public static bool HasRendezvoused(double distance, double relativeSpeed,
            double distanceTol = 300.0, double speedTol = 2.0)
        {
            return distance <= distanceTol && relativeSpeed <= speedTol;
        }

        private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    }
}
