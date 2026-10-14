using System;
using System.Collections.Generic;
using System.Text;
using MechJebLib.Functions;
using MechJebLib.TwoBody;
using MechJebLib.Primitives;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **一键验收：机动规划器（P1-8）**。
    ///
    /// 一次点击 = 铺一条**受控轨道** + 把**全部 12 个操作**各走一遍界面背后那条链
    /// （**参数描述 → `MakeNodes` → `TryQueuePlan`**）+ 打判决块 + 最后**把规划器窗口开给你看**。
    ///
    /// ## 它验什么 / 不验什么
    /// 验：发现数、每个操作的名字/参数/时间选择器、参数**经描述层写入**后能算出计划、
    /// 计划真的能进队列（队列计数 +ΔN）、以及"算不出"时必须**带理由**（不是静默）。
    /// **不验**：窗口长什么样、好不好用——那**没有可断言的形式** ⇒ 判决块末尾固定打一条
    /// `UNCHECKED` 并列好看点，**不假装测过**（0 样本与看不见的东西都不许报 PASS）。
    ///
    /// ## 参数值从哪来
    /// 表里写的是**显示单位**（km / 度 / 比值），写进去走的是 `Operation.GetParams()` 的 `Set`
    /// —— **与界面是同一条路**。所以这张表一旦与某个操作的参数个数/语义脱节，判据会立刻红。
    /// ⚠ 远心点/近心点是**高度**（内部会加中心天体半径），而半长轴是**绝对半径**（上游口径）。
    /// </summary>
    public static class PlanEditorAcceptanceRunner
    {
        private const int EARTH_BODY_ID = 1; // 与 DebugShipSpawner / AdapterAcceptanceRunner 同值
        private const double R0 = 7_000_000.0;

        /// <summary>操作名 → 参数值（**显示单位**，按 `GetParams()` 的顺序）。</summary>
        private static readonly Dictionary<string, double[]> ParamTable = new() {
            { "圆化轨道", Array.Empty<double>() },
            { "改近/远心点", new[] { 10000.0, 5000.0 } },    // 远心点高度 / 近心点高度（km）
            { "改倾角", new[] { 45.0 } },                     // 度
            { "改远心点", new[] { 12000.0 } },               // 高度 km（当前远心高度 8000）
            { "改近心点", new[] { 3000.0 } },                // 高度 km（当前近心高度 2000）
            { "改半长轴", new[] { 11000.0 } },               // **绝对半径** km（当前 a = 10000）
            { "改偏心率", new[] { 0.05 } },
            { "共振轨道", new[] { 3.0, 2.0 } },               // 分子 / 分母
            { "改升交点经度", new[] { 123.0 } },              // 度
            { "改拱点地面经度（下一圈）", new[] { 77.0 } },   // 度
            { "静止轨道（同步）", new[] { 45.0 } },            // 度
            { "对齐速度", Array.Empty<double>() },                    // 目标 = 自己 ⇒ 期望 Δv ≈ 0
        };

        // 最近一次的判据数与结果（供统一入口收进结论；`Run()` 里是局部量，末尾回填）
        private static int lastChecks, lastFailures, lastOpSucceeded, lastOpRefused;
        /// <summary>最近一次的判据数 / 失败数（0 判据 ⇒ 调用方按 UNCHECKED 处理）。</summary>
        public static int LastChecks => lastChecks;
        public static int LastFailures => lastFailures;
        public static int LastOpSucceeded => lastOpSucceeded;
        public static int LastOpRefused => lastOpRefused;

        public static void Run()
        {
            var sb = new StringBuilder();
            int checks = 0, failures = 0, opSucceeded = 0, opRefused = 0;

            void Check(bool ok, string label, string detail)
            {
                checks++;
                if (!ok) failures++;
                sb.Append("\n  ").Append(ok ? "✓" : "✗ **FAIL**").Append(' ').Append(label);
                if (!string.IsNullOrEmpty(detail)) sb.Append('｜').Append(detail);
            }

            sb.Append("[规划器验收] ═══ P1-8 一键验收（参数描述 → 计算 → 入队）═══");

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || celestialMgr == null)
            {
                Log.Warning("[规划器验收] 不在太空场景（缺 SpaceGameComp / CelestialBodyManager）");
                return;
            }

            double mu = SpaceConstants.PrimaryGravitationalParameter;
            CelestialBodyDef? earth = celestialMgr.GetBodyById(EARTH_BODY_ID);
            if (earth != null && earth.gravitationalParameter > 0.0) mu = earth.gravitationalParameter;
            double bodyR = earth?.radius ?? SpaceConstants.PlanetRadiusMeters;

            double tick = TimeScaleManager.GetPhysicsTickSafe();
            double vCirc = Math.Sqrt(mu / R0);

            // ── A. 铺一条受控轨道：倾角 30° 的椭圆 e=0.3（**非赤道且有拱点** ⇒ 大多数操作都有得算）──
            double inc = Math.PI / 6;
            double vA = Math.Sqrt(1.3) * vCirc; // r·v = 0 ⇒ e = v²R/μ − 1 = 0.3 ⇒ a = 1.0e7（近心 7000km／远心 13000km）
            SpaceEntityGroup? g = DebugShipSpawner.CreateNoMapShipGroup(spaceGame, EARTH_BODY_ID,
                new DVector3(R0, 0, 0), new DVector3(0, vA * Math.Sin(inc), -vA * Math.Cos(inc)),
                out int _, overThrust: true, circularOrbit: false);

            if (g == null)
            {
                sb.Append("\n\n[规划器验收] **UNCHECKED** —— 受控编队没铺出来（工质 Def 缺失？见日志 Error）。这不是通过。");
                Log.Message(sb.ToString());
                return;
            }

            sb.Append("\n  受控轨道：G").Append(g.GroupId)
              .Append(" 倾角 30° 椭圆 e=0.3｜mu=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{mu:E3}")
              .Append(" R0=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(R0 / 1000.0):F0}").Append("km")
              .Append(" tick=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{tick:F0}");

            if (!OrbitAdapter.TryToOrbitView(g, celestialMgr, tick, out OrbitView ov))
            {
                sb.Append("\n\n[规划器验收] **UNCHECKED** —— 拿不到 OrbitView。这不是通过。");
                Log.Message(sb.ToString());
                return;
            }

            // ── B. 发现层：反射在**游戏程序集**里扫到的操作（与单元测试是两条独立路径）──
            sb.Append("\n\n── B. 发现（游戏程序集内的反射）──");
            Operation[] ops = Operation.GetAvailableOperations();
            Check(ops.Length >= 12, "发现全部操作", $"实得 {ops.Length} 个：{string.Join("、", Array.ConvertAll(ops, o => o.GetName()))}");

            // ── C. 逐个操作：参数描述 → 计算 → 入队 ──
            sb.Append("\n\n── C. 每个操作走一遍界面那条链 ──");

            foreach (Operation op in ops)
            {
                string name = op.GetName();
                IReadOnlyList<PlanParam> ps = op.GetParams();

                if (ps.Count == 0 && op.GetTimeSelector() == null)
                {
                    Check(false, $"{name}：界面上有得可改", "既无参数也无时间选择器");
                    continue;
                }

                if (!ParamTable.TryGetValue(name, out double[]? values))
                {
                    Check(false, $"{name}：本验收认识它", "参数表里没有它 ⇒ 新操作请补进 ParamTable()");
                    continue;
                }

                if (values.Length != ps.Count)
                {
                    Check(false, $"{name}：参数个数与验收表一致",
                        $"操作有 {ps.Count} 个参数、验收表给了 {values.Length} 个 ⇒ 表已过期");
                    continue;
                }

                // ① 经描述层写值（界面就是这么写的）
                for (int i = 0; i < ps.Count; i++)
                    ps[i].Set(values[i]);

                // ② 写读往返（描述层的 Get/Set 必须对称）
                bool roundTrip = true;
                for (int i = 0; i < ps.Count; i++)
                    if (Math.Abs(ps[i].Get() - values[i]) > 1e-6) roundTrip = false;

                Check(roundTrip, $"{name}：参数写读往返",
                    roundTrip ? $"{ps.Count} 个参数" : "某个参数写进去读出来不一样");

                // ③ 计算（目标：对齐速度需要目标 ⇒ 用自己当目标，期望 Δv ≈ 0）
                List<PlanNode>? nodes = op.MakeNodes(ov, tick, new FixedTargetView(ov));
                string reason = op.GetErrorMessage() ?? "";

                if (nodes == null)
                {
                    opRefused++;
                    Check(!string.IsNullOrEmpty(reason), $"{name}：拒绝时带理由（不是静默）",
                        string.IsNullOrEmpty(reason) ? "既没计划也没理由" : $"『{reason}』");
                    continue;
                }

                opSucceeded++;
                bool shapeOk = nodes.Count > 0;
                double maxDv = 0.0;
                foreach (PlanNode n in nodes)
                {
                    maxDv = Math.Max(maxDv, n.dV.magnitude);
                    if (double.IsNaN(n.ut) || double.IsInfinity(n.ut) || n.ut < 0.0) shapeOk = false;
                    if (double.IsNaN(n.dV.x) || double.IsNaN(n.dV.y) || double.IsNaN(n.dV.z)) shapeOk = false;
                }

                Check(shapeOk, $"{name}：产出的节点可入队",
                    $"{nodes.Count} 条｜最大 |Δv|={$"{maxDv:F2}"} m/s" +
                    (nodes.Count > 0 ? $"｜首次 t={$"{nodes[0].ut:F0}"}s" : ""));

                if (name == "对齐速度")
                    Check(maxDv < 1e-6, "对齐速度（目标=自己）应当几乎不用烧", $"最大 |Δv|={maxDv:E3} m/s");

                // ④ 真的排进队列（这就是界面上那颗「送入机动队列」按钮做的事）
                int before = g.plannedManeuvers.Count;
                // 带上"意图"（目标条件）：P2 活重解的输入，走的是与界面同一条参数通道
                PlanIntent intent = PlanIntent.Capture(op, absoluteTick: nodes[0].ut);
                bool queued = OrbitAdapter.TryQueuePlan(g, ov, nodes, out string why, out string summary, intent);
                int after = g.plannedManeuvers.Count;
                Check(queued && after == before + nodes.Count, $"{name}：排进队列",
                    queued ? $"队列 {before} → {after}｜{summary}" : $"入队失败：{why}");

                if (queued && after > before)
                {
                    PlanIntent? stored = g.plannedManeuvers[after - 1].intent;
                    Check(stored != null && stored.operationName == name && stored.paramValues.Length == values.Length,
                        $"{name}：意图随条目进了队列",
                        stored == null ? "队列条目上没有意图" : stored.DescribeSelf());
                }

                g.ClearManeuvers(); // 每条验完就清，不给玩家留一堆测试机动
            }

            // ── C2. 执行侧那条路：意图活重解（P2）——**在游戏环境里做漂移实验**，不用人看日志 ──
            sb.Append("\n\n── C2. 意图活重解（执行层的同一条路：TryResolveIntentForBurn）──");
            {
                // 用「圆化」当判据载体：目标是"圆化后 e ≈ 0"，偏不偏一眼可判。
                // 意图的绝对点火时刻取**当前** tick ⇒ 就地比较（不用等真实时间流逝）。
                var op2 = new OperationCircularize();
                op2.TimeSelector.TrySetTimeRef(TimeReference.X_FROM_NOW);
                op2.TimeSelector.LeadTime.Val = 0.0;

                List<PlanNode>? n2 = op2.MakeNodes(ov, tick, new FixedTargetView(ov));
                if (n2 == null || n2.Count == 0)
                {
                    Check(false, "C2 铺场：圆化能算出计划", op2.GetErrorMessage() ?? "无计划");
                }
                else
                {
                    PlanIntent it2 = PlanIntent.Capture(op2, absoluteTick: tick);
                    g.ClearManeuvers();
                    bool queued2 = OrbitAdapter.TryQueuePlan(g, ov, n2, out string w2, out string s2, it2);
                    Check(queued2 && g.plannedManeuvers.Count == 1, "C2 铺场：带意图入队", w2 + s2);
                    // ⚠ 这一条**先留着**（下面执行层入口要用它，上一版就是提前清掉才报"没有意图"）
                }

                // ── 漂移实验：**合成**一条漂移后的轨道（不碰船，避免"改了哪儿根本没人读"这类假实验）──
                // §2.5 那条语义差别的游戏内版本：同一条意图、同一个点火时刻，只换状态。
                //
                // ⚠ 上一版这里是直接改 `g.velocityBody` —— 但 `TryToOrbitView` 读的是
                // `g.CurrentOrbit.velocity`（适配器注释写的 positionBody/velocityBody 与实现不符，
                // **实现以 `CurrentOrbit` 为准**）⇒ 漂移根本没生效：两条对照的 |Δv| 一模一样（749.269）
                // 就是这个假实验的指纹。**两个本该不同的读数完全相同，先怀疑仪器。**
                (V3 r0, V3 v0) = ov.RightHandedStateVectorsAtUT(tick);
                var ovDrift = OrbitView.FromStateVectors(r0, v0 + 5.0 * v0.normalized, mu, tick,
                    ov.equatorialAxis, ov.bodyRadius, ov.soiRadiusApprox, ov.angularVelocity);

                List<PlanNode>? frozenPlan = n2; // 规划那一刻（漂移前）算出的计划 = "冻结向量"
                List<PlanNode>? livePlan = PlanIntent.Capture(op2, absoluteTick: tick)
                    .ReSolve(ovDrift, tick, new FixedTargetView(ovDrift), out string whyLive);

                if (frozenPlan == null || livePlan == null)
                {
                    Check(false, "C2 漂移后两边都能算出计划",
                        $"冻结={(frozenPlan == null ? "无" : "有")} 活重解={(livePlan == null ? whyLive : "有")}");
                }
                else
                {
                    (V3 rD, V3 vD) = ovDrift.RightHandedStateVectorsAtUT(tick);
                    double eFrozen = Astro.EccFromStateVectors(mu, rD, vD + frozenPlan[0].dV);
                    double eLive = Astro.EccFromStateVectors(mu, rD, vD + livePlan[0].dV);

                    Check(eLive < 1e-6,
                        "C2 ⭐ 漂移后按活轨道重解：仍能圆化（e ≈ 0）",
                        $"e={eLive:E3}｜|Δv|={livePlan[0].dV.magnitude:F3} m/s");
                    Check(eFrozen > 1e-5,
                        "C2 ⭐ 对照：照烧规划那一刻的冻结向量会偏（这正是要治的病）",
                        $"e={eFrozen:E3}｜|Δv|={frozenPlan[0].dV.magnitude:F3} m/s（漂移 5 m/s）");
                }

                // ── 执行层入口：拿**队列里那条带意图的机动**（执行层进预点火窗口时走的就是它）──
                if (g.plannedManeuvers.Count > 0)
                {
                    SpaceEntityGroup.PlannedManeuver pmIn = g.plannedManeuvers[0];
                    bool ok = OrbitAdapter.TryResolveIntentForBurn(g, pmIn, tick,
                        out SpaceEntityGroup.PlannedManeuver res, out string why2);
                    Check(ok && res.intentResolved, "C2 执行层入口 TryResolveIntentForBurn 可用",
                        ok ? $"R/T/N {res.radialDV:F2}/{res.tangentialDV:F2}/{res.normalDV:F2}"
                             + $"｜世界系 Δv=({res.plannedDvSystem.x:F2},{res.plannedDvSystem.y:F2},{res.plannedDvSystem.z:F2})"
                             : why2);

                    if (ok)
                    {
                        // 状态没变 ⇒ 重解必须**复现原值**（否则"重解"就成了"改计划"）
                        double dvBefore = Math.Sqrt(pmIn.radialDV * pmIn.radialDV + pmIn.tangentialDV * pmIn.tangentialDV
                                                    + pmIn.normalDV * pmIn.normalDV);
                        double dvAfter = Math.Sqrt(res.radialDV * res.radialDV + res.tangentialDV * res.tangentialDV
                                                   + res.normalDV * res.normalDV);
                        Check(Math.Abs(dvAfter - dvBefore) < 1e-3 * Math.Max(1.0, dvBefore),
                            "C2 状态未变时重解复现原值（往返）", $"|Δv| {dvBefore:F2} → {dvAfter:F2} m/s");

                        // 执行层那一行写回（`group.plannedManeuvers[0] = pm`）必须真的改到队列里的条目
                        g.plannedManeuvers[0] = res;
                        Check(g.plannedManeuvers[0].intentResolved, "C2 写回队列条目生效", "intentResolved=true");
                    }
                }
                else
                {
                    Check(false, "C2 执行层入口：队列里应有带意图的条目", "队列是空的（铺场失败？）");
                }

                g.ClearManeuvers();

                // ── 不重解的四条路：必须**退回冻结向量并给出理由**，不许静默 ──
                {
                    var plain = new SpaceEntityGroup.PlannedManeuver { radialDV = 1f, tangentialDV = 0f, normalDV = 0f };
                    bool r1 = OrbitAdapter.TryResolveIntentForBurn(g, plain, tick, out _ , out string why1);
                    Check(!r1 && !string.IsNullOrEmpty(why1), "C2 无意图 ⇒ 不重解且给理由", why1);

                    var needTarget = new SpaceEntityGroup.PlannedManeuver
                    {
                        radialDV = 1f,
                        intent = new PlanIntent { operationName = "对齐速度", targetGroupId = 0, absoluteTick = tick },
                    };
                    bool r2 = OrbitAdapter.TryResolveIntentForBurn(g, needTarget, tick, out _, out string why2b);
                    Check(!r2 && !string.IsNullOrEmpty(why2b), "C2 需要目标 ⇒ 不重解且给理由", why2b);
                }

                g.ClearManeuvers();
            }

            // ── D. 两冲量转移（B2.4）：铺 LEO + GEO 两条船，跑相位窗口 + 两次点火，验"真的交会" ──
            sb.Append("\n\n── D. 两冲量转移（B2.4：相位窗口 + 霍曼；验收线来自计划稿）──");
            {
                const double rLeo = 7.0e6;
                const double rGeo = 4.2e7; // 6 倍半径差（计划稿的验收场景是 4.8 倍）

                double vLeo = Math.Sqrt(mu / rLeo);
                double vGeo = Math.Sqrt(mu / rGeo);

                // 游戏系里 +X 处、顺行 = −Z 方向（与既有受控场景同一约定）
                SpaceEntityGroup? from = DebugShipSpawner.CreateNoMapShipGroup(spaceGame, EARTH_BODY_ID,
                    new DVector3(rLeo, 0, 0), new DVector3(0, 0, -vLeo), out int _, overThrust: true, circularOrbit: true);
                SpaceEntityGroup? to = DebugShipSpawner.CreateNoMapShipGroup(spaceGame, EARTH_BODY_ID,
                    new DVector3(rGeo * Math.Cos(1.9), 0, rGeo * Math.Sin(1.9)),
                    new DVector3(vGeo * Math.Sin(1.9), 0, -vGeo * Math.Cos(1.9)), out int _, overThrust: true, circularOrbit: true);

                if (from == null || to == null)
                {
                    Check(false, "D 铺场：LEO 与 GEO 两条船", "有编队没铺出来（工质 Def 缺失？）");
                }
                else
                {
                    bool planned = OrbitAdapter.TryPlanHohmannTransfer(from, to, celestialMgr, tick,
                        out List<PlanNode>? nodesT, out string whyT);
                    Check(planned && nodesT != null && nodesT.Count == 2, "D 规划：产出两次点火", planned ? whyT : whyT);

                    if (planned && nodesT != null && nodesT.Count == 2 &&
                        OrbitAdapter.TryToOrbitView(to, celestialMgr, tick, out OrbitView ovToT))
                    {
                        PlanNode n1 = nodesT[0], n2 = nodesT[1];

                        // ⚠ 教训：**别用未来 tick 去构造 OrbitView**——`TryToOrbitView(g, mgr, tick)` 里那个 tick
                        // 只用来减去**中心天体在该时刻的偏移量**，它**不传播轨道** ⇒ 传未来 tick 得到的是
                        // "拿现在的轨道、换了个错的参考系原点" ✗（第一版就是这么错的：判据差 3.5e7 m）。
                        // 正确：用**当前** tick 构造，再用 `RightHandedStateVectorsAtUT` 传播到出发时刻。
                        if (!OrbitAdapter.TryToOrbitView(from, celestialMgr, tick, out OrbitView ovFromAtT))
                            ovFromAtT = ovToT; // 兜底：拿不到就退回用目标轨道（不该发生）

                        // 验收线①：等待 ≤ 0.5 个目标周期（计划稿原文）
                        Check(n1.ut - tick <= 0.5 * ovToT.period,
                            "D 验收线：等待 ≤ 半个目标周期",
                            $"等待 {n1.ut - tick:F0}s / 半周期 {0.5 * ovToT.period:F0}s");

                        // 验收线②③：两个冲量施加后**真的交会**（位置与速度都要对上）
                        // ⚠ 在**适配层参考系**里验（`Shepperd.Solve` 吃 V3；`PlanNode.dV` 也在这个系里）
                        // ⇒ 不来回换系，避免"换错一次就静默错"（本文件已经踩过同类坑）。
                        (V3 rb, V3 vb) = ovFromAtT.RightHandedStateVectorsAtUT(n1.ut);
                        (V3 rt, V3 vt) = Shepperd.Solve(mu, n2.ut - n1.ut, rb, vb + n1.dV);
                        (V3 rTgt, V3 vTgt) = ovToT.RightHandedStateVectorsAtUT(n2.ut);

                        double posErr = (rt - rTgt).magnitude;
                        double velErr = (vt + n2.dV - vTgt).magnitude;
                        Check(posErr < 1e-6 * rTgt.magnitude, "D 验收线：到达点位置交会",
                            $"位置差 {posErr:E3} m（{rTgt.magnitude / 1000.0:F0} km 处）");
                        Check(velErr < 1e-6 * vTgt.magnitude, "D 验收线：到达点速度对上",
                            $"速度差 {velErr:E3} m/s");

                        double dvTotal = n1.dV.magnitude + n2.dV.magnitude;
                        Check(dvTotal <= 3291.0, "D 验收线：Δv ≤ 3291 m/s（1.15× 霍曼）",
                            $"总 Δv {dvTotal:F0} m/s｜t_tof {n2.ut - n1.ut:F0}s");
                    }
                }

                from?.ClearManeuvers();
                to?.ClearManeuvers();
            }

            lastChecks = checks;
            lastFailures = failures;
            lastOpSucceeded = opSucceeded;
            lastOpRefused = opRefused;

            // ── E. 结论 ──
            sb.Append("\n\n── D. 结论 ──");
            sb.Append("\n  判据 ").Append(checks).Append(" 条，失败 ").Append(failures).Append(" 条");
            sb.Append("\n  操作：算出计划 ").Append(opSucceeded).Append(" 个 / 明确拒绝 ").Append(opRefused)
              .Append(" 个（**拒绝也算通过**——前提是它给了理由）");
            sb.Append("\n  ⚠ **UNCHECKED：窗口长什么样、好不好用，本探针无法断言**。");
            sb.Append("\n     需要人看一眼（窗口已自动打开）：① 左列 12 项是否都显示、不截断");
            sb.Append("\n     ② 「点火时刻」一排按钮会不会挤/换行难看 ③ 参数行「标签 + 输入框 + 单位」是否对齐");
            sb.Append("\n     ④ 换个操作时右列是否跟着变 ⑤ 「计算计划」→「送入机动队列」是否给出人话反馈");
            sb.Append("\n  总判定：").Append(failures == 0 ? "PASS（仅覆盖可断言部分）" : "**FAIL**");

            Log.Message(sb.ToString());

            // ── E. 顺手把规划器开给你看（这就是本次要人眼确认的东西）──
            Find.WindowStack.Add(new Window_PlanEditor(g));
        }
    }
}
