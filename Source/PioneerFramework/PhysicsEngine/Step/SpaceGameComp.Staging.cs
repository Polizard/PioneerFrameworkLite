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

namespace PioneerFramework
{
    /// <summary>
    /// **宿主·抛级 / 载荷 / 自动分级**（2026-10-31 类重组批 4b：从 `SpaceGameComp.cs` 搬出的 partial 文件）。
    /// 纯文件搬家、零语义改动（同一个 partial class，访问权限不变）；
    /// 真正拆成独立服务要等批 5 之后，因为这里大量使用宿主的私有状态（`payloadDesigns` / `autoStagePlans`）。
    /// </summary>
    public partial class SpaceGameComp
    {
        // ── 载荷注册表（2026-10-26 用户裁定 A 方案）──
        // 载荷舱组件只存 id；载荷的零件表在这里（随存档落盘）。**载荷无 Map**（同裁定）。
        private Dictionary<int, GroundAssembly.RocketDesignSpec> payloadDesigns = new();
        private int nextPayloadDesignId = 1;

        /// <summary>注册一份载荷设计（深拷随存档；调用方之后改原件不影响已注册条目由 Scribe 深拷保证）。
        /// 返回 id（&gt;0）；`spec == null` ⇒ -1。</summary>
        public int RegisterPayloadDesign(GroundAssembly.RocketDesignSpec? spec)
        {
            if (spec == null) return -1;
            int id = nextPayloadDesignId++;
            payloadDesigns[id] = spec;
            return id;
        }

        /// <summary>按 id 取载荷设计（未注册 ⇒ false）。</summary>
        public bool TryGetPayloadDesign(int id, out GroundAssembly.RocketDesignSpec spec)
        {
            if (payloadDesigns != null && payloadDesigns.TryGetValue(id, out var found) && found != null)
            {
                spec = found;
                return true;
            }
            spec = null!;
            return false;
        }

        /// <summary>下一个可用载荷设计 id（>0）。</summary>
        public int NextPayloadDesignId => nextPayloadDesignId;

        /// <summary>
        /// **释放载荷**（2026-10-26；用户口径：载荷舱销毁自己、原地放出同位置/同速度的新 Group）。
        ///
        /// 顺序：注册表取设计 → 装配（**无舱盒**）→ 新组（逐字段拷贝父组轨道状态）→ 注册实体/组 →
        /// 父舰 `RemovePart(舱)`（正常分离，不走 `MarkPartDestroyed`，不触发漏光/关键件失能）。
        /// 载荷**无 Map**（已裁）。失败返回 null 并给 `reason`（不抛、不静默改状态）。
        /// </summary>
        public SpaceEntityGroup? TryDeployPayload(SpaceEntityGroup parent, SpacePart bayPart, out string reason)
        {
            reason = "";
            if (parent == null || bayPart == null) { reason = "invalid-args"; return null; }

            var bay = bayPart.GetComponent<Components.PayloadBayComponentData>();
            if (bay == null || bay.payloadDesignId <= 0) { reason = "no-payload-design-id"; return null; }
            if (!TryGetPayloadDesign(bay.payloadDesignId, out var spec)) { reason = "payload-design-missing"; return null; }

            SpaceEntity? owner = null;
            foreach (int eid in parent.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e != null && e.GetPartById(bayPart.partId) != null) { owner = e; break; }
            }
            if (owner == null) { reason = "bay-part-not-found"; return null; }

            var parts = GroundAssembly.DesignPartAssembler.BuildParts(
                spec, 1, 1, out _, includeUnknownDefs: false, includeCabin: false);
            if (parts.Count == 0) { reason = "empty-payload"; return null; }

            int gid = AllocGroupId();
            var group = new SpaceEntityGroup(gid, parent.FactionId)
            {
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = parent.CelestialBodyId,
            };
            // 出生状态 = 父组**逐字段拷贝**（位置/速度/姿态/角速度）——用户要求的"速度和原先 Group 一样"。
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = parent.CurrentOrbit.positionSystem,
                velocitySystem = parent.CurrentOrbit.velocitySystem,
                rotation = parent.CurrentOrbit.rotation,
            };
            group.angularVelocity = parent.angularVelocity;
            RegisterGroup(group, GroupBirthKind.Continuation, parent);   // 同体切分：出生状态 = 父组已提交态逐字段拷贝

