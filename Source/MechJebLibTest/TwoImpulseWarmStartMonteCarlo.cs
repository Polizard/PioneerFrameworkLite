using System;
using System.Diagnostics;
using System.Text;
using MechJebLib.Maneuvers;
using MechJebLib.Primitives;
using MechJebLib.TwoBody;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **②「warm start」的蒙特卡洛压力台架**（2026-09-30，用户要求：先造仪器、再看值不值得做）。
    ///
    /// ## 被问的问题
    /// 把"上一次成功解出来的转移时长 `dt*`"当**首猜**，能不能把外层那 ≈4.4 轮重试压掉？
    /// 更要紧的是：**它会不会把答案变差**（换首猜 ⇒ 换邻域 ⇒ 可能落到另一个局部极小）。
    ///
    /// ## 台架怎么建模"重规划"
    /// 游戏里同一个机动作会被**反复重规划**（阶梯每 N 秒一次），而几何变化很慢：
    ///   1. 随机采一条几何 `G`（两条轨道各自随机平面 ⇒ 有真实的 Δa 与倾角差）⇒ **冷启动**求一次得 `dt*`；
    ///   2. 两条轨道各**推进 60 s** 当作"60 秒后的重规划点" `G'`；
    ///   3. **臂 A** = 冷启动求 `G'`（= 今天的行为）；**臂 B** = 用 `dt*` 当首猜求 `G'`。
    ///
    /// ## ⚠ 第一版的采样缺口（用户点出后修的）
    /// 第一版只用了 `OrbitMath.GM`（= **地球**）一个 μ，而**本库有三个天体、μ 跨 4 个数量级**
    /// （太阳 `2e16` · 地球 `2e14` · 月球 `5e12`）⇒ 现在是**按天体分档采样**、逐档给读数。
    /// 这条要紧：外层重试的步长是 `synodic × 0.1`，而 **synodic 随 μ 变** ⇒ 收益可能逐档不同。
    ///
    /// ## 判据
    /// · **更快吗**：两臂墙钟（同一台机器、同一次运行里交替测）。
    /// · **质量掉了吗**：返回计划的 `|dv1|+|dv2|` 更好/持平/更差各多少次 + **差距的量级**。
    /// · **结果变了吗**：逐位相同多少条（这是"会改结果"的直接证据）。
    /// · **失败了吗**：两臂各自的失败计数。
    /// ⚠ 墙钟护栏 100 s（本库硬规矩）；固定随机种子 ⇒ 可复现。
    /// </summary>
    public class TwoImpulseWarmStartMonteCarlo
    {
        private readonly ITestOutputHelper _out;
        public TwoImpulseWarmStartMonteCarlo(ITestOutputHelper o) => _out = o;

        private const double WallGuardSeconds = 100.0;
        private const int SamplesPerBody = 60;

        /// <summary>
        /// **本库真实的三个天体**（`1.6/Defs/CelestialBodies/CelestialBodies_SolarSystem.xml`，逐条读过）。
        /// 半径区间按各家实况取（月球档正是绕月交会场景用的 1800–3300 km 那一带）。
        /// </summary>
        private static readonly (string name, double mu, double radius, double rMin, double rMax)[] Bodies =
        {
            ("月球", 5.0e12, 1.5e6, 1.9e6, 1.2e7),
            ("地球", 2.0e14, 5.0e6, 5.6e6, 4.2e7),
            ("太阳", 2.0e16, 3.0e6, 4.0e6, 2.0e9),
        };

        private sealed class Stat
        {
            public int n, failA, failB, better, equal, worse, same;
            public double msA, msB, dvA, dvB, worstWorse, bestBetter, worstWorseRel;
            public double MsA => msA / Math.Max(n, 1);
            public double MsB => msB / Math.Max(n, 1);
            public double Ratio => msA / Math.Max(msB, 1e-9);
            public double DvA => dvA / Math.Max(n, 1);
            public double DvB => dvB / Math.Max(n, 1);
        }

        [Fact]
        public void ColdVsWarmStart_OnRandomReplans()
        {
            var rng = new Random(20260930);
            var swAll = Stopwatch.StartNew();

            var rows = new StringBuilder();
            foreach (var b in Bodies)
            {
                if (swAll.Elapsed.TotalSeconds > WallGuardSeconds) break;
                Stat st = RunBody(b.name, b.mu, b.rMin, b.rMax, rng);
                rows.Append($"  {b.name,-3}（μ={b.mu:E0}）：n={st.n,3}｜A {st.MsA,5:F2} ms → B {st.MsB,5:F2} ms ⇒ **{st.Ratio,4:F2}×**"
                    + $"｜质量 更好 {st.better,3} / 持平 {st.equal,3} / 更差 {st.worse,3}"
                    + $"（均值 {st.DvA,6:F1} → {st.DvB,6:F1} m/s｜更好最多 −{st.bestBetter,7:F2}、更差最多 +{st.worstWorse,5:F2}）"
                    + $"｜逐位相同 {st.same}｜失败 A{st.failA}/B{st.failB}\n");
            }

            _out.WriteLine("\n══ ② warm start 蒙特卡洛（**按天体分档采样** · 墙钟 " + swAll.Elapsed.TotalSeconds.ToString("F1") + " s）══\n" + rows);
        }

        private Stat RunBody(string name, double mu, double rMin, double rMax, Random rng)
        {
            var st = new Stat();
            for (int i = 0; i < SamplesPerBody; i++)
            {
                // ── 1) 随机几何 G（本天体档） ──
                var (r1, v1) = RandomCircular(rng, mu, rMin, rMax);
                var (r2, v2) = RandomCircular(rng, mu, rMin, rMax);

                (V3 dv1, double dt1, V3 dv2, double dt2) p0;
                try { p0 = TwoImpulseTransfer.NextManeuver(mu, r1, v1, r2, v2); }
                catch (Exception) { continue; }          // 冷启动都解不出 ⇒ 剔除（不污染对比）

                // ── 2) 推进 60 s = "60 秒后的重规划点" ──
                (r1, v1) = OrbitView.Propagate(r1, v1, mu, 60.0);
                (r2, v2) = OrbitView.Propagate(r2, v2, mu, 60.0);

                // ── 3) 臂 A：冷启动（= 今天的行为） ──
                var swA = Stopwatch.StartNew();
                (V3 a1, double adt1, V3 a2, double adt2) pa;
                try { pa = TwoImpulseTransfer.NextManeuver(mu, r1, v1, r2, v2); }
                catch (Exception) { st.failA++; continue; }
                swA.Stop();

                // ── 3) 臂 B：用上一次的转移时长当首猜 ──
                var swB = Stopwatch.StartNew();
                (V3 b1, double bdt1, V3 b2, double bdt2) pb;
                try { pb = TwoImpulseTransfer.NextManeuverWarmStart(mu, r1, v1, r2, v2, p0.dt1); }
                catch (Exception) { st.failB++; st.n++; st.msA += swA.Elapsed.TotalMilliseconds; continue; }
                swB.Stop();

                st.n++;
                st.msA += swA.Elapsed.TotalMilliseconds;
                st.msB += swB.Elapsed.TotalMilliseconds;
                double qa = pa.a1.magnitude + pa.a2.magnitude;
                double qb = pb.b1.magnitude + pb.b2.magnitude;
                st.dvA += qa; st.dvB += qb;

                if (pa.a1.Equals(pb.b1) && pa.a2.Equals(pb.b2) && pa.adt1 == pb.bdt1 && pa.adt2 == pb.bdt2) st.same++;

                double delta = qb - qa;                      // >0 = B 更差
                if (delta < -1e-9)
                {
                    st.better++;
                    if (-delta > st.bestBetter) st.bestBetter = -delta;
                }
                else if (delta > 1e-9)
                {
                    st.worse++;
                    if (delta > st.worstWorse) st.worstWorse = delta;
                    if (delta / Math.Max(qa, 1e-9) > st.worstWorseRel) st.worstWorseRel = delta / Math.Max(qa, 1e-9);
                }
                else st.equal++;

                if (st.n % 20 == 0)
                    _out.WriteLine($"[② {name}] 进度 n={st.n}｜A 均 {st.MsA:F2} ms / B 均 {st.MsB:F2} ms（{st.Ratio:F2}×）"
                        + $"｜更好 {st.better} / 持平 {st.equal} / 更差 {st.worse}｜失败 A{st.failA}/B{st.failB}");
            }
            return st;
        }

        /// <summary>随机圆轨：半径在 [rMin, rMax]，轨道平面随机（⇒ 两条轨道的倾角差自然铺开）。</summary>
        private static (V3 r, V3 v) RandomCircular(Random rng, double mu, double rMin, double rMax)
        {
            double radius = rMin + (rMax - rMin) * rng.NextDouble();
            V3 pos = RandomUnit(rng) * radius;

            // 随机法向（与位置不平行即可），切向 = n̂ × r̂ ⇒ 圆速
            V3 n;
            do { n = RandomUnit(rng); } while (Math.Abs(V3.Dot(n, pos.normalized)) > 0.9);
            V3 tangent = V3.Cross(n, pos.normalized).normalized;
            double vc = Math.Sqrt(mu / radius);
            return (pos, tangent * vc * (0.98 + 0.04 * rng.NextDouble()));   // 略偏圆 ⇒ 有偏心率
        }

        private static V3 RandomUnit(Random rng)
        {
            while (true)
            {
                var v = new V3(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1);
                double m = v.sqrMagnitude;
                if (m > 1e-6 && m <= 1.0) return v / Math.Sqrt(m);
            }
        }
    }
}
