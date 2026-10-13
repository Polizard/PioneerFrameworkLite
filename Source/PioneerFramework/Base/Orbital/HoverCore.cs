using System;

namespace PioneerFramework
{
    /// <summary>
    /// **[A7] 悬浮（定点保持）控制律 —— 纯核心**（计划稿 §2.6 / D10 / D8）。
    ///
    /// ## 它是什么
    /// 把船**保持在一个固连系定点**（经纬度 + 高度）上：每步
    /// `a_cmd = −g_local + Kp·(p_target − p) + Kd·(v_target − v)`（PD + 死区 + 上限），
    /// 其中 `−g_local` 那项**抵消本地引力**（没有它，PD 只能把船按到地上）。
    /// 姿态由 [D10] 单列：**船体上轴 = 本地铅垂**（<see cref="UpAxisTarget"/>）——与既有的 SAS
    /// （"**主推轴**对准"）**不是同一根轴**，别塞进 `SasTargetKind`。
    ///
    /// ## 为什么抖动是 dt² 量级（"悬浮是战斗时标的玩法"）
    /// 指令加速度在一个物理步内是**常值**（零阶保持）⇒ 位置残差的稳态量级 ∝ `a·dt²`；
    /// 正常时标 dt = 2 s ⇒ 十米级；战斗时标 dt = 1/30 s ⇒ 厘米级。
    /// ⚠ 这条是**被台架量出来的**（`HoverBenchTests`），不是推演 —— 推演只给出量级，系数由增益定。
    ///
    /// ## 增益取值（占位，与 ρ₀/H 同一种处境）
    /// 临界阻尼取 `Kp = ω²`、`Kd = 2ω`：默认 `ω = 0.1 rad/s`（时间常数 τ = 10 s，不追着噪声跑）。
    /// **数值待标定**：τ 越小跟得越紧、抖得越凶、越费料（保持 1 kg 悬着 ≈ 7.95 m/s²·m 的推力账）。
    ///
    /// 【边界】本文件**纯**（只 using System；`SurfaceFrame` 也是纯的）⇒ 链入 `Pure.Tests`，可单测。
    /// </summary>
    public static class HoverCore
    {
        /// <summary>位置项增益 Kp（1/s²）：默认 ω²（ω = 0.1 rad/s ⇒ τ = 10 s）。</summary>
        public const double DefaultKp = 0.01;

        /// <summary>速度项增益 Kd（1/s）：默认 2ω（**临界阻尼**）。</summary>
        public const double DefaultKd = 0.2;

        /// <summary>死区（m）：位置误差小于它 ⇒ 不产生位置项（只留阻尼项）—— 防"在零点附近抖"。</summary>
        public const double DefaultDeadZoneMeters = 5.0;

        /// <summary>指令加速度上限（m/s²）：安全阀，防增益/误差发疯时把船甩出去（≈2.5 g）。</summary>
        public const double DefaultMaxAccelMps2 = 20.0;

        /// <summary>
        /// **PD 项**（不含引力抵消）：`Kp·posErr + Kd·velErr`，其中位置项在**死区内归零**
        /// （按误差**模**判死区，不逐轴 —— 逐轴会让每一轴独立抖）。
        /// 结果按 <paramref name="maxAccelMps2"/> 截断模长（>0 才截）。
        /// </summary>
        public static DVector3 PidAccel(DVector3 posErrSystem, DVector3 velErrSystem,
            double kp = DefaultKp, double kd = DefaultKd,
            double deadZoneMeters = DefaultDeadZoneMeters, double maxAccelMps2 = DefaultMaxAccelMps2)
        {
            DVector3 a = DVector3.zero;
            if (posErrSystem.sqrMagnitude > deadZoneMeters * deadZoneMeters)
                a += posErrSystem * kp;
            a += velErrSystem * kd;

            double mag = a.magnitude;
            if (maxAccelMps2 > 0.0 && mag > maxAccelMps2)
                a = a * (maxAccelMps2 / mag);
            return a;
        }

