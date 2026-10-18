using System;
using System.Diagnostics;
using PioneerFramework;
using UnityEngine;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **蒙特卡洛：导弹群 + 舰队来袭的感知段成本**（2026-10-02；用户想定 = 每侧 1000 导弹 + 20 飞船）。
    ///
    /// ## 它回答什么
    /// [V3] 认知层的配对律是"每个观测者 × 每个**敌方**匿踪目标"（实施计划 §1.8 [I] 第 1 条 +
    /// 本次的"按阵营分目标表"）。这个规模比计划稿假设的"~10 观测者 × ~30 匿踪"大一个数量级
    /// ⇒ 先把**成本分布**量出来，再谈升级位（小网格 / 并行 / 降频）要不要上、阈值定在哪。
    ///
    /// 三臂并排（**同进程交替**：每步依次跑三臂 ⇒ 共享同一份几何与负载，避免跨进程漂移）：
    /// · **纯算术臂（主臂）**：因子形式 `d² ≤ A_obs·B_tgt`（[L](a) 的热路径镜像）+ 平方形式波束窄相
    ///   + 真 `ContactFusion` 合并 ⇒ 计划里的"地板"；
    /// · **真对象臂**：判据量走真属性（`DetectTech` / `SignalStrength`）与真谓词（`RadarBeamContains`，
    ///   含一次 `sqrt`）⇒ 量"真对象开销"与"sqrt 的代价"；
    /// · **并行臂**：`ParallelOps.For` 按观测者切、逐观测者计数槽 ⇒ 与串行**计数逐位一致**是判据。
    ///
    /// ## 它**不**测什么（别把这份读数当整步成本）
    /// 不测：整步物理（`UpdateAllMovements` 的积分 / 引力场 / 操舵）、导弹制导、碰撞、渲染。
    /// 那些只有游戏内能测（`[步耗时]` 常驻仪器 + 独立的一键铺场场景）。
    ///
    /// ## 口径（读数纪律）
    /// **绝对耗时是 Debug 口径**（测试默认 Debug；游戏 Release 快 2–6×）⇒ 横幅必打、不许跨配置比较。
    /// 时间只进读数、**绝不进 PASS/FAIL**（观测项纪律）。判据只有四条：
    /// ① 同种子重放计数逐位相同；② 串行 vs 并行计数逐位相同；③ 抽样对与 `DetectionCore.IsVisible` 结论一致；
    /// ④ 非退化（有对进判据）。
    ///
    /// ## 与生产的关系（避免"第二份算式"）
    /// 配对循环是 [V3] 的**预演**（[V3] 尚未落地）；判据本体一律由纯核心 `DetectionCore` 定，本台架
    /// **逐对抽样断言**因子形式与它等价。**[V3] 落地后本台架应改为直接调生产 sweep（登记项）。**
    ///
    /// ## 场景的诚实边界
    /// · 运动学 = **匀速直线漂移**（不是积分）——本台架测成本分布，不测物理；
    /// · 真对象臂的目标侧信号只带"点火/开机"项（headless 无实体 ⇒ 尺寸/热/电项为 0，
    ///   分辨率说明见 `SignalInvalidationTests`）；纯算术臂按**真实尺寸档注入**（导弹 1.5 m / 舰 25 m）、
    ///   走同一个 `SignalModel.Total`；
    /// · 舰队间距、点火比例、开机比例、弥散**按轮随机**（这是"蒙特卡洛"的部分），几何不演化成真实交战。
    /// </summary>
    public class MissileSwarmMonteCarloBench
    {
        // ── 想定规模（用户 2026-10-02 给的值）──
        private const int ShipsPerSide = 20;
        private const int MissilesPerSide = 1000;
        private const int Trials = 4;
        private const int StepsPerTrial = 18;
        private const int WarmupSteps = 2;
        private const int WorstCaseSteps = 3;

        // ── 装配（取 Def 值：Buildings_SensorArray / Buildings_ActiveRadar）──
        private const float SensorSensitivityMeters = 5.0e6f;   // 5000 km
        private const float BeamHalfAngleDeg = 15f;
        private const float BeamRangeMeters = 2.0e7f;
        private const int BeamEveryNthShip = 3;                 // 每 3 艘 1 艘装主动雷达（波束受限）

        // ── 运动学（只为让候选分布真实）──
        private const double StepSeconds = 2.0;                 // 物理步（UPDATE_INTERVAL_TICKS = 2）
        private const double MissileClosingSpeed = 2500.0;      // 出膛增量 m/s
        private const double ShipSpeed = 7500.0;                // 舰速量级 m/s
        private const double ShipRadiusMeters = 25.0;           // 视在半径（纯算术臂的尺寸项）
        private const double MissileRadiusMeters = 1.5;

        private const int Factions = 2;

        private readonly ITestOutputHelper _out;

        public MissileSwarmMonteCarloBench(ITestOutputHelper output) => _out = output;

        // ═══════════════════════════════════════════════════════════════════
        //  判据（只有四条；耗时一律不进判定）
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void Swarm2040_CostDistribution_SerialParallel_Deterministic()
        {
            var r1 = RunAll(20261002UL);
            var r2 = RunAll(20261002UL);

            // ① 同种子可复现（计数逐位相同）
            Assert.Equal(r1.Fact.InRange, r2.Fact.InRange);
            Assert.Equal(r1.Fact.Hits, r2.Fact.Hits);
            Assert.Equal(r1.Fact.Pairs, r2.Fact.Pairs);
            Assert.Equal(r1.Fact.ContactWrites, r2.Fact.ContactWrites);
            Assert.Equal(r1.Fact.EquivSamples, r2.Fact.EquivSamples);
            Assert.Equal(r1.Fact.EquivMismatch, r2.Fact.EquivMismatch);
            Assert.Equal(r1.Real.InRange, r2.Real.InRange);
            Assert.Equal(r1.Par.InRange, r2.Par.InRange);

            // ② 串行 vs 并行：计数逐位相同（ParallelOps 的既有纪律）
            Assert.Equal(r1.Fact.InRange, r1.Par.InRange);
            Assert.Equal(r1.Fact.Hits, r1.Par.Hits);

            // ③ 等价性抽样：与唯一规范入口 `DetectionCore.IsVisible` 结论不一致 = 0
            Assert.True(r1.Fact.EquivSamples > 0, "等价性抽样为 0 ⇒ 这条判据没跑");
            Assert.Equal(0, r1.Fact.EquivMismatch);

            // ④ 非退化：这个想定下必须有对进判据（否则量的是空场景）
            Assert.True(r1.Fact.InRange > 0, "没有任何一对进判据 ⇒ 场景规模/灵敏度配错");

            var worst = WorstCase();
            var curve = ScaleCurve();
            Report(r1, worst, curve);
        }

        /// <summary>规模曲线：每侧 (舰, 弹) 四个点 ⇒ 对数增长律与膝盖点（只读数、不判定）。</summary>
        private double[][] ScaleCurve()
        {
            int[][] points =
            {
                new[] { 5, 100 },
                new[] { 10, 300 },
                new[] { 20, 1000 },
                new[] { 40, 2000 },
            };
            var rows = new double[points.Length][];
            for (int p = 0; p < points.Length; p++)
            {
                int ships = points[p][0], missiles = points[p][1];
                var sc = BuildScene(20261002UL, 1.0e7, 6.0e7, ships, missiles);
                var b = new Buffers(sc);
                var c = new Counters();
                var us = new double[6];
                for (int i = 0; i < 2; i++) { Advance(sc); StepFactored(sc, b, 0.0, c, null, true); }   // 预热
                for (int i = 0; i < us.Length; i++)
                {
                    Advance(sc);
                    long t0 = Stopwatch.GetTimestamp();
                    StepFactored(sc, b, i * StepSeconds, c, null, false);
                    us[i] = Ms(t0);
                }
                long pairsPerStep = 2L * ships * missiles;   // 跨阵营剪枝后：观测者(2×舰) × 敌导弹(弹)
                rows[p] = new double[] { ships, missiles, Pct(us, 0.50), pairsPerStep };
            }
            return rows;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  蒙特卡洛主体：每步 = 漂移一次 → 三臂各跑一遍（各自缓冲、各自计数）
        // ═══════════════════════════════════════════════════════════════════

        private sealed class Counters
        {
            public long Steps, Pairs, InRange, BeamTested, Hits, ContactWrites, SameFactionSkipped;
            public long EquivSamples, EquivMismatch, EquivNearBoundary;
        }

        private sealed class All
        {
            public readonly Counters Fact = new Counters();
            public readonly Counters Real = new Counters();
            public readonly Counters Par = new Counters();
            public double[] FactUs, RealUs, ParUs, N1Us, FactorUs, PairUs;
        }

        private All RunAll(ulong seed)
        {
            var acc = new All();
            int perTrial = StepsPerTrial;
            acc.FactUs = new double[Trials * perTrial];
            acc.RealUs = new double[Trials * perTrial];
            acc.ParUs = new double[Trials * perTrial];
            acc.N1Us = new double[Trials * perTrial];
            acc.FactorUs = new double[Trials * perTrial];
            acc.PairUs = new double[Trials * perTrial];

            for (int t = 0; t < Trials; t++)
            {
                var sc = BuildScene(seed + (ulong)t * 7919UL, 2.0e7, 4.0e8);   // 随机间距档
                var bF = new Buffers(sc);
                var bR = new Buffers(sc);
                var bP = new Buffers(sc);

                for (int w = 0; w < WarmupSteps; w++) { Advance(sc); StepFactored(sc, bF, w * StepSeconds, acc.Fact, null, true); }

                for (int s = 0; s < perTrial; s++)
                {
                    int idx = t * perTrial + s;
                    Advance(sc);
                    double tick = (WarmupSteps + s) * StepSeconds;

                    long t0 = Stopwatch.GetTimestamp();
                    StepFactored(sc, bF, tick, acc.Fact, acc, false);
                    acc.FactUs[idx] = Ms(t0);

                    t0 = Stopwatch.GetTimestamp();
                    StepRealObjects(sc, bR, tick, acc.Real);
                    acc.RealUs[idx] = Ms(t0);

                    t0 = Stopwatch.GetTimestamp();
                    StepParallel(sc, bP, tick, acc.Par);
                    acc.ParUs[idx] = Ms(t0);
                }
            }
            return acc;
        }

        /// <summary>最坏档：全在范围内 + 全点火 + **全向（无波束剪枝）** ⇒ 命中率拉满（= 合并负载拉满）。</summary>
        private WorstSnap WorstCase()
        {
            var sc = BuildScene(4294967291UL, 4.0e6, 5.0e6);   // 间距 4,000–5,000 km ⇒ 全部进判据
            for (int i = 0; i < sc.Observers.Length; i++) sc.ObsBeam[i] = false;   // 全向哨戒（不剪枝）
            for (int j = 0; j < sc.Stealth.Length; j++)
            {
                var g = sc.Groups[sc.Stealth[j]];
                g.HasActiveBurn = true;                        // 全点火（B 最大 ⇒ 全部进判据）
                g.InvalidateSignal();
            }
            var b = new Buffers(sc);
            var snap = new WorstSnap { Us = new double[WorstCaseSteps], C = new Counters() };
            for (int i = 0; i < WorstCaseSteps; i++)
            {
                Advance(sc);
                long t0 = Stopwatch.GetTimestamp();
                StepFactored(sc, b, i * StepSeconds, snap.C, null, false);
                snap.Us[i] = Ms(t0);
            }
            return snap;
        }

        private sealed class WorstSnap { public double[] Us; public Counters C; }

        // ═══════════════════════════════════════════════════════════════════
        //  臂 1（主臂）：纯算术 —— 因子形式 + 平方形式波束 + 真融合
        // ═══════════════════════════════════════════════════════════════════

        private void StepFactored(Scene sc, Buffers b, double tick, Counters acc, All timing, bool countOnly)
        {
            // ── N1：O(N) 过滤（公开/匿踪/有无传感器）+ 按阵营分目标表（计数排序）──
            long t0 = Stopwatch.GetTimestamp();
            int nTgt = sc.Stealth.Length;
            for (int f = 0; f < Factions; f++) b.FactionCount[f] = 0;
            for (int j = 0; j < nTgt; j++) b.FactionCount[sc.Faction[sc.Stealth[j]] - 1]++;
            int off = 0;
            for (int f = 0; f < Factions; f++) { b.FactionStart[f] = off; off += b.FactionCount[f]; }
            b.FactionStart[Factions] = off;
            for (int f = 0; f < Factions; f++) b.FactionFill[f] = b.FactionStart[f];
            for (int j = 0; j < nTgt; j++)
            {
                int f = sc.Faction[sc.Stealth[j]] - 1;
                b.TgtByFaction[b.FactionFill[f]++] = j;
            }
            double n1Us = Ms(t0);

            // ── N2/N3：因子（每观测者一次 / 每目标一次）──
            t0 = Stopwatch.GetTimestamp();
            int nObs = sc.Observers.Length;
            for (int i = 0; i < nObs; i++)
            {
                var g = sc.Groups[sc.Observers[i]];
                var tech = g.DetectTech;
                double sens = tech.sensitivityMeters;
                b.A[i] = sens > 0.0 ? sens * sens : 0.0;      // J = 0（v1 无干扰）
                b.Bdir[i] = g.RadarBoresight(tick);
                double ch = System.Math.Cos(BeamHalfAngleDeg * System.Math.PI / 180.0);
                b.CosHalfSq[i] = ch * ch;
            }
            for (int j = 0; j < nTgt; j++)
            {
                var g = sc.Groups[sc.Stealth[j]];
                b.B[j] = SignalModel.Total(new SignalInputs
                {
                    rcsRadiusMeters = MissileRadiusMeters,     // 尺寸档（补 headless 缺的尺寸项）
                    burning = g.HasActiveBurn,
                    activePing = false,                        // 导弹不开机
                });
            }
            double factorUs = Ms(t0);

            // ── N4 配对（+窄相） + N5 合并 ──
            t0 = Stopwatch.GetTimestamp();
            for (int j = 0; j < nTgt; j++) b.BestFid[j] = 0.0;
            for (int j = 0; j < nTgt; j++) b.BestFid[nTgt + j] = 0.0;

            long pairs = 0, inRange = 0, beamTested = 0, hits = 0, writes = 0, skipped = 0;
            for (int i = 0; i < nObs; i++)
            {
                if (b.A[i] <= 0.0) continue;
                var obs = sc.Groups[sc.Observers[i]];
                DVector3 op = obs.CurrentOrbit.positionSystem.v;
                int fo = sc.Faction[sc.Observers[i]] - 1;
                double a = b.A[i];
                bool beam = sc.ObsBeam[i];
                double cosHalfSq = b.CosHalfSq[i];
                DVector3 bdir = b.Bdir[i];
                var probe = new Contact { source = ContactSource.OwnSensor };   // σ=0
                double fidObs = ContactFusion.Fidelity(in probe);               // 逐观测者一次

                for (int f = 0; f < Factions; f++)
                {
                    if (f == fo) { skipped += b.FactionCount[f]; continue; }   // 同阵营不进判据
                    int end = b.FactionStart[f + 1];
                    for (int k = b.FactionStart[f]; k < end; k++)
                    {
                        int j = b.TgtByFaction[k];
                        pairs++;
                        int tg = sc.Stealth[j];
                        DVector3 d = sc.Groups[tg].CurrentOrbit.positionSystem.v - op;
                        double d2 = d.sqrMagnitude;
                        double bb = b.B[j];
                        if (bb <= 0.0 || d2 > a * bb) continue;      // ← 判据即粗筛（无 sqrt 无除法）
                        inRange++;
                        if (beam)
                        {
                            double dot = bdir.x * d.x + bdir.y * d.y + bdir.z * d.z;
                            if (!(dot > 0.0 && dot * dot >= cosHalfSq * d2)) continue;
                            beamTested++;
                        }
                        hits++;
                        int slot = fo * nTgt + j;
                        // ⚠ 保真度**只依赖 (来源, σ)** ⇒ 逐观测者算一次即可；不改进就不构造 `Contact`。
                        //（这与 `ContactFusion.ShouldReplace` 严格等价——后者展开就是同一个比较。）
                        // 反例教训：先把 88 字节的 Contact 构造出来再判"要不要写"，实测 92% 是白构造。
                        if (fidObs <= b.BestFid[slot]) continue;
                        var c = new Contact
                        {
                            targetGroupId = tg,
                            decoyId = -1,
                            position = sc.Groups[tg].CurrentOrbit.positionSystem.v,
                            velocity = sc.Groups[tg].CurrentOrbit.velocitySystem.v,
                            lastUpdatePhysics = tick,
                            positionSigma = 0.0,
                            source = ContactSource.OwnSensor,
                            isRadiationSource = false,
                            apparentRadius = (float)MissileRadiusMeters,
                        };
                        b.Best[slot] = c; b.BestFid[slot] = fidObs; writes++;
                    }
                }
            }
            double pairUs = Ms(t0);

            long eqS = 0, eqM = 0, eqN = 0;
            if (!countOnly) SampleEquivalence(sc, b, ref eqS, ref eqM, ref eqN);

            if (!countOnly)
            {
                acc.Steps++; acc.Pairs += pairs; acc.InRange += inRange; acc.BeamTested += beamTested;
                acc.Hits += hits; acc.ContactWrites += writes; acc.SameFactionSkipped += skipped;
                acc.EquivSamples += eqS; acc.EquivMismatch += eqM; acc.EquivNearBoundary += eqN;
                if (timing != null) { timing.N1Us[acc.Steps - 1] = n1Us; timing.FactorUs[acc.Steps - 1] = factorUs; timing.PairUs[acc.Steps - 1] = pairUs; }
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  臂 2：真对象 —— 真属性 + 真谓词（含 sqrt）
        // ═══════════════════════════════════════════════════════════════════

        private static void StepRealObjects(Scene sc, Buffers b, double tick, Counters acc)
        {
            int nObs = sc.Observers.Length, nTgt = sc.Stealth.Length;
            long inRange = 0, hits = 0;

            for (int i = 0; i < nObs; i++)
            {
                var obs = sc.Groups[sc.Observers[i]];
                var tech = obs.DetectTech;
                if (!(tech.sensitivityMeters > 0f)) continue;
                DVector3 op = obs.CurrentOrbit.positionSystem.v;
                bool beam = sc.ObsBeam[i];
                for (int j = 0; j < nTgt; j++)
                {
                    int tg = sc.Stealth[j];
                    if (sc.Faction[tg] == sc.Faction[sc.Observers[i]]) continue;
                    var tgo = sc.Groups[tg];
                    double s = tgo.SignalStrength;                     // 真属性（含缓存/失效路径）
                    if (!(s > 0.0)) continue;
                    DVector3 tp = tgo.CurrentOrbit.positionSystem.v;
                    if (!DetectionCore.IsVisible(s, (tp - op).magnitude, in tech, 0.0)) continue;   // 真判据（sqrt）
                    inRange++;
                    if (beam && !obs.RadarBeamContains(tp, tick, out _)) continue;                 // 真谓词（sqrt）
                    hits++;
                }
            }
            acc.Steps++; acc.InRange += inRange; acc.Hits += hits;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  臂 3：并行（按观测者切；只写自己的计数槽）
        // ═══════════════════════════════════════════════════════════════════

        private static void StepParallel(Scene sc, Buffers b, double tick, Counters acc)
        {
            int nObs = sc.Observers.Length, nTgt = sc.Stealth.Length;
            // 因子（N2/N3）与分阵营表（N1）——与串行臂同形
            for (int i = 0; i < nObs; i++)
            {
                var g = sc.Groups[sc.Observers[i]];
                var tech = g.DetectTech;
                double sens = tech.sensitivityMeters;
                b.A[i] = sens > 0.0 ? sens * sens : 0.0;
                b.Bdir[i] = g.RadarBoresight(tick);
                double ch = System.Math.Cos(BeamHalfAngleDeg * System.Math.PI / 180.0);
                b.CosHalfSq[i] = ch * ch;
            }
            for (int j = 0; j < nTgt; j++)                                   // 本臂自己的目标因子（不许借用别臂的缓冲）
            {
                var g = sc.Groups[sc.Stealth[j]];
                b.B[j] = SignalModel.Total(new SignalInputs
                {
                    rcsRadiusMeters = MissileRadiusMeters,
                    burning = g.HasActiveBurn,
                    activePing = false,
                });
            }

            double costPerObs = nTgt * 1e-8;    // 量级：2000 目标 ≈ 20 µs（只决定走哪条路）
            ParallelOps.For(nObs, costPerObs, i =>
            {
                if (b.A[i] <= 0.0) { b.InRangePerObs[i] = 0; b.HitsPerObs[i] = 0; return; }
                var obs = sc.Groups[sc.Observers[i]];
                DVector3 op = obs.CurrentOrbit.positionSystem.v;
                int fo = sc.Faction[sc.Observers[i]] - 1;
                double a = b.A[i];
                bool beam = sc.ObsBeam[i];
                double cosHalfSq = b.CosHalfSq[i];
                DVector3 bdir = b.Bdir[i];
                int inR = 0, hit = 0;
                for (int j = 0; j < nTgt; j++)
                {
                    int tg = sc.Stealth[j];
                    if (sc.Faction[tg] - 1 == fo) continue;
                    DVector3 d = sc.Groups[tg].CurrentOrbit.positionSystem.v - op;
                    double d2 = d.sqrMagnitude;
                    double bb = b.B[j];                      // B 由主臂本步算好（同形即可，成本另计）
                    if (bb <= 0.0 || d2 > a * bb) continue;
                    inR++;
                    if (beam)
                    {
                        double dot = bdir.x * d.x + bdir.y * d.y + bdir.z * d.z;
                        if (!(dot > 0.0 && dot * dot >= cosHalfSq * d2)) continue;
                    }
                    hit++;
                }
                b.InRangePerObs[i] = inR;
                b.HitsPerObs[i] = hit;
            });

            long inR2 = 0, hits2 = 0;
            for (int i = 0; i < nObs; i++) { inR2 += b.InRangePerObs[i]; hits2 += b.HitsPerObs[i]; }
            acc.Steps++; acc.InRange += inR2; acc.Hits += hits2;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  等价性抽样（不计时）：因子形式 vs `DetectionCore.IsVisible`
        // ═══════════════════════════════════════════════════════════════════

        private static void SampleEquivalence(Scene sc, Buffers b, ref long samples, ref long mismatch, ref long nearBoundary)
        {
            int nObs = sc.Observers.Length, nTgt = sc.Stealth.Length;
            int stepObs = System.Math.Max(1, nObs / 8);
            int stepTgt = System.Math.Max(1, nTgt / 8);
            for (int i = 0; i < nObs; i += stepObs)
            {
                var obs = sc.Groups[sc.Observers[i]];
                var tech = obs.DetectTech;
                DVector3 op = obs.CurrentOrbit.positionSystem.v;
                for (int j = 0; j < nTgt; j += stepTgt)
                {
                    int tg = sc.Stealth[j];
                    if (sc.Faction[tg] == sc.Faction[sc.Observers[i]]) continue;
                    double bb = b.B[j];
                    if (!(bb > 0.0) || !(b.A[i] > 0.0)) continue;
                    DVector3 d = sc.Groups[tg].CurrentOrbit.positionSystem.v - op;
                    double d2 = d.sqrMagnitude;
                    double rhs = b.A[i] * bb;
                    bool factored = d2 <= rhs;
                    bool canonical = DetectionCore.IsVisible(bb, System.Math.Sqrt(d2), in tech, 0.0);
                    samples++;
                    if (factored != canonical)
                    {
                        double gap = System.Math.Abs(d2 - rhs) / System.Math.Max(1e-30, rhs);
                        if (gap < 1e-6) nearBoundary++;    // 边界带内（浮点次序差）⇒ 单独计数
                        else mismatch++;
                    }
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  场景
        // ═══════════════════════════════════════════════════════════════════

        private sealed class Scene
        {
            public SpaceEntityGroup[] Groups;
            public int[] Observers;
            public bool[] ObsBeam;
            public int[] Stealth;
            public int[] Faction;
            public DVector3[] Vel;
            public int BeamObservers;
        }

        private static Scene BuildScene(ulong seed, double sepMin, double sepMax,
            int shipsPerSide = ShipsPerSide, int missilesPerSide = MissilesPerSide)
        {
            var rng = new Rng(seed);
            int ships = shipsPerSide, missiles = missilesPerSide;
            int n = Factions * (ships + missiles);

            var sc = new Scene
            {
                Groups = new SpaceEntityGroup[n],
                Observers = new int[Factions * ships],
                ObsBeam = new bool[Factions * ships],
                Stealth = new int[Factions * missiles],
                Faction = new int[n],
                Vel = new DVector3[n],
            };

            double sep = rng.Range(sepMin, sepMax);
            double burnFrac = rng.Range(0.0, 1.0);           // 本轮点火（终末段）比例
            double dispersion = sep * rng.Range(0.005, 0.05);
            var sideCenter = new DVector3[Factions];
            sideCenter[0] = new DVector3(1.5e8, 0.0, 0.0);
            sideCenter[1] = sideCenter[0] + RandomUnit(rng) * sep;

            int gi = 0, oi = 0, si = 0;
            for (int side = 0; side < Factions; side++)
            {
                DVector3 enemy = sideCenter[1 - side];
                for (int k = 0; k < ships; k++)
                {
                    var pos = sideCenter[side] + RandomUnit(rng) * (dispersion * rng.Next01());
                    var g = new SpaceEntityGroup(gi, side + 1)
                    {
                        IsStealth = false,                                   // 舰 = 公开应答机（[N3] 默认）
                        BuildingTechBonus = new SensorTech { sensitivityMeters = SensorSensitivityMeters },
                    };
                    if (k % BeamEveryNthShip == 0 && sep < 5.0e7)            // 波束受限的哨戒档
                    {
                        g.RadarCap = new RadarCapability
                        {
                            beamHalfAngleDeg = BeamHalfAngleDeg,
                            beamRangeMeters = BeamRangeMeters,
                            scanRateDegPerSec = 6f,
                        };
                        sc.ObsBeam[oi] = true;
                        sc.BeamObservers++;
                    }
                    if (rng.Next01() < 0.3) g.ActivePingEnabled = true;      // 开机比例随机（W1 暴露项）
                    sc.Groups[gi] = g;
                    SetOrbit(sc, gi, pos, RandomUnit(rng) * ShipSpeed);
                    sc.Faction[gi] = side + 1;
                    sc.Observers[oi++] = gi;
                    gi++;
                }

                DVector3 aim = (enemy - sideCenter[side]).normalized;
                for (int m = 0; m < missiles; m++)
                {
                    int carrier = (side * ships) + (m % ships);
                    var pos = sc.Groups[sc.Observers[carrier]].CurrentOrbit.positionSystem.v
                              + RandomUnit(rng) * (5000.0 * rng.Next01());
                    var vel = aim * MissileClosingSpeed + RandomUnit(rng) * 400.0;
                    var g = new SpaceEntityGroup(gi, side + 1) { IsStealth = true };
                    g.HasActiveBurn = rng.Next01() < burnFrac;               // public 字段（生产只有 StartBurn/EndBurn 挂钩）
                    g.InvalidateSignal();                                    // ⇒ 台架显式置脏，模拟翻转路径
                    sc.Groups[gi] = g;
                    SetOrbit(sc, gi, pos, vel);
                    sc.Faction[gi] = side + 1;
                    sc.Stealth[si++] = gi;
                    gi++;
                }
            }
            return sc;
        }

        private static void SetOrbit(Scene sc, int i, DVector3 pos, DVector3 vel)
        {
            sc.Vel[i] = vel;
            sc.Groups[i].CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)pos,
                velocitySystem = (SystemVec3)vel,
                rotation = DQuaternion.identity,
            };
        }

        /// <summary>匀速直线漂移（本台架只测成本分布，不测物理）。</summary>
        private static void Advance(Scene sc)
        {
            for (int i = 0; i < sc.Groups.Length; i++)
            {
                var g = sc.Groups[i];
                var o = g.CurrentOrbit;
                g.CurrentOrbit = new OrbitState
                {
                    positionSystem = (SystemVec3)(o.positionSystem.v + sc.Vel[i] * StepSeconds),
                    velocitySystem = o.velocitySystem,
                    rotation = o.rotation,
                };
            }
        }

        private sealed class Buffers
        {
            public readonly double[] A, B, CosHalfSq;
            public readonly DVector3[] Bdir;
            public readonly Contact[] Best;
            public readonly double[] BestFid;
            public readonly int[] HitsPerObs, InRangePerObs;
            public readonly int[] TgtByFaction;
            public readonly int[] FactionStart = new int[Factions + 1];
            public readonly int[] FactionCount = new int[Factions];
            public readonly int[] FactionFill = new int[Factions];

            public Buffers(Scene sc)
            {
                A = new double[sc.Observers.Length];
                B = new double[sc.Stealth.Length];
                Bdir = new DVector3[sc.Observers.Length];
                CosHalfSq = new double[sc.Observers.Length];
                Best = new Contact[Factions * sc.Stealth.Length];
                BestFid = new double[Factions * sc.Stealth.Length];
                HitsPerObs = new int[sc.Observers.Length];
                InRangePerObs = new int[sc.Observers.Length];
                TgtByFaction = new int[sc.Stealth.Length];
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  报告
        // ═══════════════════════════════════════════════════════════════════

        private void Report(All r, WorstSnap worst, double[][] curve)
        {
            int ships = ShipsPerSide, missiles = MissilesPerSide;
            long nominalPerStep = (long)(2 * ships) * (2 * missiles);
            long prunedPerStep = 2L * ships * missiles;
            Line($"[来袭·MC] 口径：{(IsDebug() ? "Debug（比 Release 慢 2–6×；绝对耗时不许跨配置比较）" : "Release")}｜物理步 {StepSeconds:F1} s/步");
            Line($"[来袭·MC] 想定 2×({ships} 舰 + {missiles} 弹)：组 {2 * (ships + missiles)}｜观测者 {2 * ships}｜匿踪 {2 * missiles}｜名义对/步 {nominalPerStep}（跨阵营剪枝后 {prunedPerStep}）");
            Line($"[来袭·MC] 蒙特卡洛：{Trials} 轮 × {StepsPerTrial} 步 = {r.Fact.Steps} 步（另每轮预热 {WarmupSteps} 步不计）");
            Line($"[来袭·MC] 纯算术臂  p50 {Pct(r.FactUs, 0.50):F1} µs/步｜p95 {Pct(r.FactUs, 0.95):F1}｜max {Max(r.FactUs):F1}");
            Line($"[来袭·MC] 真对象臂  p50 {Pct(r.RealUs, 0.50):F1} µs/步｜p95 {Pct(r.RealUs, 0.95):F1}｜max {Max(r.RealUs):F1}（真属性 + 真谓词，含 sqrt）");
            Line($"[来袭·MC] 并行臂    p50 {Pct(r.ParUs, 0.50):F1} µs/步｜p95 {Pct(r.ParUs, 0.95):F1}｜max {Max(r.ParUs):F1}（核数 {Environment.ProcessorCount}）");
            Line($"[来袭·MC] 分段（纯算术臂）p50：N1 划分+分阵营表 {Pct(r.N1Us, 0.50):F1}｜N2+N3 因子 {Pct(r.FactorUs, 0.50):F1}｜N4+N5 配对+合并 {Pct(r.PairUs, 0.50):F1} µs");
            Line($"[来袭·MC] 命中统计（{r.Fact.Steps} 步合计）：对 {r.Fact.Pairs}｜进判据 {r.Fact.InRange}｜窄相 {r.Fact.BeamTested}｜命中 {r.Fact.Hits}｜接触写 {r.Fact.ContactWrites}｜同阵营跳过 {r.Fact.SameFactionSkipped}");
            Line($"[来袭·MC] 串并行计数：进判据 {r.Fact.InRange} vs {r.Par.InRange}｜命中 {r.Fact.Hits} vs {r.Par.Hits} ⇒ {(r.Fact.InRange == r.Par.InRange && r.Fact.Hits == r.Par.Hits ? "逐位一致 ✓" : "**不一致 ✗**")}");
            Line($"[来袭·MC] 等价性抽样：{r.Fact.EquivSamples} 对｜与 DetectionCore.IsVisible 不一致 {r.Fact.EquivMismatch}｜边界带内（<1e-6 相对）{r.Fact.EquivNearBoundary}");
            Line($"[来袭·MC] 最坏档（全在范围内 + 全点火 + **全向无剪枝**，{WorstCaseSteps} 步）：{Pct(worst.Us, 0.50):F1} µs/步｜命中 {worst.C.Hits}｜接触写 {worst.C.ContactWrites}");
            var sb = new System.Text.StringBuilder("[来袭·MC] 规模曲线（纯算术臂 p50，6 步/点）：");
            for (int p = 0; p < curve.Length; p++)
            {
                var row = curve[p];
                double perPair = row[2] / System.Math.Max(1.0, row[3]);
                sb.Append($"\n[来袭·MC]   {row[0]:F0} 舰 + {row[1]:F0} 弹/侧 ⇒ 对/步 {row[3]:F0}｜{row[2]:F1} µs/步｜{perPair * 1000.0:F2} ns/对");
            }
            Line(sb.ToString());
            Line("[来袭·MC] 台架边界：不含整步物理 / 制导 / 碰撞 / 渲染 —— 那些只有游戏内 `[步耗时]` 能测");
        }

        private void Line(string s)
        {
            _out.WriteLine(s);
            Console.WriteLine(s);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  小工具
        // ═══════════════════════════════════════════════════════════════════

        private struct Rng
        {
            private ulong _s;
            public Rng(ulong seed) => _s = seed | 1UL;
            public ulong Next() { _s ^= _s << 13; _s ^= _s >> 7; _s ^= _s << 17; return _s; }
            public double Next01() => (Next() >> 11) * (1.0 / 9007199254740992.0);
            public double Range(double a, double b) => a + (b - a) * Next01();
        }

        private static DVector3 RandomUnit(Rng rng)
        {
            double z = rng.Range(-1.0, 1.0);
            double phi = rng.Range(0.0, 2.0 * System.Math.PI);
            double r = System.Math.Sqrt(System.Math.Max(0.0, 1.0 - z * z));
            return new DVector3(r * System.Math.Cos(phi), z, r * System.Math.Sin(phi));
        }

        private static double Ms(long t0) => (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;

        private static bool IsDebug()
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }

        private static double Pct(double[] xs, double p)
        {
            var copy = (double[])xs.Clone();
            Array.Sort(copy);
            int i = (int)System.Math.Round(p * (copy.Length - 1));
            return copy[System.Math.Max(0, System.Math.Min(copy.Length - 1, i))];
        }

        private static double Max(double[] xs)
        {
            double m = 0.0;
            for (int i = 0; i < xs.Length; i++) if (xs[i] > m) m = xs[i];
            return m;
        }
    }
}
