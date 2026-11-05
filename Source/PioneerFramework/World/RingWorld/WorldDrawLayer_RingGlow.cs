using System;
using System.Collections;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    /// <summary>
    /// 环的「电影感辉光层」（2026-10-17 电影感视觉升级，task-6 / IMPL §5.1）。
    ///
    /// 为什么单独一层：受光主体（`PF_RingLit`）必须不透明写深度；而其上的边缘光/大气薄带/
    /// 警示灯光斑全部是**加性、不写深度**的（`PF_RingGlow`，renderQueue 3550）。混在主体层里
    /// 会破坏主体的深度排序，所以拆成独立 WorldDrawLayer（可选 visibility 由 Lead 的 def 控制）。
    ///
    /// 三块内容（3 个材质 = 3 类 submesh；引擎对 >4 万顶点的 submesh 会自动再拆，行为与原版图层一致）：
    /// 1. **rim**：直接复用 `RingIndustrialGeometry.Build(ring.Geometry)` 的四边形位置 + 生产面法线，
    ///    `PF_RingGlow` mode0（菲涅尔 `1-|N·V|` 幂次）。顶点色按材质压暗（Hull 最亮、Pipe 弱、警示灯为 0，
    ///    因为警示灯由第 3 块的橙色 sprite 表达）。
    /// 2. **ribbon 大气薄带**：环带上下各一条窄带（半径 +2.2，轴向略超出生产 halfWidth），
    ///    `mode1`（UV.y 做软边）+ 冷蓝色，让细带在远景视觉上加厚。
    /// 3. **警示灯光晕**：遍历生产四边形取 `WarningLight`，每盏灯一片沿表面法线外移 0.10 的
    ///    camera-independent 方形 quad，`mode2`（径向衰减 + 顶点色 a 的确定性相位做呼吸）。
    ///
    /// 纪律：
    /// - `VisibleWhenLayerNotSelected => false`；没有 collider / tile 映射 / 拾取三角形（只画装饰）。
    /// - 旋转由现有 `RingWorldMotion` 对 ring 层 `WorldDrawLayerBase.Rotation` 的 Harmony 补丁统一处理，
    ///   本层绝不自己再旋转（几何是环局部坐标，交给 Graphics.DrawMesh 的 rotation 参数）。
    /// - `Render()` 开头调用 `RingWorldVisualState.UpdatePerFrame()`（幂等，同帧只刷一次）。
    /// - `PF_RingGlow` shader 缺失时整层跳过并只告警一次（见 RingGlowMaterials）。
    /// - 预算：rim = 壳体全部四边形（256×3 为 10,624 quad / 21,248 tri）；ribbon ≤ 2×512 quad；
    ///   灯 = 每个警示灯四边形 1 片（256×3 为 256 片）。不新增每帧重建——只在 Regenerate 时建一次。
    /// </summary>
    public sealed class WorldDrawLayer_RingGlow : WorldDrawLayer
    {
        private const int RimYieldEvery = 2048;
        private const int HaloYieldEvery = 128;

        /// <summary>大气薄带的环半径偏移（IMPL 建议 +1.5~3 单位，取中值偏外以离开外侧桁架）。</summary>
        private const double RibbonRadiusOffset = 2.2;

        /// <summary>薄带靠近生产带边的轴向间隙（正值 = 在 halfWidth 之外）。</summary>
        private const double RibbonInnerInset = 0.12;

        /// <summary>薄带外缘超出生产带边的轴向距离（±略超出，制造“雾边加厚”）。</summary>
        private const double RibbonOuterInset = 1.65;

        /// <summary>警示灯光斑沿表面法线的偏移，避免与灯面 z-fighting（IMPL 建议 0.05–0.15）。</summary>
        private const double HaloNormalOffset = 0.10;

        public override bool VisibleWhenLayerNotSelected => false;

        /// <summary>
        /// 每帧入口：先刷新共享 uniform（太阳方向 / 相机位置 / 时间），再走引擎默认的
        /// “按需 Regenerate + 逐 submesh DrawMesh”流程。
        /// </summary>
        public override void Render()
        {
            if(!Megastructures.ArtificialSurfaceLayers.ModuleAvailable(planetLayer,Megastructures.SurfaceModules.Presentation))return;
            RingWorldVisualState.UpdatePerFrame();
            base.Render();
        }

        public override IEnumerable Regenerate()
        {
            foreach (object item in base.Regenerate()) yield return item;
            if (planetLayer is not RingSurfaceLayer ring) yield break;
            if (!RingGlowMaterials.TryEnsureMaterials()) yield break;

            foreach (object item in BuildRim(ring, RingGlowMaterials.Rim!)) yield return item;
            foreach (object item in BuildAtmosphereRibbons(ring, RingGlowMaterials.Ribbon!)) yield return item;
            foreach (object item in BuildWarningHalos(ring, RingGlowMaterials.Sprite!)) yield return item;

            // MeshParts.All：本层是少数会写 normals/colors 的 ring 层（受光主体也要；
            // 现有 RingShell 的 FinalizeMesh(All) 只有 verts/uvs/tris，最终由 Lead 的层补齐法线）。
            FinalizeMesh(MeshParts.All);
        }

        // ─────────────────────────────────────────────────────────────
        // 1) rim：复用壳体全部四边形
        // ─────────────────────────────────────────────────────────────
        private IEnumerable BuildRim(RingSurfaceLayer ring, Material material)
        {
            int count = 0;
            foreach (RingVisualQuad quad in RingIndustrialGeometry.Build(ring.Geometry))
            {
                LayerSubMesh mesh = GetSubMesh(material);
                int start = mesh.verts.Count;
                AddQuad(mesh, quad, FaceNormal(quad), RimTint(quad.Material));
                AddQuadTriangles(mesh, start);
                if (++count % RimYieldEvery == 0) yield return null;
            }
        }

        /// <summary>rim 顶点色：按生产材质权重压暗（mode0 里会乘 vColor.rgb）；警示灯为 0 交给 sprite。</summary>
        private static Color32 RimTint(RingVisualMaterial material) => material switch
        {
            RingVisualMaterial.Hull => new Color32(255, 255, 255, 255),
            RingVisualMaterial.Frame => new Color32(200, 200, 200, 255),
            RingVisualMaterial.Pipe => new Color32(120, 120, 120, 255),
            // 警示灯四边形很小且已被橙色 sprite 覆盖，蓝色 rim 只会污染色相 ⇒ 权重 0。
            _ => new Color32(0, 0, 0, 255)
        };

        // ─────────────────────────────────────────────────────────────
        // 2) ribbon：环带上下两条大气薄带
        // ─────────────────────────────────────────────────────────────
        private IEnumerable BuildAtmosphereRibbons(RingSurfaceLayer ring, Material material)
        {
            RingGeometry geometry = ring.Geometry;
            int segments = Math.Clamp(geometry.Topology.Columns, 64, 512);
            double radius = geometry.Radius + RibbonRadiusOffset;
            double halfWidth = geometry.RowSpacing * (geometry.Topology.Rows - 1) / 2 + geometry.Spacing / Math.Sqrt(3);
            Color32 tint = new Color32(150, 200, 255, 255);

            for (int side = -1; side <= 1; side += 2)
            {
                double yInner = side * (halfWidth + RibbonInnerInset);
                double yOuter = side * (halfWidth + RibbonOuterInset);
                LayerSubMesh mesh = GetSubMesh(material);
                for (int i = 0; i < segments; i++)
                {
                    double a0 = Math.Tau * i / segments;
                    double a1 = Math.Tau * (i + 1) / segments;
                    double cos0 = Math.Cos(a0), sin0 = Math.Sin(a0);
                    double cos1 = Math.Cos(a1), sin1 = Math.Sin(a1);
                    var inner0 = new DVector3(cos0 * radius, yInner, sin0 * radius);
                    var outer0 = new DVector3(cos0 * radius, yOuter, sin0 * radius);
                    var outer1 = new DVector3(cos1 * radius, yOuter, sin1 * radius);
                    var inner1 = new DVector3(cos1 * radius, yInner, sin1 * radius);
                    var normal = new DVector3(cos0 + cos1, 0, sin0 + sin1).normalized;
                    int start = mesh.verts.Count;
                    AddVertex(mesh, inner0, 0f, 0f, normal, tint);
                    AddVertex(mesh, outer0, 0f, 1f, normal, tint);
                    AddVertex(mesh, outer1, 1f, 1f, normal, tint);
                    AddVertex(mesh, inner1, 1f, 0f, normal, tint);
                    AddQuadTriangles(mesh, start);
                    if ((i & 63) == 0) yield return null;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 3) sprite：警示灯加性光斑
        // ─────────────────────────────────────────────────────────────
        private IEnumerable BuildWarningHalos(RingSurfaceLayer ring, Material material)
        {
            LayerSubMesh mesh = GetSubMesh(material);
            int index = 0;
            foreach (RingVisualQuad quad in RingIndustrialGeometry.Build(ring.Geometry))
            {
                if (quad.Material != RingVisualMaterial.WarningLight) continue;

                DVector3 normal = FaceNormal(quad);
                DVector3 center = (quad.A.Position + quad.B.Position + quad.C.Position + quad.D.Position) * 0.25;
                double halfSize = Math.Clamp(
                    Math.Max((quad.B.Position - quad.A.Position).magnitude,
                        (quad.C.Position - quad.B.Position).magnitude) * 0.62, 1.4, 3.8);
                DVector3 tangent = (quad.B.Position - quad.A.Position).normalized; // 灯面的轴向边
                DVector3 bitangent = DVector3.Cross(normal, tangent).normalized;    // 切向边
                DVector3 anchor = center + normal * HaloNormalOffset;
                float phase = HaloPhase(center);
                byte alpha = (byte)Mathf.Clamp(Mathf.RoundToInt(phase * 255f), 0, 255);
                // 橙色系数写在顶点色里（IMPL §5.1.3），_GlowColor 用白色避免二次染色。
                var tint = new Color32(255, 140, 46, alpha);

                int start = mesh.verts.Count;
                AddVertex(mesh, anchor - tangent * halfSize - bitangent * halfSize, 0f, 0f, normal, tint);
                AddVertex(mesh, anchor + tangent * halfSize - bitangent * halfSize, 1f, 0f, normal, tint);
                AddVertex(mesh, anchor + tangent * halfSize + bitangent * halfSize, 1f, 1f, normal, tint);
                AddVertex(mesh, anchor - tangent * halfSize + bitangent * halfSize, 0f, 1f, normal, tint);
                AddQuadTriangles(mesh, start);
                if (++index % HaloYieldEvery == 0) yield return null;
            }
        }

        /// <summary>
        /// 每盏灯的确定性呼吸相位（0..1）。同一盏灯的“外/内两面”四边形中心只在半径上略有差异，
        /// 所以用「角度 + Y」量化成 key 而不是用序号，保证成对两面同相位、不会互相闪烁。
        /// </summary>
        private static float HaloPhase(DVector3 center)
        {
            double angle = Math.Atan2(center.z, center.x);
            long key = (long)Math.Round(angle * 1000.0) * 397L ^ (long)Math.Round(center.y * 100.0);
            return Hash01((uint)(key ^ (key >> 32)));
        }

        /// <summary>确定性 32-bit 整数哈希 → [0,1)，避免依赖 UnityEngine.Random 的全局状态。</summary>
        private static float Hash01(uint value)
        {
            value ^= value >> 16;
            value *= 0x7feb352du;
            value ^= value >> 15;
            value *= 0x846ca68bu;
            value ^= value >> 16;
            return (value & 0xFFFFu) / 65535f;
        }

        // ─────────────────────────────────────────────────────────────
        // 几何小工具
        // ─────────────────────────────────────────────────────────────
        private static DVector3 FaceNormal(RingVisualQuad quad) =>
            DVector3.Cross(quad.B.Position - quad.A.Position, quad.C.Position - quad.A.Position).normalized;

        private static void AddQuad(LayerSubMesh mesh, RingVisualQuad quad, DVector3 normal, Color32 color)
        {
            AddVertex(mesh, quad.A, normal, color);
            AddVertex(mesh, quad.B, normal, color);
            AddVertex(mesh, quad.C, normal, color);
            AddVertex(mesh, quad.D, normal, color);
        }

        private static void AddVertex(LayerSubMesh mesh, RingVisualPoint point, DVector3 normal, Color32 color)
        {
            mesh.verts.Add((Vector3)point.Position);
            mesh.uvs.Add(new Vector3((float)point.U, (float)point.V, 0f));
            mesh.normals.Add((Vector3)normal);
            mesh.colors.Add(color);
        }

        private static void AddVertex(LayerSubMesh mesh, DVector3 position, float u, float v, DVector3 normal,
            Color32 color)
        {
            mesh.verts.Add((Vector3)position);
            mesh.uvs.Add(new Vector3(u, v, 0f));
            mesh.normals.Add((Vector3)normal);
            mesh.colors.Add(color);
        }

        /// <summary>winding 与服务端生产几何一致（A-B-C / A-C-D）；glow shader 是 cull=None，不依赖绕序。</summary>
        private static void AddQuadTriangles(LayerSubMesh mesh, int start)
        {
            mesh.tris.Add(start);
            mesh.tris.Add(start + 1);
            mesh.tris.Add(start + 2);
            mesh.tris.Add(start);
            mesh.tris.Add(start + 2);
            mesh.tris.Add(start + 3);
        }
    }
}