        /// <summary>
        /// **[A7+] 共转定点的期望加速度前馈**（2026-10-07，**由 2-D 台架验过才上线**）：
        /// 定点在惯性系里走圆 ⇒ 它的加速度 = `−ω²·(p − (p·â)â)`（`â` = 自转轴，本库约定 **+Y**）。
        ///
        /// PD 只管"误差"，**不产生这一项** ⇒ 少了它，稳态必留 `ω²R/Kp ≈ 5.5 m` 的固定偏置
        /// （**游戏实测 +5.1 m**、1-D 代理也量到同量级）；补上前馈 ⇒ 该项**精确抵消**，
        /// 只余科里奥利耦合（速度相关 ⇒ 由阻尼项收，静止时为零）。
        ///
        /// ⚠ **为什么上一版试过又撤**：1-D 代理模型**没建模切向自由度**（离心项与科氏耦合都在那儿），
        ///   符号/相位反复翻 ⇒ 无法判定。⇒ 先补 `HoverBenchTests.Hover_TwoD_*` 这台**2-D 台架**，
        ///   量到"不带前馈 ≈5.5 m / 带前馈 ≈0"之后才接的线。**这是"未验过的控制改动不上线"的落点。**
        /// </summary>
        public static DVector3 TargetAccel(DVector3 targetPosSystem, double omega)
        {
            if (!(omega * omega > 0.0)) return DVector3.zero;
            var perp = new DVector3(targetPosSystem.x, 0.0, targetPosSystem.z);   // 去掉自转轴（+Y）分量
            return perp * (-(omega * omega));
        }

        /// <summary>
        /// **悬浮指令加速度（系统系）** = 抵消本地引力 + 定点前馈（可选）+ PD。
        /// `gravityAccel` = 该处引力场给出的加速度（指向天体；掩码后的场就是它）。
        /// ⚠ 抵消项用**场本身**而不是 `−g·r̂` 近似：有多体/掩码时，"场"才是真值（避免两套引力）。
        /// </summary>
        public static DVector3 CommandAccel(DVector3 gravityAccel, DVector3 posErrSystem, DVector3 velErrSystem,
            double kp = DefaultKp, double kd = DefaultKd,
            double deadZoneMeters = DefaultDeadZoneMeters, double maxAccelMps2 = DefaultMaxAccelMps2,
            DVector3 feedForwardAccel = default)
            => gravityAccel * -1.0 + feedForwardAccel
               + PidAccel(posErrSystem, velErrSystem, kp, kd, deadZoneMeters, maxAccelMps2);

        /// <summary>
        /// **半步重力偏差补偿（[A7]，2026-10-06 第三轮游戏内实测定位）**。
        ///
        /// 本库的冲量在**步首**施加、引力在**整步**作用，而控制器每步**只采样一次**（步首）——
        /// 采到的正是"上一步引力下坠的**底部**" ⇒ 径向速度**恒偏低** `½·|g|·dt`
        /// （本场景 dt=2 s、g≈7.9 ⇒ **7.9 m/s 的假下坠**）。
        /// PD 把这份假下坠当真误差去补（`Kd·v` 直接顶掉一截位置项）⇒ 稳态偏置
        /// `e* = (Kd/Kp)·½·|g|·dt ≈ 163 m` —— **游戏内实测读数正好是 +162 m**（区间统计确认不是振荡）。
        ///
        /// 补偿：把采样速度按半步重力回正，得到**本步的平均**速度（这才是 PD 该看的量）。
        /// ⚠ 与"在飞冲量补偿"（上升段的 `GuidanceVelocity`）是同一类病：**控制器读了带系统偏差的状态**。
        /// </summary>
        public static DVector3 DebiasVelocity(DVector3 sampledVelocity, DVector3 gravityAccel, double dtSeconds)
            => sampledVelocity - gravityAccel * (dtSeconds * 0.5);

