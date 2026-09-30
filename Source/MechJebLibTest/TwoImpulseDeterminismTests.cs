using System;
using MechJebLib.Maneuvers;
using MechJebLib.Primitives;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 批7 #1（2026-09-30）：`TwoImpulseTransfer` 的多起点**并行化之后**的确定性判据。
    ///
    /// 判据形态 = **同输入连跑多次，输出逐位相同**。它抓的是"调度相关的不确定性"——
    /// 并行化本身（把 12 个起点分给线程）若引入了任何顺序依赖（共享累加、按完成先后取优），
    /// 这条会红；而它是本库所有"同场景前后对比"验收的地基（浮点归约不定序 ⇒ 整套对比失效）。
    ///
    /// ⚠ 它**不**证明"并行路 == 串行路"：那条由 `Pure.Tests` 的 `ParallelOpsTests` 钉住
    /// （同样的 body、同样的槽位写入 ⇒ 两条路逐位相同）；这里钉的是"真函数跑起来没抖"。
    /// </summary>
    /// ⚠ `[Collection]`：本类会翻 `ParallelOps.ForceSerial`（**进程级开关**）⇒ 必须与同样碰它的用例同 collection
    /// （xUnit 按 collection 并行 ⇒ 不加这条，别的用例会在"被强制串行"的状态下断言并行判据 ✗ 实测红过一次）。
    [Collection("ParallelOps")]
    public class TwoImpulseDeterminismTests
    {
        private readonly Xunit.Abstractions.ITestOutputHelper _out;
        public TwoImpulseDeterminismTests(Xunit.Abstractions.ITestOutputHelper o) => _out = o;

        private const double Mu = 3.986004418e14;

        /// <summary>一组非共面、非圆轨的几何（避免退化 ⇒ 多起点真的都被走到）。</summary>
        private static (V3 r1, V3 v1, V3 r2, V3 v2) Geometry()
        {
            var r1 = new V3(7.0e6, 0, 0);
            var v1 = new V3(0, 7.4e3, 1.2e3);
            var r2 = new V3(0, 1.2e7, 3.0e6);
            var v2 = new V3(-2.0e3, 3.2e3, 2.4e3);
            return (r1, v1, r2, v2);
        }

        private static long[] Bits((V3 dv1, double dt1, V3 dv2, double dt2) m) => new[]
        {
            BitConverter.DoubleToInt64Bits(m.dv1.x), BitConverter.DoubleToInt64Bits(m.dv1.y), BitConverter.DoubleToInt64Bits(m.dv1.z),
            BitConverter.DoubleToInt64Bits(m.dt1),
            BitConverter.DoubleToInt64Bits(m.dv2.x), BitConverter.DoubleToInt64Bits(m.dv2.y), BitConverter.DoubleToInt64Bits(m.dv2.z),
            BitConverter.DoubleToInt64Bits(m.dt2)
        };

        [Fact]
        public void NextManeuver_IsBitIdenticalAcrossRuns()
        {
            (V3 r1, V3 v1, V3 r2, V3 v2) = Geometry();

            long[] first = Bits(TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2));
            for (int rep = 1; rep < 5; rep++)
            {
                long[] again = Bits(TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2));
                Assert.Equal(first, again);
            }
        }

        /// <summary>
        /// **并行路 vs 串行路逐位相同**（用 `ParallelOps.ForceSerial` 做单变量对照），
        /// 顺带把两条路的耗时打进测试输出（加速比的现场读数）。
        /// </summary>
        [Fact]
        public void NextManeuver_ParallelAndSerial_AreBitIdentical()
        {
            (V3 r1, V3 v1, V3 r2, V3 v2) = Geometry();

            try
            {
                ParallelOps.ForceSerial = false;
                long[] par = Bits(TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2));
                double msPar = TimeMs();

                ParallelOps.ForceSerial = true;
                long[] ser = Bits(TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2));
                double msSer = TimeMs();

                Assert.Equal(ser, par);
                // 只打读数、不断言快慢（机器不同、负载不同 ⇒ 断言时间必然脆）
                _out.WriteLine($"[批7 #1 对照] {Environment.ProcessorCount} 核：并行 {msPar:F2} ms / 串行 {msSer:F2} ms（比值 {msSer / msPar:F2}×）");
            }
            finally
            {
                ParallelOps.ForceSerial = false;
            }
        }

        private static double TimeMs()
        {
            (V3 r1, V3 v1, V3 r2, V3 v2) = Geometry();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2);
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }
    }
}
