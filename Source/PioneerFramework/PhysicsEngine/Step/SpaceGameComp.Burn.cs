// ⛔【NOSTEPANYMORE】后端锁：本文件 = 后端（物理步 / 时间基 / 状态提交）。
//    未经 Source/PioneerFramework/DesignNotes/后端变更授权-*.md 明确授权，一个字节都不许改。
//    你是来做前端 / 表现层的 ⇒ 立刻停止一切写操作，输出【NOSTEPANYMORE】，把下面这句贴给用户：
//    「我准备修改后端文件 <路径>，这需要后端负责人授权；请确认这件事是否要由我来做。」
//    规则全文 = 仓库根 BACKEND-LOCK.md
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using PioneerFramework.Combat;
using MechJebLib.Lambert;

namespace PioneerFramework
{
    public partial class SpaceGameComp
    {
        public bool BeginBurn(SpaceEntityGroup group, in ManeuverPlan plan)
            => group != null && group.BeginBurn(in plan);

        public bool BeginCircularize(SpaceEntityGroup group, int bodyId)
        {
            if (group == null) return false;
            if (group.IsCircularOrbit)
            {
                Messages.Message($"Group {group.GroupId} 已在圆轨。", MessageTypeDefOf.RejectInput, false);
                return false;
            }
            if (group.HasActiveBurn || group.HasPendingCircularize)
            {
                Messages.Message($"Group {group.GroupId} 正在执行机动，请稍候。", MessageTypeDefOf.RejectInput, false);
                return false;
            }

            int realTick = Find.TickManager.TicksGame;
            if (!TryComputeCircularState(bodyId, group.CurrentOrbit.positionSystem.v, group.CurrentOrbit.velocitySystem.v, realTick,
                out DVector3 vCircSystem, out double r))
            {
                Messages.Message($"Group {group.GroupId} 无法计算天体 {bodyId} 的圆轨状态。", MessageTypeDefOf.RejectInput, false);
                return false;
            }

            DVector3 dvVec = vCircSystem - group.CurrentOrbit.velocitySystem.v;
            double nodeDV = dvVec.magnitude;
            if (nodeDV < 0.5)
            {
                // 已接近圆轨（<0.5 m/s）：免 burn，直接走第二段结算
                return TryCompleteCircularize(group, bodyId, vCircSystem);
            }

            // ── 工质检查：所需 + 阈值（两段机动最坏消耗 = 机动所需 + 残差阈值）──
            var src = GetGroupPropulsionSource(group);
            if (src == null || src.Networks.Count == 0)
            {
                Messages.Message($"Group {group.GroupId} 无推进能力，无法消耗 dV 进入圆轨。", MessageTypeDefOf.RejectInput, false);
                return false;
            }
            double mDry = GetGroupMass(group, null);
            var est = BurnCalculator.Estimate((float)(nodeDV + CircularizeDvThreshold), (float)mDry, src, src.Networks, null);
            if (!est.Feasible)
            {
                string reason = est.Networks == null || est.Networks.Count == 0
                    ? "未安装引擎或未启用任何工质网络"
                    : $"工质不足（需约 {est.RequiredPropellant:F0} kg，最大可达 ΔV {est.MaxDeltaV:F0} m/s）";
                Messages.Message($"Group {group.GroupId} 无法进入圆轨：{reason}。", MessageTypeDefOf.RejectInput, false);
                return false;
            }

            // ── 立即执行节点：退出圆轨残留，开始缓释 burn（全网络）──
            // 燃烧时长按**可用加速度**给（2026-10-29 A）：旧值固定 30 s ⇒ 在远地点附近烧 30 秒，
            // 船离开顶点、积累 ~10 m/s 径向漂移 ⇒ 结算残差正好压在阈值上方（地球 8/10 发失败）。
            // 现在 = 2×最短时间（≈脉冲量级），夹在 [2 s, 30 s]；低推力船仍能烧完。
            double thrustSum = 0.0;
            for (int i = 0; i < src.Networks.Count; i++) thrustSum += src.Networks[i].Thrust;
            double aCirc = thrustSum > 1.0 ? thrustSum / Math.Max(mDry, 1.0) : 0.0;
            double burnSeconds = aCirc > 1e-6
                ? Math.Clamp(2.0 * nodeDV / aCirc, 2.0, 30.0)
                : 30.0;

            var plan = new ManeuverPlan
            {
                dv = dvVec,
                burnSeconds = burnSeconds,
                networks = ManeuverNetworkMode.All,
                clearCircularOrbit = true,
            };
            if (!BeginBurn(group, in plan))
            {
                Messages.Message($"Group {group.GroupId} 圆轨点火被拒（控制核心/欠电）。", MessageTypeDefOf.RejectInput, false);
                return false;
            }
            group.HasPendingCircularize = true;
            group.pendingCircularizeBodyId = bodyId;
            // 机动追踪：进入圆轨 burn 期间自动降速到 Normal
            trackedManeuvers[group.GroupId] = new ManeuverTracker
            {
                ExecutePhysicsTick = TimeScaleManager.GetPhysicsTickSafe(),
                IsActive = true
            };

            PhysicsLogMessage($"[Circularize] Group {group.GroupId} 圆轨机动开始: body={bodyId} r={r / 1000.0:F0}km 节点ΔV={nodeDV:F1}m/s（检查含阈值余量 {CircularizeDvThreshold:F1}）");
            return true;
        }