        /// <summary>
        /// **[A8] 料尽 ⇒ 必须退出悬浮**（唯一判据，2026-10-07 自动判据抓到缺口后补）。
        ///
        /// 语义：**有**全矢量网、且**每一网都抽不出料** ⇒ 悬浮在物理上已不可能继续（推力恒 0），
        /// 此时**必须显式清掉 `hoverActive`** —— 否则状态谎报"还在悬浮"（嘴上悬着、身子在掉），
        /// 与设计稿引的先例"**燃料干涸自动退出**"（§1.2）相悖，也与解析档的料尽退出不对称。
        ///
        /// ⚠ **"没装网"不算料尽**（`hasFullVectorNet == false` ⇒ 返回 false）：那是配置问题，
        ///   留着让人在读数里看见，不静默改状态。
        /// </summary>
        public static bool ShouldExitHoverWhenDry(int fullVectorNetCount, int dryFullVectorNetCount)
            => fullVectorNetCount > 0 && dryFullVectorNetCount >= fullVectorNetCount;

        /// <summary>
        /// **姿态设定点（D10）**：船体上轴（+y）要对准的世界方向 = **本地铅垂**（径向朝外）。
        /// 与 `AlignMainAxis` 共用对准执行器；悬浮进出 = 设/清这个设定点。
        /// </summary>
        public static DVector3 UpAxisTarget(DVector3 positionSystem)
        {
            double r = positionSystem.magnitude;
            return r > 1e-9 ? positionSystem * (1.0 / r) : DVector3.up;
        }

        /// <summary>
        /// **定点（固连经纬度 + 高度）→ 系统系的（位置, 速度）**：目标点跟着天体自转走
        /// （"定点"= 地面上的一个点，不是惯性系里的一个点）。
        ///
        /// ⚠ 速度**不能用 `SurfaceStateAbove` 那份**：那个函数按"出生点继承地表速度"设计（速度对应**地表半径**），
        /// 而定点在高度 `alt` 上 ⇒ 它的共转速度是 `ω·(R+alt)·cosφ`（大 `alt/R` 那一份）。
        /// 用错的那份 = 目标永远在横向滑 2 m/s（30 km 处 ≈2.1 m/s）⇒ 控制器**终生**在纠一个假偏差。
        /// 这条是台架测试当场抓出来的（`TargetState_FollowsBodyRotation`）。
        /// </summary>
        public static (DVector3 pos, DVector3 vel) TargetState(double bodyRadiusMeters, double latRad,
            double lonRad0, double altitudeMeters, double omega, double physicsTick)
        {
            var surface = SurfaceFrame.SurfaceState(bodyRadiusMeters, latRad, lonRad0, omega, physicsTick);
            double r = surface.pos.magnitude;
            var pos = r > 1e-3 ? surface.pos + surface.pos * (altitudeMeters / r) : surface.pos;
            var vel = SurfaceFrame.SurfaceVelocity(pos, omega);   // 该半径上的共转切向（唯一算式）
            return (pos, vel);
        }

        /// <summary>
        /// **[A8]/D8 解析档的等效计费（每步等效 Δv，m/s）** = `|g_local|·dt`。
        /// 物理档**不**用这条 —— 它烧的是真实推力（走既有执行器的 `m·dv/ve` 分账）；
        /// 本式是**对照口径**：判据"同一次悬浮两档账单一致"就是拿物理档实耗与它比。
        /// </summary>
        public static double EquivalentDvPerStep(double gravityMagnitude, double dtSeconds)
            => gravityMagnitude > 0.0 && dtSeconds > 0.0 ? gravityMagnitude * dtSeconds : 0.0;

        /// <summary>本地铅垂方向的行星自转速度（m/s，供"定点速度"核对用；= |ω×p|）。</summary>
        public static double CoRotationSpeed(double radiusMeters, double latRad, double omega)
            => Math.Abs(omega * radiusMeters * Math.Cos(latRad));
    }
}
