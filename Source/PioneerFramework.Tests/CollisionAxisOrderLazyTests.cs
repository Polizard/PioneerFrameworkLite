using System;
using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Combat;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **轴序惰性化**（2026-10-21）。`EnsurePartBoxes` 每个 epoch 都重建 part 盒，原来在重建里
    /// 无条件跑 3 次排序（256 件 = 6 个数组 / 18 KB 垃圾 / 步），而轴序只有 CCD 的
    /// `PartsOverlapCore` / `SweptAxisEarlyOut` 用 —— 游戏日志里 `part盒重组` 平均 40 µs、
    /// 偶发 1.5–2.1 ms（GC 落在重建里）。现在重建只置 null，谁要用谁补建。
    ///
    /// 本文件钉的是**惰性之后仍然正确**：
    /// 1. 重建后轴序必须为空（不偷偷排序）；
    /// 2. `EnsureAxisOrders()` 建出来的下标是**按该轴 min 单调**的排列（与 relParts 几何一致）；
    /// 3. 几何变了再失效重建，顺序必须跟着变（不能拿旧序）。
    /// </summary>
    public class CollisionAxisOrderLazyTests
    {
        private static CombatManager.GroupCollisionCache MakeCache(params (double x, double y, double z)[] mins)
        {
            var c = new CombatManager.GroupCollisionCache();
            for (int i = 0; i < mins.Length; i++)
            {
                var min = new DVector3(mins[i].x, mins[i].y, mins[i].z);
                c.relParts.Add(new CombatManager.PartRelGeom
                {
                    EntityPosition = DVector3.zero,
                    Identity = true,
                    RelMin = min,
                    RelMax = min + new DVector3(2, 2, 2),
                    Half = new DVector3(1, 1, 1),
                    ProjectedHalf = new DVector3(1, 1, 1),
                    Order = i,
                    Shape = InertiaTensor.Shape.Box,
                });
            }
            return c;
        }

        private static void AssertSortedByMin(CombatManager.GroupCollisionCache c, int[] order, int axis)
        {
            Assert.Equal(c.relParts.Count, order.Length);
            var seen = new bool[order.Length];
            double prev = double.NegativeInfinity;
            foreach (int idx in order)
            {
                Assert.InRange(idx, 0, order.Length - 1);
                Assert.False(seen[idx], $"下标 {idx} 在轴序里重复出现");
                seen[idx] = true;
                var r = c.relParts[idx];
                double key = axis == 0 ? r.RelMin.x : axis == 1 ? r.RelMin.y : r.RelMin.z;
                Assert.True(key >= prev - 1e-12, $"轴 {axis} 未按 min 单调：{key} < {prev}");
                prev = key;
            }
            Assert.DoesNotContain(false, seen);
        }

        [Fact]
        public void PartRebuild_LeavesAxisOrdersLazy()
        {
            var c = MakeCache((3, 0, 0), (1, 0, 0), (2, 0, 0));
            c.InvalidateAxisOrders();

            Assert.Null(c.orderX);
            Assert.Null(c.orderY);
            Assert.Null(c.orderZ);

            c.EnsureAxisOrders();

            Assert.NotNull(c.orderX);
            Assert.NotNull(c.orderY);
            Assert.NotNull(c.orderZ);
            AssertSortedByMin(c, c.orderX!, 0);
            AssertSortedByMin(c, c.orderY!, 1);
            AssertSortedByMin(c, c.orderZ!, 2);
        }

        [Fact]
        public void EnsureAxisOrders_FollowsGeometryChange_AfterInvalidate()
        {
            var c = MakeCache((3, 0, 0), (1, 0, 0), (2, 0, 0));
            c.EnsureAxisOrders();
            int firstBefore = c.orderX![0];
            Assert.Equal(1, firstBefore);   // x=1 的那件排第一

            // 几何反转：x=1 的件挪到 x=9，x=3 的件挪到 x=0。
            var a = c.relParts[0];
            a.RelMin = new DVector3(0, 0, 0);
            a.RelMax = new DVector3(2, 2, 2);
            c.relParts[0] = a;
            var b = c.relParts[1];
            b.RelMin = new DVector3(9, 0, 0);
            b.RelMax = new DVector3(11, 2, 2);
            c.relParts[1] = b;

            c.InvalidateAxisOrders();
            Assert.Null(c.orderX);
            c.EnsureAxisOrders();

            Assert.Equal(0, c.orderX![0]);
            AssertSortedByMin(c, c.orderX!, 0);
        }

        /// <summary>惰性化后 CCD 的轴序仍必须与"每次全量重建"逐位一致（同几何、两种时机）。</summary>
        [Fact]
        public void LazyAxisOrders_MatchEagerRebuild_Bitwise()
        {
            var rng = new Random(20261021);
            for (int seed = 0; seed < 40; seed++)
            {
                int n = 8 + rng.Next(40);
                var c = new CombatManager.GroupCollisionCache();
                for (int i = 0; i < n; i++)
                {
                    var min = new DVector3(
                        rng.NextDouble() * 200 - 100,
                        rng.NextDouble() * 200 - 100,
                        rng.NextDouble() * 200 - 100);
                    c.relParts.Add(new CombatManager.PartRelGeom
                    {
                        EntityPosition = new DVector3(rng.NextDouble() * 10, 0, 0),
                        Identity = rng.Next(2) == 0,
                        RelMin = min,
                        RelMax = min + new DVector3(1, 1, 1),
                        CenterOffset = min + new DVector3(0.5, 0.5, 0.5),
                        Half = new DVector3(0.5, 0.5, 0.5),
                        ProjectedHalf = new DVector3(0.5, 0.5, 0.5),
                        Order = i,
                        Shape = InertiaTensor.Shape.Box,
                    });
                }

                c.InvalidateAxisOrders();
                c.EnsureAxisOrders();
                int[] lazyX = (int[])c.orderX!.Clone();
                int[] lazyY = (int[])c.orderY!.Clone();
                int[] lazyZ = (int[])c.orderZ!.Clone();

                // 强制"急切"重建（同一份几何再算一次）作为对照。
                c.RebuildAxisOrders();
                Assert.Equal(lazyX, c.orderX);
                Assert.Equal(lazyY, c.orderY);
                Assert.Equal(lazyZ, c.orderZ);
            }
        }
    }
}
