using System;
using System.Collections.Generic;
using System.Text;
using MechJebLib.Primitives;
using PioneerFramework.Pure.Tests;      // 台架（CircularTarget）
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **A.2 第一步："计划何时该变"的显式口径 + 触发阈度量**（2026-10-01 用户裁定"做，分两步"）。
    ///
    /// ## 现场（§10.14 结论 3）
    /// 长滑行段里每 30 s 一拍的重规划给出的是**同一份计划**（点火绝对时刻漂 ≤0.012 s、Δv 逐位不变）
    /// ⇒ 那些拍是重复劳动。要把它改成**事件触发**（行为改动）之前，先把两件事做出来（**不改行为**）：
    ///
    /// ### ① 口径（**唯一权威** = 本文件的 `PlanDiffReason`，另一条台架 `LadderEndToEndTests.G7` 共用）
    /// "同一份计划" = **级相同** + 节点数相同 + 逐发在**动作意义**下等价：
    /// · **立即发**（相对时刻 ≤ 一个节拍）：时间意义就是"现在" ⇒ 比 **Δv**（Δv 相同即同一动作）；
    /// · **排期发**：比 **绝对时刻**（≤ `UtTol`）+ Δv。
    /// ⚠ **第一版拿绝对时刻一刀切 ⇒ 量错了**：同轨调相那两组每拍都回"15 s 后点火、Δv 逐位相同"，
    ///   绝对时刻每拍 +30 s 被误判成"每拍都变"。教训 = 先问"这个量的时间意义是什么"再定容差口径。
    ///
    /// ### ② 度量：本文件走"**每拍都问**"（oracle 面）
    /// 沿一条纯滑行每 30 s 问一次 `MakePlan`（两条状态推进到**同一时刻** —— §10.14 踩过的坑），
    /// 统计真变化 / 白问 / 各触发档读数。⚠ 它**不是生产语义**（生产只在**队列空**时才重规划）⇒
    /// 真实"能省多少次"的读数在 `LadderEndToEndTests.G7`（生产语义、四条冻结几何）。
    ///
    /// ## 判据（事先登记）
    ///   ① 每拍 `MakePlan` 都必须返回（不抛、不 NaN）；
    ///   ② 读数打出来（本条要的是**度量**，不是 PASS/FAIL）；
    ///   ③ 状态推进必须**两条同时刻**（本文件用解析二体推进两条 —— 这是 §10.14 仪器事故的直接防线）。
    /// </summary>
    public class LadderReplanPolicyTests
    {
        private readonly ITestOutputHelper _out;
        public LadderReplanPolicyTests(ITestOutputHelper o) => _out = o;

        private static readonly double Mu = OrbitMath.GM;
        private const double BodyRadius = 6_000_000.0;
        private static readonly DVector3 OrbitNormal = new DVector3(0, -1, 0);   // 顺行约定（与其它台架同一套）

        private const double Cadence = 30.0;      // 现状节拍（`ladderMinReplanSeconds`）
        private const int Ticks = 60;             // 60 拍 = 30 min 滑行
        /// <summary>"同一份计划"的容差：绝对时刻（s）。</summary>
        public const double UtTol = 1.0;
        /// <summary>"同一份计划"的容差：Δv 逐分量（m/s）。</summary>
        public const double DvTol = 1e-3;
        /// <summary>"立即发"的相对时刻上限（s）：≤ 它 ⇒ 时间意义 = 现在（比 Δv，不比时刻）。</summary>
        public const double ImmediateLead = 30.0;

        // ══════════════════════════════════════════════════════════════════════════════
        // **口径（唯一权威）**：`LadderEndToEndTests.G7` 也用这一份 ⇒ 别在别处再写一遍判据
        // ══════════════════════════════════════════════════════════════════════════════

        /// <summary>两份"重规划指纹"在动作意义下等价吗？null = 同一份计划（= 那一拍是**白问**）。</summary>
        public static string? PlanDiffReason(RendezvousLadder.Rung ra, IReadOnlyList<(double Ut, DVector3 Dv)> a, double ta,
            RendezvousLadder.Rung rb, IReadOnlyList<(double Ut, DVector3 Dv)> b, double tb)
        {
            if (ra != rb) return $"级 {ra}→{rb}";
            if (a.Count != b.Count) return $"节点数 {a.Count}→{b.Count}";
            for (int i = 0; i < a.Count; i++)
            {
                DVector3 d = a[i].Dv - b[i].Dv;
                double dDv = Math.Max(Math.Abs(d.x), Math.Max(Math.Abs(d.y), Math.Abs(d.z)));
                if (dDv > DvTol)
                    return $"第{i}发 Δv 差 {dDv:E2} m/s（|Δv| {a[i].Dv.magnitude:F3}→{b[i].Dv.magnitude:F3}）";

                bool aImmediate = a[i].Ut - ta <= ImmediateLead + UtTol;
                bool bImmediate = b[i].Ut - tb <= ImmediateLead + UtTol;
                if (aImmediate != bImmediate) return $"第{i}发「立即↔排期」变了";
                if (!aImmediate)
                {
                    double dUt = Math.Abs(a[i].Ut - b[i].Ut);
                    if (dUt > UtTol) return $"第{i}发（排期）时刻差 {dUt:F2}s（{a[i].Ut:F1}→{b[i].Ut:F1}）";
                }
            }
            return null;
        }

        /// <summary>便捷重载（对 `LadderPilot.ReplanRecord`）。</summary>
        public static string? PlanDiffReason(LadderPilot.ReplanRecord a, LadderPilot.ReplanRecord b)
            => PlanDiffReason(a.Rung, a.Nodes, a.T, b.Rung, b.Nodes, b.T);

        // ══════════════════════════════════════════════════════════════════════════════

        private sealed class Plan
        {
            public double T;
            public RendezvousLadder.Rung Rung;
            public readonly List<(double Ut, DVector3 Dv)> Nodes = new List<(double, DVector3)>();
        }

        private static Plan Ask((DVector3 r, DVector3 v) self, ITarget target, double t)
        {
            (DVector3 r2, DVector3 v2) = (target.PosAt(t), target.VelAt(t));
            var selfOv = OrbitView.FromStateVectors(self.r, self.v, Mu, t, OrbitNormal, BodyRadius);
            var tgtOv = OrbitView.FromStateVectors(r2, v2, Mu, t, target.PlaneNormalAt(t), BodyRadius);
            var p = new RendezvousLadder.Params { MinRadius = BodyRadius + 100_000.0 };
            var plan = new Plan { T = t };
            plan.Rung = RendezvousLadder.MakePlan(selfOv, tgtOv, t, p, out List<PlanNode> nodes, out _);
            foreach (var n in nodes)
            {
                Assert.True(!double.IsNaN(n.ut) && !double.IsNaN(n.dV.x), "MakePlan 给出了 NaN 节点");
                plan.Nodes.Add((n.ut, n.dV));
            }
            return plan;
        }

        private static (DVector3 r, DVector3 v) CoastTo((DVector3 r, DVector3 v) s, double t) =>
            (OrbitMath.GetPositionAtTimeDelta(s.r, s.v, t, Mu), OrbitMath.GetVelocityAtTimeDelta(s.r, s.v, t, Mu));

        private void Run(string name, (DVector3 r, DVector3 v) self0, ITarget target)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"── {name} ──");

            Plan? prev = null;
            var reasons = new List<string>();
            var firstTicks = new List<string>();
            double[] taus = { 60.0, 300.0, 900.0 };
            var fired = new Dictionary<double, HashSet<int>>();
            foreach (double tau in taus) fired[tau] = new HashSet<int>();
            var changed = new HashSet<int>();

            for (int k = 0; k < Ticks; k++)
            {
                double t = k * Cadence;
                (DVector3 r, DVector3 v) = CoastTo(self0, t);          // ⚠ 两条状态都推进到 t（同一时刻）
                Plan cur = Ask((r, v), target, t);

                if (k < 5)
                    firstTicks.Add($"     t={t,5:F0}s 级={cur.Rung,-14} 节点={cur.Nodes.Count}" +
                                   (cur.Nodes.Count > 0 ? $"｜首发 ut={cur.Nodes[0].Ut,8:F1}s |Δv|={cur.Nodes[0].Dv.magnitude:F4} m/s" : ""));

                foreach (double tau in taus)
                    if (cur.Nodes.Count > 0 && cur.Nodes[0].Ut - t <= tau) fired[tau].Add(k);

                if (prev != null)
                {
                    string? why = PlanDiffReason(prev.Rung, prev.Nodes, prev.T, cur.Rung, cur.Nodes, cur.T);
                    if (why != null)
                    {
                        changed.Add(k);
                        if (reasons.Count < 3) reasons.Add($"t={t:F0}s：{why}");
                    }
                }
                prev = cur;
            }

            int total = Ticks - 1;
            int white = total - changed.Count;
            sb.AppendLine($"   级序列（{Ticks} 拍 × {Cadence:F0}s）：{prev?.Rung}");
            sb.AppendLine(string.Join("\n", firstTicks));
            sb.AppendLine("   ⚠ 本走法 = **每拍都问**（oracle 面；生产只在队列空时重规划 ⇒ 这里会高估次数）");
            sb.AppendLine($"   真变化 {changed.Count}/{total} 拍｜**白问 {white} 次**（{100.0 * white / total:F0}%）");
            foreach (string s in reasons) sb.AppendLine("     " + s);
            foreach (double tau in taus)
            {
                var F = fired[tau];
                var missed = new List<int>();
                foreach (int c in changed) if (!F.Contains(c)) missed.Add(c);
                sb.AppendLine($"   触发「下一发 ≤ {tau,4:F0}s」：会问 {F.Count} 次（现状问 {Ticks} 次）" +
                              $"｜漏报 {missed.Count} 次（真变化没被覆盖）｜多余 {F.Count - (changed.Count - missed.Count)} 次");
            }
            _out.WriteLine(sb.ToString().TrimEnd());
        }

        [Fact]
        public void ReplanTriggers_MeasureOnly()
        {
            double vc63 = Math.Sqrt(Mu / 6_300_000.0);
            var leo = (new DVector3(6_300_000.0, 0, 0), new DVector3(0, 0, vc63));   // h ∥ +Y 的顺行约定

            Run("① 同轨 15°（6,300 km 圆轨）", leo,
                CircularTarget.Create(6_300_000.0, Mu, 15.0 * Math.PI / 180.0, OrbitNormal));
            Run("② 同轨 120°（6,300 km 圆轨）", leo,
                CircularTarget.Create(6_300_000.0, Mu, 120.0 * Math.PI / 180.0, OrbitNormal));

            double vc55 = Math.Sqrt(Mu / 5_500_000.0);
            var chaser55 = (new DVector3(5_500_000.0, 0, 0), new DVector3(0, 0, vc55));
            Run("③ 现场 5500→6300 km、45°（同面版）", chaser55,
                CircularTarget.Create(6_300_000.0, Mu, 45.0 * Math.PI / 180.0, OrbitNormal));
            Run("④ LEO→GEO 5500→26322 km", chaser55,
                CircularTarget.Create(26_322_000.0, Mu, 30.0 * Math.PI / 180.0, OrbitNormal));
        }
    }
}
