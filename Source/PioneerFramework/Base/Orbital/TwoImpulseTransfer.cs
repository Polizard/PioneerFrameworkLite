using System;
using MechJebLib.Functions;
using MechJebLib.Lambert;
using MechJebLib.Maths;
using MechJebLib.Primitives;
using MechJebLib.TwoBody;
using MechJebLib.Utils;
using PioneerFramework;   // 批7（2026-09-30）：统一调度原语 `ParallelOps`（多起点并行 + 唯一归约器）
using static System.Math;
using static MechJebLib.Utils.Statics;

namespace MechJebLib.Maneuvers
{
    /// <summary>
    /// 两冲量转移（"第一次烧到转移轨道、到达时再烧一次"）——MJ 的
    /// `MechJebLib/Maneuvers/TwoImpulseTransfer.cs` 的**重写**（**不在** `ThirdParty/` 里：
    /// 要偏离上游就破坏"与上游 diff 为空"这条唯一不变式）。类名与方法签名**保持上游原样**，
    /// 调用点（`DeltaVAndTimeForHohmannTransfer`）看上去与上游逐行对应。
    ///
    /// ## ⚠ 唯一的偏离：alglib 的 `minbleic` → 手写有界模式搜索（2026-09-19）
    /// 上游用 `alglib.minbleicoptimize` 做**三变量有界、无导数**极小化
    /// （变量 = `dt` 出发延迟、`tt` 转移时长、`offset` 到达相位；目标 = `|Δv1|`（`capture` 时再加 `|Δv2|`）；
    /// 梯度由 alglib 自己差分）。我们**全库零 alglib**（P1 决定），所以这里换成
    /// **模式搜索（compass search）+ 多起点**：沿着每个坐标轴试探正负步长，失败就整体减半。
    ///
    /// 为什么敢这么换（而不是"手搓一个更好的优化器"）：
    /// · 目标函数只通过 `Izzo` 求值 ⇒ 是**黑箱**，本来也只能做无导数搜索；
    /// · 维数只有 3、且**物理量的量级由 `Scale` 归一化**（上游同款），可行域可写成有限盒
    ///   （上游把它交给 alglib 的 `bc` + `stpmax` 也是在描述同一个盒子）；
    /// · 模式搜索**只要目标函数可求值就收敛到局部极小**，不依赖曲率/梯度 ⇒ 比任何"近似梯度"的实现都稳。
    /// ⚠ **代价**：它**不保证全局最优**（上游的 CG 同样不保证）。所以本类的判据不是"与上游同值"，
    /// 而是**物理判据**：算出来的两冲量施加后必须**真的交会**（位置与速度都对上）——
    /// 见 `TwoImpulseTransferTests`。多起点只是把"局部"的风险压低，不掩盖它。
    ///
    /// ## ⚠ 第二处偏离：**把 `offset` 钉死，让返回的计划一定自洽**（2026-09-19）
    /// 上游的第三个变量 `offset` 同时影响**两处**：到达点取"目标在 `dt+tt+offset` 的位置"，
    /// 而返回的第二次点火时刻却是 `dt+tt`（**不含 offset**）⇒ 只要 `offset ≠ 0`，
    /// 返回的 `(dv2, dt2)` 就**不是一次真实交会**，而是**错开 `offset` 秒**的假解（我实测过：
    /// 位置差 2.4e6 m）。
    /// 上游默认路径恰好把它锁在 0（`lagTime` 为 NaN 或 0 时 `offsetMin = offsetMax = 0`）所以没暴露；
    /// 但 `rendezvous: false` 那条路**放开**了它（±∞），"另外试目标轨道另一侧"那个分支也硬编码 `T2/2`。
    /// ⇒ 我们的做法：**offset 恒为固定值**（= `−lagTime`，缺省 0），即"到达点与到达时刻必须对齐"；
    /// 上游想用 offset 覆盖的"另一侧"意图，改由多起点的 `dt`（synodic 分数）覆盖——
    /// 截住/交会本来就是**相位**的事，相位是 `dt` 的自由度。
    /// 收益：**本类返回的任何计划都满足"施加后真的交会/截住"**（物理判据，见测试），而上游不保证。
    ///
    /// `optguard` 参数**保留但忽略**（那是 alglib 的调试护栏，上游也只在 DEBUG 用）——
    /// 保持签名与上游一致，调用点不必分叉。
    /// </summary>
    public static class TwoImpulseTransfer
    {
        private struct Args
        {
            public V3 R1;
            public V3 V1;
            public V3 R2;
            public V3 V2;
            public bool Capture;
        }

