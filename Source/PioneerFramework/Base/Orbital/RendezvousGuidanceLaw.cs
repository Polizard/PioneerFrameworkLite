using System;
using MechJebLib.Functions;   // Astro：要素/周期的移植权威（§14.3 V1 收敛）

namespace PioneerFramework
{
    /// <summary>
    /// **远场律的完整合成**（2026-09-17 接线批次）。
    ///
    /// 定位：**唯一入口**。此前这套合成（相位 + e 谐波 + 面外 + 限权 + 饱和，约 80 行）散在
    /// 纯测试台架的 `InPlaneRendezvousPilot` 里，游戏侧无法复用。搬到这里之后：
    ///   · 台架与游戏**共用同一个函数**（律的合成只出现一次）；
    ///   · 它保持**纯**（无 RimWorld / UnityEngine），链入纯测试 ⇒ 仍受 157 项护栏保护。
    ///
    /// 与 <see cref="RendezvousGuidanceCore.InPlaneTangentialControl"/> 的分工：
    /// 后者是**单个通道**的律（相位/半长轴）；本类是**把各通道合成为一个 Δv 向量**，
    /// 并处理限权、饱和与执行器口径。
    ///
    /// 各通道一览（设计稿 §14–§20）：
    /// | 通道 | 谐波 | 作用 | 上限 |
    /// |---|---|---|---|
    /// | 相位/半长轴 $c_0$ | 常数项 | 改 $a$ ⇒ 相位漂移率 | **自适应**（受迫偏心率判据） |
    /// | 偏心 $c_1,c_2$ | 一阶 | 把 $e$-矢量收到 0 | 慢燃烧档（**修正**通道） |
    /// | 面外 $d_1,d_2$ | 一阶 | 把倾角矢量收到 0 | 慢燃烧档 |
    /// </summary>
    public static class RendezvousGuidanceLaw
    {
        // ── 调用方政策：按段决定"跑哪个通道"与"这一段多长"（游戏与台架共用）──────────
        /// <summary>进入**形状段**的半径差门槛（折算成螺旋价 |Δv_c|，m/s）。50 ≈ LEO 上 91 km 半径差。</summary>
        public const double ShapeGateMps = 50.0;
        /// <summary>**退出形状段**的门槛（迟滞下沿，防抖）。20 ≈ LEO 上 36 km 半径差。</summary>
        public const double ShapeExitMps = 20.0;
        /// <summary>形状段腿长 = 螺旋时间估计 × 此系数（偏保守：估计用**目标半径**处最小的限权）。</summary>
        public const double ShapeLegFactor = 1.3;
        /// <summary>相位段腿长 = MinLegSeconds × 此安全系数（单腿能闭合的上界是 U·T²/4）。</summary>
        public const double LegSafety = 1.5;
        /// <summary>
        /// 变轨账单的**实测倍率**：真实 Δv ≈ 螺旋价 × 此值。
        /// 依据 = 纯台架 T11（LEO→GEO 全链实测 1.5055），构成 = 螺旋价 + e 通道清理残余偏心率
        /// （T2 口径：含 e 清理后 ≤1.20）+ 相位租金 + 终端消速。
        /// ⚠ 用途只是**给玩家/场景的账单估计**，不参与任何控制。
        /// </summary>
        public const double ShapeCostFactor = 1.5;

