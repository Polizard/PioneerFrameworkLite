using System;
using MechJebLib.Primitives;
using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 批7 #5（2026-09-30）：`RendezvousCore.PlanRecovery` 的 **360 格 Lambert 网格并行化**之后的判据。
    ///
    /// 判据 = **并行路与串行路给出逐位相同的计划**（`ParallelOps.ForceSerial` 做单变量对照）。
    /// 两路的归约同语义：串行 `if (!Valid || total &lt; TotalDv)`（严格 `&lt;`、按 (wi,ti) 先到先得）
    /// ≡ `ParallelOps.MinIndex`（跳过 NaN、并列取小下标），且格子的枚举序未变。
    ///
    /// ⚠ 只打耗时读数、**不断言快慢**（机器/负载不同必然脆）。
    ///
    /// ⚠ `[Collection]`：本类会翻 `ParallelOps.ForceSerial`（进程级开关）⇒ 必须与同样碰它的用例同 collection
    /// （否则别人的并行判据会在"被强制串行"的状态下断言 ⇒ 实测红过一次）。
    /// </summary>
    [Collection("ParallelOps")]
    public class RendezvousCoreParallelTests
    {
        private readonly ITestOutputHelper _out;
        public RendezvousCoreParallelTests(ITestOutputHelper o) => _out = o;

        private const double Mu = 3.986004418e14;

        /// <summary>确定性目标预言：圆轨解析（纯函数，随时刻唯一）——判据需要它可复现。</summary>
        private static Func<double, (DVector3 pos, DVector3 vel)> Target(double r2, double omega)
            => t => (new DVector3(r2 * Math.Cos(omega * t), 0.0, r2 * Math.Sin(omega * t)),
                     new DVector3(-r2 * omega * Math.Sin(omega * t), 0.0, r2 * omega * Math.Cos(omega * t)));

        private static long[] Bits(RendezvousCore.RecoveryPlan p) => new[]
        {
            p.Valid ? 1L : 0L,
            BitConverter.DoubleToInt64Bits(p.WaitSeconds),
            BitConverter.DoubleToInt64Bits(p.TransferSeconds),
            BitConverter.DoubleToInt64Bits(p.DepartureDv.x),
            BitConverter.DoubleToInt64Bits(p.DepartureDv.y),
            BitConverter.DoubleToInt64Bits(p.DepartureDv.z),
            BitConverter.DoubleToInt64Bits(p.ArrivalDv),
            BitConverter.DoubleToInt64Bits(p.TotalDv)
        };

        private static RendezvousCore.RecoveryPlan Run()
        {
            var r1 = new DVector3(7.0e6, 0, 0);
            var v1 = new DVector3(0, 0, Math.Sqrt(Mu / 7.0e6));
            double omega = Math.Sqrt(Mu / (1.2e7 * 1.2e7 * 1.2e7));
            return RendezvousCore.PlanRecovery(r1, v1, 0.0, Target(1.2e7, omega),
                Mu, 6.371e6, 7.0e6, prograde: true, maxDepartureDv: 500.0);
        }

        [Fact]
        public void PlanRecovery_ParallelAndSerial_AreBitIdentical()
        {
            try
            {
                ParallelOps.ForceSerial = false;
                long[] par = Bits(Run());

                ParallelOps.ForceSerial = true;
                long[] ser = Bits(Run());

                Assert.Equal(ser, par);                       // 两路逐位相同
                Assert.Equal(par, Bits(Run()));               // 同路连跑也逐位稳定
            }
            finally
            {
                ParallelOps.ForceSerial = false;
            }
        }

        [Fact]
        public void PlanRecovery_TimingReading()
        {
            double msSerial, msParallel;
            try
            {
                ParallelOps.ForceSerial = true;
                Run();
                msSerial = TimeMs(10);

                ParallelOps.ForceSerial = false;
                Run();
                msParallel = TimeMs(10);
            }
            finally
            {
                ParallelOps.ForceSerial = false;
            }

            _out.WriteLine($"[批7 #5 对照] {Environment.ProcessorCount} 核 · 单次 360 格恢复扫描：串行 {msSerial:F3} ms / 并行 {msParallel:F3} ms（比值 {msSerial / Math.Max(msParallel, 1e-9):F2}×）");
        }

        private static double TimeMs(int reps)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < reps; i++) Run();
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds / reps;
        }
    }
}