        /// <summary>目标函数的"不可行"值（照搬上游的 `1e+300`，不用 `MaxValue` 以免求和溢出）。</summary>
        private const double INFEASIBLE = 1e+300;

        /// <summary>默认 `dt` 盒半宽的**上限倍数**（× 目标轨道周期）。依据见 `Maneuver` 那段"默认半宽必须封顶"。
        /// ⚠ **取 64 不是 8**（2026-09-20，被黄金值判据当场纠正）：先取 8 ⇒ `Δa=100 km` 那档的**合法远窗口**
        /// （291,136 s ≈ 35 个周期）被切掉、结果从 **19 m/s 变成 397 m/s** ✗ ⇒ 判据（成本探针的黄金值）抓到。
        /// 64 个周期足以覆盖一切现实窗口（100 km 档的 synodic ≈ 397,000 s ≈ 47 个周期 ⇒ 不触顶 ✓ 结果逐位不变 ✓），
        /// 而真正发散的近同轨（synodic → ∞）依旧被有界化 ✓（且与计算器那道 `T_syn > 200·转移时长` 守卫**同量级** ✓）。</summary>
        private const double SynodicCapPeriods = 64.0;

        private static (V3 dv1, V3 dv2) LambertFunction(double dt, double tt, double offset, Args args)
        {
            V3 r1 = args.R1;
            V3 v1 = args.V1;
            V3 r2 = args.R2;
            V3 v2 = args.V2;

            (V3 rburn, V3 vburn) = Shepperd.Solve(1.0, dt, r1, v1);
            (V3 rf2, V3 vf2) = Shepperd.Solve(1.0, dt + tt + offset, r2, v2);
            (V3 vi, V3 vf) = Izzo.Solve(1.0, rburn, rf2, tt, TransferGeometry.Prograde, 0, V3.Cross(rburn, vburn));

            return (vi - vburn, vf2 - vf);
        }

        /// <summary>
        /// **有界模式搜索**（`alglib.minbleic` 的替代品）：沿各坐标轴正负试探，接受下降者；
        /// 一轮全无下降就把各轴步长减半，直到步长小于 <paramref name="tol" /> 或达到 <paramref name="maxSweeps" />。
        /// 起点会被夹进盒子里；返回最优点，最优值走 <paramref name="fBest" />。
        ///
        /// `internal`（而不是 `private`）**只为可测**：本文件被测试工程**直接编译**（和上游一样按源码链接），
        /// 所以 `TwoImpulseTransferTests` 能直接验这个仪器本身——"仪器必须先被验证"。
        /// 生产代码里它没有别的调用者。
        /// </summary>
        internal static double[] CompassSearch(Func<double[], double> f, double[] x0, double[] lo, double[] hi,
            double[] step, double tol, int maxSweeps, out double fBest)
        {
            int n = x0.Length;
            double[] x = new double[n];
            for (int i = 0; i < n; i++)
                x[i] = MechJebLib.Utils.Statics.Clamp(x0[i], lo[i], hi[i]);

            fBest = f(x);

            double[] s = new double[n];
            for (int i = 0; i < n; i++)
                s[i] = step[i];

            for (int sweep = 0; sweep < maxSweeps; sweep++)
            {
                bool improved = false;

                for (int i = 0; i < n; i++)
                {
                    for (int d = 0; d < 2; d++)
                    {
                        double trial = MechJebLib.Utils.Statics.Clamp(x[i] + (d == 0 ? s[i] : -s[i]), lo[i], hi[i]);
                        if (trial == x[i]) continue;

                        double old = x[i];
                        x[i] = trial;
                        double fx = f(x);

                        if (fx < fBest)
                        {
                            fBest = fx;
                            improved = true;
                            break; // 该轴已下降 ⇒ 换下一轴
                        }

                        x[i] = old;
                    }
                }

                if (improved) continue;

                for (int i = 0; i < n; i++)
                    s[i] *= 0.5;

                bool tiny = true;
                for (int i = 0; i < n; i++)
                    if (s[i] > tol) tiny = false;
                if (tiny) break;
            }

            return x;
        }

