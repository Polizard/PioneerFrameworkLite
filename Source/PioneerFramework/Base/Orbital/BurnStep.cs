using System;

namespace PioneerFramework
{
    /// <summary>
    /// **燃烧每步的唯一算式（纯）** —— 执行器与台架/纯测试**共用同一份**。
    ///
    /// ## 为什么要抽出来（2026-09-20，本会话血的教训）
    /// 这个子系统的每一条"执行器政策"（选网 / 方向律 / 点火门 / 油门 / 时间基）原来在**游戏执行器**里写一遍、
    /// 在**台架**里再手写一遍 ⇒ 两份注定漂移 ✗。本会话抓到的**每一个**"缺陷"，实质都是
    /// **我台架那份复刻与真代码不一致**（方向刷新节拍、方向锚点、点火门、副本/正主、执行延迟…），
    /// 而不是物理错。⇒ **把算式收成一份**：两边都调这里，"台架 vs 游戏"的差就**只可能来自环境**
    /// （轨道、调度、积分器），不可能来自模型 ✓。
    ///
    /// ## 口径（与执行器逐位一致）
    /// · 加速度：`a = aCommand>0 ? min(aCommand, aFull) : aFull`（命令超可用 ⇒ 按可用，并记日志）；
    /// · 单步 Δv：`dv = min(a·dt, remaining)`（计划完成度按"沿指令方向"计量）；
    /// · 油门份额：`dv / (aFull·dt)`（**分母是全油门** ⇒ 比例油门：推力与流率同比例下降；
    ///   整段烧完的工质与全油门逐位相同，只是烧得久 —— 火箭方程只依赖总 Δv）；
    /// · 工质：`massFlow·dt·份额`。
    /// </summary>
    public static class BurnStep
    {
        /// <summary>单步结果。</summary>
        public readonly struct Result
        {
            /// <summary>本步沿**指令方向**施加的 Δv（m/s，已按剩余量截断）。</summary>
            public readonly double Dv;
            /// <summary>本步工质消耗（kg）。</summary>
            public readonly double Propellant;
            /// <summary>本步之后的剩余计划 Δv（m/s）。</summary>
            public readonly double RemainingAfter;
            /// <summary>本步之后计划是否达成。</summary>
            public readonly bool Exhausted;
            /// <summary>油门份额（0..1）。</summary>
            public readonly double Throttle;

            public Result(double dv, double propellant, double remainingAfter, bool exhausted, double throttle)
            {
                Dv = dv; Propellant = propellant; RemainingAfter = remainingAfter;
                Exhausted = exhausted; Throttle = throttle;
            }
        }

        /// <summary>
        /// 单步推进（**唯一实现**，见类注释的口径）。
        /// </summary>
        /// <param name="aCommand">命令加速度（m/s²；&lt;=0 ⇒ 全油门，即"没有恒定加速度政策"）</param>
        /// <param name="aFull">当前**可用**全油门加速度（= ΣF/质量）</param>
        /// <param name="massFlow">当前可用总流率（kg/s）</param>
        /// <param name="dt">本步模拟秒（= UPDATE_INTERVAL_TICKS × 挡位倍率）</param>
        /// <param name="remaining">剩余计划 Δv（m/s）</param>
        public static Result Compute(double aCommand, double aFull, double massFlow, double dt, double remaining)
        {
            if (!(dt > 0.0) || !(aFull > 0.0) || remaining <= 0.0)
                return new Result(0.0, 0.0, remaining, remaining <= 0.0, 0.0);

            double a = aCommand > 0.0 ? Math.Min(aCommand, aFull) : aFull;
            double dvFull = aFull * dt;
            double dvWanted = a * dt;
            double dv = Math.Min(dvWanted, remaining);
            double throttle = dvFull > 0.0 ? dv / dvFull : 0.0;
            double propellant = massFlow * dt * throttle;
            double after = remaining - dv;
            return new Result(dv, propellant, after, after <= 1e-9, throttle);
        }

        /// <summary>
        /// **方向律的唯一算式**：计划的 RTN 分量在**当前** RTN 里取方向（"解不动、方向按时间取"）。
        /// 与 `OrbitMath.GetDeltaVSystem` 同式（后者是游戏系口径的同一件事）。
        /// ⚠ 本轮起**只服务退路律**（拿不到 `burnTargetV` 的燃烧）；有计划向量的燃烧走
        /// <see cref="DeficitToTargetV" />。
        /// </summary>
        public static DVector3 DirectionInRTN(DVector3 posMeters, DVector3 velMeters,
            double radialDv, double tangentialDv, double normalDv)
        {
            DVector3 radialDir = posMeters.normalized;
            DVector3 h = DVector3.Cross(posMeters, velMeters).normalized;
            DVector3 tangentialDir = DVector3.Cross(h, radialDir).normalized;
            DVector3 dir = radialDir * radialDv + tangentialDir * tangentialDv + h * normalDv;
            return dir.sqrMagnitude > 0.0 ? dir.normalized : dir;
        }

