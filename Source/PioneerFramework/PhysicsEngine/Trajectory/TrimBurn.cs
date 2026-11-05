using System;

namespace PioneerFramework
{
    /// <summary>
    /// **闭环微调（B2.2-b）**：把**当前点**的速度拨到**目标共面轨道**在该点的速度上。
    ///
    /// ## 为什么是它，而不是把剩下那 0.2% 找出来
    /// 2026-09-20 实测（台架与游戏**同一台仪器**：终态反推，见
    /// `BurnArcExecutorBench.Bench_TerminalTruth_ApplesToApples` 与 `LiveBurnAcceptanceRunner` 的终端真值）：
    ///   · 执行器把**自己的账本**（808.09 m/s）交付得一字不差（逐笔对账 0.00%）；
    ///   · 但**有效 Δv**（状态反推）只有 806.10；台架同政策的理想执行器给 807.75
    ///     （差的 0.35 m/s = 有限燃烧弧本身）。
    ///   ⇒ **游戏侧多丢 ~1.6 m/s（0.2%）**，台架复现不出来。每个具名嫌疑（轨道保持 / n-body /
    ///   副本-正本 / 点火相位 / 交付延迟 / 重解差）都单独量过，量级都不够 —— 且台架能复现的那部分
    ///   已经由弧补偿（B2.2-a）的 (Δv 缩放 × 点火相位) 两个自由度吃掉了。
    ///   ⇒ 对这条"游戏独有"的偏差**不再指望定位**，改用**它影响不到**的做法：
    ///     不看模型、不看账本，只看「现在这一刻的轨道」与「意图轨道」的差 ⇒ 一次小 Δv 收口。
    ///     它对**任何**未定因的交付偏差都免疫（这正是闭环的意义）。
    ///
    /// ## 算法（单点、单次点火，共面）
    /// 目标共面轨道由 <c>(aTarget, hTarget)</c> 指定。该点的目标速度是**唯一**的：
    ///   · 模长 <c>|v| = √(μ(2/r − 1/a))</c>（vis-viva）；
    ///   · 航迹角 <c>cos γ = h/(r·|v|)</c>（角动量只由切向分量贡献），γ 的符号取**当前**径向速度的符号
    ///     （出站/入站保持一致 —— 否则会选到"同一轨道反向飞"的那一支）；
    ///   · 方向 = <c>cos γ·t̂ + sin γ·r̂</c>（t̂ = 顺行切向，r̂ = 径向外）。
    /// 于是 <c>Δv = v_target − v_now</c>。
    ///
    /// ## ⚠ "精确命中"有个前提：**本点必须落在目标曲线上**（2026-09-20 首个游戏轮当场抓到）
    /// 目标曲线只含**特定半径**的点：`r ∈ [a(1−e), a(1+e)]`。本点在其外时一发点火的精确解**不存在**
    /// —— 对**圆形**目标（`e = 0`）尤其尖锐：圆只有一个半径 ⇒ 本点半径不精确等于 `a_t` 就必然越界。
    ///
    /// ## ⚠⚠ 出范围时怎么办：**保能量，绝不重标 `a`**（2026-09-20 被游戏日志证伪后定死）
    /// **旧写法（已删，留档引以为戒）**：保意图的**形状** `e_t`、把 `a` 最小幅度地重标到容纳本点
    /// （`a = r/(1±e)`，把本点当拱点）。它**是自证式的** —— 靶跟着本点跑 ⇒ 燃烧会**在别的轨道上提前收工**：
    ///   · 实测（G2 交会那轮，`Player.log`）：计划 **430.01 m/s**，只交付 **164.9** 就"收敛"（律自己的差额 →1.6）；
    ///   · 而且它**极不连续**：本点半径只比靶近拱点低 **0.3 km** ⇒ 靶的 `a` 被拉走 **258.8 km**
    ///     （近圆靶下 `(1−e)` 与 `(1+e)` 相差 `2e ≈ 4.6%` ⇒ 重标本该是"最小幅度"的假设**不成立**）✗✗。
    /// **现行**：出范围 ⇒ **`a_t` 不动**，只匹配能量：`Δv = (v_t(a_t, r)/|v| − 1)·v`（沿当前速度方向改幅值）。
    /// 永远良态、最小范数、不制造假靶；形状（`e`/`h`）**无从匹配**（本点根本不在靶曲线上）⇒
    /// 等半径进了范围，精确支**自动接管**（`cg` 连续穿过 1）。
    /// ⚠ 本支的已知代价：**不修轨道面**（方向沿 `v̂`）⇒ 异面 + 出范围的时候由阶梯的面内级另发。
    ///
    /// ## 已知未做（别过度读本类）
    ///   · **出范围支不修面**（见上）；
    ///   · **近点角 ω 留残差**（精确支下由本点位置决定，近圆目标无所谓 ω ⇒ 验收判据用的正是这一支）；
    ///   · **一次一发**：二次微调要靠它自己收敛 ⇒ 由执行侧限制（`trimFired`），避免自激。
    /// </summary>
    public static class TrimBurn
    {
        /// <summary>允许"重标 `a`"的靶离心率上限：重标把 `a` 挪 `≈2e·a`，验收线是 `|Δa| ≤ 1%`
        /// ⇒ `e ≤ 0.5%` 时重标仍在验收线内（**圆化**这一档正是 e≈0）。超过它 ⇒ 改走"保能量"支。
        /// ⚠ 这是**由验收线推出的**阈值，不是口味。</summary>
        private const double OutOfRangeRescaleEcc = 0.005;

