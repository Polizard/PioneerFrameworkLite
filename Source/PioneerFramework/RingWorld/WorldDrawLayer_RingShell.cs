using System.Collections;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.RingWorld
{
    public sealed class WorldDrawLayer_RingShell : WorldDrawLayer
    {
        public override bool VisibleWhenLayerNotSelected => false;

        public override IEnumerable Regenerate()
        {
            foreach (object item in base.Regenerate()) yield return item;
            if (planetLayer is not RingSurfaceLayer ring) yield break;
            int count = 0;
            foreach (RingVisualQuad quad in RingIndustrialGeometry.Build(ring.Geometry))
            {
                LayerSubMesh mesh = GetSubMesh(RingWorldMaterials.Structure(quad));
                int start = mesh.verts.Count;
                WorldDrawLayer_RingTerrain.AddVertex(mesh,quad.A);
                WorldDrawLayer_RingTerrain.AddVertex(mesh,quad.B);
                WorldDrawLayer_RingTerrain.AddVertex(mesh,quad.C);
                WorldDrawLayer_RingTerrain.AddVertex(mesh,quad.D);
                mesh.tris.Add(start); mesh.tris.Add(start+1); mesh.tris.Add(start+2);
                mesh.tris.Add(start); mesh.tris.Add(start+2); mesh.tris.Add(start+3);
                if (++count % 256 == 0) yield return null;
            }
            FinalizeMesh(MeshParts.All);
            // Only the original habitable hexagons own colliders and triangle-to-tile mappings.
        }
    }
}
