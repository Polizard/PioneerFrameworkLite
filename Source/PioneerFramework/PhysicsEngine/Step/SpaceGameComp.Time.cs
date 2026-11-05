// ⛔【NOSTEPANYMORE】后端锁：本文件 = 后端（物理步 / 时间基 / 状态提交）。
//    未经 Source/PioneerFramework/DesignNotes/后端变更授权-*.md 明确授权，一个字节都不许改。
//    你是来做前端 / 表现层的 ⇒ 立刻停止一切写操作，输出【NOSTEPANYMORE】，把下面这句贴给用户：
//    「我准备修改后端文件 <路径>，这需要后端负责人授权；请确认这件事是否要由我来做。」
//    规则全文 = 仓库根 BACKEND-LOCK.md
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using Verse;
using PioneerFramework.Celestial;

namespace PioneerFramework
{
    public partial class SpaceGameComp
    {
        internal bool HasTrackedManeuvers => trackedManeuvers.Count > 0;
        internal bool RequiresTerminalPrecision
        {
            get
            {
                foreach (var group in groups.Values)
                    if (group.HasRendezvousGuidance && groups.TryGetValue(group.rendezvousTargetId, out var target)
                        && (group.positionBody - target.positionBody).magnitude < TerminalPrecisionDist) return true;
                return false;
            }
        }
        private const double TerminalPrecisionDist = 2000.0;

        internal bool GroupIsVisibleAt(SpaceEntityGroup group, double tick)
            => group.physicsBirthSourceId < 0 && tick >= group.physicsBirthTick;

        internal DVector3 RenderedGroupPosition(SpaceEntityGroup group, double tick)
        {
            if (group.IsCircularOrbit) return group.GetCircularPositionAtTick(tick);
            double current = double.IsFinite(group.physicsRenderCurrentTick) ? group.physicsRenderCurrentTick : LastPhysicsStepTick;
            if (tick < current && double.IsFinite(group.physicsRenderPreviousTick) && current > group.physicsRenderPreviousTick)
            {
                double fraction = Math.Clamp((tick - group.physicsRenderPreviousTick) / (current - group.physicsRenderPreviousTick), 0, 1);
                return group.physicsRenderPreviousPosition
                    + (group.CurrentOrbit.positionSystem.v - group.physicsRenderPreviousPosition) * fraction;
            }
            double delta = tick - current;
            if (delta < 0 || delta > 8) delta = 0;
            return group.CurrentOrbit.positionSystem.v + group.CurrentOrbit.velocitySystem.v * delta;
        }
        private int pendingGroupBirthCount;
        private List<int> invalidGroupBirths = new();

        internal void AnchorGroupBirth(SpaceEntityGroup group, SpaceEntityGroup source, DVector3 sourcePosition, DVector3 sourceVelocity)
        {
            var clock = timeScaleRef.Get();
            if (clock == null || clock.IsPhysicsBatchActive || clock.GetPhysicsTick() <= clock.CommittedPhysicsTick || source.IsCircularOrbit) return;
            group.physicsBirthSourceId = source.GroupId;
            group.physicsBirthOffsetShip = DVector3.Rotate(group.CurrentOrbit.positionSystem.v - sourcePosition,
                DQuaternion.Inverse(source.CurrentOrbit.rotation));
            group.physicsBirthMuzzleSystem = group.CurrentOrbit.velocitySystem.v - sourceVelocity;
            pendingGroupBirthCount++;
        }

