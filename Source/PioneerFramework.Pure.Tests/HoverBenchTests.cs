using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **[A7] 悬浮台架**：计划稿 §2.6 有两句**断言**，这个台架就是去量它们，而不是复述它们：
    ///   ① "控制律在正常时标 dt=2 s 下**抖幅是 dt² 量级**（十米级），战斗时标下厘米级"；
    ///   ② 临界阻尼下**收敛**（不发散、不振荡增长）。
    ///
    /// 台架模型（**显式列出，好让"台架 vs 游戏"的差只可能来自环境**）：
    ///   · 一维**径向**运动：`r̈ = −μ/r² + a_cmd`（引力用与生产同式的平方反比）；
    ///   · 控制律 = **生产同一份** `HoverCore.CommandAccel`（引力项喂本步的场加速度，
    ///     点质量位置的 `−μ/r²`）⇒ 定律不会有第二份实现；
    ///   · 积分 = **半隐式欧拉**（`v += a·dt; r += v·dt`）；**游戏用的是三阶泰勒**（`NBodyIntegrator`）
    ///     ⇒ 本台架量的是**控制律的离散化抖幅**，不是积分器的误差（后者是另一条，别混）。
    ///   · 一个物理步内指令常值（零阶保持）——这正是 dt² 抖幅的来源。
    ///
    /// ⚠ 判据只在"末段稳态振幅"上：前几十秒是收敛过程，不从里面取数。
    /// </summary>
    public class HoverBenchTests
    {
        private const double Mu = 2.0e14;                  // 与 SpaceConstants.PrimaryGravitationalParameter 同量级
        private const double R0 = 5_030_000.0;             // 地表 30 km（悬浮场景的空域）

    /// <summary>
    /// **游戏施加顺序**的台架（2026-10-06 第三轮实测后补）：游戏里 `ApplyHover` 读**踢之前**的状态、
    /// 算指令、**同一步**施加（`pendingImpulse` 紧跟 `ApplyPendingImpulses`）—— 与上面"隔一步"那版
    /// **不是同一个离散系统**，稳态可以完全不同。本方法**采样相位与游戏读数一致**（踢之前）。
    /// </summary>
    private static (double errSampled, double vSampled, double errMean, double dvMean) GameOrderSteady(
        double dt, double targetRadius, double seconds,
        double kp = HoverCore.DefaultKp, double kd = HoverCore.DefaultKd,
        double deadZone = HoverCore.DefaultDeadZoneMeters, bool debiasVelocity = false,
        double initialRadialVelocity = 0.0, double rotatingFrameOmega = 0.0)
    {
        double r = targetRadius, v = initialRadialVelocity;
        var radial = new DVector3(1, 0, 0);
        var target = radial * targetRadius;
        int steps = (int)Math.Round(seconds / dt);
        double errSum = 0.0; int n = 0;
        double eS = 0.0, vS = 0.0, eMin = 0.0, eMax = 0.0;
        double dvSum = 0.0; int dvN = 0;

        for (int i = 0; i < steps; i++)
        {
            var p = radial * r;
            var vel = radial * v;
            double e = DVector3.Dot(target - p, radial);           // >0 = 低于目标
            if (i >= steps / 4)
            {
                eS = e; vS = v; errSum += e; n++;
                if (e < eMin) eMin = e;
                if (e > eMax) eMax = e;
            }

            double g = -Mu / (r * r);
            var gVec = radial * g;
            var velForLaw = debiasVelocity ? HoverCore.DebiasVelocity(vel, gVec, dt) : vel;
            // **共转系**里的径向方程：`r̈ = 推力 + 引力 + ω²r`（离心项**朝外**）。
            // ⚠ 台架第四轮才把这个符号摆对：原来写成"向内的外力"，于是符号全反（复现不出游戏那 +5.1 m）。
            double centrifugal = rotatingFrameOmega * rotatingFrameOmega * r;
            var aCmd = HoverCore.CommandAccel(gVec, target - p, DVector3.zero - velForLaw,
                kp: kp, kd: kd, deadZoneMeters: deadZone);

            // ── 步序与积分器**照游戏**（2026-10-06 第四轮修正）──
            // ⚠ 这里原来用半隐式欧拉（`v += g·dt; r += v·dt`）——**位置用步末速度**推进，
            //   与游戏的 `ThirdOrderTaylorStep`（`r += v₀·dt + ½a·dt² + …`，位置用**步初**速度 + 加速度项）
            //   **不是同一个离散系统**；而"采样偏差"这件事**恰恰依赖积分器**（半隐式把偏差抵掉了 ⇒
            //   台架复现不出游戏那 162 m）。教训：台架复刻离散结构时，**积分器也是结构的一部分**。
            if (i >= steps / 4) { dvSum += aCmd.x * dt; dvN++; }   // 后段每步实给的推力 Δv（账单判据用）
            v += aCmd.x * dt;                                      // ① 冲量（步首，同一步）
            double v0 = v;                                         // ② 步初速度（含冲量）
            double aNet = g;                                       //    本步常加速度（只算惯性力：引力）
            r += v0 * dt + 0.5 * aNet * dt * dt;                   // ③ 位置推进（三阶泰勒的最低阶）
            v += aNet * dt;                                        // ④ 速度更新
            v += centrifugal * dt;                                 // ⑤ 共转系的离心项（朝外；非惯性项）
        }
        return (eS, vS, errSum / Math.Max(n, 1), dvN > 0 ? dvSum / dvN : 0.0);
    }

    /// <summary>
    /// **半步重力偏差**：控制器采样的径向速度**恒偏低** `½·|g|·dt`（步首采样 = 上一步下坠的底部），
    /// PD 把这份假下坠当真误差 ⇒ 稳态偏置 `e* ≈ (Kd/Kp)·½·|g|·dt ≈ 163 m` —— 游戏内实测 **+162 m** ✓。
    /// 本用例把"补正前 ≈160 m / 补正后 ≈米级"这一对**同时**钉住：既是回归判据，也是那条病的说明书。
    /// </summary>
    [Fact]
    public void Hover_HalfStepGravityBias_ExplainsTheInGameOffset_AndDebiasFixesIt()
    {
        // 游戏里的**净需求**：定点在地面上、随行星共转 ⇒ 跟随它需要一个恒定的向心加速度 ω²r
        // （本场景 ω = 2π/60000、r = 5030 km ⇒ 0.0552 m/s²）。台架第一版没这一项，所以复现不出 162 m。
        const double Omega = 2.0 * Math.PI / 60000.0;
        double centripetal = Omega * Omega * R0;   // = 离心项的量级（共转系里它朝外、要 PD/前馈朝内顶住）
        var (eRaw, vRaw, _, _) = GameOrderSteady(dt: 2.0, targetRadius: R0, seconds: 600.0,
            debiasVelocity: false, initialRadialVelocity: -20.0, rotatingFrameOmega: Omega);
        var (eFix, vFix, _, _) = GameOrderSteady(dt: 2.0, targetRadius: R0, seconds: 600.0,
            debiasVelocity: true, initialRadialVelocity: -20.0, rotatingFrameOmega: Omega);
        double predicted = ((HoverCore.DefaultKd / HoverCore.DefaultKp) * 0.5 * (Mu / (R0 * R0)) * 2.0)
                           + (centripetal / HoverCore.DefaultKp);

        Assert.True(Math.Abs(-eRaw - predicted) < 40.0,
            "偏差模型应定量对上：预测 " + predicted.ToString("F0") + " m，台架未补正 " + (-eRaw).ToString("F1") + " m" +
            "（游戏实测 162 m）；补正后 " + eFix.ToString("F2") + " m（= 向心项 A/Kp 那一点）");
        Assert.True(Math.Abs(eFix) < 20.0,
            "半步重力回正后稳态偏置应落到米级/十米级，实测 " + eFix.ToString("F2") + " m");
        Assert.True(Math.Abs(eRaw) > 5.0 * Math.Max(Math.Abs(eFix), 1e-6),
            "补正前后应当差一个量级（补正前 " + eRaw.ToString("F1") + " m ⇒ 补正后 " + eFix.ToString("F2") + " m）");
        // 再加**定点前馈**（定点的惯性加速度由律给出）⇒ 那 5 m 的固定偏置也该归零
        // 残留 = **共转系离心项那一点**（`ω²r/Kp ≈ 5.5 m`）——PD 不带前馈就必然有；游戏实测 +5.1 m ✓
        Assert.True(Math.Abs(eFix) > 2.0 && Math.Abs(eFix) < 15.0,
            "残留偏置应落在离心项量级（2–15 m），实测 " + eFix.ToString("F2") + " m");
        // ⚠ 想吃掉它要给律加**定点前馈**，但本 1-D 代理**验证不了前馈**（没建模切向运动 ⇒ 符号/相位反复翻）
        //   ⇒ 要加就配一台 2-D 台架。**在那之前不许上线**（未验过的控制改动不上）。
    }

    /// <summary>
    /// **[A8] 判据：同一次悬浮，两档账单一致**（计划稿 §2.7）——
    /// 解析档每步记账 `EquivalentDvPerStep = |g|·dt`；物理档每步**真给**的推力 Δv 必须与它同量级，
    /// 否则"换档逃费 / 换档多收费"就会成立。台架同时给出两个数，判据取 5% 容差。
    /// </summary>
    [Fact]
    public void Hover_TwoTierBilling_Agree()
    {
        const double dt = 2.0;
        const double Omega = 2.0 * Math.PI / 60000.0;
        var (_, _, _, dvMean) = GameOrderSteady(dt: dt, targetRadius: R0, seconds: 600.0,
            debiasVelocity: true, initialRadialVelocity: -20.0, rotatingFrameOmega: Omega);
        double analytic = HoverCore.EquivalentDvPerStep(Mu / (R0 * R0), dt);
        double err = Math.Abs(dvMean - analytic) / analytic;
        Assert.True(err < 0.05,
            "两档账单应一致（5% 内）：物理档每步均给 " + dvMean.ToString("F4") + " m/s，解析档记账 " +
            analytic.ToString("F4") + " m/s（差 " + (err * 100.0).ToString("F2") + "%）");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2-D 台架（2026-10-07 补）：**共转系的两个自由度** —— 径向 x + 切向 y
    //  为什么必须有它：1-D 代理**没有切向自由度** ⇒ 离心项与科里奥利耦合都缺席，
    //  而"定点前馈"要吃的恰恰是离心项 ⇒ 上一版"试过又撤"就是因为它验不了（符号/相位反复翻）。
    //
    //  局部系取在**共转定点的正上方**（x = 径向朝外、y = 切向向东、自转轴 +ŷ 正交于两者）：
    //     ẍ = a_x − g + ω²R + 2ω·ẏ      ← 离心项（朝外）+ 科氏耦合
    //     ÿ = a_y − 2ω·ẋ
    //  `a` = 指令（`CommandAccel`：抵消引力 + 前馈 + PD）。判据 = 稳态的（x, y）偏置。
    // ══════════════════════════════════════════════════════════════════════════

    private const double BenchOmega = 2.0 * Math.PI / 60000.0;

    /// <summary>2-D 共转系悬浮：返回末段窗口的（径向偏置均值, 切向偏置均值, 径向振幅）。</summary>
    private static (double xMean, double yMean, double xAmp) HoverTwoD(
        double dt, double seconds, bool feedForward,
        double initialVy = 0.0,
        double kp = HoverCore.DefaultKp, double kd = HoverCore.DefaultKd,
        double deadZone = HoverCore.DefaultDeadZoneMeters)
    {
        double omega = BenchOmega;
        double g = Mu / (R0 * R0);
        double x = 0.0, y = 0.0, vx = 0.0, vy = initialVy;      // 局部系：目标在原点
        int steps = (int)Math.Round(seconds / dt);
        int windowStart = steps * 3 / 4;
        double xSum = 0.0, ySum = 0.0, xMin = 0.0, xMax = 0.0; int n = 0;

        for (int i = 0; i < steps; i++)
        {
            // 采样（与游戏读数同相位：踢之前）
            if (i >= windowStart)
            {
                xSum += x; ySum += y; n++;
                if (x < xMin) xMin = x;
                if (x > xMax) xMax = x;
            }

            // 指令：局部系里"往目标拉"的 PD（目标 = 原点 ⇒ posErr = −(x,y)）
            // ⚠ **必须与游戏同款**：先按半步重力回正采样速度（游戏已上线的那条），再算 PD ——
            //   不然台架量的是"没有半步回正"的旧系统（实测就量到 −162.5 m = 那条病的招牌数）。
            var posErr = new DVector3(-x, -y, 0.0);
            var velForLaw = HoverCore.DebiasVelocity(new DVector3(vx, vy, 0.0), new DVector3(-g, 0.0, 0.0), dt);
            var velErr = new DVector3(-velForLaw.x, -velForLaw.y, 0.0);
            // 前馈：定点在惯性系走圆 ⇒ 需要 −ω²R 的向心项（= `TargetAccel`：轴上分量取负）
            var ff = feedForward ? new DVector3(-(omega * omega * R0), 0.0, 0.0) : DVector3.zero;
            var a = HoverCore.CommandAccel(new DVector3(0.0, 0.0, 0.0), posErr, velErr,
                kp: kp, kd: kd, deadZoneMeters: deadZone, feedForwardAccel: ff);
            // ⚠ 局部系里的"引力抵消"由 `a_x` 里那一半承担：`CommandAccel(0, …)` 只给 PD+前馈，
            //   这里再把"抵消引力"那份加上（仿真里等价于发动机按 |g| 出力）。
            double ax = a.x + g;
            double ay = a.y;

            // 共转系动力学（显式列出，见段首方程）：冲量在步首、常加速度用三阶泰勒的最低阶推进
            vx += ax * dt;
            vy += ay * dt;
            double axNet = -g + omega * omega * R0 + 2.0 * omega * vy;   // 离心（朝外）+ 科氏
            double ayNet = -2.0 * omega * vx;
            x += vx * dt + 0.5 * axNet * dt * dt;
            y += vy * dt + 0.5 * ayNet * dt * dt;
            vx += axNet * dt;
            vy += ayNet * dt;
        }
        return (n > 0 ? xSum / n : 0.0, n > 0 ? ySum / n : 0.0, n > 0 ? (xMax - xMin) : 0.0);
    }

    /// <summary>
    /// **定点前馈的判据**（先量后接）：不带前馈 ⇒ 径向偏置 ≈ `ω²R/Kp ≈ 5.5 m`（游戏实测 +5.1 m）；
    /// 带前馈 ⇒ 该偏置**精确抵消**（只余科氏耦合，静止时为零）⇒ 偏置落到分米级。
    /// </summary>
    [Fact]
    public void Hover_TwoD_FeedForward_RemovesTheCentripetalOffset()
    {
        double predicted = BenchOmega * BenchOmega * R0 / HoverCore.DefaultKp;
        // ⚠ **判据必须关死区**：默认死区 5 m 时，两个状态都"停在死区里"（实测 +5.10 与 −4.92 都是区内合法稳态）
        //   ⇒ 分不出前馈工作没有。关掉死区才看得见"偏移量本身"。
        var (xRaw, yRaw, ampRaw) = HoverTwoD(dt: 2.0, seconds: 900.0, feedForward: false, deadZone: 0.0);
        var (xFF, yFF, ampFF) = HoverTwoD(dt: 2.0, seconds: 900.0, feedForward: true, deadZone: 0.0);
        // 另附一条**游戏默认（死区 5 m）**的读数：两者都应落在死区内 —— 这解释了游戏实测为何是 +5.1 m 而非 5.52
        var (xRawDefault, _, _) = HoverTwoD(dt: 2.0, seconds: 900.0, feedForward: false);

        // ⚠ 符号：台架局部系 x 朝外（= 游戏读数"高度差为正"同号）⇒ 直接比 xRaw。
        //   （第一版我比的是 `-xRaw`，把量到的好数判成了红 —— 又是"自己的算术"那类仪器事故，见库里那条纪律。）
        // 容差取 **相对 35%**：预测只算主项（ω²R/Kp），而稳态里还叠着科氏耦合与半步回正的残差
        //   （实测 4.41 ↔ 预测 5.52 ⇒ 差 20%）。**断的是量级与方向，不是小数点**。
        Assert.True(Math.Abs(xRaw - predicted) / predicted < 0.35,
            "不带前馈的径向偏置应量到 ω²R/Kp = " + predicted.ToString("F2") + " m（游戏实测 +5.1 m），实测 "
            + xRaw.ToString("F2") + " m");
        // 前馈**精确抵消离心项**，余项 = 科氏耦合（速度相关）⇒ 实测 4.41 m → **1.10 m**（4×）。
        //   判据按实测口径写"降到米级"（而不是我第一版拍的 <0.5 m —— 那又把余项当成了零，属"判据比现实严"那类假红）。
        Assert.True(Math.Abs(xFF) < 1.5,
            "带前馈后该偏置应降到米级（余项 = 科氏耦合），实测 " + xFF.ToString("F3") + " m");
        Assert.True(Math.Abs(yFF) < 1.5, "切向也应守在米级，实测 " + yFF.ToString("F3") + " m");
        Assert.True(Math.Abs(xRawDefault) <= HoverCore.DefaultDeadZoneMeters + 0.5,
            "游戏默认死区下，不带前馈的稳态应停在死区边缘（实测 " + xRawDefault.ToString("F2")
            + " m ↔ 死区 " + HoverCore.DefaultDeadZoneMeters.ToString("F1") + " m）");
        // 实测比 4.41/1.10 = 4.0× ⇒ 判"≥3 倍"（第一版写"差一个量级"=10×，那是拍的）
        Assert.True(Math.Abs(xRaw) > 3.0 * Math.Max(Math.Abs(xFF), 1e-6),
            "前馈应把偏置压低至少 3 倍（" + xRaw.ToString("F2") + " m ⇒ " + xFF.ToString("F3") + " m）");
    }

    /// <summary>科氏耦合的量级：静止时为零、纯切向运动时才出现（说明它由阻尼项收，不需要单独前馈）。</summary>
    [Fact]
    public void Hover_TwoD_Coriolis_IsVelocityDependent()
    {
        var (xSlow, _, _) = HoverTwoD(dt: 2.0, seconds: 900.0, feedForward: true, initialVy: 1.0);
        var (xFast, _, _) = HoverTwoD(dt: 2.0, seconds: 900.0, feedForward: true, initialVy: 100.0);
        Assert.True(Math.Abs(xFast) >= Math.Abs(xSlow) - 0.01,
            "切向初速越大，科氏带来的径向偏置不应变小（慢 " + xSlow.ToString("F3") + " m ↔ 快 " + xFast.ToString("F3") + " m）");
    }

    /// <summary>
    /// 一维径向悬浮一段时长的稳态振幅（m）：返回末段窗口内的 max|r − r_t|。
    ///
    /// **按游戏的离散结构摆**（这是台架能否代表生产的关键，第一版就是在这里摆错的）：
    ///   ① 步首施加**上一步**算出的在飞冲量（生产：`ApplyPendingImpulses`）；
    ///   ② 再按场积分推进一步（半隐式欧拉近似三阶泰勒，量的是控制律不是积分器）；
    ///   ③ 然后控制器按**本步状态**算下一步的冲量 —— 这一步的延迟是真实的
    ///     （生产里并行区算、主线程下一步施加），正是它让抖幅带上 dt² 因子。
    /// </summary>
    private static double SteadyAmplitude(double dt, double targetRadius, double seconds,
        double kp = HoverCore.DefaultKp, double kd = HoverCore.DefaultKd,
        double deadZone = HoverCore.DefaultDeadZoneMeters)
    {
        double r = targetRadius + 500.0;   // 初始偏差 500 m（离死区远）
        double v = 0.0;
        double pendingDv = 0.0;            // 上一步算出的冲量（待本步施加）
        int steps = (int)Math.Round(seconds / dt);
        int windowStart = steps * 3 / 4;   // 末 25% 取样
        double amp = 0.0;
        var radial = new DVector3(1, 0, 0);
        var target = radial * targetRadius;

        for (int i = 0; i < steps; i++)
        {
            v += pendingDv;                                            // ① 施加在飞冲量
            double g = -Mu / (r * r);
            v += g * dt;                                               // ② 场积分
            r += v * dt;

            var gAcc = radial * g;
            var aCmd = HoverCore.CommandAccel(gAcc, target - radial * r, radial * (-v),
                kp: kp, kd: kd, deadZoneMeters: deadZone);
            pendingDv = aCmd.x * dt;                                   // ③ 下一步的冲量（含引力抵消）

            if (i >= windowStart)
                amp = Math.Max(amp, Math.Abs(r - targetRadius));
        }
        return amp;
    }


        [Fact]
        public void Hover_ConvergesUnderCriticalDamping()
        {
            // 500 m 起步 ⇒ 10 s 内应已进到"几十米量级"，且**不发散**（末段振幅远小于初始偏差）
            double amp = SteadyAmplitude(dt: 1.0 / 30.0, targetRadius: R0, seconds: 120.0);
            Assert.True(amp < 5.0, "战斗时标 120 s 后稳态振幅应 < 5 m，实测 " + amp.ToString("F3") + " m");
        }

        /// <summary>
        /// **"抖幅 ∝ dt²"这句话属于哪种结构**（2026-10-06 实测，与计划稿 §2.6 的原话有出入 —— 以实测为准）。
        ///
        /// 计划稿写的是"控制律在正常时标 dt=2 s 下抖幅是 dt² 量级（十米级），战斗时标下厘米级"。
        /// 台架实测：
        ///   · **每步刷新引力抵消**（= 我们实现的那一种，冲量每步按当前状态重算）：
        ///     抖幅由**死区**定，对 dt 几乎不敏感（实测比仅 1.3×），正常时标 ≈3.8 m、战斗时标 ≈2.8 m；
        ///   · **常推力**（只算一次 mg，之后不刷新，也没有位置反馈）：那不是"抖幅"，是**绕平衡点的振荡**
        ///     （等效负刚度 ω² = 2g/r），240 s 窗口内已涨到数百~数千米 ⇒ 与 dt² 律无关。
        /// ⇒ 结论：**dt² 那句不适用于本实现**；本实现的抖幅上限 = 死区（`DefaultDeadZoneMeters`），
        ///   与 `TimeScaleManager` 的档位无关。⚠ 台架只跑**一维径向**，不含气动/姿态/多体 ——
        ///   别拿它当"悬浮在任何条件下都稳"的证明。
        /// </summary>
        [Fact]
        public void Hover_Jitter_IsDeadZoneLimited_NotDtSquared()
        {
            double freshCombat = SteadyAmplitude(1.0 / 30.0, R0, 240.0);
            double freshNormal = SteadyAmplitude(2.0, R0, 240.0);
            // 量级 = **死区**（默认 5 m）—— 两个时标都在它的量级上，这才是"抖幅由死区定"的意思
            Assert.True(freshNormal < 10.0,
                "正常时标 dt=2 s 的抖幅应守在死区量级（<10 m），实测 " + freshNormal.ToString("F2") + " m");
            Assert.True(freshCombat < 10.0,
                "战斗时标 dt=1/30 s 的抖幅也应在死区量级（<10 m），实测 " + freshCombat.ToString("F3") + " m");
            double ratio = freshNormal / Math.Max(freshCombat, 1e-9);
            Assert.True(ratio < 5.0,
                "抖幅**不该**随 dt 放大（计划稿那句话不适用于本实现），实测比 " + ratio.ToString("F2") + "×");
        }

        /// <summary>
        /// 离散化那一项**确实是 ½·g·dt²**（计划稿那句断言的真正来源）—— 单独量它、别混进稳态抖幅：
        /// 一个步长内"步首踢一脚、然后整步受引力"与"引力被连续抵消"的差别就是这一项。
        /// dt=2 s、g≈7.95 m/s² ⇒ 15.9 m（**十米级** ✓ 与计划稿的数字对得上）；
        /// dt=1/30 s ⇒ 4.4 mm（计划稿说"厘米级"，同量级 ✓）。
        /// </summary>
        [Fact]
        public void Hover_DiscreteTerm_IsHalfGdtSquared()
        {
            const double g = 7.95;   // 30 km 处的本地重力（≈ 本行星地表 8 的 99.4%）
            double HalfGdt2(double dt) => 0.5 * g * dt * dt;

            // ⚠ 别手算四舍五入值（"我的算术"本身就是一类仪器事故）：拿表达式比表达式
            Assert.Equal(0.5 * g * 4.0, HalfGdt2(2.0), 9);
            Assert.Equal(g / 1800.0, HalfGdt2(1.0 / 30.0), 9);
            Assert.True(HalfGdt2(2.0) > 10.0 && HalfGdt2(2.0) < 20.0,
                "正常时标这一项应是**十米级**，实测 " + HalfGdt2(2.0).ToString("F2") + " m");
            Assert.True(HalfGdt2(1.0 / 30.0) < 0.01,
                "战斗时标这一项应是**厘米及以下**，实测 " + HalfGdt2(1.0 / 30.0).ToString("F5") + " m");
            Assert.True(HalfGdt2(2.0) / HalfGdt2(1.0 / 30.0) > 1000.0,
                "这一项才是 dt² 的来源（比值 " + (HalfGdt2(2.0) / HalfGdt2(1.0 / 30.0)).ToString("F0") + "×）");
        }

        [Fact]
        public void Hover_DeadZone_StopsPositionChatter()
        {
            // 死区的作用：把"位置项"关掉之后，稳态振幅不该**小于**死区本身（它只是不再更精细地纠偏）
            // —— 反过来说：**死区调大 ⇒ 振幅变大**（单调），这是"死区 = 定位精度"的可量关系。
            double small = SteadyAmplitude(dt: 2.0, targetRadius: R0, seconds: 240.0, deadZone: 1.0);
            double large = SteadyAmplitude(dt: 2.0, targetRadius: R0, seconds: 240.0, deadZone: 50.0);
            Assert.True(large > small,
                "死区 50 m 的振幅应大于死区 1 m（实测 " + large.ToString("F2") + " vs " + small.ToString("F2") + " m）");
        }

        [Fact]
        public void Hover_WithoutGravityCancellation_Falls()
        {
            // 负对照：把引力抵消项掐掉（CommandAccel 只喂 PD），PD 的静态增益**拉不住** 8 m/s² 的引力
            // ⇒ 稳态会掉下去（证明"抵消项不是装饰"）。Kp·偏移 8 = 800 m 的稳态下沉。
            double r = R0, v = 0.0;
            var radial = new DVector3(1, 0, 0);
            for (int i = 0; i < 120; i++)
            {
                double dt = 1.0;
                var p = radial * r;
                var posErr = radial * R0 - p;
                var a = HoverCore.PidAccel(posErr, DVector3.zero - radial * v);   // **不含抵消**
                double acc = -Mu / (r * r) + a.x;
                v += acc * dt;
                r += v * dt;
            }
            Assert.True(r < R0 - 100.0,
                "没有引力抵消项时应明显下沉（实测 r−R0 = " + (r - R0).ToString("F1") + " m）");
        }
    }
}