        private static (V3 dv1, double dt1, V3 dv2, double dt2) Maneuver(double mu, V3 r1, V3 v1, V3 r2, V3 v2, double dtguess, double offsetGuess,
            bool coplanar = true, bool capture = true, double dtmin = double.NegativeInfinity,
            double dtmax = double.PositiveInfinity,
            double ttmin = double.NegativeInfinity, double ttmax = double.PositiveInfinity, double offsetMin = double.NegativeInfinity,
            double offsetMax = double.PositiveInfinity, bool optguard = false)
        {
            if (!IsFinite(mu))
                throw new ArgumentException("bad mu in TwoImpulseTransfer");
            if (!r1.IsFinite())
                throw new ArgumentException("bad r1 in TwoImpulseTransfer");
            if (!v1.IsFinite())
                throw new ArgumentException("bad v1 in TwoImpulseTransfer");
            if (!r2.IsFinite())
                throw new ArgumentException("bad r2 in TwoImpulseTransfer");
            if (!v2.IsFinite())
                throw new ArgumentException("bad v2 in TwoImpulseTransfer");

            var scale = Scale.Create(mu, Sqrt(r1.magnitude * r2.magnitude));

            if (coplanar)
            {
                // this rotation changes the target orbit to be coplanar（上游原样）
                V3 hhat1 = V3.Cross(r1, v1).normalized;
                V3 hhat2 = V3.Cross(r2, v2).normalized;
                var toCoplanar = Q3.FromToRotation(hhat2, hhat1);

                r2 = toCoplanar * r2;
                v2 = toCoplanar * v2;
            }

            r1 /= scale.LengthScale;
            v1 /= scale.VelocityScale;
            r2 /= scale.LengthScale;
            v2 /= scale.VelocityScale;
            dtguess /= scale.TimeScale;
            offsetGuess /= scale.TimeScale;
            dtmin /= scale.TimeScale;
            dtmax /= scale.TimeScale;
            ttmin /= scale.TimeScale;
            ttmax /= scale.TimeScale;
            offsetMin /= scale.TimeScale;
            offsetMax /= scale.TimeScale;

            (_, _, double ttguess, _) = Astro.HohmannTransferParameters(1.0, r1, r2);

            var args = new Args { R1 = r1, V1 = v1, R2 = r2, V2 = v2, Capture = capture };

            Func<double[], double> objective = x =>
            {
                double tt = x[1];
                // 转移时长必须为正：Lambert 对 tt <= 0 无解（上游同款护栏）
                if (!(tt > 0.0)) return INFEASIBLE;

                try
                {
                    (V3 dv1, V3 dv2) = LambertFunction(x[0], tt, x[2], args);
                    return capture ? dv1.magnitude + dv2.magnitude : dv1.magnitude;
                }
                catch (Exception)
                {
                    // 退化几何（共线/零角动量/迭代不收敛）⇒ 当作不可行，交给别的起点
                    return INFEASIBLE;
                }
            };

            // ── 盒子的构造（上游把这些交给 alglib 的 bc + stpmax，我们显式写出来）──
            double synodic = Astro.SynodicPeriod(1.0, r1, v1, r2, v2);
            double targetPeriod = Astro.PeriodFromStateVectors(1.0, r2, v2);

            // ⚠⚠ **默认半宽必须封顶**（2026-09-20 修，用户："MJ 优秀就按 MJ 来"整批扫描里找出来的唯一一处数值核偏离）。
            //   本文件唯一偏离上游处 = alglib `minbleic` → 手写模式搜索（换法本身对），但**默认盒子照抄了"synodic"** ✗：
            //   而 `Astro.SynodicPeriod` 在**近同轨**几何上**发散**（同轨 ⇒ ∞；实测 Δa=1 km ⇒ 3.8e7 s ≈ 一万小时）
            //   ⇒ 盒宽变成天文数字 ⇒ 步长 = 盒宽/8 ⇒ 60 轮 × 8 起点 × 每轮 ~6 次 Lambert = **几分钟不返回** ✗✗
            //   （实测：同轨目标 180 s 未返回；我此前只能在外面加 NaN 兜底当创可贴 ✗）。
            //   ⇒ 封顶：`min(synodic, SynodicCapPeriods × targetPeriod)`。
            //     非退化几何实测 synodic ≈ 3~4 × targetPeriod ⇒ **不触顶、结果逐位不变** ✓（有黄金值判据钉住）；
            //     只有近同轨才触顶 ⇒ 盒子有界 ⇒ 搜索按构造快速收敛 ✓✓。
            if (!(synodic > 0.0) || double.IsInfinity(synodic) || double.IsNaN(synodic))
                synodic = SynodicCapPeriods * targetPeriod;                 // 同轨/发散 ⇒ 直接用上限
            double synodicCapped = Math.Min(synodic, SynodicCapPeriods * targetPeriod);

            double[] lo = new double[3];
            double[] hi = new double[3];

            lo[0] = IsFinite(dtmin) ? dtmin : dtguess - 0.5 * synodicCapped;
            hi[0] = IsFinite(dtmax) ? dtmax : dtguess + 1.0 * synodicCapped;

            lo[1] = IsFinite(ttmin) ? ttmin : 0.1 * ttguess;
            hi[1] = IsFinite(ttmax) ? ttmax : 6.0 * ttguess;

            lo[2] = IsFinite(offsetMin) ? offsetMin : offsetGuess - targetPeriod;
            hi[2] = IsFinite(offsetMax) ? offsetMax : offsetGuess + targetPeriod;

            for (int i = 0; i < 3; i++)
                if (lo[i] > hi[i])
                    (lo[i], hi[i]) = (hi[i], lo[i]);

            // 步长：盒子的 1/8 是常用尺度；`tt` 用与 `ttguess` 相关的尺度（它的盒子可能很宽）
            double[] step = { (hi[0] - lo[0]) / 8.0, Math.Max(ttguess / 4.0, (hi[1] - lo[1]) / 16.0), (hi[2] - lo[2]) / 8.0 };
            double[] tol = { (hi[0] - lo[0]) * 1e-6, Math.Max(ttguess, hi[1] - lo[1]) * 1e-6, (hi[2] - lo[2]) * 1e-6 };

            // ── 多起点：`tt` 的分支（Hohmann 高/低转移）+ `dt` 的相位（synodic 的分数）＋ `offset` 的对侧 ──
            double[] ttSeeds = { ttguess, 1.5 * ttguess, 0.6 * ttguess };
            // ⚠ 种子必须**与盒子同尺度**（用封顶后的）：否则近同轨时种子落在盒外、被 Clamp 拉回同一点
            //   ⇒ "多起点"退化成单起点，反而更容易卡在局部极小 ✗
            double[] dtSeeds = { dtguess, dtguess + 0.25 * synodicCapped, dtguess + 0.5 * synodicCapped, dtguess + 0.75 * synodicCapped };

            // ── 批7 #1（2026-09-30）：12 个起点**各自独立**（只共享只读的 `args`/`objective` 输入）⇒ 并行 ──
            //   归约走 `ParallelOps.MinIndex`（**按下标严格取最小、并列取小下标**）——与原先"先到先得"
            //   （串行 foreach + 严格 `<`）在并列时**选同一个** ⇒ 结果与串行路**逐位相同**（`ParallelOpsTests` 钉住这条）。
            //   工作量：12 × 60 sweeps × ≤6 次 Lambert ⇒ 实测 5.4–8.3 ms（全库最大的纯 CPU 块）⇒ 值得并行。
            int nTtSeeds = ttSeeds.Length, nDtSeeds = dtSeeds.Length, nSeeds = nTtSeeds * nDtSeeds;
            var seedX = new double[nSeeds][];
            var seedF = new double[nSeeds];
            ParallelOps.For(nSeeds, 5e-4, k =>
            {
                // 下标映射与串行**同序**（外层 ttSeed、内层 dtSeed）——并列时选谁就靠这个顺序
                double[] seed = { dtSeeds[k % nDtSeeds], ttSeeds[k / nDtSeeds], offsetGuess };
                // ⚠ `CompassSearch` 内部的 try/catch **留在工作体内**：抛到 `Parallel.For` 外会变成
                //    `AggregateException`（改控制流），见 §17.4 #1 的告警
                seedX[k] = CompassSearch(objective, seed, lo, hi, step, tol[1], 60, out double fx);
                seedF[k] = fx;
            });

            int bestIdx = ParallelOps.MinIndex(seedF);
            if (bestIdx < 0 || !(seedF[bestIdx] < INFEASIBLE))
                throw new Exception(
                    $"TwoImpulseTransfer.Maneuver({mu}, {r1}, {v1}, {r2}, {v2}): 所有起点都不可行（无解）");

            double bestF = seedF[bestIdx];
            double[] best = seedX[bestIdx];

            (V3 dv1f, V3 dv2f) = LambertFunction(best[0], best[1], best[2], args);

            return (dv1f * scale.VelocityScale, best[0] * scale.TimeScale, dv2f * scale.VelocityScale, (best[0] + best[1]) * scale.TimeScale);
        }

