using System;
using PioneerFramework;
using MechJebLib.Lambert;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 交会闭环离线台架（2026-09-17）。**镜像游戏的执行器语义**，不含任何 RimWorld 类型。
    ///
    /// 为什么需要它：<c>DesignNotes/交会物理本质与闭环提法-20260917.md</c> 的结论是
    /// 「换提法 ⇒ 任何合理解的浪费有界」。有界与否只能**实测**，不能靠推导断言。
    /// 本台架把「计划 → 有限燃烧 → 闭环修正」整链跑起来，用来验三条：
    ///   T1 相位通道 = 双积分器，成本 ≈ (2/3)·r·Δφ（§5.4）
    ///   T2 形状通道成本 = |v_c1 − v_c2|（§7.6），且 ≤ 1.15×霍曼（§10.1）
    ///   T3 全流程在**中推力船**（旧架构失效区）下收敛
    ///
    /// ── 执行器语义（与 SpaceGameComp / BurnCore 对齐）──────────────────
    /// · 主网（主推方向型）：推力沿 <c>mainAxis</c>，**只有落在 1.5° 锥内才点火**
    ///   （**出锥窗口 DefaultThrustConeCos=10°** —— 2026-09-19 起"能不能出力"用它；收心死区 1.5°
    ///   只管消旋）；姿态免费（动量轮）但限速 5°/s。
    /// · RCS（isVectoredRcs）：全矢量、免对准、立即出力。
    /// · 选网：Δv ≤ a_rcs·15s 用 RCS，否则主网（<see cref="RendezvousGuidanceCore.SelectActuator"/>）。
    /// · 质量随工质耗减 ⇒ a 上升（Tsiolkovsky）；干质量不含被烧掉的质量。
    /// </summary>
    public sealed class RendezvousShip
    {
        /// <summary>
        /// **高推力船 = 游戏调试放置船的逐位同参**（560kN 主 / ṁ140 / 66t；30kN RCS / ṁ20 / 4t；
        /// 干重 5t）⇒ dV≈9986 m/s、满载加速 7.87 m/s²。
        ///
        /// 为什么需要它（2026-09-17）：**限权（0.05·g）只对低推力船是紧约束**。
        /// 形状通道的指令是 $a_t=\delta n\,r/(3	au)$（τ=3000s，**与推力无关**）：
        ///   · 高推力船（aMax 7.87）⇒ 指令 0.52 m/s² **不饱和** ⇒ 转移约 3τ≈9000s；
        ///   · 低推力船（aMax 0.46）⇒ 指令被 aMax 截断 ⇒ 反而更慢。
        /// ⇒ 用 MidThrustShip 验证"快不快"是**测错了对象**，必须用这一档。
        /// </summary>
        public static RendezvousShip HighThrustShip(DVector3 pos, DVector3 vel, DVector3 mainAxis)
        {
            var s = RendezvousShip.Spawn(pos, vel, mainAxis);
            s.DryMass = 5000.0;
            s.MainThrust = 560000.0; s.MainVe = 4000.0; s.MainMassFlow = 140.0; s.MainFuel = 66000.0;
            s.RcsThrust = 30000.0; s.RcsVe = 1500.0; s.RcsMassFlow = 20.0; s.RcsFuel = 4000.0;
            return s;
        }

        // ── 状态 ──
        public DVector3 Pos, Vel;
        /// <summary>主推轴（世界系，单位矢量）。D15：无 Map 船 = mainAxisLocal 随 rotation 转。</summary>
        public DVector3 MainAxis;

        // ── 质量与推进 ──
        public double DryMass = 5000.0;
        public double MainFuel = 12000.0, RcsFuel = 500.0;

        /// <summary>主网（主推方向型）。</summary>
        public double MainThrust = 8000.0, MainVe = 4000.0, MainMassFlow = 2.0;
        /// <summary>RCS（全矢量）。</summary>
        public double RcsThrust = 1500.0, RcsVe = 1500.0, RcsMassFlow = 1.0;

        // ── 姿态 ──
        public double MaxAngularRate = 5.0 * Math.PI / 180.0;      // DefaultMaxAngularRate = 5°/s
        /// <summary>游戏侧**出锥窗口**（`DefaultThrustConeCos`，10°）= **主推能不能出力**的门。
        /// ⚠ 2026-09-19：游戏里出力的门原来被写成了下面那个 1.5° 死区 ⇒ 质心在死区附近摆动时
        /// **整步空转**（不推力不耗料）⇒ 燃烧被拉长、Δv 质心后移（实测不变量掉 2%、e 停在 5.8e-3）。
        /// 本台架跟着一起改（台架的职责就是**建模游戏侧政策**，不同步 = 台架变成"另一个执行器" ✗）。</summary>
        public const double ThrustConeCos = 0.984807753;            // DefaultThrustConeCos（10°）

        // ── 诊断 ──
        public double MainDv, RcsDv;                 // 各网实际提供的 Δv
        public double MainSeconds, RcsSeconds;       // 各网出力时长
        public double AlignWaitSeconds;              // 主推未对准导致"想烧主网却烧不动"的时长
        public int MainIgnitions, RcsIgnitions;
        /// <summary>最小地心距（m）。**没有这个读数，穿地飞行会静默产出垃圾数据**（2026-09-17 实测）。</summary>
        public double MinRadius = double.PositiveInfinity;
        /// <summary>是否侵入安全半径（地表+大气）。</summary>
        public bool Crashed;

        public double Mass => DryMass + MainFuel + RcsFuel;
        public double MainAccel => MainFuel > 0.0 && MainThrust > 0.0 ? MainThrust / Mass : 0.0;
        public double RcsAccel => RcsFuel > 0.0 && RcsThrust > 0.0 ? RcsThrust / Mass : 0.0;

        /// <summary>主网可达 Δv（当前剩余）。</summary>
        public double MainDvRemaining => MainVe * Math.Log(Mass / Math.Max(Mass - MainFuel, 1.0));
        /// <summary>RCS 可达 Δv（当前剩余）。独立口径——注意它只算 RCS 罐。</summary>
        public double RcsDvRemaining => RcsVe * Math.Log(Mass / Math.Max(Mass - RcsFuel, 1.0));

        public static RendezvousShip Spawn(DVector3 pos, DVector3 vel, DVector3 mainAxis)
        {
            return new RendezvousShip { Pos = pos, Vel = vel, MainAxis = mainAxis.normalized };
        }

        /// <summary>把主推轴以 ≤ maxAngularRate 的速率转向 dir；返回对准误差（rad）。</summary>
        public double SlewToward(DVector3 dir, double dt)
        {
            DVector3 d = dir.normalized;
            if (d.sqrMagnitude < 1e-18) return 0.0;
            double dot = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(MainAxis, d)));
            double err = Math.Acos(dot);
            double maxStep = MaxAngularRate * dt;
            if (err <= Math.Max(maxStep, 1e-12) || err < 1e-9)
            {
                MainAxis = d;
                return 0.0;
            }
            // 朝 d 转 maxStep
            DVector3 axis = DVector3.Cross(MainAxis, d);
            if (axis.sqrMagnitude < 1e-18)
            {
                // 反平行：任取垂直轴
                axis = DVector3.Cross(MainAxis, Math.Abs(MainAxis.y) < 0.9 ? DVector3.up : DVector3.forward);
            }
            axis = axis.normalized;
            double c = Math.Cos(maxStep), s = Math.Sin(maxStep);
            MainAxis = (MainAxis * c + DVector3.Cross(axis, MainAxis) * s + axis * (DVector3.Dot(axis, MainAxis) * (1.0 - c))).normalized;
            return Math.Acos(Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(MainAxis, d))));
        }

        /// <summary>
        /// 施加一次推力指令（本步）。**按执行器语义决定出不出力**：
        /// 主网要求锥内对准；未对准时本步不出力（并计入 AlignWaitSeconds）。
        /// 返回本步的**推力加速度矢量**（m/s²）——不是 Δv。
        /// ⚠ 曾经返回 Δv 而被 <see cref="Propagate"/> 当加速度用 ⇒ 实际推力放大 1/dt 倍
        /// （dt=5s 即 5 倍），表现为"不可能的能量增益"（2026-09-17 实测 462 m/s 的账换来
        /// 需要 2313 m/s 的能量，e 冲到 0.78）。
        /// </summary>
        public DVector3 ApplyThrust(RendezvousGuidanceCore.Actuator kind, DVector3 dir, double dvNeeded, double dt)
        {
            DVector3 d = dir.normalized;
            if (kind == RendezvousGuidanceCore.Actuator.None || d.sqrMagnitude < 1e-18 || dt <= 0.0)
                return DVector3.zero;

            if (kind == RendezvousGuidanceCore.Actuator.Main)
            {
                SlewToward(d, dt);
                double cosErr = DVector3.Dot(MainAxis, d);
                if (cosErr < ThrustConeCos)
                {
                    // 出锥（>10°）才等：本步只转，不推力不耗料。锥内（哪怕没收心到 1.5°）就出力
                    AlignWaitSeconds += dt;
                    return DVector3.zero;
                }
                if (MainFuel <= 0.0) return DVector3.zero;
                double a = MainThrust / Mass;
                double dv = Math.Min(a * dt, dvNeeded);
                double frac = a * dt > 0.0 ? dv / (a * dt) : 0.0;
                double dm = Math.Min(MainMassFlow * dt * frac, MainFuel);
                MainFuel -= dm;
                MainDv += dv; MainSeconds += dt * frac;
                // 推力沿**主推轴**（不是指令方向）——真实的小锥内误差
                return MainAxis * (dv / dt);
            }

            // RCS：全矢量，立即
            if (RcsFuel <= 0.0) return DVector3.zero;
            double aR = RcsThrust / Mass;
            double dvR = Math.Min(aR * dt, dvNeeded);
            if (dvR <= 0.0) return DVector3.zero;
            // ⚠ 耗料必须按**实际出力占比**折算：写成 RcsMassFlow*dt 会让"极小指令也烧满流率"，
            //    43.5 m/s 的 RCS 预算在几分钟内被打光（2026-09-17 实测）。与主网同口径。
            double fracR = aR * dt > 0.0 ? dvR / (aR * dt) : 0.0;
            double dmR = Math.Min(RcsMassFlow * dt * fracR, RcsFuel);
            RcsFuel -= dmR;
            RcsDv += dvR; RcsSeconds += dt * fracR;
            return d * (dvR / dt);
        }

        /// <summary>RK4 推进（引力 + 本步的推力加速度，推力在步内视为常矢量）。</summary>
        public void Propagate(DVector3 thrustAccel, double mu, double dt)
        {
            DVector3 Acc(DVector3 p) => -p * (mu / (p.sqrMagnitude * p.magnitude));
            DVector3 k1v = Acc(Pos) + thrustAccel;
            DVector3 k1r = Vel;
            DVector3 k2v = Acc(Pos + k1r * (dt * 0.5)) + thrustAccel;
            DVector3 k2r = Vel + k1v * (dt * 0.5);
            DVector3 k3v = Acc(Pos + k2r * (dt * 0.5)) + thrustAccel;
            DVector3 k3r = Vel + k2v * (dt * 0.5);
            DVector3 k4v = Acc(Pos + k3r * dt) + thrustAccel;
            DVector3 k4r = Vel + k3v * dt;
            Pos += (k1r + k2r * 2.0 + k3r * 2.0 + k4r) * (dt / 6.0);
            Vel += (k1v + k2v * 2.0 + k3v * 2.0 + k4v) * (dt / 6.0);
        }
    }

    /// <summary>圆轨目标（解析，无控制）。</summary>
    /// <summary>目标轨道的最小接口（圆/椭圆共用）：pilot 只依赖它 ⇒ 椭圆目标可以直接塞进来。</summary>
    public interface ITarget
    {
        DVector3 PosAt(double t);
        DVector3 VelAt(double t);
        /// <summary>t 时刻的轨道面法向（远场律用它定"平黄经"的基）。</summary>
        DVector3 PlaneNormalAt(double t);
    }

    /// <summary>
    /// **椭圆轨道目标**（GTO 这类）：只提供 PosAt/VelAt，用解析二体传播（与追方同一个口径）。
    /// ⚠ 台架此前**只有圆轨目标** ⇒ "交会椭圆轨道目标"这一整类工况**从未被测过**
    /// （2026-09-17 用户指出："我是从 LEO 交会 GTO 发现这个交会有问题的，你怎么一直弃置异轨情况"）。
    /// </summary>
    public sealed class EllipticTarget : ITarget
    {
        public double Mu;
        public DVector3 R0, V0;
        public DVector3 PosAt(double t) => OrbitMath.GetPositionAtTimeDelta(R0, V0, t, Mu);
        public DVector3 VelAt(double t) => OrbitMath.GetVelocityAtTimeDelta(R0, V0, t, Mu);
        public DVector3 PlaneNormalAt(double t) => DVector3.Cross(R0, V0).normalized;

        /// <summary>近地点 rp、远地点 ra 的椭圆，t=0 时在近地点（与圆轨追方"同点出发"）。</summary>
        public static EllipticTarget CreateGto(double rp, double ra, double mu, DVector3 normal)
        {
            double a = 0.5 * (rp + ra);
            double vp = Math.Sqrt(mu * (2.0 / rp - 1.0 / a));
            DVector3 px = DVector3.Cross(normal, new DVector3(0, 1, 0)).normalized;
            if (px.sqrMagnitude < 1e-9) px = new DVector3(1, 0, 0);
            DVector3 py = DVector3.Cross(normal, px).normalized;
            return new EllipticTarget { Mu = mu, R0 = px * rp, V0 = py * vp };
        }
    }

    public sealed class CircularTarget : ITarget
    {
        public double Radius, Mu, Angle0, Omega;
        public DVector3 PlaneX, PlaneY;      // 轨道面内的惯性基

        public static CircularTarget Create(double radius, double mu, double angle0, DVector3 normal)
        {
            DVector3 n = normal.normalized;
            DVector3 x = Math.Abs(n.y) < 0.9 ? DVector3.Cross(DVector3.up, n).normalized
                                             : DVector3.Cross(DVector3.forward, n).normalized;
            DVector3 y = DVector3.Cross(n, x).normalized;
            return new CircularTarget
            {
                Radius = radius, Mu = mu, Angle0 = angle0,
                Omega = Math.Sqrt(mu / (radius * radius * radius)),
                PlaneX = x, PlaneY = y,
            };
        }

        public double AngleAt(double t) => Angle0 + Omega * t;
        public DVector3 PlaneNormalAt(double t) => DVector3.Cross(PlaneX, PlaneY).normalized;

        public DVector3 PosAt(double t)
        {
            double th = AngleAt(t);
            return PlaneX * (Math.Cos(th) * Radius) + PlaneY * (Math.Sin(th) * Radius);
        }

        public DVector3 VelAt(double t)
        {
            double th = AngleAt(t);
            double v = Math.Sqrt(Mu / Radius);
            return PlaneX * (-Math.Sin(th) * v) + PlaneY * (Math.Cos(th) * v);
        }
    }

    /// <summary>
    /// 面内闭环驱动器：把 <see cref="RendezvousGuidanceCore"/> 的律接到船上，逐物理步跑。
    ///
    /// 控制律（§5 + §10 的近圆约化）：
    ///   c₀ = 相位/半长轴通道（双积分器 bang-bang + 滑行）
    ///   (c₁,c₂) = 偏心率通道 = −(v_c/τ_e)·ê —— 一个轨道周期平均后 ê 以 τ_e 衰减
    ///   合成 a_t(ν) = c₀ + c₁cosν + c₂sinν，整体缩放使 |a_t|∞ ≤ a_max
    ///
    /// 偏心率通道**必须留**：纯切向连续推力会留下 e ~ a_t/g 的残余（LEO 下 a_t/g=0.07
    /// ⇒ 半径摆动 ±7% ≈ ±385 km），那是"到不了"的量级。
    /// </summary>
    public sealed class InPlaneRendezvousPilot
    {
        public double EccentricityTimeConstant = 900.0;   // τ_e（秒）

        /// <summary>剩余时间预算 T（秒）。**必须给**——纯自由末时是退化解（§5.2 注）。</summary>
        public double TimeBudget = 86400.0;
        /// <summary>true = 同时收相位（交会）；false = 只把轨道开到目标（相位自由）。</summary>
        public bool ClosePhase = true;

        /// <summary>安全半径（地表 + 大气）。侵入即置 <see cref="RendezvousShip.Crashed"/>。</summary>
        public double SafeRadius = 5.1e6;

        /// <summary>
        /// 推力/重力比上限 η：$a_t \le \eta\cdot g_{local}$。
        ///
        /// **近圆约化的适用条件**：连续切向推力在轨道上造成的**瞬时偏心率**上界是
        /// $$e_{max} = \frac{4a_t}{v_c n} = \frac{4a_t}{g_{local}}$$
        /// （$\hat r$ 的一圈积分恒为 0 ⇒ 无净 e，但圈内 e 摆到 $4a_t/g$）。
        /// $e$ 一旦不可忽略，$\dot a = \frac{2a^2\sqrt p}{r\sqrt\mu}a_t$ 的系数沿轨道剧烈变化
        /// ⇒ **e 通道的谐波不再对 a 零均值** ⇒ 两个通道互相污染（2026-09-17 实测：
        /// LEO 下 e=0.30 与 4a_t/g=0.28 吻合；GEO 下同一推力给出 4a_t/g=6.4，律彻底失效）。
        ///
        /// 限权在 Δv 上**几乎免费**：螺旋价 ∫v_c/(2a)·|da| 是路径积分，与速率无关。
        /// η=0.05 ⇒ e_max≤0.2。
        /// </summary>
        public double ThrustToGravityCap = 0.05;

        /// <summary>
        /// 是否启用**自适应**推力上限（按受迫偏心率，而非固定 a_t）。
        ///
        /// ⚠ **默认关**：本批实测（设计稿 §17.5），换成自适应后高推力区**确实**收敛为教科书式
        /// 双脉冲（`#..................#`，集中度 97%），但**低推力区同时回归**——
        /// 因为旧的那个固定 $a_t$ 上限一直在**隐式地兼作增益**（把律节流到温和节奏）。
        /// 正确做法是把两者分开（上限管正确性、节奏归 TrackingFraction），
        /// 但配套的增益需要重调，**本批未完成**。故先保留能力、默认关闭。
        /// </summary>
        public bool AdaptiveCapEnabled = true;
        /// <summary>允许的受迫偏心率峰值。</summary>
        public double EccentricityCap = 0.2;

        /// <summary>参考漂移率 δn* 的追踪时间常数占比 τ = f·T。必须 ≪1。</summary>
        public double TrackingFraction = 0.05;
        /// <summary><b>诊断开关</b>：远场阶段禁用 RCS（只走主网）。</summary>
        public bool MainOnly;
        /// <summary>**提交式两冲量终端**（与游戏同政策，见 SpaceGameComp 的实测定案）：dv2 记下来到到达时刻再烧。</summary>
        public bool CommitTwoImpulse = false;

        /// <summary>
        /// **B1.2 的"条件语义"（2026-09-20 加）**：提交模式下，到达时刻**不烧那枚冻结的 dv2**，
        /// 而是**按当时的状态重解**（= 条目携带的是**目标条件**"到达点消掉相对速度"，点火时才解 Δv）。
        /// 用途：**先验计划稿那条假设**——上一轮"提交 dv2"台架失败（15° → 9.40 圈不收敛）的根因
        /// 究竟是**冻结向量**，还是"提交"这件事本身。三档对照：滚动重解 / 提交冻结 / 提交+重解。
        /// </summary>
        public bool CommitResolveAtBurn = false;
        /// <summary>
        /// **点火死区**（m/s，0 = 关闭）：小于它的指令不点火。
        ///
        /// ⚠ 台架原先**没有建模它**，而游戏侧落地段有（`ApplyGuidanceCorrection` 的 deadbandMps）
        /// ⇒ 一类"指令落在死区里 ⇒ 律在算、船不动"的故障**离线测不出来**。
        /// 2026-09-17 游戏内实测：同轨 90° 相位差、腿长 25056s 时律的指令只有 **0.92 m/s**，
        /// 而死区 = 0.02·aMax·dt = **1.57 m/s** ⇒ **零点火、永不收敛**（日志里 3 次登记、0 次点火）。
        /// ⇒ 补上建模；T14 用"旧值 0.02（复现停摆）/ 新值 0.001（通过）"两种取值把它钉住。
        /// </summary>
        public double ActuatorDeadband = 0.0;
        /// <summary>
        /// **建模游戏侧的安全裁剪**（撞行星保护，`ApplyGuidanceCorrection` 里那段）：
        ///   · `MaxSafeDvScale` < 1 ⇒ 按比例裁剪本次 Δv（"能烧多少烧多少"）；
        ///   · < 0.05 ⇒ **本轮不点火**（新律的口径：船已停在物理上限的相位轨道上、按最大速率收相位）。
        ///
        /// ⚠ 台架原先只有 `Crashed` 标志、**没有任何拦截** ⇒ 与游戏行为分叉：
        /// 同轨 90° 相位差在游戏里会被钳在大气层边缘滑行（4.4 圈后进终端），
        /// 台架里却直接**掉进行星**（实测 0.26 圈 Crashed）⇒ **离线复现不出游戏的工况**。
        /// 这是同一个结构性漏洞的第三次现形（先死区、再控制周期、现在安全裁剪）。
        /// 默认 false = 关闭，既有测试逐位不变。
        /// </summary>
        public bool ModelSafetyClip = false;

        // ── 终端交接（§12.2："末端仍需精确计算，这是唯一必须精确的地方"）─────────
        /// <summary>
        /// 远场律的职责是**成本与粗收敛**；末端它必然失灵——因为它的控制权随剩余预算
        /// $T	o0$ 消失（切换段的要求），而终端本质上是**没有截止时刻的反馈**。
        /// 故在相对距离足够近时切到**滑道律**：
        ///   $$\Delta v = ig(v_{tgt} - \lambda\,r_{rel}ig) - v_{self},\qquad
        ///     \lambda = \min(1/	au,\ v_{cap}/|r_{rel}|)$$
        /// 它连续、可节流、无截止时刻，且同时收位置与速度（相对轨道归零）。
        /// 与 <c>MissileGuidanceCore</c> 的"接近率 + 横向修正"同族，但终点是零相对速度而非撞击。
        /// </summary>
        public bool TerminalHandoff = true;   // 终端 = CW/多圈 Lambert 取更省者（§14.3 E1）
        // ══════════════════════════════════════════════════════════════════════════════
        //  ⚖ **台架独有旋钮：裁决表**（B5-5，2026-09-23）
        //  计划稿 §2.6 的原话：*"这类'只在台架里存在的政策'是**幻觉的来源**"* —— 逐个裁定：
        //  **要么建模进游戏，要么从台架删除**。本轮裁定如下（依据：游戏侧 `Source/PioneerFramework/`
        //  grep 零命中 + 连续律已于 2026-09-21 从流程切除、2026-09-22 被用户裁定"不能用了"）：
        //
        //  | 旋钮 | 域 | 裁定 |
        //  |---|---|---|
        //  | `TerminalHandoff` / `TerminalEngageFraction` / `TerminalUseShortestHorizon` / | 已退休的**连续律终端** | **游戏永不采用**：保留只为重跑历史读数（T5/T7/T13）， | 
        //  | `TerminalHorizonFrac` / `TerminalLateFraction` / `TerminalLateBudgets` / | （"滚动 CW 终端"） | **不得作为游戏行为的参照**；将来要做"总修缮"阶段、 | 
        //  | `TerminalSlideFraction` / `TerminalSlideTau` / `TerminalSlideClosingCap` | | 且**必须搭在 MJ 实现上**时才重新评估 |
        //  | `TerminalUseExactLambert` | 同上（且**已实测发散**，注释已判死刑） | **保留仅作反例**：别重试 |
        //  | `CommitTwoImpulse` / `CommitResolveAtBurn` | 同上（"两冲量提交"的台架版） | **游戏已有对应物**（阶梯排两发：起飞+刹车）⇒ 台架这份只作历史对照 |
        //  | `AdaptiveCapEnabled` / `EccentricityCap` | 律的限权政策 | **游戏永不采用**（游戏是 `ManeuverRegime` + 固定限权） |
        //  | `ShapeFirstLadder` / `LegSeconds` / `ReArmLegs` | 分腿/形状优先 | 游戏侧**有各自的权威**（`NewLawLegSeconds` / `PlanLeg`）⇒ 台架这份是**同题第二份**，只作历史 |
        //  | `ModelSafetyClip` | 撞行星裁剪的台架模型 | 游戏侧权威在 `ApplyGuidanceCorrection`（`safeK ≥ 0.05`）⇒ 同上 |
        //  | `ActuatorDeadband` | 死区 | **已有游戏侧对应物**（`GuidanceBurnDeadband` / `NewLawDeadbandFraction`）⇒ 台架这份只作对照 |
        //  | `MainOnly` | 诊断开关（禁用 RCS 以分离"选网政策"的贡献） | **保留**：它是**探针**不是政策 |
        //  | ~~`TerminalStallFraction`~~ | — | **已删**（只声明、无任何使用 ⇒ 死字段） |
        //
        //  ⇒ **结论**：上表里"游戏永不采用"的那些**不建模进游戏**（连续律已退休）；
        //    它们的价值只剩"重跑历史读数"，故**本文件的读数一律标注域**（见类注释）。
        // ══════════════════════════════════════════════════════════════════════════════
        /// <summary>
        /// 交接判据：相对距离 &lt; 该比例 × 轨道半径 时切滑道。
        /// **必须很小**：滑道律假定相对运动是线性的，而 CW 耦合 $1.5n\cdot\Delta r$ 在
        /// GEO 上给出 5,000 km × 1.57e-4 = **785 m/s** 的沿迹漂移——比相对速度还大。
        /// 实测 25% 处交接会让滑道**发散**（T=192000：115 → 1252 m/s，且距离从 942 km 拉大到 15,465 km）。
        /// 0.5% ⇒ GEO 下 ≈130 km，那里耦合项可忽略。
        /// </summary>
        public double TerminalEngageFraction = 0.05;
        /// <summary>
        /// 兜底交班的**外圈比例**与**机会倍数**：距离进入外圈、且远场已用掉这么多倍预算时交班。
        ///
        /// ⚠ 用"停滞"（距离不再下降）当判据**不行**：远场不是停滞，是**缓慢收敛但收不到底**
        ///   （实测 T5@96000：一路从 26,322 km 收到 2,306 km=8.8%，一直在推进、只是不够）；
        ///   停滞计时器被这点推进不断重置。改成"**给够了机会**"才对。
        /// </summary>
        public double TerminalLateFraction = 0.10;
        /// <summary>
        /// **终端视界的选择准则**。
        ///
        /// ⚠ 滚动执行下不该按"**开环计划最省**"选视界：实测（§19.2）加长视界段后 T5 终端
        /// 从 430 → 1,455 m/s；而 T6 的入口态理想解恰好取在扫描带**上界**（说明真最优在更长视界），
        /// 于是在带内取 min-cost 会挑到一个"计划便宜、但闭环执行贵"的视界
        /// （实测 T6 终端 = 自身理想的 **7.5×**）。
        /// **闭环代价偏向短视界** ⇒ 默认取带内最短。
        /// </summary>
        public bool TerminalUseShortestHorizon = true;
        /// <summary>
        /// **末段滑道律**（与游戏侧同政策，见 SpaceGameComp.NewLawSlideFraction 的实测记录）：
        /// 相对距离 &lt; 该比例 × 轨道半径时，改用"无截止时刻、同时收位置与速度"的连续律
        /// Δv = (v_tgt − λ·r_rel) − v_self（λ = min(1/τ, v_cap/|r_rel|)），而不是 CW 两冲量——
        /// 后者滚动执行时会**永远重起一段 860s 弧** ⇒ 位置收得住、**速度收不住**（实测 relVel 卡在 18~20 m/s）。
        /// 0 = 关闭（既有测试逐位不变）。
        /// </summary>
        public double TerminalSlideFraction = 0.0;
        public double TerminalSlideTau = 3000.0;
        public double TerminalSlideClosingCap = 50.0;
        /// <summary>短视界模式的视界（×T_orb）。</summary>
        public double TerminalHorizonFrac = 0.15;
        /// <summary>
        /// 终端求解器：**精确 Lambert**（true）还是 **CW 闭式**（false）。
        ///
        /// 依据（2026-09-17 §19.7）：想修"CW 线性化 vs 真实 Kepler"的模型差（台架终端 = 模型内
        /// 同策略的 **1.74 倍**）时试过换成精确 Lambert 到点。
        ///
        /// ⚠ **实测发散**（T5@192000 飞到 283,062 km）：**Lambert 到点是"拦截"解**——
        /// 只命中目标在 t+tof 的位置、**不含到达速度匹配**；而 CW 两冲量是"**交会**"结构
        /// （两次冲量联合解出，Δv₂ 负责速度匹配）。滚动执行下拦截解要求极高接近率 ⇒
        /// 追一个后退的点 ⇒ 发散。**这是"拦截 vs 交会"第三次咬人**（§14 归档 → §14.6 跨原语 → 本次）。
        /// ⇒ 修模型差必须用**精确的两冲量交会**（如对 CW 解做打靶修正），不能用 Lambert 到点。
        /// </summary>
        public bool TerminalUseExactLambert = false;   // 实测**发散**：Lambert 到点是拦截解、不含到达速度匹配（见 TerminalUseExactLambert 注释）
        /// <summary>首段预算（用于"机会倍数"）。</summary>
        public double TimeBudget0 = 96000.0;
        public double TerminalLateBudgets = 2.0;

        /// <summary>
        /// **远场分腿**：交会在几何上就是"一串相位机动"——每段收掉一部分相位差。
        /// 首段预算用尽即**重起一段**（而不是让律在 $T\le0$ 退化为保轨）。
        /// 为什么必要：远场结束时 $\delta npprox0$（$a=a_{tgt}$）⇒ **共轨下相位冻结**，
        /// 那最后的 942 km 再也收不动——除非再起一次短相位机动。
        /// 判据：每段的收敛在 $|\deltaarphi|\lerac12UT^2$ 内是精确的，故重起即可收敛。
        /// </summary>
        public bool ReArmLegs = true;    // 配 GainTimeFloor 使用（见核心注释）
        /// <summary>
        /// **形状优先阶梯**（游戏侧政策，2026-09-17）：按段调 `RendezvousGuidanceLaw.PlanLeg`
        /// 决定段模式（形状/相位）与腿长。默认 false ⇒ 既有测试逐位不变。
        /// </summary>
        public bool ShapeFirstLadder = false;
        /// <summary>阶梯的段模式（跨段保持）。</summary>
        private bool ELegShapeMode;
        /// <summary>每段预算长度（s）。</summary>
        public double LegSeconds = 60000.0;
        /// <summary>滑道时间常数 τ（s）。</summary>
        public double TerminalTimeConstant = 600.0;
        /// <summary>滑道接近速度上限（m/s）。</summary>
        public double TerminalClosingCap = 50.0;

        /// <summary>是否已进入终端（诊断）。</summary>
        public bool InTerminal;
        /// <summary>最近一次终端两冲量解的总 Δv（诊断）。</summary>
        public double LastTerminalPlanDv;
        /// <summary>最近一次终端选中的转移时长（诊断）。</summary>
        public double LastTerminalToF;
        /// <summary>最近一次终端用了哪个原语（诊断）。</summary>
        public string LastTerminalKind = "";
        /// <summary>终端分支累计 Δv 与步数（诊断：分离"策略"与"执行路径"的账）。</summary>
        public double TerminalDv;
        public int TerminalSteps;
        /// <summary>进入终端时的相对距离/相对速度（诊断）。</summary>
        public double TerminalEntryDist, TerminalEntryRel;
        /// <summary>终端入口相对状态的 RTN 分解（诊断）。</summary>
        public DVector3 TerminalEntryR, TerminalEntryV;
        /// <summary>面外通道累计 Δv（诊断）。</summary>
        public double CrossTrackDv;

        // ── "近似双脉冲"的度量：把 Δv 按**任务时间分箱**，看它集中在两端还是铺满 ──
        //   理想双脉冲：两端各一次、中间为零 ⇒ 集中度 100%
        //   均匀燃烧：铺满 ⇒ 集中度 = 两端箱占比（20 箱取首末各 3 箱 = 30%）
        /// <summary>分箱起点（秒）；&lt;0 表示未启用。</summary>
        public double BinStart = -1.0;
        /// <summary>分箱总时长（秒）。</summary>
        public double BinSpan = 1.0;
        /// <summary>Δv 时间直方图（20 箱）。</summary>
        public readonly double[] DvBins = new double[20];

        /// <summary>把一次 Δv 计入直方图。</summary>
        public void RecordDv(double t, double dv)
        {
            if (BinStart < 0.0 || dv <= 0.0) return;
            int k = (int)((t - BinStart) / BinSpan * 20.0);
            if (k < 0) k = 0; if (k > 19) k = 19;
            DvBins[k] += dv;
        }

        /// <summary>
        /// **脉冲集中度** = 首末各 3 箱（共 30% 时长）的 Δv 占比。
        /// 理想双脉冲 = 1.0；均匀燃烧 = 0.3。
        /// </summary>
        public double ImpulseConcentration()
        {
            double tot = 0.0, ends = 0.0;
            for (int i = 0; i < 20; i++) { tot += DvBins[i]; if (i < 3 || i > 16) ends += DvBins[i]; }
            return tot > 1e-12 ? ends / tot : 0.0;
        }
        /// <summary>最近一次交叉轨迹位移（诊断）。</summary>
        public double LastCrossTrackZ;

        /// <summary>e 通道继电器：e 超过此值投入清理。</summary>
        public double EccEngageAt = 0.02;


        /// <summary>e 低于此值撤出（迟滞，防抖）。</summary>
        public double EccReleaseAt = 0.005;

        private bool EEcActive;

        // 诊断：最近一次的控制分量
        public double C0, C1, C2;
        public double LastPhaseErr, LastPhaseRate;
        /// <summary>分账：c₀（相位/a 通道）与谐波（e 通道）各自的 Δv 贡献。</summary>
        public double DvFromC0, DvFromHarm;
        /// <summary>c₀ 的符号翻转次数。**抖动会在这里暴露**（正常应是 O(1) 次 Bang）。</summary>
        public int C0SignFlips;
        private double LastC0Sign;
        /// <summary>上一周期的相位差（供分腿长度估算；相位变化很慢，足够）。</summary>
        private double lastPhaseErrForLeg;
        private DVector3 PlannedDv2;
        private double PlannedDv2Tick = double.NegativeInfinity;
        /// <summary>细轨迹（前 400 条，每 100s 一条）：诊断律到底在追什么。</summary>
        public readonly System.Collections.Generic.List<string> Trace = new System.Collections.Generic.List<string>();

        /// <summary>一个物理步：算出推力加速度并施加。</summary>
        public void Step(RendezvousShip ship, ITarget target, double mu, double t, double dt,
            out double phaseErr, out double dist, out double relSpeed, out DVector3 thrustAccel)
        {
            DVector3 r = ship.Pos, v = ship.Vel;
            double rMag = r.magnitude;
            DVector3 rHat = r / rMag;
            DVector3 nrm = DVector3.Cross(r, v).normalized;             // 轨道法向
            DVector3 tHat = DVector3.Cross(nrm, rHat).normalized;        // 顺行切向

            double vc = Math.Sqrt(mu / rMag);
            double n = Math.Sqrt(mu / (rMag * rMag * rMag));

            // ── 律的合成：**唯一入口**（与游戏侧共用，见 RendezvousGuidanceLaw）──────
            //   台架只负责"取状态 / 定预算 / 交给执行器"，合成本身不在这里。
            // ── 分腿重起（**调用方的政策**，不属于律）────────────────────
            //   截点即开新的一段；**腿长由可行性判据定**（单腿能闭合的最大相位是 U·T²/4
            //   ⇒ 收掉 |δφ| 需 T ≥ 2√(|δφ|/U)），安全系数 1.5，且不小于配置值。
            //   用**上一周期**的相位差估腿长（相位变化很慢，足够），避开“调用前还没有相位差”的先有鸡。
            if (ShapeFirstLadder)
            {
                // **形状优先阶梯**（游戏侧政策）：与 `RunNewRendezvousLaw` 调**同一个** PlanLeg，
                // ⇒ 本测验的就是游戏在跑的那条政策。段模式一段内固定（见 PlanLeg 的说明）。
                // 段的收/排整个在纯核心里（与游戏侧同一个 PlanLeg；含"形状收敛即提前收段"）
                RendezvousGuidanceLaw.PlanLeg(r, v, target.PosAt(t), target.VelAt(t), mu,
                    Math.Max(ship.MainAccel, ship.RcsAccel), t, LegSeconds, lastPhaseErrForLeg,
                    ref ELegShapeMode, ref TimeBudget, out _, out _);
            }
            else if (ReArmLegs && ClosePhase && t >= TimeBudget)
            {
                double uNow = 3.0 * Math.Max(ship.MainAccel, ship.RcsAccel) / rMag;
                double need = RendezvousGuidanceCore.MinLegSeconds(lastPhaseErrForLeg, uNow) * 1.5;
                TimeBudget = t + Math.Max(LegSeconds, need);
            }

            var lawTgtNormal = target.PlaneNormalAt(t);                        // 目标轨道法向
            var lawRes = RendezvousGuidanceLaw.Compose(
                r, v, target.PosAt(t), target.VelAt(t), lawTgtNormal,
                mu, Math.Max(ship.MainAccel, ship.RcsAccel), Math.Max(TimeBudget - t, 0.0), dt,
                ref EEcActive,
                closePhase: ShapeFirstLadder ? !ELegShapeMode : ClosePhase,
                adaptiveCap: AdaptiveCapEnabled,
                trackingFraction: TrackingFraction,
                eccCap: EccentricityCap,
                slowBurnEta: ThrustToGravityCap,
                eccTimeConstant: EccentricityTimeConstant,
                eccEngageAt: EccEngageAt,
                eccReleaseAt: EccReleaseAt);
            if (!lawRes.Valid) { phaseErr = 0.0; dist = 0.0; relSpeed = 0.0; thrustAccel = DVector3.zero; return; }
            phaseErr = lawRes.PhaseErr;
            lastPhaseErrForLeg = phaseErr;
            C0 = lawRes.C0Norm;
            double aT = lawRes.AT, aN = lawRes.AN;
            double aRef = lawRes.ARef;
            CrossTrackDv += lawRes.CrossTrackDvStep;
            DVector3 aCmd3 = tHat * aT + nrm * aN;
            if (aCmd3.magnitude > aRef) aCmd3 = aCmd3.normalized * aRef;
            if (Math.Abs(aT) > 1e-12) RecordDv(t, Math.Abs(aT) * dt);

            // ── 终端交接 ──
            DVector3 relPos = r - target.PosAt(t);
            double relDist = relPos.magnitude;
            // ⚠ 交班判据**不能**直接用 e 残差：那一刻的 e 含**受迫分量** 4a_t/g_local，
            //   a 通道还在工作时它下不到阈值 ⇒ **永远交不出去**（实测：终端=False，停在 3,900 km）。
            //   受迫分量在 a 通道停下后一圈内自行归零，故它不是终端的账单；真正的账单是**自由 e**。
            //   本版先只做"临近交班解除 e 通道压制"，判据仍按距离。
            // ⚠ 本批试过三种更"聪明"的交班判据，**全部实测失败**（见设计稿 §16.5）：
            //   ① 加 e 残差阈  ⇒ 永远交不出去（受迫 e 在 a 通道工作时下不到阈值）；
            //   ② 加 |C0| 阈（安静时刻）⇒ 同样交不出去（远场收相位期间 a 通道一直忙）；
            //   ③ 提前解除 e 通道的门控与限权 ⇒ **读数逐位不变**（e 通道不是瓶颈）。
            //   ⇒ 交班时刻只能用距离判据；代价见 §16 的账单分解。
            // 交班：**正常走近圈（5%）**，或 **给够机会后的外圈（10% + 2 倍预算）**。
            //   只看距离会让"远场收到 6~9% 就收不动"的场景永远交不出去（实测 T5@96000）；
            //   而一律放大到 10% 又让终端更早介入、买得更贵（入口 1,290→2,540 km，账单 ×1.8）。
            //   ⇒ 结合：先给它 5% 的圈和 2 倍预算的机会，仍旧到不了再在外圈接手。
            bool late = relDist < TerminalLateFraction * rMag && t > TerminalLateBudgets * TimeBudget0;
            InTerminal = TerminalHandoff && (relDist < TerminalEngageFraction * rMag || late);
            // **末段滑道律**（比 CW 更靠内）：无截止时刻 ⇒ 不会"每轮重起一段 860s 弧"
            //   （游戏实测：CW 滚动执行时 dist 收得住而 relVel 卡在 18~20 m/s 不降）。
            bool slideMode = TerminalHandoff && TerminalSlideFraction > 0.0
                             && relDist < TerminalSlideFraction * rMag;
            if (InTerminal)
            {
                // 中端用的相对量（RTN 展开与诊断都要）
                DVector3 relPosW = r - target.PosAt(t);
                DVector3 relVelW0 = v - target.VelAt(t);
                // ── 终端 = CW 两冲量**闭式解**（交会的"温柔解"）──────────────────
                //   上一版用"滚动 Lambert 到点"= **拦截**解：接近率由几何决定，tof 受上限所限
                //   ⇒ 4,380 km 处接管要 730 m/s 接近率 ⇒ 总 Δv 1011 m/s。
                //   而交会的温柔解是**相位式**（小幅改 a、让时间补相位），Δv 只需 ~50 m/s。
                //   CW 是解析的、无分支 ⇒ 也避开了 Gooding 单圈解在 T≈T_orb 的退化支。
                DVector3 xHat = rHat;                                   // 径向向外
                DVector3 zHat = DVector3.Cross(r, v).normalized;         // 轨道法向
                DVector3 yHat = DVector3.Cross(zHat, xHat).normalized;   // 沿迹顺行
                double nOrb = Math.Sqrt(mu / (rMag * rMag * rMag));
                DVector3 rRelRTN = new DVector3(DVector3.Dot(relPos, xHat),
                                                DVector3.Dot(relPos, yHat),
                                                DVector3.Dot(relPos, zHat));
                DVector3 relVelW = v - target.VelAt(t);
                DVector3 vRelRTN = new DVector3(DVector3.Dot(relVelW, xHat),
                                                DVector3.Dot(relVelW, yHat),
                                                DVector3.Dot(relVelW, zHat));
                // ── 两个温柔原语各管一段，每次取更省的 ──────────────────────
                //   · **CW 闭式**（线性）：近处准（|r_rel| ≲ 5% r），远处线性化失效；
                //   · **多圈 Lambert**（精确）：任意距离都成立，nrev 给出相位式分支；
                //     单圈 nrev=0 在 tof≈T_orb 会落到退化支（实测全发散），必须带 nrev。
                double tOrbT = 2.0 * Math.PI / nOrb;
                // ⚠ 兜底（消相对速度）**不参与成本比较**：它的"成本" |v_rel| 在速度已配上时
                //   小得虚假，于是终端会每轮都选它、永远不修位置偏差——实测表现为
                //   "rel≈0 而 dist≈末面外 z"（T6：rel=0.3 m/s，dist=335 km，99.99% 在面外）。
                //   兜底只在两个正式原语都失败时启用。
                DVector3 bestDv = target.VelAt(t) - v;
                DVector3 bestDv2W = DVector3.zero;
                double bestCost = double.PositiveInfinity;
                LastTerminalToF = 0.0;
                LastTerminalKind = "兜底";

                // (1) CW 闭式
                // 视界选择：**滚动执行下按闭环代价选，不按开环计划最省选**（§19.2/§19.6）。
                //   实测：按 min-cost 选会挑到"计划便宜、闭环执行贵"的视界（T6 终端 = 自身理想 7.5×）。
                double tofUse = (TerminalUseShortestHorizon ? TerminalHorizonFrac : 0.0) * 2.0 * Math.PI / nOrb;
                bool cwOk;
                DVector3 cwDv1 = DVector3.zero;
                DVector3 cwDv2 = DVector3.zero;
                double cwCost = 0.0;
                if (TerminalUseShortestHorizon)
                {
                    if (TerminalUseExactLambert)
                    {
                        // **精确**：Lambert 到目标在 t+tof 的位置（短视界 ⇒ 单圈解良态，不在退化支附近）
                        DVector3 rTgtL = target.PosAt(t + tofUse);
                        try
                        {
                            var (viL, vfL) = Gooding.Solve(mu, r, rTgtL, tofUse, TransferGeometry.ShortWay);
                            cwDv1 = viL - v;
                            cwCost = cwDv1.magnitude + (target.VelAt(t + tofUse) - vfL).magnitude;
                            cwOk = true;
                        }
                        catch (Exception) { cwOk = false; }
                    }
                    else
                    {
                        cwOk = CwRendezvousCore.Solve(rRelRTN, vRelRTN, nOrb, tofUse, out var d1s, out var d2s);
                        if (cwOk)
                        {
                            cwDv1 = d1s; bestDv2W = d2s;
                            // ⚠ **打靶修正试过，发散**（§19.7）：用 CW 的 Φ_rv⁻¹ 当雅可比、对**精确 Kepler**
                            //   传播做牛顿迭代 —— 无阻尼与阻尼 0.3 都发散（T6 飞到 52,932 km、烧光 4,938 m/s）。
                            //   根因：在这个分离（~5% 轨道半径）与视界（0.15T_orb）上，CW 雅可比与真实
                            //   Kepler 灵敏度差得太远，迭代**不是压缩映射**。
                            //   ⇒ 修这个 1.74× 需要**真正的非线性两冲量交会求解器**（带准确雅可比的打靶，
                            //     或保住"交会结构"的多圈 Lambert），不是参数调整。**本批不做，如实登记。**
                        }
                    }
                }
                else
                {
                    cwOk = CwRendezvousCore.SolveBest(rRelRTN, vRelRTN, nOrb, out var planB);
                    if (cwOk) { cwDv1 = planB.Dv1; cwCost = planB.TotalDv; tofUse = planB.ToF; }
                }
                if (cwOk)
                {
                    bestDv = xHat * cwDv1.x + yHat * cwDv1.y + zHat * cwDv1.z;
                    bestDv2W = xHat * cwDv2.x + yHat * cwDv2.y + zHat * cwDv2.z;
                    bestCost = cwCost;
                    LastTerminalToF = tofUse;
                    LastTerminalKind = TerminalUseExactLambert ? "Lambert(精确)" : (TerminalUseShortestHorizon ? "CW(短视界)" : "CW");
                }

                // (2) 多圈 Lambert：**只在 CW 无解时兜底**（见下）
                if (double.IsPositiveInfinity(bestCost))
                {
                    for (int ri = 0; ri <= 1; ri++)
                    {
                        for (int fi = 0; fi <= 2; fi++)
                        {
                            double tofL = (0.2 + 0.2 * fi) * tOrbT;
                            DVector3 rTgt = target.PosAt(t + tofL);
                            try
                            {
                                var (vi, vf) = Gooding.Solve(mu, r, rTgt, tofL, TransferGeometry.ShortWay, ri);
                                DVector3 d1 = vi - v;
                                DVector3 d2 = target.VelAt(t + tofL) - vf;
                                double cost = d1.magnitude + d2.magnitude;
                                if (cost < bestCost)
                                {
                                    bestCost = cost;
                                    bestDv = d1;
                                    LastTerminalToF = tofL;
                                    LastTerminalKind = "Lambert nrev=" + ri;
                                }
                            }
                            catch (Exception) { }
                        }
                    }
                }
                // ⚠ **不跨原语比较成本**。滚动执行下每轮终点都会前推一个 T，跨原语比较等于拿不同的
                //   "到达时刻"当同一件事比——那不是成本，是错觉。实测后果：`Lambert @0.25·T_orb`
                //   在成本上"赢"过 CW，却对**纯面外偏移**进极限环（T6：dist 335 km，花 2159 m/s，
                //   而面外通道自己只花了 92.8）。
                //   规则：**CW（闭式、无分支、视界避开奇点带）优先；CW 无解才退到多圈 Lambert。**
                if (double.IsPositiveInfinity(bestCost)) bestDv = target.VelAt(t) - v;  // 两个原语都没解
                double lambdaSlide = Math.Min(1.0 / TerminalSlideTau, TerminalSlideClosingCap / Math.Max(relDist, 1.0));
                DVector3 dvNeed;
                if (CommitTwoImpulse && !double.IsNegativeInfinity(PlannedDv2Tick))
                {
                    if (t < PlannedDv2Tick) dvNeed = DVector3.zero;     // 到达前不发新烧
                    else
                    {
                        // ★ 条件语义（B1.2）：到达时刻**按当时状态重解**，而不是烧那枚冻结的 dv2。
                        //   目标条件 = "到达点消掉相对速度" ⇒ 到达即"就地匹配速度"（`target.VelAt - v`）。
                        //   ⚠ 这正是计划稿那条待验假设的实现：**根因是冻结向量还是提交本身**。
                        dvNeed = CommitResolveAtBurn ? (target.VelAt(t) - v) : PlannedDv2;
                        PlannedDv2Tick = double.NegativeInfinity;
                    }
                }
                else
                {
                    dvNeed = bestDv;
                    if (CommitTwoImpulse) { PlannedDv2 = bestDv2W; PlannedDv2Tick = t + LastTerminalToF; }
                }
                DVector3 dirT = dvNeed.sqrMagnitude > 1e-18 ? dvNeed.normalized : DVector3.zero;
                double aAvail = Math.Max(ship.MainAccel, ship.RcsAccel);
                double dvStep = Math.Min(dvNeed.magnitude, aAvail * dt);
                if (dvStep < ActuatorDeadband) dvStep = 0.0;   // 死区（与游戏落地段同口径）
                bool alignedT = DVector3.Dot(ship.MainAxis, dirT) >= RendezvousShip.ThrustConeCos;
                var kindT = RendezvousGuidanceCore.SelectActuator(dvStep, ship.MainAccel,
                    MainOnly ? 0.0 : ship.RcsAccel, alignedT);
                double dvBeforeT = ship.MainDv + ship.RcsDv;
                thrustAccel = ship.ApplyThrust(kindT, dirT, dvStep, dt);
                TerminalDv += (ship.MainDv + ship.RcsDv) - dvBeforeT;
                TerminalSteps++;
                if (TerminalSteps == 1)
                {
                    TerminalEntryDist = relDist; TerminalEntryRel = relVelW0.magnitude;
                    // RTN 分解：终端账单由"远场交班时留下的状态"决定，分开记才能定位
                    TerminalEntryR = new DVector3(DVector3.Dot(relPosW, rHat),
                        DVector3.Dot(relPosW, DVector3.Cross(nrm, rHat).normalized), DVector3.Dot(relPosW, nrm));
                    TerminalEntryV = new DVector3(DVector3.Dot(relVelW0, rHat),
                        DVector3.Dot(relVelW0, DVector3.Cross(nrm, rHat).normalized), DVector3.Dot(relVelW0, nrm));
                }
                ship.Propagate(thrustAccel, mu, dt);
                RecordDv(t, dvStep);

                double rAfterT = ship.Pos.magnitude;
                if (rAfterT < ship.MinRadius) ship.MinRadius = rAfterT;
                if (rAfterT < SafeRadius) ship.Crashed = true;
                LastPhaseErr = phaseErr; LastPhaseRate = lawRes.PhaseRate;

                DVector3 drT = target.PosAt(t + dt) - ship.Pos;
                dist = drT.magnitude;
                relSpeed = (target.VelAt(t + dt) - ship.Vel).magnitude;
                return;
            }

            // ── 方向与执行器（远场：切向）──
            // 幅值连续 ⇒ 方向恒为 ±切向、不翻转 ⇒ 无需指令死区（连续律之前的老问题）。
            if (aCmd3.magnitude > 1e-12 && aCmd3.magnitude * dt >= ActuatorDeadband)
            {
                DVector3 aVec = aCmd3;
                DVector3 dirN = aVec.normalized;
                double need = aVec.magnitude * dt;
                if (ModelSafetyClip)
                {
                    double k = RendezvousCore.MaxSafeDvScale(ship.Pos, ship.Vel, dirN * need, SafeRadius, mu);
                    if (k < 0.05) need = 0.0;            // 与游戏一致：钳死了 ⇒ 本轮不点火
                    else if (k < 1.0) need *= k;         // 能烧多少烧多少
                }
                bool aligned = DVector3.Dot(ship.MainAxis, dirN) >= RendezvousShip.ThrustConeCos;
                // MainOnly：把 aRcs 传 0 即可 —— SelectActuator 已经负责"只剩主网且未对准时
                // 仍返回 Main"，让 ApplyThrust 继续 SlewToward。直接返回 None 会让船永远转不过去。
                var kind = RendezvousGuidanceCore.SelectActuator(need, ship.MainAccel,
                    MainOnly ? 0.0 : ship.RcsAccel, aligned);
                thrustAccel = ship.ApplyThrust(kind, dirN, need, dt);
            }
            else
            {
                thrustAccel = DVector3.zero;
            }

            ship.Propagate(thrustAccel, mu, dt);
            if (Math.Abs(aT) > 1e-12) RecordDv(t, Math.Abs(aT) * dt);

            double rAfter = ship.Pos.magnitude;
            if (rAfter < ship.MinRadius) ship.MinRadius = rAfter;
            if (rAfter < SafeRadius) ship.Crashed = true;

            if (Trace.Count < 400 && t % 10000.0 < dt)
            {
                OrbitMath.GetEccentricity(r, v, out _, out double eTrace, mu);
                Trace.Add($"t={t,7:F0} | φ={phaseErr,8:F4} δn={lawRes.PhaseRate:E2} C0={C0,7:F3} " +
                          $"a={OrbitMath.SemiMajorAxis(r, v, mu) / 1000.0,7:F0}km e={eTrace:F4} r={rMag / 1000.0,7:F0}km");
            }

            LastPhaseErr = phaseErr;
            LastPhaseRate = lawRes.PhaseRate;

            DVector3 dr = target.PosAt(t + dt) - ship.Pos;
            dist = dr.magnitude;
            relSpeed = (target.VelAt(t + dt) - ship.Vel).magnitude;
        }

        private static double WrapPi(double a)
        {
            while (a > Math.PI) a -= 2.0 * Math.PI;
            while (a < -Math.PI) a += 2.0 * Math.PI;
            return a;
        }
    }
}
