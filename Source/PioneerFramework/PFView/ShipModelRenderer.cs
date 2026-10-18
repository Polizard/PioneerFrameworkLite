using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PioneerFramework
{
    /// <summary>
    /// 与场景无关的「船模型绘制」静态段（D-1，2026-10-11；缺口总表 `视觉与玩家交互-现状与开工-20261011.md` §7 的 D-1）。
    ///
    /// **它解决的问题**：画船的代码原先长在 PFView 窗口自己身上（`DrawRuntimeParts` / `TryDrawMapRelief` /
    /// `DrawHeadingBar`），并依赖窗口的私有状态与**没说出口的约定**（窗口的 `matBlock` / `ToonMat` /
    /// `SceneLayer` / `debugScaleParts`，以及"位置已由 `AbsToView` 换算到本视图系、单位 = 米×0.1"）。
    /// 而「世界视图叠加模型 / 特殊飞船建筑渲染 / 远 icon 近模型分档」都要在**别的视图 / 别的尺度 /
    /// 别的参考系**里画同一艘船 —— 不抽就会复制三份画船代码（三份真相），而且换系约定多半会被抄错。
    ///
    /// **本类的边界（契约）**：
    ///   · 只管「**画什么**」：按**给定矩阵**把零件盒体 / 地图浮雕 / 朝向棒画出去；
    ///   · **不碰任何窗口/场景状态**：视图系位置、姿态、缩放、材质、层、MaterialPropertyBlock 全部显式传入；
    ///   · **不含换系与尺度假设**：位置 = 调用方视图系里的位置；单位换算（米↔视图）走显式入参 `metersToView`。
    ///   ⇒ 别的视图要画同一艘船 = 自己算好这组入参（+ 自己的网格/材质），观感口径与本类共享。
    ///   浮雕的**网格集**（三档 Mesh）与缓存/生命周期仍归调用方（PFView 按地图缓存，见 `MainTabWindow_PFView.MapRelief.cs`）。
    ///
    /// **行为约定（PFView 侧调用 = 逐位不变，重构验收口径）**：
    ///   · 零件：位置 = 视图系位置 + 姿态 ×（局部偏移 × metersToView × scale）；尺寸 = 盒体尺寸 × metersToView × scale
    ///     （各轴下限 `MinPartViewSize`）；
    ///   · 浮雕：网格顶点以**格**为单位（1 格 = 1 米）⇒ 矩阵 = TRS(位置, 姿态, 单位向量 × metersToView × scale)；
    ///   · 朝向棒：方向与长度由调用方算好（那是**数据**，不是绘制）。
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

        /// <summary>B8：损毁件盒体颜色（暗红残骸）。损毁件仍画（B5/D2 乙），只换色以区别于可用件。</summary>
        public static readonly Color DamagedPartColor = new(0.42f, 0.16f, 0.12f);

        /// <summary>零件盒体颜色选择（纯函数，headless 可测）：损毁 ⇒ 残骸色，否则船体色。</summary>
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
        public static Matrix4x4 PartMatrix(Vector3 viewPos, Quaternion rotation, DQuaternion partRotation,
            DVector3 positionShip, DVector3 boundsSizeShip, float metersToView, float scale)
        {
            PartViewTransform(viewPos, rotation, positionShip, boundsSizeShip, metersToView, scale,
                out Vector3 p, out Vector3 s);
            // B3：零件自身姿态；identity 时保持旧矩阵逐位不变
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
            float metersToView, int layer, HashSet<int>? skipPartIds = null)
        {
            if (entity == null || mesh == null || material == null || block == null) return;
            if (toonOutline)
                block.SetFloat("_OutlineWidth", PartOutlineWidth);

            // 2026-10-16 合批（"渲染线优化审查"第二批）：同状态零件**同网格同材质同色** ⇒ 并成一次提交。
            // B8：损毁件单独一批（暗红残骸色），健康件那批的矩阵与顺序保持原样 ⇒ 全健康船外观逐位不变。
            block.SetColor("_Color", PartColor);
            WorldQuadBatch.Reset();
            foreach (var part in entity.Parts)
            {
                if (part == null || part.isDestroyed) continue;
                // B6：只跳过**已经用浮雕画过**的挂图 part（由调用方逐件给 id）；
                // 封闭舱/无浮雕件不在集合里 ⇒ 照常画盒体。
                if (skipPartIds != null && skipPartIds.Contains(part.partId)) continue;
                AddPartMesh(part, viewPos, rotation, mesh, metersToView, scale);
            }
            if (WorldQuadBatch.ItemCount > 0)
                WorldQuadBatch.Flush(material, block, layer);

            // B5 / D2 乙：损毁件仍在、仍可见（残骸占位），换残骸色以区分可用件。
            block.SetColor("_Color", DamagedPartColor);
            WorldQuadBatch.Reset();
            foreach (var part in entity.Parts)
            {
                if (part == null || !part.isDestroyed) continue;
                if (skipPartIds != null && skipPartIds.Contains(part.partId)) continue;
                AddPartMesh(part, viewPos, rotation, mesh, metersToView, scale);
            }
            if (WorldQuadBatch.ItemCount > 0)
                WorldQuadBatch.Flush(material, block, layer);
        }

        /// <summary>按零件形状/姿态把一件零件加入当前合批（形状网格缺席 ⇒ 跳过）。</summary>
        private static void AddPartMesh(SpacePart part, Vector3 viewPos, Quaternion rotation,
            Mesh mesh, float metersToView, float scale)
        {
            Mesh? sourceMesh = PartShapeMeshes.For(part.shape, mesh);
            if (sourceMesh == null) return;
            WorldQuadBatch.AddMesh(sourceMesh, PartMatrix(viewPos, rotation, part.rotationShip,
                part.positionShip, part.boundsSizeShip, metersToView, scale));
        }

        // ════════════════════════════════════════════════════
        //  朝向指示棒（黄棒）
        // ════════════════════════════════════════════════════

        /// <summary>朝向棒长细比与最小宽（视图单位）。</summary>
        public const float HeadingBarWidthRatio = 0.08f;
        public const float HeadingBarMinWidth = 0.05f;

        /// <summary>朝向棒颜色（黄）：它就是"主推轴指向哪边"的读数。</summary>
        public static readonly Color HeadingBarColor = new(1f, 0.75f, 0.15f, 1f);

        /// <summary>
        /// 朝向指示棒：从 `centerViewPos` 沿 `dir` 画一根细棒（中心 = 位置 + dir × 长/2）。
        /// **方向与长度由调用方算好**（主推轴出自 `SpaceGameComp.GetMainThrustAxisSystem` —— 那是数据）。
        /// </summary>
        public static void DrawHeadingBar(Vector3 centerViewPos, Vector3 dir, float barLen,
            Mesh mesh, Material material, MaterialPropertyBlock block, int layer)
        {
            if (mesh == null || material == null || block == null) return;
            if (barLen <= 0.01f) return;
            if (dir.sqrMagnitude < 0.5f) return;   // 方向无效/NaN 守卫（调用方给的是归一化向量）

            float width = Mathf.Max(barLen * HeadingBarWidthRatio, HeadingBarMinWidth);
            Vector3 mid = centerViewPos + dir * (barLen * 0.5f);
            Quaternion rot = Quaternion.LookRotation(dir, Mathf.Abs(dir.y) < 0.9f ? Vector3.up : Vector3.forward);
            Matrix4x4 mtx = Matrix4x4.TRS(mid, rot, new Vector3(width, width, barLen));
            block.SetColor("_Color", HeadingBarColor);
            Graphics.DrawMesh(mesh, mtx, material, layer, null, 0, block);
        }

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
            float metersToView, int layer)
        {
            if (meshes == null || material == null || block == null) return;

            Matrix4x4 mtx = ReliefMatrix(viewPos, rotation, metersToView, scale);
            if (toonOutline)
                block.SetFloat("_OutlineWidth", ReliefOutlineWidth);

            for (int c = 1; c < meshes.Length; c++)
            {
                Mesh? mesh = meshes[c];
                if (mesh == null) continue;
                block.SetColor("_Color", ReliefColors[c]);
                Graphics.DrawMesh(mesh, mtx, material, layer, null, 0, block);
            }
        }
    }
}
