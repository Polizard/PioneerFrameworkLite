using System;
using MechJebLib.Primitives;

namespace PioneerFramework
{
    /// <summary>
    /// **近场终端交会律（位置 + 速度联合）** —— 2026-10-01 新写（用户裁定："就当看不见以前写的 CW 那个废物，
    /// 新搞一个位置+速度联合；当我们与目标的相对速度和位置达到一个粗条件后，转为启用这个律"）。
    ///
    /// ## 为什么需要它（现场，§10.30 的读数）
    /// 地球·低推力那条的距离在 **2↔21 km 反复弹**、58 次 `BLOCKED` 烧干。成对读数（点火前 → 烧完）指着同一件事：
    /// · **起飞发**（拦截）：拉近距离 ✓ 但把相对速度升到 ~100 m/s；
    /// · **到达发**（刹车）：相对速度归零 ✓ 但**位置留在 10–19 km 外** ✗（5 km → 15.6 km；2.3 km → **21.5 km**）。
    /// ⇒ 两条动作各自只优化一个量 ⇒ **拉近 ⇄ 刹车** 二元极限环。**缺的就是"同时管位置与速度"的一条律**。
    /// （旧那份 `CwRendezvousCore` 已归档、且其"沿迹短弧"适用面窄 ⇒ 按用户裁定**不复活**，本文件是新的。）
    ///
    /// ## 律的形式（双通道比例 + 自然项前馈，全在**目标的 RTN 系**里）
    /// ```
    /// ① 期望相对速度：vDes = −∂ / τ，且按接近率上限限幅（位置通道 ⇒ 也定"接近率"）
    /// ② 期望相对加速度：aDes = (vDes − δv) / τ_v
    /// ③ 抵消自然项（CW/希尔：ẍ = 3n²x + 2nẏ、ÿ = −2nẋ、z̈ = −n²z）⇒ aCmd = aDes − aNat
    /// ④ 输出本拍要烧的 Δv = aCmd·dt，并按 `MaxDvPerTick` 夹紧
    /// ```
    /// **不动点 = (∂ = 0, δv = 0)**（两船同点同速 ⇒ 自然运动保持重合 ⇒ 是平衡态）⇒ 两条通道同时收敛 ✓
    /// 带自然项前馈的理由：不做前馈时希尔项是**扰动**，闭环会有稳态偏移；做了 ⇒ 闭环≈双积分器 ⇒ 干净收敛。
    /// ⚠ `n` 用**目标当前半径**算（`n = √(μ/r³)`）——近场下与 CW 的常 `n` 一致到可忽略。
    ///
    /// ## 粗条件（进这门就切进来；带迟滞，不许在门槛上横跳）
    /// `dist ≤ GateDistance` **且** `relVel ≤ GateRelVel`（默认 50 km / 300 m/s —— 按现场那条循环的
    /// 2–21 km、10–100 m/s 定，留足余量）；**退出**用 `HysteresisFactor`（默认 2×）放宽。
    ///
    /// ## ⭐ 参数档是**量出来的**，不是拍的（`TerminalRendezvousLawTests`，两体真值，dt=2 s）
    /// | 参数档 | 径向 20 km/20 | 沿迹 20 km/100 | 混合 5 km/100 | 50 km/300 |
    /// |---|---|---|---|---|
    /// | 凶 τ=60/上限 100 | 424 s·**231/394** | 388 s·115/307 | 250 s·146/356 | 628 s·323/**999** |
    /// | 中 τ=200/上限 50 | 1092 s·129/161 | 890 s·111/109 | 878 s·133/316 | 1124 s·325/1036 |
    /// | **柔 τ=600/上限 20（本默认）** | 4438 s·**97/97** | 4100 s·**136/136** | 4532 s·**144/144** | 5778 s·389/775 |
    /// （每格 = 用时·Δv，**脉冲臂/低推力臂（0.30 m/s²）**）
    /// · **柔档下"低推力"与"脉冲"花一样多** ✓✓ —— 慢速接近不与引力梯度较劲 ⇒ **低推力档的惩罚消失**
    ///   （这正是现场失败的那一档：0.28–0.74 m/s² 的低推力船）；凶档则要贵 1.7–3 倍。
    /// · **代价是时间**（2000–5800 s）：终端从"快点到"换成"慢而省、且真能到"——按现场那条（烧干 4900 m/s、58 次 `BLOCKED`）
    ///   这笔换得值。⚠ 50 km/300 m/s 那个角仍贵（775 m/s）：**进门的相对速度**决定终端账 ⇒ 远场/拦截侧把进门速度压低才是省的关键。
    /// </summary>
    public static class TerminalRendezvousLaw
    {
        public sealed class Params
        {
            /// <summary>粗条件①：距离上限（m）。</summary>
            public double GateDistance = 50_000.0;
            /// <summary>粗条件②：相对速度上限（m/s）。</summary>
            public double GateRelVel = 300.0;
            /// <summary>迟滞：已进入后，要放大到这个倍数才退出（防门槛横跳）。</summary>
            public double HysteresisFactor = 2.0;
            /// <summary>闭环时间常数 τ（s）：位置通道的收敛尺度。**默认取"柔"档（实测更省，见类注释）**。</summary>
            public double TimeConstant = 600.0;
            /// <summary>接近率上限（m/s）：**柔档 20**（实测：上限压下来后低推力与脉冲的 Δv 相等）。</summary>
            public double ClosingSpeedCap = 20.0;
            /// <summary>速度通道时间常数（s）：默认 τ/2 ≈ 临界阻尼。</summary>
            public double VelocityTimeConstant = 300.0;
            /// <summary>收口死区（m）：距离低于它就不再追求（避免在噪声里抖）。</summary>
            public double Deadband = 20.0;
            /// <summary>**一次修正的 Δv 上限**（m/s）。⚠ 2026-10-01 现场：原版每拍只发 ~1 m/s（= `aCmd·dt`）
            /// ⇒ **低于修正烧死区 2 m/s、且被下一拍"撤回旧节点"吃掉**（实测 `已交付 0.0 m/s`）⇒ 一发没烧出去、船只是漂 ✗✗。
            /// 现在输出改成"速度待增量" ⇒ 这个上限就是一次修正的量级（10 m/s：低推力档 ~33 s 烧完，落在 60 s 节拍内 ✓）。</summary>
            public double MaxDvPerTick = 10.0;
            /// <summary>进入本律后**不再切回**的门（占 τ 的比例）：距离/速度都低于它才算"收口"。用于调用方的状态机。</summary>
            public double SettledDistance = 300.0;
            /// <summary>同 <see cref="SettledDistance" />：相对速度（m/s）。</summary>
            public double SettledRelVel = 2.0;
        }

