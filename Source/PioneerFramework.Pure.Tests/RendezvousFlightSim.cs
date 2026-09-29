using System;
using System.Collections.Generic;
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
        /// <summary>主推轴（世界系，单位矢量）。D15：无 Map 船 = mainAxisShip 随 rotation 转。</summary>
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

}