        /// <summary>
        /// ★★ **活差额律的唯一算式**（"MJ 优先"第 5 件，2026-09-20）—— 与 MJ 的节点执行器同一条：
        /// 把**活轨道**（已含烧掉的那部分）推进到节点 `UT`，与**目标速度**做差：
        /// <c>dv_rem = v_target(UT) − v_live(UT)</c>。**这个差额既是方向、也是剩余量**。
        ///
        /// 为什么它比"冻结的解 + 事后弧补偿 + 收口微调"精确：后者按**脉冲模型**算该给多少 Δv，
        /// **重力在燃烧窗口内的贡献**没进那一算（实测残差 5.96e-4 那笔）；活差额是在 `UT` 处
        /// **直接测量**的 ⇒ 那一项按构造消失。
        ///
        /// ⚠ **帧无关**（只在同系里做差）⇒ 游戏侧用游戏系、台架用适配层系，两边是**同一份算式**
        ///   （本会话最大的教训：两边各写一份律 ⇒ 每个"缺陷"都是复刻漂移）。
        /// ⚠ `reversed`：差额已反向（超调）⇒ 调用方**不许反向燃烧**（主引擎转不过去），就此收工 ——
        ///   这与 MJ 的几何判据 `AngleFromNode() ≥ 90°` 同义。
        /// </summary>
        /// <param name="vLiveAtUt">活轨道推进到节点 UT 的速度（与 <paramref name="vTargetAtUt" /> 同系）</param>
        /// <param name="vTargetAtUt">节点目标速度（= 点火前轨道在 UT 的速度 + 计划 Δv）</param>
        /// <param name="prevDir">上一步施加的方向（判"反向"用它，与 MJ 用推力轴同理）</param>
        /// <param name="floor">收工地板（m/s）：差额小于它即达标（末步冲量与测量之间隔着一拍框架，
        /// 残数永远差一个 ~1e-3 量级 ⇒ 地板必须非零，否则空转）</param>
        public static void DeficitToTargetV(DVector3 vLiveAtUt, DVector3 vTargetAtUt, DVector3 prevDir,
            double floor, out DVector3 dir, out double remaining, out bool reversed)
            => Deficit(vTargetAtUt - vLiveAtUt, prevDir, floor, out dir, out remaining, out reversed);

        /// <summary>
        /// ★★ **判据的唯一权威**（地板 + 反向收手）：吃"到靶还差多少 Δv"这个**差额向量**。
        /// 差额**怎么算**由调用方决定（靶是要命的东西，两种靶形态：`v(UT)` 速度靶 / `(a,h向量)` 轨道靶），
        /// 但"多大算达标、什么时候算超调"**只有这一份** —— 游戏与台架都调这里（本会话"唯一算式"纪律）。
        /// ⚠ `reversed`：差额与上一步方向反向（超调）⇒ 调用方**不许反向燃烧**（主引擎转不过去），就此收工
        ///   —— 与 MJ 的几何判据 `AngleFromNode() ≥ 90°` 同义。
        /// </summary>
        public static void Deficit(DVector3 dvRem, DVector3 prevDir, double floor,
            out DVector3 dir, out double remaining, out bool reversed)
        {
            remaining = dvRem.magnitude;
            reversed = false;
            dir = DVector3.zero;
            if (!(remaining > floor) || !(remaining > 0.0))
            {
                remaining = 0.0;   // 达标（或退化输入）⇒ 收工
                return;
            }
            if (DVector3.Dot(dvRem, prevDir) < 0.0)
            {
                reversed = true;   // 超调 ⇒ 差额反向 ⇒ 收工（不做反向燃烧）
                remaining = 0.0;
                return;
            }
            dir = dvRem * (1.0 / remaining);
        }

        /// <summary>
        /// ★★ **节点窗口的截止时刻**（唯一算式；`SpaceGameComp` 的窗口闸与纯测试共用这一份）。
        ///
        /// 燃烧以节点为中心 ⇒ 合法执行区间是 `[UT − 燃时/2, UT + 燃时/2]`；这条给出**右端 + 宽限**。
        /// 活差额律的收手判据（<see cref="Deficit" /> 的地板与反向）**都只看差额本身**，
        /// 没有一条关于节点时刻 ⇒ 节点作废之后两条都可能永不触发（2026-09-21 实测：烧 9080s / 4412 m/s，
        /// 计划只有 431.7，轨道被毁）。这条是**第三条判据：关于时刻的**。
        ///
        /// ⚠ 宽限必须够大，让**合法但晚点**的燃烧不被误杀：
        ///   · 点火前等对准（上游纪律，宁可晚）⇒ 只能烧满名义燃时/2；
        ///   · RCS 起步/换网 ⇒ 实际燃时可能长于名义值。
        ///   420 s 的取值来自实测：同轨 30° 快例的调相发名义燃时 ≈124–150 s ⇒ 截止在 UT+470 s 开外，
        ///   而现场那发烧了 **9329 s** —— 相差 20 倍，判据不会误杀也不会漏抓。
        /// </summary>
        /// <param name="nodeUt">节点时刻（模拟秒）</param>
        /// <param name="burnSeconds">名义燃时（= 计划 Δv / 命令加速度；0 = 未知 ⇒ 只给宽限）</param>
        /// <param name="graceSeconds">窗口右端之后的宽限（模拟秒）</param>
        public static double NodeWindowDeadline(double nodeUt, double burnSeconds, double graceSeconds)
            => nodeUt + Math.Max(burnSeconds, 0.0) / 2.0 + Math.Max(graceSeconds, 0.0);

        /// <summary>节点窗口是否已过（=<paramref name="tick" /> 在截止之后）⇒ 调用方应**停火**。</summary>
        public static bool NodeWindowExpired(double tick, double nodeUt, double burnSeconds, double graceSeconds)
            => tick > NodeWindowDeadline(nodeUt, burnSeconds, graceSeconds);
    }
}
