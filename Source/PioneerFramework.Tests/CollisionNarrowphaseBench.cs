using System;
using System.Collections.Generic;
using System.Diagnostics;
using PioneerFramework;
using PioneerFramework.Combat;
using UnityEngine;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **窄相性能台架**（2026-10-17，用户："我希望算法的性能是无可挑剔的"）。
    ///
    /// 测的是 `TryFindDeepestPartPair` 的**纯核心**（已把姿态变换从内层循环提到收集阶段）：
    /// · 1×1 / 8×8 / 64×64 件，同轮交错、预热后取中位；
    /// · 另量 `GetPartAabbSystem` 的单件变换成本，用于说明旧实现为什么是 O(nA×nB) 次变换。
    ///
    /// 口径纪律：**绝对耗时随测试进程配置变化**（Debug / Release；游戏内 Release 通常快 2–6×）；
    /// 时间只进读数，不进 PASS/FAIL。
    /// </summary>
    public class CollisionNarrowphaseBench
    {
        private readonly ITestOutputHelper _out;
        public CollisionNarrowphaseBench(ITestOutputHelper output) => _out = output;

        [Fact]
        public void Narrowphase_DeepestPair_IsCorrect()
        {
            var a = new List<CombatManager.PartAabb>
            {
                new(new DVector3(-5, -5, -5), new DVector3(5, 5, 5)),
                new(new DVector3(50, 0, 0), new DVector3(60, 10, 10)),
            };
            var b = new List<CombatManager.PartAabb>
            {
                new(new DVector3(3, -3, -3), new DVector3(7, 3, 3)),   // 与 A0 重叠 2m
                new(new DVector3(-1, 0, 0), new DVector3(3, 6, 6)),    // 与 A0 重叠 4m（更深）
            };

            bool hit = CombatManager.TryFindDeepestPartPair(a, b,
                out DVector3 normal, out double depth, out DVector3 contact);

            Assert.True(hit);
            Assert.Equal(4.0, depth, 9);
            Assert.Equal(1.0, normal.x, 9);
            Assert.False(CombatManager.TryFindDeepestPartPair(new List<CombatManager.PartAabb>(),
                new List<CombatManager.PartAabb>(), out _, out _, out _));
        }

        [Fact]
        public void Narrowphase_Cost_Scaling_And_SweepPrune()
        {
            var a1 = MakeBoxes(1, 0);
            var b1 = MakeBoxes(1, 1);
            var a8 = MakeBoxes(8, 0);
            var b8 = MakeBoxes(8, 1);
            var a32 = MakeBoxes(32, 0);
            var b32 = MakeBoxes(32, 1);
            var a64 = MakeBoxes(64, 0);
            var b64 = MakeBoxes(64, 1);
            var a128 = MakeBoxes(128, 0);
            var b128 = MakeBoxes(128, 1);
            var a256 = MakeBoxes(256, 0);
            var b256 = MakeBoxes(256, 1);
            var a512 = MakeBoxes(512, 0);
            var b512 = MakeBoxes(512, 1);
            var a1024 = MakeBoxes(1024, 0);
            var b1024 = MakeBoxes(1024, 1);

            // 预热点（JIT + 分支预测 + ArrayPool 首次租用）
            var hugeMin = new DVector3(-1e300, -1e300, -1e300);
            var hugeMax = new DVector3(1e300, 1e300, 1e300);
            for (int i = 0; i < 1000; i++)
                CombatManager.TryFindDeepestPartPair(a64, b64, hugeMin, hugeMax, out _, out _, out _);
            for (int i = 0; i < 100; i++)
                CombatManager.TryFindDeepestPartPair(a256, b256, hugeMin, hugeMax, out _, out _, out _);
            for (int i = 0; i < 30; i++)
                CombatManager.TryFindDeepestPartPair(a512, b512, hugeMin, hugeMax, out _, out _, out _, forceBvh: true);

            var t1 = Measure(a1, b1, hugeMin, hugeMax, 200_000);
            var t8Nested = Measure(a8, b8, hugeMin, hugeMax, 50_000, forceNested: true);
            var t8Sap = Measure(a8, b8, hugeMin, hugeMax, 50_000, forceSweep: true);
            var t32Nested = Measure(a32, b32, hugeMin, hugeMax, 10_000, forceNested: true);
            var t32Sap = Measure(a32, b32, hugeMin, hugeMax, 10_000, forceSweep: true);
            var t64Nested = Measure(a64, b64, hugeMin, hugeMax, 5_000, forceNested: true);
            var t64Sap = Measure(a64, b64, hugeMin, hugeMax, 5_000, forceSweep: true);
            var t128Nested = Measure(a128, b128, hugeMin, hugeMax, 2_000, forceNested: true);
            var t128Sap = Measure(a128, b128, hugeMin, hugeMax, 2_000, forceSweep: true);
            var t256Nested = Measure(a256, b256, hugeMin, hugeMax, 200, forceNested: true);
            var t256Sap = Measure(a256, b256, hugeMin, hugeMax, 2_000);
            var t512Nested = Measure(a512, b512, hugeMin, hugeMax, 20, forceNested: true);
            var t512Sap = Measure(a512, b512, hugeMin, hugeMax, 500, forceSweep: true);
            var t512Bvh = Measure(a512, b512, hugeMin, hugeMax, 500, forceBvh: true);
            var t1024Sap = Measure(a1024, b1024, hugeMin, hugeMax, 20, forceSweep: true);
            var t1024Bvh = Measure(a1024, b1024, hugeMin, hugeMax, 200, forceBvh: true);

            // 稠密布局（X 区间大量重叠）——SAP 活跃集变大的最坏情形，检验 BVH 是否值得
            var rngDense = new System.Random(4242);
            var aDense = MakeDenseBoxes(rngDense, 512, 0.0);
            var bDense = MakeDenseBoxes(rngDense, 512, 1.0);
            var tDenseSap = Measure(aDense, bDense, hugeMin, hugeMax, 200, forceSweep: true);
            var tDenseBvh = Measure(aDense, bDense, hugeMin, hugeMax, 200, forceBvh: true);

            // 剔除收益：两艘 256 件长船只在端部 50m 重叠（clip 盒内约 6×6 件）
            var aLong = MakeLongShip(256, 0.0);
            var bLong = MakeLongShip(256, 2500.0);
            var clipLong = ClipOf(aLong, bLong);
            var tCornerClip = Measure(aLong, bLong, clipLong.min, clipLong.max, 2_000, forceSweep: true);
            var tCornerNoClip = Measure(aLong, bLong, hugeMin, hugeMax, 500, forceSweep: true);

            _out.WriteLine($"[窄相核心] 1×1     {t1,9:F1} ns/对（默认两两比较）");
            _out.WriteLine($"[窄相核心] 8×8     嵌套 {t8Nested,8:F1} ns / SAP {t8Sap,8:F1} ns");
            _out.WriteLine($"[窄相核心] 32×32   嵌套 {t32Nested,8:F1} ns / SAP {t32Sap,8:F1} ns");
            _out.WriteLine($"[窄相核心] 64×64   嵌套 {t64Nested,8:F1} ns / SAP {t64Sap,8:F1} ns");
            _out.WriteLine($"[窄相核心] 128×128 嵌套 {t128Nested,8:F1} ns / SAP {t128Sap,8:F1} ns");
            _out.WriteLine($"[窄相核心] 256×256 嵌套 {t256Nested,8:F1} ns / SAP {t256Sap,8:F1} ns");
            _out.WriteLine($"[窄相核心] 512×512 嵌套 {t512Nested,8:F1} ns / SAP {t512Sap,8:F1} ns / BVH {t512Bvh,8:F1} ns");
            _out.WriteLine($"[窄相核心] 1024×1024 SAP {t1024Sap,8:F1} ns / BVH {t1024Bvh,8:F1} ns");
            _out.WriteLine($"[稠密] 512×512 SAP {tDenseSap,8:F1} ns / BVH {tDenseBvh,8:F1} ns");
            _out.WriteLine($"[剔除] 长船端部重叠 256×256：clip {tCornerClip:F1} ns / 无 clip {tCornerNoClip:F1} ns");
            _out.WriteLine("口径：本测试进程配置（用 -c Release 跑即为 Release）；时间仅为读数，不进 PASS/FAIL。");
        }

        [Fact]
        public void SweepAndPrune_MatchesNested_OnRandomBoxes()
        {
            var rng = new System.Random(20261017);
            for (int trial = 0; trial < 20; trial++)
            {
                var a = MakeRandomBoxes(rng, 256, 0.0);
                var b = MakeRandomBoxes(rng, 256, 3.0);

                bool okSap = CombatManager.TryFindDeepestPartPair(a, b,
                    out DVector3 nSap, out double dSap, out DVector3 cSap);
                bool okNested = CombatManager.TryFindDeepestPartPair(a, b,
                    out DVector3 nNested, out double dNested, out DVector3 cNested, forceNested: true);

                Assert.Equal(okNested, okSap);
                if (!okNested) continue;
                Assert.Equal(dNested, dSap, 12);
                Assert.Equal(nNested.x, nSap.x, 12);
                Assert.Equal(nNested.y, nSap.y, 12);
                Assert.Equal(nNested.z, nSap.z, 12);
                Assert.Equal(cNested.x, cSap.x, 9);
                Assert.Equal(cNested.y, cSap.y, 9);
                Assert.Equal(cNested.z, cSap.z, 9);
            }
        }

        [Fact]
        public void Bvh_MatchesNested_OnRandomBoxes()
        {
            var rng = new System.Random(20261018);
            for (int trial = 0; trial < 5; trial++)
            {
                var a = MakeRandomBoxes(rng, 512, 0.0);
                var b = MakeRandomBoxes(rng, 512, 3.0);

                bool okBvh = CombatManager.TryFindDeepestPartPair(a, b,
                    out DVector3 nBvh, out double dBvh, out DVector3 cBvh, forceBvh: true);
                bool okNested = CombatManager.TryFindDeepestPartPair(a, b,
                    out DVector3 nNested, out double dNested, out DVector3 cNested, forceNested: true);

                Assert.Equal(okNested, okBvh);
                if (!okNested) continue;
                Assert.Equal(dNested, dBvh, 12);
                Assert.Equal(nNested.x, nBvh.x, 12);
                Assert.Equal(nNested.y, nBvh.y, 12);
                Assert.Equal(nNested.z, nBvh.z, 12);
                Assert.Equal(cNested.x, cBvh.x, 9);
                Assert.Equal(cNested.y, cBvh.y, 9);
                Assert.Equal(cNested.z, cBvh.z, 9);
            }
        }

        [Fact]
        public void PartAabbTransform_CostPerPart()
        {
            var entity = new SpaceEntity(9100, 0);
            var parts = new List<SpacePart>();
            for (int i = 0; i < 64; i++)
            {
                parts.Add(new SpacePart
                {
                    partId = i + 1,
                    defName = "PF_ShipPart_Cube",
                    positionShip = new DVector3((i % 8) * 4, (i / 8) * 4, 0),
                    boundsSizeShip = new DVector3(4, 4, 4),
                    hp = 100f,
                });
            }
            entity.SetParts(parts);
            entity.SetRotation(Quaternion.Euler(20f, 35f, 10f));

            const int iterations = 100_000;
            for (int i = 0; i < 1000; i++)
                entity.GetPartAabbSystem(i % 64, out _, out _);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
                entity.GetPartAabbSystem(i % 64, out _, out _);
            sw.Stop();

            double nsPer = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / iterations;
            _out.WriteLine($"[part AABB 变换] {nsPer:F1} ns/件（64 件旧实现 4096 次 = 约 {nsPer * 4096 / 1000:F0} µs/对）");
            _out.WriteLine("口径：本测试进程配置（用 -c Release 跑即为 Release）；时间仅为读数，不进 PASS/FAIL。");
        }

        private static List<CombatManager.PartAabb> MakeBoxes(int n, int seed)
        {
            var list = new List<CombatManager.PartAabb>(n);
            for (int i = 0; i < n; i++)
            {
                // 两组的盒沿 X 交错排布：一组间距 10m、半宽 1m ⇒ 绝大多数对不重叠，
                // 少量边界重叠，模拟真实船体零件缝。
                double x = seed * 3 + i * 10;
                double y = (i % 8) * 3;
                double z = (i / 8) * 3;
                list.Add(new CombatManager.PartAabb(
                    new DVector3(x - 1, y - 1, z - 1),
                    new DVector3(x + 1, y + 1, z + 1), i));
            }
            return list;
        }

        /// <summary>随机盒：8×8×N 网格 + 抖动 + 3~8m 半宽，保证有大量重叠（用于 SAP 与两两比较的逐位对照）。</summary>
        private static List<CombatManager.PartAabb> MakeRandomBoxes(System.Random rng, int n, double offsetX)
        {
            var list = new List<CombatManager.PartAabb>(n);
            for (int i = 0; i < n; i++)
            {
                double cx = (i % 8) * 8 + rng.NextDouble() * 4 + offsetX;
                double cy = ((i / 8) % 8) * 8 + rng.NextDouble() * 4;
                double cz = (i / 64) * 8 + rng.NextDouble() * 4;
                double hx = 2 + rng.NextDouble() * 2;
                double hy = 2 + rng.NextDouble() * 2;
                double hz = 2 + rng.NextDouble() * 2;
                list.Add(new CombatManager.PartAabb(
                    new DVector3(cx - hx, cy - hy, cz - hz),
                    new DVector3(cx + hx, cy + hy, cz + hz), i));
            }
            return list;
        }

        private static List<CombatManager.PartAabb> MakeLongShip(int n, double xStart)
        {
            var list = new List<CombatManager.PartAabb>(n);
            for (int i = 0; i < n; i++)
            {
                double x = xStart + i * 10.0;
                double y = (i % 8) * 3.0;
                double z = (i / 8) * 3.0;
                list.Add(new CombatManager.PartAabb(
                    new DVector3(x - 1, y - 1, z - 1),
                    new DVector3(x + 1, y + 1, z + 1), i));
            }
            return list;
        }

        /// <summary>两组的组 AABB 交集（窄相生产路径用的裁剪盒）。</summary>
        private static (DVector3 min, DVector3 max) ClipOf(List<CombatManager.PartAabb> a,
            List<CombatManager.PartAabb> b)
        {
            Union(a, out DVector3 aMin, out DVector3 aMax);
            Union(b, out DVector3 bMin, out DVector3 bMax);
            return (DVector3.Max(aMin, bMin), DVector3.Min(aMax, bMax));
        }

        private static void Union(List<CombatManager.PartAabb> boxes, out DVector3 min, out DVector3 max)
        {
            min = boxes[0].Min;
            max = boxes[0].Max;
            for (int i = 1; i < boxes.Count; i++)
            {
                min = DVector3.Min(min, boxes[i].Min);
                max = DVector3.Max(max, boxes[i].Max);
            }
        }

        /// <summary>稠密盒：X 区间几乎全部重叠、Y/Z 网格相邻重叠 ⇒ SAP 活跃集接近满员。</summary>
        private static List<CombatManager.PartAabb> MakeDenseBoxes(System.Random rng, int n, double offsetX)
        {
            var list = new List<CombatManager.PartAabb>(n);
            for (int i = 0; i < n; i++)
            {
                double cx = offsetX + rng.NextDouble() * 3.0;
                double cy = (i % 8) * 5.0 + rng.NextDouble() * 2.0;
                double cz = (i / 8) * 5.0 + rng.NextDouble() * 2.0;
                double h = 3.0 + rng.NextDouble();
                list.Add(new CombatManager.PartAabb(
                    new DVector3(cx - h, cy - h, cz - h),
                    new DVector3(cx + h, cy + h, cz + h), i));
            }
            return list;
        }

        private static double Measure(List<CombatManager.PartAabb> a, List<CombatManager.PartAabb> b,
            DVector3 clipMin, DVector3 clipMax,
            int iterations, bool forceNested = false, bool forceSweep = false, bool forceBvh = false)
        {
            var samples = new double[5];
            for (int s = 0; s < samples.Length; s++)
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < iterations; i++)
                    CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax,
                        out _, out _, out _, forceNested, forceSweep, forceBvh);
                sw.Stop();
                samples[s] = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / iterations;
            }
            Array.Sort(samples);
            return samples[samples.Length / 2]; // 中位
        }
    }
}
