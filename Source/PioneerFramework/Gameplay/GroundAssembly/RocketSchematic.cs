using System;
using System.Collections.Generic;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>示意图里一块零件的功能类别（只影响配色/画法，不影响装配）。</summary>
    public enum SchematicKind
    {
        Engine,
        Tank,
        Other
    }

    /// <summary>
    /// **侧视图里的一块 = 一个轴向站**（2026-10-28；2026-10-29 起还带并联信息）。
    /// 几何单位 = 米，**与 <see cref="DesignPartAssembler"/> 的堆叠口径一致**
    /// （长度取 `stackLengthMeters`，0 ⇒ `boundsY`；宽度取 `boundsX`）。
    /// 一块 = 一站（`quantity` 已展开，`count` = 该站并联几件），顺序 = spec 顺序 = 自上而下。
    /// </summary>
    public struct SchematicPiece
    {
        public string defName;
        public string label;
        public string category;

        /// <summary>这一站的横向跨度（米）：并联时 = `2R + w`，单件时 = 件宽。</summary>
        public float widthM;
        public float heightM;
        public SchematicKind kind;

        /// <summary>**单件**干重（并联站要 × <see cref="count"/> 才是这一站的总重）。</summary>
        public float dryMassKg;

        /// <summary>**单件**初装推进剂（kg）。</summary>
        public float propellantKg;

        /// <summary>级号（0 = 上面级；数字越大越早抛）。仅用于画级间隔线/读数。</summary>
        public int stageId;

        /// <summary>船体系 X 中心（米；显式位姿/径向安装用；堆叠件 = 0）。2026-11-03。</summary>
        public float centerXM;

        /// <summary>船体系 Y 中心（米；俯视图用；堆叠件 = 0）。2026-11-03。</summary>
        public float centerYM;

        /// <summary>船体系 Z 中心（米；侧视图用；堆叠件为负值，显式位姿 = localPosition.z）。2026-11-03。</summary>
        public float centerZM;

        /// <summary>船体系 Y 深度（米；俯视图用）。2026-11-03。</summary>
        public float depthM;

        /// <summary>该行是否显式位姿（径向安装）。2026-11-03。</summary>
        public bool explicitPose;

        /// <summary>对应 `RocketDesignSpec.parts` 的行下标（窗口点选/直接放置用）。2026-11-03。</summary>
        public int designRow;

        /// <summary>本轴向站的并联台数（1 = 单件；2…7 = 绕主推轴成环，见 <see cref="ParallelLayout"/>）。</summary>
        public int count;

        /// <summary>单件宽度（米）：画并联环时每块的宽度。</summary>
        public float partWidthM;

        /// <summary>形状（2026-10-29）：罐/引擎是圆柱 ⇒ 侧视图画圆头；Box 仍画矩形。</summary>
        public InertiaTensor.Shape shape;

        /// <summary>并联件的横向偏移（米，船体系 X/Y）；`count == 1` 时为 null（就在轴上）。</summary>
        public float[]? offsetX;
        public float[]? offsetY;
    }

    /// <summary>整支火箭的侧视图摘要（纯数据，不含任何 UI）。</summary>
    public sealed class RocketSchematic
    {
        public readonly List<SchematicPiece> pieces = new();

        /// <summary>总高（米）= Σ 单件高度（含级间空隙）；显式位姿行按绝对 z 取 min/max。</summary>
        public float totalHeightM;

        /// <summary>船体系 Z 的下/上边界（米；侧视图用）。2026-11-03。</summary>
        public float minZM, maxZM;

        /// <summary>最大直径（米）= max 单件宽度。</summary>
        public float maxWidthM;

        public float dryMassKg;
        public float propellantKg;

        /// <summary>工质总容量（kg）。发射按这个加注（`FuelForLaunch` 同口径）。</summary>
        public float capacityKg;

        /// <summary>涉及的级数（stageId 的不同取值个数）。</summary>
        public int stageCount;

        public int engineCount;
        public int tankCount;

        /// <summary>至少有一件的目录定义没查到（画成占位块）。</summary>
        public bool hasPlaceholder;

        /// <summary>
        /// 级间空隙（m）：每处级边界多让出的一段空当（2026-10-30 用户方案；与装配器同口径）。
        /// `totalHeightM` 已把它算进去；画的时候要在级边界把 `y` 前进 `stageGapM`。
        /// </summary>
        public float stageGapM;

        public float TotalMassKg => dryMassKg + propellantKg;
    }

    /// <summary>
    /// **设计规格 → 侧视图摘要**（纯函数，2026-10-28）。
    ///
    /// 为什么单独一个纯核：窗口要"显示火箭的样子"，但排版/缩放/取整这些一旦写进 `OnGUI` 就没法测；
    /// 所以几何与质量全部在这里算好，窗口只负责把 `SchematicPiece` 按比例画成矩形。
    /// 目录查不到（DMS:NF 自定义 def 未收录）时按 2 m × 2 m 占位，不抛、不缩水成 0 高。
    /// </summary>
    public static class RocketSchematicBuilder
    {
        public const float PlaceholderSideM = 2f;

        public static RocketSchematic Build(RocketDesignSpec? spec,
            Func<string, ShipPartDef?>? catalogLookup = null, float stageGapMeters = 0f)
        {
            var s = new RocketSchematic { stageGapM = Math.Max(0f, stageGapMeters) };
            if (spec?.parts == null) return s;

            catalogLookup ??= name => string.IsNullOrEmpty(name)
                ? null
                : DefDatabase<ShipPartDef>.GetNamedSilentFail(name);

            var seenStages = new HashSet<int>();
            int prevStageId = int.MinValue;
            float zCursor = 0f;
            bool anyPiece = false;
            float minZ = 0f, maxZ = 0f;

            for (int i = 0; i < spec.parts.Count; i++)
            {
                DesignPartSpec? entry = spec.parts[i];
                if (entry == null) continue;

                bool explicitPose = entry.useExplicitPose;
                int entryStageId = entry.stageId < 0 ? 0 : entry.stageId;
                // 级间空隙：与 `DesignPartAssembler` 同一规则（只作用于堆叠行；显式位姿行不受影响）
                if (!explicitPose && s.stageGapM > 0f && prevStageId != int.MinValue && entryStageId != prevStageId)
                    zCursor -= s.stageGapM;
                prevStageId = entryStageId;

                int stations = RocketDesignRules.StationCountOf(entry);   // 轴向站数
                int count = RocketDesignRules.ParallelOf(entry);          // 每站并联台数
                ShipPartDef? def = catalogLookup(entry.defName);

                float partW = def != null && def.boundsX > 0f ? def.boundsX : PlaceholderSideM;
                if (partW <= 0f) partW = PlaceholderSideM;
                float depth = def != null && def.boundsZ > 0f ? def.boundsZ : PlaceholderSideM;
                float height = def != null ? StackHeightOf(def) : PlaceholderSideM;
                if (height <= 0f) height = PlaceholderSideM;

                for (int q = 0; q < stations; q++)
                {
                    // 2026-11-03：显式位姿行 = 绝对坐标，不推进 zCursor（与 DesignPartAssembler 一致）。
                    float centerZ = explicitPose ? (float)entry.localPosition.z : zCursor - height * 0.5f;
                    if (!explicitPose) zCursor -= height;

                    var piece = new SchematicPiece
                    {
                        defName = entry.defName ?? "",
                        label = def != null && !string.IsNullOrEmpty(def.label) ? def.label : (entry.defName ?? ""),
                        category = def != null ? def.category : (entry.category ?? ""),
                        widthM = explicitPose ? partW : ParallelLayout.SpanM(partW, count),
                        heightM = height,
                        depthM = depth,
                        centerXM = explicitPose ? (float)entry.localPosition.x : 0f,
                        centerYM = explicitPose ? (float)entry.localPosition.y : 0f,
                        centerZM = centerZ,
                        explicitPose = explicitPose,
                        designRow = i,
                        dryMassKg = def != null ? def.massKg : 0f,
                        propellantKg = def != null ? def.tankInitialKg : 0f,
                        stageId = entryStageId,
                        count = count,
                        partWidthM = partW,
                        // 2026-10-29：形状透传（罐/引擎 = Cylinder ⇒ 侧视图画圆头）
                        shape = entry.useShapeOverride
                            ? entry.shapeOverride
                            : (def?.shape ?? InertiaTensor.Shape.Box),
                    };
                    piece.kind = KindOf(piece.category);

                    if (piece.widthM <= 0f) piece.widthM = PlaceholderSideM;

                    if (count > 1 && !explicitPose)
                    {
                        piece.offsetX = new float[count];
                        piece.offsetY = new float[count];
                        ParallelLayout.Offsets(count, partW, piece.offsetX, piece.offsetY);
                    }

                    s.pieces.Add(piece);

                    float top = piece.centerZM + piece.heightM * 0.5f;
                    float bottom = piece.centerZM - piece.heightM * 0.5f;
                    if (!anyPiece) { minZ = bottom; maxZ = top; anyPiece = true; }
                    else { if (bottom < minZ) minZ = bottom; if (top > maxZ) maxZ = top; }

                    // 横向跨度要算上显式位姿的偏移（径向 booster 不落在轴上）
                    float extent = Math.Abs(piece.centerXM) * 2f + piece.widthM;
                    if (extent > s.maxWidthM) s.maxWidthM = extent;

                    s.dryMassKg += piece.dryMassKg * count;
                    s.propellantKg += piece.propellantKg * count;
                    s.capacityKg += (def != null ? def.tankCapacityKg : 0f) * count;
                    seenStages.Add(piece.stageId);
                    if (piece.kind == SchematicKind.Engine) s.engineCount += count;
                    if (piece.kind == SchematicKind.Tank) s.tankCount += count;
                    if (def == null) s.hasPlaceholder = true;
                }
            }

            s.minZM = anyPiece ? minZ : 0f;
            s.maxZM = anyPiece ? maxZ : 0f;
            s.totalHeightM = Math.Max(0f, s.maxZM - s.minZM);
            s.stageCount = seenStages.Count;
            return s;
        }

        /// <summary>与装配器同口径：`stackLengthMeters`，0 ⇒ `boundsY`，再兜 2 m。</summary>
        public static float StackHeightOf(ShipPartDef def)
        {
            if (def == null) return PlaceholderSideM;
            if (def.stackLengthMeters > 0f) return def.stackLengthMeters;
            if (def.boundsY > 0f) return def.boundsY;
            return PlaceholderSideM;
        }

        public static SchematicKind KindOf(string? category)
        {
            if (category == "thruster" || category == "rcs") return SchematicKind.Engine;
            if (category == "tank" || category == "rcs-tank") return SchematicKind.Tank;
            return SchematicKind.Other;
        }

        /// <summary>
        /// 设计的总推力（N，目录口径 Σ ve·ṁ；只算 category=="thruster" 的件，目录查不到的不计）。
        /// 与 `PartPropulsionResolver` 同一个乘积口径 ⇒ 和发射时的 TWR 门同源。
        /// </summary>
        /// <param name="onlyStage">只算这一级的引擎（null = 全部）。飞行时只有**活动级**（最大 stageId）出力。</param>
        public static float ThrustN(RocketDesignSpec? spec,
            Func<string, ShipPartDef?>? catalogLookup = null, int? onlyStage = null)
        {
            if (spec?.parts == null || spec.parts.Count == 0) return 0f;
            catalogLookup ??= name => string.IsNullOrEmpty(name)
                ? null
                : DefDatabase<ShipPartDef>.GetNamedSilentFail(name);

            double thrust = 0.0;
            for (int i = 0; i < spec.parts.Count; i++)
            {
                DesignPartSpec? e = spec.parts[i];
                if (e == null || e.category != "thruster") continue;
                if (onlyStage.HasValue && e.stageId != onlyStage.Value) continue;
                ShipPartDef? def = catalogLookup(e.defName);
                if (def == null || def.massFlowRate <= 0f) continue;
                thrust += (double)def.exhaustVelocity * def.massFlowRate * RocketDesignRules.PartCountOf(e);
            }
            return (float)thrust;
        }

        /// <summary>
        /// **C-full 活动集合计推力**（2026-11-03）：只算 `onlyStages` 里各级的引擎。
        /// 与单级重载同口径（Σ ve·ṁ × 件数）；活动集来自 `RocketDesignRules.ActiveStages`。
        /// </summary>
        public static float ThrustN(RocketDesignSpec? spec,
            Func<string, ShipPartDef?>? catalogLookup, IReadOnlyCollection<int> onlyStages)
        {
            if (spec?.parts == null || spec.parts.Count == 0 || onlyStages == null || onlyStages.Count == 0)
                return 0f;
            catalogLookup ??= name => string.IsNullOrEmpty(name)
                ? null
                : DefDatabase<ShipPartDef>.GetNamedSilentFail(name);

            double thrust = 0.0;
            for (int i = 0; i < spec.parts.Count; i++)
            {
                DesignPartSpec? e = spec.parts[i];
                if (e == null || e.category != "thruster") continue;

                bool inSet = false;
                foreach (int s in onlyStages)
                    if (s == e.stageId) { inSet = true; break; }
                if (!inSet) continue;

                ShipPartDef? def = catalogLookup(e.defName);
                if (def == null || def.massFlowRate <= 0f) continue;
                thrust += (double)def.exhaustVelocity * def.massFlowRate * RocketDesignRules.PartCountOf(e);
            }
            return (float)thrust;
        }

        /// <summary>当前活动级 = 设计里最大的 stageId（0 = 单级/上面级）。抛级后这个值会变小。</summary>
        public static int ActiveStage(RocketDesignSpec? spec)
        {
            int max = 0;
            if (spec?.parts != null)
                for (int i = 0; i < spec.parts.Count; i++)
                {
                    DesignPartSpec? e = spec.parts[i];
                    if (e != null && e.stageId > max) max = e.stageId;
                }
            return max;
        }
    }
}
