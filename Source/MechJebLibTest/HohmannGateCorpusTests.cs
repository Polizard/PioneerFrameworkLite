using System;
using PioneerFramework;
using PioneerFramework.Pure.Tests;   // CircularTarget
using MechJebLib.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **交会引导"正确性保险"的语料扩充**（2026-10-21 下午；交接 §11.36-N 的残余风险 ①②③）。
    ///
    /// 背景：解析预判门 + 闭式霍曼已经上线，判据语料只有**地球档**（`mu=2.0e14`、
    /// `R=5.0e6`、半径比 ≤1.35 的接受档）。交接里自己登记的三条残余风险里，有两条
    /// 是"语料没覆盖"而不是"已知有问题"：
    ///  ① 月球档（g 小约 40×）没进语料 ⇒ 模型误差是否仍 &lt;0.5 圈未验；
    ///  ② `e` 在 0.005 边界附近（如 0.0049）未单独扩样 —— 旧语料的 `k=±0.005` 恰好落在
    ///     **门的外侧**（严格 `&lt;`），等于没测到边界。
    ///
    /// 本文件补的就是这两块，外加"闭式 Δv 在其他天体上同不同"：
    /// · 尺度不变性（地球/月球/小行星三档，同一套相对几何）；
    /// · 偏心门边界的**谓词级**判据（`AnalyticGateApplies`）与**语料级**判据（0 违规）；
    /// · 闭式 vs 搜索的 Δv/点火时刻差在月球/小行星档的量级。
    ///
    /// 断言口径与既有台架一致：**只判"零违规"+ 覆盖下限**，误差只进日志。
    /// </summary>
    public class HohmannGateCorpusTests
    {
        private readonly ITestOutputHelper _out;
        public HohmannGateCorpusTests(ITestOutputHelper o) => _out = o;

        private const double CapOrbits = 5.0;        // 与 `Params.MaxPhasingOrbits` 默认一致
        private const double MarginOrbits = 0.5;     // 与 `RendezvousLadder.PhasingSkipMarginOrbits` 一致
        private const double GateEcc = 0.005;        // 与 `RendezvousLadder.AnalyticMaxEccentricity` 一致
        private static readonly DVector3 OrbitNormal = new DVector3(0, -1, 0);

        private readonly struct Body
        {
            public readonly string Name;
            public readonly double Mu, Radius;
            public Body(string name, double mu, double radius) { Name = name; Mu = mu; Radius = radius; }
            public override string ToString() => $"{Name}(μ={Mu:E2}, R={Radius / 1000:F0}km)";
        }

        // 地球档 = 与既有语料同参（正对照，读数应与 `HohmannAnalyticWindowTests` 同量级）；
        // 月球档 = 交接登记的风险①；小行星档 = 更极端的低 g（验"解析式与 μ 无关"这条尺度不变性）。
        private static readonly Body[] Bodies =
        {
            new Body("地球", 2.0e14, 5.0e6),
            new Body("月球", 4.9048695e12, 1.7374e6),
            new Body("小行星", 1.0e10, 3.0e5),
        };

        private static OrbitView Circular(in Body b, double a, double phaseDeg)
        {
            var t = CircularTarget.Create(a, b.Mu, phaseDeg * Math.PI / 180.0, OrbitNormal);
            return OrbitView.FromStateVectors(t.PosAt(0.0), t.VelAt(0.0), b.Mu, 0.0,
                t.PlaneNormalAt(0.0), b.Radius);
        }

        /// <summary>指定偏心的轨道：**在近拱点放置** ⇒ 半长轴精确为 <paramref name="a" />、偏心精确为 <paramref name="e" />。
        /// （旧语料用"圆轨速度乘 (1+k)"造偏心，`e` 只是近似值 —— 边界扩样不能靠近似。）</summary>
        private static OrbitView Eccentric(in Body b, double a, double phaseDeg, double e)
        {
            double ph = phaseDeg * Math.PI / 180.0;
            double rp = a * (1.0 - e);
            var pos = new V3(rp * Math.Cos(ph), 0.0, rp * Math.Sin(ph));
            double vp = Math.Sqrt(b.Mu * (1.0 + e) / rp);          // 近拱点速度 ⇒ E = −μ/2a
            var vel = new V3(-vp * Math.Sin(ph), 0.0, vp * Math.Cos(ph));
            return OrbitView.FromStateVectors(pos, vel, b.Mu, 0.0, OrbitNormal, b.Radius);
        }

        /// <summary>
        /// **尺度不变性**：同一套相对几何（`a/R` 与半径比）在三个天体上跑，解析预判都不得
        /// 跳过"搜索会接受"的几何。地球档同时充当正对照。
        /// </summary>
        [Theory]
        [InlineData(0)]   // 地球
        [InlineData(1)]   // 月球
        [InlineData(2)]   // 小行星
        public void AnalyticEstimate_ScaleInvariance_AcrossBodies(int bi)
        {
            Body b = Bodies[bi];
            double[] radiiRel = { 1.10, 1.20, 1.40, 1.50 };
            double[] ratios = { 1.02, 1.05, 1.10, 1.20, 1.35, 1.60, 2.00 };
            double[] phases = { 0, 15, 30, 45, 60, 75, 90, 105, 120, 135, 150, 165,
                                180, 195, 210, 225, 240, 255, 270, 285, 300, 315, 330, 345 };

            int cases = 0, accepted = 0, skipped = 0, violations = 0, nanSearch = 0, gateOff = 0;
            double maxRelAccepted = 0.0, maxEstErrOrbits = 0.0;

            foreach (double rr in radiiRel)
            {
                double a1 = rr * b.Radius;
                foreach (double ratio in ratios)
                {
                    double a2 = a1 * ratio;
                    foreach (double ph in phases)
                    {
                        var self = Circular(b, a1, 0.0);
                        var tgt = Circular(b, a2, ph);
                        if (!(self.period > 0.0) || double.IsNaN(self.period)) continue;
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

                        bool gate = RendezvousLadder.AnalyticGateApplies(self.eccentricity, tgt.eccentricity);
                        if (!gate) gateOff++;
                        double searchOrbits = dt1 / self.period;
                        double estOrbits = est / self.period;
                        bool searchAccepts = searchOrbits < CapOrbits;
                        bool wouldSkip = gate && estOrbits > CapOrbits + MarginOrbits;

                        if (searchAccepts)
                        {
                            accepted++;
                            double rel = Math.Abs(estOrbits - searchOrbits) / Math.Max(searchOrbits, 1e-9);
                            if (rel > maxRelAccepted) maxRelAccepted = rel;
                            double errOrbits = Math.Abs(estOrbits - searchOrbits);
                            if (errOrbits > maxEstErrOrbits) maxEstErrOrbits = errOrbits;
                            if (wouldSkip)
                            {
                                violations++;
                                _out.WriteLine($"[违规] {b} a1={a1 / 1000:F0}km 比={ratio:F2} φ={ph}°：" +
                                               $"搜索 {searchOrbits:F2} 圈（接受）但解析 {estOrbits:F2} 圈会跳过");
                            }
                        }
                        else if (wouldSkip) skipped++;
                    }
                }
            }

            _out.WriteLine($"[解析窗口·{b.Name}] 用例={cases} 搜索接受={accepted} 搜索超窗={cases - accepted - nanSearch} " +
                           $"被解析跳过={skipped}｜门关闭={gateOff}｜接受档最大相对误差={maxRelAccepted:P1} " +
                           $"最大圈数误差={maxEstErrOrbits:F3}｜违规={violations}");

            Assert.True(accepted >= 60, $"{b.Name}：覆盖不足，搜索接受的几何只有 {accepted} 个");
            Assert.True(skipped >= 10, $"{b.Name}：被解析跳过的几何只有 {skipped} 个（预判没起作用？）");
            Assert.Equal(0, violations);
        }

        /// <summary>
        /// **偏心门的边界语义**（交接残余风险②）：门是**严格** `e &lt; 0.005`，两个轨道各判一次。
        /// `e = 0.005` 本身必须**不进**（退回原搜索 ⇒ 行为与上线前一致）；`NaN` 也必须不进（安全方向）。
        /// 这条以前内联在 `MakePlan` 的 `if` 里，只有构造出"刚好踩线"的整条几何才验得到 ⇒ 等于验不到。
        /// </summary>
        [Fact]
        public void AnalyticGate_IsStrictAtEccentricityBoundary()
        {
            Assert.True(RendezvousLadder.AnalyticGateApplies(0.0, 0.0));
            Assert.True(RendezvousLadder.AnalyticGateApplies(0.0049, 0.0049));
            Assert.True(RendezvousLadder.AnalyticGateApplies(GateEcc - 1e-9, GateEcc - 1e-9), "门内侧 1e-9 必须适用");

            Assert.False(RendezvousLadder.AnalyticGateApplies(GateEcc, 0.0), "e=0.005 本身必须落在门外（严格 <）");
            Assert.False(RendezvousLadder.AnalyticGateApplies(0.0, GateEcc));
            Assert.False(RendezvousLadder.AnalyticGateApplies(GateEcc + 1e-9, 0.0));
            Assert.False(RendezvousLadder.AnalyticGateApplies(double.NaN, 0.0), "NaN 退化轨道必须退回搜索");
            Assert.False(RendezvousLadder.AnalyticGateApplies(0.0, double.NaN));
            _out.WriteLine("[解析门] 边界谓词 8 条全过：0.0049 适用、0.005 及 NaN 不适用");
        }

        /// <summary>
        /// **偏心门内边界扩样**（交接残余风险②的语料级判据）：`e` 取 0 ~ 0.0049 之间的若干档，
        /// 两轨可以不同 ⇒ 全是"门内"几何，搜索接受的**一个都不许被跳过**。
        /// </summary>
        [Fact]
        public void AnalyticEstimate_NearEccentricityBoundary_NeverSkipsAcceptedGeometry()
        {
            Body b = Bodies[0];
            double[] a1s = { 6.0e6, 7.5e6 };
            double[] ratios = { 1.02, 1.10, 1.35 };
            double[] phases = { 0, 45, 90, 135, 180, 225, 270, 315 };
            (double e1, double e2)[] pairs =
            {
                (0.0, 0.0), (0.0049, 0.0), (0.0, 0.0049), (0.0049, 0.0049),
                (0.0040, 0.0045), (0.0020, 0.0049),
            };

            int cases = 0, accepted = 0, skipped = 0, violations = 0, nanSearch = 0, gateOff = 0;
            double maxErrOrbits = 0.0;

            foreach (double a1 in a1s)
                foreach (double ratio in ratios)
                    foreach (double ph in phases)
                        foreach ((double e1, double e2) in pairs)
                        {
                            var self = Eccentric(b, a1, 0.0, e1);
                            var tgt = Eccentric(b, a1 * ratio, ph, e2);
                            if (!(self.period > 0.0) || double.IsNaN(self.period)) continue;
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

                            bool gate = RendezvousLadder.AnalyticGateApplies(self.eccentricity, tgt.eccentricity);
                            if (!gate) gateOff++;
                            double searchOrbits = dt1 / self.period;
                            double estOrbits = est / self.period;
                            bool wouldSkip = gate && estOrbits > CapOrbits + MarginOrbits;

                            if (searchOrbits < CapOrbits)
                            {
                                accepted++;
                                double err = Math.Abs(estOrbits - searchOrbits);
                                if (err > maxErrOrbits) maxErrOrbits = err;
                                if (wouldSkip)
                                {
                                    violations++;
                                    _out.WriteLine($"[违规·边界] a1={a1 / 1000:F0} 比={ratio:F2} φ={ph}° " +
                                                   $"e1={e1:F4} e2={e2:F4}：搜索 {searchOrbits:F2} 圈（接受）但解析 {estOrbits:F2} 圈");
                                }
                            }
                            else if (wouldSkip) skipped++;
                        }

            _out.WriteLine($"[解析窗口·偏心边界] 用例={cases} 搜索接受={accepted} 被解析跳过={skipped} " +
                           $"门关闭={gateOff}｜接受档最大圈数误差={maxErrOrbits:F3}｜违规={violations}");
            Assert.True(accepted >= 30, $"覆盖不足：边界档搜索接受的只有 {accepted} 个");
            Assert.Equal(0, gateOff);      // e 全部 < 0.005 ⇒ 门必须全开（否则是本测试自己的构造错误）
            Assert.Equal(0, violations);
        }

        /// <summary>
        /// **闭式霍曼的 Δv/时刻差在月球档与极小天体档的量级**（残余风险①的另一半：
        /// 上一组只验"预判门"，这组验"闭式两发本身"）。
        /// 判据：Δv 相对差 &lt;0.1%、点火时刻差 **&lt;0.5% 轨道周期**（不是绝对秒 —— 见断言处）、不让更多例变贵。
        /// </summary>
        [Theory]
        [InlineData(1)]   // 月球
        [InlineData(2)]   // 小行星
        public void ClosedForm_DvMatchesSearch_OnOtherBodies(int bi)
        {
            Body b = Bodies[bi];
            double[] radiiRel = { 1.10, 1.20, 1.40, 1.50 };
            double[] ratios = { 1.02, 1.05, 1.10, 1.20, 1.35 };
            double[] phases = { 0, 30, 60, 90, 120, 150, 180, 210, 240, 270, 300, 330 };

            int cases = 0, okA = 0, okB = 0, cheaper = 0, pricier = 0, within1pct = 0;
            double maxDvRel = 0.0, maxBurnDt = 0.0, maxBurnDtRel = 0.0, sumDvRel = 0.0;

            foreach (double rr in radiiRel)
            {
                double a1 = rr * b.Radius;
                foreach (double ratio in ratios)
                    foreach (double ph in phases)
                    {
                        var self = Circular(b, a1, 0.0);
                        var tgt = Circular(b, a1 * ratio, ph);
                        cases++;

                        double dvA, utA1;
                        try
                        {
                            (V3 av1, double t1, V3 av2, double t2) =
                                OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(self, tgt, 0.0, coplanar: false);
                            if (double.IsNaN(t1) || double.IsInfinity(t1)) continue;
                            dvA = av1.magnitude + av2.magnitude;
                            utA1 = t1;
                            okA++;
                        }
                        catch (Exception) { continue; }

                        double tb = RendezvousLadder.EstimateHohmannWaitSeconds(self, tgt, 0.0);
                        if (double.IsNaN(tb)) continue;
                        double ar1 = self.semiMajorAxis, ar2 = tgt.semiMajorAxis;
                        double tof = Math.PI * Math.Sqrt(Math.Pow(ar1 + ar2, 3.0) / (8.0 * b.Mu));
                        double arrUt = tb + tof;
                        (V3 rb, V3 vb) = self.RightHandedStateVectorsAtUT(tb);
                        (V3 ra, V3 va) = tgt.RightHandedStateVectorsAtUT(arrUt);
                        double r1m = rb.magnitude, r2m = ra.magnitude;
                        if (!(r1m > 0.0) || !(r2m > 0.0)) continue;
                        double vNeed = Math.Sqrt(b.Mu * (2.0 / r1m - 2.0 / (r1m + r2m)));
                        double vArr = Math.Sqrt(b.Mu * (2.0 / r2m - 2.0 / (r1m + r2m)));
                        double dvB = (vb.normalized * (vNeed - vb.magnitude)).magnitude
                                   + (va.normalized * (va.magnitude - vArr)).magnitude;
                        okB++;

                        double rel = dvA > 1e-9 ? Math.Abs(dvB - dvA) / dvA : 0.0;
                        double dtBurn = Math.Abs(tb - utA1);
                        double dtBurnRel = self.period > 0.0 ? dtBurn / self.period : double.NaN;
                        sumDvRel += rel;
                        if (rel > maxDvRel) maxDvRel = rel;
                        if (dtBurn > maxBurnDt) maxBurnDt = dtBurn;
                        if (dtBurnRel > maxBurnDtRel) maxBurnDtRel = dtBurnRel;
                        if (rel <= 0.01) within1pct++;
                        if (dvB < dvA) cheaper++; else if (dvB > dvA) pricier++;
                    }
            }

            _out.WriteLine($"[闭式·{b.Name}] 用例={cases} 搜索有解={okA} 闭式有解={okB}｜Δv 相对差 中位={sumDvRel / Math.Max(okB, 1):P2} " +
                           $"最大={maxDvRel:P2}（≤1% 占 {within1pct * 100.0 / Math.Max(okB, 1):F0}%）｜点火时刻差 最大={maxBurnDt:F0}s " +
                           $"= {maxBurnDtRel:P2} 周期｜更省 {cheaper} / 更贵 {pricier}");

            Assert.True(okA >= 100, $"{b.Name}：搜索有解覆盖不足（{okA}）");
            Assert.True(okB >= 100, $"{b.Name}：闭式有解覆盖不足（{okB}）");
            Assert.True(maxDvRel < 0.001, $"{b.Name}：Δv 相对差最大 {maxDvRel:P2} 超过 0.1%");
            // ⚠ 判定用**相对周期**（0.5%），不是绝对秒：点火时刻的偏差来自窗口搜索的步长，
            //   与天体尺度成正比 —— 地球档 10 s / 5,730 s = 0.17%、小行星档 20 s / 11,922 s = 0.17%，
            //   是**同一个位移**。既有地球台架的 `15 s` 绝对门照搬到别的天体上就变成了"尺度歧视"
            //   （2026-10-21 首批扩样就是这么被自己的判据挡住的，见本文件头）。
            Assert.True(maxBurnDtRel < 0.005, $"{b.Name}：点火时刻差最大 {maxBurnDtRel:P2} 周期 超过 0.5%");
            Assert.True(pricier <= okB / 20, $"{b.Name}：闭式更贵的比例过高 {pricier}/{okB}");
        }

        /// <summary>
        /// **月球档端到端**：① `1.5R → 1.54R、90°`（地球档实测"搜索算完才发现 6.21 圈"的同一相对几何）
        /// 必须走"跳过霍曼搜索"；② `1.4R → 1.4R×1.05、30°` 必须走"闭式霍曼"且两发齐全。
        /// 这两条把"解析门/闭式"从语料钉到真实 `MakePlan` 调用路径上（换 mu 后仍然落同一条支）。
        /// </summary>
        [Fact]
        public void MakePlan_MoonScale_TakesAnalyticPaths()
        {
            Body b = Bodies[1];
            var p = new RendezvousLadder.Params
            {
                DesiredDistance = 283.0,
                MatchDistance = 2000.0,
                MaxClosingSpeed = 100.0,
                MaxPhasingOrbits = 5.0,
                MinRadius = 1.02 * b.Radius,
            };

            // ① 远窗口 ⇒ 跳过搜索
            var farSelf = Circular(b, 1.50 * b.Radius, 0.0);
            var farTgt = Circular(b, 1.50 * 1.0267 * b.Radius, 90.0);
            var farRung = RendezvousLadder.MakePlan(farSelf, farTgt, 0.0, p, out var farNodes, out var farStatus);
            _out.WriteLine($"[月球·远窗口] rung={farRung} nodes={farNodes.Count}｜{farStatus}");
            Assert.Equal(RendezvousLadder.Rung.PhasingOrbit, farRung);
            Assert.Contains("跳过霍曼搜索", farStatus);
            Assert.NotEmpty(farNodes);

            // ② 近窗口 ⇒ 闭式
            var nearSelf = Circular(b, 1.40 * b.Radius, 0.0);
            var nearTgt = Circular(b, 1.40 * 1.05 * b.Radius, 30.0);
            var nearRung = RendezvousLadder.MakePlan(nearSelf, nearTgt, 0.0, p, out var nearNodes, out var nearStatus);
            _out.WriteLine($"[月球·近窗口] rung={nearRung} nodes={nearNodes.Count}｜{nearStatus}");
            Assert.Equal(RendezvousLadder.Rung.Hohmann, nearRung);
            Assert.Contains("闭式", nearStatus);
            Assert.Equal(2, nearNodes.Count);

            // ③ 闭式计划的 Δv 必须与搜索同量级（同一几何再跑一次搜索对照）
            (V3 sv1, double st1, V3 sv2, double st2) =
                OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(nearSelf, nearTgt, 0.0, coplanar: false);
            double dvSearch = sv1.magnitude + sv2.magnitude;
            double dvClosed = RendezvousLadder.TotalDv(nearNodes);
            double rel = Math.Abs(dvClosed - dvSearch) / Math.Max(dvSearch, 1e-9);
            _out.WriteLine($"[月球·近窗口] Δv 闭式={dvClosed:F3} 搜索={dvSearch:F3} 相对差={rel:P2}｜点火 UT 闭式={nearNodes[0].ut:F1} 搜索={st1:F1}");
            Assert.True(rel < 0.001, $"月球档闭式 Δv 相对差 {rel:P2} 超过 0.1%");
        }
    }
}