        /// <summary>
        /// **调用方政策：按段决定通道与腿长**（2026-09-17；游戏侧 `RunNewRendezvousLaw` 与纯台架共用）。
        ///
        /// 交会的自然结构是**先变轨、再收相位**：
        ///   · 半径差得需要一次变轨（螺旋价 &gt; <see cref="ShapeGateMps"/>）⇒ 本段走**形状通道**
        ///     （`Compose(closePhase:false)`：把 δn 直接驱到 0，即把 a 开到目标）；纯台架 T2 验过
        ///     LEO→GEO 的成本 ≈ 螺旋价 3274.8 m/s、≤1.15×霍曼、不撞行星。
        ///   · a 已对上（&lt; <see cref="ShapeExitMps"/>，迟滞下沿）⇒ 本段走**相位通道**（S 形式收相位）。
        ///
        /// ⚠ **模式必须按段决定、一段内固定**：相位通道本来就靠"改 a"来收相位（δn≠0 正是它的
        ///   工作手段），若按瞬时 δn 连续判断模式，相位段一建起 δn 就会被形状通道当成"轨道不匹配"
        ///   给抵消掉。
        ///
        /// ⚠ **腿长必须盖住本段的机动**——这是 2026-09-17 游戏内实测的最要害一条：形状段是低推力
        ///   螺旋，限权 `a = 0.05·g(r)` 随半径**下降 23 倍**（LEO→GEO），单是推力时间就要
        ///   $1\sim3\times10^5$ s。给 60000s 是**预算不可行**：律会永久饱和——实测烧掉 1549 m/s
        ///   而距离从 21,581 km 涨到 30,077 km，永不收敛。纯台架 T3 的注释早已写明
        ///   "旧值 60000 s 是预算不可行，不是律的问题"。
        ///
        /// ⚠ **形状段做完要提前收段**（同轮游戏内实测）：形状通道把 δn 驱到零后自己会停止点火，
        ///   模式若只在腿末重判就要白等一整个腿长才轮到相位段（实测 294,875s ≈ 43 游戏小时）
        ///   —— 从玩家看就是"飞了一半不干了"。故除"到点"外再加"形状收敛即收段"；相位段不适用
        ///   （同样的理由：相位段本就靠 δn≠0 工作）。
        /// </summary>
        /// <param name="now">当前时刻（s，与 <paramref name="legEndTick"/> 同一时间轴）。</param>
        /// <param name="legEndTick">本段的结束时刻（跨调用保持；重排时被本函数改写）。</param>
        /// <param name="shapeMode">上一段的模式（跨调用保持；本函数按迟滞更新它）。</param>
        /// <param name="legSeconds">本次排出的段长（s；仅重排时有意义）。</param>
        /// <param name="dvShape">半径差折算的螺旋价（m/s；诊断、菜单估计与面板读数用）。</param>
        /// <returns>true = 重排了一个新段（调用方按需记日志）。</returns>
        public static bool PlanLeg(DVector3 rSelf, DVector3 vSelf, DVector3 rTgt, DVector3 vTgt,
            double mu, double aMax, double now, double legFloor, double lastPhaseErr,
            ref bool shapeMode, ref double legEndTick,
            out double legSeconds, out double dvShape)
        {
            legSeconds = legFloor;
            dvShape = 0.0;
            double rMag = rSelf.magnitude;
            if (!(rMag > 1.0) || !(mu > 0.0)) { shapeMode = false; return false; }

            // V1 收敛（§14.3）：本文件是**与参考系无关**的纯文件 ⇒ 走移植的 `Astro`（`OrbitView.period`
            // 本身就是它的转发，实现只有一份）。**不要**在这里构造 `OrbitView` —— 那会隐含一个参考系，
            // 而 `a`/`period` 对旋转不变，纯文件不该知道参考系。
            double aTgt = Astro.SmaFromStateVectors(mu, OrbitView.ToV3(rTgt), OrbitView.ToV3(vTgt));
            double vcHere = Math.Sqrt(mu / rMag);
            dvShape = aTgt > 1.0 ? Math.Abs(vcHere - Math.Sqrt(mu / aTgt)) : 0.0;

            // 收段：**到点** 或（**形状段 且 形状已收敛**）。
            // ⚠ 第二条是 2026-09-17 游戏内实测补的：形状通道把 δn 驱到零后**自己会停止点火**
            //   （δn→0 ⇒ 指令→0 ⇒ 落进点火死区），而模式若只在腿末重判，就要白等一整个腿长
            //   （实测 294,875s ≈ 43 游戏小时）才轮到相位段 —— 从玩家看是"飞了一半不干了"。
            //   相位段**不适用**该条：它本来就靠 δn≠0 工作（同"模式一段内固定"的理由）。
            bool due = !(now < legEndTick);
            bool shapeSettled = shapeMode && dvShape <= ShapeExitMps;
            if (!due && !shapeSettled) return false;

            // 迟滞：进形状段用高阈值，留在形状段用低阈值（防在两模式间抖）
            shapeMode = shapeMode ? dvShape > ShapeExitMps : dvShape > ShapeGateMps;

            // ⚠ **腿长的硬下限 = 目标轨道的一个周期**（$P_t=2\pi\sqrt{a_t^3/\mu}$）。
            //   这不是性能调参，是**量纲**：台架的 LegSeconds=60000 之所以合适，是因为它的主用例是
            //   GEO（P=60000s 恰好一圈）；原样搬到 LEO 就是 10.5 圈 —— 交会被人为拖慢一个数量级。
            //   S 形式的目标是"在段末闭合"，段多长就等多久 ⇒ **要"一圈交会"就必须把段设成一圈**。
            //   物理上 LEO 一圈能收的相位上限 ≈20°（缩轨缩到大气层边缘给出 δn≈6.3e-5 rad/s），
            //   所以 ≤20° 的相位差一圈内可闭合（Δv≈(2/3)rΔφ/P ≈220 m/s）；更大的相位差会自动
            //   受撞行星保护钳制、按物理速率走并重排段（代价仍是廉价路径）。
            // 周期收敛到移植权威（§14.3『周期』行）。⚠ `Astro.PeriodFromStateVectors` 对双曲**抛异常**
            // ⇒ 用同一个 `aTgt > 1.0` 守卫短路（与原先的语义逐位一致）。
            double periodTgt = aTgt > 1.0
                ? Astro.PeriodFromStateVectors(mu, OrbitView.ToV3(rTgt), OrbitView.ToV3(vTgt)) : 0.0;
            double floorAll = Math.Max(legFloor, periodTgt);
            if (shapeMode)
            {
                // 螺旋时间估计：限权取**目标半径**处的值（全程最小 ⇒ 估计保守）。
                // ⚠ 形状通道是**限权的连续推力螺旋**，不是脉冲转移（见 Compose 里那段实测证伪）。
                //   想要脉冲那种快必须显式构造两冲量（未实现）。
                double aLimTgt = aTgt > 1.0
                    ? Math.Min(aMax, SlowBurnEtaDefault * mu / (aTgt * aTgt))
                    : Math.Min(aMax, SlowBurnEtaDefault * mu / (rMag * rMag));
                double shapeSeconds = dvShape / Math.Max(aLimTgt, 1e-9);
                legSeconds = Math.Max(floorAll, ShapeLegFactor * shapeSeconds);
            }
            else
            {
                double uNow = 3.0 * aMax / rMag;
                double need = RendezvousGuidanceCore.MinLegSeconds(lastPhaseErr, uNow) * LegSafety;
                legSeconds = Math.Max(floorAll, need);
            }
            legEndTick = now + legSeconds;
            return true;
        }

