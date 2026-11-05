using System.Collections;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    /// <summary>
    /// 环居住面绘制层（2026-10-17 电影感重制：受光材质 + 真实法线）。
    ///
    /// - 居住面是圆柱内表面（朝内），逐顶点法线 = 指向环心的径向；
    /// - UV 的 V 改为覆盖整个带宽（新贴图按整条带设计，旧口径只采样贴图中段 36%）；
    /// - U 仍按"每 tile 恒用一段贴图"的旧口径，保持既有 UV 连续性与接缝测试不变；
    /// - 只有本层生成碰撞与三角形→地块映射（拾取契约不变）。
    /// </summary>
    public class WorldDrawLayer_RingTerrain : WorldDrawLayer
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
            Material material = RingWorldMaterials.Habitat;
            for (int tile = 0; tile < ring.Topology.TileCount; tile++)
            {
                LayerSubMesh mesh = GetSubMesh(material, out int meshIndex);
                int start = mesh.verts.Count;
                AddVertex(mesh, RingIndustrialGeometry.SurfaceUV(ring.Geometry, tile, ring.Geometry.Center(tile)));
                mesh.normals.Add((Vector3)RingIndustrialGeometry.HabitatNormal(ring.Geometry.Center(tile)));
                DVector3[] corners = ring.Geometry.Vertices(tile);
                for (int i = 0; i < 6; i++)
                {
                    AddVertex(mesh, RingIndustrialGeometry.SurfaceUV(ring.Geometry, tile, corners[i]));
                    mesh.normals.Add((Vector3)RingIndustrialGeometry.HabitatNormal(corners[i]));
                }
                Megastructures.SurfaceMeshParts.AddCellFan(mesh,start,corners.Length,Megastructures.SurfaceMeshRole.Habitat,()=>AppendRaycastableTriangle(meshIndex,tile));
                if (tile % Megastructures.SurfaceMeshParts.BatchSize == 0) yield return null;
            }
            FinalizeMesh(MeshParts.All);
            foreach (object item in RegenerateWorldMeshColliders()) yield return item;
        }

        internal static void AddVertex(LayerSubMesh mesh, RingVisualPoint point)
        {
            Megastructures.SurfaceMeshParts.AddVertex(mesh,point.Position,new Vector3((float)point.U,(float)point.V,0));
        }
    }
}