        private void ResolveGroupBirths(double physicsTick)
        {
            if (pendingGroupBirthCount == 0) return;
            invalidGroupBirths.Clear();
            foreach (var group in groups.Values)
            {
                if (group.physicsBirthSourceId < 0 || group.physicsBirthTick > physicsTick) continue;
                pendingGroupBirthCount--;
                if (groups.TryGetValue(group.physicsBirthSourceId, out var source) && source.IsActive)
                {
                    var orbit = group.CurrentOrbit;
                    orbit.positionSystem = (SystemVec3)(source.CurrentOrbit.positionSystem.v
                        + DVector3.Rotate(group.physicsBirthOffsetShip, source.CurrentOrbit.rotation));
                    orbit.velocitySystem = (SystemVec3)(source.CurrentOrbit.velocitySystem.v + group.physicsBirthMuzzleSystem);
                    UpdateGroupOrbit(group.GroupId, orbit);
                }
                else invalidGroupBirths.Add(group.GroupId);
                group.physicsBirthSourceId = -1;
            }
            foreach (int id in invalidGroupBirths) DestroyGroup(id, "出生前母船已失效");
        }

        private void RestorePhysicsClockAnchor()
        {
            var clock = timeScaleRef.Get();
            if (clock == null) return;
            lastUpdateTick = clock.CommittedGameTick;
            LastPhysicsStepTick = clock.CommittedPhysicsTick;
            pendingGroupBirthCount = 0;
            foreach (var group in groups.Values)
                if (group.physicsBirthSourceId >= 0) pendingGroupBirthCount++;
        }