        /// <summary>慢燃烧档的 η（= eMax/4；Compose 的默认 slowBurnEta 同值，这里用于腿长估计）。</summary>
        private const double SlowBurnEtaDefault = 0.05;

        /// <summary>一次合成的结果。</summary>
        public struct Result
        {
            public bool Valid;
            /// <summary>本周期要施加的 Δv（**中心天体参考系**，m/s）。</summary>
            public DVector3 Dv;
            /// <summary>相位差 $\delta\varphi$（rad，平黄经口径，>0 = 本船超前）。</summary>
            public double PhaseErr;
            /// <summary>相位差速率 $\delta n$（rad/s）。</summary>
            public double PhaseRate;
            /// <summary>本次用的加速度上限（诊断；+∞ = 短燃烧档放行）。</summary>
            public double ARef;
            /// <summary>e 通道本次是否投入（继电器状态，诊断）。</summary>
            public bool EccActive;
            /// <summary>归一化后的 $c_0\in[-1,1]$（诊断：分账与翻转计数用）。</summary>
            public double C0Norm;
            /// <summary>合成后的切向加速度（m/s²，饱和前，诊断）。</summary>
            public double AT;
            /// <summary>合成后的面外加速度（m/s²，饱和前，诊断）。</summary>
            public double AN;
            /// <summary>面外通道本周期贡献的 Δv 增量（m/s，诊断：分账用）。</summary>
            public double CrossTrackDvStep;
        }

