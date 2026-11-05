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
        /// 切向方向符号：+1 = up×radial（引擎旧约定），−1 = radial×up（与行星真实自转同向）。        /// 2026-09-05：自转方向由 RimWorld 太阳约定锚定（固连系太阳星下点西移 ⇒ ECI 自转 Ry(−ωt)），
        /// 表面切向 = ω·(p×up) = radial×up → 顺行发射必须取 −1，
        /// 否则控制律会先把自转初速刹掉再反向加速（白烧 ~2×v_surface ≈ 1048 m/s 赤道）。
        /// ⚠ 2026-10-06 起只在**退化兜底**分支里用（角动量可忽略时）；正常路径的切向基准是面内 `ĥ×r̂`。
        /// </summary>
        public double progradeSign;

        /// <summary>
        /// **天体自转角速度 (rad/s)**（2026-10-29）：竖直段的"速度到"要用**地面相对速度**判
        /// （MechJeb 的 `VesselState.SpeedSurface`），不能用惯性速度 ——
        /// 地球赤道自转 523 m/s &gt; `vAscent`(500) ⇒ 低纬度发射一出生就判"速度到"，
        /// 竖直段一步都没走就切转平，指令偏开 36° ⇒ 预对准门挡点火 ⇒ 直接摔
        /// （双体 20 发实测：±7° 那两发坠毁、±47°/±60° 正常）。
        /// `≤0` ⇒ 无自转信息，退回惯性速度（旧行为）。
        /// </summary>
        public double planetOmega;

        // ── 2026-10-28：**照 MechJeb 经典上升重写控制律**（用户令"一口气做完"）──────────────
        // MJ 的骨架（`MechJebModuleAscentClassicAutopilot`）：
        //   ① 竖直段：油门**恒 1.0**，机头朝上；结束条件 = **高度到 OR 速度到，先到者**；
        //   ② 重力转弯：**开环俯仰剖面** `FPA = 90° − ((alt−turnStart)/(turnEnd−turnStart))^shape × (90° − turnEndAngle)`；
        //      油门 **闭环到远地点**（`ThrottleToRaiseApoapsis`），而不是闭环到速度；
        //   ③ 滑行（远地点够了就松油门）→ 圆轨化。
        // 旧律的病根：输出的是"速度目标"，竖直段一追上目标 `dv≈0` ⇒ 油门掉到 37% ⇒ 重力压过 ⇒ 摔
        // （游戏实测两发：峰值 17.2 km、`aCmd=8.13 份额=0.366`、切向指令恒 0）。

        /// <summary>转平剖面的形状指数（MJ `TurnShapeExponent`，默认 2）。</summary>
        public double turnShapeExponent;
        /// <summary>转平终点的飞行路径角（度，0 = 水平；MJ `TurnEndAngle`）。</summary>
        public double turnEndAngleDeg;
        /// <summary>油门闭环远地点的比例带 (m)：远地点距目标小于它时按比例收油门。</summary>
        public double apoThrottleBandM;

        /// <summary>
        /// **收尾的远地点收油带 (m)**（2026-10-28 用户裁定 B）：远地点进入 `rTarget ± band` 就**停手上锁**，
        /// 交给"滑行到顶 + 一次圆化"。`≤0` ⇒ 自动 `max(2×tolR, 0.005×rTarget)`（地球 ≈ 27 km）。
        /// </summary>
        public double apoCutBandM;

        /// <summary>
        /// **最小点火量 (m/s)**（默认 2.0）：小于它的修正量不值得点一次火 —— 直接放弃本步。
        /// 台架实测：末段律会输出 0.5–0.6 m/s 的微修正，与"收油"判据交替触发 ⇒ 数出 160+ 个燃烧段
        /// （不是"一次反转"）。这些量对轨道的影响小于积分漂移，丢掉即可，由圆化一次补足。
        /// </summary>
        public double minBurnDv;

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
                altPitchStart = 20000.0,   // 必须高于发射点（火箭在 10 km 生成）；见 ForBody 注释
                altPitchEnd = 85000.0,     // MJ `AutoTurnEndAltitude` = 0.85×大气（地球 85 km）
                turnShapeExponent = 0.4,   // ★ MJ 默认 0.4：前 10% 就转掉约 40%，不是慢慢转
                progradeSign = -1.0, // 顺行 = 与真实自转（radial×up）同向
                // `CreateDefault` 是**地球基准**（台架默认用它）⇒ 按地球自转 1 天 = 60000 tick 给 ω。
                // 生产路径不吃这个值：控制器每步从天体写 `prms.planetOmega`。
                planetOmega = 2.0 * Math.PI / 60000.0,
                turnEndAngleDeg = 0.0,
                apoThrottleBandM = 50_000.0,
                // 增益默认 = 1.0（直通）。2026-10-06 台架实测：**真正治本的是"制导输入含在飞冲量"**
                // （`RocketLaunchController.GuidanceVelocity`）；换成它之后增益**无益、Fast 下反而有害**
                // （dt=8/延1：预测+增益0.3/0.5 = 1676 kg，预测+直通 = **2222 kg**）。字段保留供台架/整定。
                radialGain = 1.0,
                tanGain = 1.0
            };
        }

        /// <summary>
        /// **按天体生成制导参数**（2026-10-27）：取代"只给半径"的 `CreateDefault`。
        /// 目标高度由 `LaunchTargeting` 夹取（SOI/邻居/大气），不可行 ⇒ 返回 `default`（`rTarget = 0`），
        /// 调用方必须先看 `target.Feasible` 再决定是否发射。
        ///
        /// 参数标定：以地球（R=5,000 km、vC=6,030）为 1.0 基准，按 `√(R/R₀)` 缩放时间常数；
        /// `vAscent`/`vRadMax` 取 vC 的比例；俯仰起点 = **0.1×大气厚度**（真空天体 = 0.02R），
        /// 终点 = 0.9×目标高度。
        /// ⚠ 2026-10-28：起点从 0.2×（地球 20 km）改成 0.1×（10 km）——低推重比火箭（TWR≈2）在 20 km 之前
        /// 就把径向速度顶到 `vAscent`(500)，竖直段随即开始**主动减速**（`aCmd=8.13 份额=0.366`），
        /// 重力压过 ⇒ 到不了转平高度 ⇒ 永远进不了转平段（实测峰值 17.2 km、切向指令恒 0、直接掉回来）。
        /// </summary>
        public static LaunchGuidanceParams ForBody(in LaunchTargetInputs x, out LaunchTargetResult target)
        {
            target = LaunchTargeting.Compute(x);
            if (!target.Feasible) return default;

            double R = x.Radius;
            double atmo = x.AtmosphereThickness > 0.0 ? x.AtmosphereThickness : 0.0;
            double h = target.TargetAltitude;
            double vC = target.VCircular;
            // 竖直段起点：**只到"清空发射台"的高度**（2026-10-29 重写后的逻辑）。
            // 旧的 `0.2×大气`（地球 20 km）在"满油门竖直"下会把径向速度烧到 350+ m/s，
            // 之后亚轨道抛射的远地点先撞到目标 ⇒ 收油过早、到顶 |v| 只有 27 m/s（月球实测）。
            // 竖直段存在的唯一理由是离地，所以取 1 km（有大气）/ 0.002R（真空，月球 3 km）。
            // 必须高于发射点：火箭在 **10 km** 出生 ⇒ 出生即越过 1 km ⇒ 直接进转平段（正确）。
            double pitchStart = 1_000.0;
            // 转平终点：MJ `AutoTurnEndAltitude` = min(0.85×大气厚度, 0.85h)；真空天体 = 0.5×目标高度。
            double pitchEnd = atmo > 0.0
                ? Math.Min(0.85 * atmo, Math.Max(pitchStart + 1.0, 0.85 * h))
                // 真空：转平窗口要**短**（2026-10-29 台架：0.5h = 75 km 时月球上到 10 km 还 50° 仰角，
                // 径向速度把远地点顶到 2120 km ⇒ 冲过目标 620 km）。取 0.1h（月球 15 km）。
                : Math.Max(pitchStart + 1_000.0, 0.1 * h);

            return new LaunchGuidanceParams
            {
                rTarget = target.TargetRadius,
                tau = 600.0 * Math.Sqrt(R / 5_000_000.0),
                vRadMax = Math.Clamp(0.05 * vC, 50.0, 300.0),
                deadband = 0.5,
                tolR = Math.Max(10_000.0, 0.002 * R),
                tolV = Math.Max(10.0, 0.0015 * vC),
                planetRadius = R,
                vAscent = Math.Clamp(0.083 * vC, 30.0, 500.0),
                altPitchStart = pitchStart,
                altPitchEnd = pitchEnd,
                progradeSign = -1.0,
                turnShapeExponent = 0.4,     // ★ MJ 默认（0.4）：转平"前快后慢"
                turnEndAngleDeg = 0.0,
                apoThrottleBandM = 50_000.0,
                radialGain = 1.0,
                tanGain = 1.0,
            };
        }
    }

    /// <summary>单步制导输出：**方向 + 油门**（执行器直接用 `dv`）+ 相位 + 到达标记。</summary>
    public struct LaunchGuidanceStep
    {
        public DVector3 dv;
        public bool arrived;
        /// <summary>本步所在相位（0=竖直，1=转平，2=滑行）——控制器把它写回状态。</summary>
        public int phase;
        /// <summary>油门 0..1（诊断/台架判据用；`dv` 已经等于"方向×油门×可用加速度×dt"）。</summary>
        public double throttle;
        /// <summary>本步指令方向（单位向量；滑行时为 0）。</summary>
        public DVector3 direction;

        public LaunchGuidanceStep(DVector3 dv, bool arrived)
        {
            this.dv = dv;
            this.arrived = arrived;
            phase = LaunchGuidanceCore.PhaseVertical;
            throttle = 0.0;
            direction = DVector3.zero;
        }
    }

    /// <summary>
    /// 爬升段制导纯数学核心（**2026-10-29 重写：相位机 + 推力程序**）。
    ///
    /// ## 为什么换结构（逻辑，而不是参数）
    /// 旧律是"速度 P 控制器"：`dv = 目标速度 − 当前速度`。它在重力场里有**逻辑上必然**的毛病——
    /// 状态一旦等于目标就 `dv=0`（不点火），而重力立刻让状态偏离 ⇒ 律必然在"点火/不点火"之间摇。
    /// 2026-10-29 一晚的所有症状都是这条的推论：油门掉到 37%、t=0 死锁、径向 2.2 km/s 抛射、
    /// 月球烧干 5.1×、低纬度一出生就"速度到"。补丁（禁止减速/不主动刹车/闩锁）只能把症状挪位。
    ///
    /// ## 新结构（MJ 经典上升骨架）
    /// ```
    /// VERTICAL : 方向=当地上，油门=1            退出：alt ≥ altPitchStart 或 **地速** ≥ vAscent
    /// TURN     : 方向=FPA(alt)（90°→turnEndAngle，幂 turnShape），油门=1
    ///            预测远地点 ≥ rTarget ⇒ 二分缩油门；缩到 0 ⇒ COAST
    /// COAST    : 油门=0                          退出：到顶（r ≥ rTarget−tolR 且 vRad ≤ 0）
    /// ```
    /// 相位**有记忆**（控制器持有并传回），所以不存在"每步重算谓词"的来回切。
    ///
    /// ## 本结构下逻辑上必然成立的性质（台架逐条验）
    /// 1. VERTICAL/TURN 段油门恒为 1（只有"远地点已到"能收油）⇒ 不可能"追上目标就不烧"；
    /// 2. 方向是高度的连续函数 ⇒ 不可能一步甩 36°；
    /// 3. 收油门只有一个条件（远地点到）⇒ 不会再有微修正抖动；
    /// 4. 到顶时 r = apo ≈ rTarget、vRad ≈ 0 ⇒ 到达判据由构造满足，圆化只需补 |v| → vC。
    ///
    /// 无记忆部分只保留"**相位内**的连续量"（方向、油门），相位本身由控制器保存 ⇒ 读档不漂。
    /// </summary>
    public static class LaunchGuidanceCore
    {
        public const int PhaseVertical = 0;
        public const int PhaseTurn = 1;
        public const int PhaseCoast = 2;

        /// <summary>
        /// 计算本物理步的推力指令（方向 × 油门 × 可用加速度 × dt）与相位/到达标记。
        /// </summary>
        /// <param name="pos">ECI 位置 (m)，原点 = 行星中心。</param>
        /// <param name="vel">ECI 速度 (m/s)。</param>
        /// <param name="gm">中心天体引力参数 GM (m³/s²)。</param>
        /// <param name="p">冻结参数。</param>
        /// <param name="phase">当前相位（控制器状态）：0=竖直，1=转平，2=滑行。</param>
        /// <param name="accelAvailable">本步可用加速度 (m/s²)。</param>
        /// <param name="dt">本物理步时长 (s)。</param>
        public static LaunchGuidanceStep ComputeStep(DVector3 pos, DVector3 vel, double gm,
            in LaunchGuidanceParams p, int phase, double accelAvailable = 0.0, double dt = 0.0)
        {
            double r = pos.magnitude;
            if (r < 1e-3 || gm <= 0.0 || p.rTarget <= 0.0)
                return new LaunchGuidanceStep(DVector3.zero, false);

            DVector3 radialOut = pos / r;
            double vC = Math.Sqrt(gm / p.rTarget);   // 目标圆轨速度（常数）

            // ── 切向基准：**必须在轨道面内**（2026-10-06 定案）────────────────────────────
            // 全局东方 `Cross(up, r̂)` 在倾斜轨道上会漂出轨道面 ⇒ 制导每步都在改面、烧光燃料。
            // 面内顺行水平 `ĥ×r̂`（只由当前 pos/vel 定 ⇒ 无记忆）。退化（角动量可忽略）回退全局东方。
            DVector3 hVec = DVector3.Cross(pos, vel);
            DVector3 tangent = hVec.sqrMagnitude > r * r
                ? DVector3.Cross(hVec, radialOut).normalized
                : DVector3.Cross(DVector3.up, radialOut).normalized * p.progradeSign;
            if (tangent.sqrMagnitude < 1e-12)
                return new LaunchGuidanceStep(DVector3.zero, false);

            double alt = r - p.planetRadius;
            double vRad = DVector3.Dot(vel, radialOut);

            // **地速**（不是惯性速度）：地球赤道自转 523 m/s &gt; vAscent(500)，
            // 用惯性速度会让低纬度一出生就判"速度到"⇒ 竖直段被跳过 ⇒ 指令偏开 ⇒ 预对准门挡点火 ⇒ 摔。
            double speedSurface = p.planetOmega > 0.0
                ? (vel - SurfaceFrame.SurfaceVelocity(pos, p.planetOmega)).magnitude
                : vel.magnitude;

            double dtEff = dt > 0.0 ? dt : 2.0;
            double aMax = accelAvailable > 0.0 ? accelAvailable : 0.0;
            DVector3 gAcc = pos * (-gm / (r * r * r));

            // ── 相位推进（单向）──────────────────────────────────────────────────────────
            int ph = phase;
            if (ph == PhaseVertical && (alt >= p.altPitchStart || speedSurface >= p.vAscent))
                ph = PhaseTurn;

            DVector3 dir = DVector3.zero;
            double throttle = 0.0;

            if (ph == PhaseVertical)
            {
                // ① 竖直段：机头朝上，**恒满油门**（不是"追某个速度"——追到就没油门，然后掉回来）
                dir = radialOut;
                throttle = 1.0;
            }
            else if (ph == PhaseTurn)
            {
                // ② 转平段：**开环俯仰剖面**（方向是高度的连续函数），油门同样恒满，只被远地点收油
                double turnStart = Math.Min(p.altPitchStart, Math.Max(1.0, alt));
                double turnEnd = Math.Max(p.altPitchEnd, turnStart + 1.0);
                double t = Clamp((alt - turnStart) / (turnEnd - turnStart), 0.0, 1.0);
                double shape = p.turnShapeExponent > 0.0 ? p.turnShapeExponent : 1.0;
                double fpaDeg = 90.0 - Math.Pow(t, shape) * (90.0 - p.turnEndAngleDeg);
                double fpa = fpaDeg * Math.PI / 180.0;
                DVector3 d = radialOut * Math.Sin(fpa) + tangent * Math.Cos(fpa);
                dir = d.sqrMagnitude > 1e-12 ? d.normalized : tangent;
                throttle = 1.0;

                // ③ 远地点收油：预测"本步烧完"的远地点；过冲就二分缩油门到"恰好到顶"。
                // ⚠ 不要在收油条件里加"速度必须 ≥0.95vC"这类门槛（2026-10-29 台架）：月球上等到速度够，
                //   远地点已经被径向速度顶到 1750 km（目标 1650）⇒ 切晚了 100 km。收油只该看远地点。
                if (aMax > 0.0)
                {
                    double ApoAt(double k)
                    {
                        DVector3 v2 = vel + dir * (k * aMax * dtEff) + gAcc * dtEff;
                        DVector3 p2 = pos + (vel + dir * (k * aMax * dtEff * 0.5)) * dtEff;
                        return ApoapsisRadius(p2, v2, gm);
                    }

                    if (ApoAt(0.0) >= p.rTarget)
                    {
                        throttle = 0.0;
                        ph = PhaseCoast;          // 不烧也已经到顶 ⇒ 收油滑行
                    }
                    else if (ApoAt(1.0) >= p.rTarget)
                    {
                        double lo = 0.0, hi = 1.0;
                        for (int i = 0; i < 10; i++)
                        {
                            double mid = 0.5 * (lo + hi);
                            if (ApoAt(mid) >= p.rTarget) hi = mid; else lo = mid;
                        }
                        throttle = hi;
                        if (throttle < 1e-3) { throttle = 0.0; ph = PhaseCoast; }
                    }
                }
            }

            double dvMag = throttle * aMax * dtEff;
            DVector3 dv = throttle > 0.0 ? dir * dvMag : DVector3.zero;

            // **到达**：半径到位 + 不再上升（远地点）⇒ 交给圆化补 |v|。
            // ⚠ 不要把"|v| ≈ vC"放进到达判据：从偏心轨道在远地点做圆化**本来就要补速度**，
            //   加了这条会永远判不到（台架实测：地球 5 组滑行 14 h 才凑巧满足）。
            bool arrived = r >= p.rTarget - p.tolR && vRad <= 0.0;
            return new LaunchGuidanceStep(dv, arrived)
            {
                phase = ph,
                throttle = throttle,
                direction = dir,
            };
        }

        /// <summary>当前状态的远地点半径 (m)：抛物/双曲 ⇒ +∞。公开：控制器/台架用它做收油判据。</summary>
        public static double ApoapsisRadius(DVector3 pos, DVector3 vel, double gm)
        {
            double r = pos.magnitude;
            if (r < 1e-6 || gm <= 0.0) return double.PositiveInfinity;
            double energy = 0.5 * vel.sqrMagnitude - gm / r;
            if (energy >= 0.0) return double.PositiveInfinity;
            double a = -gm / (2.0 * energy);
            DVector3 h = DVector3.Cross(pos, vel);
            double e = Math.Sqrt(Math.Max(0.0, 1.0 + 2.0 * energy * h.sqrMagnitude / (gm * gm)));
            return a * (1.0 + e);
        }

        private static double Clamp(double v, double min, double max)
            => v < min ? min : (v > max ? max : v);
    }
}
