using System;
using PioneerFramework;
using Xunit;
using MechJebLib.Lambert;

namespace PioneerFramework.Pure.Tests
{
    public class GoodingTests
    {
        private const double Mu = 3.986004418e14;

        [Fact]
        public void Solve_HohmannCollinearTransfer_ReturnsFiniteVelocities()
        {
            double r1 = 7_000_000.0;
            double r2 = 42_000_000.0;
            double a = (r1 + r2) * 0.5;
            double tof = Math.PI * Math.Sqrt(a * a * a / Mu);

            var (vi, vf) = Gooding.Solve(
                Mu,
                new DVector3(r1, 0, 0),
                new DVector3(-r2, 0, 0),
                tof,
                TransferGeometry.ShortWay,
                0);

            Assert.False(double.IsNaN(vi.x) || double.IsNaN(vi.y) || double.IsNaN(vi.z));
            Assert.False(double.IsNaN(vf.x) || double.IsNaN(vf.y) || double.IsNaN(vf.z));
            Assert.True(vi.magnitude > 1e-9);
            Assert.True(vf.magnitude > 1e-9);
        }

        [Fact]
        public void Solve_NonPositiveTimeOfFlight_Throws()
        {
            Assert.Throws<ArgumentException>(() => Gooding.Solve(
                Mu,
                new DVector3(7_000_000, 0, 0),
                new DVector3(42_000_000, 0, 0),
                0.0));
        }

        /// <summary>
        /// 第 19 条（2026-09-10 性能优化）回归护栏：级数迭代硬上限不得误杀正常解。
        /// 全几何扫描下要求：①确实进了 TLAMB（否则断言无意义）②一次都不触顶
        /// ③实测最大迭代低于上限（上限过低会把正常解判成不收敛并抛异常）。
        /// </summary>
        [Fact]
        public void TlambSeries_StaysWithinIterationCap_AcrossGeometrySweep()
        {
            long callsBefore = Gooding.DebugTlambCalls;
            long capBefore = Gooding.DebugSeriesCapHits;

            double[] times = { 600.0, 1800.0, 3600.0, 7200.0, 18000.0, 43200.0 }; // 10min ~ 12h
            double[] radii = { 6.8e6, 7.1e6, 1.0e7, 2.5e7, 4.2e7 };
            int solved = 0;
            foreach (double tof in times)
                foreach (double ra in radii)
                    foreach (double rb in radii)
                        foreach (var geom in new[] { TransferGeometry.ShortWay, TransferGeometry.LongWay })
                        {
                            try
                            {
                                var (vi, vf) = Gooding.Solve(Mu, new DVector3(ra, 0, 0), new DVector3(0, rb, 0), tof, geom, 0);
                                Assert.False(double.IsNaN(vi.x + vi.y + vi.z + vf.x + vf.y + vf.z));
                                solved++;
                            }
                            catch (Exception) { /* VLAMB 判定无解属正常输入，不计入 */ }
                        }

            Assert.True(Gooding.DebugTlambCalls > callsBefore, "本测试未真正进入 TLAMB（级数路径），断言无意义");
            Assert.Equal(capBefore, Gooding.DebugSeriesCapHits); // 正常输入一次都不许触顶
            Assert.True(Gooding.DebugMaxSeriesIter < Gooding.MAX_SERIES_ITER,
                $"实测最大迭代 {Gooding.DebugMaxSeriesIter} 已达上限 {Gooding.MAX_SERIES_ITER}：上限过低或级数未收敛");
            Assert.True(solved > 0, "扫描中没有任何成功解，测试无意义");
        }

        /// <summary>
        /// 第 19 条：NaN 传播不得造成死循环（原实现 while (T != TOLD) 在 NaN 下 NaN != NaN 恒真 → 挂死）。
        /// 期望在有限步内以异常结束（调用点已有 try/catch：规划器回落"Lambert 无解"、制导本次跳过）。
        /// ⚠️ 若本测试将来"挂住不返回"，正是该缺陷复现。
        /// </summary>
        [Fact]
        public void Solve_NaNGarbageInput_TerminatesWithException()
        {
            long capBefore = Gooding.DebugSeriesCapHits;
            var ex = Assert.ThrowsAny<Exception>(() => Gooding.Solve(
                Mu,
                new DVector3(7_000_000, 0, 0),
                new DVector3(0, 42_000_000, 0),
                double.NaN,
                TransferGeometry.ShortWay,
                0));
            // 记录是哪条防线拦下的（本测试同时证明"上限分支确实可达"，而非只靠上游校验）
            Assert.True(Gooding.DebugSeriesCapHits > capBefore,
                $"NaN 输入未走到级数上限分支（异常=\"{ex.Message}\"）——说明被上游校验提前拦下，上限分支本条未被覆盖");
        }
    }
}
