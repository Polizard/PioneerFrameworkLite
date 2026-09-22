using System;
using System.Collections.Generic;
using System.Text;
using MechJebLib.Primitives;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **P0 适配层一键验收**（2026-09-18）：一次点击 = 铺受控场景 + 跑全套判据 + 打结论块。
    ///
    /// ## 为什么是"同步跑完"而不是像 <see cref="PerfAcceptanceRunner" /> 那样分阶段 tick 驱动
    /// 本批的判据全是**同步**的：验的是"计划算得对 + 能排进队列"。
    /// **燃烧的执行**不在这里验——那一段由既有一键验收的「燃烧②③」阶段覆盖，重复验没有新信息。
    /// 所以这里一次跑完、直接给结论，不需要进度窗口。
    ///
    /// ## 它会改动游戏状态（两处，都是刻意的）
    /// ① 铺 4 个**受控轨道**的编队（这样判据不依赖"玩家场景里恰好有什么"）；
    /// ② 在其中一个上**真的排一条机动**（"端到端"的定义就是它得真的进队列）。
    /// ⇒ 只挂在调试页签上、由人点。
    ///
    /// ## 判据分五组
    /// **A 铺场** · **B F1 换系**（游戏 +Y ↔ 适配层 +z）· **C 构造点**（刚铺出来就能用，R5）
    /// · **D 端到端**（`OperationCircularize`：真 Δv / 平面保持 / 双曲报错）· **E 队列** · **F 静态契约**
    ///
    /// ⚠ **样本为 0 时结论是 `UNCHECKED`，不是 PASS**（2026-09-18 的教训：`失败数 == 0` 在 0 样本下平凡成立）。
    /// </summary>
    public static class AdapterAcceptanceRunner
    {
        private const int EARTH_BODY_ID = 1; // 与 DebugShipSpawner 同值
        private const double R0 = 5_500_000.0; // LEO：地表 500km（行星半径 5000km）

        private static int checks;
        private static int failures;
        private static readonly StringBuilder sb = new StringBuilder();

        private static void Check(bool ok, string label, string detail)
        {
            checks++;
            if (!ok) failures++;
            sb.Append("\n  ").Append(ok ? "✓" : "✗ **FAIL**").Append(' ').Append(label);
            if (!string.IsNullOrEmpty(detail)) sb.Append("｜").Append(detail);
        }

        private static void Info(string label, string detail) =>
            sb.Append("\n  · ").Append(label).Append("｜").Append(detail);

        private static DVector3 ToD(V3 v) => new DVector3(v.x, v.y, v.z);

        public static void Run()
        {
            checks = 0;
            failures = 0;
            sb.Clear();

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || celestialMgr == null)
            {
                Log.Warning("[适配层验收] 不在太空场景（缺 SpaceGameComp / CelestialBodyManager）");
                return;
            }

            double mu = SpaceConstants.PrimaryGravitationalParameter;
            CelestialBodyDef? earth = celestialMgr.GetBodyById(EARTH_BODY_ID);
            if (earth != null && earth.gravitationalParameter > 0.0)
                mu = earth.gravitationalParameter;
            double bodyR = earth?.radius ?? SpaceConstants.PlanetRadiusMeters;

            double tick = TimeScaleManager.GetPhysicsTickSafe();
            double vCirc = Math.Sqrt(mu / R0);
            double vEsc = Math.Sqrt(2.0 * mu / R0);

            sb.Append("[适配层验收] ═══ P0 适配层一键验收（同步：铺场 → F1 → 构造点 → 端到端 → 队列 → 契约）═══");
            sb.Append("\n  参数：bodyId=").Append(EARTH_BODY_ID)
              .Append("  mu=").Append(mu.ToString("E3"))
              .Append("  R0=").Append((R0 / 1000.0).ToString("F0")).Append("km")
              .Append("  v_circ=").Append(vCirc.ToString("F1")).Append("m/s")
              .Append("  tick=").Append(tick.ToString("F0"));

            // ═══ A. 铺场：四个受控轨道（都取非圆轨模式，除了第一个）═══
            sb.Append("\n\n── A. 铺场（受控轨道；这些组的 (r,v) 是我指定的 ⇒ 期望值是闭式的）──");

            var groups = new List<(string name, SpaceEntityGroup g, double expectedIncDeg, double expectedEcc)>();

            // A1 赤道圆轨：h = +Y（游戏系极轴）⇒ 适配层 inc = 0
            SpaceEntityGroup? g1 = Spawn(spaceGame, new DVector3(R0, 0, 0), new DVector3(0, 0, -vCirc), circular: true);
            // A2 赤道椭圆 e=0.3：r·v = 0 ⇒ e = v²R/μ − 1 = 1.3 − 1
            SpaceEntityGroup? g2 = Spawn(spaceGame, new DVector3(R0, 0, 0), new DVector3(0, 0, -Math.Sqrt(1.3) * vCirc), circular: false);
            // A3 倾角 30° 的椭圆 e=0.3：方向 (0, sin30, −cos30) ⇒ inc = 30°
            double inc = Math.PI / 6;
            SpaceEntityGroup? g3 = Spawn(spaceGame, new DVector3(R0, 0, 0),
                new DVector3(0, Math.Sqrt(1.3) * vCirc * Math.Sin(inc), -Math.Sqrt(1.3) * vCirc * Math.Cos(inc)), circular: false);
            // A4 双曲 e = 2.92：v = 1.4·v_esc 且 r·v = 0
            SpaceEntityGroup? g4 = Spawn(spaceGame, new DVector3(R0, 0, 0), new DVector3(0, 0, -1.4 * vEsc), circular: false);

            bool spawned = g1 != null && g2 != null && g3 != null && g4 != null;
            Check(spawned, "铺出 4 个受控编队", spawned
                ? $"G{g1!.GroupId}(圆轨) G{g2!.GroupId}(椭圆) G{g3!.GroupId}(倾角30椭圆) G{g4!.GroupId}(双曲)"
                : "有编队没铺出来（工质 Def 缺失？见日志 Error）");
            if (!spawned)
            {
                // 铺场都失败 ⇒ 后面的判据没有样本，直接 UNCHECKED 收尾（不许报 PASS）
                Verdict(spawnedGroups: 0);
                Log.Message(sb.ToString());
                return;
            }

            groups.Add(("赤道圆轨", g1!, 0.0, 0.0));
            groups.Add(("赤道椭圆 e=0.3", g2!, 0.0, 0.3));
            groups.Add(("倾角30° 椭圆 e=0.3", g3!, 30.0, 0.3));
            groups.Add(("赤道双曲 e=2.92", g4!, 0.0, 2.92));

            foreach ((string name, SpaceEntityGroup g, double incDeg, double ecc) in groups)
            {
                if (!OrbitAdapter.TryToOrbitView(g, celestialMgr, tick, out OrbitView ov))
                {
                    Check(false, $"{name}：能构造 OrbitView", "TryToOrbitView 返回 false");
                    continue;
                }

                Check(Math.Abs(ov.eccentricity - ecc) < 1e-6,
                    $"{name}：偏心率符合设定",
                    $"实得 {ov.eccentricity:E3}（期望 {ecc:F3}）");
                Check(Math.Abs(ov.inclination - incDeg * Math.PI / 180.0) < 1e-9,
                    $"{name}：倾角符合设定",
                    $"实得 {ov.inclination * 180 / Math.PI:F4}°（期望 {incDeg:F1}°）");
            }

            // ═══ C. 构造点（R5）：刚铺出来的组就能算对 ═══
            // 这一组是**本次改动的正面证据**：`CreateNoMapShipGroup` 的非圆轨分支**不设** `localPosition`，
            // 所以"从 localPosition 构造"在这里会读到零。能过 ⇒ 构造点确实是"现算 CurrentOrbit"。
            sb.Append("\n\n── C. 构造点（R5）：铺完立刻构造，不经任何物理步 ──");
            foreach ((string name, SpaceEntityGroup g, double incDeg, double ecc) in groups)
            {
                double lpMag = g.localPosition.magnitude;
                DVector3 cur = g.CurrentOrbit.position - g.GetCentralBodyOffset((int)Math.Round(tick));
                Info($"{name}：localPosition 与 CurrentOrbit 派生值的模长",
                    $"|localPosition|={lpMag / 1000.0:F1}km  |CurrentOrbit 派生|={cur.magnitude / 1000.0:F1}km"
                    + (lpMag < 1.0 ? "  ⚠ localPosition 未同步（新铺组的已知行为，正是构造点不用它的理由）" : ""));
            }

            // ═══ B. F1 换系：对**全部**活动编队 ═══
            sb.Append("\n\n── B. F1 换系（游戏 +Y 极轴 ↔ 适配层 +z 极轴）──");
            int f1Samples = 0, f1Fail = 0, nonZeroInc = 0;
            foreach (SpaceEntityGroup g in spaceGame.GetAllGroups())
            {
                if (!g.IsActive) continue;
                CelestialBodyDef? def = celestialMgr.GetBodyById(g.CelestialBodyId);
                if (def == null) continue;

                int tk = (int)Math.Round(tick);
                DVector3 gp = g.CurrentOrbit.position - g.GetCentralBodyOffset(tk);
                DVector3 gv = g.CurrentOrbit.velocity - g.GetCentralBodyVelocity(tk);
                DVector3 hGame = DVector3.Cross(gp, gv);
                if (hGame.sqrMagnitude <= 0.0) continue;

                f1Samples++;
                DVector3 hHat = hGame.normalized;
                double incGame = Math.Acos(Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(hHat, DVector3.up))));
                if (incGame > 1e-6) nonZeroInc++;

                if (!OrbitAdapter.TryToOrbitView(g, celestialMgr, tick, out OrbitView ov)) { f1Fail++; continue; }

                // ① 往返恒等（适配层 → 游戏系）
                DVector3 backR = ToD(OrbitView.AdapterToGameWorld(ov.r));
                DVector3 backV = ToD(OrbitView.AdapterToGameWorld(ov.v));
                double rtErr = Math.Max((backR - gp).magnitude / Math.Max(gp.magnitude, 1.0),
                    (backV - gv).magnitude / Math.Max(gv.magnitude, 1.0));
                // ② 倾角一致（游戏系独立算）
                double incDiff = Math.Abs(ov.inclination - incGame);
                // ③ 法向一致（适配层法向换回游戏系 vs 直接叉乘）
                DVector3 nBack = ToD(OrbitView.AdapterToGameWorld(ov.OrbitNormal()));
                double nErr = (nBack - hHat).magnitude;

                if (rtErr >= 1e-12 || incDiff >= 1e-9 || nErr >= 1e-9)
                {
                    f1Fail++;
                    sb.Append("\n  ✗ **FAIL** G").Append(g.GroupId)
                      .Append("｜①").Append(rtErr.ToString("E1"))
                      .Append(" ②").Append((incDiff * 180 / Math.PI).ToString("E2")).Append("°")
                      .Append(" ③").Append(nErr.ToString("E1"));
                }
            }

            Check(f1Samples > 0 && f1Fail == 0, "F1 换系：全部编队 ①往返/②倾角/③法向 一致",
                $"样本 {f1Samples} 组，失败 {f1Fail} 组");
            // ⚠ 非零倾角样本必须 ≥1：否则 ② 是"全 0 对全 0"的空判据
            Check(nonZeroInc >= 1, "F1 ② 有非零倾角样本（判据不是空的）",
                $"非零倾角样本 {nonZeroInc} / {f1Samples} 组" + (nonZeroInc >= 1 ? "" : "  ⚠ 全是赤道轨 ⇒ 倾角判据未被行使"));

            // ═══ D. 端到端：OperationCircularize ═══
            sb.Append("\n\n── D. 端到端 OperationCircularize（远心点圆化）──");

            // D1 赤道椭圆：真 Δv，圆周化后 e ≈ 0、sma = ApR、h 方向不变
            RunCircularize("赤道椭圆", g2!, celestialMgr, tick, expectPlan: true, expectPlaneKept: true);
            // D2 倾角30° 椭圆：同上，且**倾角必须不变**（圆化是共面机动）
            RunCircularize("倾角30° 椭圆", g3!, celestialMgr, tick, expectPlan: true, expectPlaneKept: true);
            // D3 赤道圆轨：已经圆了 ⇒ Δv 应当 ≈ 0（"别把好轨道弄坏"）
            RunCircularize("赤道圆轨", g1!, celestialMgr, tick, expectPlan: true, expectPlaneKept: true);
            // D4 双曲：没有远心点 ⇒ 框架必须把它变成"计划不可做 + 错误消息"，而不是抛出去
            {
                if (OrbitAdapter.TryToOrbitView(g4!, celestialMgr, tick, out OrbitView ovHyp))
                {
                    var opHyp = new OperationCircularize();
                    opHyp.TimeSelector._currentTimeRef = 0; // APOAPSIS
                    List<PlanNode>? nodes = opHyp.MakeNodes(ovHyp, tick, new FixedTargetView(ovHyp));
                    Check(nodes == null && !string.IsNullOrEmpty(opHyp.GetErrorMessage()),
                        "双曲 + 远心点：计划不可做且给出错误消息",
                        nodes == null ? $"ErrorMessage=『{opHyp.GetErrorMessage()}』" : "居然产出了节点");
                }
            }

            // ═══ E. 队列：真的排进去 ═══
            sb.Append("\n\n── E. 队列（真 Δv 的那一条）──");
            {
                if (OrbitAdapter.TryToOrbitView(g2!, celestialMgr, tick, out OrbitView ovE))
                {
                    int before = g2!.plannedManeuvers.Count;
                    List<PlanNode>? nodes = PlanCircularize(ovE, tick, g2.GroupId);
                    // ⚠ 不能写成 `nodes != null && TryQueuePlan(..., out why, out sum)`——
                    //    短路求值会让 out 变量在 nodes == null 时**未赋值**（CS0165）。
                    bool queued = false;
                    string why = "", sum = "";
                    if (nodes != null)
                        queued = OrbitAdapter.TryQueuePlan(g2, ovE, nodes, out why, out sum);
                    int after = g2.plannedManeuvers.Count;

                    Check(queued && after == before + nodes!.Count, "计划排进队列",
                        queued ? $"队列 {before} → {after}｜{sum}" : $"入队失败：{why}");

                    if (queued && after > before)
                    {
                        SpaceEntityGroup.PlannedManeuver m = g2.plannedManeuvers[after - 1];
                        DVector3 w = m.plannedWorldDv;
                        bool finite = !double.IsNaN(w.x) && !double.IsNaN(w.y) && !double.IsNaN(w.z);
                        Check(finite && m.active, "队列条目的 plannedWorldDv 有限且 active",
                            $"plannedWorldDv=({w.x:F2},{w.y:F2},{w.z:F2}) active={m.active} executeTick={m.executeTick}");
                        Check(Math.Abs(m.executeTick - nodes![0].ut) < 1.0, "executeTick 与计划时刻一致",
                            $"executeTick={m.executeTick} 计划 ut={nodes![0].ut:F1}");
                        Info("（这条会真的烧；不想烧可在轨道面板取消）", "");
                    }
                }
            }

            // ═══ F. 静态契约 ═══
            sb.Append("\n\n── F. 静态契约 ──");
            {
                Operation[] all = Operation.GetAvailableOperations();
                Check(Array.Exists(all, o => o is OperationCircularize), "反射发现包含 OperationCircularize",
                    $"共发现 {all.Length} 个操作");
            }
            {
                var e = new EditableDoubleMult(200000, 1000);
                bool ok1 = Math.Abs((double)e - 200000.0) < 1e-6 && e.Text == "200";
                e.Text = "425";
                bool ok2 = e.Parsed && Math.Abs(e.Val - 425000.0) < 1e-6;
                e.Text = "not a number";
                bool ok3 = !e.Parsed && Math.Abs(e.Val - 425000.0) < 1e-6; // 解析失败时存值不变
                Check(ok1 && ok2 && ok3, "EditableDoubleMult：乘数口径 + Invariant 解析 + 失败保值",
                    $"显示『200』/ 解析『425』→ 425000 / 非法输入后仍 {e.Val:F0}");
            }
            {
                PlanConfidence c0 = PlanConfidence.From(0.0, 0.0, R0, 600.0);
                PlanConfidence c1 = PlanConfidence.From(10000.0, 0.0, R0, 600.0);
                PlanConfidence c2 = PlanConfidence.From(0.0, 600.0, R0, 600.0);
                Check(c1.value < c0.value && c2.value < c0.value && !PlanConfidence.Unusable.IsUsable,
                    "PlanConfidence：σ/年龄 单调不增，Unusable 不可用",
                    $"σ↑ {c0.value:F4}→{c1.value:F4}　年龄↑ {c0.value:F4}→{c2.value:F4}");
            }
            {
                var selector = new TimeSelector(new[] { TimeReference.CLOSEST_APPROACH });
                selector._currentTimeRef = 0;
                bool threw = false;
                try { selector.ComputeManeuverTime(OrbitAdapter.TryToOrbitView(g2!, celestialMgr, tick, out OrbitView ovS) ? ovS : default,
                    tick, new StaleTargetStub()); }
                catch (OperationException) { threw = true; }
                Check(threw, "目标过期/无解算天体：判为不可做（不回退真值）", "走 OperationException 路径");
            }

            Verdict(spawnedGroups: groups.Count);
            Log.Message(sb.ToString());
        }

        // ── 辅助 ────────────────────────────────────────────────────────────────

        private static SpaceEntityGroup? Spawn(SpaceGameComp sg, DVector3 pos, DVector3 vel, bool circular) =>
            DebugShipSpawner.CreateNoMapShipGroup(sg, EARTH_BODY_ID, pos, vel, out _, overThrust: true, circularOrbit: circular);

        /// <summary>跑一次"远心点圆化计划"，并按需断言预测结果。不改游戏状态。</summary>
        private static void RunCircularize(string name, SpaceEntityGroup g, CelestialBodyManager mgr,
            double tick, bool expectPlan, bool expectPlaneKept)
        {
            if (!OrbitAdapter.TryToOrbitView(g, mgr, tick, out OrbitView ov))
            {
                Check(false, $"{name}：能构造 OrbitView", "TryToOrbitView 返回 false");
                return;
            }

            List<PlanNode>? nodes = PlanCircularize(ov, tick, g.GroupId);
            if (expectPlan)
                Check(nodes != null && nodes.Count == 1, $"{name}：产出 1 条计划", nodes == null ? "无计划" : $"{nodes.Count} 条");
            if (nodes == null || nodes.Count == 0) return;

            PlanNode node = nodes[0];
            OrbitView after = OrbitAdapter.ApplyForPreview(ov, node);

            // 判据一：圆化后偏心率 ≈ 0
            Check(after.eccentricity < 1e-9, $"{name}：预测 e ≈ 0",
                $"e {ov.eccentricity:E3} → {after.eccentricity:E3}｜|Δv|={node.dV.magnitude:F2}m/s");

            // 判据二：sma = 那一刻的半径（圆化的定义）
            double rAt = ov.RightHandedBCIPositionAtUT(node.ut).magnitude;
            Check(Math.Abs(after.semiMajorAxis - rAt) / rAt < 1e-9, $"{name}：预测 a = 那一刻的半径",
                $"a={after.semiMajorAxis / 1000:F1}km vs |r|={rAt / 1000:F1}km");

            // 判据三：共面（h 方向不变）—— 圆化只改速度大小与径向，不该改轨道面
            if (expectPlaneKept)
            {
                V3 hBefore = ov.OrbitNormal();
                V3 hAfter = after.OrbitNormal();
                double dAng = Math.Acos(Math.Max(-1.0, Math.Min(1.0, V3.Dot(hBefore, hAfter)))) * 180 / Math.PI;
                Check(dAng < 1e-6, $"{name}：轨道面不变", $"法向夹角 {dAng:E2}°");
            }
        }

        /// <summary>对给定轨道跑"远心点圆化"，返回计划（失败返回 null）。</summary>
        private static List<PlanNode>? PlanCircularize(OrbitView ov, double tick, int groupId)
        {
            var op = new OperationCircularize();
            op.TimeSelector._currentTimeRef = 0; // APOAPSIS
            return op.MakeNodes(ov, tick, new FixedTargetView(ov, groupId));
        }

        private static void Verdict(int spawnedGroups)
        {
            sb.Append("\n\n[适配层验收] ═══════════ 结论 ═══════════");
            if (checks == 0)
            {
                sb.Append("\n[适配层验收] **UNCHECKED** —— 一条判据都没跑成（铺场失败：" + spawnedGroups + " 组）。这不是通过。");
                return;
            }

            sb.Append("\n[适配层验收] 判据 ").Append(checks).Append(" 条，失败 ").Append(failures).Append(" 条");
            sb.Append("\n[适配层验收] ═══ 总判定：").Append(failures == 0 ? "PASS" : "**FAIL**").Append(" ═══");
            if (failures == 0)
                sb.Append("\n[适配层验收] 覆盖：换系(F1) · 构造点(R5) · 端到端圆周化(含平面保持) · 双曲错误契约 · 队列 · 静态契约");
        }

        /// <summary>永远给不出目标的 <see cref="ITargetView" />——模拟"接触过期"。台架用。</summary>
        private sealed class StaleTargetStub : ITargetView
        {
            public bool TryOrbit(double ut, out OrbitView orbit) { orbit = default; return false; }
            public double PositionSigma => 5000.0;
            public double DataAgeSeconds => 3600.0;
            public int IdentityGroupId => -1;
            public int IdentityDecoyId => -1;
            public double ApparentRadius => 0.0;
            public PlanConfidence Confidence => PlanConfidence.Unusable;
        }
    }
}