        /// <summary>时间区间与消费者共同调度；切档段和射弹出生边界均不依赖奇偶相位。</summary>
        internal void AdvancePhysics(int currentTick)
        {
            // B（2026-11-02）：船步在天体区间提交之后跑；一旦船步异常，没有 2PC 可回滚 ⇒
            // 明确的 hard fault 闩锁（停推进、暴露错误），而不是带着半更新的船继续跑。
            if (celestialShipStepFaulted) return;
            // P0-3（2026-11-02）：`GameComponentRef<SpaceGameComp>` 的**首次实查**若落在并行相位里，
            // `ParallelRegionGuard` 会打一次 G1/R5 告警（场景首跑假红；`SwarmScaleAcceptanceRunner` 已有同款预热先例）。
            // 这里在**进并行区之前**（主线程）预热一次；之后全是缓存命中，成本 = 每批一次缓存检查。
            _ = GetCached();
            var timeScaleManager = timeScaleRef.Get();
            var manager = celestialRef.Get();
            CelestialRuntime? runtime = manager != null && manager.TryGetCelestialRuntime(out var currentRuntime) ? currentRuntime : null;
            if (manager?.CelestialRestorePending == true)
            {
                CelestialPhysicsErrors = manager.CelestialRestoreErrors.Count == 0 ? new[] { new CelestialError("RestorePending") } : manager.CelestialRestoreErrors;
                return;
            }
            if (runtime != null)
            {
                if (runtime.IsRestoring) return;
                // The game tick is an explicit host lifecycle entry; arbitrary Runtime.Advance callers cannot rebind ownership.
                runtime.HostWork.BindHostThread();
                var attached = AttachCelestialParticipants(runtime, timeScaleManager, manager!);
                if (!attached.Success) throw new InvalidOperationException(attached.Errors[0].Code);
                if (SteeringController.IsOpen) SteeringInputHost.EnsureInputSafety();
            }
            timeScaleManager.BeginPhysicsBatch();
            try
            {
                while (true)
                {
                    // Register/unregister at the committed instant, so every positive ship interval has one stable membership.
                    if (runtime != null && runtime.HasPendingPhysicsChanges)
                    {
                        var stamp = runtime.CurrentStamp;
                        var membership = runtime.Advance(new(runtime.WorldSessionId, stamp.TopologyRevision, stamp.PhysicalSeconds, stamp.PhysicalSeconds));
                        if (!membership.Success) { CelestialPhysicsErrors = membership.Errors; break; }
                    }
                    if (!timeScaleManager.TryBeginPhysicsStep(currentTick)) break;
                    try
                    {
                        long blockClock = PerfProbe.ExtraClockStart();   // ★ 2026-10-02：整步块钟（含全部每物理步消费者）
                        // 第 25 条墙钟：整步 = 一次 UpdateAllMovements（操舵 + 冲量 + 群组物理 + 耗散 + **交会引导**）。
                        // ★ 2026-09-20 改为**常驻测量**（原先只在优化探针开着时测）：成本 = 两次 Stopwatch 读（纳秒级），
                        //   换来"随时按需出一份平均/最慢"（`PerfProbe.StepClockReport()`）。
                        //   动机：优化探针的开关会**连带打开影子校验**（额外工作 ✗）⇒ 用它测耗时会污染计时
                        //   （观测者效应）。常驻测量则与那些校验**完全独立** ✓。
                        long stepClock = PerfProbe.StepClockStart();
                        if (runtime == null) UpdateAllMovements(timeScaleManager);
                        else
                        {
                            // B（2026-11-02）：先把天体/时钟区间提交，再让船在**已提交区间**上跑。
                            // 船不再进 Coordinator.Prepare，也不再需要 2PC 副本/未来 sampler。
                            var step = timeScaleManager.CurrentPhysicsStep;
                            if (!step.HasValue) break;
                            var advanced = AdvanceCelestialPhysicsInterval(runtime, timeScaleManager);
                            CelestialPhysicsErrors = advanced.Errors;
                            if (!advanced.Success) break;
                            try { UpdateAllMovements(timeScaleManager, step); }
                            catch (Exception ex)
                            {
                                celestialShipStepFaulted = true;
                                CelestialPhysicsErrors = new[] { new CelestialError("ShipStepFault", Detail: ex.Message) };
                                Log.Error("[PFL] celestial ship step failed after interval commit; physics halted. " + ex);
                                throw;
                            }
                        }
                        LastPhysicsStepTick = timeScaleManager.GetPhysicsTick();
                        ResolveGroupBirths(LastPhysicsStepTick);
                        RetireDueDebris(LastPhysicsStepTick);   // 2026-11-03：到期碎片退役（按 groupId 升序）
                        PerfProbe.StepClockEnd(stepClock);
                        PerfProbe.StepClockMaybeAutoLog();      // 开发者模式下自动出一行（不依赖用户按按钮）

                        // 真飞验收的**自动检验**（用户 2026-09-19 要求）：布置过的场景在"烧完"后自动出结论。
                        // 空闲时第一行就返回（静态 pending 标志）⇒ 零成本；与手动「检查」共用同一套判据。
                        LiveBurnAcceptanceRunner.Tick();
                        BurnABAcceptanceRunner.Tick();   // 对照验收（一轮做完二分）：空闲时第一行返回
                        BurnPhaseAcceptanceRunner.Tick(); // 相位三点实验：空闲时第一行返回
                        DualBodyLaunchAcceptanceRunner.Tick(); // 双天体×10 发爬升验收：空闲时第一行返回（合并时从 GameComponentTick 随块搬来）
                        // 每物理步消费者统一在推进完成后驱动（2026-09-07）：GameComponent tick 顺序按类型
                        // 全名字母序（AutonomousVehicleManager → 本组件 → CombatManager），若制导/碰撞各自
                        // %2 tick 会有一方读到推进前的旧位置 → 同 tick 位置错位 2s ≈ 5km（导弹制导报 0.7km
                        // 命中、碰撞实测 2.6km 的日志实证）。此处推进完毕 = 所有消费者同 tick 同位。
                        // ★ 2026-10-02：**顺序固定为 物理 → 能力面 → 认知 → 决策**（[V3] 认知层夹在中间）。
                        // [V7]/[N7] 建筑级投影（2026-10-01）：EW 建筑住在 pocket map，comp 不可靠地 Tick
                        // ⇒ 由**物理步**把它们的贡献投影进所属 group（设计稿 §5.4 的脊柱：建筑只登记、不自己改 Group）。
                        // ⚠ 同日由"消费者之后"提到**之前**：认知层要读它（`DetectTech` / `RadarCap`）；
                        //   今天 combat/AVM 都还不读那两个字段 ⇒ 这一步移动对它们是**行为中性**的。
                        ProjectBuildingDetectTech();
                        // [V3] 认知层（S2）：每物理步无条件刷新 + 状态门控（2026-10-02 裁定 ③）
                        long sensorClock = PerfProbe.ExtraClockStart();
                        sensorMgrRef.Get()?.OnPhysicsStepCompleted();
                        PerfProbe.ExtraClockEnd(PerfProbe.ExtraClockSensor, sensorClock);
                        // 决策类消费者（读认知层；S3 才接线，今天仍读真值）
                        // ★ 两个消费者各自圈时（来袭场景里"整 tick 192.8 ms"必须能拆开）
                        long combatClock = PerfProbe.ExtraClockStart();
                        combatMgrRef.Get()?.OnPhysicsStepCompleted();
                        PerfProbe.ExtraClockEnd(PerfProbe.ExtraClockCombat, combatClock);
                        long avmClock = PerfProbe.ExtraClockStart();
                        avmRef.Get()?.OnPhysicsStepCompleted();
                        PerfProbe.ExtraClockEnd(PerfProbe.ExtraClockAvm, avmClock);
                        Current.Game?.GetComponent<SpaceAI.SpaceAiManager>()?.OnPhysicsStepCompleted();
                        // [V7] 验收状态机放在投影**之后**：它读的就是本步刚投影进去的值（不然会差一拍）。
                        //
                        // ⚠ **[2026-10-08 加固] 这一整段跑批器/框架是"一荣俱荣、一损俱损"的**：它们同处每步块，
                        //   且**这一段原本没有任何兜底** ⇒ 任何一处抛异常，后面的跑批器与 `PflTestKit.Tick()`
                        //   （异步用例的**超时判定**）会被**一起跳过**；万一是持续性抛（每步都抛），
                        //   症状就是"用例冻结：既无结算、也无超时、日志里什么都看不到"（2026-10-08 实测撞到过）。
                        //   ⇒ 这里统一兜住 + **把异常打出来**（去重限频）：宁可留一行红字，也不许再静默。
                        try
                        {
                            SensorProjectionAcceptanceRunner.Tick();
                            // 来袭性能场景（2026-10-02）：空闲时第一行返回（静态 stage 判据）⇒ 零成本
                            SwarmAttackPerfRunner.Tick();
                            // [V3] 认知层验收（2026-10-02 S2）：必须在 `SensorManager` **之后**（读它本步刚写的视图）
                            SensorCognitionAcceptanceRunner.Tick();
                            // 电子战总控（2026-10-02）：按段推进已跑完的验收（空闲时零开销）
                            EwSystemAcceptanceRunner.Tick();
                            AtmosphereAcceptanceRunner.Tick();   // 大气/悬浮/档位（L1）：空闲时第一行返回
                            ComplexCollisionAcceptanceRunner.Tick(); // 多part 复杂碰撞：等撞击记录后自动结算（空闲零成本）
                        }
                        catch (Exception ex)
                        {
                            LogAcceptanceTickException(ex);
                        }
                        // ⚠ 总控要在**各段自己的 Tick 之后**：它靠"上一段跑完"来起下一段。
                        //   （放在本块末尾即可——它自己只做读取与派发。）
                        PerfProbe.ExtraClockEnd(PerfProbe.ExtraClockBlock, blockClock);   // 整步块钟闭合
                    }
                    finally
                    {
                        // 保留既有异常语义：发生部分状态写入的区间不可在下一 tick 重复结算。
                        if (runtime == null) timeScaleManager.CompletePhysicsStep();
                        else if (timeScaleManager.CurrentPhysicsStep.HasValue) timeScaleManager.CancelCelestialPhysicsStep();
                        lastUpdateTick = timeScaleManager.CommittedGameTick;
                        LastPhysicsStepTick = timeScaleManager.CommittedPhysicsTick;
                    }
                }
            }
            finally { timeScaleManager.EndPhysicsBatch(); }
        }
    }
}