        /// <summary>
        /// 圆轨第二段结算（在缓释 burn 完成后调用）：按当前轨道二次计算圆轨，
        /// 当前速度与圆轨速度残差 ≤ 阈值则一次性扣除对应工质并入轨（第二次计算的圆轨）；
        /// 残差过大则放弃入轨，保持正常轨道（可再次点击圆轨重试）。
        /// </summary>
        /// <param name="precomputedVCirc">已接近圆轨时直接传入的圆轨速度（避免重复计算）。</param>
        private bool TryCompleteCircularize(SpaceEntityGroup group, int bodyId, DVector3? precomputedVCirc = null)
        {
            if (group == null) return false;
            group.HasPendingCircularize = false;
            group.pendingCircularizeBodyId = -1;

            int realTick = Find.TickManager.TicksGame;
            // 最后一步 burn 冲量尚未施加：并入当前速度得到真实速度
            DVector3 vNow = group.CurrentOrbit.velocitySystem.v + group.pendingImpulse;

            DVector3 vCirc;
            double r;
            if (precomputedVCirc.HasValue)
            {
                vCirc = precomputedVCirc.Value;
                r = 0.0;
            }
            else if (!TryComputeCircularState(bodyId, group.CurrentOrbit.positionSystem.v, vNow, realTick, out vCirc, out r))
            {
                PhysicsLogWarning($"[Circularize] Group {group.GroupId} 二次计算圆轨失败，保持正常轨道。");
                return false;
            }

            double residual = (vCirc - vNow).magnitude;
            if (residual > CircularizeDvThreshold)
            {
                PhysicsLogMessage($"[Circularize] Group {group.GroupId} 残差 {residual:F1} m/s 超过阈值 {CircularizeDvThreshold:F1}，未进入圆轨（保持正常轨道，可重试）。");
                PhysicsMessage($"Group {group.GroupId} 圆轨失败：残差 {residual:F1} m/s 超过阈值 {CircularizeDvThreshold:F1} m/s。", MessageTypeDefOf.RejectInput, false);
                return false;
            }

            // ── 成功：真实速度（含最后冲量）落位 → 清空 pending → 一次性扣残差工质 → 进入圆轨 ──
            group.CurrentOrbit.velocitySystem = (SystemVec3)vNow;
            group.pendingImpulse = DVector3.zero; // 已并入 vNow，不再叠加
            ConsumeDeltaVOneShot(group, residual);
            group.EnterCircularOrbit(realTick, bodyId);

            // 2026-10-30 用户裁定：**圆轨化收尾要中和角速度（消旋）**。
            // 为什么必须在这里做：圆轨化成功 = 制导交接点（`RocketLaunchController` 随后
            // `activeLaunches.RemoveAt`、`aeroAlignToAirflow=false`）⇒ 之后**没有任何东西再写 ω**，
            // 而空间里也没有阻尼 ⇒ 交接瞬间的残余 ω（上升段姿态对准的收敛尾巴，或分离接触擦出来的转动）
            // 会被**永久冻结**，玩家看到"二级一直在转"。这里主动归零，交出一条姿态稳定的船。
            // 口径：手动旋转 / SAS 开着时不碰 ω（由玩家或控制器随后写），与机动队列的消旋同一判据。
            bool manualRotation = SteeringController.IsOpen
                && SteeringController.GroupId == group.GroupId
                && (SteeringController.AnyRotation || SteeringController.SasKind != SasTargetKind.None);
            double spinBeforeDeg = group.angularVelocity.magnitude * Mathf.Rad2Deg;
            if (!manualRotation) group.angularVelocity = DVector3.zero;

            PhysicsLogMessage($"[Circularize] Group {group.GroupId} 进入圆轨: body={bodyId} r={(r > 0.0 ? r / 1000.0 : group.circularRadius / 1000.0):F0}km 残差ΔV={residual:F1} m/s（≤阈值 {CircularizeDvThreshold:F1}）已一次性扣工质"
                + (manualRotation ? $"｜手动/SAS 在控，保留 |ω|={spinBeforeDeg:F3}°/s" : $"｜已消旋（交接前 |ω|={spinBeforeDeg:F3}°/s）"));
            return true;
        }

