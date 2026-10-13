using System;

namespace PioneerFramework
{
    /// <summary>
    /// 爬升段制导参数（设计 §4.4 冻结初值）。纯数据，可序列化/可单测。
    /// </summary>
    public struct LaunchGuidanceParams
    {
        /// <summary>目标圆轨半径 (m)。</summary>
        public double rTarget;
        /// <summary>径向收拢时间常数 τ (s)。</summary>
        public double tau;
        /// <summary>径向收拢速度上限 vRadMax (m/s)，控重力损失。</summary>
        public double vRadMax;
        /// <summary>死区：|dv| 小于此值则不烧 (m/s)。</summary>
        public double deadband;
        /// <summary>到达半径窗口 tolR (m)。</summary>
        public double tolR;
        /// <summary>到达速度窗口 tolV (m/s)。</summary>
        public double tolV;
        /// <summary>发射天体半径 (m)，用于换算高度（竖直起飞段判定）。</summary>
        public double planetRadius;
        /// <summary>竖直起飞段目标爬升速度 vAscent (m/s)。</summary>
        public double vAscent;
        /// <summary>竖直起飞段结束高度（相对表面）(m)。</summary>
        public double altPitchStart;
        /// <summary>倾倒完成高度（相对表面）(m)：此高度后切到纯追踪 vC。</summary>
        public double altPitchEnd;
        /// <summary>
        /// 切向方向符号：+1 = up×radial（引擎旧约定），−1 = radial×up（与行星真实自转同向）。
        /// 2026-09-05：自转方向由 RimWorld 太阳约定锚定（固连系太阳星下点西移 ⇒ ECI 自转 Ry(−ωt)），
        /// 表面切向 = ω·(p×up) = radial×up → 顺行发射必须取 −1，
        /// 否则控制律会先把自转初速刹掉再反向加速（白烧 ~2×v_surface ≈ 1048 m/s 赤道）。
        /// ⚠ 2026-10-06 起只在**退化兜底**分支里用（角动量可忽略时）；正常路径的切向基准是面内 `ĥ×r̂`。
        /// </summary>
        public double progradeSign;

        /// <summary>
        /// **径向通道增益**（2026-10-06）：1 = 一步把误差全交付（现状/默认）；&lt;1 = 一阶趋近。
        /// **≤0 视为 1**（旧档/未配置 = 直通，不做迁移）。
        /// ⚠ 它是对"制导输入缺在飞冲量"那个病灶的**半剂量补丁**；根因修好之后（见
        /// `RocketLaunchController.GuidanceVelocity`）增益**无益、Fast 下反而有害** ⇒ 默认 1.0。
        /// 字段保留：台架用它做对照整定（`AscentTuningTests`）。
        /// </summary>
        public double radialGain;

        /// <summary>**切向通道增益**（同上；≤0 视为 1）。默认 0.5。</summary>
        public double tanGain;

        /// <summary>设计 §4.4 冻结默认参数，rTarget = planetRadius + 500km。</summary>
        public static LaunchGuidanceParams CreateDefault(double planetRadiusMeters)
        {
            return new LaunchGuidanceParams
            {
                rTarget = planetRadiusMeters + 500000.0,
                tau = 600.0,
                vRadMax = 300.0,
                deadband = 0.5,
                tolR = 10000.0,
                tolV = 10.0,
                planetRadius = planetRadiusMeters,
                vAscent = 500.0,
                altPitchStart = 20000.0,
                altPitchEnd = 450000.0,
                progradeSign = -1.0, // 顺行 = 与真实自转（radial×up）同向
                // 增益默认 = 1.0（直通）。2026-10-06 台架实测：**真正治本的是"制导输入含在飞冲量"**
                // （`RocketLaunchController.GuidanceVelocity`）；换成它之后增益**无益、Fast 下反而有害**
                // （dt=8/延1：预测+增益0.3/0.5 = 1676 kg，预测+直通 = **2222 kg**）。字段保留供台架/整定。
                radialGain = 1.0,
                tanGain = 1.0
            };
        }
    }

    /// <summary>单步制导输出：dv 指令（m/s）+ 到达标记。</summary>
    public struct LaunchGuidanceStep
    {
        public DVector3 dv;
        public bool arrived;

        public LaunchGuidanceStep(DVector3 dv, bool arrived)
        {
            this.dv = dv;
            this.arrived = arrived;
        }
    }