        /// <summary>由轨道法向构造平面内的惯性基（$\hat x$ 在平面内，$\hat y=\hat n\times\hat x$）。</summary>
        public static void PlaneBasis(DVector3 normal, out DVector3 px, out DVector3 py)
        {
            DVector3 n = normal.normalized;
            px = Math.Abs(n.y) < 0.9
                ? DVector3.Cross(DVector3.up, n).normalized
                : DVector3.Cross(DVector3.forward, n).normalized;
            py = DVector3.Cross(n, px).normalized;
        }

        /// <summary>
        /// **平黄经之差** $\delta\varphi=\lambda_c-\lambda_t$（rad，**&gt;0 = 追方超前**）。
        ///
        /// 单独提出来是为了**口径只有一份**：律内部用它，玩家 UI 的"相位差"读数也用它
        /// （UI 另算一份就会出现"界面说 120°、律按别的数在烧"这类静默错配）。
        ///
        /// ⚠ 不能改用极角：切向推力的受迫偏心率让极角速率摆动 ±2e·n，e~0.1 时与 δn* 同量级
        /// ⇒ 律会去追噪声（设计稿 §13.2 N3）；UI 读数同理必须用平黄经。
        /// </summary>
        public static double PhaseErrorRadians(DVector3 rSelf, DVector3 vSelf,
            DVector3 rTgt, DVector3 vTgt, DVector3 targetPlaneNormal, double mu)
        {
            PlaneBasis(targetPlaneNormal, out DVector3 px, out DVector3 py);
            double thC = Math.Atan2(DVector3.Dot(rSelf, py), DVector3.Dot(rSelf, px));
            OrbitMath.GetEccentricity(rSelf, vSelf, out DVector3 eVec, out double eccNow, mu);
            double lamC = thC;
            if (eccNow > 1e-6)
            {
                double peri = Math.Atan2(DVector3.Dot(eVec, py), DVector3.Dot(eVec, px));
                lamC = thC - 2.0 * eccNow * Math.Sin(thC - peri);
            }
            // 目标的平黄经：同样口径（其偏心率也去掉一阶摆动）
            OrbitMath.GetEccentricity(rTgt, vTgt, out DVector3 eVecT, out double eccT, mu);
            double thT = Math.Atan2(DVector3.Dot(rTgt, py), DVector3.Dot(rTgt, px));
            double lamT = thT;
            if (eccT > 1e-6)
            {
                double periT = Math.Atan2(DVector3.Dot(eVecT, py), DVector3.Dot(eVecT, px));
                lamT = thT - 2.0 * eccT * Math.Sin(thT - periT);
            }
            return WrapPi(lamC - lamT);
        }

