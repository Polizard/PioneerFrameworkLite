using System;
using System.Collections.Generic;
using MechJebLib.Primitives;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **游戏世界 ↔ 适配层的桥**（P-1 §3 的"宿主侧"）。这是唯一允许把 `SpaceEntityGroup` /
    /// `CelestialBodyDef` 变成 `OrbitView` / `BodyView` 的地方。
    ///
    /// ## 三条必须守住的约定
    /// ① **构造点唯一（R5）**：`OrbitView` 只从**一个**状态源构造 —— `TryToOrbitView` 读的是
    ///    **`group.CurrentOrbit.position/velocity`**（再减去中心天体的位置/速度）。
    ///    ⚠ 2026-09-19 更正：本行原写"只从 `positionBody`/`velocityBody` 构造"——**与实现不符**，
    ///    而且真的害人：验收器一度改 `velocityBody` 造"漂移"，因为它根本不是被读的那个字段，
    ///    漂移**静默没发生**（两条本该不同的读数一模一样 = 假实验的指纹）。
    ///    那两个字段是**天体参考系**的（`CurrentOrbit.position` 是**系统系 = 根行星中心**，
    ///    见 `SpaceGameComp:1329` 的注释；`positionBody = CurrentOrbit.position − SyncBodyPos`）。
    ///    **禁止改用 `GetCircularPositionAtTick` 那类圆轨解析函数**——那会引入"第三种状态"。
    /// ② **跨系只剩两个口（批6 S2，2026-09-30）**：批4 P4 极轴统一后适配层与游戏世界同轴 +Y
    ///    ⇒ `OrbitView.GameWorldToAdapter` / `AdapterToGameWorld` 已退化为**恒等**，并**降为 `OrbitView` 私有**。
    ///    宿主侧跨系只准走两个口：**进** `OrbitView.FromGameStateVectors`（游戏系状态 → `OrbitView`）、
    ///    **出** `ov.GameFrameStateAtUT(ut)`（`OrbitView` → 游戏系状态）。
    ///    Δv / 方向这类**非状态**的向量两系同轴 ⇒ 直接传值，**不要在调用点上自己拼变换**。
    ///    ⚠ 但 `OperationInclination` 的 `180° − i` 仍然必须保留 ——
    ///    它换的是**天体自转方向**（游戏侧 `Ry(−ωt)`），不是极轴，见 `OrbitView.FromGameStateVectors` 的说明。
    /// ③ **单位**：`physicsTick` 就是**秒**（物理积分器把 `UPDATE_INTERVAL_TICKS` 当 `dtSeconds` 喂进去，
    ///    1 tick = 1 秒），所以 `epoch` / `PlanNode.ut` / `PlannedManeuver.executeTick` 三者同单位、
    ///    与 `mu`（m³/s²）自洽。**不要在这里做任何时间换算。**
    ///
    /// 【边界】本文件**碰 RimWorld 类型**，所以它是"宿主侧"、**不进**纯测试与 `MechJebLibTest`。
    /// `OrbitView` / `BodyView` / `Operation` 那一侧必须保持纯。
    /// </summary>
    public static class OrbitAdapter
    {
        // 批5 U4（2026-09-28）：原先这里有两个转发到 `OrbitView` 的恒等转换函数，已随类型统一一并删除
        // （移植面三矢量与 `DVector3` 现在是同一个类型 ⇒ 转换这个概念不存在了）。

        // ── 天体数据 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 取天体 Def。查不到时返回 null，**由调用方明确报错**——
        /// 绝不用主天体 GM 兜底（那会把两体问题解成另一个两体问题，且没有任何东西会报警）。
        /// <paramref name="mgr" /> 允许为 null（`Current.Game?.GetComponent<...>()` 本来就可能拿不到）。
        /// </summary>
        public static CelestialBodyDef? GetBody(CelestialBodyManager? mgr, int bodyId) =>
            mgr == null ? null : mgr.GetBodyById(bodyId);

        /// <summary>由天体 Def 填一张 <see cref="BodyView" /> 记录（P-1 §3.2）。</summary>
        public static BodyView ToBodyView(CelestialBodyDef def, CelestialBodyManager? mgr)
        {
            // ⚠ 我们**没有**模型的轴倾角：本 mod 的天体轨道都在世界 XZ 平面内，
            //    所以"赤道轴"取世界的极轴 `DVector3.up`（= +Y；批4 P4 起适配层也是 +Y ⇒ 不再有换系）。
            //    这一点有实测支撑：自检里 15/17 个编队的游戏系倾角正好是 0（见 OrbitViewProbe 的输出）。
            V3 pole = DVector3.up; // 两系同轴（批4 P4）⇒ 直传；跨系只剩 `OrbitView` 的进/出两个口（批6 S2）

            // 自转：1 tick = 1 秒 ⇒ 角速度 = 2π / dayLengthTicks（算式只有一份：AngularVelocity）
            double angularVelocity = AngularVelocity(def);

            // SOI：见 SoiRadiusApprox 的说明。
            double soiApprox = SoiRadiusApprox(def, mgr);

            return new BodyView(
                bodyId: def.bodyId,
                mu: def.gravitationalParameter,
                radius: def.radius,
                atmosphereThickness: def.atmosphereThickness,
                equatorialAxis: pole,
                angularVelocity: angularVelocity,
                rotationAngleT0: 0.0, // ⚠ 地表几何（经纬度/自转相位）还没接；见 P-1 §1.3 更正里的待办
                parentBodyId: def.ParentBodyId,
                soiRadiusApprox: soiApprox);
        }

        /// <summary>
        /// 中心天体的 **SOI 半径近似**（P-1 §6-R3）：`a·(m/M)^(2/5)`，即 Hill 半径。
        ///
        /// ⚠ **这不是真值**：`CelestialBodyDef` 没有 `sphereOfInfluence`，而我们**从不**拿 SOI 判定参考系
        /// （参考系是显式跟踪的：`CelestialBodyId` + `RegisterGroupToBody`）。
        /// 它只服务少量"健全性提醒"，所以名字里带 `Approx`，别当权威用。
        /// 无法近似时返回 `NaN`。
        /// </summary>
        /// <summary>
        /// 中心天体的**自转角速度** (rad/s)：`2π / dayLengthTicks`。
        /// ⚠ 前提是"**1 physics tick = 1 秒**"——这一条是实测钉住的
        /// （物理积分器把 `UPDATE_INTERVAL_TICKS` 当 `dtSeconds` 喂进去），不是假设。
        /// </summary>
        public static double AngularVelocity(CelestialBodyDef def)
        {
            double daySeconds = def.dayLengthTicks > 0.0 ? def.dayLengthTicks : 60000.0;
            return 2.0 * Math.PI / daySeconds;
        }

        public static double SoiRadiusApprox(CelestialBodyDef def, CelestialBodyManager? mgr)
        {
            if (mgr == null || def.ParentBodyId < 0 || !(def.orbitRadius > 0.0)) return double.NaN;
            CelestialBodyDef? parent = mgr.GetBodyById(def.ParentBodyId);
            if (parent == null || !(parent.gravitationalParameter > 0.0)) return double.NaN;
            double massRatio = def.gravitationalParameter / parent.gravitationalParameter;
            return def.orbitRadius * Math.Pow(massRatio, 0.4);
        }

        // ── 群组状态 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 把编队的**当前**状态变成 <see cref="OrbitView" />。查不到中心天体时返回 false——**不猜**
        /// （P-1 §4.2：猜错就是把两体问题解成另一个两体问题，且没有任何东西会报警）。
        ///
        /// ## ⚠ 为什么取 `CurrentOrbit` 而**不是** `positionBody`（R5 的答案，2026-09-18 改）
        /// P-1 §6 R5 的字面要求是“构造点只从 `CurrentOrbit` 取真值”。先前的写法是取
        /// `g.positionBody`，理由是“它按称是 `CurrentOrbit.position − 中心天体偏移` 的派生副本”。
        /// **实测把这个理由推翻了**：`DebugShipSpawner.CreateNoMapShipGroup` 在非圆轨分支**根本不设**
        /// `positionBody`（只设 `CurrentOrbit`，然后 `EnterOrbitKeeping`）⇒ **刚铺出来的编队
        /// `positionBody` 还是零**，要等下一次物理步才同步。
        ///
        /// ⇒ 取字面做法：**现算**。这也正是本项目里已有的口径
        /// （`SpaceGameComp.TryPredictTargetAtTick`：`CurrentOrbit.position − GetCentralBodyOffset(nowTick)`）。
        /// **构造点必须与“这个组是不是刚建的/刚改过”无关。**
        /// </summary>
        public static bool TryToOrbitView(SpaceEntityGroup g, CelestialBodyManager? mgr, double physicsTick, out OrbitView ov)
        {
            ov = default;
            if (g == null)
                return false;

            CelestialBodyDef? def = GetBody(mgr, g.CelestialBodyId);
            if (def == null)
                return false;

            // 系统系 → 天体局部系：**唯一换算**（`GetBodyState` 内部走 `FrameConvert`，且**同一 tick 取两次** ⇒ 位置/速度一定同刻）
            (BodyVec3 bodyPos, BodyVec3 bodyVel) = g.GetBodyState(physicsTick);
            DVector3 r = bodyPos.v;
            DVector3 v = bodyVel.v;

            ov = OrbitView.FromGameStateVectors(
                r, v, def.gravitationalParameter, physicsTick, DVector3.up, def.radius, SoiRadiusApprox(def, mgr),
                AngularVelocity(def));
            return true;
        }

        // 批6 S2（2026-09-30）：原先这里的 `StateAtUT(ov, ut)` 已删除 —— 它是出边换系的**第二个名字**
        // （与 `OrbitView.GameFrameStateAtUT` 同一个概念、同一份实现）⇒ 按"一个概念一个权威"收口，
        // 调用方一律改走 `ov.GameFrameStateAtUT(ut)`。

        // ── 把计划送进队列 ──────────────────────────────────────────────────────

        /// <summary>
        /// 把 `Operation.MakeNodes` 产出的计划**逐条**送进编队的机动队列。
        ///
        /// 每条的换算：**两条系同轴 ⇒ Δv 直传**（批6 S2：跨系只经 `OrbitView` 的进/出两个口），
        /// 再按上游约定同时填**分量**（`OrbitMath.DeltaVSystemToComponents`）与 `plannedDvSystem`
        /// （`PlannedManeuver` 的注释：点火时 dvMag 走分量、实际烧走 plannedDvSystem）。
        ///
        /// 失败时返回 false 并给出原因（队列里**一条都不会**被加入——宁可整条不做，也不要半条计划）。
        /// </summary>
        public static bool TryQueuePlan(SpaceEntityGroup g, OrbitView ov,
            List<PlanNode>? nodes, out string reason, out string summary, PlanIntent? intent = null)
        {
            reason = "";
            summary = "";

            if (nodes == null)
            {
                reason = "规划器没有产出节点（详见其错误消息）";
                return false;
            }

            if (nodes.Count == 0)
            {
                reason = "规划器产出了空计划";
                return false;
            }

            // 先全部换算好再入队：避免"前几条进去了、第 3 条才发现有问题"
            var prepared = new List<SpaceEntityGroup.PlannedManeuver>(nodes.Count);
            var sb = new System.Text.StringBuilder();

            foreach (PlanNode node in nodes)
            {
                if (node.ut < 0.0 || double.IsNaN(node.ut) || double.IsInfinity(node.ut))
                {
                    reason = $"节点时刻非法：{node.ut}";
                    return false;
                }

                DVector3 dvSystem = node.dV; // 两系同轴（批4 P4）⇒ 不经转换；跨系只剩 OrbitView 的进/出两个口
                if (double.IsNaN(dvSystem.x) || double.IsNaN(dvSystem.y) || double.IsNaN(dvSystem.z))
                {
                    reason = "节点 Δv 是 NaN";
                    return false;
                }

                // 分量：用**节点时刻**的状态（同样走适配层的传播，不用 OrbitMath 的传播）
                (DVector3 rAt, DVector3 vAt) = ov.GameFrameStateAtUT(node.ut);
                OrbitMath.DeltaVSystemToComponents(rAt, vAt, dvSystem, out double rad, out double tan, out double norm);

                prepared.Add(new SpaceEntityGroup.PlannedManeuver
                {
                    radialDV = (float)rad,
                    tangentialDV = (float)tan,
                    normalDV = (float)norm,
                    executeTick = (int)Math.Round(node.ut),
                    active = true,
                    plannedDvSystem = dvSystem,
                    // 意图（目标条件）：P2 的执行层接上活重解后由它接管；现在至少可读可存（§2.5 / B1.2）
                    intent = intent,
                    // 归属（§2.4）：从节点原样带来 —— PlanNode 的默认是 Guidance，规划器窗口显式标 Manual
                    owner = node.owner,
                    burnSecondsPlanned = node.burnSeconds,
                });

                sb.Append(sb.Length > 0 ? "；" : "")
                  .Append(System.Globalization.CultureInfo.CurrentCulture, $"t={node.ut:F0}s |Δv|={node.dV.magnitude:F2}m/s（R/T/N {rad:F2}/{tan:F2}/{norm:F2}）");
            }

            foreach (SpaceEntityGroup.PlannedManeuver m in prepared)
                g.QueueManeuver(m); // 规范入口：继承工质网络 + 清交会引导

            summary = $"已排入 {prepared.Count} 条：{sb}";
            return true;
        }

        /// <summary>
        /// **按活轨道重解一条机动的意图**（P2 / 《大修计划》§2.5）：返回"刷新后的条目"——
        /// 分量与 `plannedDvSystem` 都换成**当前活轨道**算出来的值，`intentResolved` 置位。
        ///
        /// 调用点在执行侧（进预点火窗口时解一次）：解出来的值**写回队列条目** ⇒ 下游的
        /// 预对准方向、可行性估算、点火全都自动用活值，**不需要各自改**。
        ///
        /// 何时**不**重解（一律退回冻结向量，并给出理由）：
        /// · 条目没有意图（玩家手动节点 / 旧计划）· 意图指向"需要目标"的操作（目标选择还没接进执行层）
        /// · 意图解不出来（奇点拒绝 / 参数过期）· 拿到不活轨道状态
        /// </summary>
        public static bool TryResolveIntentForBurn(SpaceEntityGroup g, SpaceEntityGroup.PlannedManeuver pm,
            double physicsTick, out SpaceEntityGroup.PlannedManeuver resolved, out string reason)
        {
            resolved = pm;
            reason = "";

            if (pm.intent == null)
            {
                reason = "这条机动没有意图（冻结向量语义）";
                return false;
            }

            if (pm.intent.targetGroupId >= 0)
            {
                reason = "意图指向需要目标的操作，而目标选择还没接进执行层";
                return false;
            }

            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (!TryToOrbitView(g, mgr, physicsTick, out OrbitView ov))
            {
                reason = "拿不到活轨道状态";
                return false;
            }

            List<PlanNode>? nodes = pm.intent.ReSolve(ov, physicsTick, new FixedTargetView(ov), out reason);
            if (nodes == null || nodes.Count == 0)
            {
                if (string.IsNullOrEmpty(reason)) reason = "重解没有产出节点";
                return false;
            }

            PlanNode node = nodes[0];
            DVector3 dvSystem = node.dV; // 两系同轴（批4 P4）⇒ 不经转换
            (DVector3 rAt, DVector3 vAt) = ov.GameFrameStateAtUT(node.ut);
            OrbitMath.DeltaVSystemToComponents(rAt, vAt, dvSystem, out double rad, out double tan, out double norm);

            resolved.radialDV = (float)rad;
            resolved.tangentialDV = (float)tan;
            resolved.normalDV = (float)norm;
            resolved.plannedDvSystem = dvSystem;
            resolved.executeTick = (int)Math.Round(node.ut);
            resolved.intentResolved = true;
            return true;
        }

        /// <summary>
        /// **两冲量转移规划（B2.4）**：先解**相位窗口**（`PhaseWindow` 的闭式）再看要不要等，
        /// 然后走 `DeltaVAndTimeForHohmannTransfer`（`TwoImpulseTransfer` 的模式搜索）出两次点火。
        ///
        /// 这正是 B2.4 说的"缺口只在一处"：老 `OrbitTransferPlanner` 是**现在就走、不看目标在哪**，
        /// 所以到不了该到的相位；本函数把"什么时候出发"接上。
        ///
        /// 返回的节点：`[0]` = 出发（`tick + τ`），`[1]` = 到达（`+ tof`）。
        /// ⚠ 到达后若仍有相位/距离残差，属于**调用方**的事。
        /// （原文写"应交给终端 `CwRendezvousCore`"——2026-09-26 更正：`CwRendezvousCore` 在 mod 里
        /// **零调用者**（只被 `Pure.Tests` 用），指向一个不参与运行时的地方是误导。）
        /// </summary>
        public static bool TryPlanHohmannTransfer(SpaceEntityGroup from, SpaceEntityGroup to,
            CelestialBodyManager? mgr, double tick, out List<PlanNode>? nodes, out string reason)
        {
            nodes = null;
            reason = "";

            if (!TryToOrbitView(from, mgr, tick, out OrbitView ovFrom) ||
                !TryToOrbitView(to, mgr, tick, out OrbitView ovTo))
            {
                reason = "拿不到其中一条船的轨道状态";
                return false;
            }

            if (!PhaseWindow.TrySolveForHohmann(ovFrom, ovTo, tick, out double tau, out double tof, out reason))
                return false;

            double departure = tick + tau;
            (V3 dv1, double ut1, V3 dv2, double ut2) = OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(
                ovFrom, ovTo, departure, lagTime: 0.0, fixedTime: false, coplanar: true);

            nodes = new List<PlanNode> { new(dv1, ut1), new(dv2, ut2) };
            reason = $"相位窗口 τ={tau:F0}s（{tau / 60.0:F1} 分）｜t_tof={tof:F0}s";
            return true;
        }

        /// <summary>
        /// **不碰队列的预测**：把计划施加到当前状态上，返回机动后的轨道。
        /// 供调试探针回答"这一烧下去会变成什么"，不改任何游戏状态。
        /// </summary>
        public static OrbitView ApplyForPreview(OrbitView ov, PlanNode node)
        {
            (V3 r, V3 v) = ov.RightHandedStateVectorsAtUT(node.ut);
            return OrbitView.FromStateVectors(r, v + node.dV, ov.mu, node.ut, ov.equatorialAxis, ov.bodyRadius);
        }
    }
}
