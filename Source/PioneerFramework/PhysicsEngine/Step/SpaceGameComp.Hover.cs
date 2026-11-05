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
        public bool SetHover(SpaceEntityGroup? group, bool on) => SetHover(group, on, analytic: false);

        /// <summary>
        /// **[A8] 开/关悬浮 + 选档位**（`analytic` = 解析档）。解析档的**准入判据**照 D8 的先例
        /// （tile-landing"付不起直接拒绝"）：**必须有全矢量（RCS）网** —— 否则等效计费没有 `ve` 可依、
        /// 也没法在退出解析档时兑现 ⇒ 直接拒绝并如实报。
        /// **进出必须显式**（D7）：两条路都写日志，含**当前档位 + 计费速率**（面板口径的日志版）。
        /// </summary>
        public bool SetHover(SpaceEntityGroup? group, bool on, bool analytic)
        {
            if (group == null) return false;
            // [模式互斥] 搬运中不许开悬浮（理由见 `CanHover`）：两者都"每步改位置" ⇒ 同时跑就是互相打架。
            if (on && group.transferActive)
            {
                PhysicsLogWarning($"[档位] G{group.GroupId} 拒绝开悬浮：**正在搬运中**（搬运与定点保持互斥；先「停止」或等它到达）");
                return false;
            }
            if (on && analytic && !CanHover(group, analytic: true, out string? whyAna))
            {
                PhysicsLogWarning($"[档位] G{group.GroupId} 拒绝进解析档：{(whyAna ?? "原因未给")}（D8：付不起直接拒绝）");
                return false;
            }
            if (!SetHoverCore(group, on)) return false;
            if (on) group.hoverAnalytic = analytic;
            LogTier(group, on ? (analytic ? "进解析档（定点解析，照扣料）" : "进物理档（定点保持控制器）")
                              : "退出悬浮 ⇒ 回默认（解析搬运/巡航）");
            return true;
        }

        /// <summary>
        /// **悬浮可用性预检**（**只读**、不改任何状态）——控制台拿它把"为什么按不动 / 开了会怎样"
        /// **写在界面上**，而不是让玩家去猜（2026-10-07：控制台原来给的是"不在大气里/没有网/没引擎"三选一，
        /// 玩家只能看日志猜是哪一条 ⇒ 改成**唯一出处**的预检，返回**具体**原因）。
        ///
        /// 与生产门**同一份判据**（本方法就是 `SetHover`/`SwitchHoverTier` 用的那道门，不再各写一份）：
        /// · 没有天体 / 在地表以下 ⇒ 不可用（硬拒）；
        /// · **解析档**必须有全矢量（RCS）网（`ve` 可依）⇒ 没有则硬拒；
        /// · **物理档**生产上允许开 ⇒ 本方法**放行但给出警告语**（"全矢量网为空 ⇒ 开了会掉"）——
        ///   实测：玩家对一台**没有 RCS** 的试验船开了物理档，船直接掉到地上（`[悬浮] … 推力不足`）。
        /// </summary>
        public bool CanHover(SpaceEntityGroup group, bool analytic, out string? reason)
        {
            reason = null;
            if (group == null) { reason = "编队不存在"; return false; }
            var mgr = celestialRef.Get();
            var body = mgr?.GetBodyById(group.CelestialBodyId);
            if (body == null) { reason = "找不到天体"; return false; }
            if (group.CurrentOrbit.positionSystem.v.magnitude <= body.radius)
            {
                reason = "本船在地表以下 ⇒ 没有可锁的定点";
                return false;
            }
            // **[模式互斥]** 搬运中不能开悬浮：两者都"每步改位置"（搬运按航线推、悬浮按定点按），
            // 一起跑就是互相打架（2026-10-07 用户实测："船疑似被锚定死了，走不出去"）。
            if (group.transferActive)
            {
                reason = "正在搬运中 ⇒ 悬浮不可用（先按「停止」或等它到达）";
                return false;
            }
            // **[D7②] 交战 ⇒ 解析档不可用**（用户 2026-10-08：用现成的"有射弹"信号，不新建交战触发）
            if (analytic && CombatActionActive)
            {
                reason = "处于交战动作（有射弹在飞 / 导弹终末攻击）⇒ 解析档（定点解析）不可用；物理档照常（降级只加精度）";
                return false;
            }
            var src = GetGroupPropulsionSource(group);
            if (src == null || EffectiveFullVectorVe(src) <= 0f)
            {
                reason = "没有可用的全矢量（RCS）网：解析档没有 ve 可计费；物理档开了也纠不动（会掉）";
                return !analytic;     // 物理档生产上允许开 ⇒ 放行（警告语已给）
            }
            return true;
        }

        /// <summary>
        /// **[A8] 档位切换（解析 ⇄ 物理）**：**只翻档位、不重锁定点** ——
        /// 定点是**工作模式**的属性（D9），档位只管"受不受力"（D7）⇒ 切档不该把船按到"现在所处的位置"上。
        /// ⚠ 实测踩过：切档误走"重开悬浮"⇒ 重新锁一次定点（日志第二条"开启"、切回后横向误差 434 m 再收敛）。
        /// </summary>
        public bool SwitchHoverTier(SpaceEntityGroup group, bool analytic)
        {
            if (group == null || !group.hoverActive) return false;
            if (group.hoverAnalytic == analytic) return true;
            if (analytic && !CanHover(group, analytic: true, out string? whyAna))
            {
                PhysicsLogWarning($"[档位] G{group.GroupId} 拒绝切解析档：{(whyAna ?? "原因未给")}（D8：付不起直接拒绝）");
                return false;
            }
            group.hoverAnalytic = analytic;
            LogTier(group, analytic ? "切解析档（定点解析，照扣料；**定点不变**）"
                                    : "切物理档（定点保持控制器；**定点不变**）");
            return true;
        }

        /// <summary>全矢量（RCS）网的有效 ve（解析档计费用；与物理档用同一批网 ⇒ 两档账单可比）。</summary>
        private static float EffectiveFullVectorVe(IPropulsionSource src)
        {
            float f = 0f, mf = 0f;
            var nets = src.Networks;
            for (int i = 0; i < nets.Count; i++)
            {
                if (!nets[i].HasRcsEngines || nets[i].MassFlow <= 0f) continue;
                f += nets[i].Thrust;
                mf += nets[i].MassFlow;
            }
            return mf > 0f ? f / mf : 0f;
        }

        /// <summary>
        /// **[A8] 档位日志（D7"进出必须显式"）**：报**档位 + 耗料率 + 还能撑多久** ——
        /// 面板口径的日志版（面板 UI 属视觉/交互线，本刀先只保证"玩家能在日志里看到自己在哪档"）。
        /// </summary>
        private void LogTier(SpaceEntityGroup group, string what)
        {
            var src = GetGroupPropulsionSource(group);
            double mass = GetGroupMass(group, src);
            var mgr = celestialRef.Get();
            var body = mgr != null ? mgr.GetBodyById(group.CelestialBodyId) : null;
            double g = body != null && body.radius > 0.0
                ? SpaceConstants.PrimaryGravitationalParameter / (body.radius * body.radius)
                : 0.0;
            double ve = src != null ? EffectiveFullVectorVe(src) : 0f;
            double rate = ve > 0.0 ? mass * g / ve : 0.0;          // 悬浮的等效质量流率（kg/s）
            double fuel = src != null ? src.TotalPropellantMass : 0.0;
            PhysicsLogMessage($"[档位] G{group.GroupId} {what}｜档位={(group.hoverActive ? (group.hoverAnalytic ? "解析" : "物理") : "—（回默认）")}｜全矢量 ve={ve:F0} m/s｜等效耗料率 ≈{rate:F2} kg/s｜余料 {fuel:F0} kg（≈{(rate > 0.0 ? fuel / rate : 0.0):F0} s）");
        }

        private bool SetHoverCore(SpaceEntityGroup group, bool on)
        {
            if (group == null) return false;
            if (!on)
            {
                if (group.hoverActive) AdjustPhysicsHoverCount(-1);
                group.hoverActive = false;
                group.hoverAnalytic = false;   // [A8] 档位随悬浮一起清（下次开启重新选档）
                return true;
            }
            var mgr = celestialRef.Get();
            if (mgr == null) return false;
            var body = mgr.GetBodyById(group.CelestialBodyId);
            if (body == null) return false;

            var pos = group.CurrentOrbit.positionSystem.v;
            double seconds = TimeScaleManager.GetPhysicsTickSafe();
            if (mgr.TryGetCelestialRuntime(out _)) pos -= mgr.BodyFrameOffset(group.CelestialBodyId, seconds).Position;
            double r = pos.magnitude;
            if (!(r > body.radius)) return false;                       // 在地表以下 ⇒ 不锁
            // ⚠ tick 口径 = **物理时间轴**（`GetPhysicsTickSafe`）：与固连系的其它调用点同一根轴
            //   （用 `TicksGame` 会在时间倍率下把经度算歪 ω·Δt）。
            var (lat, lon) = Launch.GetSurfaceState.BodyLongLat(mgr, group.CelestialBodyId, pos, seconds);
            group.hoverLatRad = lat;
            group.hoverLonRad0 = lon;
            group.hoverAltitudeMeters = r - body.radius;
            if (!group.hoverActive) AdjustPhysicsHoverCount(1);
            group.hoverActive = true;
            group.hoverShortfallLogged = false;
            PhysicsLogMessage($"[悬浮] G{group.GroupId} 开启：锁定点 φ={lat:F5} rad λ0={lon:F5} rad 高度 {group.hoverAltitudeMeters:F1} m（本刀口径 = 全矢量 RCS 推力 + 锁铅垂）");
            return true;
        }

        /// <summary>
        /// **[A8] 解析搬运的每步记账与到达清单**（主线程）：
        /// · 记 `dv = Δv_total·dt/T`（**末步截断**），扣 `m·dv/ve`（沿途全部网络按 ṁ 比例分账，与其它路径同口径）；
        /// · 料尽 ⇒ **显式退出**（不静默、不留"免费档"）；
        /// · `u ≥ 1` ⇒ **精确落位**到目标状态 + 清搬运单 + 一行日志（D7"进出必须显式"）。
        /// </summary>
        private void ApplyAnalyticTransfer(double dtSeconds, double physicsTick, CelestialBodyManager mgr)
        {
            if (!(dtSeconds > 0.0)) return;
            foreach (var group in groups.Values)
            {
                if (!group.transferActive) continue;
                var src = GetGroupPropulsionSource(group);
                float ve = src != null ? src.EffectiveVe : 0f;
                if (src == null || ve <= 0f)
                {
                    PhysicsLogWarning($"[搬运] G{group.GroupId} 中断：没有引擎（或 ve≤0）");
                    group.transferActive = false;
                    continue;
                }

                double elapsed = physicsTick - group.transferStartTick;
                double u = AnalyticTransferCore.Progress(elapsed, group.transferDuration);
                double dtUsed = Math.Min(dtSeconds, Math.Max(group.transferDuration - (elapsed - dtSeconds), 0.0));
                if (dtUsed < 0.0) dtUsed = 0.0;

                // **[D7②] 交战中止**（用户 2026-10-08 裁定：用现成的"有射弹"信号）：
                //   解析搬运是"轨上"的 ⇒ 交战中不该继续享受"不模拟受力"的特权 ⇒ 中途**中止**、
                //   交还物理档（船按当前状态继续飞，玩家可以物理档机动/悬浮）。停在哪 = 当前位置。
                if (u < 1.0 && CombatActionActive)
                {
                    group.transferActive = false;
                    group.OrbitDirty = true;
                    PhysicsLogMessage($"[搬运] G{group.GroupId} **中止**（交战动作出现 ⇒ 解析轨降级为物理档）：停在当前位置（已到 u={u:F2}）");
                    TryResumeHoverAfterTransfer(group, src, "中止");   // 起搬前在悬着 ⇒ 就地恢复（不然在大气里会开始掉）
                    continue;
                }

                if (u < 1.0 && dtUsed > 0.0)
                {
                    double dvStep = AnalyticTransferCore.DvThisStep(group.transferDvTotal, group.transferDuration, dtUsed);
                    double mass = GetGroupMass(group, src);
                    double dm = AnalyticTransferCore.PropellantThisStep(mass, dvStep, ve);
                    if (dm > 0.0)
                    {
                        float mfTotal = 0f;
                        var nets = src.Networks;
                        for (int i = 0; i < nets.Count; i++)
                            if (nets[i].MassFlow > 0f) mfTotal += nets[i].MassFlow;
                        double got = 0.0;
                        if (mfTotal > 0f)
                        {
                            for (int i = 0; i < nets.Count; i++)
                            {
                                var n = nets[i];
                                if (n.MassFlow <= 0f || n.propellant == null) continue;
                                double share = dm * ((double)n.MassFlow / mfTotal);
                                got += ConsumeGroupPropellant(group, n, (float)share);
                            }
                        }
                        // 料尽 ⇒ 显式退出（与悬浮解析档同一条纪律）
                        if (got < dm * 0.5)
                        {
                            group.transferActive = false;
                            PhysicsLogMessage($"[搬运] G{group.GroupId} **料尽** ⇒ 退出搬运（不静默；已到 u={u:F2}）");
                            continue;
                        }
                    }
                }

                if (u >= 1.0)
                {
                    group.transferActive = false;
                    group.CurrentOrbit.positionSystem = (SystemVec3)group.transferToPos;   // **精确落位**
                    group.CurrentOrbit.velocitySystem = (SystemVec3)group.transferToVel;
                    // 到达误差**当场量**（就在写完之后、同一步内）—— 事后重建会掺进"一步的位移"（判据侧踩过三次）
                    group.transferArrivalErrorMeters =
                        (group.CurrentOrbit.positionSystem.v - group.transferToPos).magnitude;
                    group.OrbitDirty = true;
                    if (!group.transferArriveLogged)
                    {
                        group.transferArriveLogged = true;
                        PhysicsLogMessage($"[搬运] G{group.GroupId} **到达**（解析档）：Δv 记账 {group.transferDvTotal:F1} m/s｜落位误差 {group.transferArrivalErrorMeters:F3} m｜余料 {src.TotalPropellantMass:F0} kg");
                    }
                    // **到点恢复悬浮**（起搬前在悬着的话）：大气层里"搬完不悬"= 开始掉（实测 30 km 掉到地面、
                    // 撞地 339.7 m/s ⇒ 坠毁 + 地图销毁）。恢复点 = 到达点（`SetHoverCore` 按**当前位置**锁点 ✓）。
                    TryResumeHoverAfterTransfer(group, src, "到达");
                }
                else if (!group.transferMidLogged && u >= 0.5)
                {
                    group.transferMidLogged = true;   // 中点报一行（长搬运才有；防"整段无声"）
                    PhysicsLogMessage($"[搬运] G{group.GroupId} 中点 u=0.50｜余料 {src.TotalPropellantMass:F0} kg");
                }
            }
        }

        /// <summary>
        /// **悬浮控制（每物理步，主线程）**：定点误差 → 指令加速度 → 全矢量推力 → 冲量 + 扣料。
        ///
        /// 口径（本刀，与计划稿 §2.6 一致）：
        ///   · 指令 = `HoverCore.CommandAccel(fieldAcc, posErr, velErr)`（**每步刷新引力抵消**，
        ///     台架实测这比"常推力"抖幅小得多，且对 dt 不敏感 —— 见 `HoverBenchTests`）；
        ///   · 推力**只走全矢量（RCS / 垂直推进器）网**：悬浮要锁铅垂（D10），主引擎会对准而把船转歪；
        ///     本刀不转船体，RCS 不够就是"推力不足"（如实报，不偷偷给主网）。
        ///   · 包线与进气**照常生效**（吸气式 RCS 在高空一样衰减/抽干）⇒ 悬浮不能绕开大气模型。
        ///   · 冲量进 `pendingImpulse` ⇒ 本步 `ApplyPendingImpulses` 立即生效（与手动操舵同相）。
        /// </summary>
        private void ApplyHover(double dtSeconds, double physicsTick, CelestialBodyManager mgr)
        {
            if (hoverActiveCount <= 0 || !(dtSeconds > 0.0)) return;

            foreach (var group in groups.Values)
            {
                if (!group.hoverActive) continue;
                if (!group.IsMovable || !group.IsActive) continue;

                var body = mgr.GetBodyById(group.CelestialBodyId);
                if (body == null) continue;
                var (tPos, tVel) = Launch.GetSurfaceState.HoverState(mgr, group.CelestialBodyId, body.radius,
                    group.hoverLatRad, group.hoverLonRad0, group.hoverAltitudeMeters, physicsTick);
                var pos = group.CurrentOrbit.positionSystem.v;
                var vel = group.CurrentOrbit.velocitySystem.v;

                // 本地场（主线程算一次；掩码/多体都在里面 ⇒ 与积分用的是同一个场）
                var sources = ResolveSystemSources(mgr, group, physicsTick, hoverScratch);
                ComputeGroupFieldAccelJerk(group, mgr, physicsTick, hoverScratch, sources,
                    out DVector3 fieldAcc, out _, out _, out _, timeSegment: false);

                // ⚠ 只做**半步重力回正**（实测已验：稳态偏置 162 m ⇒ 5.1 m）；**定点前馈暂不加** ——
                //   1-D 台架验证不了它（没建模切向运动，符号/相位反复翻）⇒ 另案：要加就配 2-D 台架。
                //   残留的 +5.1 m = `ω²r/Kp`（PD 不带前馈时的固有偏置），是**已知且已量化**的。
                DVector3 aCmd = HoverCore.CommandAccel(fieldAcc, tPos - pos,
                    tVel - HoverCore.DebiasVelocity(vel, fieldAcc, dtSeconds),   // 半步重力回正（见 HoverCore）
                    feedForwardAccel: Launch.GetSurfaceState.HoverAcceleration(mgr, group.CelestialBodyId, tPos, physicsTick));
                double dvWanted = aCmd.magnitude * dtSeconds;
                if (!(dvWanted > 1e-9)) continue;                        // 已在定点（死区内）⇒ 本步不点火

                // 全矢量网可用推力（含包线 + 空气门控；与燃烧路径同源的两条算式）
                var src = GetGroupPropulsionSource(group);
                if (src == null) continue;
                var nets = src.Networks;                                 // 解析档与物理档**同源**（两档账单才可比）

                // ── [A8] **解析档**：不出推力、不算误差 ⇒ 只按 D8 等效计费（`m·g·dt/ve`）扣料 ──
                //   位置由物理区的定点解析给出（见 ComputeGroupPhysics 的同名分支）⇒ 这里只管账。
                //   ⚠ 料尽必须**显式退出解析档**（D7：进出都必须写日志）——不然就成了"免费档"。
                if (group.hoverAnalytic)
                {
                    float veA = EffectiveFullVectorVe(src);
                    double gMagA = fieldAcc.magnitude;
                    double dvA = HoverCore.EquivalentDvPerStep(gMagA, dtSeconds);
                    if (veA <= 0f || !(dvA > 0.0))
                    {
                        LogTier(group, "解析档中止（没有可用全矢量 ve）⇒ 退出悬浮");
                        SetHover(group, false);
                        continue;
                    }
                    double dmA = GetGroupMass(group, src) * dvA / veA;
                    float mfA = 0f;
                    for (int i = 0; i < nets.Count; i++)
                    {
                        var nA = nets[i];
                        if (!nA.HasRcsEngines || nA.MassFlow <= 0f || nA.propellant == null) continue;
                        mfA += nA.MassFlow;
                    }
                    bool dryA = mfA <= 0f;
                    double gotA = 0.0;
                    if (!dryA)
                    {
                        for (int i = 0; i < nets.Count; i++)
                        {
                            var nA = nets[i];
                            if (!nA.HasRcsEngines || nA.MassFlow <= 0f || nA.propellant == null) continue;
                            double shareA = dmA * ((double)nA.MassFlow / mfA);
                            gotA += ConsumeGroupPropellant(group, nA, (float)shareA);
                        }
                        dryA = gotA < dmA * 0.5;   // 抽不够（料尽）⇒ 判干
                    }
                    if (dryA)
                    {
                        LogTier(group, "解析档**料尽** ⇒ 退出悬浮（不静默）");
                        SetHover(group, false);
                        continue;
                    }
                    // 解析档的读数（**必须打**：这一档的看点就是"位置被钉死" ⇒ 区间 min/max 宽度应为 0）
                    if (DebugDetailedSteering)
                    {
                        double rNowA = pos.magnitude;
                        DVector3 radHatA = rNowA > 1e-9 ? pos * (1.0 / rNowA) : DVector3.up;
                        double eA = DVector3.Dot(pos - tPos, radHatA);
                        double vA = DVector3.Dot(vel, radHatA);
                        group.hoverLogErrSum += eA; group.hoverLogVSum += vA; group.hoverLogCount++;
                        if (eA < group.hoverLogErrMin) group.hoverLogErrMin = eA;
                        if (eA > group.hoverLogErrMax) group.hoverLogErrMax = eA;
                        group.hoverLogAccumSeconds += dtSeconds;
                        if (group.hoverLogAccumSeconds >= HoverLogIntervalSeconds)
                        {
                            double nA = Math.Max(group.hoverLogCount, 1);
                            PhysicsLogMessage($"[悬浮·解析档] G{group.GroupId} 高度差 现{eA:F3}｜区间[{group.hoverLogErrMin:F3},{group.hoverLogErrMax:F3}]均{group.hoverLogErrSum / nA:F3}｜径向速度 现{vA:F3}｜横向{(tPos - pos).magnitude:F2}｜**本步记账 Δv={dvA:F4} m/s ⇒ 耗料 {gotA:F3} kg**｜料{src.TotalPropellantMass:F0} kg");
                            group.hoverLogAccumSeconds = 0.0;
                            group.hoverLogErrMin = double.MaxValue;
                            group.hoverLogErrMax = double.MinValue;
                            group.hoverLogErrSum = 0.0;
                            group.hoverLogVSum = 0.0;
                            group.hoverLogCount = 0;
                        }
                    }
                    continue;
                }
                var hoverSlices = hoverSliceScratch;
                hoverSlices.Clear();
                double fTotal = 0.0, mfTotal = 0.0;
                for (int i = 0; i < nets.Count; i++)
                {
                    var n = nets[i];
                    if (!n.HasRcsEngines || n.MassFlow <= 0f) continue;
                    if (n.propellant == null || n.AvailableMass(src) <= 0f) continue;

                    float envFactor = 1f;
                    if (n.envelopeTerms != null)
                    {
                        double rhoH = 0.0;
                        DVector3 vAirH = DVector3.zero;
                        if (TryGetAeroContext(group, mgr, pos, vel, out double rhoCtx, out DVector3 vAirCtx))
                        { rhoH = rhoCtx; vAirH = vAirCtx; }
                        double phiH = AirBreathingCore.EnvelopeFactor(n.envelopeTerms, n.Thrust, rhoH, body.Atmosphere.rho0,
                            AirBreathingCore.MachFromAirSpeed(vAirH.magnitude));
                        if (!(phiH > 0.0)) continue;                     // 真空 ⇒ 吸气式网不出力（D5 同一条门控）
                        envFactor = (float)phiH;
                    }
                    float cap = 1f;
                    if (n.gasDraws != null)
                    {
                        for (int g = 0; g < n.gasDraws.Count; g++)
                        {
                            var gd = n.gasDraws[g];
                            if (gd.propellant == null) continue;
                            double oneCap = AirBreathingCore.IntakeThrottleCap(src.GetPropellantMass(gd.propellant),
                                dtSeconds, gd.flowKgPerSec, n.MassFlow);
                            if (oneCap < cap) cap = (float)oneCap;
                        }
                        if (cap <= 0f) continue;                         // 空气抽干 ⇒ 熄火（悬浮也不例外）
                    }
                    float fNet = (float)(n.Thrust * (double)cap * envFactor);
                    hoverSlices.Add(new ThrustSlice { network = n, thrust = fNet, massFlow = n.MassFlow * cap });
                    fTotal += fNet;
                    mfTotal += n.MassFlow * cap;
                }

                double mass = GetGroupMass(group, src);

                // ── [A7] 周期读数（**按物理秒累积**，不按 tick 差 —— 2026-10-06 实测踩过：
                //    这个 mod 里 1 tick ≈ 1 物理秒，写成 `TicksGame + 600` 本意"10 秒"实际成了 10 分钟，
                //    整段悬浮只打出一行。放在"推力不足"检查**之前**：料尽那一刻也要看得见。──
                if (DebugDetailedSteering)
                {
                    DVector3 dPosR = pos - tPos;
                    double rNow = pos.magnitude;
                    DVector3 radialHat = rNow > 1e-9 ? pos * (1.0 / rNow) : DVector3.up;
                    double eNow = DVector3.Dot(dPosR, radialHat);        // 正 = 高于目标
                    double vRadNow = DVector3.Dot(vel, radialHat);       // 正 = 上升
                    double gRadNow = DVector3.Dot(fieldAcc, radialHat);

                    // 区间统计（2026-10-06 第三轮实测后加）：单点读数**看不出**"真在漂"还是"相位锁定的振荡"
                    // （上一轮读数里"高度差恒定 + 径向速度恒定"两者自相矛盾，缺的就是这一组数）。
                    group.hoverLogErrSum += eNow;
                    group.hoverLogVSum += vRadNow;
                    group.hoverLogCount++;
                    if (eNow < group.hoverLogErrMin) group.hoverLogErrMin = eNow;
                    if (eNow > group.hoverLogErrMax) group.hoverLogErrMax = eNow;

                    if (group.hoverLogCount == 1)   // 首次给基线（开悬浮那一刻的半径/场/ρ）
                    {
                        group.hoverLogRadius0 = rNow;
                        PhysicsLogMessage($"[悬浮] G{group.GroupId} 起点：船半径 {rNow:F1} m｜目标半径 {tPos.magnitude:F1} m｜场径向 {gRadNow:F4} m/s²｜定点高 {group.hoverAltitudeMeters:F1} m｜ρ {(TryGetAeroContext(group, mgr, pos, vel, out double rho0L, out _) ? rho0L : 0.0):F5} kg/m³");
                    }

                    group.hoverLogAccumSeconds += dtSeconds;
                    if (group.hoverLogAccumSeconds >= HoverLogIntervalSeconds)
                    {
                        double n = Math.Max(group.hoverLogCount, 1);
                        PhysicsLogMessage($"[悬浮] G{group.GroupId} 高度差 现{eNow:F1}｜区间[{group.hoverLogErrMin:F1},{group.hoverLogErrMax:F1}]均{group.hoverLogErrSum / n:F1}｜径向速度 现{vRadNow:F2}均{group.hoverLogVSum / n:F2}｜半径 船{rNow:F1}(起始{group.hoverLogRadius0:F1})目标{tPos.magnitude:F1}｜场径向{gRadNow:F4}｜横向现{(dPosR - radialHat * eNow).magnitude:F1}｜速度误差{(vel - tVel).magnitude:F2}｜指令{dvWanted:F3} 实给{(fTotal > 0.0 ? Math.Min(dvWanted, mass > 0.0 ? fTotal / mass * dtSeconds : 0.0) : 0.0):F3}｜可用a{(mass > 0.0 ? fTotal / mass : 0.0):F3}｜料{src.TotalPropellantMass:F0} kg");
                        group.hoverLogAccumSeconds = 0.0;
                        group.hoverLogErrMin = double.MaxValue;
                        group.hoverLogErrMax = double.MinValue;
                        group.hoverLogErrSum = 0.0;
                        group.hoverLogVSum = 0.0;
                        group.hoverLogCount = 0;
                    }
                }

                if (!(fTotal > 0.0) || !(mfTotal > 0.0))
                {
                    if (!group.hoverShortfallLogged)
                    {
                        group.hoverShortfallLogged = true;
                        // 读数要能自解释（2026-10-06 首轮实测的教训）：只写"推力不足"会让下一个人
                        // 以为是"没装 RCS"，而实测的真相是"**全矢量网被抽干了**"（场景把船扔成惯性零速、
                        // 控制器去追 523 m/s 的地面共转目标 ⇒ 3000 kg 烧光）。⇒ 网数/余料一并报出来。
                        int rcsNets = 0, rcsDry = 0;
                        for (int i = 0; i < nets.Count; i++)
                        {
                            if (!nets[i].HasRcsEngines || nets[i].MassFlow <= 0f) continue;
                            rcsNets++;
                            if (nets[i].propellant == null || nets[i].AvailableMass(src) <= 0f) rcsDry++;
                        }
                        PhysicsLogWarning($"[悬浮] G{group.GroupId} 推力不足 ⇒ 本步不纠偏（会掉）｜全矢量网 {rcsNets} 条（其中 **料尽/无料** {rcsDry} 条）｜全组余料 {src.TotalPropellantMass:F0} kg｜可用 a=0");
                        // **料尽 ⇒ 显式退出**（2026-10-07 自动判据抓到：原先物理档料尽后 `hoverActive` 仍是 true，
                        //   等于"嘴上还悬着、身子在掉" —— 与设计稿引的先例"燃料干涸自动退出"（§1.2）相悖，
                        //   也与解析档的料尽退出不对称）。只有**真的没料**才退；"没装网"这类留着让人看见。
                        if (HoverCore.ShouldExitHoverWhenDry(rcsNets, rcsDry))
                        {
                            LogTier(group, "物理档**料尽** ⇒ 退出悬浮（不静默：状态不再谎报'在悬浮'）");
                            SetHover(group, false);
                        }
                    }
                    continue;
                }

                double aAvail = fTotal / Math.Max(mass, 1.0);
                double dvApply = Math.Min(dvWanted, aAvail * dtSeconds);   // 给不出就只给得起的量
                double throttle = dvWanted > 0.0 ? dvApply / dvWanted : 0.0;
                group.pendingImpulse += aCmd * (dvApply / Math.Max(aCmd.magnitude, 1e-12));
                group.hoverLastDvCmd = dvWanted;
                group.hoverLastDvApplied = dvApply;
                group.hoverLastThrustN = fTotal;

                // 扣料：耗料 = 等效质量流 × 实际油门（= m·dv/ve 的同一条账，走既有唯一消耗口）
                double dm = mfTotal * dtSeconds * throttle;
                for (int i = 0; i < hoverSlices.Count; i++)
                {
                    var s = hoverSlices[i];
                    double share = mfTotal > 0.0 ? dm * (s.massFlow / mfTotal) : 0.0;
                    if (share > 0.0)
                        ConsumeGroupPropellant(group, s.network, (float)share);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  [A8] 点到点解析搬运（解析档的"搬运/巡航"）
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// **起一次解析搬运**（唯一入口）：把船从**当前位置**搬到给定目标状态，按 `Δv 总额` 计时计费。
        ///
        /// 准入（D8"付不起直接拒绝" + [A6]"解析档与气动互斥"）：
        /// · 有引擎（`ve > 0`）—— 没引擎就没"等效 dv"可付；
        /// · **起点与终点都不在大气里**（ρ = 0）—— 大气是物理档的属地，解析档不模拟受力 ✓ 与解析圆轨同一条规矩。
        /// 进出**必须显式**（D7）：成与不成各一行日志（含 Δv 总额、时长、等效耗料率）。
        /// </summary>
        public bool StartAnalyticTransfer(SpaceEntityGroup group, DVector3 targetPos, DVector3 targetVel,
            double dvTotal, double physicsTick)
        {
            if (group == null || !(dvTotal > 0.0)) return false;
            var src = GetGroupPropulsionSource(group);
            if (src == null || src.EffectiveVe <= 0f)
            {
                PhysicsLogWarning($"[搬运] G{group.GroupId} 拒绝：没有引擎 ⇒ 付不起等效 Δv");
                return false;
            }
            float ve = src.EffectiveVe;
            var mgr = celestialRef.Get();
            if (mgr == null) return false;
            // ⚠ **大气层内是允许的**（2026-10-07 用户纠正）：搬运/巡航这一行与"解析圆轨"不是一回事 ——
            //   D8 说的是"解析档**不模拟受力**但**照扣料**"，不是"解析档不许进大气"。
            //   原先把 [A6] 的"解析圆轨不许进大气"越界套到了搬运上 ⇒ 把巡航关在了大气层外面（那是错的门）。
            //   这里只挡**真正不可能的事**：端点在地表以下（或天体中心）⇒ 拒绝。
            if (group.CurrentOrbit.positionSystem.v.magnitude <= mgr.GetBodyById(group.CelestialBodyId)?.radius
                || targetPos.magnitude <= 1e-6)
            {
                PhysicsLogWarning($"[搬运] G{group.GroupId} 拒绝：起点或终点在地表以下（几何不可能）");
                return false;
            }

            // **[D7②] 交战 ⇒ 解析档不可用**（用户 2026-10-08 裁定：用现成的"有射弹"信号，**不新建交战触发**）：
            //   解析搬运是"轨上"的（不模拟受力）⇒ 交战中不许用它（免"免疫扰动"的特权）。
            //   ⚠ 这道门必须在**任何副作用之前**（尤其别先把悬浮退掉再拒绝 ⇒ 船白丢一次定点）。
            if (CombatActionActive)
            {
                PhysicsLogWarning($"[搬运] G{group.GroupId} 拒绝起搬：**处于交战动作（有射弹在飞 / 导弹终末攻击）** ⇒ 解析档（轨上）不可用；交战结束后再搬，或改物理档机动");
                return false;
            }

            // **[模式互斥] 起搬 ⇒ 先显式退出悬浮**（D7"进出必须显式"）：
            //   悬浮 = 定点保持（每步把船按在锁点上），搬运 = 按航线每步推位置 —— **两种工作模式不能同时跑**。
            //   ⚠ 2026-10-07 用户实测："飞船疑似被锚定死了，走不出去" —— 就是这两条在互相打架（旧代码起搬不清悬浮、
            //     悬浮也不看搬运单）。这里改成一件事：**玩家要它走，就先把按着它的手松开**。
            //   ⚠ 同时**记住"原来在悬着"**：大气层里没有悬浮就等于开始掉（实测：到达后 30 km 一路掉到地面，
            //     撞地 339.7 m/s ⇒ 坠毁）。巡航的语义 = "搬过去、然后继续悬停" ⇒ **到点自动恢复原档位**。
            if (group.hoverActive)
            {
                group.hoverResumeAfterTransfer = true;
                group.hoverResumeAnalytic = group.hoverAnalytic;
                SetHoverCore(group, false);
                PhysicsLogMessage($"[档位] G{group.GroupId} 起搬 ⇒ **退出悬浮**（搬运与定点保持互斥）——**到点自动恢复{(group.hoverResumeAnalytic ? "解析" : "物理")}档悬浮**");
            }
            else
            {
                group.hoverResumeAfterTransfer = false;
                group.hoverResumeAnalytic = false;
            }

            var pos = group.CurrentOrbit.positionSystem.v;
            var vel = group.CurrentOrbit.velocitySystem.v;
            double mass = GetGroupMass(group, src);
            double aAvg = GetGroupAvailableAccel(group, src);

            group.transferFromPos = pos;
            group.transferFromVel = vel;
            group.transferToPos = targetPos;
            group.transferToVel = targetVel;
            group.transferDvTotal = dvTotal;
            group.transferStartTick = physicsTick;
            group.transferDuration = AnalyticTransferCore.Duration(dvTotal, aAvg);
            group.transferActive = true;
            group.transferMidLogged = false;
            group.transferArriveLogged = false;
            PhysicsLogMessage($"[搬运] G{group.GroupId} 起搬（解析档）：Δv={dvTotal:F1} m/s｜时长={group.transferDuration:F1} s｜可用 a≈{aAvg:F3} m/s²｜等效耗料率≈{AnalyticTransferCore.PropellantThisStep(mass, dvTotal, ve) / Math.Max(group.transferDuration, 1e-9):F2} kg/s｜余料 {src.TotalPropellantMass:F0} kg");
            return true;
        }

        /// <summary>
        /// **中止解析搬运**（玩家从控制台按「停止」）：清搬运单、**留在当前位置与当前速度**（不"精确落位"到目标）。
        /// 与料尽退出同一条纪律 —— **显式**（一行日志），不许静默消失。
        /// </summary>
        public bool StopAnalyticTransfer(SpaceEntityGroup group, string reason)
        {
            if (group == null || !group.transferActive) return false;
            double u = AnalyticTransferCore.Progress(
                TimeScaleManager.GetPhysicsTickSafe() - group.transferStartTick, group.transferDuration);
            group.transferActive = false;
            group.OrbitDirty = true;
            PhysicsLogMessage($"[搬运] G{group.GroupId} **中止**（{(reason ?? "无理由")}）：停在当前位置（已到 u={u:F2}）｜Δv 只记到这儿");
            return true;
        }

        /// <summary>本处可用推力加速度（解析搬运估时用）。</summary>
        private double GetGroupAvailableAccel(SpaceEntityGroup group, IPropulsionSource src)
        {
            float f = 0f;
            var nets = src.Networks;
            for (int i = 0; i < nets.Count; i++) f += nets[i].Thrust;
            double mass = GetGroupMass(group, src);
            return mass > 0.0 ? f / mass : 0.0;
        }

        /// <summary>该点是否在大气里（ρ > 0）—— 与解析圆轨守卫**同一条口径**（`AtmosphereModel.IsInsideAtmosphere`）。</summary>
        private static bool IsInsideAtmosphere(SpaceEntityGroup group, CelestialBodyManager mgr, DVector3 pos)
        {
            var body = mgr.GetBodyById(group.CelestialBodyId);
            if (body == null || !body.Atmosphere.IsActive) return false;
            double r = pos.magnitude;
            if (!(r > body.radius)) return true;      // 地表以下也算"大气里"（保守）
            return body.Atmosphere.IsInsideAtmosphere(r - body.radius);
        }
    }
}