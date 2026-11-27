using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using PioneerFramework.Launch;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>组装器状态机（**一个 comp 同时管设计与建造**，2026-10-28 用户令简化）。</summary>
    public enum AssemblerState
    {
        Idle = 0,
        Designing = 1,
        Building = 2,
        Complete = 3,
        Launching = 4
    }

    public class CompProperties_RocketAssembler : CompProperties
    {
        /// <summary>建造速率（每秒推进的工时；默认 1 ⇒ 1 工时 = 1 秒）。</summary>
        public float workPerSecond = 1f;

        /// <summary>工人必须在这个距离内（格）才算在施工。</summary>
        public float workerRadius = 12f;

        /// <summary>
        /// 发射倒计时长度，单位 = **游戏秒**（60 game tick / 秒），**不是太空物理秒**
        /// （用户 2026-10-28 明确：不要走 `TimeScaleManager` 的物理/日历倍率）。
        /// 游戏暂停时倒计时也停；改 3 倍速则按 3 倍速走完 600 tick。
        /// </summary>
        public float launchDelayGameSeconds = 10f;

        public CompProperties_RocketAssembler()
        {
            compClass = typeof(CompRocketAssembler);
        }
    }

    /// <summary>
    /// **火箭组装器**（2026-10-28，第一步：设计与建造都归这一个建筑 Comp）。
    ///
    /// 与旧链（`CompAssemblyPad` + 蓝图 + `CompUnfinishedRocket` + 完工通知）的分工差异：
    /// · 旧链把设计放在台、施工放在框架、完工再回写台 —— 三段各自持有一部分状态，接缝靠通知；
    /// · 本类**只有一个所有者**：设计规格、建造进度、完工状态都在这里，且都随本体存档；
    ///   建完的火箭"就是这台机器里的这份规格"，不生成蓝图/框架中间物（第一步刻意如此）。
    ///
    /// 建造语义（第一步）：需要 `workerRadius` 内**有殖民者**；每秒推进 `workPerSecond` 工时；
    /// 工时 = Σ 件数 ×（40 + 干重/100 + 罐容/1000），夹在 60…12000。
    /// 不消耗材料（材料/科技留到接资源层时再定）——这一点写进窗口提示，免得玩家以为白送是 bug。
    /// </summary>
    public class CompRocketAssembler : ThingComp
    {
        private const int MaxStationsPerRow = 64;

        // 第一步刻意无舱内地图（CabinType.None）：舱室需要脐带塔/登船流程，那是另一条线。
        private RocketDesignSpec design = new() { CabinType = RocketCabinType.None };
        private AssemblerState state = AssemblerState.Idle;
        private float workDone;
        private float workRequired;
        private int launchTicksLeft;
        private int currentStage;
        private int currentParallel = 1;

        public RocketDesignSpec Design => design ??= new RocketDesignSpec();

        public AssemblerState State => state;

        public float WorkDone => workDone;

        public float WorkRequired => workRequired;

        public float Progress01 => workRequired > 1e-4f ? Mathf.Clamp01(workDone / workRequired) : 0f;

        /// <summary>可以改设计（空闲/设计中）。建造中、已完工都不许改。</summary>
        public bool CanEditDesign => state == AssemblerState.Idle || state == AssemblerState.Designing;

        /// <summary>窗口"加入级"：新加件落到这一级（0 = 上面级，数字越大越早抛）。</summary>
        public int CurrentStage => currentStage;

        public void SetCurrentStage(int stage) => currentStage = ClampStage(stage);

        /// <summary>
        /// 窗口"并联台数"：点 + 时在这一轴向站上并排放几件（1…7）。
        /// 2026-10-29 用户令："让玩家在添加部件时可以选择并联多少台（目前先放 1-7 台）"。
        /// </summary>
        public int CurrentParallel => ParallelLayout.Clamp(currentParallel);

        public void SetCurrentParallel(int count) => currentParallel = ParallelLayout.Clamp(count);

        // ── 级标签 / 并联点火（2026-11-03；设计级，按级号）────────────
        public bool IsStageKeep(int stage) => Design.keepStageIds.Contains(stage);

        public bool IsStageRecoverable(int stage) => Design.recoverableStageIds.Contains(stage);

        public bool IsStageParallel(int stage) => Design.parallelIgnitionStageIds.Contains(stage);

        /// <summary>不抛弃：该级成为最大级时拒绝抛级（与“可回收”互斥）。</summary>
        public bool SetStageKeep(int stage, bool on)
        {
            if (!CanEditDesign) return false;
            ToggleStage(Design.keepStageIds, stage, on);
            if (on) Design.recoverableStageIds.Remove(stage);
            return true;
        }

        /// <summary>可回收：碎片退役时交给 DMS:NF（占位；不抛弃级不可回收）。</summary>
        public bool SetStageRecoverable(int stage, bool on)
        {
            if (!CanEditDesign) return false;
            if (on && IsStageKeep(stage)) return false;
            ToggleStage(Design.recoverableStageIds, stage, on);
            return true;
        }

        /// <summary>并联点火：该级与上一级同时点火（C-full）。</summary>
        public bool SetStageParallel(int stage, bool on)
        {
            if (!CanEditDesign) return false;
            ToggleStage(Design.parallelIgnitionStageIds, stage, on);
            return true;
        }

        private static void ToggleStage(List<int> list, int stage, bool on)
        {
            if (on)
            {
                if (!list.Contains(stage)) list.Add(stage);
            }
            else list.Remove(stage);
        }

        /// <summary>设置某行的分离块号（0 = 默认块；径向复制用）。2026-11-03。</summary>
        public bool SetPartBlock(int index, int blockId)
        {
            if (!CanEditDesign) return false;
            if (index < 0 || index >= Design.parts.Count) return false;
            var e = Design.parts[index];
            if (e == null) return false;
            e.blockId = Mathf.Clamp(blockId, 0, 63);
            return true;
        }

        /// <summary>
        /// **径向安装**（2026-11-03，KSP 式）：绕**选中的父件行**在 X-Y 平面放 N 份，各自 `blockId 0..N−1`。
        /// 轴向安装仍走 <see cref="AddPart"/>（沿 −Z 堆叠）。父件可为堆叠行或显式位姿行；
        /// 半径 0 ⇒ 父件半宽 + 本体半宽 + 级间缝。
        /// </summary>
        public bool AddPartRadial(ShipPartDef? def, int count, int parentIndex,
            int? stageId = null, float radiusM = 0f, Func<string, ShipPartDef?>? lookup = null)
        {
            if (def == null || !CanEditDesign) return false;
            var parts = Design.parts;
            var additions = BuildRadialParts(parts, def, count, parentIndex, ClampStage(stageId ?? currentStage),
                radiusM, lookup);
            if (additions.Count == 0) return false;
            parts.AddRange(additions);
            if (state == AssemblerState.Idle) state = AssemblerState.Designing;
            return true;
        }

        internal List<DesignPartSpec> PreviewRadialParts(ShipPartDef def, int count, int parentIndex)
            => BuildRadialParts(design.parts, def, count, parentIndex, ClampStage(currentStage), 0f, null);

        private static List<DesignPartSpec> BuildRadialParts(List<DesignPartSpec> parts, ShipPartDef def, int count,
            int parentIndex, int stage, float radiusM, Func<string, ShipPartDef?>? lookup)
        {
            if (parentIndex < 0 || parentIndex >= parts.Count || parts[parentIndex] is not { } parent)
                return new List<DesignPartSpec>();
            count = Mathf.Clamp(count, 1, 8);
            lookup ??= name => DefDatabase<ShipPartDef>.GetNamedSilentFail(name);

            var parentDef = lookup(parent.defName);
            float parentW = parentDef != null && parentDef.boundsX > 0f ? parentDef.boundsX : 2f;
            // 同一级的径向件共用一圈：取该级最宽的轴向件当“芯级半径”（罐/引擎父件宽度不同也不会错圈）。
            for (int i = 0; i < parts.Count; i++)
            {
                var e = parts[i];
                if (e == null || e.useExplicitPose || e.stageId != parent.stageId) continue;
                var d = lookup(e.defName);
                float w = d != null && d.boundsX > 0f ? d.boundsX : 2f;
                if (w > parentW) parentW = w;
            }
            float partW = def.boundsX > 0f ? def.boundsX : 2f;
            float radius = radiusM > 0f
                ? radiusM
                : parentW * 0.5f + partW * 0.5f + RocketDesignRules.StageGapMeters;

            float px = 0f, py = 0f, pz;
            if (parent.useExplicitPose)
            {
                px = (float)parent.localPosition.x;
                py = (float)parent.localPosition.y;
                pz = (float)parent.localPosition.z;
            }
            else
            {
                float ph = parentDef != null ? RocketSchematicBuilder.StackHeightOf(parentDef) : 2f;
                pz = RowTopZ(parts, parentIndex, lookup) - ph * 0.5f;
            }

            var additions = new List<DesignPartSpec>(count);
            for (int i = 0; i < count; i++)
            {
                float a = Mathf.PI * 2f * i / count;
                additions.Add(new DesignPartSpec
                {
                    defName = def.defName,
                    category = def.category,
                    quantity = 1,
                    parallelCount = 1,
                    installPosition = def.category,
                    stageId = stage,
                    blockId = i,
                    useExplicitPose = true,
                    parentIndex = parentIndex,
                    localPosition = new DVector3(px + radius * Mathf.Cos(a), py + radius * Mathf.Sin(a), pz),
                });
            }
            return additions;
        }

        /// <summary>
        /// **轴向挂接**（2026-11-03）：把新件沿父件的轴向（同一 x/y）挂到它下面/上面。
        /// 径向块上的引擎就靠这个：同 `blockId`、同 stage、显式位姿 ⇒ 抛级时和罐一起走。
        /// </summary>
        public bool AddPartAxialTo(ShipPartDef? def, int parentIndex, bool below = true,
            Func<string, ShipPartDef?>? lookup = null)
        {
            if (def == null || !CanEditDesign) return false;
            var parts = Design.parts;
            var addition = BuildAxialPart(parts, def, parentIndex, below, lookup);
            if (addition == null) return false;
            parts.Add(addition);
            if (state == AssemblerState.Idle) state = AssemblerState.Designing;
            return true;
        }

        internal DesignPartSpec? PreviewAxialPart(ShipPartDef def, int parentIndex, bool below)
            => BuildAxialPart(design.parts, def, parentIndex, below, null);

        private static DesignPartSpec? BuildAxialPart(List<DesignPartSpec> parts, ShipPartDef def, int parentIndex,
            bool below, Func<string, ShipPartDef?>? lookup)
        {
            if (parentIndex < 0 || parentIndex >= parts.Count || parts[parentIndex] is not { } parent)
                return null;
            lookup ??= name => DefDatabase<ShipPartDef>.GetNamedSilentFail(name);

            var parentDef = lookup(parent.defName);
            float ph = parentDef != null ? RocketSchematicBuilder.StackHeightOf(parentDef) : 2f;
            float ch = RocketSchematicBuilder.StackHeightOf(def);
            float px = parent.useExplicitPose ? (float)parent.localPosition.x : 0f;
            float py = parent.useExplicitPose ? (float)parent.localPosition.y : 0f;
            float pz = parent.useExplicitPose
                ? (float)parent.localPosition.z
                : RowTopZ(parts, parentIndex, lookup) - ph * 0.5f;
            float z = below ? pz - (ph + ch) * 0.5f : pz + (ph + ch) * 0.5f;

            return new DesignPartSpec
            {
                defName = def.defName,
                category = def.category,
                quantity = 1,
                parallelCount = 1,
                installPosition = def.category,
                stageId = parent.stageId,
                blockId = parent.blockId,
                useExplicitPose = true,
                parentIndex = parentIndex,
                localPosition = new DVector3(px, py, z),
            };
        }

        /// <summary>
        /// **径向复制**（2026-11-03，科罗廖夫十字）：把选中行按 N 份绕船体 Z 轴均布，
        /// 写成显式位姿 + 各自独立 `blockId`（每份一个可抛块）。原行被替换。
        /// 半径 0 ⇒ 自动：芯级半宽 + 本体半宽 + 级间缝。
        /// </summary>
        public bool RadialCopy(int index, int count = 4, float radiusM = 0f,
            Func<string, ShipPartDef?>? lookup = null)
        {
            if (!CanEditDesign) return false;
            var parts = Design.parts;
            if (index < 0 || index >= parts.Count) return false;
            var src = parts[index];
            if (src == null || count < 2 || count > 8) return false;
            lookup ??= name => DefDatabase<ShipPartDef>.GetNamedSilentFail(name);

            var srcDef = lookup(src.defName);
            float partW = srcDef != null && srcDef.boundsX > 0f ? srcDef.boundsX : 2f;
            float radius = radiusM;
            if (radius <= 0f)
            {
                float coreHalf = 0f;
                for (int i = 0; i < parts.Count; i++)
                {
                    var e = parts[i];
                    if (e == null || e.useExplicitPose || e.stageId >= src.stageId) continue;
                    var def = lookup(e.defName);
                    float w = def != null && def.boundsX > 0f ? def.boundsX : 2f;
                    if (w * 0.5f > coreHalf) coreHalf = w * 0.5f;
                }
                radius = coreHalf + partW * 0.5f + RocketDesignRules.StageGapMeters;
            }
            if (radius <= 0f) radius = partW + RocketDesignRules.StageGapMeters;

            float topZ = RowTopZ(parts, index, lookup);
            var defForH = srcDef;
            float height = defForH != null ? RocketSchematicBuilder.StackHeightOf(defForH) : 2f;
            int stations = Math.Max(1, src.quantity);
            // 块号固定 0..N−1：同一级的助推器模板（罐行/引擎行）分别径向复制时，块号能对齐。
            const int firstBlock = 0;

            var copies = new List<DesignPartSpec>(count * stations);
            for (int b = 0; b < count; b++)
            {
                float angle = Mathf.PI * 2f * b / count;
                for (int q = 0; q < stations; q++)
                {
                    float zc = topZ - height * 0.5f - q * height;
                    var copy = CloneSpec(src);
                    copy.quantity = 1;
                    copy.useExplicitPose = true;
                    copy.blockId = firstBlock + b;
                    copy.localPosition = new DVector3(radius * Mathf.Cos(angle), radius * Mathf.Sin(angle), zc);
                    copy.localRotation = src.localRotation;
                    copies.Add(copy);
                }
            }

            parts.RemoveAt(index);
            parts.InsertRange(index, copies);
            return true;
        }

        internal bool CopyRadialFromDesignWindow(int index)
        {
            var parts = Design.parts;
            int oldCount = parts.Count;
            if (!RadialCopy(index)) return false;
            int shift = parts.Count - oldCount;
            // Existing children still point to the first resulting row at index.
            // Rows whose parents were originally farther down the list must move with them.
            foreach (var part in parts)
            {
                if (part != null && part.parentIndex > index && part.parentIndex < oldCount)
                    part.parentIndex += shift;
            }
            return true;
        }

        /// <summary>某行在堆叠里的**顶面 z**（与 DesignPartAssembler/Schematic 同口径；显式行不推进游标）。</summary>
        private static float RowTopZ(List<DesignPartSpec> parts, int index, Func<string, ShipPartDef?> lookup)
        {
            float z = 0f;
            int prevStage = int.MinValue;
            for (int i = 0; i < index && i < parts.Count; i++)
            {
                var e = parts[i];
                if (e == null) continue;
                if (!e.useExplicitPose && RocketDesignRules.StageGapMeters > 0f
                    && prevStage != int.MinValue && e.stageId != prevStage)
                    z -= RocketDesignRules.StageGapMeters;
                prevStage = e.stageId;
                if (e.useExplicitPose) continue;
                var def = lookup(e.defName);
                float h = def != null ? RocketSchematicBuilder.StackHeightOf(def) : 2f;
                z -= h * Math.Max(1, e.quantity);
            }
            var src = index < parts.Count ? parts[index] : null;
            if (src != null && !src.useExplicitPose && RocketDesignRules.StageGapMeters > 0f
                && prevStage != int.MinValue && src.stageId != prevStage)
                z -= RocketDesignRules.StageGapMeters;
            return z;
        }

        private static DesignPartSpec CloneSpec(DesignPartSpec src)
        {
            return new DesignPartSpec
            {
                defName = src.defName,
                category = src.category,
                quantity = src.quantity,
                parallelCount = src.parallelCount,
                installPosition = src.installPosition,
                stageId = src.stageId,
                blockId = src.blockId,
                useExplicitPose = src.useExplicitPose,
                localPosition = src.localPosition,
                localRotation = src.localRotation,
                parentIndex = src.parentIndex,
                useShapeOverride = src.useShapeOverride,
                shapeOverride = src.shapeOverride,
            };
        }

        public CompProperties_RocketAssembler Props =>
            (CompProperties_RocketAssembler)(props ?? new CompProperties_RocketAssembler());

        // ── 设计编辑 ──────────────────────────────────────────────

        /// <summary>
        /// 从目录加件；同 defName **且同级号且同并联台数** 合并数量。返回是否真的改了。
        /// </summary>
        /// <param name="stageId">null ⇒ 用 <see cref="CurrentStage"/>（窗口里的"加入级"）。</param>
        /// <param name="parallelCount">null ⇒ 用 <see cref="CurrentParallel"/>（窗口里的"并联"）；
        /// 1 = 单件，2…7 = 绕主推轴成环（<see cref="ParallelLayout"/>）。</param>
        /// <remarks>`qty` = **轴向站数**（不是件数）：加 2 站 × 并联 6 = 12 台。</remarks>
        public bool AddPart(ShipPartDef? def, int qty = 1, int? stageId = null, int? parallelCount = null)
        {
            if (def == null || qty <= 0 || !CanEditDesign) return false;
            qty = Math.Min(qty, MaxStationsPerRow);
            int stage = ClampStage(stageId ?? currentStage);
            int parallel = ParallelLayout.Clamp(parallelCount ?? currentParallel);

            var d = Design;
            for (int i = 0; i < d.parts.Count; i++)
            {
                var e = d.parts[i];
                // 2026-10-28：**同级号才合并**——否则"上面级 1 台 + 一级 9 台"会被并成一行 10 台，
                // 抛级时整行一起走，用户看到的"切分全扎堆"就是这么来的。
                // 2026-10-29：并联台数也是合并键的一部分（4 台的十字与 6 台的六方几何不同，不能并成一行）。
                if (e != null && MatchesStackRow(e, def, stage, parallel))
                {
                    e.quantity = Math.Min(MaxStationsPerRow, e.quantity + qty);
                    if (state == AssemblerState.Idle) state = AssemblerState.Designing;
                    return true;
                }
            }

            d.parts.Add(CreateStackRow(def, qty, stage, parallel));
            if (state == AssemblerState.Idle) state = AssemblerState.Designing;
            return true;
        }

        internal bool TryInsertPartAt(ShipPartDef def, int index, DesignPartSpec? sameNode,
            out int addedRow, out string reason)
        {
            addedRow = -1;
            reason = "";
            if (!CanEditDesign)
            {
                reason = "当前状态无法编辑设计。";
                return false;
            }

            var parts = design.parts;
            if (def == null || index < 0 || index > parts.Count)
            {
                reason = "插入位置或目录件已失效。";
                return false;
            }

            int stage = ClampStage(currentStage);
            int parallel = CurrentParallel;
            if (sameNode != null && index < parts.Count && ReferenceEquals(parts[index], sameNode)
                && !sameNode.useExplicitPose && MatchesStackRow(sameNode, def, stage, parallel))
            {
                if (sameNode.quantity >= MaxStationsPerRow)
                {
                    reason = $"这一行已达到 {MaxStationsPerRow} 个轴向站。";
                    return false;
                }
                sameNode.quantity++;
            }
            else
            {
                parts.Insert(index, CreateStackRow(def, 1, stage, parallel));
                for (int i = 0; i < parts.Count; i++)
                {
                    var row = parts[i];
                    if (row != null && row.parentIndex >= index && row.parentIndex < int.MaxValue)
                        row.parentIndex++;
                }
            }

            addedRow = index;
            if (state == AssemblerState.Idle) state = AssemblerState.Designing;
            return true;
        }

        private static bool MatchesStackRow(DesignPartSpec row, ShipPartDef def, int stage, int parallel)
            => row.defName == def.defName && row.category == def.category && row.stageId == stage
                && RocketDesignRules.ParallelOf(row) == parallel;

        private static DesignPartSpec CreateStackRow(ShipPartDef def, int quantity, int stage, int parallel)
            => new DesignPartSpec
            {
                defName = def.defName,
                category = def.category,
                quantity = quantity,
                parallelCount = parallel,
                installPosition = def.category,
                stageId = stage,
            };

        /// <summary>
        /// **在堆叠顺序里前后移动一行**（2026-10-29 用户令："让选中部件的顺序可以前后移动"）。
        /// delta &lt; 0 = 上移（靠机头），&gt; 0 = 下移（靠喷口）；越界夹到两端，夹完没变化就返回 false。
        /// 顺序 = 堆叠顺序 ⇒ 移动会真的改几何（也用来把引擎摆到本级末端）。
        /// </summary>
        public bool MovePart(int index, int delta)
        {
            if (!CanEditDesign || delta == 0) return false;
            var d = Design;
            int n = d.parts.Count;
            if (index < 0 || index >= n) return false;
            int target = Mathf.Clamp(index + delta, 0, n - 1);
            if (target == index) return false;

            var e = d.parts[index];
            d.parts.RemoveAt(index);
            d.parts.Insert(target, e);
            RemapParentIndices(d.parts, index, target);
            return true;
        }

        /// <summary>
        /// 挪动后修正 <see cref="DesignPartSpec.parentIndex"/> 的取值（它记的是 spec 下标）。
        /// 组装器自己不写 parentIndex（恒 −1），但 DMS:NF 的显式位姿设计会用到 ⇒ 不留静默错位。
        /// </summary>
        private static void RemapParentIndices(List<DesignPartSpec> parts, int from, int to)
        {
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null || p.parentIndex < 0) continue;
                int old = p.parentIndex;
                int mapped;
                if (old == from) mapped = to;
                else if (from < to && old > from && old <= to) mapped = old - 1;
                else if (from > to && old >= to && old < from) mapped = old + 1;
                else mapped = old;
                p.parentIndex = mapped;
            }
        }

        /// <summary>
        /// **引擎必须在它那一级的末端**（2026-10-29 用户令；口径在 <see cref="RocketDesignRules"/>）。
        /// 空串 = 通过；否则 = 可直接展示的拒绝原因。
        /// </summary>
        public string EnginePlacementError(List<int>? offenders = null)
            => RocketDesignRules.EnginePlacementError(Design, offenders);

        /// <summary>
        /// **同一级的行必须在堆叠里连成一段**（2026-10-30；口径在 <see cref="RocketDesignRules"/>）。
        /// 空串 = 通过；否则 = 可直接展示的拒绝原因。
        /// </summary>
        public string StageContiguityError(List<int>? offenders = null)
            => RocketDesignRules.StageContiguityError(Design, offenders);

        /// <summary>把某一行挪到指定级（0 = 上面级，数字越大越早抛）。</summary>
        public bool SetPartStage(int index, int stage)
        {
            if (!CanEditDesign) return false;
            var d = Design;
            if (index < 0 || index >= d.parts.Count) return false;
            var e = d.parts[index];
            if (e == null) return false;
            e.stageId = ClampStage(stage);
            return true;
        }

        /// <summary>把某一行整级上下挪一格（delta&gt;0 = 更靠下的级 = 更早抛）。</summary>
        public bool ShiftPartStage(int index, int delta = 1)
        {
            if (!CanEditDesign || delta == 0) return false;
            var d = Design;
            if (index < 0 || index >= d.parts.Count) return false;
            var e = d.parts[index];
            if (e == null) return false;
            e.stageId = ClampStage(e.stageId + delta);
            return true;
        }

        public const int MaxStageId = 4;

        public static int ClampStage(int stage) => Mathf.Clamp(stage, 0, MaxStageId);

        /// <summary>设计里出现的级号个数（0 级单级火箭 = 1）。</summary>
        public int StageCount
        {
            get
            {
                var seen = new HashSet<int>();
                var parts = Design.parts;
                for (int i = 0; i < parts.Count; i++)
                    if (parts[i] != null) seen.Add(parts[i].stageId);
                return Math.Max(1, seen.Count);
            }
        }

        /// <summary>活动级 = 最大的 stageId（发射时先烧这一级；抛级后往下掉）。</summary>
        public int ActiveStage => RocketSchematicBuilder.ActiveStage(Design);

        /// <summary>活动级集合（C-full，2026-11-03）：最大级 + 标了并联点火的下级（可传递）。</summary>
        public HashSet<int> ActiveStages => RocketDesignRules.ActiveStages(Design);

        /// <summary>某一级里的引擎**件数**（= 站数 × 并联台数）。</summary>
        public int EngineCountInStage(int stage)
        {
            int n = 0;
            var parts = Design.parts;
            for (int i = 0; i < parts.Count; i++)
            {
                var e = parts[i];
                if (e != null && e.stageId == stage && e.category == "thruster") n += RocketDesignRules.PartCountOf(e);
            }
            return n;
        }

        /// <summary>某一级里的工质箱**件数**（= 站数 × 并联台数）。</summary>
        public int TankCountInStage(int stage)
        {
            int n = 0;
            var parts = Design.parts;
            for (int i = 0; i < parts.Count; i++)
            {
                var e = parts[i];
                if (e != null && e.stageId == stage && (e.category == "tank" || e.category == "rcs-tank"))
                    n += RocketDesignRules.PartCountOf(e);
            }
            return n;
        }

        /// <summary>活动级有没有引擎（没有 ⇒ 推力 0，点不着；2026-10-28 用户实测踩到）。</summary>
        public bool ActiveStageHasEngine
        {
            get
            {
                var stages = ActiveStages;
                if (stages.Count == 0) return false;
                foreach (int s in stages)
                    if (EngineCountInStage(s) <= 0) return false;
                return true;
            }
        }

        /// <summary>
        /// 活动级能不能自己点火：**每个活动级**同时有引擎和工质箱（C-full 下并联级各自成池）。
        /// 只有引擎没罐 ⇒ 有推力没油；只有罐没引擎 ⇒ 推力 0。两种都会被发射门拒。
        /// </summary>
        public bool ActiveStageReady
        {
            get
            {
                var stages = ActiveStages;
                if (stages.Count == 0) return false;
                foreach (int s in stages)
                    if (EngineCountInStage(s) <= 0 || TankCountInStage(s) <= 0) return false;
                return true;
            }
        }

        /// <summary>某一类零件的**件数**（= 站数 × 并联台数）。</summary>
        public int CountInDesign(string category)
        {
            int n = 0;
            var parts = Design.parts;
            for (int i = 0; i < parts.Count; i++)
                if (parts[i] != null && parts[i].category == category) n += RocketDesignRules.PartCountOf(parts[i]);
            return n;
        }

        /// <summary>整份设计的**实际件数**（Σ 站数 × 并联台数），窗口状态条用。</summary>
        public int TotalPartCount => RocketDesignRules.TotalPartCount(Design);

        /// <summary>
        /// 控制核心件数。**纯零件火箭必须带一个**（2026-10-28 用户令：KSP 命令舱语义）：
        /// 没有它就没有"谁在驾驶"，点火门不该放行。
        /// </summary>
        public int ControlCount => CountInDesign("control");

        /// <summary>逐级读数（"0:1机/1罐 · 1:9机/1罐"），用于拒绝原因与窗口提示。</summary>
        public string StageDiagnosis()
        {
            var seen = new List<int>();
            var parts = Design.parts;
            for (int i = 0; i < parts.Count; i++)
                if (parts[i] != null && !seen.Contains(parts[i].stageId)) seen.Add(parts[i].stageId);
            seen.Sort();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < seen.Count; i++)
            {
                int st = seen[i];
                if (i > 0) sb.Append(" · ");
                sb.Append(st).Append(':').Append(EngineCountInStage(st)).Append("机/")
                  .Append(TankCountInStage(st)).Append('罐');
            }
            return sb.ToString();
        }

        /// <summary>
        /// **级号整体翻转**（0 ↔ 最大级）：把"建反了"的两级一键改回来。
        /// 触发场景（2026-10-28 用户实测）：上面级被编成 1、一级被编成 0 ⇒ 活动级=1 没有引擎 ⇒ 推力 0。
        /// </summary>
        public bool ReverseStages()
        {
            if (!CanEditDesign) return false;
            int max = 0;
            var parts = Design.parts;
            for (int i = 0; i < parts.Count; i++)
                if (parts[i] != null && parts[i].stageId > max) max = parts[i].stageId;
            if (max <= 0) return false;
            for (int i = 0; i < parts.Count; i++)
                if (parts[i] != null) parts[i].stageId = max - parts[i].stageId;
            return true;
        }

        /// <summary>按数量减件；减到 0 就删掉这一行。返回是否真的改了。</summary>
        public bool RemovePartAt(int index, int qty = 1)
        {
            if (!CanEditDesign || qty <= 0) return false;
            var d = Design;
            if (index < 0 || index >= d.parts.Count) return false;

            var e = d.parts[index];
            if (e == null) { d.parts.RemoveAt(index); return true; }

            e.quantity -= qty;
            if (e.quantity <= 0) d.parts.RemoveAt(index);
            if (d.parts.Count == 0 && state == AssemblerState.Designing) state = AssemblerState.Idle;
            return true;
        }

        internal List<DesignPartSpec> GetDeletionGroup(DesignPartSpec part)
        {
            var parts = design.parts;
            var indices = FindDeletionGroupIndices(parts, part);
            var group = new List<DesignPartSpec>(indices.Count);
            for (int i = 0; i < parts.Count; i++)
                if (indices.Contains(i)) group.Add(parts[i]);
            return group;
        }

        internal bool TryDeleteGroup(DesignPartSpec part, IReadOnlyList<DesignPartSpec> expected)
        {
            if (!CanEditDesign) return false;
            var parts = design.parts;
            var indices = FindDeletionGroupIndices(parts, part);
            if (indices.Count == 0 || indices.Count != expected.Count) return false;

            var expectedRows = new HashSet<DesignPartSpec>(expected, ReferenceEqualityComparer.Instance);
            foreach (int index in indices)
                if (!expectedRows.Remove(parts[index])) return false;

            var newIndices = new int[parts.Count];
            int survivingCount = 0;
            for (int i = 0; i < parts.Count; i++)
                newIndices[i] = indices.Contains(i) ? -1 : survivingCount++;

            for (int i = 0; i < parts.Count; i++)
            {
                if (newIndices[i] < 0) continue;
                var row = parts[i];
                if (row != null && row.parentIndex >= 0 && row.parentIndex < newIndices.Length)
                    row.parentIndex = newIndices[row.parentIndex];
                parts[newIndices[i]] = row!;
            }
            parts.RemoveRange(survivingCount, parts.Count - survivingCount);
            if (parts.Count == 0 && state == AssemblerState.Designing) state = AssemblerState.Idle;
            return true;
        }

        private static HashSet<int> FindDeletionGroupIndices(List<DesignPartSpec> parts, DesignPartSpec part)
        {
            var group = new HashSet<int>();
            if (part == null) return group;
            int root = parts.FindIndex(row => ReferenceEquals(row, part));
            if (root < 0) return group;

            // 先按父索引建立子行表，允许子行经移动后出现在父行之前。
            var childrenByParent = new List<int>?[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                var row = parts[i];
                if (row == null || row.parentIndex < 0 || row.parentIndex >= parts.Count) continue;
                (childrenByParent[row.parentIndex] ??= new List<int>()).Add(i);
            }

            var pending = new Queue<int>();
            group.Add(root);
            pending.Enqueue(root);
            while (pending.TryDequeue(out int current))
            {
                var children = childrenByParent[current];
                if (children == null) continue;
                foreach (int child in children)
                    if (group.Add(child)) pending.Enqueue(child);
            }
            return group;
        }

        public void ClearDesign()
        {
            if (!CanEditDesign) return;
            ResetToIdle();
        }

        /// <summary>
        /// **完工后开下一枚**（2026-10-28）：已完工状态既不能改设计也不能清空，会变成死路
        /// ⇒ 给一个显式出口，把设计、工时、状态一起复位。建造中不许调（必须先取消）。
        /// </summary>
        public bool StartNewRocket()
        {
            if (state == AssemblerState.Building || state == AssemblerState.Launching) return false;
            ResetToIdle();
            return true;
        }

        /// <summary>
        /// **完工后回去改设计**（2026-11-24，C+ UI 转生产）：`Complete` 原本没有出口 ——
        /// `CanEditDesign` 不认它、`StartBuild` 拒绝它，唯一能走的 `StartNewRocket` 会把设计一起清空，
        /// 于是 UI 只能"存快照 → StartNewRocket → 灌回去"，漏一个字段就静默丢设计。
        /// 这里只把状态拨回 `Designing`，**保留设计本身**（零件 / 级标签 / 载荷 / 机舱 / 尺寸），
        /// 清掉工时、倒计时与完工时加的油；之后必须重新 `StartBuild`（`Designing` 不是可发射态）。
        /// 不碰 `parent` / `Map` / 时间基 / 物理步 / 存档字段。
        /// </summary>
        public bool ReopenDesign()
        {
            if (state != AssemblerState.Complete) return false;
            state = AssemblerState.Designing;
            workDone = 0f;
            workRequired = 0f;          // StartBuild 会重算工时，置 0 只表示"还没开工"
            launchTicksLeft = 0;
            Design.fuelAmount = 0f;     // FinishBuild 完工时加过油，回设计态必须清掉
            return true;
        }

        private void ResetToIdle()
        {
            design = new RocketDesignSpec { CabinType = RocketCabinType.None };
            workDone = 0f;
            workRequired = 0f;
            launchTicksLeft = 0;
            currentStage = 0;
            state = AssemblerState.Idle;
        }

        // ── 建造 ──────────────────────────────────────────────────

        /// <summary>纯函数口径的工时（测试直接调这个，不必造 Thing）。</summary>
        public static float WorkFor(RocketDesignSpec? spec,
            Func<string, ShipPartDef?>? catalogLookup = null)
        {
            if (spec?.parts == null || spec.parts.Count == 0) return 0f;
            catalogLookup ??= name => string.IsNullOrEmpty(name)
                ? null
                : DefDatabase<ShipPartDef>.GetNamedSilentFail(name);

            double work = 0.0;
            for (int i = 0; i < spec.parts.Count; i++)
            {
                var e = spec.parts[i];
                if (e == null) continue;
                // 工时按**实际件数**算（站数 × 并联台数）：并联 6 台要焊 6 台，不是 1 台。
                int qty = RocketDesignRules.PartCountOf(e);
                var def = catalogLookup(e.defName);
                double perPart = 40.0;
                if (def != null)
                {
                    perPart += Math.Max(0f, def.massKg) / 100.0;
                    perPart += Math.Max(0f, def.tankCapacityKg) / 1000.0;
                }
                work += perPart * qty;
            }
            return (float)Math.Min(12000.0, Math.Max(60.0, work));
        }

        public bool StartBuild(out string reason)
        {
            reason = "";
            if (state == AssemblerState.Building) { reason = "已经在建造中。"; return false; }
            if (state == AssemblerState.Complete) { reason = "这枚火箭已经组装完成。"; return false; }
            if (!Design.IsValid(out string fail)) { reason = fail; return false; }

            // 2026-10-28 用户令：纯零件火箭必须带控制核心（KSP 命令舱语义）。
            if (ControlCount <= 0)
            {
                reason = "缺少控制核心：纯零件火箭必须带 1 个 control 件才能点火。";
                return false;
            }

            // 2026-10-29 用户令：**引擎只能在它那一级的末端**，否则拒绝设计通过。
            string engineError = EnginePlacementError();
            if (engineError.Length > 0) { reason = engineError; return false; }

            // 2026-10-30：**同一级的行必须连成一段**（否则抛级切出来的两半几何互相穿插）。
            string stageGap = StageContiguityError();
            if (stageGap.Length > 0) { reason = stageGap; return false; }

            // 2026-10-28 用户实测：活动级没引擎 ⇒ 起飞推力 0（"推力是 0 kN"）。在建之前就拦住并说清。
            int active = ActiveStage;
            if (!ActiveStageReady)
            {
                reason = $"活动级 {active} 不能自己点火（引擎 {EngineCountInStage(active)} / 工质箱 {TankCountInStage(active)}）。"
                    + $"每级要同时有引擎和工质箱；级号越大越先烧（0 = 上面级）。当前：{StageDiagnosis()}";
                return false;
            }

            float need = WorkFor(Design);
            if (need <= 0f) { reason = "没有可建造的内容。"; return false; }

            workRequired = need;
            workDone = 0f;
            state = AssemblerState.Building;
            return true;
        }

        public void CancelBuild()
        {
            if (state != AssemblerState.Building) return;
            state = AssemblerState.Designing;
            workDone = 0f;
            workRequired = 0f;
        }

        // ── 发射（2026-10-28 第二步）────────────────────────────────

        /// <summary>倒计时换算：**游戏秒 → game tick（60/s）**，与物理/日历倍率无关。</summary>
        public static int TicksForGameSeconds(float seconds)
            => Mathf.Max(0, Mathf.RoundToInt(seconds * 60f));

        /// <summary>发射燃料 = 全部工质箱满罐容量（纯函数；目录查不到的件按 0）。</summary>
        public static float FuelForLaunch(RocketDesignSpec? spec,
            Func<string, ShipPartDef?>? catalogLookup = null)
            => PartPropulsionResolver.Resolve(spec, catalogLookup, _ => null).capacity;

        public int LaunchTicksLeft => launchTicksLeft;

        public float LaunchSecondsLeft => launchTicksLeft / 60f;

        public float LaunchSecondsTotal => Mathf.Max(0f, Props.launchDelayGameSeconds);

        /// <summary>开始发射倒计时（只有已完工能开）。</summary>
        public bool BeginLaunch(out string reason)
        {
            reason = "";
            if (state == AssemblerState.Launching) { reason = "已经在倒计时。"; return false; }
            if (state != AssemblerState.Complete) { reason = "火箭还没组装完成。"; return false; }
            if (parent?.Map == null) { reason = "建筑不在任何地图上。"; return false; }
            if (ControlCount <= 0)
            {
                reason = "缺少控制核心：纯零件火箭必须带 1 个 control 件才能点火。用「下一枚」改设计。";
                return false;
            }
            // 2026-10-29：老档/老设计可能带着"引擎在级中间"的规格完工 ⇒ 发射前再拦一次。
            string engineError = EnginePlacementError();
            if (engineError.Length > 0)
            {
                reason = engineError + "用「下一枚」改设计重建。";
                return false;
            }
            // 2026-10-30：同一级的行必须连成一段（老档同样兜底）。
            string stageGap = StageContiguityError();
            if (stageGap.Length > 0)
            {
                reason = stageGap + "用「下一枚」改设计重建。";
                return false;
            }
            if (!ActiveStageReady)
            {
                reason = $"活动级 {ActiveStage} 不能自己点火（引擎 {EngineCountInStage(ActiveStage)} / "
                    + $"工质箱 {TankCountInStage(ActiveStage)}）：发射必被拒。"
                    + $"当前：{StageDiagnosis()}。用「下一枚」改设计重建。";
                return false;
            }

            launchTicksLeft = TicksForGameSeconds(Props.launchDelayGameSeconds);
            state = AssemblerState.Launching;
            Messages.Message($"{parent.LabelShort}：发射倒计时 {Props.launchDelayGameSeconds:0.#} 游戏秒。",
                MessageTypeDefOf.PositiveEvent, false);
            return true;
        }

        public void CancelLaunch()
        {
            if (state != AssemblerState.Launching) return;
            launchTicksLeft = 0;
            state = AssemblerState.Complete;
            Messages.Message($"{parent?.LabelShort ?? "组装器"}：发射已取消。",
                MessageTypeDefOf.NeutralEvent, false);
        }

        private void DoLaunch()
        {
            Map? map = parent?.Map;
            launchTicksLeft = 0;
            if (map == null) { state = AssemblerState.Complete; return; }

            var spec = Design;
            if (spec.fuelAmount <= 0.001f)                       // 老档/未完工标记：按满罐兜底
                spec.fuelAmount = FuelForLaunch(spec);
            IntVec3 center = parent!.OccupiedRect().CenterCell;

            SpaceEntity? entity = LaunchService.LaunchFromGround(map, center, spec, debug: true);
            if (entity == null)
            {
                state = AssemblerState.Complete;                // 推力/站点/燃料被拒 ⇒ 留在已完工，可改了再发
                Messages.Message($"{parent?.LabelShort ?? "组装器"}：发射被拒绝，原因见上方提示。",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            // 2026-10-28 一次性诊断：把入轨瞬间的推进读数同时打到日志与顶栏，
            // 用来分辨"没网络 / 没油 / 质量超 / 制导没点火"四种摔法（用户实报后加的）。
            ReportLaunchDiagnostics(entity);

            // **分级**（2026-10-28 用户实报"这是一发要分级的火箭，没有分级"）：
            // 原来发射后**没有任何抛级计划** —— `TrySeparateStage` 只由发射台的手动按钮/自动抛级开关触发，
            // 组装器发射的船从来没挂过计划 ⇒ 一级烧干后只是熄火，二级永远不点火。
            // 现在：设计里有 >1 级 ⇒ 发射后自动给这个群挂"油尽抛级"计划（2% 油比 / 最少 2 s），
            // 一级烧干 ⇒ 抛级 ⇒ 上层按新活动级重建推进并继续（控制器仍追母舰组，不用重新注册）。
            if (StageCount > 1)
            {
                var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
                var group = spaceGame?.GetGroupById(entity.GroupId);
                if (spaceGame != null && group != null)
                {
                    group.autoStageFuelFraction = 0.02;
                    group.autoStageMinSeconds = 2.0;
                    spaceGame.SetAutoStage(group, true);
                    Messages.Message($"{parent?.LabelShort ?? "组装器"}：已挂自动抛级（设计 {StageCount} 级）。",
                        MessageTypeDefOf.NeutralEvent, false);
                }
            }

            state = AssemblerState.Idle;                        // 火箭飞走，台子空出来（设计保留）
            Messages.Message($"{parent?.LabelShort ?? "组装器"}：已发射。", MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>
        /// **一键装填并完工一发 R-7 形态测试火箭**（2026-11-03 替换旧两级示范）。
        ///
        /// 形态（目录件，全在 `ShipParts_Realistic.xml`）：
        /// · 级 0（芯级）：控制核心 ×1 + 400 t 煤油罐 ×1 + 大推力机 ×1（引擎在本级末端）；
        ///   标**并联点火** ⇒ 与级 1 助推器同时烧（C-full）。
        /// · 级 1（助推器）：4 组 100 t 煤油罐 + 大推力机，沿芯级下段径向对称，喷口齐平。
        ///   各占 `blockId 0–3` ⇒ 一次抛级出 4 个碎片组（科罗廖夫十字）。
        ///
        /// **发动机选型（2026-11-26 修）**：保留五台 `Lifter_Heavy`（3,795 kN）。
        /// 现有目录下总推力 18,975 kN、满罐质量 867.8 t，g=8 时推重比约 2.73。
        /// 注意**不能**用 `parallelCount` 堆引擎：显式位姿行的并联件共用同一 pose
        /// （`DesignPartAssembler` "不再叠环偏移"）⇒ 只涨读数、不涨几何。
        ///
        /// 显式位姿行 → 两道堆叠设计门自动豁免；一步到位：装填 → 开工 → 立即完工（含满罐加注）。
        /// </summary>
        public bool QuickAssembleDemoRocket(out string reason)
        {
            reason = "";
            if (state == AssemblerState.Building || state == AssemblerState.Launching)
            {
                reason = "建造/发射中不能重来。";
                return false;
            }

            var spec = BuildR7TestDesign(name => DefDatabase<ShipPartDef>.GetNamedSilentFail(name), out reason);
            if (spec == null) return false;

            ResetToIdle();   // 一键示范：总是从空设计开始（旧实现会追加到已有设计上）
            design = spec;
            state = AssemblerState.Designing;

            if (!StartBuild(out reason)) return false;

            // "立即组装"：跳过工时直接完工（与 DevMode 的「完工」同一条路，但不要求 DevMode）。
            workDone = workRequired;
            FinishBuild();
            return true;
        }

        internal bool TryLoadEditableR7(out string reason)
        {
            if (!CanEditDesign)
            {
                reason = "当前状态无法编辑设计。";
                return false;
            }

            var spec = BuildR7TestDesign(name => DefDatabase<ShipPartDef>.GetNamedSilentFail(name), out reason);
            if (spec == null) return false;

            ResetToIdle();
            design = spec;
            state = AssemblerState.Designing;
            return true;
        }

        /// <summary>
        /// **纯构造 R-7 测试设计**：芯级（控制核心 + 重罐 + 大推力机，级 0，标并联点火）
        /// + 4 台助推器（中罐 + 大推力机，级 1，块 0–3，显式位姿径向对称）。
        /// 不碰 state/建造流程 ⇒ 可在 headless 测试里用注入目录直接验形状与两道设计门。
        /// </summary>
        internal static RocketDesignSpec? BuildR7TestDesign(Func<string, ShipPartDef?> lookup, out string reason)
        {
            reason = "";
            ShipPartDef? control = lookup("PF_ShipPart_ControlCore_Debug");
            ShipPartDef? engine = lookup("PF_ShipPart_Engine_Lifter_Heavy");
            ShipPartDef? tankBig = lookup("PF_ShipPart_Tank_Kerolox_Heavy");
            ShipPartDef? tankMid = lookup("PF_ShipPart_Tank_Kerolox_Medium");
            if (control == null || engine == null || tankBig == null || tankMid == null)
            {
                reason = "测试火箭缺少目录件（ControlCore_Debug / Lifter_Heavy / Kerolox_Heavy / Kerolox_Medium）。";
                return null;
            }

            var spec = new RocketDesignSpec { CabinType = RocketCabinType.None };

            // ── 级 0：芯级（控制核心 → 重罐 → 引擎；引擎在级末）──
            spec.parts.Add(new DesignPartSpec
            {
                defName = control.defName, category = "control", quantity = 1, parallelCount = 1,
                installPosition = "control", stageId = 0,
            });
            spec.parts.Add(new DesignPartSpec
            {
                defName = tankBig.defName, category = "tank", quantity = 1, parallelCount = 1,
                installPosition = "body", stageId = 0,
            });
            spec.parts.Add(new DesignPartSpec
            {
                defName = engine.defName, category = "thruster", quantity = 1, parallelCount = 1,
                installPosition = "engine", stageId = 0,
            });
            spec.parallelIgnitionStageIds.Add(0);   // 芯级与助推器同时点火

            float hControl = RocketSchematicBuilder.StackHeightOf(control);
            float hTankMid = RocketSchematicBuilder.StackHeightOf(tankMid);
            float hEngine = RocketSchematicBuilder.StackHeightOf(engine);
            float hTankBig = RocketSchematicBuilder.StackHeightOf(tankBig);
            float coreTankBottomZ = -(hControl + hTankBig);
            float boosterTankZ = coreTankBottomZ + hTankMid * 0.5f;
            float boosterEngineZ = coreTankBottomZ - hEngine * 0.5f;

            float coreHalf = Mathf.Max(control.boundsX, Mathf.Max(tankBig.boundsX, engine.boundsX)) * 0.5f;
            float radius = coreHalf + Mathf.Max(tankMid.boundsX, engine.boundsX) * 0.5f
                           + RocketDesignRules.StageGapMeters;

            // ── 级 1：4 台助推器（中罐 + 大推力机；每台一个 blockId）──
            const int coreTankRow = 1;
            for (int b = 0; b < 4; b++)
            {
                float a = Mathf.PI * 0.5f * b;   // 0°/90°/180°/270°
                float x = radius * Mathf.Cos(a);
                float y = radius * Mathf.Sin(a);

                int boosterTankRow = spec.parts.Count;
                spec.parts.Add(new DesignPartSpec
                {
                    defName = tankMid.defName,
                    category = "tank",
                    quantity = 1,
                    parallelCount = 1,
                    installPosition = "body",
                    stageId = 1,
                    blockId = b,
                    useExplicitPose = true,
                    parentIndex = coreTankRow,
                    localPosition = new DVector3(x, y, boosterTankZ),
                });
                spec.parts.Add(new DesignPartSpec
                {
                    defName = engine.defName,
                    category = "thruster",
                    quantity = 1,
                    parallelCount = 1,
                    installPosition = "engine",
                    stageId = 1,
                    blockId = b,
                    useExplicitPose = true,
                    parentIndex = boosterTankRow,
                    localPosition = new DVector3(x, y, boosterEngineZ),
                });
            }

            return spec;
        }

        /// <summary>
        /// **一次性发射诊断**（2026-10-28）：入轨瞬间的推进读数，同时进日志与顶栏。
        /// 读的是**发射后的真实源**（不是设计口径），所以能分辨：网络 0 条（推进没注册）/
        /// 工质 0 t（没加注）/ TWR&lt;1（质量或推力不够）。
        /// </summary>
        private static void ReportLaunchDiagnostics(SpaceEntity entity)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var group = spaceGame?.GetGroupById(entity.GroupId);
            if (spaceGame == null || group == null) return;

            var src = spaceGame.GetGroupPropulsionSource(group);
            int nets = src?.Networks.Count ?? 0;
            double thrustN = 0.0;
            if (src != null)
                for (int i = 0; i < nets; i++) thrustN += src.Networks[i].Thrust;

            double dry = 0.0;
            foreach (int eid in group.GetEntityIds())
            {
                var e = spaceGame.GetEntityById(eid);
                if (e != null) dry += e.TotalMass;
            }
            double prop = src?.TotalPropellantMass ?? 0.0;
            double twr = dry + prop > 0.0 ? thrustN / ((dry + prop) * 8.0) : 0.0;

            string line = $"[组装器发射] 网络={nets} 推力={thrustN / 1000.0:F0}kN "
                + $"干重={dry / 1000.0:F2}t 工质={prop / 1000.0:F2}t TWR≈{twr:F2}";
            Log.Message(line);
            Messages.Message(line, MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>调试用：跳过工时直接完工（只在 DevMode 的窗口按钮上调）。</summary>
        public void ForceComplete()        {
            if (state == AssemblerState.Building)
            {
                workDone = workRequired;
                FinishBuild();
            }
        }

        private void FinishBuild()
        {
            state = AssemblerState.Complete;
            workDone = workRequired;
            // 2026-10-28：**完工即加注**（满罐 = Σ 罐容量）。发射直接用这个值；
            // 窗口/检视条显示"加注 X t"，避免"到底加没加油"要靠猜。
            Design.fuelAmount = FuelForLaunch(Design);
            Messages.Message($"{parent?.LabelShort ?? "组装器"}：火箭组装完成"
                + $"（{Design.parts.Count} 行 / {StageCount} 级，加注 {Design.fuelAmount / 1000.0:F1} t）。",
                MessageTypeDefOf.TaskCompletion, false);
        }

        private bool HasWorkerNearby()
        {
            Map? map = parent?.Map;
            if (map == null) return false;
            var pawns = map.mapPawns?.FreeColonistsSpawned;
            if (pawns == null) return false;
            float radius = Props.workerRadius;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == null || p.Dead || !p.Spawned) continue;
                if (p.Position.DistanceTo(parent!.Position) <= radius) return true;
            }
            return false;
        }

        public override void CompTick()
        {
            base.CompTick();
            if (parent == null) return;

            // 发射倒计时：**每 tick 减 1**（600 tick = 10 游戏秒；暂停即停，与物理倍率无关）
            if (state == AssemblerState.Launching)
            {
                if (launchTicksLeft > 0) launchTicksLeft--;
                if (launchTicksLeft <= 0) DoLaunch();
                return;
            }

            if (state != AssemblerState.Building) return;
            if (!parent.IsHashIntervalTick(60)) return;

            if (!HasWorkerNearby()) return;   // 没人在旁边 ⇒ 停工（窗口里会提示）

            workDone += Math.Max(0.1f, Props.workPerSecond);
            if (workDone >= workRequired) FinishBuild();
        }

        // ── 读数 / 存档 ──────────────────────────────────────────

        public string StateText => state switch
        {
            AssemblerState.Idle => "空闲",
            AssemblerState.Designing => "设计中",
            AssemblerState.Building => "建造中",
            AssemblerState.Complete => "已完工",
            AssemblerState.Launching => "发射倒计时",
            _ => "未知",
        };

        public override string CompInspectStringExtra()
        {
            var s = RocketSchematicBuilder.Build(Design,
                stageGapMeters: RocketDesignRules.StageGapMeters);
            string head = $"组装器：{StateText}";
            if (state == AssemblerState.Launching)
            {
                head += $"（{LaunchSecondsLeft:0.0} / {LaunchSecondsTotal:0.#} 游戏秒）";
            }
            else if (state == AssemblerState.Building)
            {
                head += $"（{Progress01:P0}，{workDone:F0}/{workRequired:F0} 工时";
                head += HasWorkerNearby() ? "，有工人）" : "，等待工人）";
            }
            return head + $"\n总高 {s.totalHeightM:F1} m · 最大直径 {s.maxWidthM:F1} m"
                + $" · 干重 {s.dryMassKg / 1000.0:F2} t · 推进剂 {s.propellantKg / 1000.0:F2} t"
                + $" · 容量 {s.capacityKg / 1000.0:F2} t · {s.stageCount} 级"
                + $" · 加注 {Design.fuelAmount / 1000.0:F2} t";
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
                yield return g;

            if (parent?.Faction != Faction.OfPlayer) yield break;

            yield return new Command_Action
            {
                defaultLabel = "火箭组装器",
                defaultDesc = "打开组装窗口：左侧预览火箭外形，右侧选件、建造。",
                icon = TexCommand.DesirePower,
                action = () => Find.WindowStack.Add(new Window_RocketAssemblerC(this)),
            };
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Deep.Look(ref design, "design");
            Scribe_Values.Look(ref state, "assemblerState", AssemblerState.Idle);
            Scribe_Values.Look(ref workDone, "assemblerWorkDone", 0f);
            Scribe_Values.Look(ref workRequired, "assemblerWorkRequired", 0f);
            Scribe_Values.Look(ref currentStage, "assemblerCurrentStage", 0);
            Scribe_Values.Look(ref currentParallel, "assemblerCurrentParallel", 1);
            currentParallel = ParallelLayout.Clamp(currentParallel);
            if (design == null) design = new RocketDesignSpec();
        }
    }
}
