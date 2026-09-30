using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 批7 第 7a 片：**统一调度原语**的契约。
    ///
    /// 判据的核心是**确定性**：走并行路与走串行路必须给出**逐位相同**的结果。
    /// 这一条是本库所有"同场景前后对比"验收的地基（浮点归约不定序会让整套对比失效）。
    ///
    /// ⚠ **`[Collection]` 不是装饰**：xUnit 按 collection 并行跑用例，而 `ParallelOps.ForceSerial`
    /// 是**进程级开关** ⇒ 不加这条，别的用例会在"被强制串行"的状态下断言并行判据（实测红过一次）。
    /// 凡碰 `ForceSerial` / 断言并行行为的用例，**必须同属一个 collection**。
    /// </summary>
    [Collection("ParallelOps")]
    public class ParallelOpsTests
    {
        /// <summary>并行路与串行路**逐位相同**（同样的 body、同样的槽位写入）。</summary>
        [Fact]
        public void For_ParallelAndSerial_AreBitIdentical()
        {
            const int n = 500;
            var serial = new double[n];
            var parallel = new double[n];

            // 便宜 ⇒ 走串行路；贵 ⇒ 走并行路。两次都算同一个函数。
            ParallelOps.For(n, 0.0, i => serial[i] = Step(i));
            ParallelOps.For(n, 1.0, i => parallel[i] = Step(i));

            for (int i = 0; i < n; i++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(serial[i]), BitConverter.DoubleToInt64Bits(parallel[i]));
        }

        private static double Step(int i) => Math.Sin(i * 0.001) * 1e6 + Math.Sqrt(i + 1.0) / 3.0;

        /// <summary>归约是**顺序**的：并列取小下标，与线程数/调度无关（跑很多次都一样）。</summary>
        [Fact]
        public void MinIndex_IsOrderDeterministic_AndTieBreaksToSmallerIndex()
        {
            double[] v = { 5.0, 1.0, 1.0, 1.0, 0.5, 2.0 };
            for (int k = 0; k < 50; k++)
                Assert.Equal(4, ParallelOps.MinIndex(v));

            double[] tie = { 3.0, 3.0, 3.0 };
            Assert.Equal(0, ParallelOps.MinIndex(tie));   // 并列 ⇒ 最小下标

            Assert.Equal(-1, ParallelOps.MinIndex(Array.Empty<double>()));
            Assert.Equal(-1, ParallelOps.MinIndex(new[] { double.NaN, double.NaN }));
            Assert.Equal(1, ParallelOps.MinIndex(new[] { double.NaN, 2.0, 9.0 }));   // NaN 不参与
        }

        /// <summary>判据看**总工作量**：条数少但贵 ⇒ 并行；条数多但便宜 ⇒ 串行。</summary>
        [Fact]
        public void ShouldParallelize_UsesWorkNotJustCount()
        {
            Assert.False(ParallelOps.ShouldParallelize(1, 1.0));           // 一件活
            Assert.False(ParallelOps.ShouldParallelize(1000, 1e-9));       // 条数多但极便宜
            if (Environment.ProcessorCount > 1)
            {
                Assert.True(ParallelOps.ShouldParallelize(12, 5e-4));      // 12 × 0.5 ms = 6 ms ⇒ 值得
                Assert.True(ParallelOps.ShouldParallelize(2, 1e-3));
            }
            Assert.False(ParallelOps.ShouldParallelize(0, 1.0));
        }

        /// <summary>
        /// 并行区标记：**并行体内为真**（可嵌套、可并发）。
        /// ⚠ **不要断言"区外为假"** —— 那是**进程级**计数，别的用例的并行区会让它短暂为真 ⇒ 断言必然脆
        /// （实测红过一次）。生产侧的守卫只依赖"区内为真"这一半，所以这里也只断言这一半。
        /// </summary>
        [Fact]
        public void InParallelRegion_IsTrueInsideParallelWork()
        {
            ParallelOps.For(4, 0.0, i => Assert.False(ParallelOps.InParallelRegion));   // 串行路：不置位（本线程未开区）

            if (Environment.ProcessorCount > 1)
            {
                int seen = 0;
                ParallelOps.For(8, 1e-3, i => { if (ParallelOps.InParallelRegion) System.Threading.Interlocked.Increment(ref seen); });
                Assert.Equal(8, seen);                                                   // 8 个并行体里都应为真
            }
        }
    }
}
