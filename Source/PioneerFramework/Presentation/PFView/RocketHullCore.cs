using System;
using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// **火箭外壳几何（纯核，2026-10-30 用户规格）**：24 边形（lowpoly）棱柱 + 棱台拼出的分级外壳。
    ///
    /// 用户规格（原话要点）：
    /// · 每个分级渲染自己的外壳；**壳代替零件盒**（被壳包住的件不再单独画）。
    /// · 级间：每级在与**更小编号**（= 上方，0 = 上面级）对接的一侧，尺寸对齐更小编号那一级 ⇒ 顶部一段棱台过渡。
    /// · 0.3 m 级间空隙**归编号较大一侧**（落在下面那级的壳内 ⇒ 外形连续，缝在对接面）。
    /// · **引擎算进筒径**（活动级也算）；非活动级/碎片把引擎**包进壳**（底封口）；活动级底部**开口**露喷管。
    /// · 截面用**圆**（半径 = max(x,y)/2），不用椭圆。
    /// · 整流罩只可能是 **0 级最上面的载荷件**；罩 = 棱柱 + 棱台，**顶面很小**（不做锥尖，避免视觉尖刺）。
    /// · 损毁按**每站一段**上色（该站有损毁件 ⇒ 这段用残骸色）。
    ///
    /// 本类只做算术（顶点/环/分段给数据），不碰 UnityEngine：网格与材质由 `PFView` 侧包。
    /// 全部长度单位 = 米，坐标 = 船体轴系（机头 +Z、零件沿 −Z 堆）。
    /// </summary>
    public static class RocketHullCore
    {
        /// <summary>lowpoly 边数（24 棱柱/棱台）。</summary>
        public const int Sides = 24;

        /// <summary>整流罩高度 / 直径。</summary>
        public const double FairingHeightPerDiameter = 1.2;

        /// <summary>整流罩顶面半径 / 直径（"顶面很小"，不做锥尖）。</summary>
        public const double FairingTopPerDiameter = 0.15;

        /// <summary>整流罩下半段（棱柱）占整流罩高度的比例，其余是棱台。</summary>
        public const double FairingPrismFraction = 0.35;

        /// <summary>级间过渡段的最小/最大长度（米）与相对级长的上限。</summary>
        public const double TransitionMinMeters = 0.3;
        public const double TransitionMaxMeters = 2.0;
        public const double TransitionMaxStageFraction = 0.25;

        private const double Eps = 1e-6;

        /// <summary>一件零件的外壳输入（由游戏侧从 `SpacePart` 映射）。</summary>
        public struct Part
        {
            public int stageId;
            public double centerZ;
            /// <summary>沿堆叠方向的长度（体 Z）。</summary>
            public double lengthZ;
            public double halfX;
            public double halfY;
            public bool destroyed;
            public bool isEngine;
            /// <summary>载荷舱件（只有"0 级最上面的载荷件"会被当成整流罩）。</summary>
            public bool isPayload;
        }

        /// <summary>一段壳（棱柱或棱台）：同一条 24 边形带，`rTop == rBottom` 即棱柱。</summary>
        public struct Band
        {
            public int stageId;
            /// <summary>上沿 z（机头方向更大）。</summary>
            public double zTop;
            /// <summary>下沿 z。</summary>
            public double zBottom;
            public double rTop;
            public double rBottom;
            /// <summary>该段是否有损毁件（用残骸色画）。</summary>
            public bool destroyed;
            /// <summary>是否属于整流罩（单独材质/不参与级配色）。</summary>
            public bool isFairing;
        }

        public sealed class Result
        {
            public readonly List<Band> bands = new();

            /// <summary>整船外壳的 z 范围（含整流罩；调试/包围盒用）。</summary>
            public double minZ = double.MaxValue;
            public double maxZ = double.MinValue;

            /// <summary>是否有整流罩（0 级最上面是载荷件）。</summary>
            public bool hasFairing;
            public double fairingBottomZ;
            public double fairingTopZ;
            public double fairingBaseRadius;
            public double fairingTopRadius;

            /// <summary>需要封底的级（非活动级 / 碎片）：封底半径 = 该级筒半径。</summary>
            public readonly List<(int stageId, double z, double radius)> caps = new();

            public bool HasGeometry => bands.Count > 0 || caps.Count > 0;
        }

        /// <summary>
        /// 生成外壳分段。`activeStage` = 组当前活动级（存活件里 stageId 最大者）；`jettisoned` = 该组是抛出去的碎片
        /// （碎片即使"自己是活动级"也要把引擎包起来）。
        /// </summary>
        public static Result Build(IReadOnlyList<Part> parts, int activeStage, bool jettisoned)
        {
            var result = new Result();
            if (parts == null || parts.Count == 0) return result;

            // ── 1. 级号（升序 = 从机头到箭尾）──────────────────
            var stageIds = new List<int>();
            for (int i = 0; i < parts.Count; i++)
            {
                int id = parts[i].stageId;
                if (!stageIds.Contains(id)) stageIds.Add(id);
            }
            stageIds.Sort();
            if (stageIds.Count == 0) return result;

            int topStage = stageIds[0];

            // ── 2. 整流罩判定：0 级最上面的件是载荷件 ─────────────
            int payloadIndex = -1;
            double payloadTop = double.MinValue;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.stageId != topStage || !p.isPayload) continue;
                double top = p.centerZ + p.lengthZ * 0.5;
                if (top > payloadTop) { payloadTop = top; payloadIndex = i; }
            }
            // 必须是"该级最上面的件"：没有别的件的顶面高于它
            if (payloadIndex >= 0)
            {
                for (int i = 0; i < parts.Count && payloadIndex >= 0; i++)
                {
                    if (i == payloadIndex) continue;
                    double top = parts[i].centerZ + parts[i].lengthZ * 0.5;
                    if (top > payloadTop + Eps) { payloadIndex = -1; break; }
                }
            }

            // ── 3. 逐级建壳 ──────────────────────────────────
            double prevStageBottomZ = double.NaN;
            double prevStageRadius = double.NaN;

            for (int si = 0; si < stageIds.Count; si++)
            {
                int sid = stageIds[si];
                bool exposed = sid == activeStage && !jettisoned;   // 只有"活动级的引擎"露出来

                // 该级零件
                double stageTop = double.MinValue, stageBottom = double.MaxValue;
                double radius = 0.0;
                double engineTop = double.MinValue;
                int included = 0;

                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    if (p.stageId != sid) continue;
                    double top = p.centerZ + p.lengthZ * 0.5;
                    double bottom = p.centerZ - p.lengthZ * 0.5;
                    if (top > stageTop) stageTop = top;
                    if (bottom < stageBottom) stageBottom = bottom;
                    // 2026-10-30 用户修正："**考虑引擎**" —— 引擎永远算进筒径（活动级也算，否则筒身会
                    // 在底部突然比喷管环窄）。露出的引擎只是不进"壳的轴向范围"（hullBottom = 引擎顶面）。
                    if (p.isEngine && top > engineTop) engineTop = top;
                    if (i == payloadIndex) continue;     // 整流罩件本身画成罩，不进筒径
                    included++;
                    double r = Math.Max(p.halfX, p.halfY);
                    if (r > radius) radius = r;
                }
                if (included == 0 && radius <= 0.0)
                {
                    // 全是引擎的级（极端）：退化为全件最大半径，免得生成空壳
                    for (int i = 0; i < parts.Count; i++)
                    {
                        var p = parts[i];
                        if (p.stageId != sid) continue;
                        double r = Math.Max(p.halfX, p.halfY);
                        if (r > radius) radius = r;
                    }
                }
                if (radius <= 0.0) continue;

                // 壳的上下沿：顶 = 上一级壳底（含 0.3 m 级间空隙）/ 整流罩下缘 / 自身顶
                double hullTop = si == 0
                    ? (payloadIndex >= 0
                        ? parts[payloadIndex].centerZ - parts[payloadIndex].lengthZ * 0.5   // 罩下缘
                        : stageTop)
                    : prevStageBottomZ;
                double hullBottom = exposed && engineTop > double.MinValue ? engineTop : stageBottom;
                if (hullBottom > hullTop - Eps) hullBottom = hullTop - Math.Max(0.05, radius * 0.02);

                double topRadius = si == 0 ? radius : prevStageRadius;

                // 站表（按 z 中心分组：并联件共享同一中心）——只含"被包住"的件
                var stations = BuildStations(parts, sid, payloadIndex);

                double z = hullTop;

                // 顶部棱台（对齐上一级）
                if (Math.Abs(topRadius - radius) > 1e-4)
                {
                    double stageLen = Math.Max(Eps, hullTop - hullBottom);
                    double trans = Math.Min(TransitionMaxMeters,
                        Math.Min(Math.Abs(topRadius - radius) * 0.5 + TransitionMinMeters,
                                 stageLen * TransitionMaxStageFraction));
                    if (trans > Eps && z - trans > hullBottom + Eps)
                    {
                        AddBand(result, sid, z, z - trans, topRadius, radius, destroyed: false);
                        z -= trans;
                    }
                }

                // 逐站棱柱（损毁按站上色）
                for (int k = 0; k < stations.Count; k++)
                {
                    var st = stations[k];
                    double bandTop = Math.Min(z, st.top);
                    double bandBottom = Math.Max(hullBottom, st.bottom);
                    if (bandTop - bandBottom <= Eps) continue;
                    AddBand(result, sid, bandTop, bandBottom, radius, radius, st.destroyed);
                    z = bandBottom;
                    if (z <= hullBottom + Eps) break;
                }

                // 站表没覆盖到的残余（例如全是引擎/间距）⇒ 补一段健康棱柱
                if (z > hullBottom + Eps && z < hullTop + Eps && z - hullBottom > 1e-3)
                    AddBand(result, sid, z, hullBottom, radius, radius, destroyed: false);

                // 非活动级/碎片：底封口
                if (!exposed && result.caps.Count < 64)
                    result.caps.Add((sid, hullBottom, radius));

                prevStageBottomZ = hullBottom;
                prevStageRadius = radius;
            }

            // ── 4. 整流罩：棱柱（下 35%）+ 棱台（上 65%），顶面很小 ──
            if (payloadIndex >= 0)
            {
                var payload = parts[payloadIndex];
                double baseZ = payload.centerZ - payload.lengthZ * 0.5;
                double baseR = 0.0;
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    if (p.stageId != payload.stageId || i == payloadIndex) continue;
                    double r = Math.Max(p.halfX, p.halfY);
                    if (r > baseR) baseR = r;
                }
                if (baseR <= 0.0) baseR = Math.Max(payload.halfX, payload.halfY);
                double diameter = baseR * 2.0;
                double height = diameter * FairingHeightPerDiameter;
                double topR = diameter * FairingTopPerDiameter;
                if (height > Eps)
                {
                    double prismTop = baseZ + height * FairingPrismFraction;
                    double tipZ = baseZ + height;
                    AddBand(result, payload.stageId, prismTop, baseZ, baseR, baseR, destroyed: payload.destroyed, fairing: true);
                    AddBand(result, payload.stageId, tipZ, prismTop, topR, baseR, destroyed: payload.destroyed, fairing: true);

                    result.hasFairing = true;
                    result.fairingBottomZ = baseZ;
                    result.fairingTopZ = tipZ;
                    result.fairingBaseRadius = baseR;
                    result.fairingTopRadius = topR;
                    if (tipZ > result.maxZ) result.maxZ = tipZ;
                    if (baseZ < result.minZ) result.minZ = baseZ;
                }
            }

            return result;
        }

        private static void AddBand(Result r, int stageId, double zTop, double zBottom,
            double rTop, double rBottom, bool destroyed, bool fairing = false)
        {
            if (zTop - zBottom <= Eps) return;
            if (rTop <= Eps && rBottom <= Eps) return;
            r.bands.Add(new Band
            {
                stageId = stageId,
                zTop = zTop,
                zBottom = zBottom,
                rTop = rTop,
                rBottom = rBottom,
                destroyed = destroyed,
                isFairing = fairing,
            });
            if (zTop > r.maxZ) r.maxZ = zTop;
            if (zBottom < r.minZ) r.minZ = zBottom;
        }

        private struct Station
        {
            public double top, bottom;
            public bool destroyed;
        }

        /// <summary>把一个级的件按 z 中心聚成"站"（并联件同中心），按从上到下排序。</summary>
        private static List<Station> BuildStations(IReadOnlyList<Part> parts, int stageId, int payloadIndex)
        {
            var stations = new List<Station>();
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.stageId != stageId || i == payloadIndex) continue;
                // 露出的引擎不含在壳里，但这里不过滤：过滤由调用方按 included 半径保证；
                // 引擎站若落在 hullBottom 以下会被 `Math.Max(hullBottom, st.bottom)` 裁掉。
                double top = p.centerZ + p.lengthZ * 0.5;
                double bottom = p.centerZ - p.lengthZ * 0.5;
                int found = -1;
                for (int k = 0; k < stations.Count; k++)
                {
                    if (Math.Abs((stations[k].top + stations[k].bottom) * 0.5 - p.centerZ) <= 1e-4)
                    {
                        found = k;
                        break;
                    }
                }
                if (found < 0)
                {
                    stations.Add(new Station { top = top, bottom = bottom, destroyed = p.destroyed });
                }
                else
                {
                    var s = stations[found];
                    s.top = Math.Max(s.top, top);
                    s.bottom = Math.Min(s.bottom, bottom);
                    s.destroyed |= p.destroyed;
                    stations[found] = s;
                }
            }
            stations.Sort((a, b) => b.top.CompareTo(a.top));
            return stations;
        }
    }
}
