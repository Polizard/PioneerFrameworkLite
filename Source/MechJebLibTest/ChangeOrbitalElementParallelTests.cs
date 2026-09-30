using System;
using MechJebLib.Maneuvers;
using MechJebLib.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 批7 #4（2026-09-30）：`ChangeOrbitalElement` 的 **θ 粗扫并行化**之后的判据。
    ///
    /// 判据 = **并行路与串行路逐位相同**（用 `ParallelOps.ForceSerial` 做单变量对照）。
    /// 两路的归约是同一语义：串行 `if (!NaN(rr) && rr &lt; bestR)`（严格 `&lt;` ⇒ 保留小下标）
    /// ≡ `ParallelOps.MinIndex`（跳过 NaN、并列取小下标）。
    ///
    /// ⚠ 它**不**断言快慢（机器/负载不同必然脆），只在输出里打读数。
    /// </summary>
    [Collection("ParallelOps")]
    public class ChangeOrbitalElementParallelTests
    {
        private readonly ITestOutputHelper _out;
        public ChangeOrbitalElementParallelTests(ITestOutputHelper o) => _out = o;

        private const double Mu = 3.986004418e14;

        /// <summary>非退化几何：椭圆、非赤道、烧点不在拱点上（让 180 个方向真的有不同的可行半径）。</summary>
        private static (V3 r, V3 v) State()
        {
            var r = new V3(7.1e6, 1.2e6, 8.0e5);
            var v = new V3(-1.5e3, 7.0e3, 1.1e3);
            return (r, v);
        }

        private static long[] Bits(V3 x) => new[]
        {
            BitConverter.DoubleToInt64Bits(x.x), BitConverter.DoubleToInt64Bits(x.y), BitConverter.DoubleToInt64Bits(x.z)
        };

        [Fact]
        public void ThetaScan_ParallelAndSerial_AreBitIdentical()
        {
            (V3 r, V3 v) = State();
            const double peR = 6.9e6;

            try
            {
                ParallelOps.ForceSerial = false;
                long[] par = Bits(ChangeOrbitalElement.ChangePeriapsis(Mu, r, v, peR, optguard: true));

                ParallelOps.ForceSerial = true;
                long[] ser = Bits(ChangeOrbitalElement.ChangePeriapsis(Mu, r, v, peR, optguard: true));

                Assert.Equal(ser, par);

                // 同一条路连跑也要求逐位稳定（抓任何调度相关的不确定性）
                long[] par2 = Bits(ChangeOrbitalElement.ChangePeriapsis(Mu, r, v, peR, optguard: true));
                Assert.Equal(par, par2);
            }
            finally
            {
                ParallelOps.ForceSerial = false;
            }
        }

        [Fact]
        public void ThetaScan_TimingReading()
        {
            (V3 r, V3 v) = State();
            const double peR = 6.9e6;

            double msSerial, msParallel;
            try
            {
                ParallelOps.ForceSerial = true;
                ChangePeriapsis(Mu, r, v, peR);   // 预热
                msSerial = TimeMs(Mu, r, v, peR, 20);

                ParallelOps.ForceSerial = false;
                ChangePeriapsis(Mu, r, v, peR);
                msParallel = TimeMs(Mu, r, v, peR, 20);
            }
            finally
            {
                ParallelOps.ForceSerial = false;
            }

            _out.WriteLine($"[批7 #4 对照] {Environment.ProcessorCount} 核 · 单次 θ 粗扫+细化：串行 {msSerial:F3} ms / 并行 {msParallel:F3} ms（比值 {msSerial / Math.Max(msParallel, 1e-9):F2}×）");
        }

        private static void ChangePeriapsis(double mu, V3 r, V3 v, double peR) =>
            ChangeOrbitalElement.ChangePeriapsis(mu, r, v, peR);

        private static double TimeMs(double mu, V3 r, V3 v, double peR, int reps)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < reps; i++) ChangePeriapsis(mu, r, v, peR);
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds / reps;
        }
    }
}