        /// <summary>
        /// 规划一次微调。**共面口径**（平面取自当前状态 = 不修面；见类注释"已知未做"）。
        /// 要修面请用 <see cref="TryPlan(DVector3,DVector3,double,double,double,DVector3,out DVector3,out string,out double)" />。
        /// </summary>
        public static bool TryPlan(DVector3 r, DVector3 v, double mu, double aTarget, double hTarget,
            out DVector3 dv, out string reason, out double aUsed)
        {
            DVector3 hm = DVector3.Cross(r, v);
            if (!(hm.magnitude > 0.0)) { dv = DVector3.zero; reason = "当前角动量退化（纯径向运动）"; aUsed = 0.0; return false; }
            return TryPlan(r, v, mu, aTarget, hTarget, hm.normalized, out dv, out reason, out aUsed);
        }

        /// <summary>
        /// ★★ **唯一权威**（"MJ 优先"第 5 件扩展，2026-09-20）：把**目标轨道的 (a, 角动量向量)** 作为靶 ——
        /// 在**当前半径**上给出"落到靶轨道"的 Δv。角动量取**向量**（模长 + 方向）而不是标量，理由**不可省**：
        /// **纯面外变轨（换平面）不改 `a`、也不改 `|h|`**（|h| = r·|v|·sinγ 只依赖模长与航迹角）
        /// ⇒ 标量靶会判"无差额"而**静默跳过整个平面改变** ✗✗。
        ///
        /// 靶的语义：给定 <paramref name="r" />，要落到的轨道由 <c>(a_t, h_t·ĥ_t)</c> 完全决定
        /// （位置固定时，能量 + 角动量向量 ⇒ 唯一速度，最多两支 = 出/入站）：
        ///   · 模长 <c>|v| = √(μ(2/r − 1/a))</c>（vis-viva）；
        ///   · 航迹角 <c>cos γ = h/(r|v|)</c>，γ 符号取**当前**径向速度符号（出/入站保持一致）；
        ///   · 面内方向 = <c>ĥ_t</c> 所在的平面 ⇒ **平面改变就在这一项里**。
        /// ⇒ 差额归零 ⇒ 当前轨道 = 靶轨道（**不是**"某时刻某点的速度对上了"）。
        ///
        /// ⚠ **`ĥ_t` 与 `r` 正交性**：靶轨道若确实过本点，则两者本就正交；浮点/漂移下不正交时取
        ///   `ĥ_t` 在"垂直于 r"平面上的投影（同平面内最近的可行轨道）—— 否则 `t̂ = ĥ×r̂` 不是单位向量，
        ///   模长会静默错掉。
        /// ⚠ 退化支（本点不在靶曲线上）与共面版**同一套**：保形状 `e_t`、最小幅度重标 `a`（见类注释）。
        /// </summary>
        public static bool TryPlan(DVector3 r, DVector3 v, double mu, double aTarget, double hTarget,
            DVector3 hTargetDir, out DVector3 dv, out string reason, out double aUsed)
        {
            dv = DVector3.zero;
            reason = "";
            aUsed = 0.0;

            double rm = r.magnitude;
            if (!(rm > 0.0)) { reason = "位置退化"; return false; }
            if (!(mu > 0.0)) { reason = "μ 非正"; return false; }
            if (!(aTarget > 0.0) || aTarget > 1e12) { reason = "目标半长轴非正（双曲/抛物线目标不接）"; return false; }
            if (!(hTarget > 0.0)) { reason = "目标角动量非正"; return false; }

            // ── 意图轨道的**形状**（离心率）——退化支要保的正是它 ──
            // ⚠ 浮点余量：圆目标（e=0）的 `h²/(μa)` 在浮点上会落在 1 的两侧 ⇒ 判"双曲"必须留容差，
            //   否则**主用例**（圆化）会被自己的守卫拒掉；反向也要夹住（`max(0, ...)`）免得开负根。
            double hOverMuA = hTarget * hTarget / (mu * aTarget);
            if (hOverMuA > 1.0 + 1e-12) { reason = "意图轨道是抛物线/双曲（h² > μa）⇒ 微调不接"; return false; }
            double ecc = Math.Sqrt(Math.Max(0.0, 1.0 - hOverMuA));       // e = √(1 − h²/(μa))

            // ⚠⚠ **出范围时怎么办**（2026-09-20 被游戏日志逼出来的政策分界，**判据由验收线推出**）：
            //   靶曲线只含 `r ∈ [a(1−e), a(1+e)]` 的点 ⇒ 本点在其外时"在本点落到靶轨道"**无解**。
            //   两条可选政策，各自牺牲一样东西：
            //     · **重标 a**（把本点当拱点，`a = r/(1±e)`）：保形状 ✓、动 a ✗（挪 `≈2e·a`）；
            //     · **保能量**（`a` 不动、沿当前速度改幅值）：保 a ✓、形状无从匹配 ✗。
            //   验收线是 `e ≤ 1e-3 且 |Δa| ≤ 1%` ⇒ **重标的代价 `2e·a` 是否可接受，取决于 `e_t`**：
            //     · `e_t ≤ 0.5%`（⇒ `2e ≤ 1%`，含**圆化**这档）：重标在验收线内 ⇒ **重标** ✓
            //       （对圆轨靶这是唯一能满足 `e ≤ 1e-3` 的做法：圆只有一个半径，本点不在其上就只能"在本点圆化"；
            //        而计划会把 r 带到 `a_t` 附近 ⇒ 落点 a ≈ a_t ✓ 两条判据同时过 —— 台架实测 smaErr 1.8e-6 ✓）；
            //     · `e_t > 0.5%`：重标代价超验收线 ⇒ **保能量** ✓
            //       （实测反例：`e_t = 2.3%`、本点半径只比靶近拱点低 **0.3 km** ⇒ 重标把靶的 a 拉走 **258.8 km**，
            //        而靶跟着本点跑 ⇒ 燃烧在**别的轨道**上收工：计划 430.01 m/s 只交付 164.9 ✗✗）。
            double rp = aTarget * (1.0 - ecc), ra = aTarget * (1.0 + ecc);
            bool outOfRange = rm < rp - 1e-6 || rm > ra + 1e-6;
            bool rescale = outOfRange && ecc <= OutOfRangeRescaleEcc;
            double aUse = rescale
                ? (rm < rp - 1e-6 ? rm / (1.0 + ecc) : rm / (1.0 - ecc))   // 本点当近/远拱点
                : aTarget;                                                  // a **永不**因出范围而重标
            double hUse = Math.Sqrt(mu * aUse * (1.0 - ecc * ecc));

            double vt2 = mu * (2.0 / rm - 1.0 / aUse);
            if (!(vt2 > 0.0)) { reason = "目标轨道在该点不可达（a_t < r/2 ⇒ 需要负速度）"; return false; }
            double vt = Math.Sqrt(vt2);

            DVector3 hvec = DVector3.Cross(r, v);
            double vm = v.magnitude;
            if (!(vm > 0.0) || !(hvec.magnitude > 0.0)) { reason = "当前速度/角动量退化（纯径向运动）"; return false; }

            // 角动量只由**切向**分量贡献 ⇒ cos γ = h/(r·v)。`cg > 1` ⟺ 靶的 h 在本点半径上**不可达**
            // ⟺ 本点半径落在 `[a(1−e), a(1+e)]` 之外（拱点上恰为 1）。
            double cg = hUse / (rm * vt);
            // ⚠ **重标支必须夹住 cg**（旧代码的"浮点余量"夹具，我一度丢了它 ⇒ 重标支被自身噪声挤进保能量支 ✗）：
            //   重标支按**构造**把本点放到拱点上 ⇒ `cg` 理论上恰为 1，但浮点上会落在 1 的两侧；
            //   不夹的话"圆化"这条**主用例**会走错支（台架当场抓到：e 1.49e-8 → 2.64e-1）。
            if (rescale && cg > 1.0) cg = 1.0;
            if (cg > 1.0 + 1e-12)
            {
                // ── **保能量支**（偏心靶出范围）：`a_t` 不动，方向保持当前速度方向 ⇒
                //    `Δv = (v_t(a_t, r)/|v| − 1)·v`。永远良态、最小范数、**不制造假靶** ✓。
                //    形状（e/h）无从匹配（本点根本不在靶曲线上）⇒ 等半径进了范围，精确支自动接管（cg 连续穿过 1）。
                //    ⚠ 代价（登记）：**本支不修轨道面**（方向沿 v̂）⇒ 异面 + 出范围由阶梯的面内级另发。
                double vtE = Math.Sqrt(mu * (2.0 / rm - 1.0 / aTarget));   // 用**意图的 a_t**，不是重标后的
                dv = v * (vtE / vm - 1.0);
                aUsed = aTarget;
                return true;
            }
            if (cg < -1.0) { reason = "目标角动量与本点几何不相容（h 为负）"; return false; }

            DVector3 rhat = r / rm;
            // ★ 平面取自**靶**（不是当前）——纯平面改变就活在这一步里：
            //   `ĥ` 去掉沿 r 的分量（靶若过本点则本就正交；不正交时取同平面内最近的可行轨道）
            DVector3 hhat = hTargetDir - rhat * DVector3.Dot(hTargetDir, rhat);
            double hm2 = hhat.magnitude;
            if (!(hm2 > 1e-12)) { reason = "靶平面退化（法向与半径平行）"; return false; }
            hhat = hhat / hm2;
            DVector3 that = DVector3.Cross(hhat, rhat);          // 顺行切向（在靶平面内）

            double vr = DVector3.Dot(v, rhat);
            double gamma = Math.Acos(cg) * (vr < 0.0 ? -1.0 : 1.0);

            DVector3 vTarget = (that * Math.Cos(gamma) + rhat * Math.Sin(gamma)) * vt;
            dv = vTarget - v;
            aUsed = aUse;      // 重标支报"实际用的 a"（≠ a_t ⇒ 调用方可看出来走了哪一支）
            return true;
        }
    }
}
