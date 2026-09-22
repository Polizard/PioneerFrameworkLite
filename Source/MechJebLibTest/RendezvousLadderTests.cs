using System;
using System.Collections.Generic;
using MechJebLib.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **交会阶梯**（B3）判据：每一级都必须有"**只有它该被选中**"的场景，且它给的节点要过**定义式**检查。
    ///
    /// 为什么要按级测：阶梯的失效方式是**选错级**（而不是某一级的算式错）——外部分析报告那条
    /// "照抄的顺序"就会让它每帧选中"匹配平面"。所以"选中了哪一级"本身就是被测对象。
    ///
    /// ⚠ 这里**不**重算任何 Δv：判据只做"把它的节点施加后，物理是否变成那一级想让它变的样子"
    /// （匹配速度 ⇒ 相对速度归零；霍曼 ⇒ 真的交会；圆化 ⇒ e 归零且 a = 目标 a）。
    /// </summary>
    public class RendezvousLadderTests
    {
        private readonly ITestOutputHelper _out;
        public RendezvousLadderTests(ITestOutputHelper output) => _out = output;

        private const double Mu = 2.0e14;
        private const double R0 = 7_000_000.0;
        private const double BodyR = 1_400_000.0;

        private static readonly RendezvousLadder.Params P = new RendezvousLadder.Params
        {
            DesiredDistance = 100.0,
            MaxPhasingOrbits = 5.0,
            MaxClosingSpeed = 100.0,
            MinRadius = BodyR + 200_000.0,
        };

        /// <summary>圆轨（顺行赤道），可指定半径与相位。
        /// ⚠ **切向 = ĥ × r̂**（ĥ = +Y ⇒ 赤道轨道的法向）：本行曾写错成 `(−vc·sinφ, vc, 0)`，
        ///   于是"圆轨"的**平面是歪的**（h 有 X 分量）⇒ 阶梯正确地选了"匹配平面"。
        ///   即：**测试自己的构造也必须被验证**（这一条是被被测物反过来抓出来的）。</summary>
        private static OrbitView Circular(double radius, double phaseDeg, double ut = 0.0)
        {
            double vc = Math.Sqrt(Mu / radius);
            double ph = phaseDeg * Math.PI / 180.0;
            var r = new V3(radius * Math.Cos(ph), 0, radius * Math.Sin(ph));
            var t = new V3(Math.Sin(ph), 0, -Math.Cos(ph));      // ĥ × r̂
            return OrbitView.FromStateVectors(r, t * vc, Mu, ut, new V3(0, 0, 1), BodyR);
        }

        /// <summary>把轨道推进 <paramref name="seconds"/> 秒后，用那一刻的状态构造"同一条轨道"。</summary>
        private static OrbitView After(OrbitView o, double seconds)
        {
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(seconds);
            return OrbitView.FromStateVectors(r, v, Mu, seconds, new V3(0, 0, 1), BodyR);
        }

        /// <summary>e=0.3 的椭圆（近心点 r=R0；法向 ∥ **+Y**，与 `Circular` 同赤道约定）。</summary>
        private static OrbitView Eccentric(double ut = 0.0)
        {
            double vc = Math.Sqrt(Mu / R0);
            // ⚠ 赤道约定必须与 `Circular` 一致（法向 ∥ **+Y** = 游戏极轴）：原先写 `(0, √1.3·vc, 0)`
            //   得到 h ∥ +Z ⇒ 与别处的"赤道轨道"差 90° ⇒ 阶梯**正确地**判"平面不匹配"。
            //   即：又一次**测试构造自己**的错（被被测物反过来抓出来）。
            return OrbitView.FromStateVectors(new V3(R0, 0, 0), new V3(0, 0, -Math.Sqrt(1.3) * vc),
                Mu, ut, new V3(0, 0, 1), BodyR);
        }

        /// <summary>把一组节点按顺序施加（每条都在自己的 UT 上）⇒ 得到终态轨道（纯二体）。</summary>
        private static OrbitView Apply(OrbitView o, IReadOnlyList<PlanNode> nodes)
        {
            OrbitView cur = o;
            foreach (var n in nodes)
            {
                (V3 r, V3 v) = cur.RightHandedStateVectorsAtUT(n.ut);
                cur = OrbitView.FromStateVectors(r, v + n.dV, Mu, n.ut, new V3(0, 0, 1), BodyR);
            }
            return cur;
        }

        /// <summary>① `Done` / ② `MatchVelocitiesNow`：距离判据的两侧，且节点真的把相对速度清零。</summary>
        [Fact]
        public void Done_And_MatchVelocitiesNow()
        {
            var self = Circular(R0, 0.0);
            var tgt = Circular(R0, 0.0);
            var rung = RendezvousLadder.MakePlan(self, tgt, 0.0, P, out List<PlanNode> nodes, out string st1);
            Assert.Equal(RendezvousLadder.Rung.Done, rung);
            Assert.Empty(nodes);

            // 同位置但相对速度 5 m/s（给目标一个速度偏置）⇒ 就地匹配
            var tgtFast = OrbitView.FromStateVectors(new V3(R0, 0, 0), new V3(0, Math.Sqrt(Mu / R0) + 5.0, 0),
                Mu, 0.0, new V3(0, 0, 1), BodyR);
            var rung2 = RendezvousLadder.MakePlan(self, tgtFast, 0.0, P, out List<PlanNode> nodes2, out string st2);
            Assert.Equal(RendezvousLadder.Rung.MatchVelocitiesNow, rung2);
            Assert.Single(nodes2);
            Assert.True(nodes2[0].ut == 0.0, "就地匹配的 UT 必须是 now");

            (V3 r, V3 v) = self.RightHandedStateVectorsAtUT(0.0);
            V3 vRel = v + nodes2[0].dV - tgtFast.RightHandedOrbitalVelocityAtUT(0.0);
            Assert.True(vRel.magnitude < 1e-9, $"匹配速度后相对速度应归零，实测 {vRel.magnitude:E3}");
            _out.WriteLine($"[阶梯] ①② {st1} ｜ {st2}");
        }

        /// <summary>③ `MatchAtClosestApproach`（近处入口）：很近 + 150 s 内到最近点 ⇒ 打那一发并**真的停在期望距离**。
        /// ⚠ 场景**不能**用"同一条轨道上的一段相位差"：那样两船相对距离**恒定** ⇒ 最近点在无穷远 ⇒
        ///   `caUt < now+150s` 不成立 ⇒ 阶梯会（正确地）落到"画拦截"。必须是**真会靠近**的几何。</summary>
        [Fact]
        public void MatchAtClosestApproach_NearEntry()
        {
            // 近似共面（倾角差 0.02°）⇒ 两轨道**在节点处相交**。目标放在"沿自己的轨道、离节点还有 34 s"处：
            // 此刻间距 = 2R·sin(ω·34s/2) ≈ 183 km（< r/25 = 280 km 的"很近"门、> 107 m 的就地匹配门），
            // 最近接近 ≈ 0 且 34 s 后到 ⇒ 命中 3a。
            // ⚠ 不能用 `After(Tilted(...), -34)`（负 UT 反向传播）—— 直接在倾斜平面内按真近点角构造。
            var self = Circular(R0, 0.0);
            var tgt = TiltedBefore(R0, 0.02, 34.0);

            // ⚠ **3a 不能靠手调几何复现**：同半径、同周期的两条轨道，**间距恒定** ⇒ 最近接近在无穷远
            //   ⇒ `caUt < now+150s` 永不成立（我试了两种摆法，都被阶梯正确地判成"画拦截"）。
            //   3a 的真实触发场景是"**已经在收敛航线上**"——而那正是**上一级（画拦截）的产物**
            //   ⇒ 所以按阶梯的**生命周期**测：先让 ④ 画拦截，再在到达前 60 s 重问阶梯 ⇒ 应命中 3a。
            var intercept = RendezvousLadder.MakePlan(self, tgt, 0.0, P, out List<PlanNode> n4, out string st4);
            Assert.Equal(RendezvousLadder.Rung.Intercept, intercept);
            _out.WriteLine($"[阶梯] ④ {st4}");
            // ★ **两冲量必须提交**（2026-09-21）：`Intercept` 现在排**两发**——起飞 + 到达刹车。
            //   本用例要的是"起飞之后的那条轨道"，所以**只施加第一发**（原来的 `Apply(self, n4)` 在
            //   只有一发时恰好等价；改成两发后若不限定，施加的就是**停在靶上**的终态 ⇒ 后文必挂）。
            OrbitView selfAfter = Apply(self, new[] { n4[0] });
            // 到达时刻 = 节点 UT + 拦截时长，而拦截时长由阶梯自己按 `dist/100` 钳到上限算 ⇒ 测试里同式复算
            // （**不复用返回值**：API 目前只给节点，不给"预计到达" —— 若日后要，那是加字段而不是解析日志）
            double dist0 = (tgt.RightHandedBCIPositionAtUT(0.0) - self.RightHandedBCIPositionAtUT(0.0)).magnitude;
            double closingSpeed = Math.Min(dist0 / 100.0, P.MaxClosingSpeed);
            double arriveAt = n4[0].ut + dist0 / closingSpeed;
            double now2 = arriveAt - 60.0;                        // 到达前 60 s
            (V3 r2, V3 v2) = selfAfter.RightHandedStateVectorsAtUT(now2);
            selfAfter = OrbitView.FromStateVectors(r2, v2, Mu, now2, new V3(0, 0, 1), BodyR);

            var rung = RendezvousLadder.MakePlan(selfAfter, tgt, now2, P, out List<PlanNode> nodes, out string st);
            _out.WriteLine($"[阶梯] ③ 拦截后重问 ⇒ {rung}：{st}");
            Assert.Equal(RendezvousLadder.Rung.MatchAtClosestApproach, rung);
            Assert.Single(nodes);

            // ⚠ 节点必须打在**拦截之后的那条轨道**上（打在原轨道上是"测试自己的错"，我第一次就踩了）
            OrbitView after = Apply(selfAfter, nodes);
            double sep = after.Separation(tgt, nodes[0].ut);
            _out.WriteLine($"[阶梯] ③ {st}｜节点 UT={nodes[0].ut:F1}s Δv={nodes[0].dV.magnitude:F3} m/s｜打完在该时刻距离 {sep:F2} m");
            Assert.True(sep < P.DesiredDistance * 3.0,
                $"在最近点匹配后应停在期望距离附近（{P.DesiredDistance:F0} m），实测 {sep:F1} m");
        }

        /// <summary>④ `Intercept`（近处入口的另一半）：很近但不在 150 s 窗口 / 不走最近点 ⇒ 画拦截。</summary>
        [Fact]
        public void Intercept_WhenCloseButNotOnCourse()
        {
            // 很近但**在另一条轨道上**（半径差 5 km、相位差 0.02°）⇒ 最近点窗口不满足那一支
            var self = Circular(R0, 0.0);
            var tgt = Circular(R0 + 5_000.0, 0.02);
            var rung = RendezvousLadder.MakePlan(self, tgt, 0.0, P, out List<PlanNode> nodes, out string st);
            _out.WriteLine($"[阶梯] ④ 选中 {rung}：{st}");
            Assert.Equal(RendezvousLadder.Rung.Intercept, rung);
            // ★★ **两发 + 真的把相对速度清零**（2026-09-21，本用例从"1 发"升级）：
            //   原来只断言 `Single(nodes)` —— 而"只交第一发"正是那个 2–12 km 极限环的真身
            //   （实测 `到达冲量 ≈ Item1 ≈ 100 m/s`：每到一个会合点都带着全速穿过去）。
            //   现在契约是**两冲量转移**：第二发 = 到达时刻的刹车 ⇒ 打完**相对速度应当≈0**，
            //   这才是"交会"（而不只是"到达同一个点"）。这条是新判据，不是放宽旧判据。
            Assert.Equal(2, nodes.Count);
            Assert.True(nodes[0].ut >= 15.0, "起飞节点应排在 now+15 之后");
            Assert.True(nodes[1].ut - nodes[0].ut >= 100.0,
                $"刹车节点应在到达时刻（起飞后一整段拦截时长）；实测间隔 {nodes[1].ut - nodes[0].ut:F1}s");

            OrbitView after = Apply(self, nodes);
            (V3 ra2, V3 va2) = after.RightHandedStateVectorsAtUT(nodes[1].ut);
            (V3 rt2, V3 vt2) = tgt.RightHandedStateVectorsAtUT(nodes[1].ut);
            double miss = (ra2 - rt2).magnitude;
            double relV = (va2 - vt2).magnitude;
            _out.WriteLine($"[阶梯] ④ 两冲量打完：到达距离 {miss:F1} m、**相对速度 {relV:F3} m/s**（t={nodes[1].ut:F0}s）");
            Assert.True(miss < 1000.0, $"两冲量打完应到达靶位附近，实测距离 {miss:F1} m");
            Assert.True(relV < 1.0, $"**两冲量打完相对速度应当≈0**（这才是交会），实测 {relV:F3} m/s");
        }

        /// <summary>⑤⑦ `Hohmann` / `PhasingOrbit`：同一条几何下扫相位 ⇒ **两级都要可达**，且霍曼节点**真的交会**。</summary>
        [Fact]
        public void Hohmann_And_PhasingOrbit_AreBothReachable()
        {
            var self = Circular(R0, 0.0);
            var seen = new Dictionary<RendezvousLadder.Rung, double>();
            var sb = new System.Text.StringBuilder();
            sb.Append("\n[阶梯] ⑤⑦ 半径 7000→8100 km，扫目标相位：");

            // ⚠ 半径差有**两个夹**（两个都是 MJ 的量纲，不是我们调的参数）：
            //   · 太小（如 100 km）⇒ 最近接近距离 = Δr < a_t/25 ⇒ 永远走"最近点匹配"那级，**霍曼不可达**；
            //   · 太大（如 1500 km）⇒ 会合周期 / T1 < 5 ⇒ 霍曼窗口永远在上限内，**调相级不可达**；
            //   取 r1/r2 ≈ 0.864（会合周期 ≈ 5.1 T1）⇒ **两级都能出现**（这正是上游 `maxPhasingOrbits` 的语义边界）。
            for (double ph = 0.0; ph < 360.0; ph += 1.0)
            {
                var tgt = Circular(R0 + 1_100_000.0, ph);
                var rung = RendezvousLadder.MakePlan(self, tgt, 0.0, P, out List<PlanNode> nodes, out string st);
                if (!seen.ContainsKey(rung)) seen[rung] = ph;
                if (rung == RendezvousLadder.Rung.Hohmann && seen.Count <= 3)
                {
                    // 霍曼的定义式：打完两发之后**真的到达目标位置**
                    OrbitView after = Apply(self, nodes);
                    double sep = after.Separation(tgt, nodes[1].ut);
                    Assert.True(sep < 1.0, $"霍曼第二发后应真的交会：{sep:E3} m");
                    Assert.True(nodes[0].ut < nodes[1].ut, "两发必须按时间先后排");
                }
            }

            foreach (var kv in seen) sb.Append("\n   相位 ").Append(kv.Value.ToString("F0")).Append("° ⇒ ").Append(kv.Key);
            _out.WriteLine(sb.ToString());

            Assert.Contains(RendezvousLadder.Rung.Hohmann, seen.Keys);
            Assert.Contains(RendezvousLadder.Rung.PhasingOrbit, seen.Keys);
        }

        /// <summary>⑥ `Circularize`：共面但轨道不圆 ⇒ 在离目标半径更近的拱点圆化，且**打完 e 归零**。</summary>
        [Fact]
        public void Circularize_WhenCoplanarButNotCircular()
        {
            var self = Eccentric();                       // e=0.3、近心点 7000 km、远拱点 13000 km
            var tgt = Circular(R0 + 400_000.0, 0.0);      // 目标 a = 7400 km ⇒ 离**近**拱点（|7000−7400|=400 < |13000−7400|）更近
            var rung = RendezvousLadder.MakePlan(self, tgt, 0.0, P, out List<PlanNode> nodes, out string st);
            Assert.Equal(RendezvousLadder.Rung.Circularize, rung);
            Assert.Single(nodes);

            OrbitView after = Apply(self, nodes);
            _out.WriteLine($"[阶梯] ⑥ {st}｜Δv={nodes[0].dV.magnitude:F2} m/s ⇒ e={after.eccentricity:E3}、a={after.semiMajorAxis / 1000.0:F0} km");
            Assert.True(after.eccentricity < 1e-6, $"圆化后 e 应为零，实测 {after.eccentricity:E3}");
            Assert.True(Math.Abs(after.semiMajorAxis - R0) / R0 < 1e-9,
                "圆化是**在本点半径上**变圆（不是把 a 搬到目标 a）—— 这条钉住语义");
        }

        /// <summary>⑧ `MatchPlanes`：兜底级 —— 异面**且**互相不接近（否则会先命中"最近点匹配"那级）。</summary>
        [Fact]
        public void MatchPlanes_WhenNothingElseApplies()
        {
            var self = Circular(R0, 0.0);
            var tgt = Tilted(8_500_000.0, 2.0);           // 异面 + 大半径差 ⇒ 最近接近距离远
            var rung = RendezvousLadder.MakePlan(self, tgt, 0.0, P, out List<PlanNode> nodes, out string st);
            Assert.Equal(RendezvousLadder.Rung.MatchPlanes, rung);
            Assert.Single(nodes);

            OrbitView after = Apply(self, nodes);
            double inc = after.RelativeInclination(tgt) * 180.0 / Math.PI;
            _out.WriteLine($"[阶梯] ⑧ {st}｜Δv={nodes[0].dV.magnitude:F2} m/s ⇒ 相对倾角 {inc:E3}°");
            Assert.True(inc < 1e-6, $"匹配平面后相对倾角应归零，实测 {inc:E3}°");
        }

        /// <summary>倾角 <paramref name="incDeg"/> 的圆轨上、**离节点还有 <paramref name="leadSeconds"/> 秒**
        /// （节点 = +X 方向，与 <see cref="Tilted"/> 同平面）。直接按真近点角构造，不依赖负 UT 反向传播。</summary>
        private static OrbitView TiltedBefore(double radius, double incDeg, double leadSeconds)
        {
            double vc = Math.Sqrt(Mu / radius);
            double inc = incDeg * Math.PI / 180.0;
            double th = -Math.Sqrt(Mu / (radius * radius * radius)) * leadSeconds;   // 真近点角（负 = 节点之前）
            var xHat = new V3(1, 0, 0);                       // 节点方向
            var yHat = new V3(0, Math.Sin(inc), -Math.Cos(inc));   // 平面内第二基（与 Tilted 的 v 同向）
            var r = (xHat * Math.Cos(th) + yHat * Math.Sin(th)) * radius;
            var v = (yHat * Math.Cos(th) - xHat * Math.Sin(th)) * vc;
            return OrbitView.FromStateVectors(r, v, Mu, 0.0, new V3(0, 0, 1), BodyR);
        }

        /// <summary>倾角 <paramref name="incDeg"/> 的圆轨：基轨 = 赤道圆轨（v = −vc·ẑ ⇒ h ∥ **+Y**），
        /// 再绕**径向轴**（+X）倾 inc ⇒ 升交点落在 X 轴上。</summary>
        private static OrbitView Tilted(double radius, double incDeg)
        {
            double vc = Math.Sqrt(Mu / radius);
            double inc = incDeg * Math.PI / 180.0;
            var r = new V3(radius, 0, 0);
            var v = new V3(0, vc * Math.Sin(inc), -vc * Math.Cos(inc));
            return OrbitView.FromStateVectors(r, v, Mu, 0.0, new V3(0, 0, 1), BodyR);
        }

        /// <summary>
        /// ★★ **用户裁定 (a)**（2026-09-20）：最近接近擦线过门的几何不许走 `MatchAtClosestApproach`。
        /// 场景 = 用户的验收场景（**同轨异相位**）：同半径 5500 km、相位差 30°、倾角差 ~1.6°。
        /// 上游那条门只有 `CA &lt; a_t/25`（= 220 km），而 `DeltaVToMatchVelocities` 取的是两条轨道
        /// **各自位置**上的速度之差 ⇒ 位置差 `d` 带进 `≈ v·d/r` 的**假 Δv**（G2 实测：219.6 km 的接近
        /// ⇒ 交付组件 `R/T/N = 430.01/−0.78/0.00` 纯径向）⇒ 该几何应落到**霍曼/相位**
        /// （同场景政策预估：模式=相位 Δv≈213 m/s）。
        /// ⚠ **判据取「不许选那两级」而不去把几何调到「擦线」那一档**：我连续三次合成都没成
        ///   （相交轨道的 CA 对相位不敏感、同心圆的相位漂移太慢…）⇒ **判政策比判场景可靠**；
        ///   「该级仍能在合法时机出手」由既有正对照 `MatchAtClosestApproach_NearEntry`（拦截后重问）守住。
        /// </summary>
        [Fact]
        public void FarButShallowApproach_MustNotMatchAtClosestApproach()
        {
            const double R = 5_500_000.0;      // 与验收场景同半径
            var self = Circular(R, 0.0);
            // ⚠ 目标必须放在**30° 相位差**处（`Tilted` 的起点也在 +x ⇒ 直接用它两船会**重叠**：
            //   实测那样间距 0 km ⇒ 正确地走了“就地匹配”，用例自己就废了 —— 构造也要先验）。
            var tgt = TiltedBefore(R, 1.64, self.period * 30.0 / 360.0);
            (V3 rs, V3 vs) = self.RightHandedStateVectorsAtUT(0.0);
            (V3 rt, V3 vt) = tgt.RightHandedStateVectorsAtUT(0.0);
            double dist0 = (rt - rs).magnitude;
            double relVel = (vt - vs).magnitude;
            _out.WriteLine($"[阶梯] 同轨异相位：间距 {dist0 / 1000.0:F0} km（相对速度 {relVel:F1} m/s）");

            var rung = RendezvousLadder.MakePlan(self, tgt, 0.0, P, out List<PlanNode> nodes, out string st);
            _out.WriteLine($"[阶梯] ⇒ {rung}：{st}");
            Assert.NotEqual(RendezvousLadder.Rung.MatchAtClosestApproach, rung);
            Assert.NotEqual(RendezvousLadder.Rung.MatchVelocitiesNow, rung);
            // ⚠ 这条只能断言“**不许选速度匹配那两级**”（上面两行）。
            //   具体落到哪一级取决于几何（本例带 1.64° 倾角差 ⇒ 先走 `MatchPlanes`，
            //   修完面再重问才到霍曼/相位 —— 同一条阶梯的生命周期）。
            Assert.Contains(rung, new[] { RendezvousLadder.Rung.MatchPlanes, RendezvousLadder.Rung.Hohmann,
                RendezvousLadder.Rung.PhasingOrbit, RendezvousLadder.Rung.Intercept });
            Assert.True(nodes.Count > 0, "应给出节点");
        }
    }
}