            var entity = new SpaceEntity(AllocEntityId(), parent.FactionId)
            {
                GroupId = gid,
                HasMap = false,
                IsMovable = true,
                IsAffectedByGravity = true,
                CanInteract = false,
            };
            entity.SetParts(parts);
            RegisterEntity(entity);   // 会把实体加进组，并触发零件→推进合成

            owner.RemovePart(bayPart.partId);   // 载荷舱销毁自己（正常移除）
            return group;
        }

        /// <summary>
        /// 抛级时的**相对分离速度**（m/s，沿"两半质心连线"把被抛级推开）。0 = 只做刚体分裂、不推开。
        /// 真实分离机构量级 0.3–2 m/s；这里取 1.0（2 s 步长 ⇒ 一步分开 2 m）。
        /// </summary>
        internal const double StageSeparationRelativeSpeedMps = 1.0;

        /// <summary>
        /// **按级号抛级**（2026-10-27 用户裁定：不需要分离器部件；2026-11-03 扩成多块）。
        ///
        /// 兼容入口：单块返回该块组；多块返回**第一块**（完整列表用 <see cref="TrySeparateStageMulti"/>）。
        /// </summary>
        public SpaceEntityGroup? TrySeparateStage(SpaceEntityGroup parent, out string reason)
        {
            var groups = TrySeparateStageMulti(parent, out reason);
            return groups.Count > 0 ? groups[0] : null;
        }

        /// <summary>碎片寿命：90 游戏秒 = 5,400 物理 tick（1 游戏 tick = 1 物理秒，1x 60 tick/秒；教训 L6ysoa4vi）。</summary>
        internal const double DebrisLifetimePhysicsTicks = 90.0 * 60.0;

        /// <summary>
        /// **多块抛级**（2026-11-03）：把 maxStage 的**所有块**一次抛成 N 个组（每块一个组）。
        ///
        /// · 单块走旧两半路径（数值逐位不变）；多块走 <see cref="StageSeparationCore.SolveMany"/>。
        /// · `Keep` 级拒绝（`stage-kept`）；子级在场拒绝（`child-stage-attached`）；多实体仍拒绝。
        /// · 抛出的组是**碎片**：`IsCollidable=false`、`debrisExpireTick = now + 5400`、
        ///   `debrisPolicy` 按设计期级标签（Scrap / Recoverable）。
        /// </summary>
        public IReadOnlyList<SpaceEntityGroup> TrySeparateStageMulti(SpaceEntityGroup parent, out string reason)
        {
            reason = "";
            if (parent == null) { reason = "invalid-args"; return Array.Empty<SpaceEntityGroup>(); }

            SpaceEntity? owner = null;
            int entityCount = 0;
            int max = 0;
            foreach (int eid in parent.GetEntityIds())
            {
                entityCount++;
                var e = GetEntityById(eid);
                if (e == null) continue;
                owner ??= e;
                int m = StagePartition.MaxStage(e.PartsList);
                if (m > max) max = m;
            }
            // 没有可抛的级（含空组）：保持 2026-10-27 的 reason 契约
            if (max <= 0) { reason = "single-stage"; return Array.Empty<SpaceEntityGroup>(); }
            if (owner == null) { reason = "no-entity"; return Array.Empty<SpaceEntityGroup>(); }
            // 本期只做单实体组（多实体要按实体拆/合，另案；硬拆会把两个实体并成一个）。
            if (entityCount > 1) { reason = "multi-entity"; return Array.Empty<SpaceEntityGroup>(); }

            var all = owner.PartsList;
            // 规则（2026-11-03）：Keep 级拒绝；子级在场时父级拒绝。两条都在任何状态修改之前。
            if (StagePartition.IsKeptStage(all, max)) { reason = "stage-kept"; return Array.Empty<SpaceEntityGroup>(); }
            if (StagePartition.HasAttachedChildStage(all, max))
            {
                reason = "child-stage-attached";
                return Array.Empty<SpaceEntityGroup>();
            }

            // 解析档（圆轨）：位置/速度每步由 circular* 重新导出 ⇒ 重定会被下一步覆盖。
            // 2026-10-30 用户裁定：**不拒绝，先把圆轨物化（退出解析档）再分离**。放在拒绝之后，拒绝路径不改状态。
            if (parent.IsCircularOrbit)
            {
                parent.ExitCircularOrbit();      // 写回当前位置/速度并清 circular* 参数
                if (Prefs.DevMode)
                    Log.Message($"[抛级] G{parent.GroupId} 先退出圆轨解析档（物化位置/速度）再分离");
            }

            var blocks = StagePartition.BlocksOf(all, max);
            if (blocks.Count == 0) { reason = "empty-stage"; return Array.Empty<SpaceEntityGroup>(); }

            var disposition = StagePartition.DispositionOf(all, max);
            var created = blocks.Count == 1
                ? SeparateSingleBlock(parent, owner, max, disposition, out reason)
                : SeparateManyBlocks(parent, owner, max, blocks, disposition, out reason);
            return created ?? (IReadOnlyList<SpaceEntityGroup>)Array.Empty<SpaceEntityGroup>();
        }

        /// <summary>旧两半路径（单块）：与 2026-10-30 的算式逐位一致，只额外打碎片标记。</summary>
        private List<SpaceEntityGroup>? SeparateSingleBlock(SpaceEntityGroup parent, SpaceEntity owner, int max,
            StageDisposition disposition, out string reason)
        {
            reason = "";
            var all = owner.PartsList;

            // 两半的质量/质心（一次遍历；质量口径与 InertiaTensor.Compose 一致）
            var keptMass = new SeparationMass();
            var droppedMass = new SeparationMass();
            StageSeparation.Accumulate(owner, max, ref keptMass, ref droppedMass);
            if (keptMass.mass <= 0.0) { reason = "would-orphan"; return null; }

            // 刚体分裂解算（原点/速度/方向/相对速度）；退化几何在这里被拒（coincident-centers 等）
            if (!StageSeparationCore.Solve(keptMass, droppedMass,
                    parent.CurrentOrbit.positionSystem.v, parent.CurrentOrbit.velocitySystem.v,
                    parent.angularVelocity, parent.CurrentOrbit.rotation,
                    StageSeparationRelativeSpeedMps, out var sol))
            {
                reason = sol.reason;
                return null;
            }

            // 切件（**批量一次失效**）：先把被抛件的坐标重定到子组原点，再一次性移除
            var dropped = new List<SpacePart>();
            var droppedIds = new List<int>();
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null || p.stageId != max) continue;
                dropped.Add(p);
                droppedIds.Add(p.partId);
            }
            if (dropped.Count == 0) { reason = "empty-stage"; return null; }

            for (int i = 0; i < dropped.Count; i++)
                dropped[i].positionShip -= sol.centerB;      // 子组原点 = 被抛级质心
            owner.RemoveParts(droppedIds);
            owner.RecenterParts(sol.centerA);                // 母舰原点 = 保留下来的那半质心

            var group = CreateSeparatedGroup(parent, owner, dropped, sol.originB, sol.velocityB, disposition);
            FinishSeparation(parent, sol.originA, sol.velocityA, new[] { group });
            return new List<SpaceEntityGroup> { group };
        }

        /// <summary>N 块路径：一次解算、一次移除、逐块建组；任一块退化 ⇒ 整次拒绝（先校验后提交）。</summary>
        private List<SpaceEntityGroup>? SeparateManyBlocks(SpaceEntityGroup parent, SpaceEntity owner, int max,
            List<int> blocks, StageDisposition disposition, out string reason)
        {
            reason = "";
            var all = owner.PartsList;

            var coreMass = new SeparationMass();
            var blockMass = new SeparationMass[blocks.Count];
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null) continue;
                double m = StageSeparation.PartMass(p);
                if (p.stageId != max)
                {
                    StageSeparationCore.Add(ref coreMass, m, p.positionShip);
                    continue;
                }
                int idx = BlockIndex(blocks, p.blockId);
                if (idx >= 0) StageSeparationCore.Add(ref blockMass[idx], m, p.positionShip);
            }

            if (coreMass.mass <= 0.0) { reason = "would-orphan"; return null; }
            for (int i = 0; i < blockMass.Length; i++)
                if (blockMass[i].mass <= 0.0) { reason = "empty-block"; return null; }

            if (!StageSeparationCore.SolveMany(coreMass, blockMass,
                    parent.CurrentOrbit.positionSystem.v, parent.CurrentOrbit.velocitySystem.v,
                    parent.angularVelocity, parent.CurrentOrbit.rotation,
                    StageSeparationRelativeSpeedMps, out var sol))
            {
                reason = sol.reason;
                return null;
            }

            var byBlock = new List<SpacePart>[blocks.Count];
            var droppedIds = new List<int>();
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null || p.stageId != max) continue;
                int idx = BlockIndex(blocks, p.blockId);
                if (idx < 0) continue;
                (byBlock[idx] ??= new List<SpacePart>()).Add(p);
                droppedIds.Add(p.partId);
            }
            if (droppedIds.Count == 0) { reason = "empty-stage"; return null; }

            for (int i = 0; i < byBlock.Length; i++)
            {
                var list = byBlock[i];
                if (list == null) continue;
                for (int j = 0; j < list.Count; j++)
                    list[j].positionShip -= sol.blockCenters[i];   // 每块原点 = 自身质心
            }

            owner.RemoveParts(droppedIds);
            owner.RecenterParts(sol.coreCenter);

            var created = new List<SpaceEntityGroup>(blocks.Count);
            for (int i = 0; i < blocks.Count; i++)
            {
                var list = byBlock[i];
                if (list == null || list.Count == 0) { reason = "empty-block"; return null; }
                created.Add(CreateSeparatedGroup(parent, owner, list,
                    sol.blockOrigins[i], sol.blockVelocities[i], disposition));
            }
            FinishSeparation(parent, sol.coreOrigin, sol.coreVelocity, created);
            return created;
        }

        private static int BlockIndex(List<int> blocks, int blockId)
        {
            for (int i = 0; i < blocks.Count; i++)
                if (blocks[i] == blockId) return i;
            return -1;
        }

        /// <summary>建一个分离出来的组 + 实体；统一打碎片标记（2026-11-03）。</summary>
        private SpaceEntityGroup CreateSeparatedGroup(SpaceEntityGroup parent, SpaceEntity owner,
            List<SpacePart> parts, in DVector3 position, in DVector3 velocity, StageDisposition disposition)
        {
            int gid = AllocGroupId();
            var group = new SpaceEntityGroup(gid, parent.FactionId)
            {
                IsMovable = true,
                IsCollidable = false,             // 碎片：不参与船-船碰撞/命中（2026-11-03）
                CelestialBodyId = parent.CelestialBodyId,
                debrisExpireTick = DebrisExpireNow(),
                debrisPolicy = disposition == StageDisposition.JettisonRecoverable
                    ? DebrisPolicy.Recoverable
                    : DebrisPolicy.Scrap,
            };
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)position,
                velocitySystem = (SystemVec3)velocity,
                rotation = parent.CurrentOrbit.rotation,
            };
            group.angularVelocity = parent.angularVelocity;
            RegisterGroup(group, GroupBirthKind.Continuation, parent);   // 同体切分：位姿由分离核按父组已提交态解出

            var entity = new SpaceEntity(AllocEntityId(), parent.FactionId)
            {
                GroupId = gid,
                HasMap = false,
                IsMovable = true,
                IsAffectedByGravity = true,
                CanInteract = false,
            };
            // 2026-10-30：**碰撞用实体旋转**（`CollectPartAabbs` 读 `entity.Rotation`），不复制就会错向。
            entity.SetRotation(owner.Rotation);
            entity.SetParts(parts);
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            RegisterEntity(entity);
            PartPropulsionBuilder.BuildForMapless(entity);   // 碎片也是"真船"：罐里剩的油要算进质量
            return group;
        }

        /// <summary>母舰：新原点/速度 + 诊断重基线 + 清扫掠历史 + 强制重建推进池（2026-10-28 裁定）。</summary>
        private void FinishSeparation(SpaceEntityGroup parent, in DVector3 coreOrigin, in DVector3 coreVelocity,
            IReadOnlyList<SpaceEntityGroup> created)
        {
            parent.CurrentOrbit.positionSystem = (SystemVec3)coreOrigin;
            parent.CurrentOrbit.velocitySystem = (SystemVec3)coreVelocity;
            parent.burnDiagTick = -1.0;
            Current.Game?.GetComponent<CombatManager>()?.ResetSweepHistory(parent);

            // **抛级后强制按"活动级罐"重建推进池**（2026-10-28 用户裁定）。
            // 实测分叉：抛级后 `网络池=2028 kg` 而 `罐件Σ=102028 kg` ⇒ 上面级明明满油却只肯烧 2 t
            // 接着就是"质量冻结"。这里不给时序留余地：移除完成后对每个母舰实体显式重建一次。
            // ⚠ 2026-10-30：`RecenterParts` 已触发过一次（`InvalidateStats` 收口），这里是**第二次幂等重建**
            //   ——保留它是因为"抛级后必须重建"这条是踩过坑的；一次 O(N) 分组 ~µs，不心疼。
            double poolBefore = 0.0, poolAfter = 0.0, tankAfter = 0.0;
            foreach (int eid in parent.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e == null) continue;
                poolBefore += SumPropulsionMass(e);
                if (!e.PropulsionFromParts && e.propulsion.Count > 0)
                    e.PropulsionFromParts = true;     // 零件合成的船：重建必须真的发生
                PartPropulsionBuilder.RebuildMapless(e);
                poolAfter += SumPropulsionMass(e);
                tankAfter += SumActiveStageTankMass(e);
            }
            if (Prefs.DevMode)
            {
                Log.Message($"[抛级·重建] G{parent.GroupId} 池 {poolBefore:F1} → {poolAfter:F1} kg"
                    + $"｜活动级罐Σ={tankAfter:F1} kg｜剩余件={parent.GetEntityIds().Count} 实体");
                for (int i = 0; i < created.Count; i++)
                {
                    var c = created[i];
                    Log.Message($"[抛级·块] G{parent.GroupId}→G{c.GroupId} 块 {i + 1}/{created.Count}"
                        + $"｜质量={DebrisMassOf(c):F0}kg｜碎片={c.debrisPolicy} 到期={c.debrisExpireTick:F0}");
                }
            }
        }

        /// <summary>碎片当前质量（结构 + 罐内工质；与分离核同口径）。</summary>
        private double DebrisMassOf(SpaceEntityGroup group)
        {
            double mass = 0.0;
            foreach (int eid in group.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e?.PartsList == null) continue;
                var parts = e.PartsList;
                for (int i = 0; i < parts.Count; i++) mass += StageSeparation.PartMass(parts[i]);
            }
            return mass;
        }

        /// <summary>碎片到期 tick = 当前物理时钟 + 5,400（无时钟的测试环境按 0 起算）。</summary>
        private double DebrisExpireNow()
        {
            var clock = timeScaleRef.Get();
            return (clock?.GetPhysicsTick() ?? 0.0) + DebrisLifetimePhysicsTicks;
        }

        // ── 碎片退役（2026-11-03）──────────────────────────────────
        private readonly List<int> debrisDueScratch = new();
        private readonly List<int> debrisRetireScratch = new();

        /// <summary>
        /// **碎片唯一退役出口**：`Scrap` 记天体垃圾（kg）；`Recoverable` 占位不记；最后 `DestroyGroup`。
        /// 到期与撞天体都走这里。
        /// </summary>
        internal void RetireDebris(SpaceEntityGroup group, string reason)
        {
            if (group == null) return;
            if (group.debrisPolicy == DebrisPolicy.Scrap)
            {
                double mass = DebrisMassOf(group);
                if (mass > 0.0)
                    Current.Game?.GetComponent<SpaceDebrisTracker>()?.AddDebris(group.CelestialBodyId, mass);
            }
            DestroyGroup(group.GroupId, "debris:" + reason);
        }

        /// <summary>每步末：到期碎片退役（收集后按 groupId 升序处理，确定性）。2026-11-03。</summary>
        internal void RetireDueDebris(double physicsTick)
        {
            var due = debrisDueScratch;
            due.Clear();
            foreach (var g in groups.Values)
            {
                if (g == null || !g.IsDebris) continue;
                if (g.debrisExpireTick > physicsTick) continue;
                due.Add(g.GroupId);
            }
            if (due.Count == 0) return;
            due.Sort();
            var ids = debrisRetireScratch;
            ids.Clear();
            ids.AddRange(due);
            for (int i = 0; i < ids.Count; i++)
                if (groups.TryGetValue(ids[i], out var g)) RetireDebris(g, "lifetime");
        }

        /// <summary>本实体全部推进网络的料重之和（kg）。</summary>
        private static double SumPropulsionMass(SpaceEntity e)
        {
            double sum = 0.0;
            if (e?.propulsion == null) return sum;
            for (int i = 0; i < e.propulsion.Count; i++)
                if (e.propulsion[i] != null) sum += e.propulsion[i].mass;
            return sum;
        }

        /// <summary>本实体**活动级**（最大 stageId）罐件的燃料之和（kg）——推进池的权威口径。</summary>
        private static double SumActiveStageTankMass(SpaceEntity e)
        {
            if (e == null) return 0.0;
            var parts = e.Parts;
            int active = 0;
            for (int i = 0; i < parts.Count; i++)
                if (parts[i] != null && !parts[i].isDestroyed && parts[i].stageId > active) active = parts[i].stageId;

            double sum = 0.0;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null || p.isDestroyed || p.stageId != active) continue;
                var t = p.GetComponent<Components.PropellantTankComponentData>();
                if (t != null && t.massKg > 0.0) sum += t.massKg;
            }
            return sum;
        }

        internal readonly List<AutoStagePlan> autoStagePlans = new();

        public void AddAutoStagePlan(int groupId, double stageEverySeconds, int stages)
        {
            if (stages <= 0 || stageEverySeconds <= 0.0) return;
            autoStagePlans.Add(new AutoStagePlan
            { GroupId = groupId, SecondsToStage = stageEverySeconds, StagesLeft = stages });
        }

        /// <summary>油尽抛级（2026-10-27）：当前活动级池 ≤ 容量×`fuelFraction` 且已烧 `minSeconds` ⇒ 抛最大级号。</summary>
        public void AddAutoStageFuelPlan(int groupId, double fuelFraction, int stages, double minSeconds)
        {
            if (stages <= 0 || fuelFraction <= 0.0 || fuelFraction >= 1.0) return;
            autoStagePlans.Add(new AutoStagePlan
            { GroupId = groupId, FuelFraction = fuelFraction, StagesLeft = stages, MinSeconds = minSeconds });
        }

        /// <summary>撤掉某群的自动抛级计划（不动组上的开关位）。</summary>
        public void CancelAutoStagePlan(int groupId)
            => autoStagePlans.RemoveAll(p => p.GroupId == groupId);

        /// <summary>玩家开关：状态落组上；开 ⇒ 建计划，关 ⇒ 撤计划。</summary>
        public void SetAutoStage(SpaceEntityGroup? group, bool enabled)
        {
            if (group == null) return;
            group.autoStageEnabled = enabled;
            CancelAutoStagePlan(group.GroupId);
            if (!enabled) return;
            AddAutoStageFuelPlan(group.GroupId, group.autoStageFuelFraction, stages: 99, group.autoStageMinSeconds);
        }

        /// <summary>读档后按组上的开关重建计划（计划列表本身不序列化）。</summary>
        internal void RebuildAutoStagePlans()
        {
            autoStagePlans.Clear();
            if (groups == null) return;
            foreach (var g in groups.Values)
                if (g.autoStageEnabled)
                    AddAutoStageFuelPlan(g.GroupId, g.autoStageFuelFraction, stages: 99, g.autoStageMinSeconds);
        }

        /// <summary>组内最大 stageId（跨实体；0 = 无可抛）。2026-11-03。</summary>
        private int MaxStageOfGroup(SpaceEntityGroup group)
        {
            int max = 0;
            foreach (int eid in group.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e?.PartsList == null) continue;
                int m = StagePartition.MaxStage(e.PartsList);
                if (m > max) max = m;
            }
            return max;
        }

        /// <summary>某级按块汇总的罐质量/容量（C-full 块罐占比；无罐的块不出现）。2026-11-03。</summary>
        private Dictionary<int, (double mass, double capacity)> StageTankSums(SpaceEntityGroup group, int stageId)
        {
            var sums = new Dictionary<int, (double mass, double capacity)>();
            foreach (int eid in group.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e?.PartsList == null) continue;
                var parts = e.PartsList;
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    if (p == null || p.isDestroyed || p.stageId != stageId) continue;
                    var tank = p.GetComponent<Components.PropellantTankComponentData>();
                    if (tank == null || tank.leaked || tank.capacityKg <= 0.0) continue;
                    sums.TryGetValue(p.blockId, out var cur);
                    sums[p.blockId] = (cur.mass + tank.massKg, cur.capacity + tank.capacityKg);
                }
            }
            return sums;
        }

        internal void StepAutoStagePlans(double dt)
        {
            if (dt <= 0.0 || autoStagePlans.Count == 0) return;
            for (int i = autoStagePlans.Count - 1; i >= 0; i--)
            {
                var plan = autoStagePlans[i];
                var g = GetGroupById(plan.GroupId);
                if (g == null || !g.IsActive) { autoStagePlans.RemoveAt(i); continue; }

                plan.Elapsed += dt;
                int stage = MaxStageOfGroup(g);
                if (stage <= 0) { autoStagePlans.RemoveAt(i); continue; }

                var sums = StageTankSums(g, stage);
                double pool = 0.0, cap = 0.0;
                foreach (var kv in sums)
                {
                    pool += kv.Value.mass;
                    cap += kv.Value.capacity;
                }

                plan.LogAccum += dt;
                if (plan.LogAccum >= 10.0)
                {
                    plan.LogAccum -= 10.0;
                    double frac = cap > 0.0 ? pool / cap : -1.0;
                    Log.Message($"[发射] G{g.GroupId} r={g.positionBody.magnitude / 1000.0:F1}km "
                        + $"v={g.velocityBody.magnitude:F0}m/s 待抛={plan.StagesLeft} 级={stage} "
                        + $"池={pool:F0}kg 罐容={cap:F0}kg 油比={frac:F2} 块={sums.Count} 燃烧={g.HasActiveBurn}");
                }

                bool due;
                if (plan.FuelFraction > 0.0)
                {
                    // C-full（2026-11-03）：候选 = 最大级（叶子）；**该级所有块**都低于阈值才抛整级（一次 N 组）。
                    due = sums.Count > 0;
                    foreach (var kv in sums)
                    {
                        if (!AutoStageCore.ShouldStageByFuel(kv.Value.mass, kv.Value.capacity, plan.FuelFraction,
                                g.HasActiveBurn, plan.Elapsed, plan.MinSeconds))
                        {
                            due = false;
                            break;
                        }
                    }
                    if (Prefs.DevMode && plan.ProbeSteps < 8)
                    {
                        plan.ProbeSteps++;
                        TickLog($"[抛级判据] G{g.GroupId} 级={stage} 块={sums.Count} 池={pool:F0}/{cap:F0}kg "
                            + $"油比={(cap > 0.0 ? pool / cap : -1.0):F3} 燃烧={g.HasActiveBurn} "
                            + $"已跑={plan.Elapsed:F1}s due={due}");
                    }
                }
                else
                {
                    plan.SecondsToStage -= dt;
                    due = plan.SecondsToStage <= 0.0;
                }
                if (!due) continue;

                var ng = TrySeparateStageMulti(g, out string reason);
                if (ng.Count == 0)
                {
                    Log.Message($"[发射] G{g.GroupId} 抛级失败：{reason}");
                    autoStagePlans.RemoveAt(i);
                    continue;
                }

                plan.StagesLeft--;
                plan.Elapsed = 0.0;
                Log.Message($"[发射] 抛级 → {ng.Count} 组（级 {stage}）；母舰 G{g.GroupId} 在烧={g.HasActiveBurn} 剩余待抛={plan.StagesLeft}");
                if (plan.StagesLeft <= 0) autoStagePlans.RemoveAt(i);
            }
        }
    }
}