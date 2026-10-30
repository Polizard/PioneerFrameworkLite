using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PioneerFramework;
using PioneerFramework.Combat;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **OBB 分支的 AABB 前置排除**（2026-10-21）。
    ///
    /// 动因：2026-10-21 游戏日志里 256 长条（560m、带姿态）的 `最深对` 平均 73.8µs，但 54 个窗口
    /// 有 24 个出现 1–2.9ms 尖峰。SAP 只按**扫掠轴**出候选（另外两轴没验），而 OBB 分支原来
    /// 每个候选都要付一次 15 轴 SAT（台架 340.8ns/对）⇒ 候选多的时候就是毫秒级。
    ///
    /// 判据（本文件）：
    /// 1. **逐位同**：与"不做前置排除、每对直接 SAT"的 oracle 对比，最深对/接触集合必须一致；
    /// 2. **真的在挡**：开启子段计数后，`拒 + 进SAT == SAP 候选数`，且"拒"占绝大多数；
    /// 3. 台架读数（时间只进日志，不进 PASS/FAIL）。
    /// </summary>
    public class CollisionObbPreRejectTests
    {
        private readonly ITestOutputHelper _out;
        public CollisionObbPreRejectTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void DeepestPair_Obb_MatchesUnrejectedSatOracle_OnRandomPoses()
        {
            int compared = 0, hits = 0;
            for (int seed = 1; seed <= 200; seed++)
            {
                var rng = new Random(seed);
                var a = RandomObbList(rng, 6, spread: 6.0);
                var b = RandomObbList(rng, 6, spread: 6.0);
                if (!TryClip(a, b, out DVector3 clipMin, out DVector3 clipMax)) continue;

                bool fast = CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax,
                    out DVector3 nFast, out double dFast, out DVector3 cFast);
                bool oracle = SatOracle(a, b, clipMin, clipMax,
                    out DVector3 nOra, out double dOra, out DVector3 cOra);

                Assert.Equal(oracle, fast);
                compared++;
                if (!oracle) continue;
                hits++;
                Assert.Equal(dOra, dFast, 12);
                Assert.Equal(nOra.x, nFast.x, 12);
                Assert.Equal(nOra.y, nFast.y, 12);
                Assert.Equal(nOra.z, nFast.z, 12);
                Assert.Equal(cOra.x, cFast.x, 12);
                Assert.Equal(cOra.y, cFast.y, 12);
                Assert.Equal(cOra.z, cFast.z, 12);
            }
            Assert.True(compared > 150, $"覆盖不足：只比较了 {compared} 组");
            Assert.True(hits > 30, $"命中样本不足：{hits} 组（oracle 全程未命中说明几何造错了）");
            _out.WriteLine($"[OBB 前置排除·对拍] 组数={compared} 命中={hits}");
        }

        [Fact]
        public void CollectContacts_Obb_MatchesUnrejectedSatOracle_OnRandomPoses()
        {
            int compared = 0;
            for (int seed = 1; seed <= 120; seed++)
            {
                var rng = new Random(1000 + seed);
                var a = RandomObbList(rng, 5, spread: 5.0);
                var b = RandomObbList(rng, 5, spread: 5.0);
                if (!TryClip(a, b, out DVector3 clipMin, out DVector3 clipMax)) continue;

                var got = new List<CollisionContactPoint>();
                CombatManager.CollectPartContacts(a, b, clipMin, clipMax, got);

                var depths = new List<double>();
                foreach (var aBox in a)
                {
                    if (!OverlapsClip(aBox, clipMin, clipMax)) continue;
                    foreach (var bBox in b)
                    {
                        if (!OverlapsClip(bBox, clipMin, clipMax)) continue;
                        if (!PartGeometry.ObbObbSat(aBox.Center, aBox.Half, aBox.Rotation,
                                bBox.Center, bBox.Half, bBox.Rotation,
                                out _, out double depth, out _))
                            continue;
                        if (depth > 0.0) depths.Add(depth);
                    }
                }
                depths.Sort();
                depths.Reverse();
                int expect = Math.Min(8, depths.Count);

                Assert.Equal(expect, got.Count);
                var gotDepths = got.Select(c => c.Penetration).OrderByDescending(d => d).ToList();
                for (int i = 0; i < expect; i++)
                    Assert.Equal(depths[i], gotDepths[i], 9);
                compared++;
            }
            Assert.True(compared > 100, $"覆盖不足：只比较了 {compared} 组");
            _out.WriteLine($"[OBB 前置排除·接触对拍] 组数={compared}");
        }

        /// <summary>前置排除必须真的在挡：开子段计数后，"拒 + 进 SAT" 应恰好等于 SAP 候选数。</summary>
        [Fact]
        public void ObbPreReject_Fires_And_AccountsForEverySapCandidate()
        {
            var (a, b, clipMin, clipMax) = BuildRotatedRodPair(angleDeg: 45.0, gap: 60.0);
            Assert.True((long)a.Count * b.Count > 512, "本用例必须走 SAP 路径");

            bool wasEnabled = PerfProbe.CollisionStagesEnabled;
            try
            {
                PerfProbe.CollisionStagesEnabled = true;
                PerfProbe.ResetCollisionStages();

                CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax,
                    out _, out _, out _);

                long rejects = PerfProbe.ColObbAabbRejects;
                long sats = PerfProbe.ColObbSatCalls;
                long candidates = PerfProbe.ColSapPairs;

                Assert.True(candidates > 0, "SAP 没有产出候选（几何造错了？）");
                Assert.Equal(candidates, rejects + sats);
                Assert.True(rejects > 0, "前置排除一次都没触发");
                Assert.True(rejects > sats, $"前置排除应挡掉绝大多数候选：拒={rejects} 进SAT={sats}");
                _out.WriteLine($"[OBB 前置排除·计数] 候选={candidates} 拒={rejects} 进SAT={sats} " +
                               $"（省 {100.0 * rejects / candidates:F1}%）");
            }
            finally
            {
                PerfProbe.ResetCollisionStages();
                PerfProbe.CollisionStagesEnabled = wasEnabled;
            }
        }

        /// <summary>
        /// 台架：256×256 带姿态长条在不同相对转角下的 SAP 候选数与耗时（时间只进日志）。
        /// 转角决定"扫掠轴上的区间重叠数" ⇒ 候选数摆动 ⇒ 这就是游戏内尖峰间歇出现的来源。
        /// </summary>
        [Fact]
        public void SapCandidates_256RotatedRods_SwingWithAngle()
        {
            // 先量 OBB-SAT 单价（旧路径每个 SAP 候选都要付一次），用于把"候选数"换算成旧路径成本。
            double satNs = MeasureObbSatNs();
            _out.WriteLine($"[SAP 台架] OBB-SAT={satNs:F1}ns/对（旧路径 = 候选数 × 此值）");

            int maxCandidates = 0;
            double maxMs = 0.0;
            foreach (double angle in new[] { 0.0, 15.0, 30.0, 45.0, 60.0, 75.0, 90.0 })
            {
                var (a, b, clipMin, clipMax) = BuildRotatedRodPair(angle, gap: 20.0);
                if (clipMin.x >= clipMax.x || clipMin.y >= clipMax.y || clipMin.z >= clipMax.z)
                {
                    _out.WriteLine($"[SAP 台架] 转角={angle,5:F1}° 裁剪盒为空（无候选）");
                    continue;
                }

                bool wasEnabled = PerfProbe.CollisionStagesEnabled;
                PerfProbe.CollisionStagesEnabled = true;
                int candidates;
                double medianMs;
                try
                {
                    // 预热（含 JIT / tier 升级），再取**中位**：单次读数会被 tier-1 重编译和 GC 污染。
                    for (int w = 0; w < 8; w++)
                        CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax, out _, out _, out _);
                    PerfProbe.ResetCollisionStages();
                    var samples = new List<double>(32);
                    for (int it = 0; it < 32; it++)
                    {
                        var sw = Stopwatch.StartNew();
                        CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax, out _, out _, out _);
                        sw.Stop();
                        samples.Add(sw.ElapsedTicks * 1000.0 / Stopwatch.Frequency);
                    }
                    candidates = PerfProbe.ColSapPairsMax;
                    samples.Sort();
                    medianMs = samples[samples.Count / 2];
                }
                finally
                {
                    PerfProbe.ResetCollisionStages();
                    PerfProbe.CollisionStagesEnabled = wasEnabled;
                }

                maxCandidates = Math.Max(maxCandidates, candidates);
                maxMs = Math.Max(maxMs, medianMs);

                // 参照：同一几何走**嵌套穷举**（65,536 对，每对一次 AABB 前置排除）。
                double nestedMs = MedianMs(() => CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax,
                    out _, out _, out _, forceNested: true));
                _out.WriteLine($"[SAP 台架] 转角={angle,5:F1}° 候选={candidates,6} " +
                               $"SAP中位={medianMs,7:F3}ms 嵌套参照={nestedMs,7:F3}ms");
            }
            Assert.True(maxCandidates > 0, "所有转角都没有候选（几何造错了）");
            _out.WriteLine($"[SAP 台架] 候选峰值={maxCandidates} 最慢={maxMs:F3}ms " +
                           $"（旧路径按 {satNs:F0}ns/候选估算 ≈ {maxCandidates * satNs / 1_000_000.0:F2}ms）");
        }

        /// <summary>同轮 32 次、取中位（ms）。用于把 SAP 与嵌套参照放在同一口径下比。</summary>
        private static double MedianMs(Action body)
        {
            for (int w = 0; w < 8; w++) body();
            var samples = new List<double>(32);
            for (int it = 0; it < 32; it++)
            {
                var sw = Stopwatch.StartNew();
                body();
                sw.Stop();
                samples.Add(sw.ElapsedTicks * 1000.0 / Stopwatch.Frequency);
            }
            samples.Sort();
            return samples[samples.Count / 2];
        }

        /// <summary>OBB-SAT 单价（Release；同轮多次取中位）。只用于把候选数换算成旧路径成本。</summary>
        private static double MeasureObbSatNs()
        {
            var rng = new Random(7);
            var a = RandomObbList(rng, 64, 3.0);
            var b = RandomObbList(rng, 64, 3.0);
            const int reps = 64;
            var samples = new List<double>(reps);
            for (int r = 0; r < reps; r++)
            {
                int hits = 0;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < a.Count; i++)
                {
                    for (int j = 0; j < b.Count; j++)
                    {
                        if (PartGeometry.ObbObbSat(a[i].Center, a[i].Half, a[i].Rotation,
                                b[j].Center, b[j].Half, b[j].Rotation, out _, out _, out _))
                            hits++;
                    }
                }
                sw.Stop();
                Assert.True(hits >= 0);
                samples.Add(sw.Elapsed.TotalMilliseconds * 1_000_000.0 / (a.Count * b.Count));
            }
            samples.Sort();
            return samples[samples.Count / 2];
        }

        // ── 造几何 ────────────────────────────────────────────────────────────────

        /// <summary>两艘 256 件长条：每件 40×2.2×40 的板，沿 Y 堆 256 层（与游戏内 256 长条同形）。</summary>
        private static (List<CombatManager.PartAabb>, List<CombatManager.PartAabb>, DVector3, DVector3)
            BuildRotatedRodPair(double angleDeg, double gap)
        {
            var rot = DQuaternion.AngleAxis(angleDeg * Math.PI / 180.0, new DVector3(0, 0, 1));
            var a = BuildRod(new DVector3(0, 0, 0), rot);
            var b = BuildRod(new DVector3(gap, 0, 0), rot);
            DVector3 clipMin = DVector3.Max(GroupMin(a), GroupMin(b));
            DVector3 clipMax = DVector3.Min(GroupMax(a), GroupMax(b));
            return (a, b, clipMin, clipMax);
        }

        private static List<CombatManager.PartAabb> BuildRod(DVector3 center, DQuaternion rot)
        {
            const int layers = 256;
            const double step = 2.2;
            var list = new List<CombatManager.PartAabb>(layers);
            double half = (layers - 1) * step * 0.5;
            for (int i = 0; i < layers; i++)
            {
                // 局部偏移必须跟着姿态转：整根长条倾斜，而不是"沿世界 Y 堆一列斜板"。
                var local = DVector3.Rotate(new DVector3(0, -half + i * step, 0), rot);
                list.Add(new CombatManager.PartAabb(center + local, new DVector3(20, 1.1, 20), rot, i));
            }
            return list;
        }

        private static DVector3 GroupMin(List<CombatManager.PartAabb> list)
        {
            DVector3 m = list[0].Min;
            for (int i = 1; i < list.Count; i++) m = DVector3.Min(m, list[i].Min);
            return m;
        }

        private static DVector3 GroupMax(List<CombatManager.PartAabb> list)
        {
            DVector3 m = list[0].Max;
            for (int i = 1; i < list.Count; i++) m = DVector3.Max(m, list[i].Max);
            return m;
        }

        private static List<CombatManager.PartAabb> RandomObbList(Random rng, int n, double spread)
        {
            var list = new List<CombatManager.PartAabb>(n);
            for (int i = 0; i < n; i++)
            {
                var c = new DVector3(
                    (rng.NextDouble() * 2 - 1) * spread,
                    (rng.NextDouble() * 2 - 1) * spread,
                    (rng.NextDouble() * 2 - 1) * spread);
                var h = new DVector3(
                    0.5 + rng.NextDouble() * 3.0,
                    0.5 + rng.NextDouble() * 3.0,
                    0.5 + rng.NextDouble() * 3.0);
                var axis = new DVector3(
                    rng.NextDouble() * 2 - 1,
                    rng.NextDouble() * 2 - 1,
                    rng.NextDouble() * 2 - 1);
                if (axis.sqrMagnitude < 1e-6) axis = new DVector3(0, 1, 0);
                var rot = DQuaternion.AngleAxis(rng.NextDouble() * Math.PI * 2.0, axis.normalized);
                list.Add(new CombatManager.PartAabb(c, h, rot, i));
            }
            return list;
        }

        private static bool TryClip(List<CombatManager.PartAabb> a, List<CombatManager.PartAabb> b,
            out DVector3 clipMin, out DVector3 clipMax)
        {
            clipMin = DVector3.Max(GroupMin(a), GroupMin(b));
            clipMax = DVector3.Min(GroupMax(a), GroupMax(b));
            return clipMin.x < clipMax.x && clipMin.y < clipMax.y && clipMin.z < clipMax.z;
        }

        private static bool OverlapsClip(in CombatManager.PartAabb box, in DVector3 clipMin, in DVector3 clipMax)
            => box.Max.x > clipMin.x && box.Min.x < clipMax.x
            && box.Max.y > clipMin.y && box.Min.y < clipMax.y
            && box.Max.z > clipMin.z && box.Min.z < clipMax.z;

        /// <summary>
        /// oracle：**不做 AABB 前置排除**，每对直接 SAT，选择规则与生产一致（最深；并列取 (Order) 最小）。
        /// 前置排除的正确性判据就是它 —— 被拒的对本来就会被 SAT 判为不相交。
        /// </summary>
        private static bool SatOracle(List<CombatManager.PartAabb> a, List<CombatManager.PartAabb> b,
            DVector3 clipMin, DVector3 clipMax,
            out DVector3 normal, out double depth, out DVector3 contact)
        {
            normal = DVector3.zero;
            depth = 0.0;
            contact = DVector3.zero;
            double best = 0.0;
            int bestA = int.MaxValue, bestB = int.MaxValue;

            foreach (var aBox in a)
            {
                if (!OverlapsClip(aBox, clipMin, clipMax)) continue;
                foreach (var bBox in b)
                {
                    if (!OverlapsClip(bBox, clipMin, clipMax)) continue;
                    if (!PartGeometry.ObbObbSat(aBox.Center, aBox.Half, aBox.Rotation,
                            bBox.Center, bBox.Half, bBox.Rotation,
                            out DVector3 n, out double d, out DVector3 c))
                        continue;
                    if (d < best) continue;
                    if (d == best)
                    {
                        bool earlier = aBox.Order < bestA || (aBox.Order == bestA && bBox.Order < bestB);
                        if (!earlier) continue;
                    }
                    best = d;
                    bestA = aBox.Order;
                    bestB = bBox.Order;
                    normal = n;
                    depth = d;
                    contact = c;
                }
            }
            return best > 0.0;
        }
    }
}