        private static (V3 dv1, double dt1, V3 dv2, double dt2) ManeuverInternal(double mu, V3 r1, V3 v1, V3 r2, V3 v2, double dtguess,
            double lagTime = double.NaN, bool coplanar = true, bool rendezvous = true, bool capture = true,
            bool fixedtime = false, bool optguard = false)
        {
            V3 dv1, dv2;
            double dt1, dt2;

            double dtmin = double.NegativeInfinity;
            double dtmax = double.PositiveInfinity;

            if (fixedtime)
            {
                dtmin = dtguess;
                dtmax = dtguess;
            }

            if (rendezvous)
            {
                double offsetGuess = 0;
                double offsetMin = 0;
                double offsetMax = 0;
                if (IsFinite(lagTime))
                {
                    offsetMin = -lagTime;
                    offsetMax = -lagTime;
                    offsetGuess = -lagTime;
                }

                (dv1, dt1, dv2, dt2) =
                    Maneuver(mu, r1, v1, r2, v2, dtguess, offsetGuess, dtmin: dtmin, dtmax: dtmax, offsetMin: offsetMin, offsetMax: offsetMax,
                        coplanar: coplanar, capture: capture, optguard: optguard);
            }
            else
            {
                // ⚠ 偏离上游：这里**不再**放开 `offset`（上游传 ±∞，并另外用 `offset = T2/2` 试"目标轨道另一侧"）。
                // 理由见类注释第二处偏离：非零 offset 会让返回的计划不自洽。相位这一维由 `dt` 承担
                //（`Maneuver` 的多起点扫描了 synodic 的 0/¼/½/¾），**"另一侧"本来就在其中**。
                (dv1, dt1, dv2, dt2) =
                    Maneuver(mu, r1, v1, r2, v2, dtguess, 0, dtmin: dtmin, dtmax: dtmax,
                        offsetMin: 0, offsetMax: 0, coplanar: coplanar, capture: capture, optguard: optguard);
            }

            return (dv1, dt1, dv2, dt2);
        }