        /// <summary>
        /// **远场律：一次控制的完整合成。**
        ///
        /// 输入两船状态（**同系**：均为中心天体参考系）与本船能力，输出本周期要施加的 Δv。
        /// 调用方负责：① 剩余时间预算 $T$（见 <see cref="RendezvousGuidanceCore.MinLegSeconds"/>）；
        /// ② 起停条件与分腿（见 `MinLegSeconds` / `MaxClosablePhase`）；
        /// ③ 把 Δv 交给执行器（`StartBurn` / 节流）。
        /// </summary>
        /// <param name="eccRelay">e 通道的**迟滞继电器**状态（跨调用保持，由调用方持有）。</param>
        /// <param name="targetPlaneNormal">目标轨道法向（决定"平黄经"与各谐波的相位基准）。</param>
        /// <param name="timeRemaining">**剩余**时间预算（s）。相位自由模式传 +∞。</param>
        /// <param name="dt">本周期时长（s）——Δv = 加速度 × dt。</param>
        /// <param name="aMaxSelf">本船可用加速度上限（m/s²，取主网/RCS 的较大者）。</param>
        public static Result Compose(
            DVector3 rSelf, DVector3 vSelf,
            DVector3 rTgt, DVector3 vTgt, DVector3 targetPlaneNormal,
            double mu, double aMaxSelf, double timeRemaining, double dt,
            ref bool eccRelay,
            bool closePhase = true,
            bool adaptiveCap = true,
            double trackingFraction = 0.05,
            double eccCap = 0.2,
            double slowBurnEta = 0.05,
            double eccTimeConstant = 900.0,
            double eccEngageAt = 0.02,
            double eccReleaseAt = 0.005)
        {
            var res = default(Result);
            double rMag = rSelf.magnitude;
            if (!(rMag > 1.0) || !(mu > 0.0) || !(dt > 0.0) || !(aMaxSelf > 0.0)) return res;

            DVector3 rHat = rSelf / rMag;
            DVector3 nrm = DVector3.Cross(rSelf, vSelf).normalized;      // 本船轨道法向
            DVector3 tHat = DVector3.Cross(nrm, rHat).normalized;        // 顺行切向
            double vc = Math.Sqrt(mu / rMag);
            double n = Math.Sqrt(mu / (rMag * rMag * rMag));

            // ── 相位差 δφ = λ_c − λ_t（**平黄经**之差，>0 = 本船超前）──
            res.PhaseErr = PhaseErrorRadians(rSelf, vSelf, rTgt, vTgt, targetPlaneNormal, mu);

            // e 通道与谐波基准要用本船的平面基 / e-矢量（相位差口径已由 PhaseErrorRadians 统一）
            PlaneBasis(targetPlaneNormal, out DVector3 px, out DVector3 py);
            double thC = Math.Atan2(DVector3.Dot(rSelf, py), DVector3.Dot(rSelf, px));
            OrbitMath.GetEccentricity(rSelf, vSelf, out DVector3 eVec, out double eccNow, mu);

            // ── δn = 相位差速率 = n(a_c) − n(a_t)。**必须用要素口径**：
            //    按"相对局地圆轨速度"算会在"a 相同但目标在别处"时恒为 0 ⇒ 律静默不动（§13.4-I4）──
            double aNow = Astro.SmaFromStateVectors(mu, OrbitView.ToV3(rSelf), OrbitView.ToV3(vSelf));
            double aTgt = Astro.SmaFromStateVectors(mu, OrbitView.ToV3(rTgt), OrbitView.ToV3(vTgt));
            res.PhaseRate = RendezvousGuidanceCore.PhaseRateFromElements(aNow, aTgt, mu);

            // ── c₀：限权（**自适应**，见 AdaptiveThrustCap 的推导）──
            // ⚠ **形状通道同样要限权**——我曾以为"变轨本来就要偏心、限权没意义"而把它拆掉，
            //   **实测证伪**（2026-09-17，纯台架 T13 高推船 LEO→GEO）：
            //     不限权 ⇒ 用时 2.19e5s（也没在一个目标周期内）、**Δv 7769.9 = 2.37× 螺旋价**、
            //     **末态没收敛**（dist=21,245km / rel=9,023m/s）、**最低点 5087km 掉出大气层安全线**
            //     （限权版：1.4e5s / 1.51× 螺旋价 / **收敛到 0.00km,0.0m/s** / 从未侵地）。
            //   机理：形状指令是 a_t = δn·r/(3τ)（与推力无关）⇒ 高推船**不饱和**、按 τ 猛推
            //   ⇒ 受迫偏心暴涨 ⇒ 脱离"准圆 + 线性化有效"的域 ⇒ 律的模型失效 ⇒ 失控。
            //   **限权不是"限制变轨"，而是把整个机动关在模型有效的域里。**
            //   ⇒ 想要"脉冲转移"那种快（霍曼 ≈1.4e4s / 2862 m/s）必须**显式构造两冲量**
            //     （在近/远地点各烧一次 + 相位窗口）——那是另一套机制，不在本律的连续推力提法里。
            double gLocal = mu / (rMag * rMag);
            double Tprobe = closePhase ? Math.Max(timeRemaining, 0.0) : 1e6;
            double phiProbe = Math.Abs(res.PhaseErr + res.PhaseRate * Tprobe);
            double dvBudgetProbe = (2.0 / 3.0) * rMag * phiProbe / Math.Max(Tprobe, 1.0);
            double aCap = adaptiveCap
                ? RendezvousGuidanceCore.AdaptiveThrustCap(vc, gLocal, n, dvBudgetProbe, eccCap)
                : slowBurnEta * gLocal;
            double aRef = Math.Min(aMaxSelf, aCap);
            res.ARef = aRef;
            // **谐波通道的权按"慢燃烧档"给**，不跟随 aRef：aRef 自适应放宽是为了让 c₀（改 a）
            // 做准脉冲；e / 面外是**修正**通道、受迫响应属"长燃烧"型，权跟着放宽会把两个谐波
            // 同时放大 ⇒ 整批低推力区回归（设计稿 §18.1）。
            double aRefHarm = Math.Min(aMaxSelf, slowBurnEta * gLocal);

            double uMax = 3.0 * aRef / rMag;
            double phiForLaw = closePhase ? res.PhaseErr : 0.0;
            double budgetForLaw = closePhase ? Math.Max(timeRemaining, 0.0) : double.PositiveInfinity;
            double c0 = RendezvousGuidanceCore.InPlaneTangentialControl(
                phiForLaw, res.PhaseRate, uMax, budgetForLaw, trackingFraction);

            // ── (c₁,c₂)：偏心通道（ê 以 τ_e 衰减）＋ **迟滞继电器** ──
            //   ⚠ 不能用"连续比例"常开：那个 e 振荡是切向推力的**受迫响应**、每圈自行归零、
            //   本身免费；常开等于按时间烧 Δv（实测占 T1 总 Δv 的 70%）。继电器 ⇒ 代价 ∝ 需清理的 e。
            //   ⚠ 也不能去打 a 通道的受迫偏心率（a 通道工作时不对 e 动手，见 §13.2 N2）。
            // ⚠ **这不是轨道周期，不要收敛到 `OrbitView.period`**（《大修计划》§14.3 原先把"6 处"说成同一条算式，**那是错的**）：
            //   这里是 `2π√(r³/μ)` = **当前半径处的局部圆轨周期**（即 `2π/n`），是局部角速率的派生量，
            //   取的是**瞬时半径**而不是半长轴。本轮 V1 已逐处分类核实：6 处里 3 处是轨道周期、3 处是这个。
            double tOrb = 2.0 * Math.PI * Math.Sqrt(rMag * rMag * rMag / mu);
            double tauE = Math.Max(eccTimeConstant, 2.0 * tOrb);
            double k = vc / tauE;
            if (eccNow > eccEngageAt && Math.Abs(c0) < 0.05) eccRelay = true;
            else if (eccNow < eccReleaseAt || Math.Abs(c0) >= 0.05) eccRelay = false;
            res.EccActive = eccRelay;
            double eCmd = eccRelay ? k * eccNow : 0.0;
            double c1 = eCmd > 1e-12 ? -k * DVector3.Dot(eVec, px) : 0.0;
            double c2 = eCmd > 1e-12 ? -k * DVector3.Dot(eVec, py) : 0.0;

            // ── 合成 a_t(ν) = c₀·a_ref + c₁cosν + c₂sinν ──
            //   ⚠ **不要整体归一化**：归一化会把 e 通道也顶到满推力 ⇒"零误差也满推力常烧"。
            double nu = thC;                    // 极角：r̂=(cosν,sinν) 恒成立，与轨道形状无关
            double cosN = Math.Cos(nu), sinN = Math.Sin(nu);
            double aT = c0 * aRef + c1 * cosN + c2 * sinN;

            // ── 面外（倾角矢量）通道：纯一阶谐波（面外是纯谐振子、无长期项）──
            DVector3 relPosW = rSelf - rTgt;
            DVector3 relVelW = vSelf - vTgt;
            double zRel = DVector3.Dot(relPosW, nrm);
            double zDotRel = DVector3.Dot(relVelW, nrm);
            double tauI = Math.Max(RendezvousGuidanceCore.CrossTrackTimeConstantFloor, 3.0 * tOrb);
            RendezvousGuidanceCore.CrossTrackControl(zRel, zDotRel, n, nu, tauI, 0.30 * aRefHarm,
                out double d1, out double d2);
            double aN = d1 * cosN + d2 * sinN;
            res.C0Norm = c0; res.AT = aT; res.AN = aN; res.CrossTrackDvStep = Math.Abs(aN) * dt;

            // ── 合成三维指令并饱和 ──
            DVector3 aCmd = tHat * aT + nrm * aN;
            if (aCmd.magnitude > aRef) aCmd = aCmd.normalized * aRef;

            res.Dv = aCmd * dt;
            res.Valid = true;
            return res;
        }

        private static double WrapPi(double a)
        {
            while (a > Math.PI) a -= 2.0 * Math.PI;
            while (a < -Math.PI) a += 2.0 * Math.PI;
            return a;
        }
    }
}