        /// <summary>粗条件（**进入**）：距离与相对速度同时低于门。调用方负责迟滞（退出用 `× HysteresisFactor`）。</summary>
        public static bool GateMet(double dist, double relVel, Params p)
            => dist <= p.GateDistance && relVel <= p.GateRelVel;

        /// <summary>收口判据（调用方用它决定"终端完成、可以交回/结束"）。</summary>
        public static bool Settled(double dist, double relVel, Params p)
            => dist <= p.SettledDistance && relVel <= p.SettledRelVel;

        /// <summary>
        /// **本拍要施加的 Δv**（RTN 系方向：x 径向向外、y 沿迹、z 面外）。
        /// </summary>
        /// <param name="relPosRtn">目标 RTN 系的相对位置（本船 − 目标）。</param>
        /// <param name="relVelRtn">目标 RTN 系的相对速度（本船 − 目标）。</param>
        /// <param name="n">目标当前角速度（√(μ/r³)）。</param>
        /// <param name="dt">本拍时长（s）——输出按它折算成 Δv。</param>
        /// <param name="p">参数。</param>
        public static DVector3 ComputeDv(DVector3 relPosRtn, DVector3 relVelRtn, double n, double dt, Params p)
        {
            if (!(dt > 0.0) || !(n > 0.0)) return DVector3.zero;

            double d = relPosRtn.magnitude;
            double tau = Math.Max(1e-3, p.TimeConstant);
            double tauV = Math.Max(1e-3, p.VelocityTimeConstant);

            // ① 期望相对速度：位置通道按 τ 收敛；死区内不再追求位置（只把速度收干净）
            DVector3 vDes = d > p.Deadband ? relPosRtn * (-1.0 / tau) : DVector3.zero;

            // 接近率上限：把"朝目标"的那一分量限幅（横向/面外分量不限，它们本来就是收敛项）
            if (d > 0.0)
            {
                DVector3 uAway = relPosRtn / d;                       // 单位"远离目标"
                double vRadial = DVector3.Dot(vDes, uAway);           // < 0 = 正在接近
                if (-vRadial > p.ClosingSpeedCap)
                    vDes += uAway * (-p.ClosingSpeedCap - vRadial);   // 把接近分量压到上限
            }

            // ② 速度通道：以 τ_v 追期望相对速度
            DVector3 aDes = (vDes - relVelRtn) / tauV;

            // ③ 抵消自然项（希尔）：aNat = (3n²x + 2n·δv_y, −2n·δv_x, −n²z)
            double nn = n * n;
            var aNat = new DVector3(
                3.0 * nn * relPosRtn.x + 2.0 * n * relVelRtn.y,
                -2.0 * n * relVelRtn.x,
                -nn * relPosRtn.z);

            // ④ **输出 = "速度待增量"**：`(vDes − δv) − aNat·dt` = 把相对速度摆到期望值所需的那一发（再按 dt 前馈自然项）。
            //    ⚠ 为什么不是 `aCmd·dt`：终端节拍是**既有的 10 s**（`GuidanceTerminalCheckInterval`），
            //      而 `aCmd·10` ≈ 1 m/s —— **低于修正烧死区 2 m/s**（现场实测：指令下发 0、船只是漂 ✗✗）。
            //      ⇒ 指令必须是一发**能过死区**的量级；上限 10 m/s（RCS 能在 ~15 s 内烧完的那档 ✓）。
            DVector3 dv = (vDes - relVelRtn) - aNat * dt;
            double dvMag = dv.magnitude;
            if (dvMag > p.MaxDvPerTick) dv *= p.MaxDvPerTick / dvMag;
            return dv;
        }
    }
}