        public static (V3 dv1, double dt1, V3 dv2, double dt2) NextManeuver(double mu, V3 r1, V3 v1, V3 r2, V3 v2, int maxiter = 50,
            double lagTime = double.NaN, bool coplanar = true, bool rendezvous = true, bool capture = true,
            bool fixedTime = false, bool optguard = false)
            => NextManeuverCore(mu, r1, v1, r2, v2, 0.0, maxiter, lagTime, coplanar, rendezvous, capture, fixedTime, optguard);

        /// <summary>
        /// **批7 ② 的实验入口**（2026-09-30）：与 <see cref="NextManeuver" /> **同一条路**，但**首猜可给**
        /// （warm start：把上一次成功解出来的转移时长当首猜，省掉"从 0 摸"的那几轮重试）。
        ///
        /// ⚠ 为什么另开一个名字而**不往 `NextManeuver` 上加参数**：那条签名**必须保持上游原样**（J1）；
        ///   两者共用同一个 <see cref="NextManeuverCore" />（**一份实现**，J2）。
        /// ⚠ 它**会改结果**（换了首猜 ⇒ 搜索走另一个邻域 ⇒ 可能找到另一个局部极小）⇒
        ///   接进生产前必须先用蒙特卡洛台架量"更快了多少、质量掉了没有"。
        /// </summary>
        internal static (V3 dv1, double dt1, V3 dv2, double dt2) NextManeuverWarmStart(double mu, V3 r1, V3 v1, V3 r2, V3 v2,
            double warmDtGuess, int maxiter = 50, bool coplanar = true, bool rendezvous = true, bool capture = true,
            bool optguard = false)
            => NextManeuverCore(mu, r1, v1, r2, v2, warmDtGuess, maxiter, double.NaN, coplanar, rendezvous, capture, false, optguard);

