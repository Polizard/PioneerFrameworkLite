using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **相位三点实验**（2026-09-20）：3 条船、同一条轨道、同一个节点，**唯一变量 = 点火相位**。
    ///
    /// 为什么是这三点：台架（同政策同窗口）给出相位→e 的曲线
    ///   −4s → **1.474e-3**｜0s → **5.96e-4**｜+2s → 2.67e-4｜+4s → 4.52e-4
    /// ⇒ 若游戏在 **同一条曲线上** ⇒ **残差就是点火相位的 tick 量化**，那 B2.2 的弧补偿就是正解（不是别的未知机制）。
    ///
    /// 三条船：
    ///   P1 强制 −4 s（台架预测 **e 最大**，应为 P2 的 ~2.5 倍）
    ///   P2 强制  0 s（台架预测 5.96e-4）
    ///   P3 自动扫描（**生产路径**）+ 它自己日志里的"模型预测 e" ⇒ 直接给"实测 vs 预测"
    ///
    /// **判读（事先登记）**：
    ///   ① P1 明显大于 P2 ⇒ **相位是残差的控制量** ✓（B2.2 弧补偿为解）；
    ///   ② P3 的 实测/预测 ≤ 2 ⇒ **模型=执行器** ✓；仍差 ~4 倍 ⇒ 还有一项没建模，另查；
    ///   ③ 三点差不多 ⇒ **撤回"相位=残差"这条结论**，改查其它。
    ///
    /// ⚠ **2026-09-20 结果与回写**：① 成立（P1/P2 = 1.69）✓；② 当时"实测/预测 ≈ 2.1"确实是"另一项没建模"
    /// ——那项**已经找到并修掉**了（**一拍框架滞后**：方向在本拍算、冲量下一拍才并入速度 ⇒ 台架同仪器
    /// "理想 −0.174° / 加一拍滞后 −0.2647°"，游戏实测 −0.266°）。修复后本表**居中档 = 9.99e-4 / Δa +5.79 km**，
    /// **且 P2 与 P3 逐位相同（9.992e-4）** ⇒ 测量可复现（本调查里第一次）；★绝对曲线的**最小值现在落在节点上
    /// （0.001°）**，修复前是"最小值晚 10 s、偏 0.5°"。
    /// 残留的 5.96e-4 → 9.99e-4 那一段 = 已登记的游戏独有 ~0.2% 幅值缺口（切向 ~1.3 m/s ⇒ Δa ≈ +3.9 km），
    /// **不再追**：真飞验收里由**闭环微调**按构造吸收（实测 e = 9.6e-5）。
    /// 本表的"台架预测"列保留的是**滞后修复之前**的数（引以为戒），别当现在的期望。
    ///
    /// 船数 = 3 ⇒ 负载噪声回到 ~0.35% 量级；φ 各异 ⇒ 三条互不重叠（不共点、不互撞）。
    /// </summary>
    public static class BurnPhaseAcceptanceRunner
    {
        private const int EARTH_BODY_ID = 1;
        private const double R0 = 7_000_000.0;
        private const double Lead = 300.0;

        private sealed class Arm
        {
            public string Name = "";
            public double ForcedShift = double.NaN;
            public int GroupId = -1;
            public double NodeTick = -1.0;
            public double ExpectedSma = -1.0;
            public bool Captured;
            public double E, A, DA, Applied;
        }

        private static readonly List<Arm> arms = new();
        private static bool pending;
        private static int stagedTick;                     // 登记时刻（真实 tick）——超时判据用
        /// <summary>等真实 tick 上限（见 LiveBurn 里同名字段的理由：不许永久静默）。</summary>
        private const int TimeoutRealTicks = 60000;

        public static void Stage()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (sg == null || mgr == null) { Log.Warning("[相位三点] 不在太空场景"); return; }

            arms.Clear();
            double mu = SpaceConstants.PrimaryGravitationalParameter;
            CelestialBodyInfo? earth = mgr.GetBodyById(EARTH_BODY_ID);
            if (earth != null && earth.gravitationalParameter > 0.0) mu = earth.gravitationalParameter;

            var sb = new StringBuilder();
            sb.Append("[相位三点] ═══ 3 条船：同轨同节点，只差点火相位（−4s / 0s / 自动）═══");
            double[] shifts = [-4.0, 0.0, double.NaN];
            for (int i = 0; i < shifts.Length; i++)
            {
                string nm = double.IsNaN(shifts[i]) ? "P3自动" : ("P" + (i + 1) + "强制" + $"{shifts[i]:+0;-0}" + "s");
                double phi = 0.7 * i;   // φ 各异 ⇒ 不共点
                var pos = new DVector3(R0 * Math.Cos(phi), 0.0, R0 * Math.Sin(phi));
                double v = Math.Sqrt(1.3) * Math.Sqrt(mu / R0);
                var vel = new DVector3(v * Math.Sin(phi), 0.0, -v * Math.Cos(phi));

                var g = DebugShipSpawner.CreateNoMapShipGroup(sg, EARTH_BODY_ID, pos, vel,
                    out int _, overThrust: true, circularOrbit: false);
                if (g == null) { sb.Append("\n  ").Append(nm).Append("：铺船失败"); continue; }
                if (!double.IsNaN(shifts[i])) g.abForcePhaseShift = shifts[i];
                // 关掉闭环微调（B2.2-b）：这条实验测的正是"相位差留下多少残差"⇒ 微调会把它抹平。
                g.trimEnabled = false;

                double tick = TimeScaleManager.GetPhysicsTickSafe();
                if (!OrbitAdapter.TryToOrbitView(g, mgr, tick, out OrbitView ov))
                { sb.Append("\n  ").Append(nm).Append("：拿不到 OrbitView"); continue; }

                var op = new OperationCircularize();
                op.TimeSelector.TrySetTimeRef(TimeReference.X_FROM_NOW);
                op.TimeSelector.LeadTime.Val = Lead;
                List<PlanNode>? nodes = op.MakeNodes(ov, tick, new FixedTargetView(ov));
                if (nodes == null || nodes.Count == 0) { sb.Append("\n  ").Append(nm).Append("：算不出计划"); continue; }

                var intent = PlanIntent.Capture(op, absoluteTick: nodes[0].ut);
                if (!OrbitAdapter.TryQueuePlan(g, ov, nodes, out string why, out _, intent))
                { sb.Append("\n  ").Append(nm).Append("：排队失败 ").Append(why); continue; }

                arms.Add(new Arm
                {
                    Name = nm, ForcedShift = shifts[i], GroupId = g.GroupId,
                    NodeTick = nodes[0].ut, ExpectedSma = ov.Radius(nodes[0].ut)
                });
                sb.Append("\n  ").Append(nm.PadRight(10)).Append(" G").Append(g.GroupId)
                  .Append("  节点=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{nodes[0].ut:F0}");
            }
            sb.Append("\n  ⇒ 烧完自动出表（每行含 点火偏移 与 模型预测 e）");
            Log.Message(sb.ToString());
            Messages.Message($"相位三点：已布置 {arms.Count} 条船", MessageTypeDefOf.NeutralEvent);
            pending = arms.Count > 0;
            stagedTick = Find.TickManager?.TicksGame ?? 0;
        }

        /// <summary>每物理步：各船烧完并过节点后**各自**记账（避免读取时刻差异）。</summary>
        public static void Tick()
        {
            if (!pending) return;
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (sg == null || mgr == null) return;
            double tick = TimeScaleManager.GetPhysicsTickSafe();
            int nowReal = Find.TickManager?.TicksGame ?? 0;
            bool timedOut = nowReal - stagedTick > TimeoutRealTicks;

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
                a.Applied = g.appliedBurnDV;
                a.Captured = true;
            }
            if (!allDone && timedOut)
            {
                int n = 0;
                foreach (var a in arms) if (!a.Captured) n++;
                Log.Message($"[相位三点] **UNCOVERED**：{TimeoutRealTicks} 真实 tick 内仍有 {n} 条手臂没跑完 ⇒ 下面按**已捕获**的手臂汇总");
                pending = false;
                Report();
                return;
            }
            if (!allDone) return;
            pending = false;
            Report();
        }

        public static void Report()
        {
            var sb = new StringBuilder();
            sb.Append("[相位三点] ═══ 表 ═══");
            sb.Append("\n  船          e           Δa(km)     已烧      台架预测 e");
            foreach (var a in arms)
            {
                string pred = double.IsNaN(a.ForcedShift) ? "见 [对照·相位] 行"
                    : (a.ForcedShift < -3.0 ? "lag 前 1.474E-3" : "lag 前 5.960E-4");
                sb.Append("\n  ").Append(a.Name.PadRight(10)).Append(' ').Append(System.Globalization.CultureInfo.CurrentCulture, $"{a.E:E3}")
                  .Append("  ").Append($"{a.DA:+0.00;-0.00;0.00}".PadLeft(9))
                  .Append("  ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{a.Applied:F1}").Append("   ").Append(pred);
            }
            sb.Append("\n  ⇒ 判读：P1 明显大于 P2 ⇒ 相位是残差的控制量（B2.2 弧补偿为解）✓；");
            sb.Append("\n          P3 的 实测/预测 ≤2 ⇒ 模型=执行器 ✓；三点差不多 ⇒ 撤回「相位=残差」✗");
            // ★ 2026-09-20 回写：本表的"台架预测"列是**滞后修复之前**的数 ⇒ 别拿它当现在的期望。
            //   修复后（三条船都没开微调）：居中档实测 ≈ **1.0e-3**、Δa ≈ **+5.8 km**；
            //   与台架"理想执行器"行的 5.96e-4/+1.87 km 之差 = **那条已登记的游戏独有 ~0.2% 幅值缺口**
            //   （切向约 1.3 m/s ⇒ Δa ≈ +3.9 km，与"每 2.8 m/s 对 +8.5 km"自洽）；
            //   开了微调时该缺口被按构造吸收 ⇒ e≈1e-4（真飞验收里实测 9.6e-5）。
            sb.Append("\n  ⇒ 【2026-09-20 后的期望】居中档 ≈1.0e-3 / Δa≈+5.8 km"
                + "（= 理想 5.96e-4 + ~0.2% 幅值缺口；开微调时被吸收 ⇒ ~1e-4）");
            Log.Message(sb.ToString());
            Messages.Message("相位三点：表已出（见日志）", MessageTypeDefOf.TaskCompletion);
        }
    }
}
