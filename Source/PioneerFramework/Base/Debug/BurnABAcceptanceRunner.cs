using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **矩阵式对照验收（用户 2026-09-19 要求：用一大批不同轨道消除偶然）**。
    ///
    /// 设计：**4 条不同轨道 × 3 个变体 + 1 个同轨重复**（共 13 条船）：
    ///   · 轨道：r₀ = 7000 / 6600 / 8500 / 7800 km，近心角 φ 各不同（**不同位置 ⇒ 不互撞、不共点**）；
    ///   · 变体：A 基准 / C 冻结方向（关掉"按当前 RTN 逐点重取"）/ B 无轨道保持；
    ///   · 额外在同一条轨道上**重复一次基准** ⇒ 直接给出"同条件重复"的散布 = **噪声地板** ✓。
    ///   · 节点错开（lead = 300 + i·40 s）⇒ 燃烧彼此错峰，避免 6× 负载同时打满。
    ///
    /// **判据（事先登记）**：
    ///   ① **同轨重复那一对**的 e/Δa 之差 = **噪声地板**；任何"变体效应"必须**明显大于**它才作数；
    ///   ② **变体 C（冻结方向）在 4 条轨道上是否一致地优于 A** ⇒ 若一致 ⇒ 方向律被定罪（且不是某条轨道的巧合）；
    ///   ③ **变体 B（无保持）在 4 条轨道上是否一致地差于 A** ⇒ 若是 ⇒ "轨道保持=RCS 维二体"这条被量化，
    ///      且残差里属于它的份额可见（用户问的那条路 ✓）。
    /// 汇总按**变体**给中位数（对轨道取中位），并按**轨道**给一列 —— 两个方向都能看一致性。
    /// </summary>
    public static class BurnABAcceptanceRunner
    {
        private const int EARTH_BODY_ID = 1;

        private sealed class Arm
        {
            public string Variant = "";
            public double R0;
            public double Lead;
            public int GroupId = -1;
            public double NodeTick = -1.0;
            public double ExpectedSma = -1.0;
            public bool Captured;
            public double E, A, DA, Inc, Argp, Applied;
        }

        private static readonly List<Arm> arms = new List<Arm>();
        private static bool pending;

        private static readonly double[] Orbits = { 7_000_000.0, 6_600_000.0, 8_500_000.0, 7_800_000.0 };
        private static readonly string[] Variants = { "A基准", "C冻结方向", "D全油门", "B无轨道保持" };

        public static void Stage()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (sg == null || mgr == null) { Log.Warning("[矩阵对照] 不在太空场景"); return; }

            arms.Clear();
            BurnAB.Reset();
            double mu = SpaceConstants.PrimaryGravitationalParameter;
            CelestialBodyDef earth = mgr.GetBodyById(EARTH_BODY_ID);
            if (earth != null && earth.gravitationalParameter > 0.0) mu = earth.gravitationalParameter;

            var sb = new StringBuilder();
            sb.Append("[矩阵对照] ═══ 4 轨道 × 3 变体 + 同轨重复（共 13 条；位置/半径/时间全错开）═══");

            // ⚠ **所有船同一个 lead** ⇒ 同一个**真近点角** ⇒ 同一形状的机动（各 r₀ 各自的闭合解）
            //   ⇒ **唯一变量 = 变体** ✓✓。
            //   上一版我把 lead 错峰成 300+i·40 ⇒ **节点不同 ⇒ 机动不同** ⇒ 整张表不可比 ✗✗
            //   （实测 `已烧` 从 771 到 1040 全不一样 ⇒ 那是"各烧各的"，不是噪声）。
            //   本轮也已证：13 条船同时在场的负载**不影响基线**（A@7000 与单船历轮逐位一致 ✓）。
            const double Lead = 300.0;
            for (int oi = 0; oi < Orbits.Length; oi++)
                for (int vi = 0; vi < Variants.Length; vi++)
                    Spawn(sg, mgr, mu, Orbits[oi], Variants[vi], Lead, sb);
            Spawn(sg, mgr, mu, Orbits[0], "A基准(重复)", Lead, sb);   // **真·同轨同变体同节点的重复** ⇒ 噪声地板 ✓

            sb.Append("\n  ⇒ 全部烧完自动出表（按轨道分行、按变体汇总中位数）");
            Log.Message(sb.ToString());
            Messages.Message($"矩阵对照：已布置 {arms.Count} 条船，烧完自动出表", MessageTypeDefOf.NeutralEvent);
            pending = arms.Count > 0;
        }

        private static void Spawn(SpaceGameComp sg, CelestialBodyManager mgr, double mu,
            double r0, string variant, double lead, StringBuilder sb)
        {
            // ⚠ φ **只随轨道变**（同轨道的所有变体同 φ ⇒ 同一条"扰动几何" ⇒ 变体 vs 基准不受 φ 干扰 ✓）。
            //   上一版 φ 随*每条船*变 ⇒ 同轨道的三个变体处于不同扰动几何 ⇒ 万一有影响就混淆 ✗。
            //   （本轮已证 φ 影响很小：A@7000 φ=0 与 重复 φ=121° 的 Δa 差 0.4% ✓，但顺手做干净。）
            double phi = 0.7 * Array.IndexOf(Orbits, r0);
            var pos = new DVector3(r0 * Math.Cos(phi), 0.0, r0 * Math.Sin(phi));
            double v = Math.Sqrt(1.3) * Math.Sqrt(mu / r0);                       // e = 0.3（任意 r₀ 通用）
            var vel = new DVector3(v * Math.Sin(phi), 0.0, -v * Math.Cos(phi));   // 顺行切向（与 φ=0 时同约定）

            var g = DebugShipSpawner.CreateNoMapShipGroup(sg, EARTH_BODY_ID, pos, vel,
                out int _, overThrust: true, circularOrbit: false);
            if (g == null) { sb.Append("\n  ").Append(variant).Append(" @").Append((r0 / 1000).ToString("F0")).Append("：铺船失败"); return; }

            if (variant.StartsWith("C")) g.abFreezeDirection = true;
            if (variant.StartsWith("B")) g.HasOrbitKeeping = false;
            if (variant.StartsWith("D")) g.abFullThrottle = true;
            // ⚠ **矩阵一律关掉闭环微调**（B2.2-b）：微调会按构造把残差收掉（e → ~1e-5），
            //   而这张矩阵存在的意义正是**看各政策各自留下多少残差** ⇒ 开着它 = 把要测的信号抹平。
            //   微调的效果由「真飞验收」那条船（默认开）单独测。
            g.trimEnabled = false;

            double tick = TimeScaleManager.GetPhysicsTickSafe();
            if (!OrbitAdapter.TryToOrbitView(g, mgr, tick, out OrbitView ov))
            { sb.Append("\n  ").Append(variant).Append("：拿不到 OrbitView"); return; }

            var op = new OperationCircularize();
            op.TimeSelector.TrySetTimeRef(TimeReference.X_FROM_NOW);
            op.TimeSelector.LeadTime.Val = lead;
            List<PlanNode>? nodes = op.MakeNodes(ov, tick, new FixedTargetView(ov));
            if (nodes == null || nodes.Count == 0) { sb.Append("\n  ").Append(variant).Append("：算不出计划"); return; }

            var intent = PlanIntent.Capture(op, absoluteTick: nodes[0].ut);
            if (!OrbitAdapter.TryQueuePlan(g, ov, nodes, out string why, out _, intent))
            { sb.Append("\n  ").Append(variant).Append("：排队失败 ").Append(why); return; }

            arms.Add(new Arm
            {
                Variant = variant, R0 = r0, Lead = lead, GroupId = g.GroupId,
                NodeTick = nodes[0].ut, ExpectedSma = ov.Radius(nodes[0].ut)
            });
            sb.Append("\n  ").Append(variant.PadRight(10)).Append(" r₀=").Append((r0 / 1000).ToString("F0"))
              .Append("km  G").Append(g.GroupId).Append("  节点=").Append(nodes[0].ut.ToString("F0"));
        }

        /// <summary>每物理步：每条船**各自**烧完并过节点就抓一次单（避免读取时刻不同带来差异）。</summary>
        public static void Tick()
        {
            if (!pending) return;
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (sg == null || mgr == null) return;
            double tick = TimeScaleManager.GetPhysicsTickSafe();

            bool allDone = true;
            foreach (var a in arms)
            {
                if (a.Captured) continue;
                var g = sg.GetGroupById(a.GroupId);
                if (g == null) { a.Captured = true; continue; }
                if (g.HasActiveBurn || g.appliedBurnDV <= 0.0 || tick < a.NodeTick + 5.0) { allDone = false; continue; }
                if (!OrbitAdapter.TryToOrbitView(g, mgr, tick, out OrbitView ov)) { a.Captured = true; continue; }
                a.E = ov.eccentricity;
                a.A = ov.semiMajorAxis;
                a.DA = (ov.semiMajorAxis - a.ExpectedSma) / 1000.0;
                a.Inc = ov.inclination * 180.0 / Math.PI;
                a.Argp = ov.argumentOfPeriapsis * 180.0 / Math.PI;
                a.Applied = g.appliedBurnDV;
                a.Captured = true;
            }
            if (!allDone) return;
            pending = false;
            Report();
        }

        public static void Report()
        {
            var sb = new StringBuilder();
            sb.Append("[矩阵对照] ═══ 表（按轨道分行）═══");
            sb.Append("\n  变体         r₀(km)  e          Δa(km)     i(°)     argp(°)   已烧");
            foreach (var a in arms)
                sb.Append("\n  ").Append(a.Variant.PadRight(11)).Append(" ").Append((a.R0 / 1000).ToString("F0").PadLeft(5))
                  .Append("  ").Append(a.E.ToString("E3")).Append("  ")
                  .Append(a.DA.ToString("+0.00;-0.00;0.00").PadLeft(8)).Append("  ")
                  .Append(a.Inc.ToString("F4")).Append("  ").Append(a.Argp.ToString("F1").PadLeft(7))
                  .Append("  ").Append(a.Applied.ToString("F1"));

            // 按变体汇总（中位数：对轨道取中位 ⇒ 不被某条轨道牵着走）
            sb.Append("\n\n  ── 按变体汇总（中位数）──");
            foreach (var v in new[] { "A基准", "C冻结方向", "B无轨道保持", "A基准(重复)" })
            {
                var es = new List<double>(); var das = new List<double>();
                foreach (var a in arms) if (a.Variant == v && a.Captured) { es.Add(a.E); das.Add(a.DA); }
                if (es.Count == 0) continue;
                es.Sort(); das.Sort();
                sb.Append("\n  ").Append(v.PadRight(12)).Append(" n=").Append(es.Count)
                  .Append("  e 中位=").Append(es[es.Count / 2].ToString("E3"))
               
                  .Append("  Δa 中位=").Append(das[das.Count / 2].ToString("+0.00;-0.00;0.00"));
            }
            sb.Append("\n  ⇒ 判据：①「A基准(重复)」与「A基准」之差 = 噪声地板，变体效应必须明显大于它；");
            sb.Append("\n          ② C 在 4 条轨道上一致优于 A ⇒ 方向律定罪；③ B 一致差于 A ⇒ 轨道保持/二体维持那条被量化 ✓");
            Log.Message(sb.ToString());
            Messages.Message("矩阵对照：表已出（见日志）", MessageTypeDefOf.TaskCompletion);
        }
    }
}