        /// <summary>`NextManeuver` / `NextManeuverWarmStart` 的**唯一实现**（首猜参数化）。</summary>
        private static (V3 dv1, double dt1, V3 dv2, double dt2) NextManeuverCore(double mu, V3 r1, V3 v1, V3 r2, V3 v2,
            double initialDtGuess, int maxiter, double lagTime, bool coplanar, bool rendezvous, bool capture, bool fixedTime, bool optguard)
        {
            double synodicPeriod = Astro.SynodicPeriod(mu, r1, v1, r2, v2);

            if (fixedTime)
                return ManeuverInternal(mu, r1, v1, r2, v2, 0, coplanar: coplanar, rendezvous: rendezvous, capture: capture, optguard: optguard,
                    lagTime: lagTime, fixedtime: true);

            // ══ 批7 #1b（2026-09-30）：把并行**提到外层重试**，用**倍增批**（1 → 2 → 4 → 8）══
            //   · 实测外层平均真跑 ≈4.4 轮，且占单次调用成本的大头（`fixedTime` 一跳过它，成本从 ~2.2 ms 掉到 ~0.5 ms）；
            //   · 每个候选**只依赖自己的 `dtguess`** ⇒ 彼此独立；
            //   · `dtguess` 用**与串行同一套累加**（`+= synodic*0.1`）算 —— 写成 `k*synodic*0.1`
            //     在浮点上不一定逐位相同；
            //   · 取用时**按 iter 升序找第一个 `dt1 > 0`** ⇒ 与串行**同序、逐位相同**；
            //   · ⚠ **批大小必须倍增**（第一版写死 8 ⇒ 实测在"第 0 轮就成功"的几何上退步 7×：
            //     它把 7 个串行时根本不会算的候选也算了）。1→2→4→8 的代价是"最多算到所需轮数的 ~2 倍"，
            //     而**第 0 轮成功时与旧形状逐位同路、零浪费**（那是常见情形）。
            const int RetryBatchMax = 8;
            var cand = new (V3 dv1, double dt1, V3 dv2, double dt2)[RetryBatchMax];
            int batchSize = 1;
            double dtguess = initialDtGuess;   // ← 首猜（生产默认 0；warm start 传上一次成功的转移时长）
            for (int iter = 0; iter < maxiter; )
            {
                int batch = Min(batchSize, maxiter - iter);
                var guesses = new double[batch];
                double g = dtguess;
                for (int k = 0; k < batch; k++) { guesses[k] = g; g += synodicPeriod * 0.10; }

                if (batch == 1)
                {
                    cand[0] = ManeuverInternal(mu, r1, v1, r2, v2, guesses[0], coplanar: coplanar, rendezvous: rendezvous,
                        capture: capture, optguard: optguard, lagTime: lagTime);
                }
                else
                {
                    // 单项 ≈ 一次 12 种子搜索（实测 ~0.5 ms / 轮）⇒ 5e-4 是量级估计
                    ParallelOps.For(batch, 5e-4, k =>
                        cand[k] = ManeuverInternal(mu, r1, v1, r2, v2, guesses[k], coplanar: coplanar, rendezvous: rendezvous,
                            capture: capture, optguard: optguard, lagTime: lagTime));
                }

                for (int k = 0; k < batch; k++)
                    if (cand[k].dt1 > 0)
                        return cand[k];

                dtguess = g;                                   // 与串行的累加序列一致
                iter += batch;
                batchSize = Min(batchSize * 2, RetryBatchMax);  // 失败 ⇒ 下一批翻倍（有界）
            }

            throw new MechJebLibException("[MechJebLib] Maneuvers.NextManeuver.TwoImpulseTransfer(): too many iterations");
        }
    }
}
