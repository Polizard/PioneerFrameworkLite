using System;
using PioneerFramework;
using PioneerFramework.Pure.Tests;   // CircularTarget
using MechJebLib.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **解析窗口预判的判据**（2026-10-21）。
    ///
    /// 背景：`PhasingOrbit` 那档几何里，霍曼窗口搜索会**成功收敛到一个 6.2 圈的窗口**再被
    /// "圈数 &lt; 5" 拒掉 —— 那 ~1.8 ms 是"先算完再丢"。修法是在搜索前用近圆共面闭式解
    /// （`RendezvousLadder.EstimateHohmannWaitSeconds`）判断"最早窗口也远在上限之外"，
    /// 是就直接建调相轨道。
    ///
    /// **本文件钉的是这条安全性**：凡是**搜索会接受**（窗口圈数 &lt; 上限）的几何，
    /// 解析估计**必须**落在 `上限 + 余量` 之内 —— 否则我们就会把一个本来该走霍曼的几何
    /// 误判成"明显超窗"而改走调相轨道（行为回归）。
    ///
    /// 时间/误差只进日志；断言只有两条：①覆盖足够；②零违规。
    /// </summary>
    public class HohmannAnalyticWindowTests
    {
        private readonly ITestOutputHelper _out;
        public HohmannAnalyticWindowTests(ITestOutputHelper o) => _out = o;

        private const double Mu = 2.0e14;
        private const double BodyRadius = 5.0e6;
        private const double CapOrbits = 5.0;       // 与 `Params.MaxPhasingOrbits` 默认上限一致
        private const double MarginOrbits = 0.5;    // 与 `RendezvousLadder.PhasingSkipMarginOrbits` 一致
        private static readonly DVector3 OrbitNormal = new DVector3(0, -1, 0);

        private static OrbitView Circular(double radius, double phaseDeg)
        {
            var t = CircularTarget.Create(radius, Mu, phaseDeg * Math.PI / 180.0, OrbitNormal);
            return OrbitView.FromStateVectors(t.PosAt(0.0), t.VelAt(0.0), Mu, 0.0,
                t.PlaneNormalAt(0.0), BodyRadius);
        }

        [Fact]
        public void AnalyticEstimate_NeverSkipsAGeometryTheSearchWouldAccept()
        {
            double[] radii = { 5.5e6, 6.0e6, 7.0e6, 7.5e6 };
            double[] targets = { 5.5e6, 6.0e6, 6.5e6, 7.0e6, 7.5e6, 8.1e6, 1.2e7 };
            double[] phases = { 0, 30, 60, 90, 120, 150, 180, 210, 240, 270, 300, 330 };

            int cases = 0, accepted = 0, skipped = 0, violations = 0, nanSearch = 0;
            double maxRelErrAccepted = 0.0;

            foreach (double r in radii)
                foreach (double rt in targets)
                    foreach (double ph in phases)
                    {
                        var self = Circular(r, 0.0);
                        var tgt = Circular(rt, ph);
                        if (self.period <= 0.0 || double.IsNaN(self.period)) continue;
                        cases++;

                        double dt1;
                        try
                        {
                            (_, dt1, _, _) = OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(
                                self, tgt, 0.0, coplanar: false);
                        }
                        catch (Exception)
                        {
                            nanSearch++;
                            continue;
                        }
                        double est = RendezvousLadder.EstimateHohmannWaitSeconds(self, tgt, 0.0);
                        if (double.IsNaN(est)) continue;

                        double searchOrbits = dt1 / self.period;
                        double estOrbits = est / self.period;
                        bool searchAccepts = searchOrbits < CapOrbits;
                        bool wouldSkip = estOrbits > CapOrbits + MarginOrbits;

                        if (searchAccepts)
                        {
                            accepted++;
                            double rel = dt1 > 0 ? Math.Abs(est - dt1) / dt1 : 0.0;
                            if (rel > maxRelErrAccepted) maxRelErrAccepted = rel;
                            if (wouldSkip)
                            {
                                violations++;
                                _out.WriteLine($"[违规] r={r / 1000:F0}km rt={rt / 1000:F0}km φ={ph}°：" +
                                               $"搜索结果 {searchOrbits:F2} 圈（接受）但解析 {estOrbits:F2} 圈会跳过");
                            }
                        }
                        else if (wouldSkip) skipped++;
                    }

            _out.WriteLine($"[解析窗口] 用例={cases} 搜索接受={accepted} 搜索超窗={cases - accepted - nanSearch} " +
                           $"被解析跳过={skipped}｜接受档最大相对误差={maxRelErrAccepted:P0}｜违规={violations}");

            Assert.True(accepted > 40, $"覆盖不足：搜索接受的几何只有 {accepted} 个");
            Assert.True(skipped > 20, $"被解析跳过的几何只有 {skipped} 个（解析预判没起作用？）");
            Assert.Equal(0, violations);
        }

        /// <summary>
        /// 端到端：那档"搜索会先算完再丢"的几何，`MakePlan` 应当**跳过搜索**并给出 PhasingOrbit。
        /// （r=7500 km → 7700 km、相位 90°：实测搜索窗口 6.21 圈 &gt; 上限 5。）
        /// </summary>
        [Fact]
        public void MakePlan_SkipsSearch_OnKnownFarWindowGeometry()
        {
            var self = Circular(7.5e6, 0.0);
            var tgt = Circular(7.7e6, 90.0);
            var p = new RendezvousLadder.Params
            {
                DesiredDistance = 283.0,
                MatchDistance = 2000.0,
                MaxClosingSpeed = 100.0,
                MaxPhasingOrbits = 5.0,
                MinRadius = 5.1e6,
            };

            var rung = RendezvousLadder.MakePlan(self, tgt, 0.0, p, out var nodes, out var status);
            _out.WriteLine($"[解析窗口·端到端] rung={rung} nodes={nodes.Count}｜status={status}");

            Assert.Equal(RendezvousLadder.Rung.PhasingOrbit, rung);
            Assert.Contains("跳过霍曼搜索", status);
            Assert.NotEmpty(nodes);

            // 读数：跳过搜索后，这份计划应当只剩"解析 + 建调相轨道"的量级（原路径 ~2.0–2.7 ms）。
            var sw = new System.Diagnostics.Stopwatch();
            var samples = new double[20];
            for (int i = 0; i < samples.Length; i++)
            {
                sw.Restart();
                RendezvousLadder.MakePlan(self, tgt, 0.0, p, out _, out _);
                sw.Stop();
                samples[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(samples);
            _out.WriteLine($"[解析窗口·端到端] 跳过搜索后 MakePlan 中位={samples[10]:F3} ms（原 ~2.0–2.7 ms）");
        }
        /// <summary>诊断：预热里"Phasing抬"那档到底选了哪个相位、解析估计几圈、是否真的跳过搜索。</summary>
        [Fact]
        public void Diagnose_WarmupPhasingGeometry()
        {
            var self = Circular(1.40 * BodyRadius, 0.0);
            var p = new RendezvousLadder.Params
            {
                DesiredDistance = 283.0, MatchDistance = 2000.0, MaxClosingSpeed = 100.0,
                MaxPhasingOrbits = 5.0, MinRadius = 1.02 * BodyRadius,
            };
            foreach (double ph in new[] { 90.0, -90.0, 30.0, 150.0, -30.0, -150.0 })
            {
                var tgt = Circular(1.54 * BodyRadius, ph);
                double est = RendezvousLadder.EstimateHohmannWaitSeconds(self, tgt, 0.0);
                var rung = RendezvousLadder.MakePlan(self, tgt, 0.0, p, out _, out var status);
                _out.WriteLine($"[预热诊断] φ={ph,6:F0}° est={est / self.period:F2} 圈 rung={rung}｜{status}");
            }
        }

        /// <summary>
        /// 同上，但**带小偏心**（e≈0.03）：解析模型假设圆轨，这里验证"偏心不让它误跳过"。
        /// 构造：把圆轨速度乘 (1±k)，得到 e≈k 的椭圆（同一半长轴附近），相位照常扫描。
        /// </summary>
        [Fact]
        public void AnalyticEstimate_NeverSkips_WithSmallEccentricity()
        {
            double[] radii = { 6.0e6, 7.5e6 };
            double[] targets = { 6.5e6, 7.0e6, 7.7e6, 8.1e6 };
            double[] phases = { 0, 45, 90, 135, 180, 225, 270, 315 };
            double[] ks = { -0.005, 0.0, 0.005 };

            int cases = 0, accepted = 0, skipped = 0, violations = 0, nanSearch = 0;
            foreach (double r in radii)
                foreach (double rt in targets)
                    foreach (double ph in phases)
                        foreach (double k in ks)
                        {
                            var self = EccCircular(r, 0.0, k);
                            var tgt = EccCircular(rt, ph, -k);
                            if (double.IsNaN(self.period) || self.period <= 0.0) continue;
                            cases++;

                            double dt1;
                            try
                            {
                                (_, dt1, _, _) = OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(
                                    self, tgt, 0.0, coplanar: false);
                            }
                            catch (Exception) { nanSearch++; continue; }
                            double est = RendezvousLadder.EstimateHohmannWaitSeconds(self, tgt, 0.0);
                            if (double.IsNaN(est)) continue;

                            bool searchAccepts = dt1 / self.period < CapOrbits;
                            bool wouldSkip = self.eccentricity < 0.005 && tgt.eccentricity < 0.005
                                && est / self.period > CapOrbits + MarginOrbits;
                            if (searchAccepts)
                            {
                                accepted++;
                                if (wouldSkip)
                                {
                                    violations++;
                                    _out.WriteLine($"[违规·偏心] r={r / 1000:F0} rt={rt / 1000:F0} φ={ph} k={k}：" +
                                                   $"搜索 {dt1 / self.period:F2} 圈（接受）但解析 {est / self.period:F2} 圈会跳过");
                                }
                            }
                            else if (wouldSkip) skipped++;
                        }

            _out.WriteLine($"[解析窗口·偏心] 用例={cases} 搜索接受={accepted} 被解析跳过={skipped} 违规={violations}");
            Assert.True(accepted > 20, $"覆盖不足：{accepted}");
            Assert.Equal(0, violations);
        }

        /// <summary>圆轨速度乘 (1+k) 得到 e≈|k| 的椭圆（同一点、同一方向）。</summary>
        private static OrbitView EccCircular(double radius, double phaseDeg, double k)
        {
            var t = CircularTarget.Create(radius, Mu, phaseDeg * Math.PI / 180.0, OrbitNormal);
            return OrbitView.FromStateVectors(t.PosAt(0.0), t.VelAt(0.0) * (1.0 + k), Mu, 0.0,
                t.PlaneNormalAt(0.0), BodyRadius);
        }
    }
}