        /// <summary>
        /// 计算某天体在当前状态下的圆轨速度向量（世界系）与轨道半径。
        /// 数学与 EnterCircularOrbit 一致（含月球等非行星天体的坐标换算）。
        /// </summary>
        private bool TryComputeCircularState(int bodyId, DVector3 posSystem, DVector3 velSystem, int realTick,
            out DVector3 vCircSystem, out double radiusMeters)
        {
            vCircSystem = DVector3.zero;
            radiusMeters = 0.0;
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return false;
            var body = mgr.GetBodyById(bodyId);
            if (body == null) return false;

            DVector3 relPos = posSystem, relVel = velSystem;
            bool runtimeFrame = mgr.TryGetCelestialRuntime(out _);
            var offset = runtimeFrame ? mgr.BodyFrameOffset(bodyId, realTick) : default;
            if (runtimeFrame)
            {
                relPos -= offset.Position;
                relVel -= offset.Velocity;
            }
            else if (body.bodyType != CelestialBodyType.Planet)
            {
                relPos -= mgr.GetBodyPositionParent(bodyId, realTick);
                relVel -= mgr.GetBodyVelocityParent(bodyId, realTick);
            }
            radiusMeters = relPos.magnitude;
            if (radiusMeters < 0.01) return false;

            double mu = runtimeFrame ? body.gravitationalParameter : body.gravitationalParameter > 0.0
                ? body.gravitationalParameter : SpaceConstants.PrimaryGravitationalParameter;
            if (!(mu > 0)) return false;
            double vTan = System.Math.Sqrt(mu / radiusMeters);

            DVector3 radialDir = relPos.normalized;
            DVector3 normal = DVector3.Cross(relPos, relVel).normalized;
            if (normal.sqrMagnitude < 0.01)
                normal = DVector3.up; // 速度为零/纯径向退化
            DVector3 tangentDir = DVector3.Cross(normal, radialDir).normalized;
            if (tangentDir.sqrMagnitude < 0.01)
            {
                DVector3 refVec = System.Math.Abs(radialDir.y) < 0.9 ? DVector3.up : DVector3.forward;
                tangentDir = DVector3.Cross(refVec, radialDir).normalized;
            }
            vCircSystem = tangentDir * vTan;
            if (runtimeFrame) vCircSystem += offset.Velocity;
            else if (body.bodyType != CelestialBodyType.Planet)
                vCircSystem += mgr.GetBodyVelocityParent(bodyId, realTick);
            return true;
        }