    /// <summary>
    /// 爬升段制导纯数学核心（设计 §4.2）。无记忆：每步只由 (pos, vel, GM, 参数) 决定。
    /// 控制律 = 目标圆轨速度 + 径向收拢，重力转弯为涌现行为，不显式编俯仰程序。
    /// 控制器无积分器/历史，载入存档不漂移。
    /// </summary>
    public static class LaunchGuidanceCore
    {
        /// <summary>
        /// 计算本物理步的期望速度修正 dv 与到达标记。
        /// 控制律 = 显式俯仰程序（重力转弯近似）：
        ///   ① 低空竖直起飞段：desiredVel = 径向·vAscent + 切向·当前切向速度（全量爬升对抗重力）
        ///   ② 倾倒段：随高度线性把 竖直目标 渐变到 追踪 vC（圆轨速度 + 径向收拢）
        ///   ③ 高空：纯追踪 vC（原控制律）
        /// 原因：纯追踪 vC 时 dv 方向 ≈ 96% 切向，径向推力分量（&lt; 1 m/s²）压不住重力（8 m/s²），
        /// 低 TWR 下火箭只会贴地表水平加速坠落，永不竖直爬升（游戏日志实证 r 5010→4969km）。
        /// </summary>
        /// <param name="pos">ECI 位置 (m)，原点 = 行星中心。</param>
        /// <param name="vel">ECI 速度 (m/s)。</param>
        /// <param name="gm">中心天体引力参数 GM (m³/s²)。</param>
        /// <param name="p">冻结参数。</param>
        public static LaunchGuidanceStep ComputeStep(DVector3 pos, DVector3 vel, double gm, in LaunchGuidanceParams p)
        {
            double r = pos.magnitude;
            if (r < 1e-3 || gm <= 0.0 || p.rTarget <= 0.0)
                return new LaunchGuidanceStep(DVector3.zero, false);

            double vC = Math.Sqrt(gm / p.rTarget);   // 目标圆轨速度（常数）
            DVector3 radialOut = pos / r;

            // ── 切向基准（**必须在轨道面内**；2026-10-06 修，台架定案）──────────────────────
            // 旧写法 = 全局东方 `Cross(up, r̂)`：**赤道**发射时它恰在轨道面内（所以赤道测试一直全绿），
            // 但在**倾斜轨道**上它会**漂出轨道面** ⇒ 制导每步都命令一个面外切向修正 ⇒ 燃料烧在改面上。
            // 台架实测（φ=51.7°、50 t、同一枚测试火箭）：旧写法=烧干 50 t、耗尽于 197 km、apo 只到 5368 km
            // （**与游戏日志同步复现**：耗尽 175–200 km / v=6042 / apo≈5351）；换成面内基准后**到达且余 2.4 t**。
            // 现写法 = **面内顺行水平** `ĥ×r̂`（**无记忆**：只由当前 pos/vel 定 ⇒ 读档不漂，与"控制器无积分器"一致）。
            // 退化兜底（角动量可忽略：纯径向/零速——`|v_tan| ≤ 1 m/s`）⇒ 回退旧式全局东方 × `progradeSign`。
            DVector3 hVec = DVector3.Cross(pos, vel);
            DVector3 tangent;
            if (hVec.sqrMagnitude > r * r)
                tangent = DVector3.Cross(hVec, radialOut).normalized;
            else
                tangent = DVector3.Cross(DVector3.up, radialOut).normalized * p.progradeSign;
            if (tangent.sqrMagnitude < 1e-12)
                return new LaunchGuidanceStep(DVector3.zero, false);

            double deltaR = p.rTarget - r;                       // >0 在目标半径内侧
            double radialSpeed = Clamp(deltaR / p.tau, -p.vRadMax, p.vRadMax);
            double vTan = DVector3.Dot(vel, tangent);            // 当前切向速度（竖直段保持，防突变）

            // 目标速度 = 径向分量 + 切向分量（按高度分段）
            double alt = r - p.planetRadius;                     // 相对表面高度
            double radialTarget;
            double tanTarget;
            if (alt < p.altPitchStart)
            {
                // ① 竖直起飞段：全量径向爬升，切向保持现状
                radialTarget = p.vAscent;
                tanTarget = vTan;
            }
            else if (alt < p.altPitchEnd)
            {
                // ② 倾倒段：径向 vAscent→收拢项，切向 vTan→vC，随高度线性过渡
                double t = Clamp((alt - p.altPitchStart) / (p.altPitchEnd - p.altPitchStart), 0.0, 1.0);
                radialTarget = p.vAscent * (1.0 - t) + radialSpeed * t;
                tanTarget = vTan * (1.0 - t) + vC * t;
            }
            else
            {
                // ③ 高空纯追踪 vC
                radialTarget = radialSpeed;
                tanTarget = vC;
            }

            DVector3 desiredVel = radialOut * radialTarget + tangent * tanTarget;
            DVector3 dv = desiredVel - vel;

            // ── 通道增益（2026-10-06 抗延迟；见 `radialGain` 字段的台架证据）──────────────────
            // 目标速度只含径向/切向两个分量 ⇒ 重新合成无损（只在浮点上差最后几位）。
            // ⚠ **≤0 视为 1**：旧档/未配置 = 旧行为（不做存档迁移）。
            double gRad = p.radialGain > 0.0 ? p.radialGain : 1.0;
            double gTan = p.tanGain > 0.0 ? p.tanGain : 1.0;
            dv = radialOut * (DVector3.Dot(dv, radialOut) * gRad)
               + tangent * (DVector3.Dot(dv, tangent) * gTan);

            // 到达判定：窗口内才切给入轨（§4.2/§4.5）
            bool arrived = Math.Abs(deltaR) < p.tolR && Math.Abs(vel.magnitude - vC) < p.tolV;

            if (dv.magnitude < p.deadband)
                return new LaunchGuidanceStep(DVector3.zero, arrived); // 不烧，但窗口内仍上报到达

            return new LaunchGuidanceStep(dv, arrived);
        }

        private static double Clamp(double v, double min, double max)
            => v < min ? min : (v > max ? max : v);
    }
}
