using System.Collections;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    /// <summary>
    /// 环外壳/桁架/管线/警示灯绘制层（2026-10-17 电影感重制：受光材质 + 真实法线）。
    ///
    /// 与旧版的差异：
    /// - 材质从「unlit + 4 档假明暗」改为 `RingWorldMaterials.Structure()` 的受光材质（PF_RingLit）；
    /// - 每个四边形写入 `cross(B-A, C-A)` 的可见侧法线，供 shader 做与行星同源的昼夜光照；
    /// - `Render()` 每帧刷新 `RingWorldVisualState`（太阳方向/相机位置/时间）。
    ///
    /// 只有 `WorldDrawLayer_RingTerrain` 的居住面生成碰撞与三角形→地块映射；本层不生成这些，
    /// 装饰面不参与地块拾取（保持旧纪律）。
    /// </summary>
    public sealed class WorldDrawLayer_RingShell : WorldDrawLayer
    {
        public override bool VisibleWhenLayerNotSelected => false;

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
            int count = 0;
            foreach (RingVisualQuad quad in RingIndustrialGeometry.Build(ring.Geometry))
            {
                LayerSubMesh mesh = GetSubMesh(RingWorldMaterials.Structure(quad));
                int start = mesh.verts.Count;
                DVector3 normal = RingIndustrialGeometry.FaceNormal(quad);
                AddVertex(mesh, quad.A, normal);
                AddVertex(mesh, quad.B, normal);
                AddVertex(mesh, quad.C, normal);
                AddVertex(mesh, quad.D, normal);
                Megastructures.SurfaceMeshParts.AddQuadTriangles(mesh,start);
                if (++count % Megastructures.SurfaceMeshParts.BatchSize == 0) yield return null;
            }
            FinalizeMesh(MeshParts.All);
            // Only the original habitable hexagons own colliders and triangle-to-tile mappings.
        }

        private static void AddVertex(LayerSubMesh mesh, RingVisualPoint point, DVector3 normal)
        {
            mesh.verts.Add((Vector3)point.Position);
            mesh.uvs.Add(new Vector3((float)point.U, (float)point.V, 0f));
            mesh.normals.Add((Vector3)normal);
        }
    }
}