        /// <summary>
        /// 一次性按火箭方程把残差 dV 换算为工质并扣除（按各网络 ṁ 比例分配，与 burn/轨道保持一致）。
        /// 对应「小于阈值就一次性减去 dV（减去对应工质）」。
        /// </summary>
        /// <summary>
        /// B2.2-b **闭环微调**：按「现在这一刻的轨道」与**意图轨道**的差算一次小 Δv 并排队。
        /// 执行完全走**既有机动队列**（执行器、选网、工质、居中、日志、诊断全部沿用现成那一套 ——
        /// 微调就是一次普通机动，不是第二套执行路径）。
        ///
        /// 判据（三条，全部满足才打）：
        ///   · `TrimBurn.TryPlan` 成功（共面可达）；
        ///   · Δv 落在**合理带** <c>[TrimDvMin, TrimDvMax]</c>：太小没有意义（会被点火门/死区吃掉），
        ///     太大说明不是"末段收口"而是"计划本身错了"—— 那种情况该由计划侧报警，**不能悄悄补一个大机动**；
        ///   · 每次机动最多一次（`trimFired`）。
        /// </summary>
        private void TryQueueTrimBurn(SpaceEntityGroup group, CelestialBodyManager mgr, double physicsTick)
        {
            group.trimFired = true;      // 无论成不成，判过就不再判（否则每 tick 刷屏）
            // 主燃的累计 Δv 此刻还在（`appliedBurnDV` 要等微调自己 `StartBurn` 才被清）⇒ 留给验收读数用：
            // 否则验收会显示"实际已烧 = 微调那 1.3 m/s"，把主燃的 808 藏起来（2026-09-20 实测踩到）。
            group.trimMainBurnDv = group.appliedBurnDV;
            if (!OrbitAdapter.TryToOrbitView(group, mgr, physicsTick, out OrbitView ovNow))
                return;

            (DVector3 rNow, DVector3 vNow) = ovNow.GameFrameStateAtUT(physicsTick);   // 出边口（批6 S2）
            // ★ 主燃烧**最后一步的冲量**此刻可能还没落位（它在下一步的物理步首才并入速度）。
            //   不补上的话，微调会把"那一笔还没到"当成残差 ⇒ 规划出一个假的修正量。
            //   口径与 `TryCompleteCircularize` 逐字相同（"最后一步 burn 冲量尚未施加：并入当前速度"）。
            vNow += group.pendingImpulse;
            if (!TrimBurn.TryPlan(rNow, vNow, ovNow.mu, group.trimTargetA, group.trimTargetH,
                    out DVector3 dvSystem, out string why, out double aUsed))
            {
                PhysicsLogMessage($"[燃烧·微调] G{group.GroupId} 不微调：{why}");
                return;
            }

            double dvMag = dvSystem.magnitude;
            if (dvMag < TrimDvMin || dvMag > TrimDvMax)
            {
                PhysicsLogMessage($"[燃烧·微调] G{group.GroupId} 不微调：Δv={dvMag:F3} m/s 落在合理带 [{TrimDvMin:F2}, {TrimDvMax:F0}] 之外｜目标 a={group.trimTargetA / 1000.0:F0} km");
                return;
            }

            // 分量按**当前** RTN 分解（执行器整段燃烧只换框架、不换解）
            OrbitMath.DeltaVSystemToComponents(rNow, vNow, dvSystem, out double rad, out double tan, out double nor);
            group.trimLastDv = dvMag;
            group.QueueManeuver((float)rad, (float)tan, (float)nor, TrimLeadTicks, PlanOwner.Executor);
            PhysicsLogMessage($"[燃烧·微调] G{group.GroupId} 闭环微调：Δv={dvMag:F3} m/s（R/T/N {rad:F2}/{tan:F2}/{nor:F2}）｜本点 r={rNow.magnitude / 1000.0:F1} km，目标 a={group.trimTargetA / 1000.0:F1} km（差 {(rNow.magnitude - group.trimTargetA) / 1000.0:F1} km）");
            // 退化支必须**显式写出**：本点不在意图曲线上时，一发点火只能"保形状、把尺度锚到本点"
            // （见 `TrimBurn` 类注释的取舍）⇒ 半长轴会落在 r 而不是 a_t，读数上要看得出来。
            if (Math.Abs(aUsed - group.trimTargetA) > 1.0)
                PhysicsLogMessage($"[燃烧·微调] G{group.GroupId} 目标退化：本点 r={rNow.magnitude / 1000.0:F1} km 不在意图轨道径向范围内（a_t={group.trimTargetA / 1000.0:F1} km、e_t={Math.Sqrt(Math.Max(0.0, 1.0 - group.trimTargetH * group.trimTargetH / (ovNow.mu * group.trimTargetA))):E4}）⇒ 保形状、锚定本点 ⇒ a 落在 {aUsed / 1000.0:F1} km（与意图差 {(aUsed - group.trimTargetA) / 1000.0:F1} km）；**这一发修的是 e，不是 a**");
        }

