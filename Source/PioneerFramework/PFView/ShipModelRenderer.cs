using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PioneerFramework
{
    /// <summary>
    /// 与场景无关的「船模型绘制」静态段（D-1，2026-10-11；缺口总表 `视觉与玩家交互-现状与开工-20261011.md` §7 的 D-1）。
    ///
    /// **它解决的问题**：画船的代码原先长在 PFView 窗口自己身上（`DrawRuntimeParts` / `TryDrawMapRelief`），
    /// 并依赖窗口的私有状态与**没说出口的约定**（窗口的 `matBlock` / `ToonMat` /
    /// `SceneLayer` / `debugScaleParts`，以及"位置已由 `AbsToView` 换算到本视图系、单位 = 米×0.1"）。
    /// 而「世界视图叠加模型 / 特殊飞船建筑渲染 / 远 icon 近模型分档」都要在**别的视图 / 别的尺度 /
    /// 别的参考系**里画同一艘船 —— 不抽就会复制三份画船代码（三份真相），而且换系约定多半会被抄错。
    ///
    /// **本类的边界（契约）**：
    ///   · 只管「**画什么**」：按**给定矩阵**把零件盒体 / 地图浮雕画出去；
    ///   · **不碰任何窗口/场景状态**：视图系位置、姿态、缩放、材质、层、MaterialPropertyBlock 全部显式传入；
    ///   · **不含换系与尺度假设**：位置 = 调用方视图系里的位置；单位换算（米↔视图）走显式入参 `metersToView`。
    ///   ⇒ 别的视图要画同一艘船 = 自己算好这组入参（+ 自己的网格/材质），观感口径与本类共享。
    ///   浮雕的**网格集**（三档 Mesh）与缓存/生命周期仍归调用方（PFView 按地图缓存，见 `MainTabWindow_PFView.MapRelief.cs`）。
    ///
    /// **行为约定（PFView 侧调用 = 逐位不变，重构验收口径）**：
    ///   · 零件：位置 = 视图系位置 + 姿态 ×（局部偏移 × metersToView × scale）；尺寸 = 盒体尺寸 × metersToView × scale
    ///     （各轴下限 `MinPartViewSize`）；
    ///   · 浮雕：网格顶点以**格**为单位（1 格 = 1 米）⇒ 矩阵 = TRS(位置, 姿态, 单位向量 × metersToView × scale)。
    ///   （2026-10-30 用户令"直接删"：原先的"朝向棒 `DrawHeadingBar`"已整段移除 —— 主推轴读数在姿态球 T 记号与左栏文字里，
    ///   那根棒只是一根穿过船体的重复标记。）
    ///
    /// ⚠ 乘法**逐字保留**重构前的结合顺序（`v * metersToView * scale`，不预乘成单个系数）——
    ///   浮点乘法不满足结合律，换个顺序会在末位比特上分家（"逐位不变"的验收就靠这个）。
    /// </summary>
    public static class ShipModelRenderer
    {
        // ════════════════════════════════════════════════════
        //  零件盒体
        // ════════════════════════════════════════════════════

        /// <summary>零件盒体的最小视图尺寸（各轴）：再小的零件也要看得见一个点。</summary>
        public const float MinPartViewSize = 0.1f;

        /// <summary>部件描边宽（对象空间，Toon 材质用。V-1.3：部件也画描边）。</summary>
        public const float PartOutlineWidth = 0.02f;

        /// <summary>零件盒体颜色（船体素色；明暗由场景方向光给，本类不表态光从哪来）。</summary>
        public static readonly Color PartColor = new(0.3f, 0.6f, 1.0f);

        /// <summary>B8：损毁件盒体颜色（暗红残骸）。损毁件仍画，只换色以区别于可用件。</summary>
        public static readonly Color DamagedPartColor = new(0.42f, 0.16f, 0.12f);

        /// <summary>零件盒体颜色选择（纯函数，headless 可测）。</summary>
        public static Color PartColorFor(bool isDestroyed) => isDestroyed ? DamagedPartColor : PartColor;

        /// <summary>
        /// 单个零件的**视图变换**（纯算术：headless 可测 —— 「零件矩阵组装」判据的落点）：
        /// 位置 = 视图系位置 + 姿态 ×（局部偏移 × metersToView × scale）；
        /// 尺寸 = 盒体尺寸 × metersToView × scale（各轴下限 <see cref="MinPartViewSize"/>）。
        /// </summary>
        public static void PartViewTransform(Vector3 viewPos, Quaternion rotation,
            DVector3 positionShip, DVector3 boundsSizeShip, float metersToView, float scale,
            out Vector3 outPos, out Vector3 outSize)
        {
            Vector3 pv = (Vector3)positionShip * metersToView * scale;
            Vector3 sv = (Vector3)boundsSizeShip * metersToView * scale;
            if (sv.x < MinPartViewSize) sv.x = MinPartViewSize;
            if (sv.y < MinPartViewSize) sv.y = MinPartViewSize;
            if (sv.z < MinPartViewSize) sv.z = MinPartViewSize;

            outPos = viewPos + rotation * pv;
            outSize = sv;
        }

        /// <summary>零件盒体矩阵 = TRS(位置, 姿态, 尺寸)（组装口径见 <see cref="PartViewTransform"/>）。
        /// ⚠ Unity 的 `Matrix4x4.TRS` 是 **ECall**（headless 一调即抛，2026-10-11 实测）⇒ 纯测试钉的是
        /// <see cref="PartViewTransform"/> 那两个读数，矩阵构造本身只在游戏里跑。</summary>
        public static Matrix4x4 PartMatrix(Vector3 viewPos, Quaternion rotation,
            DVector3 positionShip, DVector3 boundsSizeShip, float metersToView, float scale)
            => PartMatrix(viewPos, rotation, DQuaternion.identity, positionShip, boundsSizeShip,
                metersToView, scale);

        /// <summary>B3：带零件自身姿态的矩阵；identity 时保持旧矩阵逐位不变。</summary>
        public static Matrix4x4 PartMatrix(Vector3 viewPos, Quaternion rotation, DQuaternion partRotation,
            DVector3 positionShip, DVector3 boundsSizeShip, float metersToView, float scale)
        {
            PartViewTransform(viewPos, rotation, positionShip, boundsSizeShip, metersToView, scale,
                out Vector3 p, out Vector3 s);
            Quaternion renderRot = partRotation == DQuaternion.identity
                ? rotation
                : rotation * partRotation.ToQuaternion();
            return Matrix4x4.TRS(p, renderRot, s);
        }

        /// <summary>
        /// 画一个实体的全部零件盒体（单 part 化之后的统一几何）。材质/网格/层/MaterialPropertyBlock
        /// 全由调用方给；`toonOutline` = 当前材质是 Toon（要按 <see cref="PartOutlineWidth"/> 设描边宽）。
        /// </summary>
        public static void DrawParts(SpaceEntity entity, Vector3 viewPos, Quaternion rotation, float scale,
            Mesh mesh, Material material, MaterialPropertyBlock block, bool toonOutline,
            float metersToView, int layer, Color? tint = null, HashSet<int>? skipPartIds = null)
        {
            if (entity == null || mesh == null || material == null || block == null) return;
            if (toonOutline)
                block.SetFloat("_OutlineWidth", PartOutlineWidth);

            // 健康件：同网格同材质同色 ⇒ 一次提交；蒙皮 tint 非空时用阵营色。
            block.SetColor("_Color", tint ?? PartColor);
            WorldQuadBatch.Reset();
            foreach (var part in entity.Parts)
            {
                if (part == null || part.isDestroyed) continue;
                if (skipPartIds != null && skipPartIds.Contains(part.partId)) continue;
                Mesh? source = PartShapeMeshes.For(part.shape, mesh);
                if (source == null) continue;
                WorldQuadBatch.AddMesh(source, PartMatrix(viewPos, rotation, part.rotationShip,
                    part.positionShip, part.boundsSizeShip, metersToView, scale));
            }
            if (WorldQuadBatch.ItemCount > 0)
                WorldQuadBatch.Flush(material, block, layer);

            // B5/B8：损毁件仍可见（残骸占位），单独一批暗红残骸色。
            block.SetColor("_Color", DamagedPartColor);
            WorldQuadBatch.Reset();
            foreach (var part in entity.Parts)
            {
                if (part == null || !part.isDestroyed) continue;
                if (skipPartIds != null && skipPartIds.Contains(part.partId)) continue;
                Mesh? source = PartShapeMeshes.For(part.shape, mesh);
                if (source == null) continue;
                WorldQuadBatch.AddMesh(source, PartMatrix(viewPos, rotation, part.rotationShip,
                    part.positionShip, part.boundsSizeShip, metersToView, scale));
            }
            if (WorldQuadBatch.ItemCount > 0)
                WorldQuadBatch.Flush(material, block, layer);
        }

        // ════════════════════════════════════════════════════
        //  火箭外壳（2026-10-30）：24 边形棱柱/棱台，三批颜色
        // ════════════════════════════════════════════════════

        /// <summary>外壳描边宽度（对象空间；比零件盒略细，避免 lowpoly 棱线糊成一片）。</summary>
        public const float HullOutlineWidth = 0.012f;

        /// <summary>整流罩颜色 = 健康色向白提亮（与壳体区分）。</summary>
        public static Color FairingColorFor(Color hull) => Color.Lerp(hull, Color.white, 0.35f);

        /// <summary>
        /// 画一个实体的外壳（三批：健康 / 残骸 / 整流罩）。网格在**米制船体系**，
        /// 这里统一用 `position=viewPos、rotation=组姿态、scale=metersToView×scale` 的矩阵。
        /// </summary>
        internal static void DrawHull(RocketHullGeometry? hull, Vector3 viewPos, Quaternion rotation, float scale,
            Material material, MaterialPropertyBlock block, float metersToView, int layer, bool toonOutline,
            Color hullColor)
        {
            if (hull == null || material == null || block == null) return;
            if (toonOutline)
                block.SetFloat("_OutlineWidth", HullOutlineWidth);
            Matrix4x4 m = Matrix4x4.TRS(viewPos, rotation, Vector3.one * (metersToView * scale));

            DrawHullBatch(hull.healthy, hullColor, m, material, block, layer);
            DrawHullBatch(hull.damaged, DamagedPartColor, m, material, block, layer);
            DrawHullBatch(hull.fairing, FairingColorFor(hullColor), m, material, block, layer);
        }

        private static void DrawHullBatch(Mesh? mesh, Color color, in Matrix4x4 matrix,
            Material material, MaterialPropertyBlock block, int layer)
        {
            if (mesh == null) return;
            block.SetColor("_Color", color);
            WorldQuadBatch.Reset();
            WorldQuadBatch.AddMesh(mesh, matrix);
            if (WorldQuadBatch.ItemCount > 0)
                WorldQuadBatch.Flush(material, block, layer);
        }

        // ════════════════════════════════════════════════════
        //  朝向指示棒（黄棒）
        // ════════════════════════════════════════════════════

        // ════════════════════════════════════════════════════
        //  地图浮雕
        // ════════════════════════════════════════════════════

        /// <summary>浮雕的描边宽度（对象空间 = **格**；1 格 = 0.1 视图单位 ⇒ 这里 0.05 ≈ 5% 格宽）。
        /// 天体/部件用的 0.02–0.035 是"单位网格"口径，直接拿来会把 1 格的小方块糊掉。</summary>
        public const float ReliefOutlineWidth = 0.05f;

        /// <summary>浮雕三档颜色（索引 = 分类：0 空位 / 1 结构梁 / 2 舱室 / 3 装甲）。</summary>
        public static readonly Color[] ReliefColors =
        [new(0f, 0f, 0f, 0f),
            new(0.55f, 0.58f, 0.62f),   // 结构梁：中性灰
            new(0.78f, 0.82f, 0.86f),   // 舱室：亮钢
            new(0.34f, 0.38f, 0.44f),   // 装甲：深板岩
        ];

        /// <summary>浮雕矩阵：网格顶点以**格**为单位（1 格 = 1 米）⇒ 缩放 = metersToView × scale（"放大部件"同比）。</summary>
        public static Matrix4x4 ReliefMatrix(Vector3 viewPos, Quaternion rotation, float metersToView, float scale)
        {
            return Matrix4x4.TRS(viewPos, rotation, Vector3.one * (metersToView * scale));
        }

        /// <summary>
        /// 画地图浮雕（三档网格各一次 DrawMesh）。`meshes` 由调用方给（缓存/销毁归调用方，见 MapRelief.cs 的
        /// `ReliefMeshSet`）；颜色/描边宽走本类的三档口径 —— "同一艘船在哪儿看都长一样"。
        /// </summary>
        public static void DrawRelief(Mesh?[] meshes, Vector3 viewPos, Quaternion rotation, float scale,
            Material material, MaterialPropertyBlock block, bool toonOutline,
            float metersToView, int layer, Color? tint = null)
        {
            if (meshes == null || material == null || block == null) return;

            Matrix4x4 mtx = ReliefMatrix(viewPos, rotation, metersToView, scale);
            if (toonOutline)
                block.SetFloat("_OutlineWidth", ReliefOutlineWidth);

            for (int c = 1; c < meshes.Length; c++)
            {
                Mesh? mesh = meshes[c];
                if (mesh == null) continue;
                // 2026-10-19 蒙皮：tint 非空 = 三档明度向阵营色靠 28%（仍有结构层次）；空 = 旧三色逐字不变。
                block.SetColor("_Color", tint.HasValue ? Color.Lerp(ReliefColors[c], tint.Value, 0.28f) : ReliefColors[c]);
                Graphics.DrawMesh(mesh, mtx, material, layer, null, 0, block);
            }
        }
    }
}
