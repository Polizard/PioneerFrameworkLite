using System.Collections;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    public abstract class WorldDrawLayer_RingHighlight : WorldDrawLayer
    {
        private PlanetTile lastTile = PlanetTile.Invalid;
        protected abstract PlanetTile Target { get; }
        protected abstract Material HighlightMaterial { get; }
        public override bool VisibleWhenLayerNotSelected => false;
        public override bool ShouldRegenerate => base.ShouldRegenerate || lastTile != Target;
        public override IEnumerable Regenerate()
        {
            foreach (object item in base.Regenerate()) yield return item;
            PlanetTile tile = Target; lastTile = tile;
            if (planetLayer is not RingSurfaceLayer ring || !tile.Valid || tile.Layer != ring) yield break;
            LayerSubMesh mesh = GetSubMesh(HighlightMaterial);
            Vector3 offset = (Vector3)ring.Geometry.Normal(tile.tileId) * .04f;
            mesh.verts.Add((Vector3)ring.Geometry.Center(tile.tileId) + offset); mesh.uvs.Add(new Vector2(.5f,.5f));
            foreach(DVector3 corner in ring.Geometry.Vertices(tile.tileId)) { mesh.verts.Add((Vector3)corner+offset); mesh.uvs.Add(Vector2.zero); }
            for(int i=0;i<6;i++) { mesh.tris.Add(0); mesh.tris.Add(1+i); mesh.tris.Add(1+(i+1)%6); }
            FinalizeMesh(MeshParts.All);
        }
    }
    public sealed class WorldDrawLayer_RingSelected : WorldDrawLayer_RingHighlight
    {
        protected override PlanetTile Target => Find.WorldSelector.SelectedTile;
        protected override Material HighlightMaterial => WorldMaterials.SelectedTile;
    }
    public sealed class WorldDrawLayer_RingMouse : WorldDrawLayer_RingHighlight
    {
        protected override PlanetTile Target => GenWorld.MouseTile();
        protected override Material HighlightMaterial => WorldMaterials.MouseTile;
    }
}