        /// <summary>
        /// **一次性付一笔 Δv**（火箭方程唯一算式：`Δm = m₀(1 − e^(−Δv/ve))`，按各网络 ṁ 占比分摊扣料）。
        /// <paramref name="tag"/> 只影响日志前缀（既有调用方传默认值 ⇒ 输出逐字不变）；
        /// 返回实际扣掉的工质 (kg)，受存量截断时可与需求不同。
        /// 主动降落由 ConsumeLandingDeltaV 按报价分配逐个网络扣料。
        /// </summary>
        private double ConsumeDeltaVOneShot(SpaceEntityGroup group, double dv, string tag = "Circularize")
        {
            if (dv <= 0.001) return 0.0;
            var src = GetGroupPropulsionSource(group);
            if (src == null) return 0.0;
            double ve = src.EffectiveVe;
            if (ve <= 0.0) return 0.0;
            double m0 = GetGroupMass(group, src);
            // Δm = m0·(1 − e^(−ΔV/ve))
            double dm = m0 * (1.0 - Math.Exp(-dv / ve));
            if (dm <= 0.0) return 0.0;

            double mfTotal = 0.0;
            foreach (var n in src.Networks)
                mfTotal += n.MassFlow;
            if (mfTotal <= 0.0) return 0.0;
            double actual = 0.0;
            foreach (var n in src.Networks)
            {
                double share = dm * (n.MassFlow / mfTotal);
                actual += ConsumeGroupPropellant(group, n, (float)share);
            }
            PhysicsLogMessage($"[{tag}] Group {group.GroupId} {(tag == "Circularize" ? "残差 " : "")}dV 一次性消耗: {dv:F1} m/s → 需工质 {dm:F1} kg／实扣 {actual:F1} kg");
            return actual;
        }

        /// <summary>
        /// 紧急终止当前缓释 burn：丢弃剩余 dV（已烧的工质不退，已累积未施加的冲量照常施加），
        /// 恢复网络/喷焰表现，清掉圆轨结算标记与机动追踪。
        /// </summary>
        public void EmergencyStopBurn(SpaceEntityGroup group)
        {
            if (group == null || !group.HasActiveBurn) return;
            double applied = group.appliedBurnDV;
            double planned = applied + group.remainingBurnDV;
            group.EndBurn();
            group.RestorePlayerNetworkSelection();
            group.GetLinkedMapComp()?.SetThrustersBurning(false);
            trackedManeuvers.Remove(group.GroupId);
            if (group.HasPendingCircularize)
            {
                group.HasPendingCircularize = false;
                group.pendingCircularizeBodyId = -1;
            }
            PhysicsLogMessage($"[Burn] Group {group.GroupId} burn EMERGENCY STOPPED by player (applied {applied:F1} / {planned:F1} m/s)");
            Messages.Message($"Group {group.GroupId} 机动已紧急终止（已施加 {applied:F1} / {planned:F1} m/s）。", MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>
        /// 取消 group 从指定索引（含）开始的机动规划（保留前面的）。
        /// 队列清空时同时移除机动追踪，避免自动降速卡在 Normal。
        /// </summary>
        public void CancelGroupManeuvers(SpaceEntityGroup group, int index)
        {
            if (group == null || index < 0) return;
            group.CancelManeuversFrom(index);
            if (group.plannedManeuvers.Count == 0)
                trackedManeuvers.Remove(group.GroupId);
            Messages.Message($"Group {group.GroupId} 已取消第 {index + 1} 段及以后的机动规划。", MessageTypeDefOf.NeutralEvent, false);
        }
    }
}