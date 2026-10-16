using System.Collections;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    public class WorldDrawLayer_RingTerrain : WorldDrawLayer
    {
        public override bool VisibleWhenLayerNotSelected => false;
        public override IEnumerable Regenerate()
        {
            foreach (object item in base.Regenerate()) yield return item;
            if (planetLayer is not RingSurfaceLayer ring) yield break;
            Material material = RingWorldMaterials.Habitat;
            for (int tile = 0; tile < ring.Topology.TileCount; tile++)
            {
                LayerSubMesh mesh = GetSubMesh(material, out int meshIndex);
                int start = mesh.verts.Count;
                AddVertex(mesh,RingIndustrialGeometry.SurfaceUV(ring.Geometry,tile,ring.Geometry.Center(tile)));
                DVector3[] corners = ring.Geometry.Vertices(tile);
                for (int i=0;i<6;i++)
                {
                    AddVertex(mesh,RingIndustrialGeometry.SurfaceUV(ring.Geometry,tile,corners[i]));
                }
                for (int i=0;i<6;i++)
                {
                    mesh.tris.Add(start);
                    mesh.tris.Add(start + 1 + i);
                    mesh.tris.Add(start + 1 + (i+1)%6);
                    AppendRaycastableTriangle(meshIndex,tile);
                }
                if (tile % 256 == 0) yield return null;
            }
            FinalizeMesh(MeshParts.All);
            foreach (object item in RegenerateWorldMeshColliders()) yield return item;
        }
        internal static void AddVertex(LayerSubMesh mesh,RingVisualPoint point)
        {
            mesh.verts.Add((Vector3)point.Position);
            mesh.uvs.Add(new Vector2((float)point.U,(float)point.V));
        }
    }
}
