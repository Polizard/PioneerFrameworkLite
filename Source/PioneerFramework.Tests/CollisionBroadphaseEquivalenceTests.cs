using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework;
using PioneerFramework.Combat;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 碰撞宽相对拍（2026-10-19）：有序双指针/SAP 的结果必须与嵌套基线一致。
    /// 台架只验证“同结果”，不改验收场景。
    /// </summary>
    public class CollisionBroadphaseEquivalenceTests
    {
        [Fact]
        public void DeepestPair_SortedSweep_MatchesNested()
        {
            var (a, b, clipMin, clipMax) = BuildStack(120);

            Assert.True(CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax,
                out DVector3 nFast, out double dFast, out DVector3 cFast));
            Assert.True(CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax,
                out DVector3 nSlow, out double dSlow, out DVector3 cSlow, forceNested: true));

            Assert.Equal(dSlow, dFast, 12);
            AssertNear(cSlow, cFast);
            AssertNear(nSlow, nFast);
        }

        [Fact]
        public void DeepestPair_UnsortedFallback_MatchesNested()
        {
            var (a, b, clipMin, clipMax) = BuildStack(120);
            Shuffle(a, 0x5EED);
            Shuffle(b, 0xBEEF);

            Assert.True(CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax,
                out DVector3 nFast, out double dFast, out DVector3 cFast));
            Assert.True(CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax,
                out DVector3 nSlow, out double dSlow, out DVector3 cSlow, forceNested: true));

            Assert.Equal(dSlow, dFast, 12);
            AssertNear(cSlow, cFast);
            AssertNear(nSlow, nFast);
        }

        [Fact]
        public void CollectContacts_SortedSweep_MatchesNested()
        {
            var (a, b, clipMin, clipMax) = BuildStack(120);
            var fast = new List<CollisionContactPoint>();
            var slow = new List<CollisionContactPoint>();

            CombatManager.CollectPartContacts(a, b, clipMin, clipMax, fast, 8);
            CombatManager.CollectPartContacts(a, b, clipMin, clipMax, slow, 8, null, null, forceNested: true);

            Assert.Equal(slow.Count, fast.Count);
            // 两者都保留最深 8 个；枚举顺序不同，按深度配对。
            foreach (var s in slow)
            {
                var match = fast.FirstOrDefault(f => Math.Abs(f.Penetration - s.Penetration) < 1e-9);
                Assert.True(match.Normal.sqrMagnitude > 0.5, $"未找到深度 {s.Penetration} 的对应接触");
                AssertNear(s.Point, match.Point);
                AssertNear(s.Normal, match.Normal);
            }
        }

        [Fact]
        public void CollectContacts_UnsortedFallback_MatchesNested()
        {
            var (a, b, clipMin, clipMax) = BuildStack(120);
            Shuffle(a, 0x1234);
            Shuffle(b, 0x4321);
            var fast = new List<CollisionContactPoint>();
            var slow = new List<CollisionContactPoint>();

            CombatManager.CollectPartContacts(a, b, clipMin, clipMax, fast, 8);
            CombatManager.CollectPartContacts(a, b, clipMin, clipMax, slow, 8, null, null, forceNested: true);

            Assert.Equal(slow.Count, fast.Count);
            foreach (var s in slow)
            {
                var match = fast.FirstOrDefault(f => Math.Abs(f.Penetration - s.Penetration) < 1e-9);
                Assert.True(match.Normal.sqrMagnitude > 0.5, $"未找到深度 {s.Penetration} 的对应接触");
                AssertNear(s.Point, match.Point);
                AssertNear(s.Normal, match.Normal);
            }
        }

        [Fact]
        public void SegmentHitsSphere_CrossingThroughOrigin_IsHit()
        {
            Assert.True(CombatManager.SegmentHitsSphere(
                new DVector3(800, 0, 0), new DVector3(-19200, 0, 0), 200.0));
        }

        [Fact]
        public void SegmentHitsSphere_PassesWithinRadius_IsHit()
        {
            Assert.True(CombatManager.SegmentHitsSphere(
                new DVector3(1000, 5, 0), new DVector3(-1000, 5, 0), 100.0));
        }

        [Fact]
        public void SegmentHitsSphere_PassesOutsideRadius_IsMiss()
        {
            Assert.False(CombatManager.SegmentHitsSphere(
                new DVector3(1000, 50, 0), new DVector3(-1000, 50, 0), 100.0));
        }

        [Fact]
        public void SegmentHitsSphere_StationaryOutside_IsMiss()
        {
            Assert.False(CombatManager.SegmentHitsSphere(
                new DVector3(1000, 0, 0), new DVector3(1000, 0, 0), 100.0));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DeepestPair_RandomMonotonic_MatchesNested(bool descending)
        {
            var (a, b) = BuildRandomMonotonic(200, 0xC0FFEE, descending);
            var clipMin = new DVector3(-1e6, -1e6, -1e6);
            var clipMax = new DVector3(1e6, 1e6, 1e6);

            Assert.True(CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax,
                out DVector3 nFast, out double dFast, out DVector3 cFast));
            Assert.True(CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax,
                out DVector3 nSlow, out double dSlow, out DVector3 cSlow, forceNested: true));

            Assert.Equal(dSlow, dFast, 12);
            AssertNear(cSlow, cFast);
            AssertNear(nSlow, nFast);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CollectContacts_RandomMonotonic_MatchesNested(bool descending)
        {
            var (a, b) = BuildRandomMonotonic(200, 0xBADF00D, descending);
            var clipMin = new DVector3(-1e6, -1e6, -1e6);
            var clipMax = new DVector3(1e6, 1e6, 1e6);
            var fast = new List<CollisionContactPoint>();
            var slow = new List<CollisionContactPoint>();

            CombatManager.CollectPartContacts(a, b, clipMin, clipMax, fast, 8);
            CombatManager.CollectPartContacts(a, b, clipMin, clipMax, slow, 8, null, null, forceNested: true);

            Assert.Equal(slow.Count, fast.Count);
            foreach (var s in slow)
            {
                var match = fast.FirstOrDefault(f => Math.Abs(f.Penetration - s.Penetration) < 1e-9);
                Assert.True(match.Normal.sqrMagnitude > 0.5, $"未找到深度 {s.Penetration} 的对应接触");
                AssertNear(s.Point, match.Point);
                AssertNear(s.Normal, match.Normal);
            }
        }

        /// <summary>随机但沿 Y 单调的零件表（长度/间隔/横向尺寸都变化），用于验证双指针不漏对。</summary>
        private static (List<CombatManager.PartAabb> a, List<CombatManager.PartAabb> b)
            BuildRandomMonotonic(int count, int seed, bool descending)
        {
            var rng = new Random(seed);
            var a = new List<CombatManager.PartAabb>(count);
            var b = new List<CombatManager.PartAabb>(count);
            double ya = 0, yb = 0;
            for (int i = 0; i < count; i++)
            {
                ya += rng.NextDouble() * 3.0;
                yb += rng.NextDouble() * 3.0;
                double ha = 0.5 + rng.NextDouble() * 3.0;
                double hb = 0.5 + rng.NextDouble() * 3.0;
                double hxa = 10 + rng.NextDouble() * 20;
                double hxb = 10 + rng.NextDouble() * 20;
                a.Add(new CombatManager.PartAabb(
                    new DVector3(-hxa, ya - ha, -30), new DVector3(hxa, ya + ha, 30), i));
                b.Add(new CombatManager.PartAabb(
                    new DVector3(-hxb + 1 + i % 7, yb - hb, -30),
                    new DVector3(hxb + 1 + i % 7, yb + hb, 30), i));
            }
            if (descending)
            {
                a.Reverse();
                b.Reverse();
            }
            return (a, b);
        }

        /// <summary>120 件沿 Y 堆叠（降序收集）；每对深度不同，避免“同深度保留哪个”的枚举顺序歧义。</summary>
        private static (List<CombatManager.PartAabb> a, List<CombatManager.PartAabb> b,
            DVector3 clipMin, DVector3 clipMax) BuildStack(int count)
        {
            var a = new List<CombatManager.PartAabb>(count);
            var b = new List<CombatManager.PartAabb>(count);
            for (int i = 0; i < count; i++)
            {
                double y = -i * 2.0;
                // X 重叠 = 0.5 + 0.005i（小于 Y/Z 重叠 1.8）⇒ 每对深度唯一且逐渐加深。
                a.Add(new CombatManager.PartAabb(
                    new DVector3(-20, y - 0.9, -20), new DVector3(20, y + 0.9, 20), i));
                b.Add(new CombatManager.PartAabb(
                    new DVector3(19.5 - i * 0.005, y - 0.9, -20), new DVector3(100, y + 0.9, 20), i));
            }

            DVector3 clipMin = a[0].Min, clipMax = a[0].Max;
            for (int i = 1; i < count; i++)
            {
                clipMin = DVector3.Min(clipMin, a[i].Min);
                clipMax = DVector3.Max(clipMax, a[i].Max);
            }
            return (a, b, clipMin, clipMax);
        }

        private static void Shuffle(List<CombatManager.PartAabb> list, int seed)
        {
            var rng = new Random(seed);
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private static void AssertNear(DVector3 expected, DVector3 actual)
        {
            Assert.Equal(expected.x, actual.x, 9);
            Assert.Equal(expected.y, actual.y, 9);
            Assert.Equal(expected.z, actual.z, 9);
        }
    }
}
