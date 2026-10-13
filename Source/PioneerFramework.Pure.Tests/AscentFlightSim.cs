using System;
using System.Collections.Generic;
using PioneerFramework;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **爬升段离线台架（[A6+] 2026-10-06 建）**：把 `LaunchGuidanceCore` 的闭环制导 + 火箭方程
    /// + **可选气动**（[A2-2] 的 `AeroDrag`）按**游戏物理步 dt** 积分，输出终态与**损失分解**。
    ///
    /// 用途：
    /// ① **复现游戏内日志**（同一枚测试火箭：干重 4000 / 燃料 50000 / ve 3500 / ṁ 300；
    ///    φ=51.7° 高纬顺行发射）——台架与日志对得上，才有资格谈"哪一段贵"；
    /// ② 量 **阻力损失 / 重力损失 / 总 Δv** 三项（游戏里从日志推不出来）；
    /// ③ 离线试修法（改燃料 / 改型线参数），不给用户加游戏内轮次。
    ///
    /// **忠实性边界（照实登记）**：
    /// · 引力 = 只用中心天体（两体）——与大气档"只算中心天体"的掩码一致；月球项 ~8e-4 m/s² 忽略；
    /// · 大气 = 唯一提供者 `AtmosphereModel`（64 点表）+ 唯一算式 `AeroDrag`；**姿态 = 顺气流**
    ///   （与 [A6+] 的指向纪律一致；侧飞工况由 `noseFirst:false` 显式打开，用于对照旧故障）；
    /// · 制导 = `LaunchGuidanceCore`（逐字，不抄算式）；每步 dv 上限 = 一步能烧出的最大 Δv
    ///   （`ve·ln(m/(m−ṁ·dt))`，与执行器"按 ṁ·dt 扣料"同口径）；
    /// · 积分 = 半隐式欧拉（游戏用三阶泰勒 + 冲量；dt=2 s 下两者差在米级，不影响损失量级）。
    ///
    /// ⚠ 本文件**只进纯测试工程**（不进生产装配）——它是尺子，不是生产件。
    /// </summary>
    public static class AscentFlightSim
    {
        public struct Setup
        {
            /// <summary>制导型线覆盖（null = 用 `LaunchGuidanceParams.CreateDefault`）。型线扫描用。</summary>
            public struct GuidanceKnobs
            {
                public double vAscent;
                public double altPitchStart;
                public double altPitchEnd;
                public double vRadMax;
                public double tau;
                /// <summary>高空段切向目标：false = 现行"常数 vC(目标半径)"; true = **局部圆轨速度 √(μ/r)**（实验）。</summary>
                public bool localCircularTarget;
                /// <summary>**面内切向**（实验）：切向基准用 `ĥ×r̂`（轨道面内顺行水平）而不是全局东方。</summary>
                public bool inPlaneTangent;
                /// <summary>径向通道增益（1 = 现状"一步把误差全交付"；&lt;1 = 抗延迟）。</summary>
                public double radialGain;
                /// <summary>切向通道增益（1 = 现状）。</summary>
                public double tanGain;
                /// <summary>径向**重力前馈**：目标里加 `+g·dt`，补偿"一拍延迟里重力吃掉的那段径向速度"。</summary>
                public bool gravityLead;
            }

            public GuidanceKnobs? guidanceOverride;

            public double planetRadius;      // m
            public double mu;                // m³/s²
            public double dayLengthTicks;    // 自转周期（tick）
            public double latitudeRad;       // 发射纬度
            public double spawnAltitude;     // 出生高度 (m)
            public double dryMass;           // kg
            public double fuelMass;          // kg
            public double ve;                // 排气速度
            public double massFlow;          // ṁ (kg/s)
            public double dt;                // 物理步长 (s)
            public double rTarget;           // 目标圆轨半径 (m)

            // 气动（drag=false 时全忽略）
            public bool drag;
            public bool noseFirst;           // true = 顺气流（带限速/死区的真实对准）；false = 姿态锁死（旧故障）
            /// <summary>对准死区（rad）：生产 = `SpaceGameComp.DefaultThrustAlignCos` = cos(1.5°) ⇒ ≈0.02618。</summary>
            public double alignDeadZoneRad;
            /// <summary>魔法动量轮最大角速率（rad/s）：生产 = `SpaceEntityGroup.DefaultMaxAngularRate` = 5°/s。</summary>
            public double maxAngularRateRad;
            /// <summary>出生姿态：false = 世界系单位阵（游戏现状，长棍 = 侧向 90 m²）；true = 长轴顺当地铅垂。</summary>
            public bool initialNoseUp;
            /// <summary>**延迟拍数**（0 = 同拍；1+ = 冲量延后 n 个物理步施加；游戏现状 ≥1）。</summary>
            public int delaySteps;
            /// <summary>
            /// **制导用预测量**（2026-10-06 修法）：算制导用的状态 = 当前 + **在飞冲量**（下一步首落地）
            /// + **步内位移**（`pos + vel·dt`）。
            /// 依据：`RocketLaunchController`（字母序在 `SpaceGameComp` 之前）先 tick ⇒ 它看到的状态**不含**
            /// 上一拍命令、当拍才落地的冲量 ⇒ 同一个修正被**逐帧重复索要**（交付两次、来回抵消 = 抖 + 双倍油耗），
            /// 且"到达"判定用的位置滞后一整个物理步（实测 ΔR=10.4 km 卡在容差边上）。
            /// </summary>
            public bool predictState;
            /// <summary>**位置外推**（`pos + vel·dt`）：只修"到达判定滞后一整步"，大 dt 下会冲过头（台架实测）。</summary>
            public bool predictPos;
            public double rho0, scaleHeight, atmoTop;

            public static Setup TestRocket(double dayTicks = 60000.0) => new Setup
            {
                planetRadius = 5_000_000.0,
                mu = 2.0e14,
                dayLengthTicks = dayTicks,
                latitudeRad = 51.7 * Math.PI / 180.0,   // 与本轮游戏日志同一发射场
                spawnAltitude = 10_000.0,
                dryMass = 4000.0,
                fuelMass = 50_000.0,
                ve = 3500.0,
                massFlow = 300.0,
                dt = 2.0,
                rTarget = 5_500_000.0,
                drag = false,
                noseFirst = true,
                alignDeadZoneRad = 0.0261799,      // acos(0.999657325) = 1.5°
                maxAngularRateRad = 0.0872665,     // 5°/s（SpaceEntityGroup.DefaultMaxAngularRate）
                initialNoseUp = false,             // 游戏现状：单位阵出生（长棍侧向）
                rho0 = 1.2,
                scaleHeight = 8000.0,
                atmoTop = 100_000.0,
            };
        }

        public struct Result
        {
            public bool arrived;
            public double fuelLeft;
            public double timeSeconds;
            public double finalR, finalV, finalVRad;
            public double apogee, perigee, semiMajorAxis;
            public double dvSpent;        // Σ 实际施加的 |dv|
            public double dragLoss;       // ∫|a_drag|·dt
            public double gravityLoss;    // ∫g·sinγ·dt（sinγ = v̂·r̂）
            public double maxAltitude;
            public int steps;
            /// <summary>**燃料耗尽那一刻**的快照（诊断用：烧完之后的坠落/再入会污染损失读数）。</summary>
            public double burnoutAltKm, burnoutV, burnoutVRad, burnoutApoKm, burnoutPeriKm, burnoutTime;
            public double burnoutDvSpent, burnoutDragLoss, burnoutGravityLoss;
            /// <summary>逐步轨迹（前 40 步，与游戏内探针行同格式拼好，供人工对照）。</summary>
            public List<string> trace;
            /// <summary>首次越过 80 km 时的高度/速度/燃料（与游戏日志逐点对照用：判别燃料差额出在前段还是后段）。
            /// `-1` = 未越过（在 <see cref="Run"/> 里显式置初值——结构体不许有字段初始值设定项）。</summary>
            public double fuelAt80km, vAt80km, tAt80km;

            /// <summary>**30 s 同频采样**（与游戏探针行同列：t / r(km) / v / vRad / |dv| / 燃料 kg）——
            /// 逐段对账用（每段 Δv = ve·ln(m₁/m₂)）。</summary>
            public List<(double t, double rKm, double v, double vRad, double dv, double fuel)> samples;
        }

        public static Result Run(in Setup s, double maxSeconds = 20000.0)
        {
            double omega = 2.0 * Math.PI / s.dayLengthTicks;
            var surface = SurfaceFrame.SurfaceState(s.planetRadius, s.latitudeRad, 0.0, omega, 0.0);
            var birth = SurfaceFrame.SurfaceStateAbove(surface, s.spawnAltitude);
            DVector3 pos = birth.pos;
            DVector3 vel = birth.vel;
            double fuel = s.fuelMass;
            var p = LaunchGuidanceParams.CreateDefault(s.planetRadius);
            p.rTarget = s.rTarget;
            if (s.guidanceOverride.HasValue)
            {
                var k = s.guidanceOverride.Value;
                p.vAscent = k.vAscent;
                p.altPitchStart = k.altPitchStart;
                p.altPitchEnd = k.altPitchEnd;
                p.vRadMax = k.vRadMax;
                p.tau = k.tau;
            }
            var shape = AeroShape.FromBox(new DVector3(3.0, 3.0, 30.0));   // [补3] 长棍

            // 出生姿态：默认 = 世界系单位阵（游戏现状）；可选 = 长轴顺当地铅垂（实验：省掉 90° 转正瞬态）
            DQuaternion att = DQuaternion.identity;   // 下写字面量是既有名字
            if (s.initialNoseUp)
            {
                DVector3 radial0 = pos.normalized;
                DVector3 axis0 = DVector3.Cross(DVector3.forward, radial0);
                double ang0 = Math.Acos(Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(DVector3.forward, radial0))));
                if (axis0.sqrMagnitude > 1e-12)
                    att = DQuaternion.AngleAxis(ang0, axis0.normalized);
            }

            var r = new Result();
            var inFlight = new System.Collections.Generic.Queue<DVector3>();   // 在飞冲量（下一步首落地）
            r.fuelAt80km = -1.0;
            r.samples = new List<(double, double, double, double, double, double)>();
            r.trace = new List<string>();
            DVector3 up = DVector3.up;
            int maxSteps = (int)(maxSeconds / s.dt);
            for (int i = 0; i < maxSteps; i++)
            {
                double rr = pos.magnitude;
                if (rr < s.planetRadius) break;              // 撞地
                double mass = s.dryMass + fuel;

                // 制导用状态：默认 = 游戏现状（**不含**在飞冲量）；`predictState` = 修法（含）
                DVector3 lawPos = pos, lawVel = vel;
                if (s.predictState)
                {
                    DVector3 pendingNow = inFlight.Count > 0 ? inFlight.Peek() : DVector3.zero;
                    lawVel = vel + pendingNow;                       // 含在飞冲量（修"重复索要"）
                    if (s.predictPos)
                        lawPos = pos + lawVel * s.dt;                // 位置外推（只治"到达判定滞后"）
                }
                var step = s.guidanceOverride.HasValue
                    ? ComputeStepLocalCircular(lawPos, lawVel, s.mu, p,
                        s.guidanceOverride.Value.localCircularTarget, s.guidanceOverride.Value.inPlaneTangent,
                        s.guidanceOverride.Value.radialGain, s.guidanceOverride.Value.tanGain,
                        s.guidanceOverride.Value.gravityLead, s.dt)
                    : LaunchGuidanceCore.ComputeStep(lawPos, lawVel, s.mu, p);
                DVector3 dvApplied = DVector3.zero;
                DVector3 dvPending = DVector3.zero;   // 本步算出的指令，下步才施加（一拍延迟）
                if (!step.arrived && step.dv.sqrMagnitude > 0.0 && fuel > 1e-9)
                {
                    double dvMax = s.ve * Math.Log(mass / (mass - s.massFlow * s.dt));
                    double dv = Math.Min(step.dv.magnitude, Math.Max(0.0, dvMax));
                    if (dv > 0.0)
                    {
                        double dm = mass * (1.0 - Math.Exp(-dv / s.ve));
                        dm = Math.Min(dm, fuel);
                        fuel -= dm;
                        dvPending = step.dv.normalized * dv;
                    }
                }

                // 引力（两体，**游戏同款核**：含 jerk ⇒ 大 dt 下也稳）
                NBodyIntegrator.ComputeBodyAccelJerk(pos, vel,
                    DVector3.zero, DVector3.zero, s.mu, out DVector3 acc, out DVector3 jerk);
                double rrG = rr;
                double g = s.mu / (rrG * rrG);
                r.gravityLoss += g * Math.Abs(DVector3.Dot(vel.normalized, pos.normalized)) * s.dt;

                // 气动
                if (s.drag && rr < s.planetRadius + s.atmoTop)
                {
                    var profile = AtmosphereModel.Build(s.rho0, s.scaleHeight, s.atmoTop);
                    double rho = profile.Density(rr - s.planetRadius);
                    if (rho > 0.0)
                    {
                        DVector3 vAir = vel - SurfaceFrame.SurfaceVelocity(pos, omega);
                        if (vAir.sqrMagnitude > 1e-12)
                        {
                            DVector3 vHat = vAir.normalized;
                            // ── 姿态（**带限速与死区的真实对准**，照生产 `AlignShipAxis` 的口径）──────────
                            // 早期版本假设"瞬时顺气流" ⇒ 低估了头几秒的迎风面积（长棍侧着挨风 = 90 m² vs 头锥 9 m²）
                            // ⇒ 与游戏日志逐点对照时燃料偏乐观（台架 80 km 处 4203 kg vs 游戏 3451 kg）。
                            DVector3 nose = DVector3.Rotate(DVector3.forward, att);
                            if (s.noseFirst)
                            {
                                double dotNA = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(nose, vHat)));
                                double ang = Math.Acos(dotNA);
                                if (ang > s.alignDeadZoneRad)
                                {
                                    double dAngle = Math.Min(s.maxAngularRateRad * s.dt, ang);
                                    DVector3 axis = DVector3.Cross(nose, vHat);
                                    if (axis.sqrMagnitude > 1e-12)
                                        att = DQuaternion.AngleAxis(dAngle, axis.normalized) * att;
                                }
                                nose = DVector3.Rotate(DVector3.forward, att);
                            }
                            DVector3 fwd = nose;
                            DVector3 sideRef = Math.Abs(fwd.y) < 0.9 ? DVector3.up : DVector3.forward;
                            DVector3 left = DVector3.Cross(sideRef, fwd).normalized;
                            DVector3 up2 = DVector3.Cross(fwd, left).normalized;

                            DVector3 aDrag = AeroDrag.Accel(rho, vAir, shape, fwd, left, up2, mass);
                            acc += aDrag;
                            r.dragLoss += aDrag.magnitude * s.dt;
                        }
                    }
                }

                // 冲量入队（在飞）；延迟 = 队列深度（0 = 本步就落地 = 理想；1+ = 延后 n 步）
                inFlight.Enqueue(dvPending);
                if (inFlight.Count > s.delaySteps) dvApplied = inFlight.Dequeue();
                if (inFlight.Count == 0) { }   // 队列空 = 无在飞（供 predictState 读取）
                r.dvSpent += dvApplied.magnitude;
                vel += dvApplied;                                   // 冲量（步首施加，与游戏一致）
                NBodyIntegrator.ThirdOrderTaylorStep(pos, vel, acc, jerk, s.dt, out pos, out vel);

                r.steps = i + 1;
                r.timeSeconds = r.steps * s.dt;
                if (r.steps % 15 == 0)   // 30 s 同频（dt=2 s）：与游戏探针行逐点对照
                {
                    r.samples.Add((r.timeSeconds, pos.magnitude / 1000.0, vel.magnitude,
                        DVector3.Dot(vel, pos.normalized), step.dv.magnitude, fuel));
                }
                if (r.steps % 75 == 0 && r.trace.Count < 40)
                {
                    double altT = pos.magnitude - s.planetRadius;
                    double vRadT = DVector3.Dot(vel, pos.normalized);
                    r.trace.Add(string.Format(
                        "t={0:F0} alt={1:F0}km v={2:F1} vRad={3:F1} |dv|={4:F1} 燃={5:F0}kg",
                        r.timeSeconds, altT / 1000.0, vel.magnitude, vRadT,
                        step.dv.magnitude, fuel));
                }
                double alt = pos.magnitude - s.planetRadius;
                if (alt > r.maxAltitude) r.maxAltitude = alt;
                if (r.fuelAt80km < 0.0 && alt > 80_000.0)   // 首次越过 80 km：与游戏日志对照的基准点
                {
                    r.fuelAt80km = fuel;
                    r.vAt80km = vel.magnitude;
                    r.tAt80km = r.timeSeconds;
                }

                if (step.arrived)
                {
                    r.arrived = true;
                    break;
                }

                // 燃料耗尽 ⇒ **在这里快照并停**（烧完之后的坠落/再入会把 dragLoss 抬到 ~10 km/s 的假数）
                if (fuel <= 1e-9)
                {
                    r.burnoutTime = r.timeSeconds;
                    r.burnoutAltKm = (pos.magnitude - s.planetRadius) / 1000.0;
                    r.burnoutV = vel.magnitude;
                    r.burnoutVRad = DVector3.Dot(vel, pos.normalized);
                    r.burnoutDvSpent = r.dvSpent;
                    r.burnoutDragLoss = r.dragLoss;
                    r.burnoutGravityLoss = r.gravityLoss;
                    double rr2 = pos.magnitude;
                    double en = vel.sqrMagnitude / 2.0 - s.mu / rr2;
                    if (en < 0.0)
                    {
                        double a2 = -s.mu / (2.0 * en);
                        double h2 = DVector3.Cross(pos, vel).magnitude;
                        double e2 = Math.Sqrt(Math.Max(0.0, 1.0 + 2.0 * en * h2 * h2 / (s.mu * s.mu)));
                        r.burnoutApoKm = a2 * (1.0 + e2) / 1000.0;
                        r.burnoutPeriKm = a2 * (1.0 - e2) / 1000.0;
                    }
                    break;
                }
            }

            r.fuelLeft = fuel;
            r.finalR = pos.magnitude;
            r.finalV = vel.magnitude;
            r.finalVRad = DVector3.Dot(vel, pos.normalized);
            // 轨道要素（两体）
            double v2 = vel.sqrMagnitude;
            double energy = v2 / 2.0 - s.mu / r.finalR;
            if (energy < 0.0)
            {
                r.semiMajorAxis = -s.mu / (2.0 * energy);
                double h = DVector3.Cross(pos, vel).magnitude;
                double e = Math.Sqrt(Math.Max(0.0, 1.0 + 2.0 * energy * h * h / (s.mu * s.mu)));
                r.apogee = r.semiMajorAxis * (1.0 + e);
                r.perigee = r.semiMajorAxis * (1.0 - e);
            }
            return r;
        }

        private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);

        /// <summary>
        /// **台架实验：局部圆轨切向目标**（只在本文件里；用于回答"型线结构是不是病灶"）。
        /// 与 `LaunchGuidanceCore` 的唯一差别：倾倒段/高空段的切向目标 = **√(μ/r_local)**（重力转弯的自然目标），
        /// 而不是常数 `vC(rTarget)`。**只有台架证明有效才谈移植**——现在不许进生产。
        /// </summary>
        private static LaunchGuidanceStep ComputeStepLocalCircular(DVector3 pos, DVector3 vel, double gm,
            in LaunchGuidanceParams p, bool localCircular, bool inPlane,
            double gainRadial = 1.0, double gainTan = 1.0, bool leadGravity = false, double dtSeconds = 2.0)
        {
            double r = pos.magnitude;
            if (r < 1e-3 || gm <= 0.0 || p.rTarget <= 0.0)
                return new LaunchGuidanceStep(DVector3.zero, false);
            double vLocal = Math.Sqrt(gm / r);
            DVector3 radialOut = pos / r;
            DVector3 tangent;
            DVector3 h = DVector3.Cross(pos, vel);
            if (inPlane && h.sqrMagnitude > 1e-6)
                tangent = DVector3.Cross(h, radialOut).normalized;   // 轨道面内顺行水平
            else
                tangent = DVector3.Cross(DVector3.up, radialOut).normalized * p.progradeSign;
            if (tangent.sqrMagnitude < 1e-12)
                return new LaunchGuidanceStep(DVector3.zero, false);

            double deltaR = p.rTarget - r;
            double radialSpeed = Clamp(deltaR / p.tau, -p.vRadMax, p.vRadMax);
            double vTan = DVector3.Dot(vel, tangent);
            double alt = r - p.planetRadius;
            double radialTarget, tanTarget;
            if (alt < p.altPitchStart)
            {
                radialTarget = p.vAscent;
                tanTarget = vTan;
            }
            else if (alt < p.altPitchEnd)
            {
                double t = Clamp((alt - p.altPitchStart) / (p.altPitchEnd - p.altPitchStart), 0.0, 1.0);
                radialTarget = p.vAscent * (1.0 - t) + radialSpeed * t;
                tanTarget = vTan * (1.0 - t) + (localCircular ? vLocal : Math.Sqrt(gm / p.rTarget)) * t;
            }
            else
            {
                radialTarget = radialSpeed;
                tanTarget = localCircular ? vLocal : Math.Sqrt(gm / p.rTarget);
            }

            double rGain = gainRadial > 0.0 ? gainRadial : 1.0;
            double tGain = gainTan > 0.0 ? gainTan : 1.0;
            double lead = leadGravity ? (gm / (r * r)) * dtSeconds : 0.0;
            DVector3 desiredVel = radialOut * (radialTarget + lead) + tangent * tanTarget;
            DVector3 dv = (desiredVel - vel);
            dv = radialOut * (DVector3.Dot(dv, radialOut) * rGain) + tangent * (DVector3.Dot(dv, tangent) * tGain);
            bool arrived = Math.Abs(deltaR) < p.tolR
                && Math.Abs(vel.magnitude - Math.Sqrt(gm / p.rTarget)) < p.tolV;
            if (dv.magnitude < p.deadband)
                return new LaunchGuidanceStep(DVector3.zero, arrived);
            return new LaunchGuidanceStep(dv, arrived);
        }
    }
}
